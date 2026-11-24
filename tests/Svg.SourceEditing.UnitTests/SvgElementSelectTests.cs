using System;
using Svg.Expressions;
using Svg.SourceEditing;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>Select, by what the file writes.</summary>
public class SvgElementSelectTests
{
    private static string Svg(string svgText) => svgText.Replace("EXPR-NS", SvgExpressionDeclarations.Namespace);

    private static SvgSourceDocument Read(string svgText)
    {
        var source = SvgSourceDocument.Read(Svg(svgText), out var refusal);

        Assert.True(source is { }, refusal);

        return source!;
    }

    private static string[] Select(string svgText, SvgElementSelect how, string[]? selected = null, string? name = null)
        => Select(svgText, how, selected, name, out _);

    private static string[] Select(string svgText, SvgElementSelect how, string[]? selected, string? name, out string? note)
    {
        var source = Read(svgText);
        var before = source.ToText();
        var chosen = SvgElementEditor.Select(source, how, selected ?? Array.Empty<string>(), name, out note);

        Assert.Equal(before, source.ToText());

        return [.. chosen];
    }

    [Fact]
    public void All_Takes_What_Is_Drawn_Under_The_Root()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS" xmlns:sodipodi="http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd">
              <title>t</title>
              <sodipodi:namedview />
              <style>.a { fill: red; }</style>
              <defs><e:code /><rect id="kept" /></defs>
              <rect id="r" />
              <g id="g"><circle id="c" /></g>
              <text id="t"><tspan>hi</tspan></text>
            </svg>
            """;

        Assert.Equal(new[] { "4", "5", "6" }, Select(svgText, SvgElementSelect.All));
        Assert.Empty(Select(svgText, SvgElementSelect.None, new[] { "4" }));
    }

    [Fact]
    public void Inverse_Opens_A_Group_Around_What_Is_Picked()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect id="one" />
              <g id="pair">
                <rect id="a" />
                <rect id="b" />
              </g>
              <rect id="two" />
              <defs><rect /></defs>
            </svg>
            """;

