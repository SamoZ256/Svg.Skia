// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
using Svg.Expressions;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// Turns what a document declares into rows a host can bind to.
/// </summary>
public static class SvgViewerParameterFactory
{
    // The paint placeholder, so a colour that cannot be seeded starts where an unevaluated document
    // renders rather than at some other arbitrary grey.
    private static readonly Color PlaceholderColor = Color.FromArgb(0xFF, 0x80, 0x80, 0x80);

    public static IReadOnlyList<SvgViewerParameter> Create(IReadOnlyList<SvgExpressionParameter>? declarations)
    {
        if (declarations is null || declarations.Count == 0)
        {
            return Array.Empty<SvgViewerParameter>();
        }

        var rows = new List<SvgViewerParameter>(declarations.Count);

        foreach (var declaration in declarations)
        {
            rows.Add(Create(declaration));
        }

        return rows;
    }

    public static SvgViewerParameter Create(SvgExpressionParameter declaration)
    {
        if (declaration is null)
        {
            throw new ArgumentNullException(nameof(declaration));
        }

        var seed = Seed(declaration);

        return declaration.Type switch
        {
            ExprType.Number => Number(declaration, seed),
            ExprType.Integer => Integer(declaration, seed),
            ExprType.Color => new SvgViewerColorParameter(declaration, ToColor(seed)),
            ExprType.Boolean => new SvgViewerBooleanParameter(declaration, seed?.Type == ExprType.Boolean && seed.Value.AsBoolean),
            ExprType.String => new SvgViewerStringParameter(
                declaration,
                seed?.Type == ExprType.String ? seed.Value.AsString : string.Empty),
            _ => throw Unknown(declaration.Type)
        };
    }

    private static SvgViewerIntegerParameter Integer(SvgExpressionParameter declaration, ExprValue? seed)
    {
        var value = seed?.Type == ExprType.Integer ? seed.Value.AsInteger : 0;
        var range = Range(declaration);

        // At least one. A declared step is whole already, having resolved as an integer, and a step
        // of zero is refused -- but the fallback for no step at all is one rather than the number
        // row's fraction of the range.
        var step = range.Step > 0f ? Math.Max(1, (int)range.Step) : 1;

        // Widened to reach the value whichever side it is on: an end that excludes the default would
        // have the field coerce it on the way onto the screen, which is a value nobody edited.
        var minimum = Math.Min((int)range.Minimum, value);
        var maximum = Math.Max((int)range.Maximum, value);

        return HasEnds(declaration)
            ? new SvgViewerIntegerParameter(declaration, value, minimum, maximum, step, hasSlider: true)
            : new SvgViewerIntegerParameter(declaration, value, int.MinValue, int.MaxValue, step, hasSlider: false);
    }

    /// <summary>Whether a slider has two ends to span: the declared ones, there being no others.</summary>
    /// <remarks>
    /// An end was once inferred from the default — 217 came up on a 0..500 slider whose ends were
    /// this code's guess, and a drag along it wrote numbers nobody had said the drawing takes. The
    /// 0..1 a range resolves to when nothing is declared is no better a guess for being written down
    /// somewhere: a parameter that says only <c>default="0.5"</c> has not said it lives in 0..1. So
    /// an undeclared range is no range, and the row is the field alone, free to take whatever the
    /// drawing would.
    ///
    /// A fraction still gets its slider, because a fraction says so: a PaintCode import writes
    /// <c>min="0" max="1"</c> from the kind, as it writes 0..360 for an angle, and both are then
    /// declared like any other.
    ///
    /// Both or neither, with nothing in between to answer for: a document declaring one end is
    /// refused as it is read, and a hand-built one cannot resolve either — the end nobody wrote falls
    /// back to the 0 or the 1 of the default range, and a min above a max is refused in its turn.
    /// The PaintCode writer writes the pair for the same reason.
    /// </remarks>
    private static bool HasEnds(SvgExpressionParameter declaration)
        => declaration.MinExpression is { } && declaration.MaxExpression is { };

    /// <summary>The declared range, or the default one where the block was refused.</summary>
    /// <remarks>
    /// Swallowed: the declaration panel already says what the block was refused for. The parameter
    /// is still offered, since the document renders.
    /// </remarks>
    private static SvgExpressionRange Range(SvgExpressionParameter declaration)
    {
        try
        {
            return declaration.ResolveRange();
        }
        catch (Exception resolveError) when (resolveError is ExprException or ArgumentException)
        {
            return SvgExpressionRange.Default;
        }
    }

