using System;
using Svg.SourceEditing;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>Giving an element another id, and what points at it with it.</summary>
public class SvgElementRenameTests
{
    private static SvgSourceDocument Read(string text)
    {
        var source = SvgSourceDocument.Read(text, out var refusal);

        Assert.True(source is { }, refusal);

        return source!;
    }

    private const string Pointing = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
          <style>#g { opacity: 1 } a #g.x:hover { fill: url(#g) } #gg { fill: #fff }</style>
          <linearGradient id="g" />
          <linearGradient id="h" xlink:href="#g" />
          <rect fill="url(#g)" style="stroke: url( '#g' )" clip-path="url(#g)" mask="url(#g)" marker-end="url(#g)" filter="url(#g)" />
          <use href=" #g" />
          <a href="#g"><rect /></a>
          <animate begin="g.click; 2s;g.end" end="g.end" />
        </svg>
        """;

    [Fact]
    public void Everything_That_Points_At_The_Id_Follows_It()
    {
        var source = Read(Pointing);

        Assert.Null(SvgElementEditor.Rename(source, "1", "k", out var repointed));

        Assert.Equal("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
              <style>#k { opacity: 1 } a #k.x:hover { fill: url(#k) } #gg { fill: #fff }</style>
              <linearGradient id="k" />
              <linearGradient id="h" xlink:href="#k" />
              <rect fill="url(#k)" style="stroke: url( '#k' )" clip-path="url(#k)" mask="url(#k)" marker-end="url(#k)" filter="url(#k)" />
              <use href=" #k" />
              <a href="#k"><rect /></a>
              <animate begin="k.click; 2s;k.end" end="k.end" />
            </svg>
            """, source.ToText());
        Assert.Equal(15, repointed);
    }

    /// <summary>A reference finds the first element with an id, so renaming a later one takes nothing with it.</summary>
    [Fact]
    public void A_Later_Duplicate_Is_Renamed_Without_What_Points_At_The_First()
    {
        const string text = """<svg xmlns="http://www.w3.org/2000/svg"><linearGradient id="g" /><linearGradient id="g" /><rect fill="url(#g)" /></svg>""";
        var source = Read(text);

        Assert.Null(SvgElementEditor.Rename(source, "1", "k", out var repointed));

        Assert.Equal(text.Replace("<linearGradient id=\"g\" /><linearGradient id=\"g\" />", "<linearGradient id=\"g\" /><linearGradient id=\"k\" />", StringComparison.Ordinal), source.ToText());
        Assert.Equal(0, repointed);

        Assert.Null(SvgElementEditor.Rename(Read(text), "1", null, out _));
    }

    [Fact]
    public void An_Id_A_Css_Escape_Carries_On_Is_Another_Id()
    {
        const string text = """<svg xmlns="http://www.w3.org/2000/svg"><style>#a\.b { } #ab\.c { } #a { }</style><rect id="a" /></svg>""";
        var source = Read(text);

        Assert.Null(SvgElementEditor.Rename(source, "1", "z", out var repointed));

        Assert.Equal("""<svg xmlns="http://www.w3.org/2000/svg"><style>#a\.b { } #ab\.c { } #z { }</style><rect id="z" /></svg>""", source.ToText());
        Assert.Equal(1, repointed);
    }

    [Fact]
    public void A_Colour_In_A_Sheet_Is_Not_An_Id()
    {
        var source = Read("""<svg xmlns="http://www.w3.org/2000/svg"><style>#fff { fill: #fff }</style><rect id="fff" /></svg>""");

        Assert.Null(SvgElementEditor.Rename(source, "1", "w", out var repointed));

        Assert.Equal("""<svg xmlns="http://www.w3.org/2000/svg"><style>#w { fill: #fff }</style><rect id="w" /></svg>""", source.ToText());
        Assert.Equal(1, repointed);
    }

    [Fact]
    public void An_Id_That_Begins_Another_Is_Left_Alone()
    {
        const string text = """<svg xmlns="http://www.w3.org/2000/svg"><style>#ab { }</style><rect id="a" /><rect id="ab" /><use href="#ab" /><rect fill="url(#ab)" /><animate begin="ab.click" /></svg>""";
        var source = Read(text);

        Assert.Null(SvgElementEditor.Rename(source, "1", "z", out var repointed));

        Assert.Equal(text.Replace("id=\"a\"", "id=\"z\"", StringComparison.Ordinal), source.ToText());
        Assert.Equal(0, repointed);
    }

    [Fact]
    public void A_Sheet_In_CDATA_Stays_In_CDATA()
    {
        var source = Read("""<svg xmlns="http://www.w3.org/2000/svg"><style><!-- note --><![CDATA[#g { fill: url(#g) }]]></style><rect id="g" /></svg>""");

        Assert.Null(SvgElementEditor.Rename(source, "1", "k", out _));

        Assert.Equal("""<svg xmlns="http://www.w3.org/2000/svg"><style><!-- note --><![CDATA[#k { fill: url(#k) }]]></style><rect id="k" /></svg>""", source.ToText());
    }

    [Theory]
    [InlineData("b", "#b is already the id of a <circle>, and the drawing would answer to it twice.")]
    [InlineData("1a", "'1a' cannot be an id: it has to begin with a letter or '_' and hold no spaces, colons, brackets or braces.")]
    [InlineData("a b", "'a b' cannot be an id: it has to begin with a letter or '_' and hold no spaces, colons, brackets or braces.")]
    [InlineData("{{ x }}", "'{{ x }}' cannot be an id: it has to begin with a letter or '_' and hold no spaces, colons, brackets or braces.")]
    public void A_Taken_Id_And_A_Name_That_Is_No_Id_Are_Refused(string id, string refused)
    {
        const string text = """<svg xmlns="http://www.w3.org/2000/svg"><rect id="a" /><circle id="b" /><use href="#a" /></svg>""";
        var source = Read(text);

        Assert.Equal(refused, SvgElementEditor.Rename(source, "0", id, out _));
        Assert.Equal(text, source.ToText());
    }

    [Fact]
    public void An_Id_Something_Points_At_Is_Not_Taken_Away()
    {
        const string text = """<svg xmlns="http://www.w3.org/2000/svg"><linearGradient id="g" /><rect fill="url(#g)" /><rect id="r" /></svg>""";
        var source = Read(text);

        Assert.Equal("A reference points at #g: give it another id, or remove the reference first.", SvgElementEditor.Rename(source, "0", " ", out _));
        Assert.Equal(text, source.ToText());

        Assert.Null(SvgElementEditor.Rename(source, "2", null, out _));
        Assert.Equal(text.Replace(" id=\"r\"", string.Empty, StringComparison.Ordinal), source.ToText());
    }

    [Fact]
    public void An_Element_With_No_Id_Is_Given_One_And_Nothing_Else_Changes()
    {
        const string text = """<svg xmlns="http://www.w3.org/2000/svg"><rect /><rect id="{{ name }}" /><use href="#{{ name }}" /></svg>""";
        var source = Read(text);

        Assert.Null(SvgElementEditor.Rename(source, "0", "n", out var given));
        Assert.Null(SvgElementEditor.Rename(source, "1", "m", out var bound));

        Assert.Equal("""<svg xmlns="http://www.w3.org/2000/svg"><rect id="n" /><rect id="m" /><use href="#{{ name }}" /></svg>""", source.ToText());
        Assert.Equal((0, 0), (given, bound));
    }
}
