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
            ExprType.Color => new SvgViewerColorParameter(declaration, ToColor(seed)),
            ExprType.Boolean => new SvgViewerBooleanParameter(declaration, seed?.Type == ExprType.Boolean && seed.Value.AsBoolean),
            ExprType.String => new SvgViewerStringParameter(
                declaration,
                seed?.Type == ExprType.String ? seed.Value.AsString : string.Empty),
            _ => throw Unknown(declaration.Type)
        };
    }

    private static SvgViewerNumberParameter Number(SvgExpressionParameter declaration, ExprValue? seed)
    {
        var value = seed?.Type == ExprType.Number ? Widen(seed.Value.AsNumber) : 0d;

        SvgExpressionRange range;
        try
        {
            range = declaration.ResolveRange();
        }
        catch (Exception resolveError) when (resolveError is ExprException or ArgumentException)
        {
            // Swallowed: the source pane already marks a bad range at the attribute it is in. The
            // parameter is still offered, since the document renders.
            range = SvgExpressionRange.Default;
        }

        double minimum = Widen(range.Minimum);
        double maximum = Widen(range.Maximum);

        // The 0..1 fallback is useless for a default of 217, so infer from the seed and round
        // outwards to ends a person would have chosen.
        if (!declaration.HasRange)
        {
            if (value > maximum)
            {
                maximum = NiceCeiling(2d * value);
            }
            else if (value < minimum)
            {
                minimum = -NiceCeiling(-2d * value);
            }
        }

        // Whatever the range came from, the seed has to be reachable: a declared range that excludes
        // its own default would otherwise put the slider somewhere the value cannot return to.
        minimum = Math.Min(minimum, value);
        maximum = Math.Max(maximum, value);

        return new SvgViewerNumberParameter(declaration, value, minimum, maximum, Widen(range.Step));
    }

    /// <summary>A value as a document would write it.</summary>
    /// <remarks>
    /// One spelling for the readout beside a let and for the default a commit writes, so the two
    /// cannot disagree. Round-trip formatting for a number, since the same parser reads it back;
    /// three bytes for an opaque colour, because that is how a drawing writes one.
    /// </remarks>
    public static string Describe(ExprValue value) => value.Type switch
    {
        ExprType.Number => value.AsNumber.ToString("R", CultureInfo.InvariantCulture),
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

    private static Color ToColor(ExprValue? seed)
        => seed?.Type == ExprType.Color
            ? Color.FromArgb(seed.Value.Alpha, seed.Value.Red, seed.Value.Green, seed.Value.Blue)
            : PlaceholderColor;

    // 1, 2 or 5 times a power of ten, so an inferred end is a round number.
    private static double NiceCeiling(double value)
    {
        if (value <= 0d || double.IsNaN(value) || double.IsInfinity(value))
        {
            return 1d;
        }

        var magnitude = Math.Pow(10d, Math.Floor(Math.Log10(value)));
        var normalised = value / magnitude;

        var step = normalised <= 1d ? 1d
            : normalised <= 2d ? 2d
            : normalised <= 5d ? 5d
            : 10d;

        return step * magnitude;
    }
}
