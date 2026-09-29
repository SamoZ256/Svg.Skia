using System;
using System.IO;
using System.Linq;
using Svg.Expressions.Recipes;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// Which of a project's import templates an incoming icon is offered, in what order, and how the
/// icon is made ready for them.
/// </summary>
[Collection("settings")]
public class TemplateLibraryTests : IDisposable
{
    // Two schemes: the user's PaintCode one, whose names the group declares, and an unrelated tint
    // one that brings its own. Neither template names a colour.
    private const string Templates = """
          <e:templates xmlns:e="https://svg.skia/expr/1.0">
            <e:recipe name="Accent glyph">
              <e:match colors="1" />
              <e:code><e:let name="stateAccentColor">#fb3e72</e:let></e:code>
              <e:slot name="glyph" rest="true">stateAccentColor</e:slot>
            </e:recipe>
            <e:recipe name="Mono glyph">
              <e:match colors="1" />
              <e:slot name="glyph" rest="true">stateBlackColor</e:slot>
            </e:recipe>
            <e:recipe name="Accent two-tone (line)">
              <e:match colors="2-3" strokes="1" />
              <e:slot name="outline" paint="stroke">stateAccentColor</e:slot>
              <e:slot name="underlay" paint="fill" by="lightness" rank="1">stateAccentColor30</e:slot>
              <e:slot name="highlight" rest="true" optional="true">stateWhiteColor30</e:slot>
            </e:recipe>
            <e:recipe name="Tinted two-tone">
              <e:match colors="2+" family="*tint*" />
              <e:code>
                <e:param name="tint" type="color" default="#3b82f6" />
                <e:let name="ink">tint</e:let>
              </e:code>
              <e:slot name="ink" by="lightness" rank="1">ink</e:slot>
              <e:slot name="wash" rest="true">withAlpha(ink, 0.35)</e:slot>
              <e:slot attribute="stroke-width" rest="true" optional="true">$value * 2</e:slot>
            </e:recipe>
            <e:recipe name="Broken"><e:slot by="size">ink</e:slot></e:recipe>
          </e:templates>
        """;

    private const string Glyph = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M2 2h20v20z" fill="#000000" /></svg>""";

