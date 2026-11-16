// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ShimSkiaSharp;
using Svg.Expressions;
using Svg.Model;
using Svg.Model.Services;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// A clip whose geometry or transform an expression drives: what the scene records, and that a
/// bound clip is the clip the same document written with those numbers draws.
/// </summary>
/// <remarks>
/// Compared as the native path each clip becomes, so a part spelled differently in the model — the
/// literal document's polygon is <c>AddPoly</c>, the driven one a move, lines and a close — still
/// has to come to the same path.
/// </remarks>
public class SvgSceneClipExpressionTests
{
    private const string Ns = "https://svg.skia/expr/1.0";

    private static readonly SkiaModel s_model = new(new SKSvgSettings());

    private static string Document(string clip, string target = "<rect id=\"target\" width=\"32\" height=\"32\" clip-path=\"url(#c)\" />")
        => "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:e=\"" + Ns + "\" viewBox=\"0 0 32 32\" width=\"32\" height=\"32\">" +
           "<defs><e:code><e:param name=\"a\" type=\"number\" default=\"0\" /><e:param name=\"b\" type=\"number\" default=\"0\" /></e:code>" +
           clip + "</defs>" + target + "</svg>";

    private static (SvgSceneDocument Scene, SvgSceneNode Node) Compile(string markup)
    {
        var document = SvgService.FromSvg(markup);
        Assert.NotNull(document);
        Assert.True(SvgSceneRuntime.TryCompile(document!, new SkiaSvgAssetLoader(s_model), DrawAttributes.None, out var scene));
        Assert.True(scene!.TryGetNodeById("target", out var node));

        return (scene, node!);
    }

    private static ExprEvaluator Values(SvgSceneDocument scene, float a, float b)
        => ExprEvaluator.Create(
            scene.SourceDocument!.ExpressionDeclarations,
            new Dictionary<string, ExprValue> { ["a"] = ExprValue.Number(a), ["b"] = ExprValue.Number(b) });

    private static string Native(ClipPath? clip)
    {
        Assert.NotNull(clip);
        using var path = s_model.ToSKPath(clip);
        Assert.NotNull(path);

        return path!.ToSvgPathData();
    }

