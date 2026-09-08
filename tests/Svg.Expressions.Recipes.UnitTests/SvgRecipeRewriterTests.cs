using System;
using System.Linq;
using Xunit;

namespace Svg.Expressions.Recipes.UnitTests;

public class SvgRecipeRewriterTests
{
    private const string PrimaryRecipe = """
        <recipe xmlns="https://svg.skia/expr/1.0">
          <code>
            <param name="hue" type="number" default="217" />
            <let name="primary">hsl(hue, 91%, 60%)</let>
          </code>
          <replace color="#3b82f6">primary</replace>
        </recipe>
        """;

    private static SvgRecipeResult Apply(string svg, string? recipeXml = null)
        => SvgRecipeRewriter.Apply(svg, SvgRecipe.Parse(recipeXml ?? PrimaryRecipe));

    // ---- what a drawing offers a rule for -------------------------------------------------

    [Fact]
    public void Survey_ListsEachColourOnceWithHowMuchItPaints()
    {
        var colours = SvgRecipeRewriter.Survey("""
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect fill="#3b82f6" stroke="red" />
              <rect fill="rgb(59, 130, 246)" />
              <circle fill="#3B82F6" />
            </svg>
            """);

        // By value, not by spelling — the same rule matching would claim all three.
        Assert.Equal(new[] { "#3b82f6", "#ff0000" }, colours.Select(colour => colour.Text).ToArray());
        Assert.Equal(new[] { 3, 1 }, colours.Select(colour => colour.Count).ToArray());
    }

    [Fact]
    public void Survey_TakesTheStyleDeclarationAndNotTheDeadAttributeUnderIt()
    {
        // The one rule that must not be re-implemented beside the rewrite: offering the attribute
        // here would offer a colour the rewrite then refuses to replace, because it never paints.
        var colours = SvgRecipeRewriter.Survey(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect fill="#3b82f6" style="fill:#ff0000" /></svg>""");

        Assert.Equal("#ff0000", Assert.Single(colours).Text);
    }

    [Fact]
    public void Survey_LeavesOutWhatNoRuleCouldClaim()
    {
        var colours = SvgRecipeRewriter.Survey("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <rect fill="{{ primary }}" stroke="none" />
              <rect fill="url(#gradient)" stroke="currentColor" />
              <rect fill="#3b82f6" />
            </svg>
            """);

        Assert.Equal("#3b82f6", Assert.Single(colours).Text);
    }

