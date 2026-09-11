using System.Linq;
using Svg.Expressions;
using Svg.SourceEditing;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// Writing a recipe's replacement rules into the tree.
/// </summary>
/// <remarks>
/// The same refusals the span half gives, less the two that were about resolving an element back to
/// an offset. What this does not do is check the declarations, deliberately: a rule can be written
/// into a recipe whose parameters are halfway through being typed, and refusing here would make the
/// two halves of a recipe take turns.
/// </remarks>
public class SvgRecipeRuleEditorDocumentTests
{
    private static string Recipe(string text) => text.Replace("EXPR-NS", SvgExpressionDeclarations.Namespace);

    private static readonly string Full = Recipe("""
        <recipe xmlns="EXPR-NS">
          <code>
            <param name="tint" type="color" default="#c0392b" />
          </code>
          <replace color="#c0392b">tint</replace>
        </recipe>
        """);

    private static SvgSourceDocument Read(string recipeText)
    {
        var source = SvgSourceDocument.Read(recipeText, out var refusal);

        Assert.NotNull(source);
        Assert.Null(refusal);

        return source!;
    }

    [Fact]
    public void A_Rule_That_Is_There_Has_Its_Expression_Written()
    {
        var source = Read(Full);

        Assert.Null(SvgRecipeRuleEditor.SetRule(source, "color", "#c0392b", "accent"));

        var written = source.ToText();

        Assert.Contains("""<replace color="#c0392b">accent</replace>""", written);

        // The declaration above it is not this edit's business.
        Assert.Contains("""<param name="tint" type="color" default="#c0392b" />""", written);
    }

    [Fact]
    public void A_Rule_That_Is_Not_There_Joins_The_Rules()
    {
        var source = Read(Full);

        Assert.Null(SvgRecipeRuleEditor.SetRule(source, "color", "#1e3a5f", "deep"));

        Assert.Contains(
            "<replace color=\"#c0392b\">tint</replace>\n"
            + "  <replace color=\"#1e3a5f\">deep</replace>",
            source.ToText());
    }

    [Fact]
    public void A_Rule_In_A_Recipe_With_No_Rules_Follows_The_Code_Block()
    {
        var recipeText = Recipe("""
            <recipe xmlns="EXPR-NS">
              <code>
                <param name="tint" type="color" default="#c0392b" />
              </code>
            </recipe>
            """);

        var source = Read(recipeText);

        Assert.Null(SvgRecipeRuleEditor.SetRule(source, "color", "#c0392b", "tint"));

        Assert.Contains(
            "  </code>\n"
            + "  <replace color=\"#c0392b\">tint</replace>\n"
            + "</recipe>",
            source.ToText());
    }

    [Fact]
    public void A_Rule_In_An_Empty_Recipe_Goes_One_Level_In()
    {
        var recipeText = Recipe("<recipe xmlns=\"EXPR-NS\">\n</recipe>\n");

        var source = Read(recipeText);

        Assert.Null(SvgRecipeRuleEditor.SetRule(source, "color", "#c0392b", "tint"));

        Assert.Contains("\n  <replace color=\"#c0392b\">tint</replace>\n", source.ToText());
    }

    [Fact]
    public void A_Removed_Rule_Takes_Its_Line_With_It()
    {
        var source = Read(Full);

        Assert.Null(SvgRecipeRuleEditor.RemoveRule(source, "color", "#c0392b"));

        var written = source.ToText();

        Assert.DoesNotContain("replace", written);
        Assert.Contains("  </code>\n</recipe>", written);
    }

    [Fact]
    public void Removing_A_Rule_That_Was_Never_There_Is_Not_A_Mistake()
    {
        var source = Read(Full);

        Assert.Null(SvgRecipeRuleEditor.RemoveRule(source, "color", "#nosuch"));
        Assert.Equal(Full, source.ToText());
    }

    [Fact]
    public void A_Rule_That_Closes_Itself_Can_Be_Given_An_Expression()
    {
        // The span half refused this: opening one spelling into the other is surgery on the text.
        var recipeText = Recipe("<recipe xmlns=\"EXPR-NS\">\n  <replace color=\"#c0392b\" />\n</recipe>\n");

        var source = Read(recipeText);

        Assert.Null(SvgRecipeRuleEditor.SetRule(source, "color", "#c0392b", "tint"));
        Assert.Contains("""<replace color="#c0392b">tint</replace>""", source.ToText());
    }

    [Theory]
    [InlineData("", "#c0392b", "tint", "A rule has to say what it replaces.")]
    [InlineData("color", "", "tint", "A rule has to name a value.")]
    [InlineData("color", "#c0392b", "", "A rule with no expression paints nothing. Remove it instead.")]
    [InlineData("color", "#c0392b", "{{ tint }}", "An expression here is written without braces; they are added when it is used.")]
    public void What_A_Rule_Cannot_Say_Is_Refused(string name, string value, string expression, string refusal)
    {
        var source = Read(Full);

        Assert.Equal(refusal, SvgRecipeRuleEditor.SetRule(source, name, value, expression));
        Assert.Equal(Full, source.ToText());
    }

    [Fact]
    public void A_File_That_Is_Not_A_Recipe_Says_So()
    {
        var source = Read("""<svg xmlns="http://www.w3.org/2000/svg" />""");

        Assert.Equal(
            "This is not a recipe: the root is <svg>.",
            SvgRecipeRuleEditor.SetRule(source, "color", "#c0392b", "tint"));
    }

    [Fact]
    public void A_Rule_Is_Written_Into_A_Recipe_Whose_Declarations_Are_Half_Typed()
    {
        // On purpose: refusing here would make the two halves of a recipe take turns.
        var recipeText = Recipe("""
            <recipe xmlns="EXPR-NS">
              <code>
                <param name="tint" />
              </code>
            </recipe>
            """);

        var source = Read(recipeText);

        Assert.Null(SvgRecipeRuleEditor.SetRule(source, "color", "#c0392b", "tint"));
        Assert.Contains("""<replace color="#c0392b">tint</replace>""", source.ToText());
    }

    [Fact]
    public void Everything_The_Edit_Did_Not_Touch_Is_Left_Alone()
    {
        var recipeText = Recipe("""
            <?xml version="1.0" encoding="utf-8"?>
            <!-- what this recipe is for -->
            <recipe xmlns="EXPR-NS">
              <code>
                <param name='tint' type="color" default="#c0392b" />
              </code>

              <!-- the brand red -->
              <replace color="#c0392b">tint</replace>
            </recipe>
            """);

        var source = Read(recipeText);

        Assert.Null(SvgRecipeRuleEditor.SetRule(source, "color", "#c0392b", "accent"));

        var written = source.ToText();

        Assert.Contains("<!-- what this recipe is for -->", written);
        Assert.Contains("<!-- the brand red -->", written);
        Assert.Contains("""<param name='tint' type="color" default="#c0392b" />""", written);

        // One line differs, and it is the one the expression is on.
        var changed = recipeText.Split('\n').Where((line, index) => line != written.Split('\n')[index]).ToList();

        Assert.Single(changed);
    }
}
