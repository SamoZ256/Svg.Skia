// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Linq;
using Svg.Expressions;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// Rewriting a declaration, and carrying every use of its name along when it is renamed.
/// </summary>
/// <remarks>
/// A rename that moved only the declaration would leave a drawing that still parses and no longer
/// draws, so most of what is asserted here is about the places the name is used rather than the
/// place it is declared.
/// </remarks>
public class SvgDeclarationEditorUpdateTests
{
    private const string Ns = SvgExpressionDeclarations.Namespace;

    private const string Source = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS" width="10" height="10">
          <defs>
            <e:code>
              <e:param name="hue" type="number" default="217" min="0" max="360" />
              <e:param name="fade" type="number" default="1" />

              <e:let name="primary">hsl(hue, 91%, 60%)</e:let>
              <e:let name="deep">hsl(hue + 5, 71%, 40%)</e:let>
            </e:code>
          </defs>
          <circle cx="5" cy="5" r="4" fill="{{ primary }}" opacity="{{ fade * 0.5 }}" />
          <rect width="10" height="10" fill="{{ hsl(hue, 50%, 50%) }}" />
        </svg>
        """;

    private static string Document() => Source.Replace("EXPR-NS", Ns);

    private static string Apply(string svgText, SvgSourceEditResult result)
    {
        Assert.True(result.Succeeded, result.Refusal);

        return SvgTextEdit.ApplyAll(svgText, result.Edits);
    }

    private static SvgExpressionParameter Declared(string svgText, string name)
        => Assert.Single(SvgExpressionDeclarations.Parse(svgText, out _).Parameters, p => p.Name == name);

    // ---- the declaration itself ----

    [Fact]
    public void A_Range_Is_Rewritten_Where_It_Stands()
    {
        var source = Document();

        var edited = Apply(
            source,
            SvgDeclarationEditor.Update(source, "hue", new SvgExpressionParameter("hue", ExprType.Number, "217", "10", "350", "5")));

        var declared = Declared(edited, "hue");

        Assert.Equal("10", declared.MinExpression);
        Assert.Equal("350", declared.MaxExpression);
        Assert.Equal("5", declared.StepExpression);
        Assert.Equal("217", declared.DefaultExpression);
    }

    [Fact]
    public void A_Range_Set_To_Nothing_Is_Taken_Away()
    {
        var source = Document();

        var edited = Apply(
            source,
            SvgDeclarationEditor.Update(source, "hue", new SvgExpressionParameter("hue", ExprType.Number, "217", null, null, null)));

        Assert.False(Declared(edited, "hue").HasRange);
        Assert.Contains("""<e:param name="hue" type="number" default="217" />""", edited);
    }

    [Fact]
    public void Changing_Nothing_Is_Nothing_To_Do()
    {
        var source = Document();

        var result = SvgDeclarationEditor.Update(
            source,
            "hue",
            new SvgExpressionParameter("hue", ExprType.Number, "217", "0", "360", null));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Edits);
    }

    // ---- renaming ----

    [Fact]
    public void A_Rename_Carries_Every_Use_Of_The_Name()
    {
        var source = Document();

        var edited = Apply(
            source,
            SvgDeclarationEditor.Update(source, "hue", new SvgExpressionParameter("tone", ExprType.Number, "217", "0", "360", null)));

        Assert.Equal("217", Declared(edited, "tone").DefaultExpression);

        Assert.Contains("hsl(tone, 91%, 60%)", edited);
        Assert.Contains("hsl(tone + 5, 71%, 40%)", edited);
        Assert.Contains("{{ hsl(tone, 50%, 50%) }}", edited);

        Assert.DoesNotContain("hue", edited);
    }

    [Fact]
    public void A_Rename_Leaves_Names_That_Merely_Contain_It_Alone()
    {
        var source = Document().Replace(
            """<e:let name="deep">hsl(hue + 5, 71%, 40%)</e:let>""",
            """<e:let name="deep">hsl(hue + hue2, 71%, 40%)</e:let>"""
                + "\n              " + """<e:let name="hue2">5</e:let>""");

        var edited = Apply(
            source,
            SvgDeclarationEditor.Update(source, "hue", new SvgExpressionParameter("tone", ExprType.Number, "217", "0", "360", null)));

        Assert.Contains("hsl(tone + hue2, 71%, 40%)", edited);
        Assert.Contains("""<e:let name="hue2">""", edited);
    }

    [Fact]
    public void A_Rename_Does_Not_Touch_A_Function_Or_A_Constant_Of_The_Same_Shape()
    {
        var source = Document().Replace(
            """<e:let name="primary">hsl(hue, 91%, 60%)</e:let>""",
            """<e:let name="primary">hsl(hue * tau, 91%, 60%)</e:let>""");

        var edited = Apply(
            source,
            SvgDeclarationEditor.Update(source, "hue", new SvgExpressionParameter("tone", ExprType.Number, "217", "0", "360", null)));

        Assert.Contains("hsl(tone * tau, 91%, 60%)", edited);
    }

    [Fact]
    public void A_Name_Inside_An_Entity_Is_Not_A_Use_Of_It()
    {
        // 'amp' is a legal parameter name and &amp; is not a use of it. Searching the file's own
        // characters would rename the entity and take the drawing's markup with it.
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="amp" type="number" default="1" />
                  <e:let name="wide">amp &gt; 0.5 and amp &lt; 2</e:let>
                </e:code>
              </defs>
              <circle cx="5" cy="5" r="4" opacity="{{ amp }}" />
            </svg>
            """.Replace("EXPR-NS", Ns);

