using System;
using System.Linq;
using System.Xml.Linq;
using Svg.SourceEditing;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// Moving, adding, removing and copying elements on the tree.
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
            "A <text> cannot hold a <rect>.",
            SvgElementEditor.Move(source, "1", "0", SvgElementDrop.Inside));

        Assert.Equal(svgText, source.ToText());
    }

    // ---- insert -------------------------------------------------------------------------------

    private static XElement Group() => new("g", new XText("\n"));

    [Fact]
    public void A_New_Group_Is_Written_As_A_Pair_Of_Tags()
    {
        var source = Read();

        Assert.Null(SvgElementEditor.Insert(source, "0", SvgElementDrop.After, Group(), out var key));
        Assert.Equal("1", key);

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

        Assert.Null(SvgElementEditor.Insert(source, string.Empty, SvgElementDrop.After, Group(), out var key));
        Assert.Equal("2", key);

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

        Assert.Null(SvgElementEditor.Insert(source, "0", SvgElementDrop.After, Group(), out _));

        Assert.Equal(
            "http://www.w3.org/2000/svg",
            SvgSourceDocument.Read(source.ToText(), out _)!.Document.Root!.Elements().ElementAt(1).Name.NamespaceName);
        Assert.DoesNotContain("xmlns=\"\"", source.ToText());
    }

    [Fact]
    public void A_Shape_Is_Written_Inside_A_Group_Before_Its_Closing_Tag()
    {
        var source = Read();

        Assert.Null(SvgElementEditor.Insert(
            source, "1", SvgElementDrop.Inside, new XElement("rect", new XAttribute("width", "10")), out var key));
        Assert.Equal("1/1", key);

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect id="a" />
              <g id="b">
                <circle id="c" />
                <rect width="10" />
              </g>
            </svg>
            """,
            source.ToText());
    }

    [Fact]
    public void A_Shape_Put_Inside_An_Element_That_Closes_Itself_Opens_It()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g id="b" />
            </svg>
            """;

        var source = Read(svgText);

        Assert.Null(SvgElementEditor.Insert(source, "0", SvgElementDrop.Inside, new XElement("circle"), out var key));
        Assert.Equal("0/0", key);

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g id="b">
                <circle />
              </g>
            </svg>
            """,
            source.ToText());
    }

    [Fact]
    public void A_Shape_Cannot_Be_Put_Inside_A_Text()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <text id="t">hello</text>
            </svg>
            """;

        var source = Read(svgText);

        Assert.Equal(
            "A <text> cannot hold a <rect>.",
            SvgElementEditor.Insert(source, "0", SvgElementDrop.Inside, new XElement("rect"), out var key));
        Assert.Null(key);
        Assert.Equal(svgText, source.ToText());
    }

    // ---- remove -------------------------------------------------------------------------------

    private const string Expressed = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
          <defs>
            <e:code>
              <e:param name="size" value="10" />
            </e:code>
          </defs>
          <rect id="a" width="{{ size * 2 }}" />

          <g id="b">
            <circle id="c" r="{{ size }}" />
            <circle id="d" />
          </g>
          <text id="t">hel&amp;lo <tspan id="s">there</tspan></text>
        </svg>
        """;

    [Fact]
    public void A_Removed_Element_Takes_Its_Line_With_It_And_Leaves_The_Blank_Line_Above()
    {
        var source = Read(Expressed);

        Assert.Null(SvgElementEditor.Remove(source, new[] { "2" }));

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs>
                <e:code>
                  <e:param name="size" value="10" />
                </e:code>
              </defs>
              <rect id="a" width="{{ size * 2 }}" />

              <text id="t">hel&amp;lo <tspan id="s">there</tspan></text>
            </svg>
            """,
            source.ToText());
    }

    [Fact]
    public void Removing_A_Parent_With_Its_Child_Removes_The_Parent_Once()
    {
        var source = Read(Expressed);

        Assert.Null(SvgElementEditor.Remove(source, new[] { "2/0", "2", "1" }));

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs>
                <e:code>
                  <e:param name="size" value="10" />
                </e:code>
              </defs>

              <text id="t">hel&amp;lo <tspan id="s">there</tspan></text>
            </svg>
            """,
            source.ToText());
    }

    [Fact]
    public void The_Drawing_The_Declarations_And_What_Is_Not_There_Cannot_Be_Removed()
    {
        var source = Read(Expressed);

        Assert.Equal("The drawing itself cannot be deleted.", SvgElementEditor.Remove(source, new[] { "1", string.Empty }));
        Assert.Equal(
            "The declarations cannot be deleted here: the Parameters panel edits them.",
            SvgElementEditor.Remove(source, new[] { "0" }));
        Assert.Equal(
            "The declarations cannot be deleted here: the Parameters panel edits them.",
            SvgElementEditor.Remove(source, new[] { "0/0" }));
        Assert.Equal("That is not in this drawing any more.", SvgElementEditor.Remove(source, new[] { "1", "9" }));

        Assert.Equal(Expressed, source.ToText());
    }

    // ---- duplicate ----------------------------------------------------------------------------

    [Fact]
    public void A_Duplicate_Is_Written_After_The_Original_With_A_Free_Id()
    {
        var source = Read(Expressed);

        Assert.Null(SvgElementEditor.Duplicate(source, new[] { "1" }, out var copies));
        Assert.Equal(new[] { "2" }, copies);

        Assert.Contains(
            """
              <rect id="a" width="{{ size * 2 }}" />
              <rect id="a-2" width="{{ size * 2 }}" />

              <g id="b">
            """,
            source.ToText());
    }

    [Fact]
    public void A_Duplicated_Group_Keeps_Its_Bytes_And_Renames_What_Is_Inside_It()
    {
        var source = Read(Expressed);

        Assert.Null(SvgElementEditor.Duplicate(source, new[] { "2" }, out var copies));
        Assert.Equal(new[] { "3" }, copies);

        Assert.Contains(
            """
              <g id="b">
                <circle id="c" r="{{ size }}" />
                <circle id="d" />
              </g>
              <g id="b-2">
                <circle id="c-2" r="{{ size }}" />
                <circle id="d-2" />
              </g>
              <text id="t">hel&amp;lo <tspan id="s">there</tspan></text>
            """,
            source.ToText());
    }

    [Fact]
    public void A_Duplicated_Text_Keeps_Its_Entities()
    {
        var source = Read(Expressed);

        Assert.Null(SvgElementEditor.Duplicate(source, new[] { "3" }, out _));

        Assert.Contains(
            """
              <text id="t">hel&amp;lo <tspan id="s">there</tspan></text>
              <text id="t-2">hel&amp;lo <tspan id="s-2">there</tspan></text>
            """,
            source.ToText());
    }

    [Fact]
    public void Ids_Count_Up_Past_The_Ones_Taken_And_An_Expression_Id_Is_Left_Alone()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect id="a" />
              <rect id="a-2" />
              <rect id="{{ name }}" />
            </svg>
            """;

        var source = Read(svgText);

        Assert.Null(SvgElementEditor.Duplicate(source, new[] { "0", "2" }, out var copies));
        Assert.Equal(new[] { "1", "4" }, copies);

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect id="a" />
              <rect id="a-3" />
              <rect id="a-2" />
              <rect id="{{ name }}" />
              <rect id="{{ name }}" />
            </svg>
            """,
            source.ToText());
    }

    [Fact]
    public void Part_Of_A_Text_And_The_Declarations_Cannot_Be_Duplicated()
    {
        var source = Read(Expressed);

        Assert.Equal(
            "What a <text> holds is one run of text, so a part of it cannot be duplicated.",
            SvgElementEditor.Duplicate(source, new[] { "3/0" }, out var copies));
        Assert.Empty(copies);
        Assert.Equal(
            "The declarations cannot be duplicated here: the Parameters panel edits them.",
            SvgElementEditor.Duplicate(source, new[] { "0" }, out _));
        Assert.Equal("The drawing itself cannot be duplicated.", SvgElementEditor.Duplicate(source, new[] { string.Empty }, out _));

        Assert.Equal(Expressed, source.ToText());
    }

    // ---- clip ---------------------------------------------------------------------------------

    private const string Clipped = """
        <svg xmlns="http://www.w3.org/2000/svg">
          <defs>
            <clipPath id="window">
              <rect width="10" height="10" />
            </clipPath>
            <mask id="sweep">
              <rect width="10" height="10" fill="white" />
            </mask>
            <circle id="spare" r="5" />
            <linearGradient id="fade">
              <stop offset="0" />
            </linearGradient>
          </defs>
          <rect id="a" width="20" height="20" />
          <g id="pair">
            <rect id="b" width="5" height="5" />
          </g>
        </svg>
        """;

    [Fact]
    public void A_Clip_Path_In_Defs_Is_Pointed_At_And_Left_Where_It_Is()
    {
        var source = Read(Clipped);

        Assert.Null(SvgElementEditor.Clip(source, "1", "clip-path", "0/0", null, out var held));
        Assert.Equal("1", held);

        Assert.Equal(
            Clipped.Replace("""<rect id="a" width="20" height="20" />""", """<rect id="a" width="20" height="20" clip-path="url(#window)" />"""),
            source.ToText());
    }

    [Fact]
    public void A_Clip_Path_With_No_Id_Is_Given_A_Free_One()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <clipPath>
                  <rect width="10" height="10" />
                </clipPath>
              </defs>
              <rect id="clip" />
            </svg>
            """;

        var source = Read(svgText);

        Assert.Null(SvgElementEditor.Clip(source, "1", "clip-path", "0/0", null, out _));

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <clipPath id="clip-2">
                  <rect width="10" height="10" />
                </clipPath>
              </defs>
              <rect id="clip" clip-path="url(#clip-2)" />
            </svg>
            """,
            source.ToText());
    }

    [Fact]
    public void A_Shape_In_Defs_Becomes_A_Clip_Path_Where_It_Was()
    {
        var source = Read(Clipped);

        Assert.Null(SvgElementEditor.Clip(source, "1", "clip-path", "0/2", null, out var held));
        Assert.Equal("0/2/0", held);

        Assert.Contains(
            """
                </mask>
                <clipPath id="a-clip">
                  <circle id="spare" r="5" />
                </clipPath>
                <linearGradient id="fade">
            """,
            source.ToText());
        Assert.Contains("""<rect id="a" width="20" height="20" clip-path="url(#a-clip)" />""", source.ToText());
    }

    [Fact]
    public void An_Element_Clipped_Again_Points_At_The_New_One_And_The_Old_One_Stays()
    {
        var source = Read(Clipped);

        Assert.Null(SvgElementEditor.Clip(source, "1", "clip-path", "0/0", null, out _));
        Assert.Null(SvgElementEditor.Clip(source, "1", "clip-path", "0/2", null, out _));

        Assert.Equal("url(#a-clip)", SvgAttributeEditor.Attribute(source, "1", "clip-path"));
        Assert.Contains("""<clipPath id="window">""", source.ToText());
    }

    [Fact]
    public void A_Shape_Made_Into_A_Mask_Masks_By_Where_It_Is_Drawn()
    {
        var source = Read(Clipped);

        Assert.Null(SvgElementEditor.Clip(source, "1", "mask", "2/0", null, out var held));
        Assert.Equal("0/4/0", held);

        Assert.Contains(
            """
                </linearGradient>
                <mask id="a-mask" mask-type="alpha">
                  <rect id="b" width="5" height="5" />
                </mask>
              </defs>
              <rect id="a" width="20" height="20" mask="url(#a-mask)" />
              <g id="pair">
              </g>
            """,
            source.ToText());
    }

    [Fact]
    public void A_Shape_Something_Else_Points_At_Is_Copied_Rather_Than_Moved()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
              <defs>
                <path id="p" d="M0 0 L10 0 L10 10 Z" />
                <g id="icon">
                  <circle id="dot" r="2" />
                </g>
              </defs>
              <use xlink:href="#p" />
              <use href="#icon" />
              <rect id="a" width="20" height="20" />
            </svg>
            """;

        var source = Read(svgText);

        Assert.Null(SvgElementEditor.Clip(source, "3", "clip-path", "0/0", null, out var held));
        Assert.Equal("0/1/0", held);

        Assert.Contains(
            """
                <path id="p" d="M0 0 L10 0 L10 10 Z" />
                <clipPath id="a-clip">
                  <path id="p-2" d="M0 0 L10 0 L10 10 Z" />
                </clipPath>
            """,
            source.ToText());

        // Drawn through the group around it, which a <use> names: taking it out would change the use.
        Assert.Null(SvgElementEditor.Clip(source, "3", "mask", "0/2/0", null, out held));
        Assert.Equal("0/2/1/0", held);

        Assert.Contains(
            """
                  <circle id="dot" r="2" />
                  <mask id="a-mask" mask-type="alpha">
                    <circle id="dot-2" r="2" />
                  </mask>
            """,
            source.ToText());
    }

    [Fact]
    public void A_Carried_Transform_Goes_In_Front_Of_The_Shapes_Own()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect id="a" width="20" height="20" />
              <path id="p" d="M0 0 L10 0 L10 10 Z" transform="{{ turn }}" />
            </svg>
            """;

        var source = Read(svgText);

        Assert.Null(SvgElementEditor.Clip(source, "0", "clip-path", "1", "translate(5,5)", out var held));
        Assert.Equal("0/0/0", held);

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <clipPath id="a-clip">
                  <path id="p" d="M0 0 L10 0 L10 10 Z" transform="translate(5,5) {{ turn }}" />
                </clipPath>
              </defs>
              <rect id="a" width="20" height="20" clip-path="url(#a-clip)" />
            </svg>
            """,
            source.ToText());
    }

    [Fact]
    public void A_Carried_Transform_Is_Refused_Where_The_Shapes_Style_Sets_One()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect id="a" width="20" height="20" />
              <circle id="c" r="5" style="transform: rotate(45deg)" />
            </svg>
            """;

        var source = Read(svgText);

        Assert.Equal(
            "'transform' is set in this element's style attribute, which wins over the attribute. Change it there instead.",
            SvgElementEditor.Clip(source, "0", "clip-path", "1", "translate(5,5)", out var held));
        Assert.Null(held);
        Assert.Equal(svgText, source.ToText());

        Assert.Null(SvgElementEditor.Clip(source, "0", "clip-path", "1", null, out _));
    }

    [Fact]
    public void A_New_Mask_Makes_The_Defs_It_Needs_At_The_Top()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect id="a" width="20" height="20" />
            </svg>
            """;

        var source = Read(svgText);
        var seed = new XElement("path", new XAttribute("d", "M0 0 L20 0 L20 20 L0 20 Z"), new XAttribute("fill", "white"));

        Assert.Null(SvgElementEditor.Clip(source, "0", "mask", seed, out var held));
        Assert.Equal("0/0/0", held);

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <mask id="a-mask">
                  <path d="M0 0 L20 0 L20 20 L0 20 Z" fill="white" />
                </mask>
              </defs>
              <rect id="a" width="20" height="20" mask="url(#a-mask)" />
            </svg>
            """,
            source.ToText());
        Assert.All(
            SvgSourceDocument.Read(source.ToText(), out _)!.Document.Descendants(),
            element => Assert.Equal("http://www.w3.org/2000/svg", element.Name.NamespaceName));
    }

    [Fact]
    public void A_New_Clip_Path_Is_Named_After_What_It_Clips_Where_That_Has_A_Plain_Name()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <clipPath id="a-clip" />
              </defs>
              <rect id="a" />
              <rect />
              <rect id="{{ name }}" />
              <rect id="Frame 1" />
            </svg>
            """;

        var source = Read(svgText);

        foreach (var key in new[] { "1", "2", "3", "4" })
        {
            Assert.Null(SvgElementEditor.Clip(source, key, "clip-path", new XElement("path"), out _));
        }

        Assert.Equal("url(#a-clip-2)", SvgAttributeEditor.Attribute(source, "1", "clip-path"));
        Assert.Equal("url(#clip)", SvgAttributeEditor.Attribute(source, "2", "clip-path"));
        Assert.Equal("url(#clip-2)", SvgAttributeEditor.Attribute(source, "3", "clip-path"));

        // Figma names ids after layers, and a browser drops a url(#…) with a space in it.
        Assert.Equal("url(#clip-3)", SvgAttributeEditor.Attribute(source, "4", "clip-path"));
    }

    [Fact]
    public void An_Element_Cannot_Clip_Itself_Or_Be_Clipped_By_What_Holds_It()
    {
        var source = Read(Clipped);

        Assert.Equal("An element cannot clip itself.", SvgElementEditor.Clip(source, "1", "clip-path", "1", null, out var held));
        Assert.Null(held);
        Assert.Equal("A <g> holds the <rect> it would mask.", SvgElementEditor.Clip(source, "2/0", "mask", "2", null, out _));
        Assert.Equal(
            "A <clipPath> holds the <rect> it would clip.",
            SvgElementEditor.Clip(source, "0/0/0", "clip-path", "0/0", null, out _));

        Assert.Equal(Clipped, source.ToText());
    }

    [Fact]
    public void A_Clip_Path_Set_In_Style_Is_Refused_Before_Anything_Moves()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect id="a" style="clip-path: url(#old)" />
              <circle id="c" r="5" />
            </svg>
            """;

        var source = Read(svgText);
        const string refusal =
            "'clip-path' is set in this element's style attribute, which wins over the attribute. Change it there instead.";

        Assert.Equal(refusal, SvgElementEditor.Clip(source, "0", "clip-path", "1", null, out _));
        Assert.Equal(refusal, SvgElementEditor.Clip(source, "0", "clip-path", new XElement("path"), out _));
        Assert.Equal(svgText, source.ToText());
    }

    [Fact]
    public void A_Mask_Is_Not_A_Clip_Path_Nor_A_Clip_Path_A_Mask()
    {
        var source = Read(Clipped);

        Assert.Equal("A <mask> cannot be used as a clip path.", SvgElementEditor.Clip(source, "1", "clip-path", "0/1", null, out _));
        Assert.Equal("A <clipPath> cannot be used as a mask.", SvgElementEditor.Clip(source, "1", "mask", "0/0", null, out _));
        Assert.Throws<ArgumentException>(() => SvgElementEditor.Clip(source, "1", "filter", "0/2", null, out _));

        Assert.Equal(Clipped, source.ToText());
    }

    [Fact]
    public void Only_What_Is_Drawn_Clips_Or_Is_Clipped()
    {
        var source = Read(Clipped);

        Assert.Equal(
            "A <stop> is not drawn, so it cannot be clipped or masked.",
            SvgElementEditor.Clip(source, "0/3/0", "clip-path", "0/2", null, out _));
        Assert.Equal(
            "A <stop> is not drawn, so it cannot be clipped or masked.",
            SvgElementEditor.Clip(source, "0/3/0", "mask", new XElement("path"), out _));
        Assert.Equal(
            "A <linearGradient> draws nothing of its own, so it cannot be made into a mask.",
            SvgElementEditor.Clip(source, "1", "mask", "0/3", null, out _));
        Assert.Equal("One of those is not in this drawing any more.", SvgElementEditor.Clip(source, "1", "mask", "9", null, out _));
        Assert.Equal("That is not in this drawing any more.", SvgElementEditor.Clip(source, "9", "mask", new XElement("path"), out _));

        Assert.Equal(Clipped, source.ToText());
    }

    [Fact]
    public void Part_Of_A_Clip_Path_Cannot_Become_A_Clip_Path_Of_Its_Own()
    {
        var source = Read(Clipped);

        Assert.Equal(
            "That is part of a <clipPath>, so it cannot be made into a clip path of its own.",
            SvgElementEditor.Clip(source, "1", "clip-path", "0/0/0", null, out _));
        Assert.Equal(
            "That is part of a <linearGradient>, so it cannot be made into a mask of its own.",
            SvgElementEditor.Clip(source, "1", "mask", "0/3/0", null, out _));

        Assert.Equal(Clipped, source.ToText());
    }

    [Fact]
    public void A_Group_Can_Be_Made_Into_A_Mask_But_Not_A_Clip_Path()
    {
        var source = Read(Clipped);

        Assert.Equal("A <clipPath> cannot hold a <g>.", SvgElementEditor.Clip(source, "1", "clip-path", "2", null, out _));
        Assert.Equal("A <clipPath> cannot hold a <g>.", SvgElementEditor.Clip(source, "1", "clip-path", new XElement("g"), out _));
        Assert.Equal(Clipped, source.ToText());

        Assert.Null(SvgElementEditor.Clip(source, "1", "mask", "2", null, out var held));
        Assert.Equal("0/4/0", held);
        Assert.Contains("""<mask id="a-mask" mask-type="alpha">""", source.ToText());
    }

    [Fact]
    public void The_Drawing_The_Declarations_And_Part_Of_A_Text_Cannot_Be_Made_Into_A_Clip_Path()
    {
        var source = Read(Expressed);

        Assert.Equal(
            "The drawing itself cannot be made into a clip path.",
            SvgElementEditor.Clip(source, "1", "clip-path", string.Empty, null, out _));
        Assert.Equal(
            "The declarations cannot be made into a clip path here: the Parameters panel edits them.",
            SvgElementEditor.Clip(source, "1", "clip-path", "0", null, out _));
        Assert.Equal(
            "What a <text> holds is one run of text, so a part of it cannot be made into a mask.",
            SvgElementEditor.Clip(source, "1", "mask", "3/0", null, out _));

        Assert.Equal(Expressed, source.ToText());
    }

    [Fact]
    public void A_Clip_Path_Named_By_An_Expression_Cannot_Be_Pointed_At()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <clipPath id="{{ name }}" />
              </defs>
              <rect id="a" />
            </svg>
            """;

        var source = Read(svgText);

        Assert.Equal(
            "This <clipPath> is named by an expression, so nothing can point at it here.",
            SvgElementEditor.Clip(source, "1", "clip-path", "0/0", null, out _));
        Assert.Equal(svgText, source.ToText());
    }

    [Fact]
    public void Shapes_Can_Now_Be_Reordered_Inside_A_Clip_Path()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <clipPath id="window">
                  <rect id="r" />
                  <circle id="c" />
                </clipPath>
              </defs>
            </svg>
            """;

        var source = Read(svgText);

        Assert.Null(SvgElementEditor.Move(source, "0/0/1", "0/0/0", SvgElementDrop.Before));
        Assert.Null(SvgElementEditor.Insert(source, "0/0", SvgElementDrop.Inside, new XElement("path"), out var key));
        Assert.Equal("0/0/2", key);
        Assert.Equal(
            "A <clipPath> cannot hold a <g>.",
            SvgElementEditor.Insert(source, "0/0", SvgElementDrop.Inside, Group(), out _));

        Assert.Contains(
            """
                <clipPath id="window">
                  <circle id="c" />
                  <rect id="r" />
                  <path />
                </clipPath>
            """,
            source.ToText());
    }

    [Fact]
    public void A_Use_Clips_Only_Where_What_It_Draws_Is_A_Shape()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
              <defs>
                <symbol id="icon"><circle r="5" /></symbol>
                <use id="again" href="#icon" />
                <use id="twice" xlink:href="#once" />
                <use id="once" href="#dot" />
                <circle id="dot" r="2" />
                <use id="lost" href="#nowhere" />
              </defs>
              <rect id="a" width="20" height="20" />
              <use href="#icon" />
            </svg>
            """;

        var source = Read(svgText);
        const string symbol = "A clip path takes no <symbol>, which is what that <use> draws; use it as a mask instead.";

        Assert.Equal(symbol, SvgElementEditor.Clip(source, "1", "clip-path", "2", null, out _));
        Assert.Equal(symbol, SvgElementEditor.Clip(source, "1", "clip-path", "0/1", null, out _));
        Assert.Equal(
            "That <use> draws nothing in this drawing, so it cannot be made into a clip path.",
            SvgElementEditor.Clip(source, "1", "clip-path", "0/5", null, out _));
        Assert.Equal(svgText, source.ToText());

        // Through a use of a use to a circle, and as a mask whatever it draws.
        Assert.Null(SvgElementEditor.Clip(source, "1", "clip-path", "0/2", null, out _));
        Assert.Null(SvgElementEditor.Clip(source, "1", "mask", "2", null, out _));
    }

    [Fact]
    public void A_Shape_Leaving_Its_Groups_Keeps_What_They_Gave_It()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg" font-family="serif">
              <defs />
              <rect id="a" width="20" height="20" />
              <rect id="b" width="20" height="20" />
              <g font-size="40" fill="none" style="stroke: #000">
                <text id="t">Hi</text>
                <path id="p" d="M0 0 L10 10" stroke-width="3" />
              </g>
            </svg>
            """;

        var source = Read(svgText);

        // A clip path is the text's outline at the size it was drawn, and takes no paint; the root still gives its font.
        Assert.Null(SvgElementEditor.Clip(source, "1", "clip-path", "3/0", null, out _));
        Assert.Contains("""<text id="t" font-size="40">Hi</text>""", source.ToText());

        // A mask by alpha is the stroke that was drawn, not the black fill a shape in <defs> has.
        Assert.Null(SvgElementEditor.Clip(source, "2", "mask", "3/0", null, out _));
        Assert.Contains("""<path id="p" d="M0 0 L10 10" stroke-width="3" fill="none" stroke="#000" />""", source.ToText());
    }

    [Fact]
    public void A_Shape_Filled_Even_Odd_Clips_By_The_Same_Rule()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <path id="ring" fill-rule="evenodd" d="M0 0h20v20H0z M5 5h10v10H5z" />
              </defs>
              <rect id="a" width="20" height="20" />
              <rect id="b" width="20" height="20" />
              <rect id="c" width="20" height="20" />
              <g fill-rule="evenodd"><path id="frame" d="M0 0h20v20H0z M5 5h10v10H5z" /><path id="edge" d="M0 0h9v9H0z M2 2h5v5H2z" /></g>
            </svg>
            """;

        var source = Read(svgText);

        Assert.Null(SvgElementEditor.Clip(source, "1", "clip-path", "0/0", null, out _));
        Assert.Equal("evenodd", SvgAttributeEditor.Attribute(source, "0/0/0", "clip-rule"));

        Assert.Null(SvgElementEditor.Clip(source, "2", "mask", "4/0", null, out var held));
        Assert.Null(SvgAttributeEditor.Attribute(source, held!, "clip-rule"));
        Assert.Equal("evenodd", SvgAttributeEditor.Attribute(source, held!, "fill-rule"));

        // From the group it was drawn in as well.
        Assert.Null(SvgElementEditor.Clip(source, "3", "clip-path", "4/0", null, out held));
        Assert.Equal("evenodd", SvgAttributeEditor.Attribute(source, held!, "clip-rule"));
    }

    [Fact]
    public void A_Mask_Inside_A_Clip_Path_Is_Refused_But_A_Clip_Path_Is_Not()
    {
        var source = Read(Clipped);
        const string refusal = "Inside a <clipPath> only its outline counts, so a mask does nothing there.";

        Assert.Equal(refusal, SvgElementEditor.Clip(source, "0/0/0", "mask", new XElement("path"), out _));
        Assert.Equal(refusal, SvgElementEditor.Clip(source, "0/0/0", "mask", "0/1", null, out _));
        Assert.Equal(Clipped, source.ToText());

        Assert.Null(SvgElementEditor.Clip(source, "0/0/0", "clip-path", "0/2", null, out _));
    }

    [Fact]
    public void The_Drawing_Itself_Is_Not_Clipped_Nor_An_Svg_Or_A_Link_Masked()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <svg id="inner" width="10" height="10"><rect width="10" height="10" /></svg>
              <a id="link"><rect width="5" height="5" /></a>
            </svg>
            """;

        var source = Read(svgText);

        Assert.Equal(
            "The drawing itself cannot be clipped or masked here.",
            SvgElementEditor.Clip(source, string.Empty, "clip-path", new XElement("path"), out _));
        Assert.Equal(
            "A mask on an <svg> is not drawn, so mask what it holds instead.",
            SvgElementEditor.Clip(source, "0", "mask", new XElement("path"), out _));
        Assert.Equal(
            "A mask on an <a> is not drawn, so mask what it holds instead.",
            SvgElementEditor.Clip(source, "1", "mask", new XElement("path"), out _));
        Assert.Equal(svgText, source.ToText());

        Assert.Null(SvgElementEditor.Clip(source, "0", "clip-path", new XElement("path"), out _));
    }

    /// <summary>In the target's own units rather than round its bounding box, which a straight line has none of.</summary>
    [Fact]
    public void A_New_Mask_Shows_The_Region_It_Is_Given()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <circle id="spare" r="5" />
              </defs>
              <path id="line" d="M3 12h18" stroke="#000" stroke-width="1.5" />
            </svg>
            """;

        var source = Read(svgText);

        Assert.Null(SvgElementEditor.Clip(source, "1", "mask", new XElement("path"), out _, "3 11.25 18 1.5"));
        Assert.Contains("""<mask id="line-mask" maskUnits="userSpaceOnUse" x="3" y="11.25" width="18" height="1.5">""", source.ToText());

        Assert.Null(SvgElementEditor.Clip(source, "1", "mask", "0/0", null, out _, "3 11.25 18 1.5"));
        Assert.Contains("""<mask id="line-mask-2" maskUnits="userSpaceOnUse" x="3" y="11.25" width="18" height="1.5" mask-type="alpha">""", source.ToText());

        // A clip path has no region: it covers what it holds and nothing else.
        Assert.Null(SvgElementEditor.Clip(source, "1", "clip-path", new XElement("path"), out _, "3 11.25 18 1.5"));
        Assert.Contains("""<clipPath id="line-clip">""", source.ToText());
    }
}
