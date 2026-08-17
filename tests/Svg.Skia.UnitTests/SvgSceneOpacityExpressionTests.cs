using System.Collections.Generic;
using System.Linq;
using ShimSkiaSharp;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Expressions;
using Svg.Expressions;
using Svg.Model.Services;
using Xunit;

namespace Svg.Skia.UnitTests;

public class SvgSceneOpacityExpressionTests
{
    private const string Ns = SvgCodeDeclarations.Namespace;

    private static SKPicture? Build(string svgMarkup)
    {
        var document = SvgService.FromSvg(svgMarkup);
        Assert.NotNull(document);
        var assetLoader = new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings()));
        return SvgSceneRuntime.CreateModel(document!, assetLoader);
    }

    private static string Generate(string svgMarkup)
    {
        var picture = Build(svgMarkup);
        Assert.NotNull(picture);

        return SkiaCSharpCodeGen.Generate(picture!, "Svg", "Generated", SvgCodeDeclarations.Parse(svgMarkup));
    }

    private static IEnumerable<SaveLayerCanvasCommand> SaveLayers(SKPicture? picture)
        => picture?.Commands?.OfType<SaveLayerCanvasCommand>() ?? Enumerable.Empty<SaveLayerCanvasCommand>();

    [Fact]
    public void Opacity_Expression_Produces_A_Layer_Paint_Carrying_It()
    {
        var picture = Build("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <g opacity="{{ a }}">
                <rect x="0" y="0" width="10" height="10" fill="#808080" />
              </g>
            </svg>
            """);

        var layer = Assert.Single(SaveLayers(picture), l => l.Paint?.Color?.Expression is { });
        var color = layer.Paint!.Color!.Value;

        // An inline expression supplies no design-time value, so the concrete channels come from
        // the placeholder the parser substituted: fully opaque white.
        Assert.Equal(255, color.Alpha);
        Assert.Equal(255, color.Red);

        var binary = Assert.IsType<SymBinary>(color.Expression);
        Assert.Equal(SymOp.ScaleAlpha, binary.Op);
        Assert.Equal(SymNode.Source("#ffffff"), binary.Left);
        Assert.Equal(SymNode.Source("a"), binary.Right);
    }

    [Fact]
    public void A_Fully_Opaque_Group_Still_Gets_A_Layer_When_An_Expression_Is_Present()
    {
        // Without an expression an opacity of 1 needs no layer at all, so the expression would
        // have nowhere to live.
        var picture = Build("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <g opacity="{{ a }}">
                <rect x="0" y="0" width="10" height="10" fill="#808080" />
              </g>
            </svg>
            """);

        var layer = Assert.Single(SaveLayers(picture), l => l.Paint?.Color?.Expression is { });

        Assert.Equal(255, layer.Paint!.Color!.Value.Alpha);
    }

    [Fact]
    public void A_Plain_Opaque_Group_Still_Gets_No_Layer_Paint()
    {
        var picture = Build("""
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <g opacity="1">
                <rect x="0" y="0" width="10" height="10" fill="#808080" />
              </g>
            </svg>
            """);

        Assert.DoesNotContain(SaveLayers(picture), l => l.Paint?.Color?.Expression is { });
    }

    [Fact]
    public void Same_Opacity_With_Different_Expressions_Does_Not_Share_A_Cached_Paint()
    {
        // Layer paints are cached by opacity value, which would otherwise collapse these two.
        var picture = Build("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <g opacity="{{ a }}">
                <rect x="0" y="0" width="10" height="10" fill="#808080" />
              </g>
              <g opacity="{{ b }}">
                <rect x="20" y="0" width="10" height="10" fill="#808080" />
              </g>
            </svg>
            """);

        var expressions = SaveLayers(picture)
            .Select(l => l.Paint?.Color?.Expression)
            .OfType<SymBinary>()
            .Select(b => b.Right)
            .ToList();

        Assert.Equal(2, expressions.Count);
        Assert.NotEqual(expressions[0], expressions[1]);
    }

    [Fact]
    public void Generated_Code_Scales_White_By_The_Opacity_Expression()
    {
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs>
                <e:code>
                  <e:param name="t" type="number" default="0" />
                  <e:let name="fade">clamp(t, 0, 1)</e:let>
                </e:code>
              </defs>
              <g opacity="{{ fade }}">
                <rect x="0" y="0" width="10" height="10" fill="#808080" />
              </g>
            </svg>
            """);

        Assert.Contains("SvgScaleAlpha(new SKColor(255, 255, 255, 255), fade)", code);
    }

    [Fact]
    public void An_Opacity_Expression_Must_Be_A_Number()
    {
        // The factor position is a number even though the surrounding paint value is a colour.
        var error = Assert.Throws<ExprException>(() => Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code><e:param name="tint" type="color" default="#ff0000" /></e:code></defs>
              <g opacity="{{ tint }}">
                <rect x="0" y="0" width="10" height="10" fill="#808080" />
              </g>
            </svg>
            """));

        Assert.Contains("must be a number expression", error.Message);
    }

    [Fact]
    public void Rendering_Uses_The_Placeholder_Not_The_Expression()
    {
        // This is the cost of the inline form: an expression carries no design-time value, so
        // renderers that ignore Expression show the placeholder. For opacity that is 1, chosen
        // so the element stays visible rather than vanishing.
        const string opaque = """
            <svg xmlns="http://www.w3.org/2000/svg" width="40" height="40">
              <g opacity="1">
                <rect x="5" y="5" width="30" height="30" fill="#808080" />
              </g>
            </svg>
            """;

        var symbolic = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="40" height="40">
              <g opacity="{{ a }}">
                <rect x="5" y="5" width="30" height="30" fill="#808080" />
              </g>
            </svg>
            """;

        Assert.Equal(Render(opaque), Render(symbolic));
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
}
