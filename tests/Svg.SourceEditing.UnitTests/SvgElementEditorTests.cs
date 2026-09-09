using System;
using System.Collections.Generic;
using Svg.SourceEditing;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// Putting elements inside a group, and taking them out again, as spans.
/// </summary>
/// <remarks>
/// The text is asserted whole rather than by fragment: what this has to get right is the
/// indentation and the line breaks around what it moved, and a contains-check would pass while
/// leaving the file a mess.
/// </remarks>
public class SvgElementEditorTests
{
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
          <!-- what it paints -->
          <rect width="24" height="24" fill="#00ff00" />
          <circle cx="12" cy="12" r="6" />
          <line x1="0" y1="0" x2="24" y2="24" />
        </svg>
        """;

    private static string Apply(string svgText, SvgSourceEditResult result)
    {
        Assert.True(result.Succeeded, result.Refusal);

        return SvgTextEdit.ApplyAll(svgText, result.Edits);
    }

    private static IReadOnlyList<string> Keys(params string[] keys) => keys;

    [Fact]
    public void Two_Neighbours_Are_Wrapped_Where_They_Sit()
    {
        var wrapped = Apply(Drawing, SvgElementEditor.Wrap(Drawing, Keys("0", "1")));

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <!-- what it paints -->
              <g>
                <rect width="24" height="24" fill="#00ff00" />
                <circle cx="12" cy="12" r="6" />
              </g>
              <line x1="0" y1="0" x2="24" y2="24" />
            </svg>
            """,
            wrapped);
    }

    /// <summary>The comment is not an element, so it is neither counted nor moved.</summary>
    [Fact]
    public void What_Is_Not_An_Element_Is_Left_Where_It_Was()
    {
        Assert.Contains("<!-- what it paints -->", Apply(Drawing, SvgElementEditor.Wrap(Drawing, Keys("0", "2"))));
    }

    /// <summary>
    /// Picked apart, they come together — which changes the order they paint in.
    /// </summary>
    /// <remarks>
    /// The circle was drawn between them and is drawn after them now. That is the cost the caller
    /// accepts by offering the command at all, and it is asserted so nobody can change it quietly.
    /// </remarks>
    [Fact]
    public void Two_That_Were_Apart_Are_Brought_Together_At_The_First()
    {
        var wrapped = Apply(Drawing, SvgElementEditor.Wrap(Drawing, Keys("0", "2")));

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <!-- what it paints -->
              <g>
                <rect width="24" height="24" fill="#00ff00" />
                <line x1="0" y1="0" x2="24" y2="24" />
              </g>
              <circle cx="12" cy="12" r="6" />
            </svg>
            """,
            wrapped);
    }

    [Fact]
    public void An_Element_Carrying_Children_Moves_With_Them()
    {
        const string nested = """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <g id="inner">
                <rect width="4" height="4" />
              </g>
              <circle cx="12" cy="12" r="6" />
            </svg>
            """;

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <g>
                <g id="inner">
                  <rect width="4" height="4" />
                </g>
                <circle cx="12" cy="12" r="6" />
              </g>
            </svg>
            """,
            Apply(nested, SvgElementEditor.Wrap(nested, Keys("0", "1"))));
    }

    [Fact]
    public void A_Drawing_Written_With_Tabs_Keeps_Them()
    {
        var tabbed = Drawing.Replace("  ", "\t");

        Assert.Contains("\t\t<rect", Apply(tabbed, SvgElementEditor.Wrap(tabbed, Keys("0", "1"))));
    }

    [Fact]
    public void Ungrouping_Gives_Back_What_Grouping_Took()
    {
        var wrapped = Apply(Drawing, SvgElementEditor.Wrap(Drawing, Keys("0", "1")));

        Assert.Equal(Drawing, Apply(wrapped, SvgElementEditor.Unwrap(wrapped, "0")));
    }

    [Fact]
    public void One_Element_Is_Not_A_Group()
    {
        Assert.Equal("Grouping takes two elements or more.", SvgElementEditor.Wrap(Drawing, Keys("0")).Refusal);
    }

    [Fact]
    public void The_Drawing_Itself_Cannot_Be_Grouped()
    {
        Assert.Contains("cannot be put inside a group", SvgElementEditor.Wrap(Drawing, Keys("", "0")).Refusal!);
    }

    [Fact]
    public void An_Element_Cannot_Be_Grouped_With_Something_Inside_It()
    {
        const string nested = """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <g id="inner">
                <rect width="4" height="4" />
              </g>
            </svg>
            """;

        Assert.Contains("inside it", SvgElementEditor.Wrap(nested, Keys("0", "0/0")).Refusal!);
    }

    /// <summary>A minified drawing has no line to move, and is told so rather than mangled.</summary>
    [Fact]
    public void A_Drawing_On_One_Line_Says_What_Is_In_The_Way()
    {
        const string minified = """<svg xmlns="http://www.w3.org/2000/svg"><rect width="4" height="4" /><circle r="2" /></svg>""";

        Assert.Contains("line of its own", SvgElementEditor.Wrap(minified, Keys("0", "1")).Refusal!);
    }

    [Fact]
    public void What_Is_Not_Part_Of_The_Picture_Is_Refused()
    {
        const string declared = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24">
              <defs>
                <clipPath id="c">
                  <rect width="4" height="4" />
                </clipPath>
              </defs>
              <rect width="24" height="24" />
              <circle r="6" />
            </svg>
            """;

        Assert.NotNull(SvgElementEditor.Wrap(declared, Keys("0/0/0", "1")).Refusal);
        Assert.Null(SvgElementEditor.Wrap(declared, Keys("1", "2")).Refusal);
    }

    /// <summary>A greater-than is legal in an attribute, and this language writes them.</summary>
    [Fact]
    public void An_Attribute_Holding_A_Greater_Than_Is_Not_Cut_Through()
    {
        const string compared = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24">
              <rect width="24" height="24" fill="{{ t > 0.5 ? #ffffff : #000000 }}" />
              <circle cx="12" cy="12" r="6" />
            </svg>
            """;

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24">
              <g>
                <rect width="24" height="24" fill="{{ t > 0.5 ? #ffffff : #000000 }}" />
                <circle cx="12" cy="12" r="6" />
              </g>
            </svg>
            """,
            Apply(compared, SvgElementEditor.Wrap(compared, Keys("0", "1"))));
    }

    /// <summary>Two things kept together may be grouped together; one of each may not.</summary>
    [Fact]
    public void Inside_A_Defs_Is_A_Place_Like_Any_Other()
    {
        const string kept = """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <defs>
                <rect id="a" width="4" height="4" />
                <rect id="b" width="4" height="4" />
              </defs>
              <use href="#a" />
            </svg>
            """;

        Assert.Null(SvgElementEditor.Wrap(kept, Keys("0/0", "0/1")).Refusal);
        Assert.Contains("same place", SvgElementEditor.Wrap(kept, Keys("0/0", "1")).Refusal!);
    }

    /// <summary>
    /// What a group holds is not only its elements.
    /// </summary>
    /// <remarks>
    /// Taking the children out one at a time would leave a comment written between them behind, to
    /// be deleted with the tags around it.
    /// </remarks>
    [Fact]
    public void Ungrouping_Brings_Out_What_Is_Not_An_Element_Too()
    {
        const string commented = """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <g>
                <!-- the pair -->
                <rect width="4" height="4" />
                <circle r="2" />
              </g>
            </svg>
            """;

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <!-- the pair -->
              <rect width="4" height="4" />
              <circle r="2" />
            </svg>
            """,
            Apply(commented, SvgElementEditor.Unwrap(commented, "0")));
    }

    [Fact]
    public void A_Group_Carrying_Something_Its_Children_Would_Lose_Is_Not_Ungrouped()
    {
        const string carried = """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <g transform="rotate(30)">
                <rect width="4" height="4" />
              </g>
            </svg>
            """;

        Assert.Contains("transform", SvgElementEditor.Unwrap(carried, "0").Refusal!);
    }
}
