// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using SkiaSharp;
using Svg.Expressions;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// Sweeps the evaluator's <c>hsl</c> against SkiaSharp's own <c>SKColor.FromHsl</c>, which is what
/// the generated code calls.
/// </summary>
/// <remarks>
/// The one function the evaluator reimplements rather than delegates, and it nearly shipped an
/// off-by-one: the final conversion to a byte <b>truncates</b>, and a rounding version disagrees
/// with SkiaSharp on 40,747 of the 53,361 samples below. The whole grid is swept, which is what
/// makes that a fact rather than a spot check.
/// </remarks>
public class ExprEvaluatorHslTests
{
    private static readonly IReadOnlyDictionary<string, ExprType> s_symbols = new Dictionary<string, ExprType>(StringComparer.Ordinal)
    {
        ["h"] = ExprType.Number,
        ["s"] = ExprType.Number,
        ["l"] = ExprType.Number,
        ["a"] = ExprType.Number
    };

    /// <summary>
    /// Evaluates through the public surface, so the sweep covers the whole composition — the hue
    /// wrap and the percentage scaling as well as the conversion.
    /// </summary>
    private static ExprValue Evaluate(string expression, float h, float s, float l, float a = 1f)
    {
        var values = new Dictionary<string, ExprValue>(StringComparer.Ordinal)
        {
            ["h"] = ExprValue.Number(h),
            ["s"] = ExprValue.Number(s),
            ["l"] = ExprValue.Number(l),
            ["a"] = ExprValue.Number(a)
        };

        return new ExprEvaluator(s_symbols, values).Evaluate(expression);
    }

    /// <summary>What ExprHelpers.SvgHsl does, using the real SkiaSharp.</summary>
    private static SKColor Reference(float h, float s, float l)
        => SKColor.FromHsl(
            ((h % 360f) + 360f) % 360f,
            Math.Clamp(s, 0f, 1f) * 100f,
            Math.Clamp(l, 0f, 1f) * 100f);

    [Fact]
    public void Hsl_Matches_SkiaSharp_Across_The_Whole_Domain()
    {
        var samples = 0;
        var mismatches = 0;
        string? first = null;

        for (var hue = 0; hue <= 360; hue += 3)
        {
            for (var saturation = 0; saturation <= 100; saturation += 5)
            {
                for (var lightness = 0; lightness <= 100; lightness += 5)
                {
                    samples++;

                    var s = saturation / 100f;
                    var l = lightness / 100f;

                    var expected = Reference(hue, s, l);
                    var actual = Evaluate("hsl(h, s, l)", hue, s, l);

                    if (expected.Red == actual.Red
                        && expected.Green == actual.Green
                        && expected.Blue == actual.Blue
                        && expected.Alpha == actual.Alpha)
                    {
                        continue;
                    }

                    mismatches++;
                    first ??= $"h={hue} s={s} l={l}: SkiaSharp gave "
                              + $"{expected.Red},{expected.Green},{expected.Blue},{expected.Alpha} "
                              + $"and the evaluator gave {actual.Red},{actual.Green},{actual.Blue},{actual.Alpha}";
                }
            }
        }

        Assert.Equal(53361, samples);
        Assert.True(mismatches == 0, $"{mismatches} of {samples} samples disagree. First: {first}");
    }

    [Fact]
    public void Hue_Is_Wrapped_Before_Conversion_Rather_Than_By_FromHsl()
    {
        // FromHsl folds the hue back into range only once, so it alone is wrong past a full turn.
        // SvgHsl wraps first, which makes the wrap load-bearing rather than defensive, and both back
        // ends have to do it in that order.
        var wrapped = Evaluate("hsl(h, s, l)", 720f, 0.5f, 0.5f);
        var equivalent = Evaluate("hsl(h, s, l)", 0f, 0.5f, 0.5f);

        Assert.Equal(
            (equivalent.Red, equivalent.Green, equivalent.Blue),
            (wrapped.Red, wrapped.Green, wrapped.Blue));

        // The naive route really does differ, which is what makes the order matter.
        var naive = SKColor.FromHsl(720f, 50f, 50f);
        Assert.NotEqual((naive.Red, naive.Green, naive.Blue), (wrapped.Red, wrapped.Green, wrapped.Blue));
    }

    [Fact]
    public void Negative_Hue_Wraps_The_Same_Way_SkiaSharp_Does()
    {
        var evaluated = Evaluate("hsl(h, s, l)", -30f, 0.5f, 0.5f);
        var expected = Reference(-30f, 0.5f, 0.5f);

        Assert.Equal((expected.Red, expected.Green, expected.Blue), (evaluated.Red, evaluated.Green, evaluated.Blue));
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.25f, 64)]
    [InlineData(0.5f, 128)]
    [InlineData(1f, 255)]
    // Out of range on both sides: the alpha argument is clamped to 0..1 before scaling.
    [InlineData(-1f, 0)]
    [InlineData(4f, 255)]
    public void Hsla_Scales_And_Clamps_The_Alpha(float alpha, byte expected)
    {
        var evaluated = Evaluate("hsla(h, s, l, a)", 210f, 0.5f, 0.5f, alpha);

        Assert.Equal(expected, evaluated.Alpha);

        // The colour channels are whatever hsl would have produced; alpha rides on top.
        var opaque = Evaluate("hsl(h, s, l)", 210f, 0.5f, 0.5f);
        Assert.Equal((opaque.Red, opaque.Green, opaque.Blue), (evaluated.Red, evaluated.Green, evaluated.Blue));
    }

    [Fact]
    public void Saturation_And_Lightness_Are_Clamped_To_The_Unit_Interval()
    {
        Assert.Equal(
            Evaluate("hsl(h, s, l)", 200f, 1f, 1f).ToString(),
            Evaluate("hsl(h, s, l)", 200f, 2f, 5f).ToString());

        Assert.Equal(
            Evaluate("hsl(h, s, l)", 200f, 0f, 0f).ToString(),
            Evaluate("hsl(h, s, l)", 200f, -1f, -1f).ToString());
    }
}
