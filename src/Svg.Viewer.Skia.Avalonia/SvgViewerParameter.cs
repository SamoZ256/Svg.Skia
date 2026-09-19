// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
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
        double minimum,
        double maximum,
        double step)
        : base(declaration)
    {
        _seed = seed;
        _value = seed;
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
    }

    public double Minimum { get; }

    public double Maximum { get; }

    /// <summary>The declared increment, or zero when the range is continuous.</summary>
    public double Step { get; }

    public bool HasStep => Step > 0d;

    /// <summary>
    /// What a slider should tick by. The declared step when there is one, and a hundredth of the
    /// range otherwise, which is fine enough to feel continuous at any width.
    /// </summary>
    public double TickFrequency => HasStep ? Step : (Maximum - Minimum) / 100d;

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
        int step)
        : base(declaration)
    {
        _seed = seed;
        _value = seed;
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
    }

    public int Minimum { get; }

    public int Maximum { get; }

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
