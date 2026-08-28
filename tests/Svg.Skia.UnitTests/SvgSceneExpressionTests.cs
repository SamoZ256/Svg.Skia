using System.Collections.Generic;
using System.Linq;
using ShimSkiaSharp;
using Svg.Model.Services;
using Xunit;

namespace Svg.Skia.UnitTests;

public class SvgSceneExpressionTests
{
    private const string Ns = "https://svg.skia/expr/1.0";

    private static SKPicture? Build(string svgMarkup)
    {
        var document = SvgService.FromSvg(svgMarkup);
        Assert.NotNull(document);
        var assetLoader = new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings()));
        return SvgSceneRuntime.CreateModel(document!, assetLoader);
    }

    private static IEnumerable<SKPaint> EnumeratePaints(SKPicture? picture)
    {
        if (picture?.Commands is null)
        {
            yield break;
        }

        foreach (var command in picture.Commands)
        {
            if (command is DrawPathCanvasCommand { Paint: { } paint })
            {
                yield return paint;
            }
        }
    }

    private static SKPaint SinglePaint(string svgMarkup)
        => Assert.Single(EnumeratePaints(Build(svgMarkup)));

    [Fact]
    public void Plain_Svg_Has_No_Expression()
    {
        var paint = SinglePaint("""
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect x="0" y="0" width="10" height="10" fill="#808080" />
            </svg>
            """);

        Assert.NotNull(paint.Color);
        Assert.Null(paint.Color!.Value.Expression);
    }

    [Fact]
    public void Fill_Expression_Rides_Along_With_The_Fallback_Color()
    {
        var paint = SinglePaint("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <rect x="0" y="0" width="10" height="10" fill="{{ primary }}" />
            </svg>
            """);

        Assert.NotNull(paint.Color);
        var color = paint.Color!.Value;

        // The plain attribute stays authoritative for rendering.
        Assert.Equal(0x80, color.Red);
        Assert.Equal(0x80, color.Green);
        Assert.Equal(0x80, color.Blue);
        Assert.Equal(0xFF, color.Alpha);

        Assert.Equal(SymNode.Source("primary"), color.Expression);
    }

    [Fact]
    public void Stroke_Expression_Is_Read_From_The_Stroke_Attribute()
    {
        var paints = EnumeratePaints(Build("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <rect x="0" y="0" width="10" height="10" fill="none"
                    stroke="{{ edge }}" stroke-width="2" />
            </svg>
            """)).ToList();

        var stroke = Assert.Single(paints, p => p.Style == SKPaintStyle.Stroke);
        Assert.Equal(SymNode.Source("edge"), stroke.Color!.Value.Expression);
    }

    [Fact]
    public void Fill_And_Stroke_Expressions_Do_Not_Cross_Over()
    {
        var paints = EnumeratePaints(Build("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <rect x="0" y="0" width="10" height="10"
                    fill="{{ body }}"
                    stroke="{{ edge }}" stroke-width="2" />
            </svg>
            """)).ToList();

        var fill = Assert.Single(paints, p => p.Style == SKPaintStyle.Fill);
        var stroke = Assert.Single(paints, p => p.Style == SKPaintStyle.Stroke);

        Assert.Equal(SymNode.Source("body"), fill.Color!.Value.Expression);
        Assert.Equal(SymNode.Source("edge"), stroke.Color!.Value.Expression);
    }

    [Fact]
    public void Fill_Opacity_Wraps_The_Expression_In_ScaleAlpha()
    {
        var paint = SinglePaint("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <rect x="0" y="0" width="10" height="10" fill="{{ primary }}" fill-opacity="0.5" />
            </svg>
            """);

        var color = paint.Color!.Value;

        // Concrete alpha is folded as before.
        Assert.Equal(128, color.Alpha);

        var binary = Assert.IsType<SymBinary>(color.Expression);
        Assert.Equal(SymOp.ScaleAlpha, binary.Op);
        Assert.Equal(SymNode.Source("primary"), binary.Left);
        Assert.Equal(SymNode.Literal(0.5), binary.Right);
    }

    [Fact]
    public void A_Fill_Opacity_Expression_Scales_The_Literal_Colour()
    {
        var paint = SinglePaint("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <rect x="0" y="0" width="10" height="10" fill="#3366cc" fill-opacity="{{ fade }}" />
            </svg>
            """);

        var color = paint.Color!.Value;

        // The placeholder is 1, so the design-time colour is the one the author wrote.
        Assert.Equal(0xcc, color.Blue);
        Assert.Equal(255, color.Alpha);

        // Nothing symbolic to scale until the literal colour is written back out as one.
        var binary = Assert.IsType<SymBinary>(color.Expression);
        Assert.Equal(SymOp.ScaleAlpha, binary.Op);
        Assert.Equal(SymNode.Source("#3366ccff"), binary.Left);
        Assert.Equal(SymNode.Source("fade"), binary.Right);
    }

    [Fact]
    public void A_Fill_And_Its_Opacity_Compose_When_Both_Are_Expressions()
    {
        var paint = SinglePaint("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <rect x="0" y="0" width="10" height="10" fill="{{ primary }}" fill-opacity="{{ fade }}" />
            </svg>
            """);

        var binary = Assert.IsType<SymBinary>(paint.Color!.Value.Expression);

        Assert.Equal(SymOp.ScaleAlpha, binary.Op);
        Assert.Equal(SymNode.Source("primary"), binary.Left);
        Assert.Equal(SymNode.Source("fade"), binary.Right);
    }

    [Fact]
    public void A_Stroke_Opacity_Expression_Reaches_The_Stroke_Alone()
    {
        var paints = EnumeratePaints(Build("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <rect x="2" y="2" width="10" height="10" fill="#3366cc"
                    stroke="#000000" stroke-width="2" stroke-opacity="{{ fade }}" />
            </svg>
            """)).ToList();

        var fill = Assert.Single(paints, p => p.Style == SKPaintStyle.Fill);
        var stroke = Assert.Single(paints, p => p.Style == SKPaintStyle.Stroke);

        Assert.Null(fill.Color!.Value.Expression);

        var binary = Assert.IsType<SymBinary>(stroke.Color!.Value.Expression);
        Assert.Equal(SymOp.ScaleAlpha, binary.Op);
        Assert.Equal(SymNode.Source("fade"), binary.Right);
    }

    [Fact]
    public void A_Stop_Opacity_Expression_Scales_That_Stop_Only()
    {
        var picture = Build("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs>
                <linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="100" y2="0">
                  <stop offset="0%" stop-color="#3366cc" stop-opacity="{{ fade }}" />
                  <stop offset="100%" stop-color="#000000" />
                </linearGradient>
              </defs>
              <rect x="0" y="0" width="100" height="100" fill="url(#g)" />
            </svg>
            """);

        var shader = EnumeratePaints(picture)
            .Select(p => p.Shader)
            .OfType<LinearGradientShader>()
            .Single();

        // The element's own fill-opacity is 1, and Multiply folds that away rather than recording it.
        var binary = Assert.IsType<SymBinary>(shader.Colors![0].Expression);
        Assert.Equal(SymOp.ScaleAlpha, binary.Op);
        Assert.Equal(SymNode.Source("#3366ccff"), binary.Left);
        Assert.Equal(SymNode.Source("fade"), binary.Right);

        Assert.Null(shader.Colors[1].Expression);
    }

    [Fact]
    public void A_Fill_Opacity_Expression_Reaches_Every_Stop_Of_A_Gradient_Fill()
    {
        // The element's own alpha is applied to each stop rather than to the shader, so an
        // expression driving it has to reach all of them or the drawing would fade unevenly.
        var picture = Build("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs>
                <linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="100" y2="0">
                  <stop offset="0%" stop-color="#3366cc" />
                  <stop offset="100%" stop-color="#000000" />
                </linearGradient>
              </defs>
              <rect x="0" y="0" width="100" height="100" fill="url(#g)" fill-opacity="{{ fade }}" />
            </svg>
            """);

        var shader = EnumeratePaints(picture)
            .Select(p => p.Shader)
            .OfType<LinearGradientShader>()
            .Single();

        Assert.All(
            shader.Colors!,
            color => Assert.Equal(SymNode.Source("fade"), Assert.IsType<SymBinary>(color.Expression).Right));
    }

    [Fact]
    public void Full_Opacity_Leaves_The_Expression_Unwrapped()
    {
        var paint = SinglePaint("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <rect x="0" y="0" width="10" height="10" fill="{{ primary }}" fill-opacity="1" />
            </svg>
            """);

        Assert.Equal(SymNode.Source("primary"), paint.Color!.Value.Expression);
    }

    [Fact]
    public void LinearRgb_Color_Interpolation_Wraps_The_Expression()
    {
        // The concrete channels get converted, so the expression has to record the same step
        // or generated code would emit sRGB where the model holds linear.
        var picture = Build("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <rect x="0" y="0" width="10" height="10" fill="{{ primary }}"
                    color-interpolation="linearRGB" />
            </svg>
            """);

        var shader = EnumeratePaints(picture)
            .Select(p => p.Shader)
            .OfType<ColorShader>()
            .Single();

        var unary = Assert.IsType<SymUnary>(shader.Color.Expression);
        Assert.Equal(SymOp.ToLinearRgb, unary.Op);
        Assert.Equal(SymNode.Source("primary"), unary.Operand);
    }

    [Fact]
    public void Gradient_Stop_Color_Expression_Is_Read()
    {
        var picture = Build("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs>
                <linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="100" y2="0">
                  <stop offset="0%" stop-color="{{ start }}" />
                  <stop offset="100%" stop-color="#000000" />
                </linearGradient>
              </defs>
              <rect x="0" y="0" width="100" height="100" fill="url(#g)" />
            </svg>
            """);

        var shader = EnumeratePaints(picture)
            .Select(p => p.Shader)
            .OfType<LinearGradientShader>()
            .Single();

        Assert.NotNull(shader.Colors);
        Assert.Equal(SymNode.Source("start"), shader.Colors![0].Expression);
        Assert.Null(shader.Colors[1].Expression);
    }

    [Fact]
    public void A_Plain_Value_Is_Left_Alone()
    {
        var paint = SinglePaint("""
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect x="0" y="0" width="10" height="10" fill="#123456" />
            </svg>
            """);

        var color = paint.Color!.Value;

        Assert.Null(color.Expression);
        Assert.Equal(0x12, color.Red);
    }

    [Fact]
    public void Only_A_Fully_Braced_Value_Is_An_Expression()
    {
        // A stray brace in an otherwise ordinary value must not be mistaken for an expression.
        var paint = SinglePaint("""
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect x="0" y="0" width="10" height="10" fill="url(#nope) {{ primary }}" />
            </svg>
            """);

        Assert.Null(paint.Color!.Value.Expression);
    }

    [Fact]
    public void Blank_Expression_Is_Ignored()
    {
        var paint = SinglePaint("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <rect x="0" y="0" width="10" height="10" fill="{{     }}" />
            </svg>
            """);

        Assert.Null(paint.Color!.Value.Expression);
    }

    [Fact]
    public void Rendering_Is_Unaffected_By_The_Expression()
    {
        // The whole point of carrying a concrete value alongside the expression: every consumer
        // that ignores Expression must produce exactly what the plain SVG produced.
        const string plain = """
            <svg xmlns="http://www.w3.org/2000/svg" width="40" height="40">
              <rect x="5" y="5" width="30" height="30" fill="#808080" fill-opacity="0.5" />
            </svg>
            """;

        var symbolic = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="40" height="40">
              <rect x="5" y="5" width="30" height="30" fill="{{ primary }}" fill-opacity="0.5" />
            </svg>
            """;

        Assert.Equal(Render(plain), Render(symbolic));
    }

    private static byte[] Render(string svgMarkup)
    {
        using var svg = SKSvg.CreateFromSvg(svgMarkup);
        Assert.NotNull(svg.Picture);

        using var bitmap = new SkiaSharp.SKBitmap(40, 40);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.Transparent);
        canvas.DrawPicture(svg.Picture);
        canvas.Flush();

        return bitmap.Bytes;
    }

    [Fact]
    public void Same_Fallback_With_Different_Expressions_Produces_Distinct_Colors()
    {
        // Solid fills are cached by a key containing SKColor; equal fallbacks must not collapse.
        var paints = EnumeratePaints(Build("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <rect x="0" y="0" width="10" height="10" fill="{{ First(t) }}" />
              <rect x="20" y="0" width="10" height="10" fill="{{ Second(t) }}" />
            </svg>
            """)).ToList();

        Assert.Equal(2, paints.Count);
        Assert.Equal(SymNode.Source("First(t)"), paints[0].Color!.Value.Expression);
        Assert.Equal(SymNode.Source("Second(t)"), paints[1].Color!.Value.Expression);
        Assert.NotEqual(paints[0].Color!.Value, paints[1].Color!.Value);
    }
}