        var edited = Apply(
            source,
            SvgDeclarationEditor.Update(source, "amp", new SvgExpressionParameter("gain", ExprType.Number, "1")));

        Assert.Contains("gain &gt; 0.5 and gain &lt; 2", edited);
        Assert.Contains("{{ gain }}", edited);
        Assert.Equal("1", Declared(edited, "gain").DefaultExpression);
    }

    [Fact]
    public void A_Name_Already_Taken_Is_Refused()
    {
        var source = Document();

        var result = SvgDeclarationEditor.Update(
            source,
            "hue",
            new SvgExpressionParameter("fade", ExprType.Number, "217", "0", "360", null));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Its_Own_Name_Is_Not_A_Name_Already_Taken()
    {
        var source = Document();

        var result = SvgDeclarationEditor.Update(
            source,
            "hue",
            new SvgExpressionParameter("hue", ExprType.Number, "90", "0", "360", null));

        Assert.True(result.Succeeded, result.Refusal);
        Assert.Equal("90", Declared(Apply(source, result), "hue").DefaultExpression);
    }

    [Fact]
    public void A_Name_A_Let_Has_Taken_Is_Refused()
    {
        var source = Document();

        Assert.False(
            SvgDeclarationEditor.Update(source, "hue", new SvgExpressionParameter("primary", ExprType.Number, "217", "0", "360", null))
                .Succeeded);
    }

    [Fact]
    public void A_Change_Of_Type_Is_Refused()
    {
        var source = Document();

        var result = SvgDeclarationEditor.Update(source, "hue", new SvgExpressionParameter("hue", ExprType.Color, "#ff0000"));

        Assert.False(result.Succeeded);
        Assert.Empty(result.Edits);
    }

    [Fact]
    public void Renaming_Through_Set_Is_Refused_Because_It_Would_Leave_The_Uses_Behind()
    {
        var source = Document();

        var result = SvgDeclarationEditor.Set(source, "hue", SvgDeclarationPart.Name, "tone");

        Assert.False(result.Succeeded);
        Assert.Empty(result.Edits);
    }

    [Fact]
    public void A_Parameter_The_Drawing_Does_Not_Declare_Is_Refused()
    {
        var source = Document();

        Assert.False(
            SvgDeclarationEditor.Update(source, "nosuch", new SvgExpressionParameter("nosuch", ExprType.Number, "1")).Succeeded);
    }

    [Fact]
    public void Everything_The_Edit_Did_Not_Touch_Is_Left_Alone()
    {
        var source = Document();

        var edited = Apply(
            source,
            SvgDeclarationEditor.Update(source, "hue", new SvgExpressionParameter("tone", ExprType.Number, "217", "0", "360", null)));

        Assert.Equal(source.Split('\n').Length, edited.Split('\n').Length);
        Assert.Contains("""<e:param name="fade" type="number" default="1" />""", edited);
        Assert.Contains("<circle cx=\"5\" cy=\"5\" r=\"4\"", edited);
    }
}
