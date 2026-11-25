using System;
using Svg.SourceEditing;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>Swapping what elements fill and stroke with, as the file writes it.</summary>
public class SvgAttributeEditorSwapTests
{
    private static string Svg(string body) => $"""
        <svg xmlns="http://www.w3.org/2000/svg">
          {body}
        </svg>
        """;

    private static (string? Refusal, string Text, string? Note) Swap(string body, params string[] keys)
    {
        var source = SvgSourceDocument.Read(Svg(body), out var unread);

        Assert.True(source is { }, unread);

        var refusal = SvgAttributeEditor.SwapPaints(source!, keys, out var note);

        return (refusal, source!.ToText(), note);
    }

    private static void Swaps(string body, string swapped, params string[] keys)
    {
        var (refusal, text, _) = Swap(body, keys);

        Assert.Null(refusal);
        Assert.Equal(Svg(swapped), text);
    }

    [Fact]
    public void Fill_And_Stroke_Change_Places_As_Written()
        => Swaps("""<rect fill="red" width="4" stroke=" {{ accent }}" />""", """<rect fill="{{ accent }}" width="4" stroke="red" />""", "0");

    [Fact]
    public void A_Declaration_Keeps_Its_Place_And_Its_Importance()
        => Swaps(
            """<rect style="fill: red !important; stroke:blue" fill="green" />""",
            """<rect style="fill: blue !important; stroke:red" fill="green" />""",
            "0");

    [Fact]
    public void What_A_Group_Gives_Is_Carried_And_Shadowing_It_Is_Said()
    {
        var (refusal, text, note) = Swap("""<g id="wrap" fill="{{ accent }}"><rect stroke="red" /></g>""", "0/0");

        Assert.Null(refusal);
        Assert.Equal(Svg("""<g id="wrap" fill="{{ accent }}"><rect stroke="{{ accent }}" fill="red" /></g>"""), text);
        Assert.Equal("The fill now shadows {{ accent }} from <g #wrap>.", note);
    }

    /// <summary>Unwritten, a fill paints black and a stroke nothing, so a plain shape swaps to an outline.</summary>
    [Fact]
    public void An_Unwritten_Paint_Swaps_As_What_It_Paints()
        => Swaps("""<path d="M0 0H4" />""", """<path d="M0 0H4" fill="none" stroke="black" />""", "0");

    [Fact]
    public void Opacities_Travel_With_Their_Paints()
        => Swaps(
            """<rect fill="red" fill-opacity="0.5" stroke="blue" />""",
            """<rect fill="blue" fill-opacity="1" stroke="red" stroke-opacity="0.5" />""",
            "0");

    /// <summary>The group first, so what it holds inherits the swapped paints and needs nothing of its own.</summary>
    [Fact]
    public void A_Group_And_Its_Child_Are_Swapped_Once()
        => Swaps(
            """<g fill="red" stroke="blue"><rect /></g>""",
            """<g fill="blue" stroke="red"><rect /></g>""",
            "0/0", "0");

    /// <summary>Written in place even where the group around says the same, so the element is not tied to the group from then on.</summary>
    [Fact]
    public void An_Own_Value_Is_Kept_Even_Where_A_Group_Says_The_Same()
        => Swaps(
            """<g fill="blue"><rect fill="red" stroke="blue" /></g>""",
            """<g fill="blue"><rect fill="blue" stroke="red" /></g>""",
            "0/0");

    /// <summary>What an element now shadows is what its group says after the group is swapped too.</summary>
    [Theory]
    [InlineData("""<g id="wrap" fill="{{ accent }}"><rect stroke="red" /></g>""", null)]
    [InlineData("""<g fill="red" stroke="{{ c }}"><rect stroke="blue" /></g>""", "The fill now shadows {{ c }} from <g>.")]
    public void A_Shadow_Is_Of_What_The_Group_Says_Once_Swapped_Too(string body, string? said)
    {
        var (refusal, _, note) = Swap(body, "0", "0/0");

        Assert.Null(refusal);
        Assert.Equal(said, note);
    }

    [Fact]
    public void One_Element_Shadowing_Both_Paints_Is_Said_As_One()
    {
        var (_, _, note) = Swap("""<g fill="{{ a }}" stroke="{{ b }}"><rect /></g>""", "0/0");

        Assert.Equal("Its fill and stroke now shadow {{ a }}, {{ b }} from <g>.", note);
    }

    /// <summary>What a <c>&lt;use&gt;</c> draws inherits from it, so a paint left to the groups the file writes it in is not known.</summary>
    [Fact]
    public void A_Paint_A_Use_Gives_Is_Not_Guessed()
    {
        const string body = """<symbol id="i"><path d="M0 0H4" /></symbol><use href="#i" fill="red" />""";
        var (refusal, text, _) = Swap(body, "0/0");

        Assert.Equal("Its fill is not written inside the <symbol> it is kept in, so a <use> drawing it gives it its own; write it there first.", refusal);
        Assert.Equal(Svg(body), text);

        Swaps(
            """<symbol id="i" fill="red"><path d="M0 0H4" stroke="blue" /></symbol>""",
            """<symbol id="i" fill="red"><path d="M0 0H4" stroke="red" fill="blue" /></symbol>""",
            "0/0");
    }

    /// <summary>An empty attribute is no value, and the element paints what it would without it.</summary>
    [Fact]
    public void A_Blank_Value_Swaps_As_What_It_Paints()
        => Swaps("""<rect fill="" stroke="red" />""", """<rect fill="red" stroke="black" />""", "0");

    [Theory]
    [InlineData("""<linearGradient><stop offset="0" /></linearGradient>""", "0/0", "A <stop> has no fill or stroke to swap.")]
    [InlineData("""<clipPath><rect fill="red" /></clipPath>""", "0/0", "Inside a <clipPath> only its outline counts, so its fill and stroke do nothing.")]
    public void What_Takes_No_Paint_Or_Only_Outlines_Is_Refused(string body, string key, string refused)
    {
        var (refusal, text, _) = Swap(body, key);

        Assert.Equal(refused, refusal);
        Assert.Equal(Svg(body), text);
    }

    [Fact]
    public void A_Paint_A_Sheet_May_Set_Is_Not_Guessed()
    {
        const string body = """<style>rect { fill: green }</style><rect stroke="blue" />""";
        var (refusal, text, _) = Swap(body, "1");

        Assert.Equal("Its fill is not written on it or on a group around it, and the drawing's <style> sheet, which swapping does not read, may set it.", refusal);
        Assert.Equal(Svg(body), text);
    }

    [Fact]
    public void A_Missing_Key_Refuses_The_Lot()
    {
        const string body = """<rect fill="red" />""";
        var (refusal, text, _) = Swap(body, "0", "9");

        Assert.Equal("There is no element at 9 in this drawing.", refusal);
        Assert.Equal(Svg(body), text);
    }
}
