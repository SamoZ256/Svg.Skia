// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Linq;
using Svg.Model.Services;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// Splitting path data into the numbers an expression can drive, and lifting a clip shape's
/// geometry only where it is a shape's.
/// </summary>
/// <remarks>
/// The path builder reads a letter inside braces as a command, so the split has to find the
/// expressions first and segment the rest exactly as the builder would, or the slot an expression
/// lands in is not the one it was written in.
/// </remarks>
public class SvgPathDataExpressionTests
{
    private static SvgTransformExpressionValue Parse(string value)
        => SvgTransformExpression.Parse(SvgTransformExpression.PathDataName, value);

    [Fact]
    public void Path_Data_Is_Split_Into_One_Function_Per_Segment()
    {
        var value = Parse("M 10 20 L {{ x }} 5 h {{ w }} Z");

        Assert.Equal(new[] { "M", "L", "h", "Z" }, value.Functions.Select(function => function.Name));
        Assert.Equal(new[] { 2, 2, 1, 0 }, value.Functions.Select(function => function.Arguments.Count));
        Assert.Empty(value.Stray);
        Assert.Equal("x", value.Functions[1].Arguments[0].Expression);
        Assert.Equal("5", value.Functions[1].Arguments[1].Text);
        Assert.Equal("w", value.Functions[2].Arguments[0].Expression);
    }

    [Fact]
    public void A_Repeat_After_A_Move_Is_A_Line()
    {
        var value = Parse("m 0 0 {{ x }} 1 2 3");

        Assert.Equal(new[] { "m", "l", "l" }, value.Functions.Select(function => function.Name));
        Assert.Equal("x", value.Functions[1].Arguments[0].Expression);
    }

    [Fact]
    public void Numbers_Written_Without_Separators_Are_Split_As_SVG_Reads_Them()
    {
        var value = Parse("M10-5L.5.5{{ x }} 2e1-1E-1");

        Assert.Equal(new[] { "10", "-5" }, value.Functions[0].Arguments.Select(argument => argument.Text));
        Assert.Equal(new[] { ".5", ".5" }, value.Functions[1].Arguments.Select(argument => argument.Text));
        Assert.Equal(new[] { "2e1", "-1E-1" }, value.Functions[2].Arguments.Select(argument => argument.Text));

        // Written against the number before it, so it is no number of its own.
        Assert.Equal("{{ x }}", Assert.Single(value.Stray).Text);
    }

    /// <summary>An arc's flags are one character each, so two written together are two flags.</summary>
    [Fact]
    public void Arc_Flags_Written_Together_Are_Two_Numbers()
    {
        var joined = Parse("M 0 0 a1 1 0 00 1 1");
        Assert.Equal(new[] { "1", "1", "0", "0", "0", "1", "1" }, joined.Functions[1].Arguments.Select(argument => argument.Text));

        var driven = Parse("M 0 0 A 5 5 0 {{ big }} 1 {{ x }} {{ y }}");
        Assert.Equal("big", driven.Functions[1].Arguments[3].Expression);
        Assert.Equal(3, driven.Functions[1].Arguments[3].Index);
        Assert.Equal("y", driven.Functions[1].Arguments[6].Expression);
    }

    /// <summary>
    /// The path builder stops a command at a character it cannot read, so nothing after it joins
    /// that command until the next letter; and only its own five characters separate.
    /// </summary>
    [Theory]
    [InlineData("M 0 0 L {{ a }} 0 ; 10 10 Z")]
    [InlineData("M 0 0 L {{ a }} 0\u00a010 10 Z")]
    [InlineData("M 0 0 L {{ a }} 0 A 5 5 0 2 1 10 10 Z")]
    public void A_Character_The_Builder_Cannot_Read_Ends_The_Command(string value)
    {
        var parsed = Parse(value);

        Assert.Equal(new[] { "M", "L", "Z" }, parsed.Functions.Select(function => function.Name));
    }

    [Fact]
    public void An_Unbound_Number_In_A_Path_Reads_As_Nought()
    {
        Assert.Equal("M 6 6 L 0,0 A 6 6 0 0 1 0,0 Z", Parse("M 6 6 L {{ x }},{{ y }} A 6 6 0 {{ big }} 1 {{ cos(a) }},{{ sin(a) }} Z").Placeholder);
    }

    [Fact]
    public void A_Letter_Inside_Braces_Is_Code_Not_A_Command()
    {
        var value = Parse("M 0 0 L {{ cos(a) * big + sweep }} 0");

        Assert.Equal(new[] { "M", "L" }, value.Functions.Select(function => function.Name));
        Assert.Equal("cos(a) * big + sweep", value.Functions[1].Arguments[0].Expression);
    }

