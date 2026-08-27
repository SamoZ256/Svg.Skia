// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;
using System.Linq;
using Svg.Expressions;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// Writing what a declaration says, which is what committing a value somebody chose comes down to.
/// </summary>
/// <remarks>
/// The whole of a commit is one thing a reader did, so it is one edit list however many parameters
/// it touches — a host applying it in a text editor gets one undo step for the lot. That is what the
/// multi-parameter cases here are really pinning.
/// </remarks>
public class SvgDeclarationEditorSetTests
{
    private const string Ns = SvgExpressionDeclarations.Namespace;

    private const string Three = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS" width="10" height="10">
          <defs>
            <e:code>
              <e:param name="hue"   type="number"  default="217" min="0" max="360" />
              <e:param name="tint"  type="color"   default="#3fb5b5" />
              <e:param name="badge" type="boolean" default="true" />
            </e:code>
          </defs>
        </svg>
        """;

    private static string Source() => Three.Replace("EXPR-NS", Ns);

    private static string Apply(string svgText, SvgSourceEditResult result)
    {
        Assert.True(result.Succeeded, result.Refusal);

        return SvgTextEdit.ApplyAll(svgText, result.Edits);
    }

    private static SvgExpressionParameter Declared(string svgText, string name)
        => Assert.Single(SvgExpressionDeclarations.Parse(svgText, out _).Parameters, p => p.Name == name);

    [Fact]
    public void A_Default_Is_Replaced_Where_It_Stands()
    {
        var source = Source();

        var edited = Apply(source, SvgDeclarationEditor.Set(source, "hue", SvgDeclarationPart.Default, "90"));

        Assert.Equal("90", Declared(edited, "hue").DefaultExpression);

        // Its neighbours on the same line are untouched, alignment included.
        Assert.Contains("""<e:param name="hue"   type="number"  default="90" min="0" max="360" />""", edited);
    }

    [Fact]
    public void Every_Changed_Default_Is_One_Edit_List()
    {
        var source = Source();

        var result = SvgDeclarationEditor.SetDefaults(source, new Dictionary<string, string>
        {
            ["hue"] = "90",
            ["tint"] = "#ff0000",
            ["badge"] = "false",
        });

        var edited = Apply(source, result);

        Assert.Equal(3, result.Edits.Count);
        Assert.Equal("90", Declared(edited, "hue").DefaultExpression);
        Assert.Equal("#ff0000", Declared(edited, "tint").DefaultExpression);
        Assert.Equal("false", Declared(edited, "badge").DefaultExpression);
    }

    [Fact]
    public void The_Edits_Come_Back_In_The_Order_The_Document_Reads()
    {
        var source = Source();

        // Handed over backwards, because a panel iterating its rows has no reason to be in order.
        var result = SvgDeclarationEditor.SetDefaults(source, new Dictionary<string, string>
        {
            ["badge"] = "false",
            ["hue"] = "90",
        });

        Assert.True(result.Succeeded, result.Refusal);
        Assert.Equal(result.Edits.OrderBy(edit => edit.Position), result.Edits);
    }

    [Fact]
    public void Writing_What_It_Already_Says_Is_Nothing_To_Do()
    {
        var source = Source();

        var result = SvgDeclarationEditor.Set(source, "hue", SvgDeclarationPart.Default, "217");

        Assert.True(result.Succeeded);
        Assert.Empty(result.Edits);
    }

    [Fact]
    public void An_Attribute_A_Declaration_Does_Not_Have_Is_Added()
    {
        var source = Source();

        var edited = Apply(source, SvgDeclarationEditor.Set(source, "hue", SvgDeclarationPart.Step, "15"));

        Assert.Equal("15", Declared(edited, "hue").StepExpression);
    }

    [Fact]
    public void An_Attribute_Set_To_Nothing_Is_Taken_Away()
    {
        var source = Source();

        var edited = Apply(source, SvgDeclarationEditor.Set(source, "badge", SvgDeclarationPart.Default, null));

        Assert.Null(Declared(edited, "badge").DefaultExpression);

        // The space in front of it goes too, so a declaration does not keep a gap where an attribute
        // used to be.
        Assert.Contains("""<e:param name="badge" type="boolean" />""", edited);
    }

    [Fact]
    public void An_Edit_That_Would_Leave_The_Document_Saying_Something_Wrong_Is_Refused()
    {
        var source = Source();

        // min and max are a pair, so taking one away leaves a declaration the language refuses. That
        // verdict is the reader's, not this one's — the edit is refused rather than applied and the
        // document left in that state.
        var result = SvgDeclarationEditor.Set(source, "hue", SvgDeclarationPart.Min, null);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Edits);
    }

    [Fact]
    public void A_Value_With_A_Quote_In_It_Does_Not_End_The_Attribute()
    {
        var source = Source();

        var edited = Apply(source, SvgDeclarationEditor.Set(source, "tint", SvgDeclarationPart.Default, "\"#fff\""));

        Assert.Equal("\"#fff\"", Declared(edited, "tint").DefaultExpression);
    }

    [Fact]
    public void A_Parameter_The_Drawing_Does_Not_Declare_Is_Refused()
    {
        var source = Source();

        var result = SvgDeclarationEditor.Set(source, "nosuch", SvgDeclarationPart.Default, "1");

        Assert.False(result.Succeeded);
        Assert.Empty(result.Edits);
    }

    [Fact]
    public void An_Expression_Default_Can_Be_Committed_Over()
    {
        // What committing a slider does to a default somebody wrote as an expression. It is a real
        // loss of what they meant, and the reason a host says so before doing it.
        var source = Source().Replace("default=\"217\"", "default=\"tau * 30\"");

        var edited = Apply(source, SvgDeclarationEditor.Set(source, "hue", SvgDeclarationPart.Default, "188.5"));

        Assert.Equal("188.5", Declared(edited, "hue").DefaultExpression);
    }
}
