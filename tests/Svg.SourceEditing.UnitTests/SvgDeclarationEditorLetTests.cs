// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Linq;
using Svg.Expressions;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// Declaring, rewriting and reordering a <c>&lt;e:let&gt;</c>.
/// </summary>
/// <remarks>
/// A let differs from a parameter in the one way that matters here: its position is part of what it
/// means. So alongside the two things the parameter suites assert — that the document reads it back,
/// and that everything else is untouched — these ask where it landed, and whether an edit that would
/// leave one unresolved is refused rather than applied.
/// </remarks>
public class SvgDeclarationEditorLetTests
{
    private const string Ns = SvgExpressionDeclarations.Namespace;

    /// <summary>The document after the edit, which must have been allowed.</summary>
    private static string Apply(string svgText, SvgSourceEditResult result)
    {
        Assert.True(result.Succeeded, result.Refusal);

        return SvgTextEdit.ApplyAll(svgText, result.Edits);
    }

    private static SvgExpressionLet Declared(string svgText, string name)
        => Assert.Single(SvgExpressionDeclarations.Parse(svgText, out _).Lets, l => l.Name == name);

    private static string[] Order(string svgText)
        => SvgExpressionDeclarations.Parse(svgText, out _).Lets.Select(let => let.Name).ToArray();

    // ---- adding ----

    [Fact]
    public void A_Let_Joins_The_Lets_A_Drawing_Already_Has()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="217" />
                  <e:let name="half">hue / 2</e:let>
                </e:code>
              </defs>
            </svg>
            """;

        var edited = Apply(source, SvgDeclarationEditor.AddLet(source, "quarter", "half / 2"));

        Assert.Equal("half / 2", Declared(edited, "quarter").Expression);
        Assert.Equal(new[] { "half", "quarter" }, Order(edited));

        // Below the let it names, which is the only place it can name it, and at that let's own
        // indentation rather than one of this code's choosing.
        Assert.Contains("""
                  <e:let name="half">hue / 2</e:let>
                  <e:let name="quarter">half / 2</e:let>
            """.TrimEnd(), edited);
    }

    [Fact]
    public void A_Let_Lands_Below_The_Parameters_When_A_Drawing_Has_No_Lets()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="217" />
                </e:code>
              </defs>
            </svg>
            """;

        var edited = Apply(source, SvgDeclarationEditor.AddLet(source, "half", "hue / 2"));

