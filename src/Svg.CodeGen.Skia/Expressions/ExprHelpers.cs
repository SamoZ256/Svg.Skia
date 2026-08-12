// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Collections.Generic;

namespace Svg.CodeGen.Skia.Expressions;

// Helper methods the generated class may need. Each one is self-contained and never calls
// another: emission is decided by scanning the generated body for the marker, so a helper that
// depended on a second helper could select itself without selecting its dependency.
internal static class ExprHelpers
{
    public const string ScaleAlpha = "SvgScaleAlpha";
    public const string ToLinearRgb = "SvgToLinearRgb";
    public const string ToColorF = "SvgToColorF";
    public const string Lerp = "SvgLerp";
    public const string Rgb = "SvgRgb";
    public const string Rgba = "SvgRgba";
    public const string Hsl = "SvgHsl";
    public const string Hsla = "SvgHsla";
    public const string Mix = "SvgMix";
    public const string WithAlpha = "SvgWithAlpha";

    // Ordered so generated output is stable.
    public static IReadOnlyList<KeyValuePair<string, string[]>> All { get; } = new List<KeyValuePair<string, string[]>>
    {
        new(ScaleAlpha, new[]
        {
            $"private static SKColor {ScaleAlpha}(SKColor color, float factor)",
            "    => color.WithAlpha((byte)Math.Round(color.Alpha * (double)factor));"
        }),

        new(ToLinearRgb, new[]
        {
            $"private static SKColor {ToLinearRgb}(SKColor color)",
            "    => new SKColor(",
            $"        {ToLinearRgb}Channel(color.Red),",
            $"        {ToLinearRgb}Channel(color.Green),",
            $"        {ToLinearRgb}Channel(color.Blue),",
            "        color.Alpha);",
            "",
            $"private static byte {ToLinearRgb}Channel(byte value)",
            "{",
            "    var srgb = value / 255f;",
            "    var linear = srgb <= 0.04045f ? srgb / 12.92f : (float)Math.Pow((srgb + 0.055f) / 1.055f, 2.4f);",
            "    return (byte)Math.Round(linear * 255f);",
            "}"
        }),

        new(ToColorF, new[]
        {
            $"private static SKColorF {ToColorF}(SKColor color)",
            "    => new SKColorF(color.Red / 255f, color.Green / 255f, color.Blue / 255f, color.Alpha / 255f);"
        }),

        new(Lerp, new[]
        {
            $"private static float {Lerp}(float a, float b, float t) => a + (b - a) * t;"
        }),

        new(Rgb, new[]
        {
            $"private static SKColor {Rgb}(float r, float g, float b)",
            "    => new SKColor(",
            "        (byte)Math.Round(Math.Clamp(r, 0f, 255f)),",
            "        (byte)Math.Round(Math.Clamp(g, 0f, 255f)),",
            "        (byte)Math.Round(Math.Clamp(b, 0f, 255f)),",
            "        255);"
        }),

        new(Rgba, new[]
        {
            $"private static SKColor {Rgba}(float r, float g, float b, float a)",
            "    => new SKColor(",
            "        (byte)Math.Round(Math.Clamp(r, 0f, 255f)),",
            "        (byte)Math.Round(Math.Clamp(g, 0f, 255f)),",
            "        (byte)Math.Round(Math.Clamp(b, 0f, 255f)),",
            "        (byte)Math.Round(Math.Clamp(a, 0f, 1f) * 255f));"
        }),

        new(Hsl, new[]
        {
            $"private static SKColor {Hsl}(float h, float s, float l)",
            "    => SKColor.FromHsl(",
            "        ((h % 360f) + 360f) % 360f,",
            "        Math.Clamp(s, 0f, 1f) * 100f,",
            "        Math.Clamp(l, 0f, 1f) * 100f);"
        }),

        new(Hsla, new[]
        {
            $"private static SKColor {Hsla}(float h, float s, float l, float a)",
            "    => SKColor.FromHsl(",
            "        ((h % 360f) + 360f) % 360f,",
            "        Math.Clamp(s, 0f, 1f) * 100f,",
            "        Math.Clamp(l, 0f, 1f) * 100f)",
            "        .WithAlpha((byte)Math.Round(Math.Clamp(a, 0f, 1f) * 255f));"
        }),

        new(Mix, new[]
        {
            $"private static SKColor {Mix}(SKColor a, SKColor b, float t)",
            "{",
            "    var k = Math.Clamp(t, 0f, 1f);",
            "    return new SKColor(",
            "        (byte)Math.Round(a.Red + (b.Red - a.Red) * k),",
            "        (byte)Math.Round(a.Green + (b.Green - a.Green) * k),",
            "        (byte)Math.Round(a.Blue + (b.Blue - a.Blue) * k),",
            "        (byte)Math.Round(a.Alpha + (b.Alpha - a.Alpha) * k));",
            "}"
        }),

        new(WithAlpha, new[]
        {
            $"private static SKColor {WithAlpha}(SKColor color, float a)",
            "    => color.WithAlpha((byte)Math.Round(Math.Clamp(a, 0f, 1f) * 255f));"
        })
    };
}