    [Fact]
    public void Survey_ReadsADocumentThatIsAlreadyInTheExpressionFormat()
    {
        // Apply refuses one of these, and rightly. A survey is asked precisely to find what is
        // still literal in a drawing somebody has begun converting.
        var colours = SvgRecipeRewriter.Survey("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code><e:param name="hue" type="number" default="217" /></e:code></defs>
              <rect fill="{{ hsl(hue, 91%, 60%) }}" stroke="#3b82f6" />
            </svg>
            """);

        Assert.Equal("#3b82f6", Assert.Single(colours).Text);
    }

    /// <summary>
    /// The drawing the invariant below is held against: every attribute an expression can drive,
    /// spread over attributes and style declarations, with values a rule must not claim mixed in.
    /// </summary>
    private const string Everything = """
        <svg xmlns="http://www.w3.org/2000/svg">
          <rect fill="#3b82f6" style="fill:#ff0000" opacity="0.5" />
          <rect stroke="rgb(59,130,246)" fill-opacity="0.5" style="stroke-opacity:.5" />
          <rect fill="none" stroke="{{ kept }}" display="none" visibility="hidden" />
          <filter>
            <feFlood flood-color="#3b82f6" />
            <feDiffuseLighting lighting-color="red" />
          </filter>
          <linearGradient>
            <stop stop-color="red" stop-opacity="0.25" />
          </linearGradient>
        </svg>
        """;

    [Fact]
    public void Survey_ListsExactlyWhatARuleWouldClaim()
    {
        // The two halves of one walk, held against each other: for every value listed, a rule
        // naming it replaces that many attributes and no others. A survey that counted the dead
        // attribute under a style declaration would say two here and the rewrite would say one.
        var values = SvgRecipeRewriter.Survey(Everything);

        Assert.NotEmpty(values);

        foreach (var value in values)
        {
            var rule = $"""
                <recipe xmlns="https://svg.skia/expr/1.0">
                  <replace {value.Name}="{value.Text}">painted</replace>
                </recipe>
                """;

            Assert.Equal(value.Count, Apply(Everything, rule).TotalReplacements);
        }
    }

    /// <summary>Every attribute the language lifts is reached, and each is named as its own rule.</summary>
    /// <remarks>
    /// The walk used to carry its own list of three attributes beside a table of eleven, so
    /// flood-color and lighting-color went unsurveyed and unreplaced with nothing said. Reading the
    /// table is what keeps the two from drifting again; this is what would notice if they did.
    /// </remarks>
    [Fact]
    public void Survey_ReachesEveryAttributeAnExpressionCanDrive()
    {
        var values = SvgRecipeRewriter.Survey(Everything);

        Assert.Equal(
            new[] { "color", "display", "fill-opacity", "opacity", "stop-opacity", "stroke-opacity", "visibility" },
            values.Select(value => value.Name).Distinct().OrderBy(name => name, StringComparer.Ordinal).ToArray());

        // #3b82f6 on stroke (spelled rgb) and on flood-color: one rule, two places. The fill under
        // a style declaration that overrides it is not one of them.
        var blue = values.Single(value => value.Text == "#3b82f6");

        Assert.Equal("color", blue.Name);
        Assert.Equal(2, blue.Count);

        // 'red' on lighting-color and stop-color, plus the winning style declaration. The old
        // three-attribute walk reached neither of the first two.
        Assert.Equal(3, values.Single(value => value.Text == "#ff0000").Count);
    }

    [Fact]
    public void Apply_ReplacesAColourInFloodAndLighting()
    {
        var written = Apply("""
            <svg xmlns="http://www.w3.org/2000/svg">
              <filter>
                <feFlood flood-color="#3b82f6" />
                <feDiffuseLighting lighting-color="#3b82f6" />
              </filter>
            </svg>
            """).Svg;

        Assert.Contains("flood-color=\"{{ primary }}\"", written);
        Assert.Contains("lighting-color=\"{{ primary }}\"", written);
    }

    [Theory]
    [InlineData("opacity", "0.5", "0.5")]
    [InlineData("opacity", "0.5", ".5")]
    [InlineData("opacity", "0.5", "0.50")]
    [InlineData("stop-opacity", "0.25", "0.25")]
    [InlineData("display", "none", "NONE")]
    [InlineData("visibility", "hidden", "Hidden")]
    public void Apply_ReplacesAValueThatIsNotAColour(string name, string ruleValue, string written)
    {
        var recipe = $"""
            <recipe xmlns="https://svg.skia/expr/1.0">
              <replace {name}="{ruleValue}">shown</replace>
            </recipe>
            """;

        var result = Apply($"""<svg xmlns="http://www.w3.org/2000/svg"><rect {name}="{written}" /></svg>""", recipe);

        Assert.Equal(1, result.TotalReplacements);
        Assert.Contains($"{name}=\"{{{{ shown }}}}\"", result.Svg);
    }

    [Fact]
    public void Apply_ReplacesAnOpacityWrittenAsAStyleDeclaration()
    {
        var recipe = """
            <recipe xmlns="https://svg.skia/expr/1.0">
              <replace opacity="0.5">fade</replace>
            </recipe>
            """;

        var result = Apply("""<svg xmlns="http://www.w3.org/2000/svg"><rect style="opacity:.5" /></svg>""", recipe);

        Assert.Equal(1, result.TotalReplacements);
        Assert.Contains("style=\"opacity:{{ fade }}\"", result.Svg);
    }

    /// <summary>
    /// One element carrying two of them in one style declaration. The flag saying the style was
    /// rewritten used to be assigned rather than or-ed, which only three attributes and one write
    /// per element kept from showing.
    /// </summary>
    [Fact]
    public void Apply_ReplacesTwoDeclarationsInOneStyleAttribute()
    {
        var recipe = """
            <recipe xmlns="https://svg.skia/expr/1.0">
              <replace color="#ff0000">accent</replace>
              <replace opacity="0.5">fade</replace>
            </recipe>
            """;

        var result = Apply(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect style="fill:#ff0000;opacity:0.5" /></svg>""",
            recipe);

        Assert.Equal(2, result.TotalReplacements);
        Assert.Contains("style=\"fill:{{ accent }};opacity:{{ fade }}\"", result.Svg);
    }