    [Theory]
    [InlineData("M 5{{ x }} 0")]
    [InlineData("M {{ x }}{{ y }}")]
    [InlineData("M {{ x }}-3 0")]
    [InlineData("M 0 0 L {{ x }} Z")]
    [InlineData("{{ x }} M 0 0")]
    [InlineData("M 0 0 Z {{ x }}")]
    [InlineData("M 0 0 L {{ x 0")]
    public void An_Expression_That_Is_Not_One_Whole_Number_Of_A_Whole_Segment_Drives_Nothing(string value)
    {
        Assert.NotEmpty(Parse(value).Stray);
        Assert.Contains("whole of one number", SvgExpressionAttributes.WhyUnsupported("d", value, "path", clipped: true));
    }

    [Fact]
    public void Geometry_Says_Where_It_Takes_An_Expression()
    {
        Assert.Null(SvgExpressionAttributes.WhyUnsupported("width", "{{ w }}", "rect", clipped: true));
        Assert.Contains("only inside a <clipPath>", SvgExpressionAttributes.WhyUnsupported("width", "{{ w }}", "rect", clipped: false));
        Assert.Contains("only on <rect>", SvgExpressionAttributes.WhyUnsupported("x", "{{ a }}", "text", clipped: true));
        Assert.Contains("<circle> or <ellipse>", SvgExpressionAttributes.WhyUnsupported("cx", "{{ a }}", "use", clipped: true));
        Assert.Contains("on the shapes of a <clipPath>", SvgExpressionAttributes.WhyUnsupported("points", "{{ a }}"));
    }

    private static SvgDocument Load(string body)
    {
        var document = SvgService.FromSvg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 32 32\" width=\"32\" height=\"32\">" + body + "</svg>");
        Assert.NotNull(document);

        return document!;
    }

    [Fact]
    public void A_Driven_Path_Is_Lifted_Whole_And_Read_From_Its_Stand_In()
    {
        var path = Load("<path d=\"M 0 0 L {{ x }} 4 Z\" />").Descendants().OfType<SvgPath>().Single();

        Assert.Equal("M 0 0 L {{ x }} 4 Z", SvgExpressionAttributes.Lifted(path.CustomAttributes, "d"));
        Assert.Equal(3, path.PathData.Count);
        Assert.Equal(new System.Drawing.PointF(0f, 4f), path.PathData[1].End);
    }

    [Fact]
    public void Geometry_Is_Lifted_On_The_Shapes_It_Belongs_To_And_Nowhere_Else()
    {
        var document = Load(
            "<rect id=\"r\" width=\"{{ w }}\" height=\"4\" rx=\"{{ r }}\" />" +
            "<circle id=\"c\" r=\"{{ r }}\" />" +
            "<ellipse id=\"e\" cx=\"{{ x }}\" rx=\"{{ r }}\" ry=\"2\" />" +
            "<text id=\"t\" x=\"{{ a }}\">A</text>" +
            "<use id=\"u\" x=\"{{ a }}\" href=\"#r\" />" +
            "<image id=\"i\" width=\"{{ a }}\" href=\"#r\" />");

        var rect = (SvgRectangle)document.GetElementById("r");
        Assert.Equal("w", SvgExpressionAttributes.Lifted(rect.CustomAttributes, "width"));
        Assert.Equal("r", SvgExpressionAttributes.Lifted(rect.CustomAttributes, "rx"));
        Assert.Equal(0f, rect.Width.Value);
        Assert.Equal("r", SvgExpressionAttributes.Lifted(document.GetElementById("c").CustomAttributes, "r"));
        Assert.Equal("x", SvgExpressionAttributes.Lifted(document.GetElementById("e").CustomAttributes, "cx"));

        foreach (var (id, name) in new[] { ("t", "x"), ("u", "x"), ("i", "width") })
        {
            Assert.Null(SvgExpressionAttributes.Lifted(document.GetElementById(id).CustomAttributes, name));
        }
    }

    /// <summary>A declaration does not know its element, so it lifts no geometry and takes none down.</summary>
    [Fact]
    public void Geometry_In_Style_Is_Neither_Lifted_Nor_Overrules_The_Attribute()
    {
        var document = Load(
            "<rect id=\"a\" height=\"4\" style=\"width: {{ w }}\" />" +
            "<rect id=\"b\" width=\"{{ w }}\" height=\"4\" style=\"width: 5\" />");

        Assert.Null(SvgExpressionAttributes.Lifted(document.GetElementById("a").CustomAttributes, "width"));
        Assert.Equal("w", SvgExpressionAttributes.Lifted(document.GetElementById("b").CustomAttributes, "width"));
        Assert.False(SvgExpressionAttributes.IsSupported("width", elementName: null));
        Assert.True(SvgExpressionAttributes.IsSupported("width", "rect"));
        Assert.True(SvgExpressionAttributes.IsSupported("fill", elementName: null));
    }
}
