// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using Svg.Model;
using Svg.Model.Services;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// Where a driven transform is refused, and — just as important — where it is not.
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
        Assert.NotNull(Audit("<g opacity=\"0.5\"><rect width=\"8\" height=\"8\" " + Driven + " /></g>"));
    }

    [Fact]
    public void A_Transform_Written_Inside_A_ClipPath_Is_Refused()
    {
        Assert.Contains(
            "clipPath",
            Audit(
                "<defs><clipPath id=\"d\"><rect width=\"32\" height=\"32\" transform=\"rotate({{ a }})\" /></clipPath></defs>" +
                "<rect width=\"8\" height=\"8\" clip-path=\"url(#d)\" />")!);
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