    [Theory]
    [InlineData("fill")]
    [InlineData("stroke")]
    [InlineData("stop-color")]
    public void Apply_RewritesEveryPaintAttribute(string name)
    {
        var result = Apply($"""<svg xmlns="http://www.w3.org/2000/svg"><rect {name}="#3b82f6" /></svg>""");

        Assert.Contains($"{name}=\"{{{{ primary }}}}\"", result.Svg);
        Assert.Equal(1, result.TotalReplacements);
    }

    [Theory]
    [InlineData("#3b82f6")]
    [InlineData("#3B82F6")]
    [InlineData("rgb(59, 130, 246)")]
    [InlineData("rgb(59,130,246)")]
    public void Apply_MatchesByValueNotBySpelling(string written)
    {
        // A recipe written against one spelling has to claim the others, or converting a drawing
        // would depend on which tool exported it.
        var result = Apply($"""<svg xmlns="http://www.w3.org/2000/svg"><rect fill="{written}" /></svg>""");

        Assert.Contains("fill=\"{{ primary }}\"", result.Svg);
    }

    [Fact]
    public void Apply_MatchesShortHexAndColourNames()
    {
        var recipe = """
            <recipe xmlns="https://svg.skia/expr/1.0">
              <replace color="#3388ff">a</replace>
              <replace color="#ff0000">b</replace>
            </recipe>
            """;

        var result = Apply(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect fill="#38f" stroke="red" /></svg>""",
            recipe);

        Assert.Contains("fill=\"{{ a }}\"", result.Svg);
        Assert.Contains("stroke=\"{{ b }}\"", result.Svg);
    }

    [Fact]
    public void Apply_LeavesPaintServersAndKeywordsAlone()
    {
        var result = Apply("""
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect fill="url(#g)" stroke="none" />
              <rect fill="currentColor" stroke="#123456" />
            </svg>
            """);

        Assert.Contains("fill=\"url(#g)\"", result.Svg);
        Assert.Contains("stroke=\"none\"", result.Svg);
        Assert.Contains("fill=\"currentColor\"", result.Svg);
        Assert.Contains("stroke=\"#123456\"", result.Svg);
        Assert.Equal(0, result.TotalReplacements);
    }

    [Fact]
    public void Apply_RewritesAMatchedStyleDeclarationWhereItStands()
    {
        // A declaration is lifted like an attribute, so the colour is replaced in place rather
        // than promoted out of 'style' as it once had to be.
        var result = Apply("""
            <svg xmlns="http://www.w3.org/2000/svg"><path style="fill:#3b82f6;stroke-width:2" /></svg>
            """);

        Assert.Contains("style=\"fill:{{ primary }};stroke-width:2\"", result.Svg);
        Assert.DoesNotContain("fill=\"", result.Svg);
        Assert.DoesNotContain("fill:#3b82f6", result.Svg);
    }

    [Fact]
    public void Apply_KeepsTheStyleAttributeWhenItsOnlyDeclarationIsRewritten()
    {
        var result = Apply("""
            <svg xmlns="http://www.w3.org/2000/svg"><path style="fill:#3b82f6" /></svg>
            """);

        Assert.Contains("style=\"fill:{{ primary }}\"", result.Svg);
        Assert.DoesNotContain("fill=\"", result.Svg);
    }

    [Fact]
    public void Apply_IgnoresAPresentationAttributeShadowedByStyle()
    {
        // 'style' wins in the cascade, so the attribute underneath never painted; rewriting it
        // would produce an expression that silently does nothing.
        var result = Apply("""
            <svg xmlns="http://www.w3.org/2000/svg"><path style="fill:#999999" fill="#3b82f6" /></svg>
            """);

        Assert.Contains("fill=\"#3b82f6\"", result.Svg);
        Assert.Equal(0, result.TotalReplacements);
    }

    [Fact]
    public void Apply_KeepsSemicolonsInsideAStyleValue()
    {
        var result = Apply("""
            <svg xmlns="http://www.w3.org/2000/svg"><path style="fill:#3b82f6;mask:url(#a;b)" /></svg>
            """);

        // The semicolon inside url(...) is not a declaration boundary, so the mask survives whole
        // beside the rewritten colour.
        Assert.Contains("style=\"fill:{{ primary }};mask:url(#a;b)\"", result.Svg);
    }

    [Fact]
    public void Apply_InjectsTheCodeBlockFirstInExistingDefs()
    {
        var result = Apply("""
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <linearGradient id="g" />
              </defs>
            </svg>
            """);

        Assert.Contains("xmlns:e=\"https://svg.skia/expr/1.0\"", result.Svg);
        Assert.Contains("<e:param name=\"hue\" type=\"number\" default=\"217\" />", result.Svg);
        Assert.Contains("<e:let name=\"primary\">hsl(hue, 91%, 60%)</e:let>", result.Svg);
        Assert.True(result.Svg.IndexOf("<e:code>") < result.Svg.IndexOf("<linearGradient"));
    }

    [Fact]
    public void Apply_CopiesRangeAttributesVerbatim()
    {
        // Declarations are copied as XML rather than re-serialised from a model, so a recipe carries
        // whatever the format grows. That is a property of `new XElement(element)` and nothing here
        // mentions ranges, which is exactly why it is worth pinning.
        const string recipe = """
            <recipe xmlns="https://svg.skia/expr/1.0">
              <code>
                <param name="hue" type="number" default="217" min="0" max="360" step="1" />
                <let name="primary">hsl(hue, 91%, 60%)</let>
              </code>
              <replace color="#3b82f6">primary</replace>
            </recipe>
            """;

        var result = Apply("""<svg xmlns="http://www.w3.org/2000/svg"><rect fill="#3b82f6" /></svg>""", recipe);

        Assert.Contains(
            "<e:param name=\"hue\" type=\"number\" default=\"217\" min=\"0\" max=\"360\" step=\"1\" />",
            result.Svg);
    }

    [Fact]
    public void Apply_CreatesDefsWhenTheDocumentHasNone()
    {
        var result = Apply("""<svg xmlns="http://www.w3.org/2000/svg"><rect fill="#3b82f6" /></svg>""");

        Assert.Contains("<defs>", result.Svg);
        Assert.Contains("<e:code>", result.Svg);
        Assert.True(result.Svg.IndexOf("<defs>") < result.Svg.IndexOf("<rect"));
    }

    [Fact]
    public void Apply_AddsNoCodeBlockWhenTheRecipeDeclaresNothing()
    {
        var result = Apply(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect fill="#3b82f6" /></svg>""",
            """
            <recipe xmlns="https://svg.skia/expr/1.0">
              <replace color="#3b82f6">#ff0000</replace>
            </recipe>
            """);

        Assert.DoesNotContain("<defs>", result.Svg);
        Assert.Contains("fill=\"{{ #ff0000 }}\"", result.Svg);
    }

