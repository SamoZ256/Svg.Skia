using System;
using System.Linq;
using Svg.SourceEditing;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// Writing one attribute of one element into the tree, rather than into the text it came from.
/// </summary>
/// <remarks>
/// The same behaviour as the span editor beside it, refusal for refusal — those sentences are
/// user-facing and the two must not come to disagree while both exist. What differs is the medium,
/// and one thing more: the tree refuses a name that XML rejects, where splicing text would have
/// written it and left a document nobody could read back.
/// </remarks>
public class SvgAttributeEditorDocumentTests
{
    private const string Source = """
        <svg xmlns="http://www.w3.org/2000/svg">
          <rect x='1' y="2" fill="{{ tint }}"/>
          <g><circle cx="32" style="fill:red" /></g>
        </svg>
        """;

    /// <summary>Text elements, and the ones that look like them and are not.</summary>
    private const string Texts = """
        <svg xmlns="http://www.w3.org/2000/svg">
          <text x="4">hi</text>
          <text x="5"/>
          <text x="6"> <tspan>a</tspan> </text>
          <text x="7"><!-- kept -->hi</text>
          <tref xmlns:xlink="http://www.w3.org/1999/xlink" xlink:href="#o">hi</tref>
          <rect x="8"/>
        </svg>
        """;

    private static SvgSourceDocument Read(string svgText = Source)
    {
        var source = SvgSourceDocument.Read(svgText, out var refusal);

        Assert.NotNull(source);
        Assert.Null(refusal);

        return source!;
    }

    [Fact]
    public void An_Address_Names_An_Element_Or_It_Does_Not()
    {
        var source = Read();

        Assert.True(SvgAttributeEditor.Contains(source, string.Empty));
        Assert.True(SvgAttributeEditor.Contains(source, "0"));
        Assert.True(SvgAttributeEditor.Contains(source, "1/0"));
        Assert.False(SvgAttributeEditor.Contains(source, "9"));
        Assert.False(SvgAttributeEditor.Contains(source, "nonsense"));
        Assert.False(SvgAttributeEditor.Contains(source, "-1"));
    }

    [Fact]
    public void The_Attributes_Come_Back_In_The_Order_They_Were_Written()
    {
        var attributes = SvgAttributeEditor.Attributes(Read(), "0");

        Assert.Equal(new[] { "x", "y", "fill" }, attributes.Select(attribute => attribute.Name));

        // The expression as written, not the placeholder a parser would put in its place.
        Assert.Equal("{{ tint }}", attributes.Single(attribute => attribute.Name == "fill").Value);
    }

    [Fact]
    public void A_Namespace_Declaration_Is_Not_An_Attribute_Anybody_Edits()
    {
        Assert.Empty(SvgAttributeEditor.Attributes(Read(), string.Empty));
    }

    [Fact]
    public void An_Address_That_Names_Nothing_Has_No_Attributes()
    {
        Assert.Empty(SvgAttributeEditor.Attributes(Read(), "9"));
    }

    [Fact]
    public void A_Value_Is_Written_Where_The_Old_One_Stood()
    {
        var source = Read();

        Assert.Null(SvgAttributeEditor.SetAttribute(source, "0", "fill", "#c0392b"));

        // The apostrophes and the tight close are the element's, not this edit's to change.
        Assert.Contains("""<rect x='1' y="2" fill="#c0392b"/>""", source.ToText());
    }

    [Fact]
    public void A_Null_Value_Takes_The_Attribute_Away()
    {
        var source = Read();

        Assert.Null(SvgAttributeEditor.SetAttribute(source, "0", "y", null));

        Assert.Contains("""<rect x='1' fill="{{ tint }}"/>""", source.ToText());
    }

    [Fact]
    public void An_Attribute_That_Was_Not_There_Is_Added()
    {
        var source = Read();

        Assert.Null(SvgAttributeEditor.SetAttribute(source, "0", "ry", "3"));

        Assert.Contains("""<rect x='1' y="2" fill="{{ tint }}" ry="3"/>""", source.ToText());
    }

