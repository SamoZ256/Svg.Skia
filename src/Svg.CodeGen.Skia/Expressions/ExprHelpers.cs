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
    public const string WithSaturation = "SvgWithSaturation";
    public const string Upper = "SvgUpper";
    public const string Lower = "SvgLower";
    public const string Len = "SvgLen";
    public const string Str = "SvgStr";
    public const string IAbs = "SvgIAbs";
    public const string IDiv = "SvgIDiv";
    public const string IMod = "SvgIMod";
    public const string Int = "SvgInt";
    public const string Num = "SvgNum";
    public const string Tangent = "SvgTangent";
    public const string Dash = "SvgDash";

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

        // Multiplied by the reciprocal rather than divided, to match how ShimSkiaSharp.SKColor
        // converts itself to SKColorF. The two are not the same in floating point — 1/255f is itself
        // rounded — and they disagree for 126 of the 256 byte values. That mattered: a literal
        // gradient stop is emitted as the floats the model already converted this way, so dividing
        // here made generated code disagree with its own literal stops and with the runtime, which
        // showed up as a one-level difference on a gradient pixel.
        new(ToColorF, new[]
        {
            $"private static SKColorF {ToColorF}(SKColor color)",
            "    => new SKColorF(color.Red * (1 / 255.0f), color.Green * (1 / 255.0f), color.Blue * (1 / 255.0f), color.Alpha * (1 / 255.0f));"
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
        }),

        // Written out rather than through SkiaSharp's own round trip, and in doubles: its ToHsv
        // answers 60.000008 for yellow, which lands the far side of a sector boundary and turns one
        // channel a byte darker. Character for character what ExprValueBackend computes, because
        // ExprEvaluatorDifferentialTests holds the two to the same byte.
        new(WithSaturation, new[]
        {
            $"private static SKColor {WithSaturation}(SKColor color, float s)",
            "{",
            "    var red = color.Red / 255d;",
            "    var green = color.Green / 255d;",
            "    var blue = color.Blue / 255d;",
            "    var max = Math.Max(red, Math.Max(green, blue));",
            "    var min = Math.Min(red, Math.Min(green, blue));",
            "    var delta = max - min;",
            "    var hue = 0d;",
            "",
            "    if (delta > 0d)",
            "    {",
            "        hue = max == red ? 60d * (((green - blue) / delta) % 6d)",
            "            : max == green ? 60d * (((blue - red) / delta) + 2d)",
            "            : 60d * (((red - green) / delta) + 4d);",
            "    }",
            "",
            "    if (hue < 0d)",
            "    {",
            "        hue += 360d;",
            "    }",
            "",
            "    var chroma = max * Math.Min(1d, Math.Max(0d, s));",
            "    var sector = ((hue % 360d) + 360d) % 360d / 60d;",
            "    var second = chroma * (1d - Math.Abs((sector % 2d) - 1d));",
            "    var match = max - chroma;",
            "",
            "    var (r, g, b) = (int)sector switch",
            "    {",
            "        0 => (chroma, second, 0d),",
            "        1 => (second, chroma, 0d),",
            "        2 => (0d, chroma, second),",
            "        3 => (0d, second, chroma),",
            "        4 => (second, 0d, chroma),",
            "        _ => (chroma, 0d, second)",
            "    };",
            "",
            $"    return new SKColor({WithSaturation}Channel(r + match), {WithSaturation}Channel(g + match), {WithSaturation}Channel(b + match), color.Alpha);",
            "}",
            "",
            $"private static byte {WithSaturation}Channel(double value) => (byte)(Math.Min(1d, Math.Max(0d, value)) * 255d);"
        }),

        // Invariant, or the generated code would fold a Turkish dotless i where the interpreter
        // did not. Length is returned as a float because that is what a number is here.
        new(Upper, new[]
        {
            $"private static string {Upper}(string value) => value.ToUpperInvariant();"
        }),

        new(Lower, new[]
        {
            $"private static string {Lower}(string value) => value.ToLowerInvariant();"
        }),

        new(Len, new[]
        {
            $"private static int {Len}(string value) => value.Length;"
        }),

        // Two, because str() has an overload per numeric type and C# picks between them by the
        // argument it is handed -- which is already the right type, the checker having said so.
        new(Str, new[]
        {
            $"private static string {Str}(float value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);",
            string.Empty,
            $"private static string {Str}(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);"
        }),

        // Math.Abs(int.MinValue) throws, its answer being one past the top. int.MaxValue is the
        // nearest that fits, and where int() and integer division saturate.
        // ExprValueBackend.Absolute spells it the same way.
        new(IAbs, new[]
        {
            $"private static int {IAbs}(int value) => value == int.MinValue ? int.MaxValue : Math.Abs(value);"
        }),

        // The number path answers a zero divisor with NaN, which int() reads as zero.
        // ExprValueBackend.Remainder spells it the same way.
        new(IMod, new[]
        {
            $"private static int {IMod}(int left, int right)",
            "    => right == 0 || (left == int.MinValue && right == -1) ? 0 : left % right;"
        }),

        // A drawing that renders must not start throwing because a divisor reached zero, and the
        // number path does not -- it produces an infinity. These are that infinity's integer
        // spelling, and the ends SvgInt saturates to, so int(1 / 0) and 1 / 0 agree.
        // ExprValueBackend.Divide spells it the same way.
        new(IDiv, new[]
        {
            $"private static int {IDiv}(int left, int right)",
            "    => right != 0 && !(left == int.MinValue && right == -1)",
            "        ? left / right",
            "        : left == 0 && right == 0 ? 0",
            "        : (left < 0) == (right < 0) ? int.MaxValue",
            "        : int.MinValue;"
        }),

        // The ends are named rather than left to a bare cast: .NET does not promise what
        // (int)1e30f is, and ExprValueBackend.Truncate has to land on the same answer.
        new(Int, new[]
        {
            $"private static int {Int}(float value)",
            "    => float.IsNaN(value) ? 0",
            "        : value >= 2147483647f ? int.MaxValue",
            "        : value <= -2147483648f ? int.MinValue",
            "        : (int)value;"
        }),

        new(Num, new[]
        {
            $"private static float {Num}(int value) => value;"
        }),

        // Character for character what ShimSkiaSharp.SymMatrix.Tangent computes, including the
        // double division: a skew driven by an expression must land on the same coefficient here
        // as it does in the renderer, and SkiaCSharpRenderTests diffs the two at a zero threshold.
        new(Tangent, new[]
        {
            $"private static float {Tangent}(float degrees) => (float)Math.Tan(Math.PI * degrees / 180d);"
        }),

        // What SvgSceneExpressionEvaluator does with a bound phase, so the two agree about a
        // binding that comes to nothing: a pattern shifted by nothing is the dash as written.
        new(Dash, new[]
        {
            $"private static SKPathEffect {Dash}(float[] intervals, float phase)",
            "    => SKPathEffect.CreateDash(intervals, float.IsNaN(phase) || float.IsInfinity(phase) ? 0f : phase);"
        })
    };
}
