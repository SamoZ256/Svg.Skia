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
    public void A_Row_Dropped_After_Another_Is_Written_There()
    {
        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <!-- what it paints -->
              <circle cx="12" cy="12" r="6" />
              <rect width="24" height="24" fill="#00ff00" />
              <line x1="0" y1="0" x2="24" y2="24" />
            </svg>
            """,
            Apply(Drawing, SvgElementEditor.Move(Drawing, "0", "1", SvgElementDrop.After)));
    }

    [Fact]
    public void A_Row_Dropped_Before_Another_Is_Written_There()
    {
        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <!-- what it paints -->
              <line x1="0" y1="0" x2="24" y2="24" />
              <rect width="24" height="24" fill="#00ff00" />
              <circle cx="12" cy="12" r="6" />
            </svg>
            """,
            Apply(Drawing, SvgElementEditor.Move(Drawing, "2", "0", SvgElementDrop.Before)));
    }

    /// <summary>A drop inside is what puts an element in a group, and it lands one level deeper.</summary>
    [Fact]
    public void A_Row_Dropped_Inside_A_Group_Is_Indented_Into_It()
    {
        const string held = """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <g id="wrap">
                <rect width="4" height="4" />
              </g>
              <circle cx="12" cy="12" r="6" />
            </svg>
            """;

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <g id="wrap">
                <rect width="4" height="4" />
                <circle cx="12" cy="12" r="6" />
              </g>
            </svg>
            """,
            Apply(held, SvgElementEditor.Move(held, "1", "0", SvgElementDrop.Inside)));
    }

    [Fact]
    public void A_Group_Dropped_Inside_Itself_Is_Refused()
    {
        const string held = """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <g id="wrap">
                <rect width="4" height="4" />
              </g>
            </svg>
            """;

        Assert.Contains("inside itself", SvgElementEditor.Move(held, "0", "0/0", SvgElementDrop.After).Refusal!);
        Assert.Contains("inside itself", SvgElementEditor.Move(held, "0", "0", SvgElementDrop.Inside).Refusal!);
    }

    [Fact]
    public void A_Row_Carrying_Children_Moves_Whole_And_Reindents()
    {
        const string held = """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <g id="a">
                <rect width="4" height="4" />
              </g>
              <g id="b">
              </g>
            </svg>
            """;

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <g id="b">
                <g id="a">
                  <rect width="4" height="4" />
                </g>
              </g>
            </svg>
            """,
            Apply(held, SvgElementEditor.Move(held, "0", "1", SvgElementDrop.Inside)));
    }

    /// <summary>A tag that closes itself has no inside, and is told so rather than mangled.</summary>
    [Fact]
    public void A_Self_Closing_Row_Has_No_Inside()
    {
        const string held = """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <rect width="4" height="4" />
              <g id="b" />
            </svg>
            """;

        Assert.Contains("no inside", SvgElementEditor.Move(held, "0", "1", SvgElementDrop.Inside).Refusal!);
    }

    [Fact]
    public void A_New_Group_Is_Written_Where_The_Drop_Says()
    {
        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <!-- what it paints -->
              <rect width="24" height="24" fill="#00ff00" />
              <g>
              </g>
              <circle cx="12" cy="12" r="6" />
              <line x1="0" y1="0" x2="24" y2="24" />
            </svg>
            """,
            Apply(Drawing, SvgElementEditor.NewGroup(Drawing, "0", SvgElementDrop.After)));
    }

    /// <summary>
    /// Beside the drawing itself means inside it.
    /// </summary>
    /// <remarks>
    /// The root has no siblings, so a group asked for next to it has nowhere to go — and refusing
    /// was the answer until somebody picked the top row, which is the obvious row to pick.
    /// </remarks>
    [Fact]
    public void A_Group_Beside_The_Drawing_Goes_In_It()
    {
        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <!-- what it paints -->
              <rect width="24" height="24" fill="#00ff00" />
              <circle cx="12" cy="12" r="6" />
              <line x1="0" y1="0" x2="24" y2="24" />
              <g>
              </g>
            </svg>
            """,
            Apply(Drawing, SvgElementEditor.NewGroup(Drawing, "", SvgElementDrop.After)));
    }

    [Fact]
    public void A_Row_Dropped_Beside_The_Drawing_Goes_In_It()
    {
        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <!-- what it paints -->
              <circle cx="12" cy="12" r="6" />
              <line x1="0" y1="0" x2="24" y2="24" />
              <rect width="24" height="24" fill="#00ff00" />
            </svg>
            """,
            Apply(Drawing, SvgElementEditor.Move(Drawing, "0", "", SvgElementDrop.After)));
    }
}