    [Fact]
    public void An_Edit_Has_To_Name_An_Attribute()
    {
        var source = Read();

        Assert.Equal("An edit has to name an attribute.", SvgAttributeEditor.SetAttribute(source, "0", "  ", "1"));
        Assert.Equal(Source, source.ToText());
    }

    [Theory]
    [InlineData("9")]
    [InlineData("0/9")]
    [InlineData("nonsense")]
    [InlineData("-1")]
    public void An_Address_That_Names_Nothing_Is_Refused(string addressKey)
    {
        var source = Read();

        Assert.Equal(
            "That element is no longer in the drawing.",
            SvgAttributeEditor.SetAttribute(source, addressKey, "fill", "red"));

        Assert.Equal(Source, source.ToText());
    }

    [Fact]
    public void A_Style_Declaration_Wins_Over_The_Attribute_And_Says_So()
    {
        var source = Read();

        Assert.Equal(
            "'fill' is set in this element's style attribute, which wins over the attribute. Change it there instead.",
            SvgAttributeEditor.SetAttribute(source, "1/0", "fill", "blue"));

        Assert.Equal(Source, source.ToText());
    }

    [Fact]
    public void A_Name_XML_Will_Not_Take_Is_Refused_Rather_Than_Written()
    {
        // The one place the tree is stricter than the span it replaces: splicing text would have
        // written this and left a drawing that no longer reads back.
        var source = Read();

        Assert.Equal("'a b' is not a name an attribute can have.", SvgAttributeEditor.SetAttribute(source, "0", "a b", "1"));
        Assert.Equal(Source, source.ToText());
    }

