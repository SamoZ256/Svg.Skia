using System.IO;
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
        Assert.Equal(SkiaSharpVersion.V3, project.SkiaSharp);
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
    [InlineData("3", SkiaSharpVersion.V3)]
    [InlineData("4", SkiaSharpVersion.V4)]
    [InlineData("", SkiaSharpVersion.V4)]
    [InlineData(null, SkiaSharpVersion.V4)]
    public void SkiaSharp_Values(string? value, SkiaSharpVersion expected) => Assert.Equal(expected, SvgcProject.ParseSkiaSharp(value));

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
