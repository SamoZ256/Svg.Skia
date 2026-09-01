// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using Svg.Expressions;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// Writing a recipe's colour rules as spans over its text.
/// </summary>
/// <remarks>
/// Two things throughout, and the second is why it is written this way at all: that the rule reads
/// back, and that the rest of the file is byte for byte what it was. A recipe is hand written and
/// commented, and a colour editor that reformatted it would be worse than typing the rule.
/// </remarks>
public class SvgRecipeRuleEditorTests
{
    private const string Recipe = """
        <?xml version="1.0" encoding="utf-8"?>
        <recipe xmlns="https://svg.skia/expr/1.0">

          <!-- Copied verbatim into the drawing. -->
          <code>
            <param name="hue" type="number" default="217" />
            <let name="primary">hsl(hue, 91%, 60%)</let>
          </code>

          <!-- What it paints. -->
          <replace color="#3b82f6">primary</replace>
        </recipe>
        """;

    private static string Set(string recipe, string color, string expression)
        => Applied(recipe, SvgRecipeRuleEditor.SetRule(recipe, color, expression));

    private static string Remove(string recipe, string color)
        => Applied(recipe, SvgRecipeRuleEditor.RemoveRule(recipe, color));

    private static string Applied(string recipe, SvgSourceEditResult result)
    {
        Assert.True(result.Succeeded, result.Refusal);

        return SvgTextEdit.ApplyAll(recipe, result.Edits);
    }

    [Fact]
    public void SetRule_ReplacesTheExpressionAndNothingElse()
    {
        var written = Set(Recipe, "#3b82f6", "deep");

        Assert.Equal(Recipe.Replace(">primary</replace>", ">deep</replace>"), written);
    }

    [Fact]
    public void SetRule_KeepsTheColourSpelledAsItWasWritten()
    {
        // The caller says which rule it means; a colour has many spellings and this end knows none
        // of them, so rewriting the attribute would be inventing an opinion it does not have.
        var recipe = Recipe.Replace("\"#3b82f6\"", "\"rgb(59, 130, 246)\"");

        Assert.Contains("color=\"rgb(59, 130, 246)\">deep<", Set(recipe, "rgb(59, 130, 246)", "deep"));
    }

    [Fact]
    public void SetRule_AddsANewRuleUnderTheLastOne()
    {
        var written = Set(Recipe, "#ff0000", "alert");

        Assert.Contains("""
              <replace color="#3b82f6">primary</replace>
              <replace color="#ff0000">alert</replace>
            </recipe>
            """, written);

        // The comment above the rules, and the block before them, are where they were.
        Assert.Contains("<!-- What it paints. -->", written);
        Assert.Contains("""<let name="primary">hsl(hue, 91%, 60%)</let>""", written);
    }

    [Fact]
    public void SetRule_AddsTheFirstRuleInsideARecipeThatHasNone()
    {
        const string bare = """
            <recipe xmlns="https://svg.skia/expr/1.0">
              <code>
                <param name="hue" type="number" default="217" />
              </code>
            </recipe>
            """;

        Assert.Equal("""
            <recipe xmlns="https://svg.skia/expr/1.0">
              <code>
                <param name="hue" type="number" default="217" />
              </code>
              <replace color="#ff0000">alert</replace>
            </recipe>
            """, Set(bare, "#ff0000", "alert"));
    }

    [Fact]
    public void SetRule_SayingWhatItAlreadySaysIsNoEdit()
    {
        var result = SvgRecipeRuleEditor.SetRule(Recipe, "#3b82f6", "primary");

        Assert.True(result.Succeeded);
        Assert.Empty(result.Edits);
    }

    [Fact]
    public void SetRule_RefusesAnExpressionWrittenWithBraces()
    {
        // They are added when the rule is used. A recipe carrying them produces {{ {{ … }} }},
        // which the drawing then cannot read — and the recipe parser says so far from here.
        var result = SvgRecipeRuleEditor.SetRule(Recipe, "#3b82f6", "{{ primary }}");

        Assert.False(result.Succeeded);
        Assert.Contains("without braces", result.Refusal);
    }

    [Fact]
    public void SetRule_RefusesAnEmptyExpression()
    {
        Assert.Contains("Remove it instead", SvgRecipeRuleEditor.SetRule(Recipe, "#3b82f6", "  ").Refusal);
    }

    [Fact]
    public void SetRule_EscapesWhatXmlWouldReadAsMarkup()
    {
        Assert.Contains(">hue &lt; 100 ? a : b</replace>", Set(Recipe, "#3b82f6", "hue < 100 ? a : b"));
    }

    [Fact]
    public void RemoveRule_TakesTheLineWithIt()
    {
        var written = Remove(Recipe, "#3b82f6");

        Assert.DoesNotContain("<replace", written);

        // No blank line where it was, and the comment that introduced it left alone — it may be
        // about the section rather than about the one rule, and this end cannot tell.
        Assert.Contains("""
              <!-- What it paints. -->
            </recipe>
            """, written);
    }

