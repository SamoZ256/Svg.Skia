// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using Svg.Model;
using Svg.Model.Services;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// Where a driven transform or stroke width is refused, and — just as important — where it is not.
/// </summary>
/// <remarks>
/// A refusal drawn too widely takes the feature down with it, and one drawn too narrowly draws the
/// wrong picture, so both sides are pinned here. The sound cases are the surprising half: a clip
/// path and a userSpaceOnUse gradient both look like they were measured against the matrix and
/// neither was.
/// </remarks>
public class SvgSceneTransformAuditTests
{
    private const string Ns = "https://svg.skia/expr/1.0";

    private static string? Audit(string body)
    {
        var markup =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:e=\"" + Ns + "\" viewBox=\"0 0 64 64\" width=\"64\" height=\"64\">" +
            "<defs><e:code><e:param name=\"a\" type=\"number\" default=\"0\" /></e:code>" +
            "<filter id=\"f\"><feGaussianBlur stdDeviation=\"1\" /></filter>" +
            "<clipPath id=\"c\"><rect x=\"0\" y=\"0\" width=\"32\" height=\"32\" /></clipPath>" +
            "<linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"64\" y2=\"0\">" +
            "<stop offset=\"0%\" stop-color=\"#ff0000\" /><stop offset=\"100%\" stop-color=\"#0000ff\" /></linearGradient>" +
            "</defs>" + body + "</svg>";

        var document = SvgService.FromSvg(markup);
        Assert.NotNull(document);

        var assetLoader = new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings()));
        Assert.True(SvgSceneRuntime.TryCompile(document!, assetLoader, DrawAttributes.None, out var scene));

        return SvgSceneTransformAudit.WhyUnsound(scene);
    }

    private const string Driven = "transform=\"rotate({{ a }} 32 32)\"";

    private const string Stroked = "stroke=\"#000000\" stroke-width=\"{{ a }}\"";

    [Fact]
    public void A_Driven_Stroke_Width_On_Its_Own_Is_Allowed()
    {
        Assert.Null(Audit("<rect width=\"8\" height=\"8\" " + Stroked + " />"));
    }

    [Fact]
    public void A_Driven_Stroke_Width_Inside_A_Layer_Is_Refused()
    {
        // The layer's bounds were unioned from where the rect painted, stroke included, and
        // SaveLayer clips to them — so a wider stroke would be cut at the edge it was compiled with.
        Assert.Contains(
            "the layer opened by",
            Audit("<g opacity=\"0.5\"><rect width=\"8\" height=\"8\" " + Stroked + " /></g>"));
    }

    [Fact]
    public void A_Driven_Stroke_Width_Under_A_Filter_Is_Refused()
    {
        Assert.Contains(
            "a filter on",
            Audit("<g filter=\"url(#f)\"><rect width=\"8\" height=\"8\" " + Stroked + " /></g>"));
    }

    [Fact]
    public void A_Driven_Stroke_Width_Inside_A_Clip_Path_Is_Allowed()
    {
        // Unlike a transform: a clip travels with the element rather than staying where it was, and
        // a stroke is a value on the recorded paint wherever it was compiled.
        Assert.Null(Audit("<g clip-path=\"url(#c)\"><rect width=\"8\" height=\"8\" " + Stroked + " /></g>"));
    }

    [Fact]
    public void A_Drawing_That_Drives_Nothing_Is_Not_Audited()
    {
        Assert.Null(Audit("<rect width=\"8\" height=\"8\" transform=\"rotate(30)\" filter=\"url(#f)\" />"));
    }

    [Fact]
    public void A_Driven_Transform_On_Its_Own_Is_Allowed()
    {
        Assert.Null(Audit("<rect width=\"8\" height=\"8\" " + Driven + " />"));
    }

    /// <summary>
    /// A driven transform written in defs and drawn through a <c>&lt;use&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The bound against which the rule above is drawn: it refuses an element that reached no node,
    /// and a definition reaches one through whatever uses it.
    /// </remarks>
    [Fact]
    public void A_Used_Definition_Is_Allowed()
    {
        Assert.Null(Audit(
            "<defs><g id=\"u\" " + Driven + "><rect width=\"8\" height=\"8\" /></g></defs>" +
            "<use href=\"#u\" />"));
    }

    [Fact]
    public void A_Filter_On_The_Element_Is_Refused()
    {
        Assert.Contains("filter", Audit("<rect width=\"8\" height=\"8\" " + Driven + " filter=\"url(#f)\" />")!);
    }

    [Fact]
    public void A_Filter_Above_It_Is_Refused()
    {
        Assert.Contains("filter", Audit("<g filter=\"url(#f)\"><rect width=\"8\" height=\"8\" " + Driven + " /></g>")!);
    }

    [Fact]
    public void A_Filter_Below_It_Is_Refused()
    {
        Assert.Contains("filter", Audit("<g " + Driven + "><rect width=\"8\" height=\"8\" filter=\"url(#f)\" /></g>")!);
    }

    /// <summary>SaveLayer clips to bounds unioned from where the children were compiled.</summary>
    [Fact]
    public void A_Layer_Opened_Above_It_Is_Refused()
    {
        // Named, not merely refused: every other rule here would also answer non-null.
        Assert.Contains("layer", Audit("<g opacity=\"0.5\"><rect width=\"8\" height=\"8\" " + Driven + " /></g>")!);
    }

    /// <summary>
    /// The origin wraps the author's functions in translations no list of them can describe.
    /// </summary>
    /// <remarks>
    /// ApplyTransformOrigin short-circuits on an identity transform, which is what a single driven
    /// function stands in as, so this was accepted and then drawn about the wrong point.
    /// </remarks>
    [Fact]
    public void A_Transform_Origin_On_The_Same_Element_Is_Refused()
    {
        Assert.NotNull(Audit(
            "<rect width=\"8\" height=\"8\" transform-origin=\"32 32\" " + Driven + " />"));
    }

    /// <summary>A clip records the functions behind its own transform beside the matrix they baked to.</summary>
    [Fact]
    public void A_Transform_On_The_ClipPath_Itself_Is_Allowed()
    {
        Assert.Null(Audit(
            "<defs><clipPath id=\"e\" " + Driven + "><rect width=\"32\" height=\"32\" /></clipPath></defs>" +
            "<rect width=\"8\" height=\"8\" clip-path=\"url(#e)\" />"));
    }

    [Fact]
    public void A_Transform_Written_Inside_A_ClipPath_Is_Allowed()
    {
        Assert.Null(Audit(
            "<defs><clipPath id=\"d\"><rect width=\"32\" height=\"32\" transform=\"rotate({{ a }})\" /></clipPath></defs>" +
            "<rect width=\"8\" height=\"8\" clip-path=\"url(#d)\" />"));
    }

    private static string Clipped(string parts, string defs = "")
        => "<defs>" + defs + "<clipPath id=\"e\">" + parts + "</clipPath></defs>" +
           "<rect width=\"8\" height=\"8\" clip-path=\"url(#e)\" />";

    [Theory]
    [InlineData("<path d=\"M 16 16 L 16 0 A 16 16 0 0 1 {{ a }} {{ a }} Z\" />")]
    [InlineData("<rect width=\"{{ a }}\" height=\"8\" rx=\"{{ a }}\" />")]
    [InlineData("<circle cx=\"16\" cy=\"16\" r=\"{{ a }}\" />")]
    [InlineData("<ellipse cx=\"{{ a }}\" cy=\"16\" rx=\"{{ a }}\" ry=\"4\" />")]
    [InlineData("<text x=\"2\" y=\"12\" transform=\"rotate({{ a }})\">A</text>")]
    public void Driven_Geometry_On_A_Part_Of_A_Clip_Is_Allowed(string part)
    {
        Assert.Null(Audit(Clipped(part)));
    }

    [Fact]
    public void A_Definition_Only_A_Clip_Uses_Is_Allowed()
    {
        Assert.Null(Audit(Clipped(
            "<use href=\"#s\" transform=\"translate({{ a }}, 0)\" />",
            "<rect id=\"s\" width=\"{{ a }}\" height=\"8\" transform=\"rotate({{ a }})\" />")));
    }

    /// <summary>A clip only takes ink away, and nothing a layer or a filter measures goes through it.</summary>
    [Fact]
    public void A_Driven_Clip_Under_A_Layer_Or_A_Filter_Is_Allowed()
    {
        const string clip = "<defs><clipPath id=\"e\"><rect width=\"{{ a }}\" height=\"8\" /></clipPath></defs>";

        Assert.Null(Audit(clip + "<g opacity=\"0.5\"><rect width=\"8\" height=\"8\" clip-path=\"url(#e)\" /></g>"));
        Assert.Null(Audit(clip + "<g filter=\"url(#f)\"><rect width=\"8\" height=\"8\" clip-path=\"url(#e)\" /></g>"));
    }

    [Theory]
    [InlineData("<rect width=\"{{ a }}\" height=\"8\" />", "is drawn")]
    [InlineData("<path d=\"M 0 0 L {{ a }} 8 Z\" />", "is drawn")]
    [InlineData("<defs><rect id=\"z\" width=\"{{ a }}\" height=\"8\" /></defs>", "not a part of any <clipPath>")]
    public void Driven_Geometry_Anywhere_But_A_Clip_Is_Refused(string body, string reason)
    {
        var refusal = Audit(body);

        Assert.Contains(reason, refusal);
        Assert.Contains("cannot reshape it", refusal);
    }

    /// <summary>A part a clip draws and a drawn &lt;use&gt; draws again is a drawn shape on the second count.</summary>
    [Fact]
    public void A_Definition_A_Clip_Uses_And_A_Drawing_Uses_Is_Refused()
    {
        Assert.Contains("is drawn", Audit(
            Clipped("<use href=\"#s\" />", "<rect id=\"s\" width=\"{{ a }}\" height=\"8\" />") +
            "<use href=\"#s\" />"));
    }

    [Fact]
    public void A_Clip_Part_A_TextPath_Follows_Is_Refused()
    {
        Assert.Contains("<textPath>", Audit(
            Clipped("<use href=\"#p\" />", "<path id=\"p\" d=\"M 0 16 L {{ a }} 16\" />") +
            "<text><textPath href=\"#p\">A</textPath></text>"));
    }

    [Theory]
    [InlineData("<rect width=\"8\" height=\"8\" transform=\"rotate({{ a }})\" transform-origin=\"16 16\" />", "transform-origin")]
    [InlineData("<rect width=\"{{ a }}\" height=\"8\" transform=\"rotate(10)\" transform-origin=\"16 16\" />", "transform-origin")]
    [InlineData("<path d=\"M 0 0 B 45 L {{ a }} 0 Z\" />", "bearing")]
    [InlineData("<path d=\"M {{ a }} 0 L {{ a }}0 8 Z\" />", "whole number")]
    [InlineData("<rect width=\"{{ a }}\" height=\"8\" clip-path=\"url(#c)\" />", "clip of its own")]
    [InlineData("<rect width=\"{{ a }}\" height=\"8\" style=\"width: 4\" />", "in CSS")]
    [InlineData("<rect width=\"8\" height=\"8\" transform=\"rotate({{ a }} 16)\" />", "no list of functions")]
    public void A_Part_Of_A_Clip_Measured_As_Its_Stand_In_Is_Refused(string part, string reason)
    {
        Assert.Contains(reason, Audit(Clipped(part)));
    }

    [Fact]
    public void A_Stylesheet_Rule_Over_Driven_Geometry_Is_Refused()
    {
        Assert.Contains("in CSS", Audit(
            "<style>.moved { cx: 4px; }</style>" +
            Clipped("<ellipse class=\"moved\" cx=\"{{ a }}\" cy=\"4\" rx=\"3\" ry=\"2\" />")));
    }

    /// <summary>
    /// A clip outside <c>&lt;defs&gt;</c> compiles to a container that draws nothing, which is no
    /// drawing of it, and no layer above that container measured it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("<g opacity=\"0.5\" filter=\"url(#f)\">", "</g>")]
    public void A_Driven_Clip_Outside_Defs_Is_Allowed(string open, string close = "")
    {
        Assert.Null(Audit(
            open +
            "<clipPath id=\"o\" transform=\"translate(8 8) rotate({{ a }})\"><rect x=\"-4\" y=\"-4\" width=\"{{ a }}\" height=\"8\" /></clipPath>" +
            "<clipPath id=\"z\" transform=\"rotate({{ a }})\"><rect width=\"8\" height=\"8\" /></clipPath>" +
            close +
            "<rect width=\"32\" height=\"32\" clip-path=\"url(#o)\" />" +
            "<rect width=\"32\" height=\"32\" clip-path=\"url(#z)\" />"));
    }

    /// <summary>
    /// What a clip holds gets a node only from a <c>&lt;use&gt;</c> outside it, which draws it as any
    /// drawn shape is drawn — the usual way to show a clip's outline.
    /// </summary>
    [Fact]
    public void A_Clip_Child_A_Use_Outside_It_Draws_Is_Refused()
    {
        Assert.Contains("is drawn", Audit(
            "<defs><clipPath id=\"e\"><rect id=\"r\" width=\"{{ a }}\" height=\"8\" /></clipPath></defs>" +
            "<rect width=\"8\" height=\"8\" clip-path=\"url(#e)\" />" +
            "<use href=\"#r\" fill=\"none\" stroke=\"#ff0000\" />"));

        Assert.Contains("the layer opened by", Audit(
            "<defs><clipPath id=\"e\"><rect id=\"r\" width=\"8\" height=\"8\" transform=\"rotate({{ a }})\" /></clipPath></defs>" +
            "<rect width=\"8\" height=\"8\" clip-path=\"url(#e)\" />" +
            "<g opacity=\"0.5\"><use href=\"#r\" /></g>"));
    }

    [Fact]
    public void A_Clip_Part_An_FeImage_Draws_As_Well_Is_Refused()
    {
        Assert.Contains("<feImage>", Audit(
            Clipped(
                "<use href=\"#bar\" />",
                "<rect id=\"bar\" width=\"{{ a }}\" height=\"10\" />" +
                "<filter id=\"i\"><feImage href=\"#bar\" /></filter>") +
            "<rect width=\"20\" height=\"20\" filter=\"url(#i)\" />"));
    }

    /// <summary>A pattern's content is compiled apart, so a clip part it draws as well is baked there.</summary>
    [Fact]
    public void A_Clip_Part_A_Pattern_Draws_As_Well_Is_Refused()
    {
        Assert.Contains("by a pattern", Audit(
            Clipped(
                "<use href=\"#bar\" />",
                "<rect id=\"bar\" width=\"{{ a }}\" height=\"10\" />" +
                "<pattern id=\"p\" width=\"20\" height=\"20\" patternUnits=\"userSpaceOnUse\"><use href=\"#bar\" /></pattern>") +
            "<rect width=\"20\" height=\"20\" fill=\"url(#p)\" />"));
    }

    [Fact]
    public void A_Clip_Part_Moved_By_A_Value_And_Followed_By_A_TextPath_Is_Refused()
    {
        Assert.Contains("<textPath>", Audit(
            Clipped("<use href=\"#p\" />", "<path id=\"p\" d=\"M4 4H28V28H4Z\" transform=\"rotate({{ a }} 16 16)\" />") +
            "<text><textPath href=\"#p\">A</textPath></text>"));
    }

    [Fact]
    public void A_Group_Inside_A_ClipPath_Is_Refused()
    {
        Assert.Contains(
            "without being one of its parts",
            Audit(Clipped("<g transform=\"rotate({{ a }})\"><rect width=\"8\" height=\"8\" /></g>")));
    }

    /// <summary>Redone against the live matrix by all three back ends, so it moves with the shape.</summary>
    [Fact]
    public void A_Non_Scaling_Stroke_Is_Allowed()
    {
        Assert.Null(Audit(
            "<rect width=\"8\" height=\"8\" " + Driven + " stroke=\"#000000\" vector-effect=\"non-scaling-stroke\" />"));
    }

    /// <summary>
    /// Recorded after the element's own matrix, in its own space, so it moves with the shape.
    /// </summary>
    [Fact]
    public void A_Clip_Path_On_The_Element_Is_Allowed()
    {
        Assert.Null(Audit("<rect width=\"8\" height=\"8\" " + Driven + " clip-path=\"url(#c)\" />"));
    }

    /// <summary>
    /// Built from local bounds and applied under the live canvas matrix, which is what SVG means by
    /// user space.
    /// </summary>
    [Fact]
    public void A_UserSpaceOnUse_Gradient_Is_Allowed()
    {
        Assert.Null(Audit("<rect width=\"8\" height=\"8\" " + Driven + " fill=\"url(#g)\" />"));
    }

    [Fact]
    public void An_Opacity_On_The_Element_Itself_Is_Allowed()
    {
        Assert.Null(Audit("<rect width=\"8\" height=\"8\" " + Driven + " opacity=\"0.5\" />"));
    }
}
