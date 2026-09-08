// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Linq;
using Svg.Model.Services;
using Svg.Transforms;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// Splitting a transform into the arguments an expression can drive.
/// </summary>
/// <remarks>
/// The grammar this pins is one level down from the extension's own: an argument is wholly an
/// expression or wholly a literal, and what a stand-in has to be differs by slot.
/// </remarks>
public class SvgTransformExpressionTests
{
    [Fact]
    public void A_Value_Without_Braces_Is_Not_Worth_Splitting()
    {
        Assert.False(SvgTransformExpression.Holds("translate(10, 20) rotate(45)"));
        Assert.False(SvgTransformExpression.Holds(null));
        Assert.True(SvgTransformExpression.Holds("rotate({{ a }})"));
    }

    [Fact]
    public void Each_Function_Keeps_Its_Arguments_In_Order()
    {
        var value = SvgTransformExpression.Parse("translate({{ dx }}, 0) rotate({{ a }} 32 32)");

        Assert.Equal(new[] { "translate", "rotate" }, value.Functions.Select(function => function.Name));
        Assert.Equal(new[] { 2, 3 }, value.Functions.Select(function => function.Arguments.Count));
        Assert.True(value.Any);
        Assert.Empty(value.Stray);

        Assert.Equal("dx", value.Functions[0].Arguments[0].Expression);
        Assert.Null(value.Functions[0].Arguments[1].Expression);
        Assert.Equal("a", value.Functions[1].Arguments[0].Expression);
        Assert.Equal(0, value.Functions[1].Arguments[0].Index);
    }

    [Fact]
    public void An_Unbound_Argument_Reads_As_The_Identity_Of_Its_Slot()
    {
        Assert.Equal(
            "translate(0, 0) rotate(0 32 32)",
            SvgTransformExpression.Parse("translate({{ dx }}, 0) rotate({{ angle }} 32 32)").Placeholder);
    }

    /// <summary>The whole reason the stand-in is per slot: a scale of zero has no shape left to bind.</summary>
    [Fact]
    public void An_Unbound_Scale_Is_One_And_Not_Zero()
    {
        Assert.Equal("scale(1)", SvgTransformExpression.Parse("scale({{ s }})").Placeholder);
        Assert.Equal("scale(1, 1)", SvgTransformExpression.Parse("scale({{ x }}, {{ y }})").Placeholder);
        Assert.Equal("matrix(1 0 0 1 0 0)", SvgTransformExpression.Parse("matrix({{ a }} 0 0 {{ d }} {{ e }} 0)").Placeholder);
    }

    [Fact]
    public void The_Literals_Written_Beside_An_Expression_Survive()
    {
        Assert.Equal("translate(10, 0)", SvgTransformExpression.Parse("translate(10, {{ dy }})").Placeholder);
    }

    /// <summary>The case that fails if anyone reuses SvgTransformConverter's splitter.</summary>
    [Fact]
    public void A_Comma_Inside_An_Expression_Does_Not_Separate_Arguments()
    {
        var value = SvgTransformExpression.Parse("translate({{ min(a, b) }}, 0)");

        Assert.Single(value.Functions);
        Assert.Equal(2, value.Functions[0].Arguments.Count);
        Assert.Equal("min(a, b)", value.Functions[0].Arguments[0].Expression);
        Assert.Equal("translate(0, 0)", value.Placeholder);
    }

    [Fact]
    public void An_Expression_That_Is_Not_A_Whole_Argument_Drives_Nothing()
    {
        var partial = SvgTransformExpression.Parse("translate(1{{ dx }}, 0)");
        Assert.False(partial.Any);
        Assert.NotEmpty(partial.Stray);

        var whole = SvgTransformExpression.Parse("{{ t }}");
        Assert.False(whole.Any);
        Assert.NotEmpty(whole.Stray);
        Assert.Empty(whole.Functions);

        var unterminated = SvgTransformExpression.Parse("rotate({{ a )");
        Assert.False(unterminated.Any);
    }

    [Fact]
    public void An_Argument_Knows_Where_It_Was_Written()
    {
        const string written = "translate(4, {{ dy }})";
        var argument = SvgTransformExpression.Parse(written).Functions[0].Arguments[1];

        Assert.Equal("{{ dy }}", written.Substring(argument.Start, argument.Length));
    }
}