    private const string TwoTone = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none">
          <path d="M2 2h20v20z" fill="#d7e0ff" />
          <path d="M2 2h20v20z" stroke="#4147d5" stroke-width="1.5" />
        </svg>
        """;

    private readonly string _was = TemplateLibrary.ChoicesStore;
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public TemplateLibraryTests() => TemplateLibrary.ChoicesStore = Path.Combine(_directory, "templates-choices");

    public void Dispose()
    {
        TemplateLibrary.ChoicesStore = _was;

        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void A_Broken_Template_Is_Listed_With_Its_Error_And_Never_Offered()
    {
        var (library, group) = Library();

        Assert.Equal(
            new[] { "Accent glyph", "Mono glyph", "Accent two-tone (line)", "Tinted two-tone", "Broken", TemplateLibrary.KeepColoursName },
            library.Templates.Select(template => template.Name));

        var broken = library.Templates.Single(template => template.Name == "Broken");
        Assert.Null(broken.Recipe);
        Assert.Contains("by=\"size\"", broken.Error, StringComparison.Ordinal);

        Assert.DoesNotContain(library.Suggest(TemplateLibrary.Prepare(Glyph), group), one => one.Template == broken);
    }

    [Fact]
    public void Accent_And_Mono_Tie_On_A_Single_Colour_Glyph_And_Keep_Colours_Is_Last()
    {
        var (library, group) = Library();

        var suggestions = library.Suggest(TemplateLibrary.Prepare(Glyph), group);

        Assert.Equal(
            new[] { "Accent glyph", "Mono glyph", TemplateLibrary.KeepColoursName },
            suggestions.Select(one => one.Template.Name));

        // Only a rest slot claims the colour, so the two score the same and the first wants checking.
        Assert.Equal(suggestions[0].Score, suggestions[1].Score);
        Assert.All(suggestions, one => Assert.False(one.Sure));

        // The group already declares stateAccentColor, so the template brings nothing.
        Assert.Empty(suggestions[0].Recipe.Declarations);
    }

    [Fact]
    public void A_Two_Tone_Line_Icon_Is_Sure_Of_The_Two_Tone_Template()
    {
        var (library, group) = Library();

        var first = library.Suggest(TemplateLibrary.Prepare(TwoTone, family: "streamline-light"), group)[0];

        Assert.Equal("Accent two-tone (line)", first.Template.Name);
        Assert.True(first.Sure);

        var applied = SvgRecipeRewriter.Apply(TemplateLibrary.Prepare(TwoTone).Text, first.Recipe).Svg;
        Assert.Contains("stroke=\"{{ stateAccentColor }}\"", applied, StringComparison.Ordinal);
        Assert.Contains("fill=\"{{ stateAccentColor30 }}\"", applied, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Tint_Family_Is_Offered_The_Tint_Template_With_What_The_Group_Lacks()
    {
        var (library, group) = Library();

        var suggestions = library.Suggest(TemplateLibrary.Prepare(TwoTone, family: "tint-duo"), group);
        var tinted = suggestions.Single(one => one.Template.Name == "Tinted two-tone");

        // Two predicates each, but the tint's wash is a rest slot: 2 + 1 claimed against 2 + 2.
        Assert.Equal(new[] { "Accent two-tone (line)", "Tinted two-tone", TemplateLibrary.KeepColoursName }, suggestions.Select(one => one.Template.Name));
        Assert.Equal(suggestions[0].Score - 1, tinted.Score);
        Assert.Equal(new[] { "tint", "ink" }, tinted.Recipe.Declarations.Select(declaration => (string?)declaration.Attribute("name")));
        Assert.Contains(tinted.Recipe.Rules, rule => rule.Name == "stroke-width" && rule.Expression == "1.5 * 2");
    }

    [Fact]
    public void A_Remembered_Choice_Flips_The_Ranking_And_Is_Sure()
    {
        var (library, group) = Library();
        var icon = TemplateLibrary.Prepare(Glyph, family: "streamline-light", style: "line");

        TemplateLibrary.Remember(icon, "Mono glyph");

        var first = library.Suggest(icon, group)[0];

        Assert.Equal("Mono glyph", first.Template.Name);
        Assert.True(first.Sure);
        Assert.Contains("streamline-light|line|#000000=Mono glyph", File.ReadAllText(TemplateLibrary.ChoicesStore), StringComparison.Ordinal);

        // And under no style, which is how an update asks: the drawing does not say what it was searched under.
        Assert.Equal("Mono glyph", TemplateLibrary.Remembered(TemplateLibrary.Prepare(Glyph, family: "streamline-light")));

        // Keyed by its colours as well: an icon of the same family in another colour was never chosen for.
        Assert.Equal("Accent glyph", library.Suggest(TemplateLibrary.Prepare(Glyph.Replace("#000000", "#ff0000"), family: "streamline-light", style: "line"), group)[0].Template.Name);
    }

    [Fact]
    public void A_Remembered_Choice_Overturns_A_Lead()
    {
        var (library, group) = Library();
        var icon = TemplateLibrary.Prepare(TwoTone, family: "tint-duo");

        TemplateLibrary.Remember(icon, "Tinted two-tone");

        var first = library.Suggest(icon, group)[0];

        Assert.Equal("Tinted two-tone", first.Template.Name);
        Assert.True(first.Sure);
    }

    [Fact]
    public void Remembering_Keep_Colours_Puts_It_First()
    {
        var (library, group) = Library();
        var icon = TemplateLibrary.Prepare(Glyph);

        TemplateLibrary.Remember(icon, TemplateLibrary.KeepColoursName);

        Assert.Equal(TemplateLibrary.KeepColoursName, library.Suggest(icon, group)[0].Template.Name);
    }

    [Fact]
    public void What_The_Group_Already_Uses_Breaks_The_Accent_Mono_Tie()
    {
        var (library, group) = Library("""<drawing name="Bell"><svg xmlns="http://www.w3.org/2000/svg"><path d="M0 0h24" fill="{{ stateBlackColor }}" /></svg></drawing>""");

        var suggestions = library.Suggest(TemplateLibrary.Prepare(Glyph), group);

        Assert.Equal("Mono glyph", suggestions[0].Template.Name);
        Assert.Equal(1d, suggestions[0].Score - suggestions[1].Score);
        Assert.False(suggestions[0].Sure);
    }

    [Fact]
    public void A_Group_That_Writes_None_Of_Them_Falls_Back_On_The_Project()
    {
        var (library, group) = Library("""
            <group name="Bells"><drawing name="Bell"><svg xmlns="http://www.w3.org/2000/svg"><path d="M0 0h24" fill="{{ stateBlackColor }}" /></svg></drawing></group>
            <group name="New" />
            """);
        var target = group.Children.OfType<ProjectGroup>().Single(child => child.Name == "New");

        Assert.Equal("Mono glyph", library.Suggest(TemplateLibrary.Prepare(Glyph), target)[0].Template.Name);
    }

    [Fact]
    public void An_Icon_With_Nothing_To_Template_Keeps_Its_Colours_And_Is_Sure()
    {
        var (library, group) = Library();
        var multicolour = """<svg xmlns="http://www.w3.org/2000/svg"><path fill="#111111" /><path fill="#222222" /><path fill="#333333" /><path fill="#444444" /></svg>""";

        var only = Assert.Single(library.Suggest(TemplateLibrary.Prepare(multicolour), group));

        Assert.Equal(TemplateLibrary.KeepColoursName, only.Template.Name);
        Assert.True(only.Sure);
    }

    [Fact]
    public void CurrentColor_Is_Black_And_A_Drawing_That_Never_Says_Fill_Is_Given_One()
    {
        var icon = TemplateLibrary.Prepare("""<svg xmlns="http://www.w3.org/2000/svg"><path d="M0 0h24" stroke="currentColor" /></svg>""");

        Assert.Equal("""<svg xmlns="http://www.w3.org/2000/svg" fill="#000000"><path d="M0 0h24" stroke="#000000" /></svg>""", icon.Text);

        var black = Assert.Single(icon.Survey);
        Assert.Equal(1, black.Fills);
        Assert.Equal(1, black.Strokes);
        Assert.Equal("#000000", icon.Signature);
    }

    [Fact]
    public void CurrentColor_In_A_Style_Declaration_Is_Black_Too()
    {
        var icon = TemplateLibrary.Prepare("""<svg xmlns="http://www.w3.org/2000/svg"><path d="M0 0h24" style="fill: currentColor; stroke:currentColor" /></svg>""");

        Assert.Equal("""<svg xmlns="http://www.w3.org/2000/svg"><path d="M0 0h24" style="fill: #000000; stroke:#000000" /></svg>""", icon.Text);
        Assert.Equal("#000000", icon.Signature);
    }

    [Theory]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" fill="none"><path stroke="#000000" /></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><path style="fill: none" stroke="#000000" /></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><defs><style>.a{fill:#ff0000}</style></defs><path class="a" d="M0 0h24" /></svg>""")]
    public void A_Drawing_That_Says_Fill_Anywhere_Is_Left_As_It_Is(string svg)
        => Assert.Equal(svg, TemplateLibrary.Prepare(svg).Text);

    [Fact]
    public void A_Batch_Is_Grouped_By_Family_And_Colours()
    {
        var icons = new[]
        {
            TemplateLibrary.Prepare(Glyph, family: "a"),
            TemplateLibrary.Prepare(TwoTone, family: "a"),
            TemplateLibrary.Prepare(Glyph.Replace("M2 2", "M4 4"), family: "a"),
            TemplateLibrary.Prepare(Glyph, family: "b"),
        };

        var groups = TemplateLibrary.Batch(icons).ToList();

        Assert.Equal(new[] { 2, 1, 1 }, groups.Select(group => group.Count()));
        Assert.Equal(("a", "#4147d5 #d7e0ff"), groups[1].Key);
    }

    [Fact]
    public void A_Sized_Template_Maps_A_24_Grid_Icon_Onto_30()
    {
        var sized = TemplateLibrary.Sized(Glyph, Recipe("size=\"30\""));

        Assert.Equal(
            """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 30 30" width="30" height="30"><g transform="scale(1.25)"><path d="M2 2h20v20z" fill="#000000" /></g></svg>""",
            sized);
    }

    [Fact]
    public void Padding_Leaves_Room_Around_The_Icon_And_A_Wide_One_Is_Centred()
    {
        Assert.Contains(
            "<g transform=\"translate(3 3) scale(1)\">",
            TemplateLibrary.Sized(Glyph, Recipe("size=\"30\" padding=\"10%\"")),
            StringComparison.Ordinal);

        Assert.Contains(
            "<g transform=\"translate(0 7.5) scale(0.625)\">",
            TemplateLibrary.Sized(Glyph.Replace("0 0 24 24", "0 0 48 24"), Recipe("size=\"30\"")),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><path d="M0 0h24" /></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" width="100%" height="100%"><path d="M0 0h24" /></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" width="10mm" height="10mm"><path d="M0 0h24" /></svg>""")]
    public void An_Icon_Without_A_Frame_In_User_Units_Is_Not_Sized(string svg)
        => Assert.Same(svg, TemplateLibrary.Sized(svg, Recipe("size=\"30\"")));

    [Fact]
    public void A_Template_Without_A_Size_Keeps_The_Icon_As_Delivered()
        => Assert.Same(Glyph, TemplateLibrary.Sized(Glyph, Recipe(string.Empty)));

    // A PaintCode import's two-tone line: the outline in both an attribute and a style, a fill and a
    // gradient stop sharing one expression, and a highlight.
    private const string Extractable = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
          <defs><linearGradient id="g"><stop offset="0" stop-color="{{ stateAccentColor30 }}" /></linearGradient></defs>
          <path d="M1 1h20" style="stroke: {{ stateAccentColor }}; stroke-width: 1.5" fill="none" />
          <path d="M2 2h20" stroke="{{ stateAccentColor }}" fill="none" />
          <path d="M3 3h20" fill="{{ stateAccentColor30 }}" />
          <path d="M4 4h20" fill="{{ stateWhiteColor30 }}" />
        </svg>
        """;

    [Fact]
    public void Extract_Picks_An_Expression_By_The_One_Attribute_It_Paints_Else_By_Coverage_And_The_Last_Takes_The_Rest()
    {
        Assert.Equal(
            """
            <e:recipe xmlns:e="https://svg.skia/expr/1.0" name="Two-tone">
              <e:match colors="3" strokes="1" />
              <e:slot name="colour" by="coverage" rank="1">stateAccentColor30</e:slot>
              <e:slot name="stroke" paint="stroke" by="coverage" rank="1">stateAccentColor</e:slot>
              <e:slot name="rest" rest="true">stateWhiteColor30</e:slot>
            </e:recipe>
            """.ReplaceLineEndings("\n"),
            TemplateLibrary.Extract("Two-tone", Extractable).ReplaceLineEndings("\n"));
    }

    /// <summary>The same drawing with a literal in place of each expression binds back to the expressions it was extracted from.</summary>
    [Fact]
    public void An_Extracted_Template_Bound_To_The_Plain_Drawing_Writes_It_Again()
    {
        var plain = Extractable
            .Replace("{{ stateAccentColor30 }}", "#d7e0ff", StringComparison.Ordinal)
            .Replace("{{ stateAccentColor }}", "#4147d5", StringComparison.Ordinal)
            .Replace("{{ stateWhiteColor30 }}", "#ffffff", StringComparison.Ordinal);

        var template = SvgRecipe.Parse(TemplateLibrary.Extract("Two-tone", Extractable));
        var survey = SvgRecipeRewriter.Survey(plain);

        Assert.True(template.Matches(survey));

        var written = SvgRecipeRewriter.Apply(plain, template.Bind(survey).Recipe!).Svg;

        Assert.Equal(Painted(Extractable), Painted(written));

        static string[] Painted(string svg)
            => SvgRecipeRewriter.Survey(svg, written: true)
                .Select(value => $"{value.Text}: {string.Join(",", value.Attributes.OrderBy(one => one.Key, StringComparer.Ordinal).Select(one => $"{one.Key}={one.Value}"))}")
                .ToArray();
    }

    /// <summary>
    /// A colour kept as it is and an expression painting as much: a binding ranks the tie by which
    /// comes first, so the extract has to rank it the same way, not expressions ahead of colours.
    /// </summary>
    [Fact]
    public void A_Tie_With_A_Colour_Kept_As_It_Is_Binds_Back_To_The_Colour_It_Came_From()
    {
        const string accent = """<svg xmlns="http://www.w3.org/2000/svg"><path d="M0 0h24" fill="#ffffff" /><path d="M1 1h2" fill="{{ stateAccentColor }}" /></svg>""";

        var plain = accent.Replace("{{ stateAccentColor }}", "#4147d5", StringComparison.Ordinal);
        var template = SvgRecipe.Parse(TemplateLibrary.Extract("Accent", accent));
        var survey = SvgRecipeRewriter.Survey(plain);

        var written = SvgRecipeRewriter.Apply(plain, template.Bind(survey).Recipe!).Svg;

        Assert.Contains("""<path d="M0 0h24" fill="#ffffff" />""", written, StringComparison.Ordinal);
        Assert.Contains("""<path d="M1 1h2" fill="{{ stateAccentColor }}" />""", written, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Accent_Drawing_Extracted_And_Bound_To_A_Plain_Glyph_Gives_It_The_Same_Expression()
    {
        const string accent = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M0 0h24" fill="{{ stateAccentColor }}" /><path d="M1 1h2" style="fill: {{ stateAccentColor }}" /></svg>""";

        var (_, group) = Library();
        var template = SvgRecipe.Parse(TemplateLibrary.Extract("Accent", accent));
        var icon = TemplateLibrary.Prepare(Glyph);

        Assert.Empty(template.Declarations);
        Assert.True(template.Matches(icon.Survey));

        var bound = template.Bind(icon.Survey, declared: ProjectDeclarations.Names(group)).Recipe!;

        Assert.Contains("fill=\"{{ stateAccentColor }}\"", SvgRecipeRewriter.Apply(icon.Text, bound).Svg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Drawing_Painted_Through_No_Expression_Has_Nothing_To_Extract()
        => Assert.Throws<SvgRecipeException>(() => TemplateLibrary.Extract("Plain", Glyph));

    /// <summary>What a row does to a two-tone icon, kept: its template's slot names, each colour picked by paint and lightness.</summary>
    [Fact]
    public void Save_Writes_A_Rows_Mapping_As_Slots_Picked_By_Paint_And_Lightness()
    {
        var (library, group) = Library();
        var icon = TemplateLibrary.Prepare(TwoTone, family: "core-line");
        var mapping = library.Suggest(icon, group).Single(one => one.Template.Name == "Accent two-tone (line)").Recipe;

        var saved = TemplateLibrary.Save("Mine", icon, mapping, "core-line");

        Assert.Equal(
            """
            <e:recipe xmlns:e="https://svg.skia/expr/1.0" name="Mine">
              <e:match colors="2" strokes="1" family="core-line" />
              <e:slot name="underlay" paint="fill" by="lightness" rank="1">stateAccentColor30</e:slot>
              <e:slot name="outline" rest="true">stateAccentColor</e:slot>
            </e:recipe>
            """.ReplaceLineEndings("\n"),
            saved.ReplaceLineEndings("\n"));

        var again = SvgRecipe.Parse(saved).Bind(icon.Survey).Recipe!;

        Assert.Equal(
            mapping.Rules.Select(rule => (rule.Key, rule.Expression)).OrderBy(rule => rule.Key, StringComparer.Ordinal),
            again.Rules.Select(rule => (rule.Key, rule.Expression)).OrderBy(rule => rule.Key, StringComparer.Ordinal));
        Assert.False(SvgRecipe.Parse(saved).Matches(icon.Survey, family: "core-solid"));
    }

    /// <summary>A colour kept as it is leaves no slot to take the rest, which would take it too.</summary>
    [Fact]
    public void Save_With_A_Colour_Kept_Picks_Every_Other_One_Explicitly()
    {
        var icon = TemplateLibrary.Prepare(TwoTone);
        var mapping = SvgRecipe.Parse("""<recipe xmlns="https://svg.skia/expr/1.0" size="30"><replace color="#4147d5">stateAccentColor</replace></recipe>""");

        var saved = TemplateLibrary.Save("Outline only", icon, mapping, null);

        Assert.Contains("""<e:recipe xmlns:e="https://svg.skia/expr/1.0" name="Outline only" size="30">""", saved, StringComparison.Ordinal);
        Assert.Contains("""<e:slot name="stroke" paint="stroke" by="lightness" rank="1">stateAccentColor</e:slot>""", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("rest=", saved, StringComparison.Ordinal);
        Assert.Equal("#d7e0ff", Assert.Single(SvgRecipe.Parse(saved).Bind(icon.Survey).Leftover).Text);
    }

    private static SvgRecipe Recipe(string attributes)
        => SvgRecipe.Parse($"<recipe xmlns=\"https://svg.skia/expr/1.0\" {attributes} />");

    private static (TemplateLibrary Library, ProjectGroup Group) Library(string drawings = "")
    {
        var document = ProjectDocument.Parse($"""
            <?xml version="1.0" encoding="utf-8"?>
            <studio namespace="Demo.Icons">
            {Templates}
              <group name="Icons">
                <e:code xmlns:e="https://svg.skia/expr/1.0">
                  <e:param name="accentColorOn" type="color" default="#fb3e72" />
                  <e:let name="stateAccentColor">accentColorOn</e:let>
                  <e:let name="stateAccentColor30">withAlpha(accentColorOn, 0.3)</e:let>
                  <e:let name="stateBlackColor">#1f2123</e:let>
                  <e:let name="stateWhiteColor30">withAlpha(#ffffff, 0.3)</e:let>
                </e:code>
                {drawings}
              </group>
            </studio>

            """);

        return (new TemplateLibrary(document.Root), (ProjectGroup)document.Root.Children.Single());
    }
}
