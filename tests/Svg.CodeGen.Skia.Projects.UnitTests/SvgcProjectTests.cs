using System.IO;
using System.Linq;
using Svg.CodeGen.Skia;
using Xunit;

namespace Svg.CodeGen.Skia.Projects.UnitTests;

public class SvgcProjectTests
{
    private const string Base = "/icons";

    private static SvgcProject Parse(string xml) => SvgcProject.Parse(xml, Base);

    [Fact]
    public void Settings_And_Items_Are_Read()
    {
        var project = Parse("""
            <svgc>
              <recipe>icons.recipe</recipe>
              <namespace>HouseIcons</namespace>
              <class>Fallback</class>
              <emit>csharp</emit>
              <cache>lastValueLocked</cache>
              <helperScope>internal</helperScope>
              <skiaSharp>3</skiaSharp>
              <singleFile>Generated/Icons.cs</singleFile>

              <svg input="home.svg" class="Home" />
              <svg input="search.svg" class="Search" namespace="Glyphs" recipe="search.recipe" output="Search.cs" />
            </svgc>
            """);

        Assert.Equal(Path.Combine(Base, "icons.recipe"), project.Recipe);
        Assert.Equal("HouseIcons", project.Namespace);
        Assert.Equal("Fallback", project.Class);
        Assert.Equal(SvgEmit.CSharp, project.Emit);
        Assert.Equal(SvgPictureCache.LastValueLocked, project.Cache);
        Assert.Equal(SvgHelperScope.Internal, project.HelperScope);
        Assert.Equal(SkiaSharpTarget.V3, project.SkiaSharp);
        Assert.Equal(Path.Combine(Base, "Generated/Icons.cs"), project.SingleFile);

        Assert.Collection(
            project.Items,
            first =>
            {
                Assert.Equal(Path.Combine(Base, "home.svg"), first.Input);
                Assert.Equal("Home", first.Class);
                Assert.Null(first.Namespace);
                Assert.Null(first.Recipe);
                Assert.Null(first.Output);
            },
            second =>
            {
                Assert.Equal(Path.Combine(Base, "search.svg"), second.Input);
                Assert.Equal("Glyphs", second.Namespace);
                Assert.Equal(Path.Combine(Base, "search.recipe"), second.Recipe);
                Assert.Equal(Path.Combine(Base, "Search.cs"), second.Output);
            });
    }

    [Fact]
    public void A_Size_Is_Read_From_The_Project_And_From_An_Item()
    {
        var project = Parse("""
            <svgc>
              <scale>2</scale>

              <svg input="home.svg" />
              <svg input="search.svg" width="48" />
              <svg input="menu.svg" height="1.5" />
            </svgc>
            """);

        Assert.Equal(2f, project.Scale);
        Assert.Null(project.Width);
        Assert.Null(project.Height);
        Assert.True(project.HasSize);

        Assert.False(project.Items[0].HasSize);

        Assert.Equal(48f, project.Items[1].Width);
        Assert.True(project.Items[1].HasSize);

        // Read invariantly, so a project describes the same build on every machine.
        Assert.Equal(1.5f, project.Items[2].Height);
    }

    [Fact]
    public void A_Group_Settles_What_Its_Drawings_Do_Not_Say()
    {
        var project = Parse("""
            <svgc>
              <namespace>Icons</namespace>

              <svg input="logo.svg" class="Logo" />

              <group namespace="Icons.Nav" recipe="nav.recipe" scale="2">
                <svg input="home.svg" class="Home" />
                <svg input="search.svg" class="Search" recipe="search.recipe" />
              </group>
            </svgc>
            """);

        // Outside every group, so it inherits nothing and falls through to the project settings.
        Assert.Null(project.Items[0].Namespace);
        Assert.Null(project.Items[0].Recipe);
        Assert.False(project.Items[0].HasSize);

        Assert.Equal("Icons.Nav", project.Items[1].Namespace);
        Assert.Equal(Path.Combine(Base, "nav.recipe"), project.Items[1].Recipe);
        Assert.Equal(2f, project.Items[1].Scale);

        // A drawing overrides its group exactly as it overrides the project.
        Assert.Equal(Path.Combine(Base, "search.recipe"), project.Items[2].Recipe);
        Assert.Equal("Icons.Nav", project.Items[2].Namespace);
    }

    [Fact]
    public void Groups_Nest_And_The_Nearest_Wins()
    {
        var project = Parse("""
            <svgc>
              <group namespace="Icons" class="Fallback">
                <svg input="logo.svg" />

                <group namespace="Icons.Nav">
                  <svg input="home.svg" class="Home" />
                </group>
              </group>
            </svgc>
            """);

        Assert.Equal("Icons", project.Items[0].Namespace);
        Assert.Equal("Fallback", project.Items[0].Class);

        Assert.Equal("Icons.Nav", project.Items[1].Namespace);
        Assert.Equal("Home", project.Items[1].Class);
    }

