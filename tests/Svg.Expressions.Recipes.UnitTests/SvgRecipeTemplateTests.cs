using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Svg.Expressions.Recipes.UnitTests;

public class SvgRecipeTemplateTests
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

    // The prototype templates from the design research: one for the PaintCode scheme, two for an
    // unrelated tint scheme, so nothing here leans on one set of let names.
    private const string AccentGlyph = """
        <recipe xmlns="https://svg.skia/expr/1.0" name="Accent glyph">
          <match colors="1" />
          <code>
            <param name="enabled" type="boolean" default="true" />
            <param name="state" type="boolean" default="true" />
            <param name="accent" type="boolean" default="true" />
            <param name="accentColorOn" type="color" default="#fb3e72ff" />
            <param name="isLight" type="boolean" default="false" />
            <param name="whiteColor_" type="color" default="#ffffffff" />
            <param name="blackColor_" type="color" default="#1f2123ff" />
            <let name="whiteColor30">withAlpha(whiteColor_, 0.3)</let>
            <let name="blackColor30">withAlpha(blackColor_, 0.3)</let>
            <let name="stateAccentColor">(enabled and state) ? accent ? accentColorOn : isLight ? whiteColor_ : blackColor_ : isLight ? whiteColor30 : blackColor30</let>
          </code>
          <slot name="glyph" rest="true">stateAccentColor</slot>
        </recipe>
        """;

    private const string AccentDuoLine = """
        <recipe xmlns="https://svg.skia/expr/1.0" name="Accent two-tone (line)">
          <match colors="2-3" strokes="1" />
          <slot name="outline" paint="stroke">stateAccentColor</slot>
          <slot name="underlay" paint="fill" by="lightness" rank="1">stateAccentColor30</slot>
          <slot name="highlight" rest="true" optional="true">stateWhiteColor30</slot>
        </recipe>
        """;

    private const string TintCode = """
          <code>
            <param name="tint" type="color" default="#3b82f6" />
            <param name="enabled" type="boolean" default="true" />
            <param name="disabledOpacity" type="number" default="0.4" />
            <param name="weight" type="number" default="1" />
            <let name="ink">enabled ? tint : withAlpha(tint, disabledOpacity)</let>
          </code>
        """;

    private const string Tinted = $"""
        <recipe xmlns="https://svg.skia/expr/1.0" name="Tinted">
          <match colors="1" />
        {TintCode}
          <slot name="ink" rest="true">ink</slot>
          <slot name="weight" attribute="stroke-width" rest="true" optional="true">$value * weight</slot>
        </recipe>
        """;

    private const string TintedDuo = $"""
        <recipe xmlns="https://svg.skia/expr/1.0" name="Tinted two-tone">
          <match colors="2+" />
        {TintCode}
          <slot name="ink" by="lightness" rank="1">ink</slot>
          <slot name="wash" rest="true">withAlpha(ink, 0.35)</slot>
          <slot name="weight" attribute="stroke-width" rest="true" optional="true">$value * weight</slot>
        </recipe>
        """;

    // Shaped like a Streamline duo line icon: a light fill under a dark outline, with a white glint.
    private const string DuoLineIcon = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" height="24" width="24">
          <path d="M3 6h18v12H3z" fill="#8fbffa" />
          <path d="M5 8h4v2H5z" fill="#ffffff" />
          <path d="M3 6h18v12H3z" fill="none" stroke="#2859c5" stroke-linecap="round" stroke-width="1.5" />
          <path d="M3 10h18" fill="none" stroke="#2859c5" stroke-width="1.5" />
        </svg>
        """;

    private static readonly string[] s_paintCodeNames =
    {
        "enabled", "state", "accent", "accentColorOn", "isLight", "whiteColor_", "blackColor_",
        "whiteColor30", "blackColor30", "stateAccentColor"
    };

    private static string DemoSvg()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is { } && !File.Exists(Path.Combine(directory.FullName, "Svg.Skia.slnx")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(directory!.FullName, "samples", "SvgRecipeDemo", "Svg", "demo.svg"));
    }

    private static string Template(string slots, string match = "")
        => $"""<recipe xmlns="https://svg.skia/expr/1.0">{match}{slots}</recipe>""";

    private static SvgRecipe Bound(string recipeXml, string svg, params string[] palette)
    {
        var binding = SvgRecipe.Parse(recipeXml).Bind(SvgRecipeRewriter.Survey(svg), palette.Length == 0 ? null : palette);

        Assert.True(binding.Recipe is { }, binding.Failure);
        return binding.Recipe!;
    }

    private static string[] Claims(SvgRecipe recipe, string slot)
        => recipe.Rules.Where(rule => rule.Slot?.Name == slot).Select(rule => rule.Key).ToArray();

    // Three colours used 1, 3 and 2 times, in that document order, with #222222 the only stroke.
    private const string ThreeColours = """
        <svg xmlns="http://www.w3.org/2000/svg">
          <rect fill="#dddddd" />
          <rect fill="#222222" stroke="#222222" />
          <rect fill="#222222" />
          <rect fill="#888888" />
          <rect fill="#888888" />
        </svg>
        """;

    // ---- a recipe without the new parts ---------------------------------------------------

    [Fact]
    public void PlainRecipe_HasNothingNewAndBindsToItself()
    {
        var recipe = SvgRecipe.Parse(PrimaryRecipe);

        Assert.Null(recipe.Name);
        Assert.Null(recipe.Size);
        Assert.True(recipe.Padding.IsEmpty);
        Assert.Null(recipe.Match);
        Assert.Empty(recipe.Slots);
        Assert.True(recipe.Matches(Array.Empty<SvgRecipeSurveyValue>()));

        var svg = """<svg xmlns="http://www.w3.org/2000/svg"><rect fill="#3b82f6" /><rect fill="red" /></svg>""";
        var binding = recipe.Bind(SvgRecipeRewriter.Survey(svg));

        Assert.Null(binding.Failure);
        Assert.Equal(recipe.Rules, binding.Recipe!.Rules);
        Assert.Equal(recipe.Declarations, binding.Recipe.Declarations);
        Assert.Equal("#ff0000", Assert.Single(binding.Leftover).Text);
        Assert.Equal(SvgRecipeRewriter.Apply(svg, recipe).Svg, SvgRecipeRewriter.Apply(svg, binding.Recipe).Svg);
    }

    // ---- root attributes ------------------------------------------------------------------

    [Fact]
    public void Parse_ReadsNameSizeAndPadding()
    {
        var recipe = SvgRecipe.Parse("""<recipe xmlns="https://svg.skia/expr/1.0" name=" Accent glyph " size="30" padding="10% 0.05" />""");

        Assert.Equal("Accent glyph", recipe.Name);
        Assert.Equal(30f, recipe.Size);
        Assert.Equal(0.1f, recipe.Padding.Top, 5);
        Assert.Equal(0.05f, recipe.Padding.Right, 5);
        Assert.Equal(0.1f, recipe.Padding.Bottom, 5);
        Assert.Equal(0.05f, recipe.Padding.Left, 5);
    }

    [Theory]
    [InlineData("size=\"0\"", "size is not a positive number")]
    [InlineData("size=\"big\"", "size is not a positive number")]
    [InlineData("padding=\"10\"", "padding")]
    [InlineData("padding=\"1 2 3 4 5\"", "one, two, three or four")]
    public void Parse_RejectsABadSizeOrPadding(string attribute, string expected)
    {
        var ex = Assert.Throws<SvgRecipeException>(() => SvgRecipe.Parse($"""<recipe xmlns="https://svg.skia/expr/1.0" {attribute} />"""));

        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("<slot bogus=\"1\">a</slot>", "has no 'bogus'")]
    [InlineData("<slot attribute=\"d\">a</slot>", "cannot fill 'd'")]
    [InlineData("<slot attribute=\"display\">a</slot>", "cannot fill 'display'")]
    [InlineData("<slot attribute=\"fill\">a</slot>", "cannot fill 'fill'")]
    [InlineData("<slot attribute=\"stroke-width\" paint=\"fill\">a</slot>", "do not apply")]
    [InlineData("<slot attribute=\"stroke-width\" by=\"lightness\">a</slot>", "do not apply")]
    [InlineData("<slot paint=\"d\">a</slot>", "not a colour attribute")]
    [InlineData("<slot by=\"size\">a</slot>", "Expected coverage, lightness or document")]
    [InlineData("<slot rank=\"0\">a</slot>", "rank other than 0")]
    [InlineData("<slot rank=\"1\" rest=\"true\">a</slot>", "rank other than 0")]
    [InlineData("<slot rank=\"first\">a</slot>", "not a whole number")]
    [InlineData("<slot palette=\"0\">a</slot>", "count from 1")]
    [InlineData("<slot rest=\"yes\">a</slot>", "'true' or 'false'")]
    [InlineData("<slot color=\"nonsense\">a</slot>", "not a colour")]
    [InlineData("<slot> </slot>", "no expression")]
    [InlineData("<slot name=\"x\">{{ a }}</slot>", "must not contain braces")]
    [InlineData("<match colors=\"two\" />", "not a count")]
    [InlineData("<match colors=\"2-\" />", "not a count")]
    [InlineData("<match colors=\"99999999999\" />", "not a count")]
    [InlineData("<match colors=\"1-99999999999\" />", "not a count")]
    [InlineData("<match colors=\"\u0663\" />", "not a count")]
    [InlineData("<match shape=\"round\" />", "has no 'shape'")]
    [InlineData("<match has=\"#fff|nonsense\" />", "not a colour")]
    [InlineData("<match /><match />", "one <match>")]
    public void Parse_RejectsABadSlotOrMatch(string body, string expected)
    {
        var ex = Assert.Throws<SvgRecipeException>(() => SvgRecipe.Parse(Template(body)));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Parse_ReadsASlot()
    {
        var slot = Assert.Single(SvgRecipe.Parse(Template("""
            <slot name="s" paint="stroke" by="lightness" rank="-2" color="#F00, #00f" palette="2" optional="true">  withAlpha($value,
              0.5) </slot>
            """)).Slots);

        Assert.Equal("s", slot.Name);
        Assert.Equal("color", slot.Attribute);
        Assert.Equal(ExprType.Color, slot.Type);
        Assert.Equal("stroke", slot.Paint);
        Assert.Equal("lightness", slot.By);
        Assert.Equal(-2, slot.Rank);
        Assert.Equal(new[] { "#ff0000", "#0000ff" }, slot.Colors);
        Assert.Equal(2, slot.Palette);
        Assert.False(slot.Rest);
        Assert.True(slot.Optional);
        Assert.Equal("withAlpha($value, 0.5)", slot.Expression);
    }

    [Fact]
    public void Apply_RefusesATemplateThatWasNotBound()
    {
        var ex = Assert.Throws<SvgRecipeException>(() => SvgRecipeRewriter.Apply(ThreeColours, SvgRecipe.Parse(AccentGlyph)));

        Assert.Contains("Bind it", ex.Message);
    }

    // ---- survey and luminance -------------------------------------------------------------

    [Fact]
    public void Survey_CountsEachValuePerAttribute()
    {
        var survey = SvgRecipeRewriter.Survey(DuoLineIcon);

        var outline = survey.Single(value => value.Text == "#2859c5");
        Assert.Equal(2, outline.Count);
        Assert.Equal(2, outline.Strokes);
        Assert.Equal(0, outline.Fills);

        var underlay = survey.Single(value => value.Text == "#8fbffa");
        Assert.Equal(1, underlay.Fills);
        Assert.Equal(0, underlay.Strokes);

        var width = survey.Single(value => value.Name == "stroke-width");
        Assert.Equal("1.5", width.Text);
        Assert.Equal(2, width.Count);
        Assert.Equal(2, width.Attributes["stroke-width"]);
    }

    [Fact]
    public void Luminance_RunsFromBlackToWhiteAndWeighsGreenMost()
    {
        Assert.Equal(0, SvgRecipeColor.Luminance(SvgRecipeColor.Parse("#000", "")), 6);
        Assert.Equal(1, SvgRecipeColor.Luminance(SvgRecipeColor.Parse("#fff", "")), 6);
        Assert.True(SvgRecipeColor.Luminance(SvgRecipeColor.Parse("#00ff00", "")) > SvgRecipeColor.Luminance(SvgRecipeColor.Parse("#ff0000", "")));
        Assert.True(SvgRecipeColor.Luminance(SvgRecipeColor.Parse("#2859c5", "")) < SvgRecipeColor.Luminance(SvgRecipeColor.Parse("#8fbffa", "")));
    }

    // ---- selectors ------------------------------------------------------------------------

    [Theory]
    [InlineData("", "#222222")]
    [InlineData("by=\"coverage\" rank=\"2\"", "#888888")]
    [InlineData("by=\"coverage\" rank=\"-1\"", "#dddddd")]
    [InlineData("by=\"lightness\"", "#222222")]
    [InlineData("by=\"lightness\" rank=\"-1\"", "#dddddd")]
    [InlineData("by=\"lightness\" rank=\"2\"", "#888888")]
    [InlineData("by=\"document\"", "#dddddd")]
    [InlineData("by=\"document\" rank=\"-1\"", "#888888")]
    [InlineData("paint=\"stroke\"", "#222222")]
    [InlineData("color=\"#888 #ddd\"", "#888888")]
    [InlineData("color=\"#888 #ddd\" by=\"document\"", "#dddddd")]
    [InlineData("palette=\"2\"", "#dddddd")]
    public void Bind_PicksByEachSelector(string selector, string expected)
    {
        var recipe = Bound(Template($"<slot name=\"s\" {selector}>x</slot>"), ThreeColours, "#888888", "#DDD");

        Assert.Equal(new[] { expected }, Claims(recipe, "s"));
    }

    [Theory]
    [InlineData("rank=\"4\"")]
    [InlineData("rank=\"-4\"")]
    [InlineData("paint=\"stop-color\"")]
    [InlineData("color=\"#123456\"")]
    [InlineData("palette=\"3\"")]
    public void Bind_FailsARequiredSlotThatFindsNothing(string selector)
    {
        var binding = SvgRecipe.Parse(Template($"<slot name=\"s\" {selector}>x</slot>"))
            .Bind(SvgRecipeRewriter.Survey(ThreeColours), new[] { "#888888", "#dddddd" });

        Assert.Null(binding.Recipe);
        Assert.Contains("<slot name=\"s\"> found nothing", binding.Failure);
        Assert.Equal(3, binding.Leftover.Count);
    }

    [Fact]
    public void Bind_WithoutAPaletteFindsNothingForAPaletteSlot()
    {
        var binding = SvgRecipe.Parse(Template("<slot palette=\"1\">x</slot>")).Bind(SvgRecipeRewriter.Survey(ThreeColours));

        Assert.Null(binding.Recipe);
    }

    [Fact]
    public void Bind_NeverOffersAClaimedValueToALaterSlot()
    {
        var recipe = Bound(
            Template("""
                <slot name="a">first</slot>
                <slot name="b">second</slot>
                <slot name="c" by="lightness">third</slot>
                """),
            ThreeColours);

        Assert.Equal(new[] { "#222222" }, Claims(recipe, "a"));
        Assert.Equal(new[] { "#888888" }, Claims(recipe, "b"));
        Assert.Equal(new[] { "#dddddd" }, Claims(recipe, "c"));
    }

    [Fact]
    public void Bind_FailsWhenAnEarlierRestSlotTookEverything()
    {
        var binding = SvgRecipe.Parse(Template("""<slot name="all" rest="true">a</slot><slot name="late">b</slot>"""))
            .Bind(SvgRecipeRewriter.Survey(ThreeColours));

        Assert.Contains("<slot name=\"late\">", binding.Failure);
    }

    [Fact]
    public void Bind_LetsAWrittenRuleClaimFirst()
    {
        var recipe = Bound(Template("""<replace color="#222">written</replace><slot name="s">slotted</slot>"""), ThreeColours);

        Assert.Equal("written", recipe.Rules.Single(rule => rule.Key == "#222222").Expression);
        Assert.Null(recipe.Rules.Single(rule => rule.Key == "#222222").Slot);
        Assert.Equal(new[] { "#888888" }, Claims(recipe, "s"));
    }

    [Fact]
    public void Bind_RestTakesEveryCandidateInOrder()
    {
        var recipe = Bound(Template("""<slot name="s" rest="true" by="lightness">x</slot>"""), ThreeColours);

        Assert.Equal(new[] { "#222222", "#888888", "#dddddd" }, Claims(recipe, "s"));
    }

    [Fact]
    public void Bind_LeavesAnOptionalSlotEmpty()
    {
        var binding = SvgRecipe.Parse(Template("""<slot name="s" color="#123456" optional="true">x</slot><slot name="t">y</slot>"""))
            .Bind(SvgRecipeRewriter.Survey(ThreeColours));

        Assert.Empty(Claims(binding.Recipe!, "s"));
        Assert.Equal(new[] { "#222222" }, Claims(binding.Recipe!, "t"));
        Assert.Equal(new[] { "#dddddd", "#888888" }, binding.Leftover.Select(value => value.Text));
    }

    [Fact]
    public void Bind_FailsATemplateWhoseOptionalSlotsAllFoundNothing()
    {
        var binding = SvgRecipe.Parse(Template("""<slot color="#123456" optional="true">x</slot>"""))
            .Bind(SvgRecipeRewriter.Survey(ThreeColours));

        Assert.Null(binding.Recipe);
        Assert.Contains("None of the recipe's slots", binding.Failure);
    }

    [Fact]
    public void Bind_KeepsAWrittenRuleWhenItsOptionalSlotsFindNothing()
    {
        var recipe = Bound(Template("""<replace color="#222">w</replace><slot color="#123456" optional="true">x</slot>"""), ThreeColours);

        Assert.Equal("w", recipe.Rules.Single().Expression);
    }

    [Fact]
    public void Bind_WritesTheMatchedValueForDollarValue()
    {
        var recipe = Bound(
            Template("""
                <slot name="c" rest="true">mix($value, tint)</slot>
                <slot name="w" attribute="stroke-width" rest="true">$value * weight</slot>
                """),
            DuoLineIcon);

        Assert.Equal("mix(#8fbffa, tint)", recipe.Rules.Single(rule => rule.Key == "#8fbffa").Expression);
        var width = recipe.Rules.Single(rule => rule.Name == "stroke-width");
        Assert.Equal("1.5", width.Key);
        Assert.Equal("1.5 * weight", width.Expression);
    }

    [Fact]
    public void Bind_WritesASmallNumberWithoutAnExponent()
    {
        var recipe = Bound(
            Template("""<slot attribute="stroke-width">$value * weight</slot>"""),
            """<svg xmlns="http://www.w3.org/2000/svg"><path stroke-width="0.00001" /></svg>""");

        Assert.Equal("0.00001 * weight", recipe.Rules.Single().Expression);
    }

    // The bound rule is a colour rule, so paint only chooses the value: once chosen, it is
    // rewritten on the fill as well as the stroke.
    [Fact]
    public void Bind_PaintPicksTheValueButTheRuleRewritesEveryUse()
    {
        var recipe = Bound(Template("""<slot name="s" paint="stroke">x</slot>"""), ThreeColours);

        Assert.Equal("color", recipe.Rules.Single().Name);
        var svg = SvgRecipeRewriter.Apply(ThreeColours, recipe).Svg;
        Assert.Contains("fill=\"{{ x }}\" stroke=\"{{ x }}\"", svg);
        Assert.Equal(3, SvgRecipeRewriter.Apply(ThreeColours, recipe).TotalReplacements);
    }

    [Fact]
    public void Bind_KeepsOnlyTheDeclarationsTheTargetLacks()
    {
        var recipe = SvgRecipe.Parse(AccentGlyph);
        var survey = SvgRecipeRewriter.Survey(DemoSvg());

        Assert.Equal(10, recipe.Bind(survey).Recipe!.Declarations.Count);
        Assert.Empty(recipe.Bind(survey, declared: s_paintCodeNames).Recipe!.Declarations);
        Assert.Equal(
            new[] { "stateAccentColor" },
            recipe.Bind(survey, declared: s_paintCodeNames.Take(9)).Recipe!.Declarations.Select(d => (string)d.Attribute("name")!));
    }

    // ---- match ----------------------------------------------------------------------------

    [Theory]
    [InlineData("colors=\"3\"", true)]
    [InlineData("colors=\"2\"", false)]
    [InlineData("colors=\"2-3\"", true)]
    [InlineData("colors=\"4-9\"", false)]
    [InlineData("colors=\"3+\"", true)]
    [InlineData("colors=\"4+\"", false)]
    [InlineData("strokes=\"1\"", true)]
    [InlineData("strokes=\"0\"", false)]
    [InlineData("fills=\"3\"", true)]
    [InlineData("fills=\"1-2\"", false)]
    [InlineData("has=\"#123456|#DDD\"", true)]
    [InlineData("has=\"#123456\"", false)]
    [InlineData("colors=\"3\" strokes=\"2+\"", false)]
    public void Matches_Counts(string condition, bool expected)
    {
        var recipe = SvgRecipe.Parse(Template("", $"<match {condition} />"));

        Assert.Equal(expected, recipe.Matches(SvgRecipeRewriter.Survey(ThreeColours)));
    }

    [Theory]
    [InlineData("family=\"*line*\"", true)]
    [InlineData("family=\"STREAMLINE-*\"", true)]
    [InlineData("family=\"*solid*\"", false)]
    [InlineData("family=\"!*solid*\"", true)]
    [InlineData("family=\"!*line*\"", false)]
    [InlineData("style=\"line\"", true)]
    [InlineData("style=\"l?ne\"", true)]
    [InlineData("style=\"l?\"", false)]
    [InlineData("name=\"house*\"", true)]
    [InlineData("name=\"!house*\"", false)]
    [InlineData("family=\"*line*\" style=\"solid\"", false)]
    public void Matches_Globs(string condition, bool expected)
    {
        var recipe = SvgRecipe.Parse(Template("", $"<match {condition} />"));

        Assert.Equal(expected, recipe.Matches(SvgRecipeRewriter.Survey(ThreeColours), "streamline-plump-line", "line", "house-2"));
    }

    [Fact]
    public void Matches_TreatsAnUnknownFactAsEmpty()
    {
        var survey = SvgRecipeRewriter.Survey(ThreeColours);

        Assert.True(SvgRecipe.Parse(Template("", "<match family=\"*\" />")).Matches(survey));
        Assert.True(SvgRecipe.Parse(Template("", "<match family=\"!*solid*\" />")).Matches(survey));
        Assert.False(SvgRecipe.Parse(Template("", "<match family=\"*line*\" />")).Matches(survey));
    }

    // ---- the prototype templates against real drawings ------------------------------------

    [Fact]
    public void AccentGlyph_TurnsTheDemoIconAccent()
    {
        var svg = DemoSvg();
        var template = SvgRecipe.Parse(AccentGlyph);
        var survey = SvgRecipeRewriter.Survey(svg);

        Assert.Equal("Accent glyph", template.Name);
        Assert.True(template.Matches(survey));

        var binding = template.Bind(survey, declared: s_paintCodeNames);
        var result = SvgRecipeRewriter.Apply(svg, binding.Recipe!);

        Assert.Empty(binding.Leftover);
        Assert.Contains("fill=\"{{ stateAccentColor }}\"", result.Svg);
        Assert.DoesNotContain("e:code", result.Svg);

        // Bound against a group that declares nothing, it carries its own declarations and reads.
        var standalone = SvgRecipeRewriter.Apply(svg, template.Bind(survey).Recipe!).Svg;
        Assert.Equal(10, SvgExpressionDeclarations.Parse(standalone).Parameters.Count + SvgExpressionDeclarations.Parse(standalone).Lets.Count);
    }

    [Fact]
    public void AccentGlyph_DoesNotMatchATwoToneIcon()
    {
        Assert.False(SvgRecipe.Parse(AccentGlyph).Matches(SvgRecipeRewriter.Survey(DuoLineIcon)));
    }

    [Fact]
    public void AccentDuoLine_BindsOutlineUnderlayAndHighlight()
    {
        var template = SvgRecipe.Parse(AccentDuoLine);
        var survey = SvgRecipeRewriter.Survey(DuoLineIcon);

        Assert.True(template.Matches(survey, "streamline-plump-duo", "duo", "window"));
        Assert.False(template.Matches(SvgRecipeRewriter.Survey(DemoSvg())));

        var recipe = template.Bind(survey).Recipe!;

        Assert.Equal(new[] { "#2859c5" }, Claims(recipe, "outline"));
        Assert.Equal(new[] { "#8fbffa" }, Claims(recipe, "underlay"));
        Assert.Equal(new[] { "#ffffff" }, Claims(recipe, "highlight"));

        var svg = SvgRecipeRewriter.Apply(DuoLineIcon, recipe).Svg;
        Assert.Contains("stroke=\"{{ stateAccentColor }}\"", svg);
        Assert.Contains("fill=\"{{ stateAccentColor30 }}\"", svg);
        Assert.Contains("fill=\"{{ stateWhiteColor30 }}\"", svg);
    }

    [Fact]
    public void Tinted_BindsTheDemoIconAndItsStrokeWidth()
    {
        var svg = DemoSvg();
        var template = SvgRecipe.Parse(Tinted);
        var survey = SvgRecipeRewriter.Survey(svg);

        Assert.True(template.Matches(survey));
        Assert.False(template.Matches(SvgRecipeRewriter.Survey(DuoLineIcon)));

        var result = SvgRecipeRewriter.Apply(svg, template.Bind(survey).Recipe!).Svg;

        Assert.Contains("fill=\"{{ ink }}\"", result);
        Assert.Contains("stroke-width=\"{{ 1 * weight }}\"", result);
        Assert.Equal(4, SvgExpressionDeclarations.Parse(result).Parameters.Count);
    }

    [Fact]
    public void TintedDuo_InksTheDarkestAndWashesTheRest()
    {
        var template = SvgRecipe.Parse(TintedDuo);
        var survey = SvgRecipeRewriter.Survey(DuoLineIcon);

        Assert.True(template.Matches(survey));
        Assert.False(template.Matches(SvgRecipeRewriter.Survey(DemoSvg())));

        var binding = template.Bind(survey, declared: new[] { "tint", "enabled", "disabledOpacity", "weight", "ink" });
        var recipe = binding.Recipe!;

        Assert.Empty(recipe.Declarations);
        Assert.Empty(binding.Leftover);
        Assert.Equal(new[] { "#2859c5" }, Claims(recipe, "ink"));
        Assert.Equal(new[] { "#8fbffa", "#ffffff" }, Claims(recipe, "wash"));

        var svg = SvgRecipeRewriter.Apply(DuoLineIcon, recipe).Svg;
        Assert.Contains("stroke=\"{{ ink }}\"", svg);
        Assert.Contains("fill=\"{{ withAlpha(ink, 0.35) }}\"", svg);
        Assert.Contains("stroke-width=\"{{ 1.5 * weight }}\"", svg);
    }
}