        Assert.Equal(new[] { "0", "1/1", "2" }, Select(svgText, SvgElementSelect.Inverse, new[] { "1/0" }));
        Assert.Equal(new[] { "1", "2" }, Select(svgText, SvgElementSelect.Inverse, new[] { "0" }));
        Assert.Equal(new[] { "0", "1", "2" }, Select(svgText, SvgElementSelect.Inverse));
    }

    [Fact]
    public void Same_Fill_Reads_The_Style_Before_The_Attribute()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect fill="red" />
              <rect fill="blue" style="fill: red" />
              <rect style="fill:blue" fill="red" />
            </svg>
            """;

        Assert.Equal(new[] { "0", "1" }, Select(svgText, SvgElementSelect.Fill, new[] { "0" }));
    }

    [Fact]
    public void Same_Fill_Inherits_From_A_Group_And_Opacity_Does_Not()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g fill="red" opacity="0.5">
                <rect id="in" />
              </g>
              <rect id="out" fill="red" opacity="0.5" />
              <rect id="bare" />
            </svg>
            """;

        Assert.Equal(new[] { "0/0", "1" }, Select(svgText, SvgElementSelect.Fill, new[] { "0/0" }));
        Assert.Equal(new[] { "1" }, Select(svgText, SvgElementSelect.Opacity, new[] { "1" }));
    }

    /// <summary><c>accent</c> comes to <c>#2a7de1</c>, and is still not that colour as written.</summary>
    [Fact]
    public void An_Expression_Is_Not_The_Colour_It_Comes_To_And_A_Reference_Keeps_Its_Case()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS">
              <defs><e:code><e:param name="accent" type="color" default="#2a7de1" /></e:code></defs>
              <rect fill="{{ accent }}" />
              <rect fill="{{accent}}" />
              <rect fill="#2A7DE1" />
              <rect fill="#2a7de1" />
              <rect fill="url(#A)" />
              <rect fill="url(#a)" />
              <rect style="fill: #2a7de1 !important" />
              <rect fill="URL(#A)" />
            </svg>
            """;

        Assert.Equal(new[] { "1", "2" }, Select(svgText, SvgElementSelect.Fill, new[] { "1" }));
        Assert.Equal(new[] { "3", "4", "7" }, Select(svgText, SvgElementSelect.Fill, new[] { "3" }));
        Assert.Equal(new[] { "5", "8" }, Select(svgText, SvgElementSelect.Fill, new[] { "5" }));
        Assert.Equal(new[] { "6" }, Select(svgText, SvgElementSelect.Fill, new[] { "6" }));
    }

    /// <summary>The last declaration paints, as CSS has it, unless an earlier one is !important.</summary>
    [Fact]
    public void Same_Fill_Reads_The_Declaration_That_Paints()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect style="fill: red; fill: blue" />
              <rect fill="blue" />
              <rect style="fill: green !important; fill: red" />
              <rect fill="green" />
            </svg>
            """;

        Assert.Equal(new[] { "0", "1" }, Select(svgText, SvgElementSelect.Fill, new[] { "0" }));
        Assert.Equal(new[] { "2", "3" }, Select(svgText, SvgElementSelect.Fill, new[] { "2" }));
    }

    /// <summary>What a clip path keeps is not drawn, wherever it is written, and stands for nothing drawn.</summary>
    [Fact]
    public void A_Clips_Part_Stands_For_Nothing_Not_For_The_Group_It_Is_Written_In()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g>
                <clipPath id="c"><rect fill="red" /></clipPath>
                <rect clip-path="url(#c)" fill="blue" />
              </g>
              <circle fill="blue" />
            </svg>
            """;

        Assert.Equal(new[] { "0", "1" }, Select(svgText, SvgElementSelect.Inverse, new[] { "0/0/0" }));

        Assert.Empty(Select(svgText, SvgElementSelect.Fill, new[] { "0/0/0" }, null, out var note));
        Assert.Equal("There is no element to compare with.", note);
    }

    /// <summary>A shape is compared with shapes; a group with groups and shapes, outermost only.</summary>
    [Fact]
    public void A_Shape_Leaves_Groups_Out_And_A_Group_Takes_Them_In()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g fill="red"><rect /></g>
              <rect fill="red" />
            </svg>
            """;

        Assert.Equal(new[] { "1", "0/0" }, Select(svgText, SvgElementSelect.Fill, new[] { "1" }));
        Assert.Equal(new[] { "0", "1" }, Select(svgText, SvgElementSelect.Fill, new[] { "0" }));
    }

    /// <summary>The anchor is what was compared with, or the group that took it in.</summary>
    [Fact]
    public void What_Was_Compared_With_Comes_First_Even_Inside_A_Group_That_Matched()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect fill="red" />
              <g fill="red">
                <g id="r" fill="red"><rect /></g>
              </g>
            </svg>
            """;

        Assert.Equal(new[] { "1", "0" }, Select(svgText, SvgElementSelect.Fill, new[] { "1/0" }));
    }

    [Fact]
    public void Unwritten_Matches_Unwritten_Unless_A_Sheet_Could_Write_It()
    {
        const string plain = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect />
              <rect fill="red" />
              <rect />
            </svg>
            """;

        Assert.Equal(new[] { "0", "2" }, Select(plain, SvgElementSelect.Fill, new[] { "0" }));

        const string sheeted = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect />
              <style>.a { fill: red }</style>
              <rect fill="red" />
              <rect />
            </svg>
            """;

        Assert.Empty(Select(sheeted, SvgElementSelect.Fill, new[] { "0" }, null, out var note));
        Assert.Contains("<style>", note, StringComparison.Ordinal);

        // Opacity is read from the element alone, so the note says nothing of groups around it.
        Assert.Empty(Select(sheeted, SvgElementSelect.Opacity, new[] { "0" }, null, out note));
        Assert.DoesNotContain("group", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Type_Is_The_Element_Name_And_A_Part_Of_Text_Stands_For_Its_Text()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect />
              <circle />
              <rect />
              <text><tspan>a</tspan></text>
            </svg>
            """;

        Assert.Equal(new[] { "0", "2" }, Select(svgText, SvgElementSelect.Type, new[] { "0" }));
        Assert.Equal(new[] { "3" }, Select(svgText, SvgElementSelect.Type, new[] { "3/0" }));
        Assert.Equal(new[] { "1" }, Select(svgText, SvgElementSelect.Type, null, "circle"));

        Assert.Empty(Select(svgText, SvgElementSelect.Type, null, "tspan", out var note));
        Assert.Equal("Nothing drawn here is a <tspan>.", note);

        Assert.Empty(Select(svgText, SvgElementSelect.Fill, null, null, out note));
        Assert.Equal("There is no element to compare with.", note);
    }

    [Fact]
    public void Uses_Are_The_Drawn_Elements_Reading_A_Name()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS">
              <defs>
                <e:code>
                  <e:param name="tint" type="color" default="#ff0000" />
                  <e:let name="shade">tint</e:let>
                </e:code>
              </defs>
              <rect fill="{{ tint }}" />
              <text><tspan fill="{{ tint }}">a</tspan></text>
              <rect fill="red" />
              <g><circle fill="{{ tint }}" /></g>
            </svg>
            """;

        Assert.Equal(new[] { "1", "2", "4/0" }, Select(svgText, SvgElementSelect.Uses, null, "tint", out var note));

        // Nothing to say, rather than an empty saying, which would hide what the status line already says.
        Assert.Null(note);

        Assert.Empty(Select(svgText, SvgElementSelect.Uses, null, "unused", out note));
        Assert.Equal("Nothing drawn here reads unused.", note);

        Assert.Empty(Select(svgText, SvgElementSelect.Uses, null, " ", out note));
        Assert.Equal("Say which name.", note);
    }

    [Fact]
    public void Uses_Say_Where_The_Walk_Stopped()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect fill="{{ tint }}" />
              <circle r="{{ size = 2 }}" />
              <path fill="{{ tint }}" />
            </svg>
            """;

        Assert.Equal(new[] { "0" }, Select(svgText, SvgElementSelect.Uses, null, "tint", out var note));
        Assert.StartsWith("'size = 2' cannot be read", note, StringComparison.Ordinal);
        Assert.EndsWith("Uses past it were not looked for.", note, StringComparison.Ordinal);
    }
}
