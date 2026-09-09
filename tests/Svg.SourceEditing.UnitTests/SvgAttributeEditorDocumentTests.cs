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
}