    [Fact]
    public void Apply_ReusesAnExistingPrefixForTheExtensionNamespace()
    {
        var result = Apply("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:x="https://svg.skia/expr/1.0"><rect /></svg>
            """);

        Assert.Contains("<x:code>", result.Svg);
        Assert.DoesNotContain("xmlns:e=", result.Svg);
    }

    [Fact]
    public void Apply_PicksAFreePrefixWhenEIsTaken()
    {
        var result = Apply("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="http://example.com/other"><rect /></svg>
            """);

        Assert.Contains("xmlns:e2=\"https://svg.skia/expr/1.0\"", result.Svg);
        Assert.Contains("<e2:code>", result.Svg);
    }

    [Fact]
    public void Apply_PreservesTheDeclarationAndTheSourceLayout()
    {
        var svg = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
                  + "<svg xmlns=\"http://www.w3.org/2000/svg\">\n"
                  + "  <!-- a comment -->\n"
                  + "  <rect fill=\"#3b82f6\"\n"
                  + "        width=\"10\" />\n"
                  + "</svg>\n";

        var result = Apply(svg);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", result.Svg);
        Assert.Contains("<!-- a comment -->", result.Svg);
        Assert.Contains("\n  <rect ", result.Svg);

        // Whitespace between elements survives, but an XML reader does not retain the layout
        // inside a tag, so attributes spread over several lines come back on one.
        Assert.Contains("<rect fill=\"{{ primary }}\" width=\"10\" />", result.Svg);

        Assert.EndsWith("</svg>\n", result.Svg);
    }