    [Fact]
    public void Drawings_Keep_Their_Declared_Order_Across_Groups()
    {
        // singleFile emits them in this order, so a group must not reshuffle the build.
        var project = Parse("""
            <svgc>
              <svg input="a.svg" />
              <group>
                <svg input="b.svg" />
                <group><svg input="c.svg" /></group>
                <svg input="d.svg" />
              </group>
              <svg input="e.svg" />
            </svgc>
            """);

        Assert.Equal(
            new[] { "a.svg", "b.svg", "c.svg", "d.svg", "e.svg" },
            project.Items.Select(item => Path.GetFileName(item.Input)));
    }

    [Fact]
    public void A_Size_Named_At_Any_Level_Replaces_The_Whole_Group_Of_Three()
    {
        var project = Parse("""
            <svgc>
              <scale>2</scale>

              <group width="48">
                <svg input="home.svg" />
                <svg input="search.svg" scale="3" />
              </group>
            </svgc>
            """);

        // The group's width replaces the project's scale rather than joining it — and the project
        // scale is not folded in here at all, since it is what the item falls through to.
        Assert.Equal(48f, project.Items[0].Width);
        Assert.Null(project.Items[0].Scale);

        // And the drawing's own scale replaces the group's width in turn.
        Assert.Equal(3f, project.Items[1].Scale);
        Assert.Null(project.Items[1].Width);
    }

    [Fact]
    public void An_Empty_Group_Is_Allowed()
    {
        var project = Parse("""<svgc><group namespace="Icons" /></svgc>""");

        Assert.Empty(project.Items);
    }

    [Fact]
    public void A_Setting_Element_Inside_A_Group_Is_Rejected()
    {
        // It would read as scoping to the group and would in fact be ignored.
        var error = Assert.Throws<SvgcProjectException>(() => Parse("""
            <svgc>
              <group>
                <namespace>Icons.Nav</namespace>
                <svg input="home.svg" />
              </group>
            </svgc>
            """));

        Assert.Contains("<namespace> is not allowed in a <group>", error.Message);
        Assert.Contains("settings are attributes on it", error.Message);
    }

    [Fact]
    public void Anything_Unset_Stays_Null()
    {
        // A setting the document did not mention has to stay distinguishable from one it set, or
        // a command line flag could not override the file.
        var project = Parse("""<svgc><svg input="home.svg" /></svgc>""");

        Assert.Null(project.Recipe);
        Assert.Null(project.Namespace);
        Assert.Null(project.Class);
        Assert.Null(project.Emit);
        Assert.Null(project.Cache);
        Assert.Null(project.HelperScope);
        Assert.Null(project.SkiaSharp);
        Assert.Null(project.SingleFile);
        Assert.Null(project.Width);
        Assert.Null(project.Height);
        Assert.Null(project.Scale);
        Assert.False(project.HasSize);
        Assert.False(project.Items[0].HasSize);
    }

    [Fact]
    public void Absolute_Paths_Are_Left_Alone()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere.svg");
        var project = Parse($"""<svgc><svg input="{absolute}" /></svgc>""");

