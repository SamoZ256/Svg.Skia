// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using Svg.Expressions;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// Measures the netstandard2.0 maths fallback against <c>MathF</c>, which is what every other
/// target — and all of the generated code — actually calls.
/// </summary>
/// <remarks>
/// The fallback exists because <c>MathF</c> arrived with netstandard2.1, and it only runs on a
/// target this repository does not test, so without this its accuracy would be a claim in a comment.
/// What it pins is the shape of the answer rather than a count: the exactly-rounded and integral
/// functions have to agree bit for bit, and the transcendental ones are allowed one ulp and no more.
/// </remarks>
public class ExprMathFallbackTests
{
    /// <summary>
    /// Maps a float onto a monotonically increasing integer, so subtracting two of them counts the
    /// representable values in between. Negative zero lands on the same key as positive zero.
    /// </summary>
    private static long Key(float value)
    {
        var bits = BitConverter.SingleToInt32Bits(value);

        return bits >= 0 ? bits : (long)int.MinValue - bits;
    }

    private static long UlpsApart(float left, float right)
    {
        if (float.IsNaN(left) || float.IsNaN(right))
        {
            return float.IsNaN(left) && float.IsNaN(right) ? 0 : long.MaxValue;
        }

        if (left.Equals(right))
        {
            return 0;
        }

        if (float.IsInfinity(left) || float.IsInfinity(right))
        {
            return long.MaxValue;
        }

        return Math.Abs(Key(left) - Key(right));
    }

    private static void AssertWithin(
        long allowedUlps,
        string name,
        Func<float, float> single,
        Func<float, float> fallback)
    {
        long worst = 0;
        var worstAt = 0f;

        for (var x = -20.0; x <= 20.0; x += 0.0007)
        {
            var value = (float)x;
            var apart = UlpsApart(single(value), fallback(value));

            if (apart > worst)
            {
                worst = apart;
                worstAt = value;
            }
        }

        Assert.True(
            worst <= allowedUlps,
            $"{name} fallback is {worst} ulps from MathF at x={worstAt:R} (allowed {allowedUlps}).");
    }

    [Fact]
    public void The_Exactly_Rounded_Functions_Agree_Bit_For_Bit()
    {
        // IEEE 754 requires sqrt to be exactly rounded, and the rest are integral or a sign flip,
        // so double precision cannot change the answer for any of these.
        AssertWithin(0, "Sqrt", MathF.Sqrt, ExprMathFallback.Sqrt);
        AssertWithin(0, "Abs", MathF.Abs, ExprMathFallback.Abs);
        AssertWithin(0, "Floor", MathF.Floor, ExprMathFallback.Floor);
        AssertWithin(0, "Ceiling", MathF.Ceiling, ExprMathFallback.Ceiling);
        AssertWithin(0, "Round", MathF.Round, ExprMathFallback.Round);
    }

    [Fact]
    public void The_Transcendental_Functions_Agree_To_Within_One_Ulp()
    {
        // These genuinely differ — computing in double and narrowing is not the same as computing
        // in single — but never by more than the last bit. Colours quantise to bytes, so a
        // difference this size does not reach a pixel, which is why the netstandard2.0 build is
        // allowed to ship it.
        AssertWithin(1, "Sin", MathF.Sin, ExprMathFallback.Sin);
        AssertWithin(1, "Cos", MathF.Cos, ExprMathFallback.Cos);
        AssertWithin(1, "Tan", MathF.Tan, ExprMathFallback.Tan);
    }

    [Fact]
    public void Pow_Agrees_To_Within_One_Ulp_Across_A_Grid()
    {
        long worst = 0;
        var worstAt = (0f, 0f);

        for (var x = 0.0; x <= 8.0; x += 0.01)
        {
            for (var y = -3.0; y <= 3.0; y += 0.05)
            {
                var apart = UlpsApart(MathF.Pow((float)x, (float)y), ExprMathFallback.Pow((float)x, (float)y));

                if (apart > worst)
                {
                    worst = apart;
                    worstAt = ((float)x, (float)y);
                }
            }
        }

        Assert.True(worst <= 1, $"Pow fallback is {worst} ulps from MathF at {worstAt} (allowed 1).");
    }

    [Fact]
    public void Pi_Is_Exactly_MathF_Pi()
        => Assert.Equal(MathF.PI, ExprMathFallback.Pi);

    [Fact]
    public void Clamp_Matches_Math_Clamp_Including_The_Reversed_Range_Throw()
    {
        Assert.Equal(Math.Clamp(5f, 0f, 1f), ExprMathFallback.Clamp(5f, 0f, 1f));
        Assert.Equal(Math.Clamp(-5f, 0f, 1f), ExprMathFallback.Clamp(-5f, 0f, 1f));
        Assert.Equal(Math.Clamp(0.5f, 0f, 1f), ExprMathFallback.Clamp(0.5f, 0f, 1f));

        // NaN falls through both comparisons and comes back out, in both implementations.
        Assert.True(float.IsNaN(ExprMathFallback.Clamp(float.NaN, 0f, 1f)));
        Assert.True(float.IsNaN(Math.Clamp(float.NaN, 0f, 1f)));

        // A reversed range throws rather than silently picking a bound. The evaluator relies on
        // this to match generated code, which raises the same thing at runtime.
        Assert.Throws<ArgumentException>(() => Math.Clamp(0f, 1f, 0f));
        Assert.Throws<ArgumentException>(() => ExprMathFallback.Clamp(0f, 1f, 0f));
    }
}
