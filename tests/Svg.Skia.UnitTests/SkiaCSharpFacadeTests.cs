using System;
using Svg.CodeGen.Skia;
using Svg.Expressions;
using Svg.Model.Services;
using Xunit;

namespace Svg.Skia.UnitTests;

public class SkiaCSharpFacadeTests
{
    private static SkiaCSharpDrawing Drawing(
        string namespaceName,
        string className,
        string svgMarkup,
        ShimSkiaSharp.SKRect? box = null,
        string? method = null,
        string[]? parameters = null)
    {
        var document = SvgService.FromSvg(svgMarkup);
        Assert.NotNull(document);
        var picture = SvgSceneRuntime.CreateModel(document!, new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings())));
        Assert.NotNull(picture);

        return new SkiaCSharpDrawing(
            picture!,
            namespaceName,
            className,
            SvgExpressionDeclarations.Parse(svgMarkup),
            bounds: box is { } rect ? new[] { ("LevelRect", rect) } : null)
        {
            Method = method,
            Parameters = parameters
        };
    }

    private const string Tank = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="30" height="30">
          <defs><e:code>
            <e:param name="level" type="number" default="1" />
            <e:param name="state" type="boolean" default="true" />
            <e:param name="whiteColor_" type="color" default="#fff" />
            <e:param name="accentColorOn" type="color" default="#f00" />
          </e:code></defs>
          <rect x="0" y="0" width="30" height="{{ level * 30 }}" fill="{{ state ? accentColorOn : whiteColor_ }}" />
        </svg>
        """;

    private const string Label = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="30" height="30">
          <defs><e:code><e:param name="accentColorOn" type="color" default="#f00" /></e:code></defs>
          <text x="2" y="20" font-size="10" fill="{{ accentColorOn }}">7</text>
        </svg>
        """;

    private static readonly SvgCodeStyle s_paintCode = new()
    {
        Api = "PaintCodeResources.VectorIconsResource",
        Resizing = "PaintCode.ResizingBehavior",
        Context = "PaintCode.Context",
        Order = new[] { "accentColorOn", "whiteColor_", "state", "level" }
    };

    [Fact]
    public void No_Api_Emits_What_The_Generator_Always_Has()
    {
        var drawings = new[] { Drawing("Icons", "Tank", Tank) };

        Assert.Equal(SkiaCSharpCodeGen.GenerateFile(drawings), SkiaCSharpCodeGen.GenerateFile(drawings, style: new SvgCodeStyle()));
        Assert.DoesNotContain("partial class", SkiaCSharpCodeGen.GenerateFile(drawings));
    }

    [Fact]
    public void The_Api_Takes_Every_Argument_Positionally_In_The_Given_Order()
    {
        var code = SkiaCSharpCodeGen.GenerateFile(new[] { Drawing("Icons", "Tank", Tank, method: "drawTanklevel") }, style: s_paintCode);

        Assert.Contains("namespace PaintCodeResources", code);
        Assert.Contains("public static partial class VectorIconsResource", code);
        Assert.Contains(
            "public static void drawTanklevel(SKCanvas canvas, SKColor accentColorOn, SKColor whiteColor_, bool state, float level)",
            code);
        Assert.Contains(
            "global::Icons.Tank.Draw(canvas, level: level, state: state, whiteColor_: whiteColor_, accentColorOn: accentColorOn);",
            code);
    }

    [Fact]
    public void The_Framed_Overload_Fits_The_Drawing_Into_The_Target()
    {
        var code = SkiaCSharpCodeGen.GenerateFile(new[] { Drawing("Icons", "Tank", Tank) }, style: s_paintCode);

        Assert.Contains(
            "public static void drawTank(SKCanvas canvas, SKRect targetFrame, global::PaintCode.ResizingBehavior resizing, SKColor accentColorOn, SKColor whiteColor_, bool state, float level)",
            code);
        Assert.Contains("var resizedFrame = SvgResize(resizing, new SKRect(0f, 0f, 30f, 30f), targetFrame);", code);
        Assert.Contains("canvas.Scale(resizedFrame.Width / 30f, resizedFrame.Height / 30f);", code);
        Assert.Contains("private static SKRect SvgResize(global::PaintCode.ResizingBehavior behavior, SKRect rect, SKRect target)", code);
    }

    [Fact]
    public void Without_A_Resizing_Type_There_Is_No_Framed_Overload()
    {
        var code = SkiaCSharpCodeGen.GenerateFile(new[] { Drawing("Icons", "Tank", Tank) }, style: new SvgCodeStyle { Api = "Api" });

        Assert.DoesNotContain("targetFrame", code);
        Assert.DoesNotContain("SvgResize", code);
        Assert.Contains("public static partial class Api", code);
    }

    [Fact]
    public void A_Box_Comes_Back_As_An_Out_Parameter()
    {
        var code = SkiaCSharpCodeGen.GenerateFile(
            new[] { Drawing("Icons", "Tank", Tank, new ShimSkiaSharp.SKRect(2, 6, 28, 24)) },
            style: s_paintCode);

        Assert.Contains("bool state, float level, out SKRect @levelRect)", code);
        Assert.Contains("@levelRect = global::Icons.Tank.@LevelRect;", code);
    }

    [Fact]
    public void Only_A_Drawing_With_Text_Takes_The_Context()
    {
        var code = SkiaCSharpCodeGen.GenerateFile(
            new[] { Drawing("Icons", "Label", Label), Drawing("Icons", "Tank", Tank) },
            style: s_paintCode);

        Assert.Contains("public static void drawLabel(SKCanvas canvas, global::PaintCode.Context context, SKColor accentColorOn)", code);
        Assert.Contains("public static void drawTank(SKCanvas canvas, SKColor accentColorOn,", code);
    }

    [Fact]
    public void Listed_Parameters_Replace_The_Order_And_Take_Types_From_Other_Drawings()
    {
        var code = SkiaCSharpCodeGen.GenerateFile(
            new[]
            {
                Drawing("Icons", "Label", Label, parameters: new[] { "accentColorOn", "level", "state" }),
                Drawing("Icons", "Tank", Tank)
            },
            style: new SvgCodeStyle { Api = "Api" });

        // Accepted for the caller's sake, and nothing in the drawing reads them.
        Assert.Contains("public static void drawLabel(SKCanvas canvas, SKColor accentColorOn, float level, bool state)", code);
        Assert.Contains("global::Icons.Label.Draw(canvas, accentColorOn: accentColorOn);", code);
    }

    [Fact]
    public void A_Listed_Parameter_Nothing_Declares_Is_Refused()
    {
        var thrown = Assert.Throws<ExprException>(() => SkiaCSharpCodeGen.GenerateFile(
            new[] { Drawing("Icons", "Label", Label, parameters: new[] { "accentColorOn", "angle" }) },
            style: new SvgCodeStyle { Api = "Api" }));

        Assert.Contains("'angle'", thrown.Message);
    }

    [Fact]
    public void A_Namespace_That_Would_Hide_The_Api_Is_Refused()
    {
        var thrown = Assert.Throws<ExprException>(() => SkiaCSharpCodeGen.GenerateFile(
            new[] { Drawing("VectorIconsResource.Analog", "Tank", Tank) },
            style: s_paintCode));

        Assert.Contains("would hide the API class", thrown.Message);
    }

    [Fact]
    public void Two_Drawings_Drawn_By_The_Same_Overload_Are_Refused()
    {
        var thrown = Assert.Throws<ExprException>(() => SkiaCSharpCodeGen.GenerateFile(
            new[]
            {
                Drawing("Icons", "Tank", Tank, method: "drawTank"),
                Drawing("Glyphs", "Tank", Tank)
            },
            style: s_paintCode));

        Assert.Contains("drawTank(", thrown.Message);
    }

    [Fact]
    public void Leaving_Out_A_Parameter_Draw_Requires_Is_Refused()
    {
        // Declared out of default order, so the drawing's Draw requires every argument.
        var required = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="30" height="30">
              <defs><e:code>
                <e:param name="level" type="number" />
                <e:param name="state" type="boolean" default="true" />
              </e:code></defs>
              <rect width="30" height="{{ state ? level : 0 }}" />
            </svg>
            """;

        var thrown = Assert.Throws<ExprException>(() => SkiaCSharpCodeGen.GenerateFile(
            new[] { Drawing("Icons", "Tank", required, parameters: new[] { "state" }) },
            style: new SvgCodeStyle { Api = "Api" }));

        Assert.Contains("'level'", thrown.Message);
    }
}
