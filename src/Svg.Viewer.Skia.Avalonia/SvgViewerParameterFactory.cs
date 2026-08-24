// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
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
            _ => new SvgViewerBooleanParameter(declaration, seed?.Type == ExprType.Boolean && seed.Value.AsBoolean)
        };
    }

    private static SvgViewerNumberParameter Number(SvgExpressionParameter declaration, ExprValue? seed)
    {
        var value = seed?.Type == ExprType.Number ? seed.Value.AsNumber : 0d;

        SvgExpressionRange range;
        try
        {
            range = declaration.ResolveRange();
        }
        catch (Exception resolveError) when (resolveError is ExprException or ArgumentException)
        {
            // Swallowed rather than reported: what is wrong with a range is marked in the source
            // pane, at the attribute it is wrong in, and saying it twice made the panel repeat what
            // the file already shows. The parameter is still offered — the document renders, and the
            // value is still bindable.
            range = SvgExpressionRange.Default;
        }

        double minimum = range.Minimum;
        double maximum = range.Maximum;

        // A document that declares no range still has to get a usable slider, and the 0..1 fallback
        // is useless for a default of 217. Infer from the seed instead, then round outwards so the
        // ends read as numbers a person would have chosen.
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

        return new SvgViewerNumberParameter(declaration, value, minimum, maximum, range.Step);
    }

    /// <summary>The declared default, evaluated as the binder will evaluate it.</summary>
    /// <remarks>
    /// Through <see cref="ExprEvaluator"/> rather than by parsing a number, because a default is an
    /// expression — <c>tau / 4</c>, <c>hsl(200, 60%, 50%)</c> — and because resolving it the same way
    /// is what makes the value shown here the value an unsupplied parameter would render with.
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
            // clamp refuses a reversed range by throwing an ArgumentException rather than the
            // language's own, and a default is evaluated here while a document is being opened: a
            // drawing that renders must not fail to open because of a parameter it can still offer.
            // What was wrong with it is the source pane's to say.
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
