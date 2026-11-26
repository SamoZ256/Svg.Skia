// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia.Media;
using Svg.Expressions;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// One parameter a document declares, as something a control can be bound to.
/// </summary>
/// <remarks>
/// Separate from the immutable declaration, so reloading a document whose parameters are unchanged
/// keeps the values somebody has already set.
/// </remarks>
public abstract class SvgViewerParameter : SvgViewerVariable
{
    protected SvgViewerParameter(SvgExpressionParameter declaration)
    {
        Declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));
    }

    /// <summary>Raised when the bound value changes, whatever its type.</summary>
    public event EventHandler? ValueChanged;

    public SvgExpressionParameter Declaration { get; }

    public string Name => Declaration.Name;

    public ExprType Type => Declaration.Type;

    /// <summary>The value to bind for this parameter.</summary>
    /// <remarks>
    /// A method rather than a property named <c>Value</c>, so each subclass keeps a <c>Value</c> of
    /// its own natural type for a control to bind to.
    /// </remarks>
    public abstract ExprValue ToExprValue();

    /// <summary>Takes a value back, where it is one this row can hold.</summary>
    /// <remarks>
    /// The inverse of <see cref="ToExprValue"/>, and refused rather than coerced: a number offered
    /// to an integer row is a caller's mistake, and rounding it would put a value into the drawing
    /// that the evaluator refuses and nobody chose.
    /// </remarks>
    public abstract bool TrySet(ExprValue value);

    /// <summary>The value as a document would write it, for committing it as the declared default.</summary>
    /// <remarks>
    /// The expression language, since the same parser reads it back. It is a literal, so committing
    /// over a <c>tau / 4</c> loses that it was written that way — a host's to warn about.
    /// </remarks>
    public abstract string ToExpression();

    /// <summary>Whether the value differs from the one the document declares.</summary>
    public abstract bool IsModified { get; }

    public abstract void ResetToDefault();

    /// <inheritdoc />
    /// <remarks>
    /// Any value change can change both, and a host listens for one signal rather than knowing
    /// which subclass it holds.
    /// </remarks>
    protected override void Changed()
    {
        Raise(nameof(IsModified));
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A <c>number</c> parameter, with the range its author declared.</summary>
public sealed class SvgViewerNumberParameter : SvgViewerParameter
{
    private readonly double _seed;
    private double _value;

    internal SvgViewerNumberParameter(
        SvgExpressionParameter declaration,
        double seed,
        decimal minimum,
        decimal maximum,
        double step,
        bool hasSlider)
        : base(declaration)
    {
        _seed = seed;
        _value = seed;
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        HasSlider = hasSlider;
    }

    /// <summary>
    /// What the row will take, which is anything at all on a side nobody declared an end for.
    /// </summary>
    /// <remarks>
    /// Decimal rather than double, being what the field holds its bounds as: "no end" has to be
    /// exactly what that control means by it, and double's own ends do not survive the conversion.
    /// A float's do not either, so an end outside decimal is no end.
    /// </remarks>
    public decimal Minimum { get; }

    /// <inheritdoc cref="Minimum"/>
    public decimal Maximum { get; }

    /// <summary>The declared increment, or zero when the range is continuous.</summary>
    public double Step { get; }

    public bool HasStep => Step > 0d;

    /// <summary>
    /// Whether there is a slider: two ends, both of them somebody's answer rather than this code's.
    /// </summary>
    /// <remarks>
    /// A row without one is the field alone. Inventing the end an author left out put a default of
    /// 217 in the middle of a 0..500 range nobody chose, and a drag along it wrote numbers the
    /// drawing was never said to accept.
    /// </remarks>
    public bool HasSlider { get; }

    /// <summary>
    /// What to tick by. The declared step where there is one, a hundredth of the range under a
    /// slider, which is fine enough to feel continuous at any width, and one where there is neither.
    /// </summary>
    public double TickFrequency => HasStep
        ? Step
        : HasSlider ? (double)(Maximum - Minimum) / 100d : 1d;

    public double Value
    {
        get => _value;
        set => Set(ref _value, value);
    }

    public override ExprValue ToExprValue() => ExprValue.Number((float)_value);

    public override bool TrySet(ExprValue value)
    {
        if (value.Type != ExprType.Number)
        {
            return false;
        }

        // The same widening the seed took: compared plainly, the float's binary tail would leave
        // the row modified for ever over a difference nobody made.
        Value = SvgViewerParameterFactory.Widen(value.AsNumber);

        return true;
    }

    public override string ToExpression() => SvgViewerParameterFactory.Describe(ToExprValue());

    public override bool IsModified => !_value.Equals(_seed);

    public override void ResetToDefault() => Value = _seed;
}

/// <summary>An <c>integer</c> parameter, with the range its author declared.</summary>
/// <remarks>
/// Its own row rather than a number one that rounds: a control bound to a double would let a drag
/// land between two values and put a number back where an integer is declared, which the evaluator
/// refuses. The bounds are held as ints for the same reason.
/// </remarks>
public sealed class SvgViewerIntegerParameter : SvgViewerParameter
{
    private readonly int _seed;
    private int _value;

    internal SvgViewerIntegerParameter(
        SvgExpressionParameter declaration,
        int seed,
        int minimum,
        int maximum,
        int step,
        bool hasSlider)
        : base(declaration)
    {
        _seed = seed;
        _value = seed;
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        HasSlider = hasSlider;
    }

    /// <summary>
    /// What the row will take, which on a side nobody declared an end for is as far as an integer
    /// goes — the value is one, so the type's own ends are the honest answer to no end at all.
    /// </summary>
    public int Minimum { get; }

    /// <inheritdoc cref="Minimum"/>
    public int Maximum { get; }

    /// <inheritdoc cref="SvgViewerNumberParameter.HasSlider"/>
    public bool HasSlider { get; }

    /// <summary>The declared increment, which is one where the document declared none.</summary>
    /// <remarks>
    /// One rather than zero, unlike a number's: a continuous integer is a contradiction, so there is
    /// no case for the number row's hundredth-of-the-range fallback to serve.
    /// </remarks>
    public int Step { get; }

    public int TickFrequency => Step;

    public int Value
    {
        get => _value;
        set => Set(ref _value, value);
    }

    public override ExprValue ToExprValue() => ExprValue.Integer(_value);

    public override bool TrySet(ExprValue value)
    {
        if (value.Type != ExprType.Integer)
        {
            return false;
        }

        Value = value.AsInteger;

        return true;
    }

    public override string ToExpression() => SvgViewerParameterFactory.Describe(ToExprValue());

    public override bool IsModified => _value != _seed;

    public override void ResetToDefault() => Value = _seed;
}

/// <summary>A <c>color</c> parameter.</summary>
public sealed class SvgViewerColorParameter : SvgViewerParameter
{
    private readonly Color _seed;
    private Color _color;

    internal SvgViewerColorParameter(SvgExpressionParameter declaration, Color seed)
        : base(declaration)
    {
        _seed = seed;
        _color = seed;
    }

    public Color Color
    {
        get => _color;
        set => Set(ref _color, value);
    }

    public override ExprValue ToExprValue() => ExprValue.Color(_color.R, _color.G, _color.B, _color.A);

    public override bool TrySet(ExprValue value)
    {
        if (value.Type != ExprType.Color)
        {
            return false;
        }

        Color = global::Avalonia.Media.Color.FromArgb(value.Alpha, value.Red, value.Green, value.Blue);

        return true;
    }

    public override string ToExpression() => SvgViewerParameterFactory.Describe(ToExprValue());

    public override bool IsModified => _color != _seed;

    public override void ResetToDefault() => Color = _seed;
}

/// <summary>A <c>string</c> parameter.</summary>
public sealed class SvgViewerStringParameter : SvgViewerParameter
{
    private readonly string _seed;
    private string _value;

    internal SvgViewerStringParameter(SvgExpressionParameter declaration, string seed)
        : base(declaration)
    {
        _seed = seed;
        _value = seed;
    }

    /// <remarks>
    /// Never null, whatever a two-way binding to an emptied text box puts back: the value goes to
    /// the evaluator, which has no null.
    /// </remarks>
    public string Value
    {
        get => _value;
        set => Set(ref _value, value ?? string.Empty);
    }

    public override ExprValue ToExprValue() => ExprValue.String(_value);

    public override bool TrySet(ExprValue value)
    {
        if (value.Type != ExprType.String)
        {
            return false;
        }

        Value = value.AsString;

        return true;
    }

    public override string ToExpression() => SvgViewerParameterFactory.Describe(ToExprValue());

    public override bool IsModified => !string.Equals(_value, _seed, StringComparison.Ordinal);

    public override void ResetToDefault() => Value = _seed;
}

/// <summary>A <c>boolean</c> parameter.</summary>
public sealed class SvgViewerBooleanParameter : SvgViewerParameter
{
    private readonly bool _seed;
    private bool _value;

    internal SvgViewerBooleanParameter(SvgExpressionParameter declaration, bool seed)
        : base(declaration)
    {
        _seed = seed;
        _value = seed;
    }

    public bool Value
    {
        get => _value;
        set => Set(ref _value, value);
    }

    public override ExprValue ToExprValue() => ExprValue.Boolean(_value);

    public override bool TrySet(ExprValue value)
    {
        if (value.Type != ExprType.Boolean)
        {
            return false;
        }

        Value = value.AsBoolean;

        return true;
    }

    public override string ToExpression() => SvgViewerParameterFactory.Describe(ToExprValue());

    public override bool IsModified => _value != _seed;

    public override void ResetToDefault() => Value = _seed;
}

/// <summary>A <c>number[]</c> or <c>color[]</c> parameter: an editor per element, and a way to add one.</summary>
/// <remarks>
/// Not generic, so the panel has one row for either kind, whose buttons act on an element without
/// knowing which kind of list it is in; an element's own type picks its editor.
/// <see cref="SvgViewerListParameter{T}"/> holds the elements.
/// </remarks>
public abstract class SvgViewerListParameter : SvgViewerParameter
{
    private protected SvgViewerListParameter(SvgExpressionParameter declaration)
        : base(declaration)
    {
    }

    public abstract int Count { get; }

    public bool IsEmpty => Count == 0;

    /// <summary>Appends a copy of the last element, or a zero or an opaque black to a list with none.</summary>
    public abstract void Add();

    internal abstract void Move(SvgViewerListItem item, int by);

    internal abstract void Remove(SvgViewerListItem item);
}

/// <summary>The elements of a list parameter, each one an item a control binds to.</summary>
/// <remarks>
/// Every change, an edit to one element as much as an element added, removed or moved, is one
/// <see cref="SvgViewerParameter.ValueChanged"/>: the drawing takes the list whole.
/// </remarks>
public abstract class SvgViewerListParameter<T> : SvgViewerListParameter
{
    private readonly T[] _seed;
    private readonly ObservableCollection<SvgViewerListItem<T>> _items = new();

    private protected SvgViewerListParameter(SvgExpressionParameter declaration, IEnumerable<T> seed)
        : base(declaration)
    {
        _seed = seed.ToArray();
        Items = new ReadOnlyObservableCollection<SvgViewerListItem<T>>(_items);

        foreach (var value in _seed)
        {
            _items.Add(Item(value));
        }

        Renumber();
    }

    public ReadOnlyObservableCollection<SvgViewerListItem<T>> Items { get; }

    public override int Count => _items.Count;

    public override bool IsModified => !_items.Select(item => item.Value).SequenceEqual(_seed);

    public override void ResetToDefault() => Replace(_seed);

    public override string ToExpression() => SvgViewerParameterFactory.Describe(ToExprValue());

    public override void Add()
    {
        _items.Add(Item(_items.Count > 0 ? _items[_items.Count - 1].Value : Blank));
        Reordered();
    }

    /// <summary>What <see cref="Add"/> appends to a list with nothing to copy.</summary>
    private protected abstract T Blank { get; }

    /// <summary>An element of this kind of list, whose type is what picks its editor.</summary>
    private protected abstract SvgViewerListItem<T> Item(T value);

    internal override void Move(SvgViewerListItem item, int by)
    {
        var from = _items.IndexOf((SvgViewerListItem<T>)item);
        var to = from + by;

        if (from < 0 || to < 0 || to >= _items.Count)
        {
            return;
        }

        _items.Move(from, to);
        Reordered();
    }

    internal override void Remove(SvgViewerListItem item)
    {
        if (_items.Remove((SvgViewerListItem<T>)item))
        {
            Reordered();
        }
    }

    internal void Edited() => Changed();

    /// <summary>Puts a whole list in, as one change, and none where it is the list already held.</summary>
    private protected void Replace(IReadOnlyList<T> values)
    {
        if (_items.Select(item => item.Value).SequenceEqual(values))
        {
            return;
        }

        _items.Clear();

        foreach (var value in values)
        {
            _items.Add(Item(value));
        }

        Reordered();
    }

    private void Reordered()
    {
        Renumber();
        Raise(nameof(Count));
        Raise(nameof(IsEmpty));
        Changed();
    }

    private void Renumber()
    {
        for (var index = 0; index < _items.Count; index++)
        {
            _items[index].Renumber(index);
        }
    }
}

/// <summary>A <c>number[]</c> parameter, such as a gradient's stop offsets.</summary>
public sealed class SvgViewerNumberListParameter : SvgViewerListParameter<double>
{
    internal SvgViewerNumberListParameter(SvgExpressionParameter declaration, IEnumerable<float> seed)
        : base(declaration, seed.Select(SvgViewerParameterFactory.Widen))
    {
    }

    private protected override double Blank => 0d;

    private protected override SvgViewerListItem<double> Item(double value) => new SvgViewerNumberListItem(this, value);

    public override ExprValue ToExprValue() => ExprValue.NumberList(Items.Select(item => (float)item.Value));

    public override bool TrySet(ExprValue value)
    {
        if (value.Type != ExprType.NumberList)
        {
            return false;
        }

        Replace(value.AsNumberList.Select(SvgViewerParameterFactory.Widen).ToList());

        return true;
    }
}

/// <summary>A <c>color[]</c> parameter, such as a gradient's stop colours.</summary>
public sealed class SvgViewerColorListParameter : SvgViewerListParameter<Color>
{
    internal SvgViewerColorListParameter(SvgExpressionParameter declaration, IEnumerable<Color> seed)
        : base(declaration, seed)
    {
    }

    private protected override Color Blank => Colors.Black;

    private protected override SvgViewerListItem<Color> Item(Color value) => new SvgViewerColorListItem(this, value);

    public override ExprValue ToExprValue()
        => ExprValue.ColorList(Items.Select(item => ExprValue.Color(item.Value.R, item.Value.G, item.Value.B, item.Value.A)));

    public override bool TrySet(ExprValue value)
    {
        if (value.Type != ExprType.ColorList)
        {
            return false;
        }

        Replace(value.AsColorList.Select(colour => SvgViewerParameterFactory.ToColor(colour)).ToList());

        return true;
    }
}

/// <summary>One element of a list parameter, where it stands and what it can be moved to.</summary>
public abstract class SvgViewerListItem : INotifyPropertyChanged
{
    private protected SvgViewerListItem(SvgViewerListParameter owner)
    {
        Owner = owner;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SvgViewerListParameter Owner { get; }

    /// <summary>Where it stands, counted as an expression indexes it.</summary>
    public int Index { get; private set; }

    /// <summary>The element as an expression names it, <c>colors[1]</c>, which is what its controls are called.</summary>
    public string Label => $"{Owner.Name}[{Index}]";

    public bool CanMoveUp => Index > 0;

    public bool CanMoveDown => Index < Owner.Count - 1;

    public void MoveUp() => Owner.Move(this, -1);

    public void MoveDown() => Owner.Move(this, 1);

    public void Remove() => Owner.Remove(this);

    /// <remarks>Raised whatever the index was, since what follows it in the list may have changed.</remarks>
    internal void Renumber(int index)
    {
        Index = index;

        Raise(nameof(Index));
        Raise(nameof(Label));
        Raise(nameof(CanMoveUp));
        Raise(nameof(CanMoveDown));
    }

    private protected void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>An element's value, for the control editing it.</summary>
public abstract class SvgViewerListItem<T> : SvgViewerListItem
{
    private readonly SvgViewerListParameter<T> _list;
    private T _value;

    private protected SvgViewerListItem(SvgViewerListParameter<T> list, T value)
        : base(list)
    {
        _list = list;
        _value = value;
    }

    public T Value
    {
        get => _value;
        set
        {
            if (EqualityComparer<T>.Default.Equals(_value, value))
            {
                return;
            }

            _value = value;
            Raise(nameof(Value));
            _list.Edited();
        }
    }
}

/// <summary>An element of a <c>number[]</c>, edited in a number field.</summary>
public sealed class SvgViewerNumberListItem : SvgViewerListItem<double>
{
    internal SvgViewerNumberListItem(SvgViewerNumberListParameter list, double value)
        : base(list, value)
    {
    }
}

/// <summary>An element of a <c>color[]</c>, edited in a colour picker.</summary>
public sealed class SvgViewerColorListItem : SvgViewerListItem<Color>
{
    internal SvgViewerColorListItem(SvgViewerColorListParameter list, Color value)
        : base(list, value)
    {
    }
}
