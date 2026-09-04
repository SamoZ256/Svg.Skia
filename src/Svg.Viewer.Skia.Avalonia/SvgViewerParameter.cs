// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
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
public abstract class SvgViewerParameter : INotifyPropertyChanged
{
    protected SvgViewerParameter(SvgExpressionParameter declaration)
    {
        Declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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

    /// <summary>The value as a document would write it, for committing it as the declared default.</summary>
    /// <remarks>
    /// The expression language, since the same parser reads it back. It is a literal, so committing
    /// over a <c>tau / 4</c> loses that it was written that way — a host's to warn about.
    /// </remarks>
    public abstract string ToExpression();

    /// <summary>Whether the value differs from the one the document declares.</summary>
    public abstract bool IsModified { get; }

    public abstract void ResetToDefault();

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        Raise(property);

        // Any value change can change both, and a host listens for one signal rather than knowing
        // which subclass it holds.
        Raise(nameof(IsModified));
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    protected void Raise(string? property)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
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

    public override string ToExpression() => SvgViewerParameterFactory.Describe(ToExprValue());

    public override bool IsModified => !_value.Equals(_seed);

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

    public override string ToExpression() => SvgViewerParameterFactory.Describe(ToExprValue());

    public override bool IsModified => _value != _seed;

    public override void ResetToDefault() => Value = _seed;
}