    private static SvgViewerNumberParameter Number(SvgExpressionParameter declaration, ExprValue? seed)
    {
        var value = seed?.Type == ExprType.Number ? Widen(seed.Value.AsNumber) : 0d;
        var range = Range(declaration);

        // Whatever the range came from, the seed has to be reachable: an end that excludes its own
        // default would otherwise put the value somewhere it cannot return to.
        var minimum = Held(Math.Min(Widen(range.Minimum), value), decimal.MinValue);
        var maximum = Held(Math.Max(Widen(range.Maximum), value), decimal.MaxValue);

        return HasEnds(declaration)
            ? new SvgViewerNumberParameter(declaration, value, minimum, maximum, Widen(range.Step), hasSlider: true)
            : new SvgViewerNumberParameter(
                declaration,
                value,
                decimal.MinValue,
                decimal.MaxValue,
                Widen(range.Step),
                hasSlider: false);
    }

    /// <summary>An end as the field holds one, or <paramref name="free"/> where it cannot hold it.</summary>
    /// <remarks>
    /// The bounds are decimal, and a bound is any float expression: the language has no exponent
    /// literal but it multiplies, so a bound can resolve above what decimal holds, and its sqrt hands
    /// back what MathF hands back, so <c>sqrt(0 - 1)</c> is a bound that is not a number at all.
    /// Either is no end rather than an exception thrown while a document is opening.
    /// </remarks>
    private static decimal Held(double value, decimal free)
        => double.IsNaN(value) ? free
            : value <= (double)decimal.MinValue ? decimal.MinValue
            : value >= (double)decimal.MaxValue ? decimal.MaxValue
            : (decimal)value;

    /// <summary>A value as a document would write it.</summary>
    /// <remarks>
    /// One spelling for the readout beside a let and for the default a commit writes, so the two
    /// cannot disagree. Round-trip formatting for a number, since the same parser reads it back;
    /// three bytes for an opaque colour, because that is how a drawing writes one.
    /// </remarks>
    public static string Describe(ExprValue value) => value.Type switch
    {
        ExprType.Number => value.AsNumber.ToString("R", CultureInfo.InvariantCulture),
        ExprType.Integer => value.AsInteger.ToString(CultureInfo.InvariantCulture),
        ExprType.Color => value.Alpha == byte.MaxValue
            ? string.Format(CultureInfo.InvariantCulture, "#{0:x2}{1:x2}{2:x2}", value.Red, value.Green, value.Blue)
            : string.Format(CultureInfo.InvariantCulture, "#{0:x2}{1:x2}{2:x2}{3:x2}", value.Red, value.Green, value.Blue, value.Alpha),
        ExprType.Boolean => value.AsBoolean ? "true" : "false",

        // Quoted by the language itself, so a committed default is spelled the one way the lexer
        // reads back.
        ExprType.String => value.ToString(),
        _ => throw Unknown(value.Type),
    };

    private static Exception Unknown(ExprType type)
        => new NotSupportedException($"Unsupported {nameof(ExprType)}: {type}.");

    /// <summary>
    /// Widens a number the language computed to the double a control wants.
    /// </summary>
    /// <remarks>
    /// Through decimal, which rounds to the seven significant digits a float carries. Widening
    /// plainly keeps the binary tail, so <c>step="0.1"</c> arrives as 0.10000000149011612 and two
    /// ticks along reads 0.200000002980232. Narrowing gives back the same float, so this is a
    /// widening and not a rounding; what decimal cannot hold is widened plainly instead.
    /// </remarks>
    /// <remarks>
    /// Internal because <see cref="SvgViewer.TrySetParameterValue"/> puts a float back the same way.
    /// Both must land on the same double, or a row is modified against its own seed by a binary tail
    /// nobody chose.
    /// </remarks>
    internal static double Widen(float value)
    {
        try
        {
            return (double)(decimal)value;
        }
        catch (OverflowException)
        {
            return value;
        }
    }

    /// <summary>The declared default, evaluated as the binder will evaluate it.</summary>
    /// <remarks>
    /// A default is an expression — <c>tau / 4</c>, <c>hsl(200, 60%, 50%)</c> — and resolving it the
    /// same way is what makes this the value an unsupplied parameter renders with.
    /// </remarks>
    private static ExprValue? Seed(SvgExpressionParameter declaration)
    {
        if (declaration.DefaultExpression is null)
        {
            return null;
        }

        try
        {
            return ExprEvaluator.Create(
                    SvgExpressionDeclarations.Empty,
                    parameterValues: null)
                .EvaluateTo(
                    declaration.DefaultExpression,
                    declaration.Type,
                    $"The default for '{declaration.Name}'");
        }
        catch (Exception failure) when (failure is ExprException or ArgumentException)
        {
            // clamp throws ArgumentException rather than the language's own, and this runs while a
            // document is opening: a drawing that renders must not fail to open.
            return null;
        }
    }

    internal static Color ToColor(ExprValue? seed)
        => seed?.Type == ExprType.Color
            ? Color.FromArgb(seed.Value.Alpha, seed.Value.Red, seed.Value.Green, seed.Value.Blue)
            : PlaceholderColor;
}