        Assert.Contains("""
                  <e:param name="hue" type="number" default="217" />
                  <e:let name="half">hue / 2</e:let>
            """.TrimEnd(), edited);
    }

    [Fact]
    public void A_Let_Brings_The_Block_And_The_Namespace_With_It()
    {
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" width="10" height="10">
              <rect width="10" height="10" fill="#ff0000" />
            </svg>
            """;

        var edited = Apply(source, SvgDeclarationEditor.AddLet(source, "third", "tau / 3"));

        Assert.Equal("tau / 3", Declared(edited, "third").Expression);
        Assert.Contains($"xmlns:e=\"{Ns}\"", edited);
        Assert.Contains("<defs>", edited);

        // The drawing itself is not part of the edit.
        Assert.Contains("""<rect width="10" height="10" fill="#ff0000" />""", edited);
    }

    [Fact]
    public void A_Let_Naming_Something_The_Drawing_Does_Not_Declare_Is_Refused()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="217" />
                </e:code>
              </defs>
            </svg>
            """;

        var result = SvgDeclarationEditor.AddLet(source, "half", "saturation / 2");

        Assert.False(result.Succeeded);
        Assert.Contains("half", result.Refusal);
    }

    [Fact]
    public void A_Name_The_Drawing_Has_Already_Given_Out_Is_Refused()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="217" />
                </e:code>
              </defs>
            </svg>
            """;

        Assert.False(SvgDeclarationEditor.AddLet(source, "hue", "1").Succeeded);
    }

    [Fact]
    public void A_Comparison_Keeps_The_Angle_Bracket_Somebody_Typed()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="t" type="number" default="0.25" />
                </e:code>
              </defs>
            </svg>
            """;

        var edited = Apply(source, SvgDeclarationEditor.AddLet(source, "early", "t < 0.5"));

        // Escaped in the file, because a bare < opens a tag; read back as what was typed.
        Assert.Contains("t &lt; 0.5", edited);
        Assert.Equal("t < 0.5", Declared(edited, "early").Expression);

        // The other three of the attribute set are legal here and would be noise: somebody typing
        // `t > 0.5` should see `t > 0.5` in the pane.
        var greater = Apply(edited, SvgDeclarationEditor.AddLet(edited, "late", "t > 0.5"));

        Assert.Contains("t > 0.5", greater);
    }

    // ---- rewriting ----

    [Fact]
    public void Changing_A_Body_Leaves_Everything_Else_Alone()
    {
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="217" />
                  <!-- what the drawing is really tinted by -->
                  <e:let name="half">hue / 2</e:let>
                </e:code>
              </defs>
              <rect width="10" height="10" fill="{{ hsl(half, 1, 0.5) }}" />
            </svg>
            """.Replace("EXPR-NS", Ns);

        var edited = Apply(source, SvgDeclarationEditor.UpdateLet(source, "half", "half", "hue / 3"));

        Assert.Equal("hue / 3", Declared(edited, "half").Expression);
        Assert.Contains("<!-- what the drawing is really tinted by -->", edited);
        Assert.Equal(source.Replace("hue / 2", "hue / 3"), edited);
    }

    [Fact]
    public void Renaming_A_Let_Moves_Every_Use_With_It()
    {
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="217" />
                  <e:let name="half">hue / 2</e:let>
                  <e:let name="tint">hsl(half, 1, 0.5)</e:let>
                </e:code>
              </defs>
              <rect width="10" height="10" fill="{{ tint }}" opacity="{{ half / 180 }}" />
            </svg>
            """.Replace("EXPR-NS", Ns);

        var edited = Apply(source, SvgDeclarationEditor.UpdateLet(source, "half", "midpoint", "hue / 2"));

        Assert.Equal(new[] { "midpoint", "tint" }, Order(edited));

        // The other let's body and the placeholder, not only the declaration.
        Assert.Equal("hsl(midpoint, 1, 0.5)", Declared(edited, "tint").Expression);
        Assert.Contains("{{ midpoint / 180 }}", edited);
        Assert.DoesNotContain("half", edited);
    }

    [Fact]
    public void A_Body_Naming_A_Let_Below_It_Is_Refused()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="217" />
                  <e:let name="half">hue / 2</e:let>
                  <e:let name="quarter">half / 2</e:let>
                </e:code>
              </defs>
            </svg>
            """;

        var result = SvgDeclarationEditor.UpdateLet(source, "half", "half", "quarter * 2");

        Assert.False(result.Succeeded);
        Assert.Contains("half", result.Refusal);
    }

    [Fact]
    public void A_Let_The_Drawing_Does_Not_Have_Is_Refused()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="217" />
                </e:code>
              </defs>
            </svg>
            """;

        Assert.False(SvgDeclarationEditor.UpdateLet(source, "half", "half", "1").Succeeded);
    }

    // ---- reordering ----

    private const string Three = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS" width="10" height="10">
          <defs>
            <e:code>
              <e:param name="hue" type="number" default="217" />
              <e:let name="a">hue / 2</e:let>
              <e:let name="b">tau / 4</e:let>
              <e:let name="c">tau / 8</e:let>
            </e:code>
          </defs>
        </svg>
        """;

    [Fact]
    public void A_Let_Moved_Up_Takes_Its_Line_With_It()
    {
        var source = Three.Replace("EXPR-NS", Ns);

        var edited = Apply(source, SvgDeclarationEditor.MoveLet(source, "c", 1));

        Assert.Equal(new[] { "a", "c", "b" }, Order(edited));

        // The same characters in a different order, so a move cannot quietly reformat or drop one.
        Assert.Contains("""
                  <e:let name="a">hue / 2</e:let>
                  <e:let name="c">tau / 8</e:let>
                  <e:let name="b">tau / 4</e:let>
            """.TrimEnd(), edited);
    }

    [Fact]
    public void A_Let_Moved_To_The_Top_Sits_Above_The_Rest()
    {
        var source = Three.Replace("EXPR-NS", Ns);

        var edited = Apply(source, SvgDeclarationEditor.MoveLet(source, "c", 0));

        Assert.Equal(new[] { "c", "a", "b" }, Order(edited));

        // Still below the parameters: moving among the lets does not move it out of its group.
        Assert.Contains("""
                  <e:param name="hue" type="number" default="217" />
                  <e:let name="c">tau / 8</e:let>
            """.TrimEnd(), edited);
    }

    [Fact]
    public void A_Let_Dragged_Past_What_It_Names_Is_Refused()
    {
        var source = Three.Replace("EXPR-NS", Ns).Replace("""<e:let name="b">tau / 4</e:let>""", """<e:let name="b">a * 2</e:let>""");

        var result = SvgDeclarationEditor.MoveLet(source, "b", 0);

        Assert.False(result.Succeeded);
        Assert.Contains("'b'", result.Refusal);
    }

    [Fact]
    public void A_Let_Sharing_Its_Line_Is_Refused_Rather_Than_Cut_Out_Of_It()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code><e:let name="a">tau / 4</e:let><e:let name="b">tau / 8</e:let></e:code></defs>
            </svg>
            """;

        var result = SvgDeclarationEditor.MoveLet(source, "b", 0);

        Assert.False(result.Succeeded);
        Assert.Contains("line", result.Refusal);
    }

    [Fact]
    public void Moving_A_Let_Where_It_Already_Is_Changes_Nothing()
    {
        var source = Three.Replace("EXPR-NS", Ns);

        var result = SvgDeclarationEditor.MoveLet(source, "b", 1);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Edits);
    }

    // ---- what the reordering check may and may not refuse ----

    [Fact]
    public void An_Edit_Is_Not_Blamed_For_A_Let_That_Was_Already_Unresolved()
    {
        // Reading does not type check, so a drawing can hold this and still open. Somebody part-way
        // through fixing it must still be able to edit the rest.
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="217" />
                  <e:let name="broken">saturation / 2</e:let>
                </e:code>
              </defs>
            </svg>
            """;

        var edited = Apply(source, SvgDeclarationEditor.Add(source, new SvgExpressionParameter("radius", ExprType.Number, "40")));

        Assert.Contains("radius", edited);
        Assert.Contains("saturation / 2", edited);
    }

    // ---- parameters reorder too, under a different rule ----

    private const string Mixed = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS" width="10" height="10">
          <defs>
            <e:code>
              <e:param name="size" type="number" />
              <e:param name="hue" type="number" default="217" />
              <e:param name="tint" type="color" default="#ff0000" />
            </e:code>
          </defs>
        </svg>
        """;

    private static string[] Parameters(string svgText)
        => SvgExpressionDeclarations.Parse(svgText, out _).Parameters.Select(p => p.Name).ToArray();

    [Fact]
    public void Two_Parameters_With_Defaults_Can_Be_Swapped()
    {
        var source = Mixed.Replace("EXPR-NS", Ns);

        var edited = Apply(source, SvgDeclarationEditor.MoveParameter(source, "tint", 1));

        Assert.Equal(new[] { "size", "tint", "hue" }, Parameters(edited));
    }

    [Fact]
    public void A_Parameter_With_No_Default_Can_Go_Anywhere_The_Author_Wants()
    {
        var source = Mixed.Replace("EXPR-NS", Ns);

        // The C# generator needs the ones with defaults last and says so when it is run --
        // SkiaCSharpCodeGenExpressionTests holds that. It is a rule of that back end and not of
        // this language, so a document is not stopped from saying it.
        var edited = Apply(source, SvgDeclarationEditor.MoveParameter(source, "size", 2));

        Assert.Equal(new[] { "hue", "tint", "size" }, Parameters(edited));
    }

    [Fact]
    public void Moving_A_Parameter_Leaves_The_Lets_Where_They_Were()
    {
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="217" />
                  <e:param name="tint" type="color" default="#ff0000" />

                  <e:let name="half">hue / 2</e:let>
                </e:code>
              </defs>
            </svg>
            """.Replace("EXPR-NS", Ns);

        var edited = Apply(source, SvgDeclarationEditor.MoveParameter(source, "tint", 0));

        Assert.Equal(new[] { "tint", "hue" }, Parameters(edited));
        Assert.Equal(new[] { "half" }, Order(edited));

        // The blank line between the groups is layout somebody chose, and a reorder within one
        // group is not a reason to lose it.
        Assert.Contains("""

                  <e:let name="half">hue / 2</e:let>
            """.TrimEnd(), edited);
    }
}
