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

        var rule = Assert.Single(recipe.Rules);
        Assert.Equal("#3b82f6", rule.ValueText);
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
        Assert.Equal("t < 1 ? #ff0000 : #00ff00", recipe.Rules[0].Expression);
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

        Assert.Equal(equivalent, recipe.Rules[0].Key);
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
    [InlineData("opacity", "0.5", "0.5")]
    [InlineData("opacity", ".5", "0.5")]
    [InlineData("fill-opacity", "0.50", "0.5")]
    [InlineData("stroke-opacity", "1", "1")]
    // A keyword is compared the way the pipeline compares it, which is without regard to case.
    [InlineData("display", "NONE", "none")]
    [InlineData("visibility", "Hidden", "hidden")]
    public void Parse_ReadsARuleForSomethingOtherThanAColour(string name, string written, string key)
    {
        var recipe = SvgRecipe.Parse($"""
            <recipe xmlns="https://svg.skia/expr/1.0">
              <replace {name}="{written}">shown</replace>
            </recipe>
            """);

        var rule = Assert.Single(recipe.Rules);

        Assert.Equal(name, rule.Name);
        Assert.Equal(written, rule.ValueText);
        Assert.Equal(key, rule.Key);
        Assert.Equal("shown", rule.Expression);
    }

    /// <summary>
    /// Two names for one value cannot both apply. Colours have one name for every attribute they
    /// paint and everything else is named per attribute, so the two can never collide across names
    /// and this is the whole of the check.
    /// </summary>
    [Fact]
    public void Parse_RejectsTwoRulesForTheSameOpacity()
    {
        var ex = Assert.Throws<SvgRecipeException>(() => SvgRecipe.Parse("""
            <recipe xmlns="https://svg.skia/expr/1.0">
              <replace opacity="0.5">a</replace>
              <replace opacity=".50">b</replace>
            </recipe>
            """));

        Assert.Contains("same number", ex.Message);
    }

    /// <summary>An opacity and a stop-opacity of the same value are different quantities.</summary>
    [Fact]
    public void Parse_KeepsTheSameNumberOnTwoAttributesApart()
    {
        var recipe = SvgRecipe.Parse("""
            <recipe xmlns="https://svg.skia/expr/1.0">
              <replace opacity="0.5">a</replace>
              <replace stop-opacity="0.5">b</replace>
            </recipe>
            """);

        Assert.Equal(new[] { "opacity", "stop-opacity" }, recipe.Rules.Select(rule => rule.Name).ToArray());
    }

    [Theory]
    [InlineData("<recipe xmlns=\"https://svg.skia/expr/1.0\"><rplace color=\"red\">a</rplace></recipe>", "not a recipe element")]
    [InlineData("<recipe xmlns=\"https://svg.skia/expr/1.0\"><code><parm name=\"a\" /></code></recipe>", "not a declaration")]
    [InlineData("<recipe xmlns=\"https://svg.skia/expr/1.0\"><replace>a</replace></recipe>", "missing a value to replace")]
    [InlineData("<recipe xmlns=\"https://svg.skia/expr/1.0\"><replace color=\"nonsense\">a</replace></recipe>", "not a colour")]
    [InlineData("<recipe xmlns=\"https://svg.skia/expr/1.0\"><replace color=\"red\"> </replace></recipe>", "no expression")]
    [InlineData("<recipe xmlns=\"https://svg.skia/expr/1.0\"><replace color=\"red\">{{ a }}</replace></recipe>", "must not contain braces")]
    [InlineData("<recipe><replace color=\"red\">a</replace></recipe>", "must be <recipe")]
    [InlineData("<recipe xmlns=\"https://svg.skia/expr/1.0\"><replace color=\"red\">a</replace>", "not well formed")]
    // A value the language cannot drive at all, named as an attribute.
    [InlineData("<recipe xmlns=\"https://svg.skia/expr/1.0\"><replace stroke-width=\"1\">w</replace></recipe>", "cannot replace 'stroke-width'")]
    // A colour attribute is replaceable, but only under the one name that claims all of them.
    [InlineData("<recipe xmlns=\"https://svg.skia/expr/1.0\"><replace fill=\"red\">a</replace></recipe>", "one attribute at a time")]
    [InlineData("<recipe xmlns=\"https://svg.skia/expr/1.0\"><replace color=\"red\" opacity=\"1\">a</replace></recipe>", "a rule replaces one value")]
    [InlineData("<recipe xmlns=\"https://svg.skia/expr/1.0\"><replace opacity=\"half\">a</replace></recipe>", "not a number")]
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
