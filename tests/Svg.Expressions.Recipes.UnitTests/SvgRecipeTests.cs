using System.Linq;
using Xunit;

namespace Svg.Expressions.Recipes.UnitTests;

public class SvgRecipeTests
{
    [Fact]
    public void Parse_ReadsDeclarationsAndRules()
    {
        var recipe = SvgRecipe.Parse("""
            <recipe xmlns="https://svg.skia/expr/1.0">
              <code>
                <param name="hue" type="number" default="217" />
                <let name="primary">hsl(hue, 91%, 60%)</let>
              </code>
              <replace color="#3b82f6">primary</replace>
            </recipe>
            """);

        Assert.Collection(
            recipe.Declarations,
            declaration => Assert.Equal("param", declaration.Name.LocalName),
            declaration => Assert.Equal("let", declaration.Name.LocalName));

        var rule = Assert.Single(recipe.ColorRules);
        Assert.Equal("#3b82f6", rule.ColorText);
        Assert.Equal("primary", rule.Expression);
    }

    [Fact]
    public void Parse_MergesSeveralCodeBlocksInOrder()
    {
        var recipe = SvgRecipe.Parse("""
            <recipe xmlns="https://svg.skia/expr/1.0">
              <code><param name="a" type="number" /></code>
              <code><param name="b" type="number" /></code>
            </recipe>
            """);

        Assert.Equal(
            new[] { "a", "b" },
            recipe.Declarations.Select(declaration => (string)declaration.Attribute("name")!));
    }

    [Fact]
    public void Parse_FoldsWhitespaceAndReadsCdata()
    {
        var recipe = SvgRecipe.Parse("""
            <recipe xmlns="https://svg.skia/expr/1.0">
              <replace color="#000"><![CDATA[t < 1
                 ? #ff0000
                 : #00ff00]]></replace>
            </recipe>
            """);

        // The expression ends up in an XML attribute, where any reader would collapse the
        // newlines anyway.
        Assert.Equal("t < 1 ? #ff0000 : #00ff00", recipe.ColorRules[0].Expression);
    }

    [Theory]
    [InlineData("#3b82f6", "#3b82f6")]
    [InlineData("#38f", "#3388ff")]
    [InlineData("rgb(51, 136, 255)", "#3388ff")]
    [InlineData("red", "#ff0000")]
    [InlineData("LIME", "#00ff00")]
    public void Parse_NormalisesColourSpelling(string written, string equivalent)
    {
        var recipe = SvgRecipe.Parse($"""
            <recipe xmlns="https://svg.skia/expr/1.0">
              <replace color="{written}">accent</replace>
            </recipe>
            """);

        Assert.True(SvgRecipeColor.TryParse(equivalent, out var argb));
        Assert.Equal(argb, recipe.ColorRules[0].Argb);
    }

    [Fact]
    public void Parse_RejectsTwoRulesForTheSameColour()
    {
        // '#f00' and 'red' are one colour, so one of the two expressions could never apply.
        var ex = Assert.Throws<SvgRecipeException>(() => SvgRecipe.Parse("""
            <recipe xmlns="https://svg.skia/expr/1.0">
              <replace color="#f00">a</replace>
              <replace color="red">b</replace>
            </recipe>
            """));

        Assert.Contains("same colour", ex.Message);
    }

    [Theory]
    [InlineData("<recipe xmlns=\"https://svg.skia/expr/1.0\"><rplace color=\"red\">a</rplace></recipe>", "not a recipe element")]
    [InlineData("<recipe xmlns=\"https://svg.skia/expr/1.0\"><code><parm name=\"a\" /></code></recipe>", "not a declaration")]
    [InlineData("<recipe xmlns=\"https://svg.skia/expr/1.0\"><replace>a</replace></recipe>", "missing a color")]
    [InlineData("<recipe xmlns=\"https://svg.skia/expr/1.0\"><replace color=\"nonsense\">a</replace></recipe>", "not a colour")]
    [InlineData("<recipe xmlns=\"https://svg.skia/expr/1.0\"><replace color=\"red\"> </replace></recipe>", "no expression")]
    [InlineData("<recipe xmlns=\"https://svg.skia/expr/1.0\"><replace color=\"red\">{{ a }}</replace></recipe>", "must not contain braces")]
    [InlineData("<recipe><replace color=\"red\">a</replace></recipe>", "must be <recipe")]
    [InlineData("<recipe xmlns=\"https://svg.skia/expr/1.0\"><replace color=\"red\">a</replace>", "not well formed")]
    public void Parse_Rejects(string recipeXml, string expected)
    {
        var ex = Assert.Throws<SvgRecipeException>(() => SvgRecipe.Parse(recipeXml));

        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("inherit")]
    [InlineData("currentColor")]
    [InlineData("url(#gradient)")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_KeywordsAndPaintServersAreNotColours(string? value)
    {
        // These select a paint rather than name a colour, so substituting an expression for one
        // would change what the element does rather than what shade it is.
        Assert.False(SvgRecipeColor.TryParse(value, out _));
    }
}
