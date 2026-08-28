// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Linq;
using ShimSkiaSharp;
using Svg.Expressions;
using Svg.Model.Services;
using Svg.SceneGraph;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// The evaluator applied to a whole picture: which colours it reaches, and what it leaves alone.
/// </summary>
/// <remarks>
/// Compiled from real SVG rather than hand-built, because the point is to cover the paths by which
/// an expression actually reaches the model — a paint's colour, a colour shader, a gradient stop
/// array, the paint of an opacity layer — and those are decided by <c>SvgScenePaintingService</c>,
/// not by anything a test could reasonably assemble.
/// </remarks>
public class SvgSceneExpressionEvaluatorTests
{
    private const string Ns = "https://svg.skia/expr/1.0";

    private static SKPicture Build(string svgMarkup)
    {
        var document = SvgService.FromSvg(svgMarkup);
        Assert.NotNull(document);

        var assetLoader = new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings()));
        var picture = SvgSceneRuntime.CreateModel(document!, assetLoader);
        Assert.NotNull(picture);

        return picture!;
    }

    private static SKPicture Evaluate(SKPicture picture, string svgMarkup, params (string Name, ExprValue Value)[] values)
    {
        var declarations = SvgExpressionDeclarations.Parse(svgMarkup);
        var bound = new Dictionary<string, ExprValue>(StringComparer.Ordinal);

        foreach (var (name, value) in values)
        {
            bound[name] = value;
        }

        var result = SvgSceneExpressionEvaluator.Evaluate(picture, declarations, bound);
        Assert.NotNull(result);

        return result!;
    }

    private static SKPicture BuildAndEvaluate(string svgMarkup, params (string Name, ExprValue Value)[] values)
        => Evaluate(Build(svgMarkup), svgMarkup, values);

    private static IEnumerable<SKPaint> Paints(SKPicture picture)
        => picture.Commands?.OfType<DrawPathCanvasCommand>().Select(c => c.Paint).OfType<SKPaint>()
           ?? Enumerable.Empty<SKPaint>();

    private static SKPaint SinglePaint(SKPicture picture) => Assert.Single(Paints(picture));

    private static string Wrap(string code, string body)
        => $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code>{code}</e:code></defs>
              {body}
            </svg>
            """;

    [Fact]
    public void A_Fill_Expression_Becomes_The_Value_And_Loses_Its_Expression()
    {
        var markup = Wrap(
            """<e:param name="tint" type="color" />""",
            """<rect x="0" y="0" width="24" height="24" fill="{{ tint }}" />""");

        var evaluated = BuildAndEvaluate(markup, ("tint", ExprValue.Color(255, 0, 0, 255)));
        var color = SinglePaint(evaluated).Color;

        Assert.Equal(new SKColor(255, 0, 0, 255), color);

        // The expression is gone: it has been resolved, and a renderer that still saw one would have
        // no reason to trust the channels beside it.
        Assert.Null(color!.Value.Expression);
    }

    [Fact]
    public void A_Stroke_Expression_Is_Resolved_Independently_Of_The_Fill()
    {
        var markup = Wrap(
            """<e:param name="line" type="color" />""",
            """<rect x="2" y="2" width="20" height="20" fill="#808080" stroke="{{ line }}" stroke-width="2" />""");

        var evaluated = BuildAndEvaluate(markup, ("line", ExprValue.Color(0, 255, 0, 255)));

        var colors = Paints(evaluated).Select(p => p.Color).ToList();

        Assert.Contains(new SKColor(0, 255, 0, 255), colors);
        Assert.Contains(new SKColor(128, 128, 128, 255), colors);
    }

    [Fact]
    public void Fill_Opacity_Folded_Into_The_Expression_Scales_The_Evaluated_Alpha()
    {
        // The scene records fill-opacity as ScaleAlpha around the authored expression, so the fold
        // applies to whatever alpha the expression produces rather than to the placeholder.
        var markup = Wrap(
            """<e:param name="tint" type="color" />""",
            """<rect x="0" y="0" width="24" height="24" fill="{{ tint }}" fill-opacity="0.5" />""");

        var evaluated = BuildAndEvaluate(markup, ("tint", ExprValue.Color(255, 0, 0, 255)));

        Assert.Equal(new SKColor(255, 0, 0, 128), SinglePaint(evaluated).Color);
    }

    [Fact]
    public void A_Linear_Rgb_Element_Converts_The_Evaluated_Colour()
    {
        // color-interpolation puts the colour on a ColorShader and wraps the expression in
        // ToLinearRgb, so the conversion has to happen after the value is known, not before.
        var markup = Wrap(
            """<e:param name="tint" type="color" />""",
            """<rect x="0" y="0" width="24" height="24" fill="{{ tint }}" style="color-interpolation:linearRGB" />""");

        var evaluated = BuildAndEvaluate(markup, ("tint", ExprValue.Color(255, 0, 0, 255)));
        var shader = Assert.IsType<ColorShader>(SinglePaint(evaluated).Shader);

        // 255 in sRGB is 255 in linear; the mid channels are what move, so check a non-extreme one.
        Assert.Equal(255, shader.Color.Red);
        Assert.Null(shader.Color.Expression);

        var mid = BuildAndEvaluate(markup, ("tint", ExprValue.Color(128, 128, 128, 255)));
        var midShader = Assert.IsType<ColorShader>(SinglePaint(mid).Shader);

        Assert.Equal(ExprColor.ToLinearRgb(ExprValue.Color(128, 128, 128, 255)).Red, midShader.Color.Red);
        Assert.NotEqual(128, midShader.Color.Red);
    }

    [Fact]
    public void A_Gradient_Stop_Expression_Is_Resolved_And_The_Other_Stops_Are_Left_Alone()
    {
        // Not interpolated: a raw interpolated string would read the expression's own braces as an
        // interpolation hole, so the namespace is written out instead of coming from Ns.
        const string markup = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <e:code><e:param name="tint" type="color" /></e:code>
                <linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="24" y2="0">
                  <stop offset="0%" stop-color="{{ tint }}" />
                  <stop offset="100%" stop-color="#1e40af" />
                </linearGradient>
              </defs>
              <rect x="0" y="0" width="24" height="24" fill="url(#g)" />
            </svg>
            """;

        var evaluated = BuildAndEvaluate(markup, ("tint", ExprValue.Color(255, 0, 0, 255)));
        var gradient = Assert.IsType<LinearGradientShader>(SinglePaint(evaluated).Shader);

        Assert.Equal(1f, gradient.Colors![0].Red);
        Assert.Equal(0f, gradient.Colors[0].Green);
        Assert.Null(gradient.Colors[0].Expression);

        // The second stop was never symbolic and must be untouched.
        Assert.Null(gradient.Colors[1].Expression);
        Assert.Equal(0x1e / 255f, gradient.Colors[1].Red, 3);
    }

    [Fact]
    public void A_Fill_Opacity_Expression_Scales_The_Colour_It_Was_Written_Beside()
    {
        var markup = Wrap(
            """<e:param name="fade" type="number" />""",
            """<rect x="0" y="0" width="24" height="24" fill="#3366cc" fill-opacity="{{ fade }}" />""");

        var evaluated = BuildAndEvaluate(markup, ("fade", ExprValue.Number(0.5f)));
        var color = SinglePaint(evaluated).Color;

        // The channels are the ones the author wrote; only the alpha moved.
        Assert.Equal(new SKColor(0x33, 0x66, 0xcc, 128), color);
        Assert.Null(color!.Value.Expression);
    }

    [Fact]
    public void A_Stop_Opacity_Expression_Scales_That_Stop_Only()
    {
        const string markup = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <e:code><e:param name="fade" type="number" /></e:code>
                <linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="24" y2="0">
                  <stop offset="0%" stop-color="#3366cc" stop-opacity="{{ fade }}" />
                  <stop offset="100%" stop-color="#1e40af" />
                </linearGradient>
              </defs>
              <rect x="0" y="0" width="24" height="24" fill="url(#g)" />
            </svg>
            """;

        var evaluated = BuildAndEvaluate(markup, ("fade", ExprValue.Number(0.5f)));
        var gradient = Assert.IsType<LinearGradientShader>(SinglePaint(evaluated).Shader);

        Assert.Equal(128f / 255f, gradient.Colors![0].Alpha, 3);
        Assert.Equal(0x33 / 255f, gradient.Colors[0].Red, 3);
        Assert.Null(gradient.Colors[0].Expression);

        Assert.Equal(1f, gradient.Colors[1].Alpha);
        Assert.Null(gradient.Colors[1].Expression);
    }

    [Fact]
    public void An_Opacity_Expression_Scales_The_Layer_Paint()
    {
        // Recorded as ScaleAlpha(Source("#ffffff"), <authored>), which is a colour literal written in
        // the authored language inside a node the model built. The walk cannot shortcut that.
        var markup = Wrap(
            """<e:param name="fade" type="number" />""",
            """
            <g opacity="{{ fade }}">
                <rect x="0" y="0" width="24" height="24" fill="#808080" />
              </g>
            """);

        var evaluated = BuildAndEvaluate(markup, ("fade", ExprValue.Number(0.25f)));

        var layer = Assert.Single(evaluated.Commands!.OfType<SaveLayerCanvasCommand>());
        var color = layer.Paint!.Color!.Value;

        Assert.Equal(new SKColor(255, 255, 255, 64), color);
        Assert.Null(color.Expression);
    }

    [Fact]
    public void Two_Elements_Sharing_One_Cached_Paint_Share_One_Evaluated_Paint()
    {
        // Equal values mean one cached SKPaint instance for both elements, and the rewrite has to
        // preserve that rather than producing a paint per drawing command.
        var markup = Wrap(
            """<e:param name="tint" type="color" />""",
            """
            <rect x="0" y="0" width="10" height="10" fill="{{ tint }}" />
              <rect x="12" y="12" width="10" height="10" fill="{{ tint }}" />
            """);

        var picture = Build(markup);
        var before = Paints(picture).ToList();
        Assert.Equal(2, before.Count);
        Assert.Same(before[0], before[1]);

        var after = Paints(Evaluate(picture, markup, ("tint", ExprValue.Color(255, 0, 0, 255)))).ToList();

        Assert.Equal(2, after.Count);
        Assert.Same(after[0], after[1]);
        Assert.NotSame(before[0], after[0]);
    }

    [Fact]
    public void A_Paint_Without_An_Expression_Comes_Back_As_The_Same_Instance()
    {
        // Structural sharing, so the parts of a drawing that carry no expressions are not copied on
        // every parameter change.
        var markup = Wrap(
            """<e:param name="tint" type="color" />""",
            """
            <rect x="0" y="0" width="10" height="10" fill="{{ tint }}" />
              <rect x="12" y="12" width="10" height="10" fill="#1e40af" />
            """);

        var picture = Build(markup);
        var plain = Paints(picture).Single(p => p.Color!.Value.Expression is null);

        var evaluated = Evaluate(picture, markup, ("tint", ExprValue.Color(255, 0, 0, 255)));
        var stillPlain = Paints(evaluated).Single(p => p.Color!.Value.Red == 0x1e);

        Assert.Same(plain, stillPlain);
    }

    [Fact]
    public void A_Document_With_No_Expressions_Comes_Back_As_The_Same_Picture()
    {
        var picture = Build("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect x="0" y="0" width="24" height="24" fill="#1e40af" />
            </svg>
            """);

        var evaluated = SvgSceneExpressionEvaluator.Evaluate(picture, SvgExpressionDeclarations.Empty);

        Assert.Same(picture, evaluated);
    }

    [Fact]
    public void The_Symbolic_Picture_Is_Not_Mutated()
    {
        // The model has to survive intact for the next set of values, and its paints are shared, so
        // rewriting in place would corrupt both.
        var markup = Wrap(
            """<e:param name="tint" type="color" />""",
            """<rect x="0" y="0" width="24" height="24" fill="{{ tint }}" />""");

        var picture = Build(markup);
        var original = SinglePaint(picture);
        var originalColor = original.Color;

        Evaluate(picture, markup, ("tint", ExprValue.Color(255, 0, 0, 255)));

        Assert.Same(original, SinglePaint(picture));
        Assert.Equal(originalColor, original.Color);
        Assert.NotNull(original.Color!.Value.Expression);
    }

    [Fact]
    public void Evaluating_Twice_With_Different_Values_Gives_Different_Results()
    {
        // What a viewer does on every change: the same symbolic picture, new values, no recompile.
        var markup = Wrap(
            """<e:param name="tint" type="color" />""",
            """<rect x="0" y="0" width="24" height="24" fill="{{ tint }}" />""");

        var picture = Build(markup);

        var red = Evaluate(picture, markup, ("tint", ExprValue.Color(255, 0, 0, 255)));
        var blue = Evaluate(picture, markup, ("tint", ExprValue.Color(0, 0, 255, 255)));

        Assert.Equal(new SKColor(255, 0, 0, 255), SinglePaint(red).Color);
        Assert.Equal(new SKColor(0, 0, 255, 255), SinglePaint(blue).Color);
    }

    [Fact]
    public void Source_Element_Metadata_Survives_A_Rewritten_Command()
    {
        // `with` copies a record's own members, not the metadata on CanvasCommand, and that metadata
        // is how hit testing and the editor find the element a command came from.
        var markup = Wrap(
            """<e:param name="tint" type="color" />""",
            """<rect id="target" x="0" y="0" width="24" height="24" fill="{{ tint }}" />""");

        var picture = Build(markup);
        var before = Assert.Single(picture.Commands!.OfType<DrawPathCanvasCommand>());
        Assert.Equal("target", before.SourceElementId);

        var evaluated = Evaluate(picture, markup, ("tint", ExprValue.Color(255, 0, 0, 255)));
        var after = Assert.Single(evaluated.Commands!.OfType<DrawPathCanvasCommand>());

        Assert.NotSame(before, after);
        Assert.Equal("target", after.SourceElementId);
        Assert.Equal(before.SourceElementTypeName, after.SourceElementTypeName);
    }

    [Fact]
    public void An_Expression_Inside_A_Pattern_Is_Reached_Through_The_Nested_Picture()
    {
        // A pattern paints through a PictureShader whose Src is a picture of its own, so the walk has
        // to recurse rather than stopping at the top-level command list.
        const string markup = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <e:code><e:param name="tint" type="color" /></e:code>
                <pattern id="p" width="8" height="8" patternUnits="userSpaceOnUse">
                  <rect x="0" y="0" width="4" height="4" fill="{{ tint }}" />
                </pattern>
              </defs>
              <rect x="0" y="0" width="24" height="24" fill="url(#p)" />
            </svg>
            """;

        var picture = Build(markup);
        var shader = Assert.IsType<PictureShader>(SinglePaint(picture).Shader);
        Assert.NotNull(shader.Src);

        var evaluated = BuildAndEvaluate(markup, ("tint", ExprValue.Color(255, 0, 0, 255)));
        var evaluatedShader = Assert.IsType<PictureShader>(SinglePaint(evaluated).Shader);

        var inner = Assert.Single(Paints(evaluatedShader.Src!));

        Assert.Equal(new SKColor(255, 0, 0, 255), inner.Color);
        Assert.Null(inner.Color!.Value.Expression);
    }

    [Fact]
    public void A_Colour_Filter_Carrying_An_Expression_Is_Resolved()
    {
        // No SVG produces this today — BlendModeColorFilter takes an SKColor but is never handed one
        // with an expression. Walked anyway, and pinned here, because the alternative is that
        // attaching one later renders the placeholder with nothing to say so.
        var symbols = new Dictionary<string, ExprType>(StringComparer.Ordinal) { ["tint"] = ExprType.Color };
        var values = new Dictionary<string, ExprValue>(StringComparer.Ordinal)
        {
            ["tint"] = ExprValue.Color(255, 0, 0, 255)
        };

        var paint = new SKPaint
        {
            ColorFilter = SKColorFilter.CreateBlendMode(
                new SKColor(128, 128, 128, 255).WithExpression(SymNode.Source("tint")),
                SKBlendMode.SrcIn)
        };

        var picture = new SKPicture(
            SKRect.Create(0, 0, 24, 24),
            new List<CanvasCommand> { new DrawPathCanvasCommand(new SKPath(), paint) });

        var evaluated = SvgSceneExpressionEvaluator.Evaluate(picture, new ExprEvaluator(symbols, values));
        var filter = Assert.IsType<BlendModeColorFilter>(SinglePaint(evaluated!).ColorFilter);

        Assert.Equal(new SKColor(255, 0, 0, 255), filter.Color);
        Assert.Null(filter.Color.Expression);
    }

    [Fact]
    public void A_Paint_Expression_Of_The_Wrong_Type_Reports_Which_Position_It_Was_In()
    {
        var markup = Wrap(
            """<e:param name="fade" type="number" />""",
            """<rect x="0" y="0" width="24" height="24" fill="{{ fade }}" />""");

        var picture = Build(markup);

        var error = Assert.Throws<ExprException>(
            () => Evaluate(picture, markup, ("fade", ExprValue.Number(1f))));

        // The same wording the emitter uses, so a bad document reads the same either way.
        Assert.Contains("A paint expression must be a colour expression", error.Message);
    }

    [Fact]
    public void A_Visibility_Expression_Of_The_Wrong_Type_Is_Not_Called_An_Opacity()
    {
        // It was. The label was a ternary on whether the type wanted was a colour, so everything
        // that was not a paint was an opacity -- and visibility, the one attribute that is a
        // condition rather than a value, was told it was one.
        var markup = Wrap(
            """<e:param name="fade" type="number" />""",
            """<rect x="0" y="0" width="24" height="24" fill="#3fb5b5" visibility="{{ fade }}" />""");

        var picture = Build(markup);

        var error = Assert.Throws<ExprException>(
            () => Evaluate(picture, markup, ("fade", ExprValue.Number(1f))));

        Assert.Contains("A visibility expression must be a boolean expression", error.Message);
    }
}
