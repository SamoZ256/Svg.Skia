using System.IO;
using System.Linq;
using Xunit;

namespace Svg.CodeGen.Skia.Projects.UnitTests;

/// <summary>
/// The project as a tree rather than a build: what <see cref="SvgcProject"/> throws away so that a
/// generator does not have to know groups exist, and an editor has nothing else to read it from.
/// </summary>
public class SvgcProjectDocumentTests
{
    private const string Base = "/icons";

    private static SvgcProjectDocument Parse(string xml) => SvgcProjectDocument.Parse(xml, Base);

    [Fact]
    public void The_Tree_Keeps_The_Nesting_That_Flattening_Folds_Away()
    {
        var document = Parse("""
            <svgc>
              <svg input="a.svg" />
              <group namespace="Nav">
                <svg input="b.svg" />
                <group><svg input="c.svg" /></group>
              </group>
            </svgc>
            """);

        var children = document.Root.Children;

        Assert.Equal(2, children.Count);
        Assert.Equal("a.svg", Assert.IsType<SvgcProjectDrawing>(children[0]).Input);

        var nav = Assert.IsType<SvgcProjectGroup>(children[1]);
        Assert.Equal("Nav", nav.Namespace);
        Assert.Equal(2, nav.Children.Count);

        var inner = Assert.IsType<SvgcProjectGroup>(nav.Children[1]);
        Assert.Equal("c.svg", Assert.IsType<SvgcProjectDrawing>(Assert.Single(inner.Children)).Input);

        // The same drawings the build gets, in the same order.
        Assert.Equal(
            new[] { "a.svg", "b.svg", "c.svg" },
            document.Root.Drawings.Select(drawing => drawing.Input));
    }

    [Fact]
    public void An_Effective_Setting_Falls_Through_To_The_Project()
    {
        var document = Parse("""
            <svgc>
              <namespace>Icons</namespace>
              <class>Fallback</class>
              <svg input="a.svg" />
              <group namespace="Icons.Nav">
                <svg input="b.svg" class="B" />
              </group>
            </svgc>
            """);

        var drawings = document.Root.Drawings.ToArray();

        // Unlike the flattened item, which leaves this null for the project to answer later.
        Assert.Equal("Icons", drawings[0].EffectiveNamespace);
        Assert.Null(drawings[0].Namespace);

        Assert.Equal("Icons.Nav", drawings[1].EffectiveNamespace);
        Assert.Equal("B", drawings[1].EffectiveClass);
        Assert.Equal("Fallback", drawings[0].EffectiveClass);
    }

    [Fact]
    public void An_Effective_Size_Obeys_The_Trio_And_Padding_Split()
    {
        var document = Parse("""
            <svgc>
              <scale>2</scale>
              <padding>25%</padding>
              <group width="48">
                <svg input="a.svg" />
                <svg input="b.svg" padding="0" />
              </group>
              <svg input="c.svg" />
            </svgc>
            """);

        var drawings = document.Root.Drawings.ToArray();

        // The group's width replaces the project's scale outright rather than joining it.
        Assert.Equal(48f, drawings[0].EffectiveWidth);
        Assert.Null(drawings[0].EffectiveScale);

        // Padding is not one of the three, so it still reaches through the group that resized.
        Assert.Equal("25%", drawings[0].EffectivePadding);
        Assert.Equal("0", drawings[1].EffectivePadding);

        // Outside the group, so the project's own sizing answers.
        Assert.Equal(2f, drawings[2].EffectiveScale);
        Assert.Null(drawings[2].EffectiveWidth);
    }

    [Fact]
    public void Where_A_Setting_Comes_From_Can_Be_Named()
    {
        var document = Parse("""
            <svgc>
              <namespace>Icons</namespace>
              <group namespace="Icons.Nav" scale="2">
                <svg input="a.svg" />
              </group>
            </svgc>
            """);

        var drawing = document.Root.Drawings.Single();
        var group = document.Root.Children.OfType<SvgcProjectGroup>().Single();

        Assert.Same(group, drawing.OwnerOf("namespace"));
        Assert.Same(group, drawing.OwnerOf("scale"));
        Assert.Null(drawing.OwnerOf("class"));
    }

    [Fact]
    public void Paths_Are_Kept_As_Written_And_Resolved_Separately()
    {
        var document = Parse("""
            <svgc>
              <svg input="art/a.svg" output="A.cs" recipe="a.recipe" />
            </svgc>
            """);

        var drawing = document.Root.Drawings.Single();

        // Kept raw, or saving would rewrite every relative path in the file as an absolute one.
        Assert.Equal("art/a.svg", drawing.Input);
        Assert.Equal("A.cs", drawing.Output);
        Assert.Equal("a.recipe", drawing.Recipe);

        Assert.Equal(Path.Combine(Base, "art/a.svg"), drawing.ResolvedInput);
        Assert.Equal(Path.Combine(Base, "A.cs"), drawing.ResolvedOutput);
        Assert.Equal(Path.Combine(Base, "a.recipe"), drawing.ResolvedRecipe);
    }

