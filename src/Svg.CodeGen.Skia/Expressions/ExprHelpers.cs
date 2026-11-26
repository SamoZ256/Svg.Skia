// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ShimSkiaSharp;

namespace Svg.CodeGen.Skia.Expressions;

// Helper methods the generated class may need, each selected where the generated text names it.
// One may call another: what a helper names is scanned for in turn.
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
    public const string ListOf = "SvgList";
    public const string At = "SvgAt";
    public const string Last = "SvgLast";
    public const string Stop = "SvgStop";
    public const string Stops = "SvgStops";
    public const string Tangent = "SvgTangent";
    public const string Dash = "SvgDash";
    public const string Paint = "SvgPaint";
    public const string PathData = "SvgPathData";
    public const string PathBuilderData = "SvgPathBuilderData";
    public const string PathSlot = "SvgPathSlot";
    public const string PathBuilderSlot = "SvgPathBuilderSlot";
    public const string DrivenPathData = "SvgDrivenPathData";
    public const string DrivenPathBuilderData = "SvgDrivenPathBuilderData";

    /// <summary>The helpers that build what exists in SkiaSharp 3 alone, or in 4 alone.</summary>
    public static IReadOnlyList<string> OnlyIn(bool skiaSharp3)
        => skiaSharp3 ? new[] { PathData, PathSlot, DrivenPathData } : new[] { PathBuilderData, PathBuilderSlot, DrivenPathBuilderData };

    /// <summary>Whether a drawing calls the helper with no expression in it: the path data and the shared paint.</summary>
    public static bool Drawing(string key) => key is PathData or PathBuilderData or Paint;

    /// <summary>The members a helper's text declares, which nothing else in its class may be named.</summary>
    public static IEnumerable<string> Members(string[] helper)
        => helper.Select(line => s_member.Match(line)).Where(match => match.Success).Select(match => match.Groups[1].Value);

    // `<` as well, or a generic helper's name is never seen and a drawing could be named after it.
    private static readonly Regex s_member = new(@"^private static [^\s(]+ (\w+)\s*[<(;]", RegexOptions.CultureInvariant);

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

        // A list literal that reads an argument, filled into the array its site keeps per thread.
        // The elements arrive on the stack, so once the array exists a draw allocates nothing, and
        // a params array would allocate on every one.
        new(ListOf, new[]
        {
            $"private static IReadOnlyList<T> {ListOf}<T>(ref T[] list, Span<T> elements)",
            "{",
            "    var filled = list ??= new T[elements.Length];",
            "    elements.CopyTo(filled);",
            "    return filled;",
            "}"
        }),

        // Any index answers, because a drawing has to draw whatever it is handed: clamped into the
        // list, and zero or transparent from an empty one. ExprValue.At answers the same.
        new(At, new[]
        {
            $"private static T {At}<T>(IReadOnlyList<T> list, int index)",
            "    => list.Count == 0 ? default! : list[index < 0 ? 0 : index >= list.Count ? list.Count - 1 : index];"
        }),

        // The last of as many colours as both lists hold, as SvgStops counts them; transparent for none.
        new(Last, new[]
        {
            $"private static SKColor {Last}(IReadOnlyList<SKColor> colors, IReadOnlyList<float> offsets)",
            "{",
            "    var count = offsets == null ? colors.Count : Math.Min(colors.Count, offsets.Count);",
            "    return count == 0 ? default : colors[count - 1];",
            "}"
        }),

        // One colour of a gradient's list as its stop: faded by the paint's opacity, then made linear,
        // in the order a written stop's colour is.
        new(Stop, new[]
        {
            $"private static SKColor {Stop}(IReadOnlyList<SKColor> colors, int index, float alpha, bool linear)",
            "{",
            $"    var faded = {ScaleAlpha}(colors[index], alpha);",
            $"    return linear ? {ToLinearRgb}(faded) : faded;",
            "}"
        }),

        // A gradient's lists as the stops Skia takes, written over what a site kept from its last draw,
        // and whether anything differs from it: arrays are allocated only when the count changes.
        // SvgSceneExpressionEvaluator.Listed binds the same, through ClampStopOffset and
        // AdjustStopColorPos, whose arithmetic this repeats, and SkiaCSharpRenderTests diffs the two at
        // a zero threshold.
        new(Stops, new[]
        {
            $"private static bool {Stops}(IReadOnlyList<SKColor> colors, IReadOnlyList<float> offsets, float alpha, bool linear, ref SKColorF[] held, ref float[] positions)",
            "{",
            "    var count = offsets == null ? colors.Count : Math.Min(colors.Count, offsets.Count);",
            "    var changed = held == null || held.Length != count;",
            "    if (changed)",
            "    {",
            "        held = new SKColorF[count];",
            "    }",
            "",
            "    if (offsets != null && (positions == null || positions.Length != count))",
            "    {",
            "        positions = new float[count];",
            "        changed = true;",
            "    }",
            "",
            "    var reached = float.MinValue;",
            "    for (var i = 0; i < count; i++)",
            "    {",
            $"        var color = {ToColorF}({Stop}(colors, i, alpha, linear));",
            "        changed |= held[i] != color;",
            "        held[i] = color;",
            "        if (offsets == null)",
            "        {",
            "            continue;",
            "        }",
            "",
            "        var offset = offsets[i];",
            "        offset = float.IsNaN(offset) || float.IsNegativeInfinity(offset) ? 0f : float.IsPositiveInfinity(offset) ? 1f : Math.Min(Math.Max(offset, 0f), 1f);",
            "        if (offset > reached)",
            "        {",
            "            reached = offset;",
            "        }",
            "        else if (offset < reached)",
            "        {",
            "            offset = reached;",
            "        }",
            "",
            "        changed |= positions[i] != offset;",
            "        positions[i] = offset;",
            "    }",
            "",
            "    return changed;",
            "}"
        }),

        // Character for character what ShimSkiaSharp.SymMatrix.Tangent computes, including the
        // double division: a skew driven by an expression must land on the same coefficient here
        // as it does in the renderer, and SkiaCSharpRenderTests diffs the two at a zero threshold.
        new(Tangent, new[]
        {
            $"private static float {Tangent}(float degrees) => (float)Math.Tan(Math.PI * degrees / 180d);"
        }),

        // What SvgSceneExpressionEvaluator does with a bound dash, so the two agree about a binding
        // that comes to nothing: a pattern shifted by nothing is the dash as written, and an invalid
        // array strokes solid. null! because the generated file's nullable context is the caller's.
        new(Dash, new[]
        {
            $"private static SKPathEffect {Dash}(float[] intervals, float phase)",
            "{",
            "    var sum = 0f;",
            "",
            "    foreach (var interval in intervals)",
            "    {",
            "        if (float.IsNaN(interval) || float.IsInfinity(interval) || interval < 0f)",
            "        {",
            "            return null!;",
            "        }",
            "",
            "        sum += interval;",
            "    }",
            "",
            "    return sum <= 0f ? null! : SKPathEffect.CreateDash(intervals, float.IsNaN(phase) || float.IsInfinity(phase) ? 0f : phase);",
            "}"
        }),

        // A direct drawing's paint for every shape whose paint follows an argument: Skia copies a paint
        // into what it records or draws, so one per thread serves every draw, with nothing allocated.
        new(Paint, new[]
        {
            "[ThreadStatic]",
            $"private static SKPaint {Paint}Instance;",
            "",
            $"private static SKPaint {Paint}()",
            "{",
            $"    var paint = {Paint}Instance ??= new SKPaint();",
            "    paint.Reset();",
            "    return paint;",
            "}"
        }),

        // A run of path commands as data, which the compiler keeps as one blob: thousands of calls,
        // each a few dozen bytes of IL and more of native code, are one loop.
        new(PathData, PathDataHelper(PathData, "SKPath")),
        new(PathBuilderData, PathDataHelper(PathBuilderData, "SKPathBuilder")),

        // A clip an expression drives is built on every draw, into a path kept per thread. A path is
        // copied on write, so starting it over leaves alone the copy Skia kept for the clip it was
        // last given, and reuses the storage where nothing else holds it.
        new(PathSlot, PathSlotHelper(PathSlot, "SKPath", "Rewind")),
        new(PathBuilderSlot, PathSlotHelper(PathBuilderSlot, "SKPathBuilder", "Reset")),
        new(DrivenPathData, DrivenPathDataHelper(DrivenPathData, "SKPath")),
        new(DrivenPathBuilderData, DrivenPathDataHelper(DrivenPathBuilderData, "SKPathBuilder"))
    };

    private static string[] PathSlotHelper(string name, string target, string reset) => new[]
    {
        "[ThreadStatic]",
        $"private static {target}[] {name}Instances;",
        "",
        $"private static {target} {name}(int slot)",
        "{",
        $"    var paths = {name}Instances ??= new {target}[4];",
        "    if (slot >= paths.Length)",
        "    {",
        "        Array.Resize(ref paths, slot + 4);",
        $"        {name}Instances = paths;",
        "    }",
        "",
        $"    var path = paths[slot] ??= new {target}();",
        $"    path.{reset}();",
        "    return path;",
        "}"
    };

    /// <summary>
    /// Replays the commands of a driven clip shape: ShimSkiaSharp.SymPath.Apply, statement for
    /// statement, since the runtime draws what that answers and SkiaCSharpRenderTests diffs the two
    /// at a zero threshold. The verbs are SymPathOp's.
    /// </summary>
    private static string[] DrivenPathDataHelper(string name, string target) => new[]
    {
        $"private static void {name}({target} path, ReadOnlySpan<byte> ops, ReadOnlySpan<float> p)",
        "{",
        "    foreach (var value in p)",
        "    {",
        "        if (float.IsNaN(value) || float.IsInfinity(value))",
        "        {",
        "            return;",
        "        }",
        "    }",
        "",
        "    var i = 0;",
        "    float x = 0f, y = 0f, moveX = 0f, moveY = 0f;",
        "    foreach (var op in ops)",
        "    {",
        "        switch (op)",
        "        {",
        $"            case {(int)SymPathOp.MoveTo}: path.MoveTo(p[i], p[i + 1]); x = moveX = p[i]; y = moveY = p[i + 1]; i += 2; break;",
        $"            case {(int)SymPathOp.LineTo}: path.LineTo(p[i], p[i + 1]); x = p[i]; y = p[i + 1]; i += 2; break;",
        $"            case {(int)SymPathOp.QuadTo}: path.QuadTo(p[i], p[i + 1], p[i + 2], p[i + 3]); x = p[i + 2]; y = p[i + 3]; i += 4; break;",
        $"            case {(int)SymPathOp.CubicTo}: path.CubicTo(p[i], p[i + 1], p[i + 2], p[i + 3], p[i + 4], p[i + 5]); x = p[i + 4]; y = p[i + 5]; i += 6; break;",
        $"            case {(int)SymPathOp.Close}: path.Close(); x = moveX; y = moveY; break;",
        $"            case {(int)SymPathOp.ArcTo}:",
        "            {",
        "                var rx = Math.Abs(p[i]);",
        "                var ry = Math.Abs(p[i + 1]);",
        "                var endX = p[i + 5];",
        "                var endY = p[i + 6];",
        "                if (rx <= float.Epsilon || ry <= float.Epsilon)",
        "                {",
        "                    path.LineTo(endX, endY);",
        "                }",
        "                else if (!(Math.Abs(x - endX) <= float.Epsilon && Math.Abs(y - endY) <= float.Epsilon))",
        "                {",
        "                    path.ArcTo(rx, ry, p[i + 2], p[i + 3] != 0f ? SKPathArcSize.Large : SKPathArcSize.Small, p[i + 4] != 0f ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise, endX, endY);",
        "                }",
        "",
        "                x = endX;",
        "                y = endY;",
        "                i += 7;",
        "                break;",
        "            }",
        $"            case {(int)SymPathOp.Rect}:",
        "            {",
        "                var width = p[i + 2];",
        "                var height = p[i + 3];",
        "                var rx = p[i + 4];",
        "                var ry = p[i + 5];",
        "                if (width > 0f && height > 0f)",
        "                {",
        "                    var borrowedX = rx == 0f && ry > 0f ? p[i + 7] : rx;",
        "                    var borrowedY = ry == 0f && rx > 0f ? p[i + 6] : ry;",
        "                    rx = borrowedX;",
        "                    ry = borrowedY;",
        "                    if (rx < 0f && ry < 0f)",
        "                    {",
        "                        rx = 0f;",
        "                        ry = 0f;",
        "                    }",
        "",
        "                    if (rx == 0f || ry == 0f)",
        "                    {",
        "                        rx = 0f;",
        "                        ry = 0f;",
        "                    }",
        "",
        "                    if (rx < 0f)",
        "                    {",
        "                        rx = Math.Abs(rx);",
        "                    }",
        "",
        "                    if (ry < 0f)",
        "                    {",
        "                        ry = Math.Abs(ry);",
        "                    }",
        "",
        "                    if (rx > 0f && rx > width / 2f)",
        "                    {",
        "                        rx = width / 2f;",
        "                    }",
        "",
        "                    if (ry > 0f && ry > height / 2f)",
        "                    {",
        "                        ry = height / 2f;",
        "                    }",
        "",
        "                    var rect = SKRect.Create(p[i], p[i + 1], width, height);",
        "                    if (rx > 0f && ry > 0f)",
        "                    {",
        "                        path.AddRoundRect(rect, rx, ry);",
        "                    }",
        "                    else",
        "                    {",
        "                        path.AddRect(rect);",
        "                    }",
        "                }",
        "",
        "                i += 8;",
        "                break;",
        "            }",
        $"            case {(int)SymPathOp.Ellipse}:",
        "            {",
        "                var cx = p[i];",
        "                var cy = p[i + 1];",
        "                var rx = p[i + 2];",
        "                var ry = p[i + 3];",
        "                if (!(rx < 0f && ry < 0f))",
        "                {",
        "                    rx = Math.Abs(rx);",
        "                    ry = Math.Abs(ry);",
        "                    if (rx > 0f && ry > 0f)",
        "                    {",
        "                        path.AddOval(SKRect.Create(cx - rx, cy - ry, rx + rx, ry + ry));",
        "                    }",
        "                }",
        "",
        "                i += 4;",
        "                break;",
        "            }",
        $"            case {(int)SymPathOp.Circle}:",
        "                if (p[i + 2] > 0f)",
        "                {",
        "                    path.AddCircle(p[i], p[i + 1], p[i + 2]);",
        "                }",
        "",
        "                i += 3;",
        "                break;",
        "        }",
        "    }",
        "}"
    };

    /// <summary>
    /// Replays verbs onto <paramref name="target"/>: 0 move, 1 line, 2 quad, 3 cubic, 4 close, and 5
    /// to 8 an arc, plus 1 for the large one and 2 for counter-clockwise.
    /// </summary>
    private static string[] PathDataHelper(string name, string target) => new[]
    {
        $"private static void {name}({target} path, ReadOnlySpan<byte> verbs, ReadOnlySpan<float> p)",
        "{",
        "    var i = 0;",
        "    foreach (var verb in verbs)",
        "    {",
        "        switch (verb)",
        "        {",
        "            case 0: path.MoveTo(p[i], p[i + 1]); i += 2; break;",
        "            case 1: path.LineTo(p[i], p[i + 1]); i += 2; break;",
        "            case 2: path.QuadTo(p[i], p[i + 1], p[i + 2], p[i + 3]); i += 4; break;",
        "            case 3: path.CubicTo(p[i], p[i + 1], p[i + 2], p[i + 3], p[i + 4], p[i + 5]); i += 6; break;",
        "            case 4: path.Close(); break;",
        "            default: path.ArcTo(p[i], p[i + 1], p[i + 2], (SKPathArcSize)((verb - 5) & 1), (SKPathDirection)((verb - 5) >> 1), p[i + 3], p[i + 4]); i += 5; break;",
        "        }",
        "    }",
        "}"
    };
}
