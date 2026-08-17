// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;

namespace Svg.Expressions;

/// <summary>
/// The single-precision maths the evaluator needs, routed to <c>MathF</c> where the target
/// framework has it and to <see cref="ExprMathFallback"/> where it does not.
/// </summary>
/// <remarks>
/// Generated code calls <c>MathF</c>, so the evaluator has to as well or the two back ends would
/// disagree about the same document. <c>MathF</c> and <c>Math.Clamp</c> arrived with
/// netstandard2.1, so the netstandard2.0 build — which is also the flavour loaded into the compiler
/// as part of the source generator — falls back. How closely the fallback agrees is measured by
/// <c>ExprMathFallbackTests</c> rather than assumed.
/// </remarks>
internal static class ExprMath
{
#if NET6_0_OR_GREATER
    public const float Pi = MathF.PI;

    public static float Sin(float x) => MathF.Sin(x);

    public static float Cos(float x) => MathF.Cos(x);

    public static float Tan(float x) => MathF.Tan(x);

    public static float Abs(float x) => MathF.Abs(x);

    public static float Sqrt(float x) => MathF.Sqrt(x);

    public static float Floor(float x) => MathF.Floor(x);

    public static float Ceiling(float x) => MathF.Ceiling(x);

    public static float Round(float x) => MathF.Round(x);

    public static float Pow(float x, float y) => MathF.Pow(x, y);

    public static float Min(float x, float y) => MathF.Min(x, y);

    public static float Max(float x, float y) => MathF.Max(x, y);

    public static float Clamp(float value, float min, float max) => Math.Clamp(value, min, max);
#else
    public const float Pi = ExprMathFallback.Pi;

    public static float Sin(float x) => ExprMathFallback.Sin(x);

    public static float Cos(float x) => ExprMathFallback.Cos(x);

    public static float Tan(float x) => ExprMathFallback.Tan(x);

    public static float Abs(float x) => ExprMathFallback.Abs(x);

    public static float Sqrt(float x) => ExprMathFallback.Sqrt(x);

    public static float Floor(float x) => ExprMathFallback.Floor(x);

    public static float Ceiling(float x) => ExprMathFallback.Ceiling(x);

    public static float Round(float x) => ExprMathFallback.Round(x);

    public static float Pow(float x, float y) => ExprMathFallback.Pow(x, y);

    public static float Min(float x, float y) => ExprMathFallback.Min(x, y);

    public static float Max(float x, float y) => ExprMathFallback.Max(x, y);

    public static float Clamp(float value, float min, float max) => ExprMathFallback.Clamp(value, min, max);
#endif
}

/// <summary>
/// The double-precision stand-ins used where <c>MathF</c> does not exist.
/// </summary>
/// <remarks>
/// Compiled on every target, not just the one that uses it, so a test host on a modern framework
/// can compare it against <c>MathF</c> and pin how far apart they are. Otherwise the only
/// framework where this code runs would be one nothing tests.
/// </remarks>
internal static class ExprMathFallback
{
    // Exactly MathF.PI: the double is rounded to the nearest float either way.
    public const float Pi = (float)Math.PI;

    public static float Sin(float x) => (float)Math.Sin(x);

    public static float Cos(float x) => (float)Math.Cos(x);

    public static float Tan(float x) => (float)Math.Tan(x);

    public static float Abs(float x) => Math.Abs(x);

    public static float Sqrt(float x) => (float)Math.Sqrt(x);

    public static float Floor(float x) => (float)Math.Floor((double)x);

    public static float Ceiling(float x) => (float)Math.Ceiling((double)x);

    public static float Round(float x) => (float)Math.Round((double)x);

    public static float Pow(float x, float y) => (float)Math.Pow(x, y);

    public static float Min(float x, float y) => Math.Min(x, y);

    public static float Max(float x, float y) => Math.Max(x, y);

    // Math.Clamp's own behaviour, including the throw: a reversed range is a mistake in the
    // document, and generated code would raise it at runtime rather than picking a bound.
    public static float Clamp(float value, float min, float max)
    {
        if (min > max)
        {
            throw new ArgumentException($"'{min}' cannot be greater than {max}.", nameof(min));
        }

        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }
}