    [Fact]
    public void RemoveRule_ForAColourWithNoRuleIsNoEdit()
    {
        // The ordinary state of most colours in a drawing, and clearing one twice is not a mistake.
        var result = SvgRecipeRuleEditor.RemoveRule(Recipe, "#00ff00");

        Assert.True(result.Succeeded);
        Assert.Empty(result.Edits);
    }

    [Fact]
    public void Rules_AreRefusedInSomethingThatIsNotARecipe()
    {
        var result = SvgRecipeRuleEditor.SetRule(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect fill="#3b82f6" /></svg>""",
            "#3b82f6",
            "primary");

        Assert.False(result.Succeeded);
        Assert.Contains("not a recipe", result.Refusal);
    }

    [Fact]
    public void Rules_AreRefusedWhileTheTextIsNotWellFormed()
    {
        var result = SvgRecipeRuleEditor.SetRule(Recipe.Replace("</recipe>", string.Empty), "#3b82f6", "deep");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Refusal);
    }

    [Fact]
    public void Rules_AreWrittenWhileTheDeclarationsAreStillBeingTyped()
    {
        // The two halves must not take turns: a colour is bound while the parameter it names is
        // half written, and the recipe reports that itself once both are done.
        var half = Recipe.Replace("""<param name="hue" type="number" default="217" />""", """<param name="" />""");

        Assert.Contains(">deep</replace>", Set(half, "#3b82f6", "deep"));
    }
}

/// <summary>
/// Declaring into a recipe, which the declarations editor reaches by namespace and not by shape.
/// </summary>
/// <remarks>
/// A recipe holds the extension as its default namespace, so its <c>&lt;code&gt;</c>,
/// <c>&lt;param&gt;</c> and <c>&lt;let&gt;</c> are the same elements a drawing writes as
/// <c>&lt;e:code&gt;</c>. Everything asserted here is the same question asked of the other kind of
/// document that can hold them.
/// </remarks>
public class SvgDeclarationEditorRecipeTests
{
    private const string Recipe = """
        <recipe xmlns="https://svg.skia/expr/1.0">
          <code>
            <param name="hue" type="number" default="217" />
            <let name="primary">hsl(hue, 91%, 60%)</let>
          </code>
          <replace color="#3b82f6">primary</replace>
        </recipe>
        """;

    private static string Applied(string recipe, SvgSourceEditResult result)
    {
        Assert.True(result.Succeeded, result.Refusal);

        return SvgTextEdit.ApplyAll(recipe, result.Edits);
    }

    [Fact]
    public void Add_WritesTheDeclarationWithNoPrefixAndDeclaresNothing()
    {
        var written = Applied(
            Recipe,
            SvgDeclarationEditor.Add(Recipe, new SvgExpressionParameter("bold", ExprType.Boolean, "false")));

        // Unprefixed, because the recipe's own default namespace already qualifies it. Written
        // under a prefix instead, it named one nothing bound and the write was refused.
        Assert.Contains("""<param name="bold" type="boolean" default="false" />""", written);
        Assert.DoesNotContain("e:param", written);
        Assert.DoesNotContain("xmlns:e", written);
    }

    [Fact]
    public void AddLet_JoinsTheBlockThatIsAlreadyThere()
    {
        var written = Applied(Recipe, SvgDeclarationEditor.AddLet(Recipe, "deep", "hsl(hue + 5, 71%, 40%)"));

        Assert.Contains("""
                <let name="primary">hsl(hue, 91%, 60%)</let>
                <let name="deep">hsl(hue + 5, 71%, 40%)</let>
              </code>
            """, written);
    }

    [Fact]
    public void Add_WritesTheBlockWhereARecipeKeepsItRatherThanInADefs()
    {
        // <defs> is SVG's. Written into a recipe it would make a file the recipe parser refuses.
        const string bare = """
            <recipe xmlns="https://svg.skia/expr/1.0">
              <replace color="#3b82f6">primary</replace>
            </recipe>
            """;

        var written = Applied(
            bare,
            SvgDeclarationEditor.Add(bare, new SvgExpressionParameter("hue", ExprType.Number, "217")));

        Assert.DoesNotContain("defs", written);
        Assert.Contains("""
              <code>
                <param name="hue" type="number" default="217" />
              </code>
            """, written);
    }

    [Fact]
    public void Remove_IsRefusedWhileARuleStillNamesIt()
    {
        // The one that would have corrupted quietly: a rule's body is a use, and nothing searched
        // it — so the parameter went and the rule was left naming something that had gone.
        var result = SvgDeclarationEditor.RemoveLet(Recipe, "primary");

        Assert.False(result.Succeeded);
        Assert.Contains("still used", result.Refusal);
    }

    [Fact]
    public void Update_CarriesTheRuleThatNamesItAlong()
    {
        var written = Applied(
            Recipe,
            SvgDeclarationEditor.UpdateLet(Recipe, "primary", "accent", "hsl(hue, 91%, 60%)"));

        Assert.Contains("""<let name="accent">""", written);
        Assert.Contains("""<replace color="#3b82f6">accent</replace>""", written);
    }
}