    [Fact]
    public void A_Prefixed_Attribute_Is_Named_By_Its_Prefix_And_Written_As_Itself()
    {
        // Reporting xlink:href as href is how an editor comes to write both: the panel shows the
        // short name, the write finds nothing under it, and the drawing ends up pointing two ways.
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
              <use xlink:href="#a" x="1" />
            </svg>
            """;

        var source = Read(svgText);

        Assert.Equal(new[] { "xlink:href", "x" }, SvgAttributeEditor.Attributes(source, "0").Select(a => a.Name));
        Assert.Null(SvgAttributeEditor.SetAttribute(source, "0", "xlink:href", "#b"));

        var written = source.ToText();

        Assert.Contains("""<use xlink:href="#b" x="1" />""", written);
        Assert.DoesNotContain(" href=", written);
    }

    [Fact]
    public void A_Prefix_The_Drawing_Does_Not_Declare_Is_Refused()
    {
        var source = Read();

        Assert.Equal(
            "This drawing does not say what 'bogus' stands for, so 'bogus:thing' cannot be written.",
            SvgAttributeEditor.SetAttribute(source, "0", "bogus:thing", "1"));

        Assert.Equal(Source, source.ToText());
    }

    [Theory]
    [InlineData("xmlns")]
    [InlineData("xmlns:e")]
    public void A_Namespace_Declaration_Is_Not_Edited_Here(string name)
    {
        // Taking the root's xmlns away leaves every name under it standing for something else, and
        // nothing about the file would look wrong.
        var source = Read();

        Assert.Equal("A namespace declaration is not something to edit here.", SvgAttributeEditor.SetAttribute(source, string.Empty, name, null));
        Assert.Equal(Source, source.ToText());
    }

    [Fact]
    public void Setting_A_Value_To_The_One_It_Already_Has_Changes_Nothing()
    {
        var source = Read();

        Assert.Null(SvgAttributeEditor.SetAttribute(source, "0", "y", "2"));
        Assert.Equal(Source, source.ToText());
    }

    [Fact]
    public void Taking_Away_An_Attribute_That_Was_Never_There_Changes_Nothing()
    {
        var source = Read();

        Assert.Null(SvgAttributeEditor.SetAttribute(source, "0", "ry", null));
        Assert.Equal(Source, source.ToText());
    }

    // ---- the text between an element's tags ----

    [Fact]
    public void An_Elements_Text_Is_What_Is_Written_Between_Its_Tags()
    {
        var source = Read(Texts);

        Assert.Equal("hi", SvgAttributeEditor.Content(source, "0", out var why));
        Assert.Null(why);

        // Written as <text/>: it holds no text, which is not the same as having none to hold.
        Assert.Equal(string.Empty, SvgAttributeEditor.Content(source, "1", out why));
        Assert.Null(why);
    }

    [Fact]
    public void An_Element_With_No_Text_Of_Its_Own_Says_So_Or_Says_Nothing()
    {
        var source = Read(Texts);

        // Split across children: the row cannot stand for what is on screen, and says why.
        Assert.Null(SvgAttributeEditor.Content(source, "2", out var why));
        Assert.Equal(
            "This element's text is written in its <tspan> children, so it cannot be edited as one value. "
            + "Pick a child row instead.",
            why);

        // A <tref> looks like a text element and is not, so it is told why.
        Assert.Null(SvgAttributeEditor.Content(source, "4", out why));
        Assert.Equal("A <tref> takes its text from what its href names, so it has none of its own to edit.", why);

        // A shape is not: saying "a rectangle has no text" under every shape in a drawing is noise.
        Assert.Null(SvgAttributeEditor.Content(source, "5", out why));
        Assert.Null(why);

        // And an address naming nothing is nothing to say anything about.
        Assert.Null(SvgAttributeEditor.Content(source, "9", out why));
        Assert.Null(why);
    }

    [Fact]
    public void Writing_Text_Changes_The_Run_And_Nothing_Around_It()
    {
        var source = Read(Texts);

        Assert.Null(SvgAttributeEditor.SetContent(source, "0", "there"));

        var written = source.ToText();

        Assert.Contains("<text x=\"4\">there</text>", written, StringComparison.Ordinal);

        // Every other line as it was, quoting and self-closing included.
        Assert.Contains("<text x=\"5\"/>", written, StringComparison.Ordinal);
        Assert.Contains("<rect x=\"8\"/>", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// The trap the obvious implementation falls into.
    /// </summary>
    /// <remarks>
    /// <c>XElement.Value</c> is the property anybody reaches for, and setting it removes every child
    /// node on the way to installing one of its own — so a comment beside the words would be dropped
    /// by an edit that had no business touching it.
    /// </remarks>
    [Fact]
    public void A_Comment_Beside_The_Words_Survives_An_Edit()
    {
        var source = Read(Texts);

        Assert.Null(SvgAttributeEditor.SetContent(source, "3", "bye"));

        Assert.Contains("<text x=\"7\"><!-- kept -->bye</text>", source.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_Element_Given_Text_And_Emptied_Again_Is_As_It_Was()
    {
        var source = Read(Texts);

        Assert.Null(SvgAttributeEditor.SetContent(source, "1", "briefly"));
        Assert.Contains("<text x=\"5\">briefly</text>", source.ToText(), StringComparison.Ordinal);

        Assert.Null(SvgAttributeEditor.SetContent(source, "1", null));

        // Byte for byte, self-closing tag and all.
        Assert.Equal(Texts, source.ToText());
    }

    [Fact]
    public void Text_Cannot_Be_Written_Where_There_Is_None_To_Write()
    {
        var source = Read(Texts);

        Assert.Equal(
            "A <tref> takes its text from what its href names, so it has none of its own to edit.",
            SvgAttributeEditor.SetContent(source, "4", "no"));

        Assert.Equal(
            "Only a <text>, a <tspan> or a <textPath> has text of its own to edit.",
            SvgAttributeEditor.SetContent(source, "5", "no"));

        Assert.Equal("That element is no longer in the drawing.", SvgAttributeEditor.SetContent(source, "9", "no"));

        Assert.StartsWith("This element's text is written in its <tspan> children", SvgAttributeEditor.SetContent(source, "2", "no"), StringComparison.Ordinal);

        Assert.Equal(Texts, source.ToText());
    }

    /// <summary>A space is a value where the document says it is.</summary>
    [Fact]
    public void Text_Is_Not_Trimmed()
    {
        var source = Read(Texts);

        Assert.Null(SvgAttributeEditor.SetContent(source, "0", "  spaced  "));

        Assert.Contains("<text x=\"4\">  spaced  </text>", source.ToText(), StringComparison.Ordinal);
    }
}