    private static string Literal(string markup, float a, float b)
        => markup
            .Replace("{{ a }}", a.ToString("R", CultureInfo.InvariantCulture))
            .Replace("{{ b }}", b.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>Binds the driven document, and checks its clip against the same document with the numbers written in.</summary>
    private static void AssertBindsAsTheLiteral(string clip, float a, float b, string? target = null)
    {
        var markup = target is null ? Document(clip) : Document(clip, target);
        var (scene, node) = Compile(markup);
        Assert.True(node.ClipPath!.IsSymbolic);

        scene.ApplyExpressionTransforms(Values(scene, a, b));
        Assert.False(node.BoundClipPath!.IsSymbolic);

        Assert.Equal(Native(Compile(Literal(markup, a, b)).Node.ClipPath), Native(node.BoundClipPath));
    }

    [Fact]
    public void A_Driven_Path_Records_What_Drives_It_And_Draws_Its_Stand_In()
    {
        var (_, node) = Compile(Document("<clipPath id=\"c\"><path d=\"M 16 16 L 16 0 A 16 16 0 0 1 {{ a }} {{ b }} Z\" /></clipPath>"));

        var part = Assert.Single(node.ClipPath!.Clips!);
        Assert.NotNull(part.SymbolicPath);
        Assert.True(part.IsSymbolic);
        Assert.Equal(
            new[] { SymPathOp.MoveTo, SymPathOp.LineTo, SymPathOp.ArcTo, SymPathOp.Close },
            part.SymbolicPath!.Commands.Select(command => command.Op));
        Assert.Equal(part.SymbolicPath.ToPlaceholder().Commands, part.Path!.Commands);
    }

    [Fact]
    public void A_Clip_Nothing_Drives_Records_What_It_Always_Did()
    {
        var (_, node) = Compile(Document("<clipPath id=\"c\"><path d=\"M 2 2 L 20 2 L 2 20 Z\" transform=\"rotate(10)\" /></clipPath>"));

        var part = Assert.Single(node.ClipPath!.Clips!);
        Assert.False(node.ClipPath.IsSymbolic);
        Assert.Null(part.SymbolicPath);
        Assert.Null(part.SymbolicTransform);
        Assert.IsType<AddPolyPathCommand>(Assert.Single(part.Path!.Commands!));
        Assert.Same(node.ClipPath, node.BoundClipPath);
    }

    [Theory]
    [InlineData("M {{ a }} 2 L 20 {{ b }} L 4 20 Z", 3f, 5f)]
    [InlineData("m {{ a }} 2 l 10 {{ b }} h -4 v 3 z m 2 2 l 3 3 z", 3f, -2f)]
    [InlineData("M 2 2 H {{ a }} V {{ b }} H 2 Z", 20f, 18f)]
    [InlineData("M 2 2 C {{ a }} 0 20 {{ b }} 20 20 S 0 20 2 2 s 4 4 6 0 Z", 12f, 6f)]
    [InlineData("M 2 2 Q {{ a }} 0 20 {{ b }} T 2 20 t 4 -4 z", 12f, 9f)]
    [InlineData("M 16 16 L 16 0 A 16 16 0 0 1 {{ a }} {{ b }} A 16 16 0 0 1 16 32 Z", 32f, 16f)]
    [InlineData("M 16 16 l 0 -16 a 16 16 0 {{ a }} 1 {{ b }} 16 z", 1f, 16f)]
    [InlineData("M 2 2 {{ a }} 2 20 {{ b }} z", 24f, 20f)]
    [InlineData("M 2 2 L 20 {{ a }} Z M {{ b }} 4 L 30 4 L 30 30 Z", 18f, 22f)]
    public void A_Bound_Path_Clips_As_The_Same_Path_Written_With_Its_Numbers(string data, float a, float b)
        => AssertBindsAsTheLiteral($"<clipPath id=\"c\"><path d=\"{data}\" /></clipPath>", a, b);

    /// <summary>The arc rules the converter applies are applied to a bound arc as well.</summary>
    [Theory]
    [InlineData(0f, 10f)]
    [InlineData(-8f, 10f)]
    [InlineData(8f, 16f)]
    public void A_Bound_Arc_Of_No_Radius_Or_No_Length_Is_What_The_Literal_Is(float radius, float endX)
        => AssertBindsAsTheLiteral(
            "<clipPath id=\"c\"><path d=\"M 16 16 L 16 4 A {{ a }} 8 0 0 1 {{ b }} 4 Z\" /></clipPath>",
            radius,
            endX);

    [Theory]
    [InlineData("<rect x=\"{{ a }}\" y=\"2\" width=\"{{ b }}\" height=\"20\" />", 4f, 12f)]
    [InlineData("<rect x=\"2\" y=\"2\" width=\"20\" height=\"20\" rx=\"{{ a }}\" ry=\"{{ b }}\" />", 4f, 0f)]
    [InlineData("<rect x=\"2\" y=\"2\" width=\"20\" height=\"20\" rx=\"{{ a }}\" ry=\"{{ b }}\" />", 40f, -3f)]
    [InlineData("<rect x=\"2\" y=\"2\" width=\"{{ a }}\" height=\"{{ b }}\" rx=\"6\" />", 20f, 8f)]
    [InlineData("<circle cx=\"{{ a }}\" cy=\"16\" r=\"{{ b }}\" />", 12f, 9f)]
    [InlineData("<ellipse cx=\"16\" cy=\"16\" rx=\"{{ a }}\" ry=\"{{ b }}\" />", 12f, -6f)]
    [InlineData("<ellipse cx=\"16\" cy=\"{{ b }}\" rx=\"{{ a }}\" />", 9f, 12f)]
    public void A_Bound_Shape_Clips_As_The_Same_Shape_Written_With_Its_Numbers(string shape, float a, float b)
        => AssertBindsAsTheLiteral($"<clipPath id=\"c\">{shape}<rect x=\"28\" y=\"28\" width=\"2\" height=\"2\" /></clipPath>", a, b);

    /// <summary>
    /// A radius bound to nought borrows the other, measured on its own axis: a percentage of a target
    /// wider than it is tall comes to two different lengths.
    /// </summary>
    [Theory]
    [InlineData("<rect x=\"2\" y=\"2\" width=\"28\" height=\"12\" rx=\"10%\" ry=\"{{ b }}\" />")]
    [InlineData("<rect x=\"2\" y=\"2\" width=\"28\" height=\"12\" rx=\"{{ b }}\" ry=\"20%\" />")]
    public void A_Bound_Radius_Of_Nought_Borrows_A_Percentage_As_The_Literal_Does(string shape)
        => AssertBindsAsTheLiteral(
            $"<clipPath id=\"c\">{shape}</clipPath>",
            0f,
            0f,
            "<rect id=\"target\" width=\"32\" height=\"16\" clip-path=\"url(#c)\" />");

    /// <summary>Built from the expressions, so a stand-in that draws nothing still leaves the part a value reshapes.</summary>
    [Fact]
    public void A_Rect_That_Stands_In_At_No_Width_Still_Records_Its_Part()
    {
        var clip = "<clipPath id=\"c\"><rect x=\"2\" y=\"2\" width=\"{{ a }}\" height=\"20\" /></clipPath>";
        var (_, node) = Compile(Document(clip));

        var part = Assert.Single(node.ClipPath!.Clips!);
        Assert.True(part.Path!.IsEmpty);
        Assert.Equal(SymPathOp.Rect, Assert.Single(part.SymbolicPath!.Commands).Op);

        AssertBindsAsTheLiteral(clip, 12f, 0f);
    }

    [Theory]
    [InlineData("<clipPath id=\"c\"><rect width=\"16\" height=\"16\" transform=\"rotate({{ a }} 16 16)\" /></clipPath>")]
    [InlineData("<clipPath id=\"c\" transform=\"translate({{ a }}, {{ b }})\"><rect width=\"16\" height=\"16\" /></clipPath>")]
    [InlineData("<clipPath id=\"c\" clipPathUnits=\"objectBoundingBox\" transform=\"scale({{ b }}, 1)\"><rect width=\"0.5\" height=\"0.5\" /></clipPath>")]
    [InlineData("<rect id=\"s\" width=\"16\" height=\"16\" /><clipPath id=\"c\"><use href=\"#s\" x=\"4\" transform=\"rotate({{ a }} 16 16)\" /></clipPath>")]
    [InlineData("<rect id=\"s\" width=\"16\" height=\"{{ b }}\" transform=\"translate({{ a }}, 0)\" /><clipPath id=\"c\"><use href=\"#s\" x=\"2\" /></clipPath>")]
    public void A_Bound_Transform_Places_The_Clip_Where_The_Literal_Does(string clip)
        => AssertBindsAsTheLiteral(clip, 30f, 0.5f);

    /// <summary>A driven translate stands in as the identity, which is no reason to leave the use out.</summary>
    [Fact]
    public void A_Use_Moved_By_A_Driven_Translate_Alone_Still_Moves()
    {
        var clip = "<rect id=\"s\" width=\"16\" height=\"16\" /><clipPath id=\"c\"><use href=\"#s\" transform=\"translate({{ a }}, 0)\" /></clipPath>";
        var (_, node) = Compile(Document(clip));

        Assert.NotNull(Assert.Single(node.ClipPath!.Clips!).SymbolicTransform);
        AssertBindsAsTheLiteral(clip, 9f, 0f);
    }

    [Fact]
    public void Binding_Reshapes_What_A_Hit_Meets_Without_Touching_The_Recorded_Clip()
    {
        var (scene, node) = Compile(Document("<clipPath id=\"c\"><rect width=\"{{ a }}\" height=\"32\" /></clipPath>"));
        var compiled = node.ClipPath;
        var placeholder = compiled!.Clips![0].Path;

        scene.ApplyExpressionTransforms(Values(scene, 12f, 0f));

        Assert.Same(compiled, node.ClipPath);
        Assert.Same(placeholder, compiled.Clips[0].Path);
        Assert.NotSame(compiled, node.BoundClipPath);
        Assert.Equal(new AddRectPathCommand(SKRect.Create(0, 0, 12, 32)), Assert.Single(node.BoundClipPath!.Clips![0].Path!.Commands!));

        scene.ApplyExpressionTransforms(null);
        Assert.Same(compiled, node.BoundClipPath);
    }

    [Fact]
    public void A_Child_Of_A_Clip_Is_Shown_With_The_Values_It_Is_Given()
    {
        var (scene, node) = Compile(Document("<clipPath id=\"c\"><rect width=\"{{ a }}\" height=\"8\" /></clipPath>"));
        Assert.True(scene.TryGetResourceById("c", out var resource));

        var placed = Assert.Single(resource!.ClipContent(scene, node, Values(scene, 6f, 0f))).Clip;
        Assert.False(placed.IsSymbolic);
        Assert.Equal(new AddRectPathCommand(SKRect.Create(0, 0, 6, 8)), Assert.Single(placed.Clips![0].Path!.Commands!));

        Assert.True(Assert.Single(resource.ClipContent(scene, node)).Clip.IsSymbolic);
    }
}