/// <summary>
/// Lifting a driven transform out of a document, and what the element holds instead of it.
/// </summary>
public class SvgTransformExpressionLiftTests
{
    private const string Ns = "https://svg.skia/expr/1.0";

    private static SvgElement Load(string element)
    {
        var markup =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:e=\"" + Ns + "\" viewBox=\"0 0 64 64\">" +
            "<defs><e:code><e:param name=\"dx\" type=\"number\" default=\"0\" />" +
            "<e:param name=\"s\" type=\"number\" default=\"1\" />" +
            "<e:param name=\"a\" type=\"number\" default=\"0\" /></e:code></defs>" +
            element +
            "</svg>";

        var document = SvgService.FromSvg(markup);
        Assert.NotNull(document);

        return document!.Descendants().OfType<SvgRectangle>().Single();
    }

    private static string Written(SvgElement element)
        => string.Join(" ", element.Transforms.Select(transform => transform.WriteToString()));

    [Fact]
    public void The_Attribute_Keeps_The_Author_Text_And_Stands_In_For_It()
    {
        var rect = Load("<rect width=\"8\" height=\"8\" transform=\"translate({{ dx }}, 0) rotate({{ a }} 32 32)\" />");

        Assert.Equal(
            "translate({{ dx }}, 0) rotate({{ a }} 32 32)",
            SvgExpressionAttributes.Lifted(rect.CustomAttributes, "transform"));

        Assert.Collection(
            rect.Transforms,
            transform => Assert.IsType<SvgTranslate>(transform),
            transform => Assert.IsType<SvgRotate>(transform));

        var rotate = (SvgRotate)rect.Transforms[1];
        Assert.Equal(0f, rotate.Angle);
        Assert.Equal(32f, rotate.CenterX);
        Assert.Equal(32f, rotate.CenterY);
    }

    /// <summary>The whole reason a stand-in is per slot: zero would leave no shape to bind to.</summary>
    [Fact]
    public void An_Unbound_Scale_Leaves_The_Shape_Its_Authored_Size()
    {
        var rect = Load("<rect width=\"8\" height=\"8\" transform=\"scale({{ s }})\" />");

        var scale = Assert.IsType<SvgScale>(Assert.Single(rect.Transforms));
        Assert.Equal(1f, scale.X);
    }

    [Fact]
    public void A_Literal_Written_Beside_An_Expression_Survives()
    {
        var rect = Load("<rect width=\"8\" height=\"8\" transform=\"translate(10, {{ dx }})\" />");

        var translate = Assert.IsType<SvgTranslate>(Assert.Single(rect.Transforms));
        Assert.Equal(10f, translate.X);
        Assert.Equal(0f, translate.Y);
    }

    [Fact]
    public void A_Style_Declaration_Lifts_The_Same_Way()
    {
        var rect = Load("<rect width=\"8\" height=\"8\" style=\"transform: rotate({{ a }} 32 32)\" />");

        Assert.Equal(
            "rotate({{ a }} 32 32)",
            SvgExpressionAttributes.Lifted(rect.CustomAttributes, "transform"));
        Assert.Equal("rotate(0, 32, 32)", Written(rect));
    }

    /// <summary>The cascade decides, as it does for a colour: a literal that wins takes the expression with it.</summary>
    [Fact]
    public void A_Literal_In_Style_Takes_Down_An_Expression_In_The_Attribute()
    {
        var rect = Load("<rect width=\"8\" height=\"8\" transform=\"rotate({{ a }})\" style=\"transform: rotate(30)\" />");

        Assert.Null(SvgExpressionAttributes.Lifted(rect.CustomAttributes, "transform"));
    }

    [Fact]
    public void Braces_That_Are_Not_A_Whole_Argument_Lift_Nothing()
    {
        Assert.Null(SvgExpressionAttributes.Lifted(
            Load("<rect width=\"8\" height=\"8\" transform=\"{{ a }}\" />").CustomAttributes,
            "transform"));

        Assert.Null(SvgExpressionAttributes.Lifted(
            Load("<rect width=\"8\" height=\"8\" transform=\"translate(1{{ dx }}, 0)\" />").CustomAttributes,
            "transform"));
    }
}
