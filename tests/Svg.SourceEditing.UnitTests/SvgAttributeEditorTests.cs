using Svg.SourceEditing;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// Writing one attribute of one element, as a span, so that everything else about the file survives
/// a value being changed.
/// </summary>
/// <remarks>
/// The address is asserted as much as the edit. This side counts <c>XElement</c>s and the SVG parser
/// counts <c>SvgElement.Children</c>; the two have to spell the same path or a caller holding a
/// parsed element aims at somebody else's attribute.
/// </remarks>
public class SvgAttributeEditorTests
{
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
          <!-- what it paints -->
          <g id="wrap">
            <rect width="24" height="24" fill="#00ff00" />
            <circle cx="12" cy="12" r="6" />
          </g>
        </svg>
        """;

    private static string Apply(string svgText, SvgSourceEditResult result)
    {
        Assert.True(result.Succeeded, result.Refusal);

        return SvgTextEdit.ApplyAll(svgText, result.Edits);
    }

    [Fact]
    public void A_Value_Is_Replaced_Where_It_Stands()
    {
        var rewritten = Apply(Drawing, SvgAttributeEditor.SetAttribute(Drawing, "0/0", "fill", "{{ tint }}"));

        Assert.Contains("""<rect width="24" height="24" fill="{{ tint }}" />""", rewritten);

        // And the rest of the file is what it was, comment and layout included.
        Assert.Contains("<!-- what it paints -->", rewritten);
        Assert.Contains("""<circle cx="12" cy="12" r="6" />""", rewritten);
        Assert.Contains("""<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">""", rewritten);
    }

    [Fact]
    public void An_Attribute_The_Element_Lacks_Joins_The_Others()
    {
        var rewritten = Apply(Drawing, SvgAttributeEditor.SetAttribute(Drawing, "0/1", "opacity", "{{ fade }}"));

        Assert.Contains("""<circle cx="12" cy="12" r="6" opacity="{{ fade }}" />""", rewritten);
    }

    /// <summary>An element with nothing on it has nowhere to measure a first attribute from.</summary>
    [Fact]
    public void An_Element_With_No_Attributes_Takes_One()
    {
        const string bare = """<svg xmlns="http://www.w3.org/2000/svg"><rect /></svg>""";

        Assert.Equal(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect fill="{{ tint }}" /></svg>""",
            Apply(bare, SvgAttributeEditor.SetAttribute(bare, "0", "fill", "{{ tint }}")));
    }

    [Fact]
    public void A_Null_Takes_The_Attribute_Away()
    {
        var rewritten = Apply(Drawing, SvgAttributeEditor.SetAttribute(Drawing, "0/0", "fill", null));

        Assert.Contains("""<rect width="24" height="24" />""", rewritten);
    }

    [Fact]
    public void Saying_What_It_Already_Says_Is_No_Edit()
    {
        var result = SvgAttributeEditor.SetAttribute(Drawing, "0/0", "fill", "#00ff00");

        Assert.True(result.Succeeded);
        Assert.Empty(result.Edits);
    }

    [Fact]
    public void Taking_Away_What_Is_Not_There_Is_No_Edit()
    {
        var result = SvgAttributeEditor.SetAttribute(Drawing, "0/0", "opacity", null);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Edits);
    }

    /// <summary>
    /// A style declaration wins over the attribute under it, so writing the attribute would leave a
    /// document where the change paints nothing and nothing said so.
    /// </summary>
    [Fact]
    public void An_Attribute_A_Style_Declaration_Shadows_Is_Refused()
    {
        const string styled = """<svg xmlns="http://www.w3.org/2000/svg"><rect fill="#00ff00" style="fill:#ff0000" /></svg>""";

        var result = SvgAttributeEditor.SetAttribute(styled, "0", "fill", "{{ tint }}");

        Assert.False(result.Succeeded);
        Assert.Contains("style attribute", result.Refusal);

        // And one the style says nothing about is written as usual.
        Assert.Contains("opacity=\"0.5\"", Apply(styled, SvgAttributeEditor.SetAttribute(styled, "0", "opacity", "0.5")));
    }

    [Theory]
    [InlineData("9")]
    [InlineData("0/9")]
    [InlineData("nonsense")]
    [InlineData("-1")]
    public void An_Address_That_Names_Nothing_Is_Refused(string addressKey)
    {
        var result = SvgAttributeEditor.SetAttribute(Drawing, addressKey, "fill", "{{ tint }}");

        Assert.False(result.Succeeded);
        Assert.Contains("no longer in the drawing", result.Refusal);
    }

    [Fact]
    public void The_Root_Is_The_Empty_Address()
    {
        Assert.Contains(
            """<svg xmlns="http://www.w3.org/2000/svg" width="48" height="24">""",
            Apply(Drawing, SvgAttributeEditor.SetAttribute(Drawing, string.Empty, "width", "48")));
    }

    [Fact]
    public void A_Document_That_Will_Not_Read_Is_Refused()
    {
        var result = SvgAttributeEditor.SetAttribute("<svg><rect", "0", "fill", "red");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Refusal);
    }

    [Fact]
    public void An_Edit_Has_To_Name_An_Attribute()
    {
        Assert.Contains("name an attribute", SvgAttributeEditor.SetAttribute(Drawing, "0", "  ", "red").Refusal);
    }

    /// <summary>
    /// A fault in what the document declares is not this edit's business.
    /// </summary>
    /// <remarks>
    /// The rule <c>SvgFrameEditor</c> states: an edit elsewhere in the document has nothing to do
    /// with a broken block and is refused for no reason.
    /// </remarks>
    [Fact]
    public void A_Broken_Declaration_Block_Does_Not_Stop_It()
    {
        const string broken = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code><e:param name="hue" /></e:code></defs>
              <rect fill="#00ff00" />
            </svg>
            """;

        Assert.Contains("fill=\"red\"", Apply(broken, SvgAttributeEditor.SetAttribute(broken, "1", "fill", "red")));
    }

    [Fact]
    public void A_Value_Is_Escaped_As_Markup()
    {
        var rewritten = Apply(Drawing, SvgAttributeEditor.SetAttribute(Drawing, "0/0", "fill", """{{ a < b ? "x" : "y" }}"""));

        Assert.Contains("&lt;", rewritten);
        Assert.Contains("&quot;", rewritten);
    }
}