        Assert.Equal(absolute, project.Items[0].Input);
    }

    [Fact]
    public void An_Unknown_Element_Is_Rejected()
    {
        // The json batch this replaces bound nothing on a mistyped key and still exited zero.
        var error = Assert.Throws<SvgcProjectException>(() => Parse("""
            <svgc>
              <namesapce>Icons</namesapce>
              <svg input="home.svg" />
            </svgc>
            """));

        Assert.Contains("<namesapce> is not a project setting", error.Message);
    }

    [Fact]
    public void An_Unknown_Item_Attribute_Is_Rejected()
    {
        var error = Assert.Throws<SvgcProjectException>(
            () => Parse("""<svgc><svg input="home.svg" clas="Home" /></svgc>"""));

        Assert.Contains("'clas' is not a <svg> attribute", error.Message);
    }

    [Fact]
    public void A_Setting_Cannot_Repeat()
    {
        var error = Assert.Throws<SvgcProjectException>(() => Parse("""
            <svgc>
              <namespace>One</namespace>
              <namespace>Two</namespace>
            </svgc>
            """));

        Assert.Contains("<namespace> is set more than once", error.Message);
    }

    [Theory]
    [InlineData("""<svgc><svg /></svgc>""", "missing an input")]
    [InlineData("""<project><svg input="a.svg" /></project>""", "must be <svgc>")]
    [InlineData("""<svgc><emit>xml</emit></svgc>""", "not an output format")]
    [InlineData("""<svgc><cache>always</cache></svgc>""", "not a cache mode")]
    [InlineData("""<svgc><helperScope>global</helperScope></svgc>""", "not a helper scope")]
    [InlineData("""<svgc><skiaSharp>2</skiaSharp></svgc>""", "not a SkiaSharp version")]
    [InlineData("""<svgc><width>wide</width></svgc>""", "not a width")]
    [InlineData("""<svgc><height>tall</height></svgc>""", "not a height")]
    [InlineData("""<svgc><scale>big</scale></svgc>""", "not a scale")]
    // A decimal comma is a number somewhere, but not in a project file.
    [InlineData("""<svgc><scale>1,5</scale></svgc>""", "not a scale")]
    [InlineData("""<svgc><svg input="a.svg" width="wide" /></svgc>""", "not a width")]
    [InlineData("""<svgc><group output="Out.cs"><svg input="a.svg" /></group></svgc>""", "not a <group> attribute")]
    [InlineData("""<svgc><group input="art"><svg input="a.svg" /></group></svgc>""", "not a <group> attribute")]
    [InlineData("""<svgc><group scale="big"><svg input="a.svg" /></group></svgc>""", "not a scale")]
    [InlineData("""<svgc><group><svgs input="a.svg" /></group></svgc>""", "not allowed in a <group>")]
    [InlineData("""<svgc><svg input="a.svg" >""", "not well formed")]
    public void Rejects(string xml, string expected)
    {
        var error = Assert.Throws<SvgcProjectException>(() => Parse(xml));

        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData("csharp", SvgEmit.CSharp)]
    [InlineData("CSharp", SvgEmit.CSharp)]
    [InlineData("svg", SvgEmit.Svg)]
    [InlineData("", SvgEmit.CSharp)]
    [InlineData(null, SvgEmit.CSharp)]
    public void Emit_Values(string? value, SvgEmit expected) => Assert.Equal(expected, SvgcProject.ParseEmit(value));

    [Theory]
    [InlineData("none", SvgPictureCache.None)]
    [InlineData("lastValue", SvgPictureCache.LastValue)]
    [InlineData("lastValueLocked", SvgPictureCache.LastValueLocked)]
    public void Cache_Values(string value, SvgPictureCache expected) => Assert.Equal(expected, SvgcProject.ParseCache(value));

    [Theory]
    [InlineData("3", SkiaSharpTarget.V3)]
    [InlineData("4", SkiaSharpTarget.V4)]
    [InlineData("", SkiaSharpTarget.V4)]
    [InlineData(null, SkiaSharpTarget.V4)]
    public void SkiaSharp_Values(string? value, SkiaSharpTarget expected) => Assert.Equal(expected, SvgcProject.ParseSkiaSharpTarget(value));

    [Theory]
    [InlineData("48", 48f)]
    [InlineData(" 48 ", 48f)]
    [InlineData("1.5", 1.5f)]
    // Whether a number makes sense as a size is not decided here: SvgSizeRequest owns that, and
    // it is the only place that sees width, height and scale together.
    [InlineData("-5", -5f)]
    [InlineData(null, null)]
    public void Length_Values(string? value, float? expected)
        => Assert.Equal(expected, SvgcProject.ParseLength(value, "width"));

    [Theory]
    [InlineData("2", 2f)]
    [InlineData("0.5", 0.5f)]
    [InlineData(null, null)]
    public void Scale_Values(string? value, float? expected) => Assert.Equal(expected, SvgcProject.ParseScale(value));

    [Theory]
    [InlineData("file", SvgHelperScope.FileLocal)]
    [InlineData("internal", SvgHelperScope.Internal)]
    [InlineData("perClass", SvgHelperScope.PerClass)]
    public void HelperScope_Values(string value, SvgHelperScope expected) => Assert.Equal(expected, SvgcProject.ParseHelperScope(value));

    [Fact]
    public void Load_Resolves_Against_The_Files_Own_Directory()
    {
        // Item paths used to resolve against the working directory, so a batch file could only be
        // run from one place. A project owns its directory.
        var directory = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(directory, "icons.svgcproj");
        File.WriteAllText(path, """<svgc><recipe>icons.recipe</recipe><svg input="art/home.svg" /></svgc>""");

        var project = SvgcProject.Load(path);

        Assert.Equal(Path.Combine(directory, "icons.recipe"), project.Recipe);
        Assert.Equal(Path.Combine(directory, "art/home.svg"), project.Items[0].Input);

        Directory.Delete(directory, recursive: true);
    }
}