    [Fact]
    public void Apply_LeavesAnAttributeThatIsAlreadyAnExpression()
    {
        var result = Apply("""
            <svg xmlns="http://www.w3.org/2000/svg"><rect fill="{{ handWritten }}" /></svg>
            """);

        Assert.Contains("fill=\"{{ handWritten }}\"", result.Svg);
        Assert.Equal(0, result.TotalReplacements);
    }

    /// <summary>
    /// Applying a recipe to its own output does nothing, rather than failing.
    /// </summary>
    /// <remarks>
    /// It used to be refused outright. Only the declarations were ever the problem — a second copy
    /// of a name makes a document that reads as "declared more than once" — and replacing was
    /// already idempotent, since a value written as an expression is no longer a literal to claim.
    /// A build re-run over its own output, and a conversion run again over a set half of which was
    /// converted already, both turn on this.
    /// </remarks>
    [Fact]
    public void Apply_OverItsOwnOutputChangesNothing()
    {
        var converted = Apply("""<svg xmlns="http://www.w3.org/2000/svg"><rect fill="#3b82f6" /></svg>""").Svg;

        var again = Apply(converted);

        Assert.Equal(converted, again.Svg);
        Assert.Equal(0, again.TotalReplacements);

        // One block and one declaration of the name, which is what makes it still read.
        Assert.Equal(1, Occurrences(again.Svg, "<e:code>"));
        Assert.Equal(1, Occurrences(again.Svg, "name=\"hue\""));
    }

    [Fact]
    public void Apply_RefusesADocumentThatDeclaresTheSameNameDifferently()
    {
        // The one case that was ever broken: two declarations of one name, which no reader accepts.
        const string mine = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code><e:param name="hue" type="number" default="20" /></e:code></defs>
              <rect fill="#3b82f6" />
            </svg>
            """;

        var ex = Assert.Throws<SvgRecipeException>(() => Apply(mine));

        Assert.Contains("'hue'", ex.Message);
        Assert.Contains("differently", ex.Message);
    }

    /// <summary>A drawing with declarations of its own takes a recipe that collides with none.</summary>
    [Fact]
    public void Apply_AddsToDeclarationsTheDocumentAlreadyHas()
    {
        const string mine = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code><e:param name="fade" type="number" default="1" /></e:code></defs>
              <rect fill="#3b82f6" fill-opacity="{{ fade }}" />
            </svg>
            """;

        var result = Apply(mine);

        Assert.Equal(1, result.TotalReplacements);
        Assert.Contains("fill=\"{{ primary }}\"", result.Svg);

        // Both names, and the one it arrived with is untouched.
        Assert.Equal(1, Occurrences(result.Svg, "name=\"fade\""));
        Assert.Equal(1, Occurrences(result.Svg, "name=\"hue\""));
    }

    private static int Occurrences(string text, string what)
    {
        var count = 0;

        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void Apply_RejectsADocumentThatIsNotSvg()
    {
        var ex = Assert.Throws<SvgRecipeException>(() => Apply("<html><body /></html>"));

        Assert.Contains("not <svg>", ex.Message);
    }

    [Fact]
    public void Apply_RejectsMalformedXml()
    {
        var ex = Assert.Throws<SvgRecipeException>(() => Apply("""<svg xmlns="http://www.w3.org/2000/svg"><rect>"""));

        Assert.Contains("not well formed", ex.Message);
    }

    [Fact]
    public void Apply_ReportsRulesThatMatchedNothing()
    {
        // Not an error: one recipe usually covers a family of drawings, and not every drawing
        // uses every colour.
        var result = Apply("""<svg xmlns="http://www.w3.org/2000/svg"><rect fill="#123456" /></svg>""");

        var unmatched = Assert.Single(result.UnmatchedRules);
        Assert.Equal("#3b82f6", unmatched.ValueText);
    }

    [Fact]
    public void Apply_CountsEveryOccurrenceOfAColour()
    {
        var result = Apply("""
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect fill="#3b82f6" stroke="#3B82F6" />
              <g><circle style="fill:rgb(59,130,246)" /></g>
              <linearGradient><stop stop-color="#3b82f6" /></linearGradient>
            </svg>
            """);

        Assert.Equal(4, result.Matches.Single().Count);
    }
}