    [Fact]
    public void Saving_An_Untouched_Project_Gives_Back_What_Was_Read()
    {
        // Comments, attribute order, blank lines and indentation are none of an edit's business.
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <!-- the icons of the thing -->
            <svgc>
              <namespace>Demo.Icons</namespace>
              <singleFile>Icons.cs</singleFile>

              <svg class="Home" input="home.svg" />

              <!-- twice the size, for the header -->
              <group namespace="Demo.Icons.Large" scale="2">
                <svg input="badge.svg" class="BadgeLarge" />
              </group>
            </svgc>
            """;

        Assert.Equal(xml, Parse(xml).ToXml());
    }

    [Fact]
    public void An_Edit_Rewrites_Only_What_It_Changed()
    {
        var document = Parse("""
            <svgc>
              <namespace>Demo.Icons</namespace>

              <!-- kept -->
              <group namespace="Nav" scale="2">
                <svg input="home.svg" class="Home" />
              </group>
            </svgc>
            """);

        document.Root.Children.OfType<SvgcProjectGroup>().Single().Scale = 3f;

        Assert.Equal("""
            <svgc>
              <namespace>Demo.Icons</namespace>

              <!-- kept -->
              <group namespace="Nav" scale="3">
                <svg input="home.svg" class="Home" />
              </group>
            </svgc>
            """, document.ToXml());
    }

    [Fact]
    public void Clearing_A_Setting_Removes_It_So_The_Value_Is_Inherited_Again()
    {
        var document = Parse("""
            <svgc>
              <scale>2</scale>
              <group scale="4"><svg input="a.svg" /></group>
            </svgc>
            """);

        var group = document.Root.Children.OfType<SvgcProjectGroup>().Single();

        group.Scale = null;

        Assert.Equal("""
            <svgc>
              <scale>2</scale>
              <group><svg input="a.svg" /></group>
            </svgc>
            """, document.ToXml());

        // And the drawing goes back to the project's sizing.
        Assert.Equal(2f, document.Root.Drawings.Single().EffectiveScale);
    }

    [Fact]
    public void A_Project_Setting_Added_Lands_Before_The_Drawings()
    {
        var document = Parse("""
            <svgc>
              <namespace>Icons</namespace>
              <svg input="a.svg" />
            </svgc>
            """);

        document.Root.SingleFile = "Icons.cs";

        // After the settings and before the build they describe, which is where the docs put it.
        Assert.Contains("<namespace>Icons</namespace>\n  <singleFile>Icons.cs</singleFile>", document.ToXml());
    }

    [Fact]
    public void Crlf_Survives_A_Save()
    {
        // XmlReader normalises CRLF to LF as the spec requires, so a Windows file read and written
        // back would otherwise come out with every line changed.
        var document = Parse("<svgc>\r\n  <svg input=\"a.svg\" />\r\n</svgc>");

        Assert.Equal("\r\n", document.NewLine);
        Assert.Equal("<svgc>\r\n  <svg input=\"a.svg\" />\r\n</svgc>", document.ToXml());
    }

    [Fact]
    public void Load_And_Save_Round_Trip_A_File()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(directory, "icons.svgcproj");
        const string xml = """
            <svgc>
              <namespace>Icons</namespace>
              <group scale="2">
                <svg input="art/home.svg" class="Home" />
              </group>
            </svgc>
            """;

        File.WriteAllText(path, xml);

        var document = SvgcProjectDocument.Load(path);

        Assert.Equal(directory, document.BaseDirectory);
        Assert.Equal(Path.Combine(directory, "art/home.svg"), document.Root.Drawings.Single().ResolvedInput);

        document.Root.Namespace = "Icons.Renamed";
        document.Save();

        var reloaded = SvgcProjectDocument.Load(path);

        Assert.Equal("Icons.Renamed", reloaded.Root.Namespace);
        Assert.Equal(2f, reloaded.Flatten().Items.Single().Scale);

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void Flattening_Is_What_The_Generator_Already_Reads()
    {
        const string xml = """
            <svgc>
              <namespace>Icons</namespace>
              <group namespace="Nav" scale="2">
                <svg input="home.svg" class="Home" />
              </group>
            </svgc>
            """;

        var flattened = Parse(xml).Flatten();
        var parsed = SvgcProject.Parse(xml, Base);

        Assert.Equal(parsed.Namespace, flattened.Namespace);
        Assert.Equal(parsed.Items.Single().Namespace, flattened.Items.Single().Namespace);
        Assert.Equal(parsed.Items.Single().Scale, flattened.Items.Single().Scale);
        Assert.Equal(parsed.Items.Single().Input, flattened.Items.Single().Input);
    }
}
