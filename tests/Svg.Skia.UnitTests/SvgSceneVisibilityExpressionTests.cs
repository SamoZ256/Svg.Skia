using System.Collections.Generic;
using System.Linq;
using ShimSkiaSharp;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Expressions;
using Svg.Model.Services;
using Xunit;

namespace Svg.Skia.UnitTests;

public class SvgSceneVisibilityExpressionTests
{
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

    private static IReadOnlyList<CanvasCommand> Commands(SKPicture? picture)
        => picture?.Commands as IReadOnlyList<CanvasCommand> ?? new List<CanvasCommand>();

    [Fact]
    public void A_Visibility_Expression_Brackets_The_Elements_Commands()
    {
        var commands = Commands(Build("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <rect x="0" y="0" width="10" height="10" fill="#808080" visibility="{{ shown }}" />
            </svg>
            """));

        var begin = commands.OfType<BeginConditionalCanvasCommand>().Single();
        Assert.Equal(SymNode.Source("shown"), begin.Condition);

        // The draw has to sit between the markers, not beside them.
        var beginAt = commands.ToList().FindIndex(c => c is BeginConditionalCanvasCommand);
        var endAt = commands.ToList().FindIndex(c => c is EndConditionalCanvasCommand);
        var drawAt = commands.ToList().FindIndex(c => c is DrawPathCanvasCommand);

        Assert.True(beginAt >= 0 && endAt > beginAt);
        Assert.InRange(drawAt, beginAt + 1, endAt - 1);
    }

    [Fact]
    public void Markers_Are_Balanced()
    {
        var commands = Commands(Build("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <g visibility="{{ shown }}">
                <rect x="0" y="0" width="10" height="10" fill="#808080" />
                <rect x="20" y="0" width="10" height="10" fill="#404040" visibility="{{ other }}" />
              </g>
            </svg>
            """));

        var depth = 0;
        var lowest = 0;

        foreach (var command in commands)
        {
            if (command is BeginConditionalCanvasCommand)
            {
                depth++;
            }
            else if (command is EndConditionalCanvasCommand)
            {
                depth--;
                lowest = System.Math.Min(lowest, depth);
            }
        }

        Assert.Equal(0, depth);
        Assert.Equal(0, lowest);
        Assert.Equal(2, commands.OfType<BeginConditionalCanvasCommand>().Count());
    }

    [Fact]
    public void A_Plain_Document_Gets_No_Markers()
    {
        var commands = Commands(Build("""
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect x="0" y="0" width="10" height="10" fill="#808080" visibility="visible" />
            </svg>
            """));

        Assert.DoesNotContain(commands, c => c is BeginConditionalCanvasCommand);
    }

    [Fact]
    public void The_Placeholder_Keeps_The_Element_Rendered()
    {
        // A hidden element contributes no commands at all, so the placeholder must be visible
        // or there would be nothing to make conditional.
        var commands = Commands(Build("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <rect x="0" y="0" width="10" height="10" fill="#808080" visibility="{{ shown }}" />
            </svg>
            """));

        Assert.Single(commands.OfType<DrawPathCanvasCommand>());
    }

    [Fact]
    public void Generated_Code_Wraps_The_Draws_In_An_If()
    {
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs>
                <e:code>
                  <e:param name="t" type="number" default="0" />
                  <e:let name="shown">t &gt; 0.5</e:let>
                </e:code>
              </defs>
              <rect x="0" y="0" width="10" height="10" fill="#808080" visibility="{{ shown }}" />
            </svg>
            """);

        Assert.Contains("if (shown)", code);

        var ifAt = code.IndexOf("if (shown)", System.StringComparison.Ordinal);
        var drawAt = code.IndexOf(".DrawPath(", System.StringComparison.Ordinal);

        Assert.True(ifAt >= 0 && drawAt > ifAt, "The draw must be emitted inside the conditional.");
    }

    [Fact]
    public void Generated_Braces_Are_Balanced()
    {
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs>
                <e:code>
                  <e:param name="t" type="number" default="0" />
                  <e:let name="shown">t &gt; 0.5</e:let>
                </e:code>
              </defs>
              <g visibility="{{ shown }}">
                <rect x="0" y="0" width="10" height="10" fill="#808080" />
              </g>
            </svg>
            """);

        Assert.Equal(
            code.Count(c => c == '{'),
            code.Count(c => c == '}'));
    }

    [Fact]
    public void A_Visibility_Expression_Must_Be_A_Boolean()
    {
        var error = Assert.Throws<ExprException>(() => Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs>
                <e:code>
                  <e:param name="t" type="number" default="0" />
                  <e:let name="shown">t &gt; 0.5</e:let>
                </e:code>
              </defs>
              <rect x="0" y="0" width="10" height="10" fill="#808080" visibility="{{ t }}" />
            </svg>
            """));

        Assert.Contains("boolean", error.Message);
    }

    [Fact]
    public void Rendering_Ignores_The_Markers()
    {
        // Renderers that do not understand conditionals execute the range as written, which is
        // the placeholder state, so the drawing is unchanged.
        const string plain = """
            <svg xmlns="http://www.w3.org/2000/svg" width="40" height="40">
              <rect x="5" y="5" width="30" height="30" fill="#808080" />
            </svg>
            """;

        const string conditional = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="40" height="40">
              <rect x="5" y="5" width="30" height="30" fill="#808080" visibility="{{ shown }}" />
            </svg>
            """;

        Assert.Equal(Render(plain), Render(conditional));
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
