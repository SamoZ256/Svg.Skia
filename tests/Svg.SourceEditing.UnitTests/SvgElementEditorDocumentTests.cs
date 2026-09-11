using System.Linq;
using Svg.SourceEditing;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// Moving an element, and making a group to move things into, on the tree.
/// </summary>
/// <remarks>
/// The refusals that are about what a drawing means are carried over word for word. Three that were
/// about text are gone, and each is a widening rather than a slip: an element that shares its line
/// can now be moved, a target that closes itself is opened by the writer when it gains a child, and
/// a splice into a tree cannot leave markup nobody can read.
/// </remarks>
public class SvgElementEditorDocumentTests
{
    private const string Source = """
        <svg xmlns="http://www.w3.org/2000/svg">
          <rect id="a" />
          <g id="b">
            <circle id="c" />
          </g>
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
    public void An_Element_Dropped_Inside_A_Group_Is_Written_At_Its_New_Depth()
    {
        var source = Read();

        Assert.Null(SvgElementEditor.Move(source, "0", "1", SvgElementDrop.Inside));

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g id="b">
                <circle id="c" />
                <rect id="a" />
              </g>
            </svg>
            """,
            source.ToText());
    }

    [Fact]
    public void An_Element_Dropped_Before_A_Row_Lands_Above_It()
    {
        var source = Read();

        Assert.Null(SvgElementEditor.Move(source, "1", "0", SvgElementDrop.Before));

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g id="b">
                <circle id="c" />
              </g>
              <rect id="a" />
            </svg>
            """,
            source.ToText());
    }

    [Fact]
    public void An_Element_Taken_Out_Of_A_Group_Is_Written_Less_Deep()
    {
        var source = Read();

        Assert.Null(SvgElementEditor.Move(source, "1/0", "0", SvgElementDrop.After));

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect id="a" />
              <circle id="c" />
              <g id="b">
              </g>
            </svg>
            """,
            source.ToText());
    }

    [Fact]
    public void What_Is_Moved_Carries_Its_Children_And_Its_Comments()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g id="a">
                <!-- why this is here -->
                <rect />
              </g>
              <g id="b" />
            </svg>
            """;

        var source = Read(svgText);

        Assert.Null(SvgElementEditor.Move(source, "0", "1", SvgElementDrop.Inside));

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g id="b">
                <g id="a">
                  <!-- why this is here -->
                  <rect />
                </g>
              </g>
            </svg>
            """,
            source.ToText());
    }

    [Fact]
    public void A_Drawing_Written_With_Tabs_Goes_On_Being_Written_With_Tabs()
    {
        const string svgText = "<svg xmlns=\"http://www.w3.org/2000/svg\">\n\t<rect id=\"a\" />\n\t<g id=\"b\">\n\t</g>\n</svg>";

        var source = Read(svgText);

        Assert.Null(SvgElementEditor.Move(source, "0", "1", SvgElementDrop.Inside));

        Assert.Equal(
            "<svg xmlns=\"http://www.w3.org/2000/svg\">\n\t<g id=\"b\">\n\t\t<rect id=\"a\" />\n\t</g>\n</svg>",
            source.ToText());
    }

    [Fact]
    public void A_File_Written_With_Carriage_Returns_Keeps_Them_Through_A_Move()
    {
        // The trap the tree hides: a reader folds every ending to a newline before the tree sees
        // one, so a break built with a carriage return would be written back as &#xD;.
        const string svgText = "<svg xmlns=\"http://www.w3.org/2000/svg\">\r\n  <rect id=\"a\" />\r\n  <g id=\"b\">\r\n  </g>\r\n</svg>";

        var source = Read(svgText);

        Assert.Null(SvgElementEditor.Move(source, "0", "1", SvgElementDrop.Inside));

        var written = source.ToText();

        Assert.DoesNotContain("&#xD;", written);
        Assert.Equal(
            "<svg xmlns=\"http://www.w3.org/2000/svg\">\r\n  <g id=\"b\">\r\n    <rect id=\"a\" />\r\n  </g>\r\n</svg>",
            written);
    }

    [Fact]
    public void A_Blank_Line_Above_What_Moved_Stays_Where_It_Was_Put()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g id="b" />

              <rect id="a" />
            </svg>
            """;

        var source = Read(svgText);

        Assert.Null(SvgElementEditor.Move(source, "1", "0", SvgElementDrop.Inside));

        Assert.Contains("\n  <g id=\"b\">\n    <rect id=\"a\" />\n  </g>\n\n</svg>", source.ToText());
    }

    [Fact]
    public void An_Element_Sharing_Its_Line_Can_Now_Be_Moved()
    {
        // The span editor refused this, because there was no line to take. A minified drawing is
        // now something the tree can rearrange.
        const string svgText = """<svg xmlns="http://www.w3.org/2000/svg"><rect id="a" /><g id="b" /></svg>""";

        var source = Read(svgText);

        Assert.Null(SvgElementEditor.Move(source, "0", "1", SvgElementDrop.Inside));
        Assert.Contains("""<g id="b">""", source.ToText());
        Assert.Contains("""<rect id="a" />""", source.ToText());
    }

    [Fact]
    public void A_Target_That_Closes_Itself_Is_Opened_By_What_Is_Put_In_It()
    {
        var source = Read();

        Assert.Null(SvgElementEditor.Move(source, "1/0", "0", SvgElementDrop.Inside));

        Assert.Contains("""<rect id="a">""", source.ToText());
        Assert.Contains("</rect>", source.ToText());
    }

    [Fact]
    public void The_Drawing_Itself_Cannot_Be_Moved()
    {
        var source = Read();

        Assert.Equal("The drawing itself cannot be moved.", SvgElementEditor.Move(source, string.Empty, "0", SvgElementDrop.After));
        Assert.Equal(Source, source.ToText());
    }

    [Fact]
    public void An_Element_Cannot_Be_Put_Inside_Itself()
    {
        var source = Read();

        Assert.Equal("<g> cannot be put inside itself.", SvgElementEditor.Move(source, "1", "1/0", SvgElementDrop.After));
        Assert.Equal(Source, source.ToText());
    }

    [Fact]
    public void An_Address_That_Names_Nothing_Is_Refused()
    {
        var source = Read();

        Assert.Equal("One of those is not in this drawing any more.", SvgElementEditor.Move(source, "9", "0", SvgElementDrop.After));
        Assert.Equal("One of those is not in this drawing any more.", SvgElementEditor.Move(source, "0", "9", SvgElementDrop.After));
        Assert.Equal(Source, source.ToText());
    }

    [Fact]
    public void Moving_Across_A_Defs_Is_Refused()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <rect id="a" />
              </defs>
              <g id="b" />
            </svg>
            """;

        var source = Read(svgText);

        Assert.Equal(
            "That would move it across a <defs>, a <clipPath> or a <mask>, which changes what the drawing paints.",
            SvgElementEditor.Move(source, "0/0", "1", SvgElementDrop.Inside));

        Assert.Equal(svgText, source.ToText());
    }

    [Fact]
    public void A_Parent_That_Cannot_Hold_A_Group_Says_So()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <text id="t">hello</text>
              <rect id="a" />
            </svg>
            """;

        var source = Read(svgText);

        Assert.Equal(
            "A <text> cannot hold a <g>, so what is written in one cannot be grouped.",
            SvgElementEditor.Move(source, "1", "0", SvgElementDrop.Inside));

        Assert.Equal(svgText, source.ToText());
    }

    [Fact]
    public void A_New_Group_Is_Written_As_A_Pair_Of_Tags()
    {
        var source = Read();

        Assert.Null(SvgElementEditor.NewGroup(source, "0", SvgElementDrop.After));

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect id="a" />
              <g>
              </g>
              <g id="b">
                <circle id="c" />
              </g>
            </svg>
            """,
            source.ToText());
    }

    [Fact]
    public void A_New_Group_Beside_The_Drawing_Itself_Goes_Inside_It()
    {
        var source = Read();

        Assert.Null(SvgElementEditor.NewGroup(source, string.Empty, SvgElementDrop.After));

        Assert.Contains("""
              <g>
              </g>
            </svg>
            """, source.ToText());
    }

    [Fact]
    public void A_New_Group_Takes_The_Drawings_Own_Namespace()
    {
        // A bare name would write <g> into a file whose tree holds it in no namespace, so the two
        // would disagree and every later lookup by namespace would miss it.
        var source = Read();

        Assert.Null(SvgElementEditor.NewGroup(source, "0", SvgElementDrop.After));

        Assert.Equal(
            "http://www.w3.org/2000/svg",
            SvgSourceDocument.Read(source.ToText(), out _)!.Document.Root!.Elements().ElementAt(1).Name.NamespaceName);
    }
}
