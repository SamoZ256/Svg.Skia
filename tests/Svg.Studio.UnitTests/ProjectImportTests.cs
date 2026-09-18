using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Svg.CodeGen.Skia.Projects;
using Svg.PaintCode;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// Converting into the format: an svgc project, and a PaintCode document.
/// </summary>
/// <remarks>
/// Both are one way, and the svgc one is deliberately lossy: a recipe is baked into the drawings it
/// painted, and a file named twice becomes two drawings. The tests say so rather than guarding it.
/// </remarks>
public class ProjectImportTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);

        return path;
    }

    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect width="24" height="24" fill="#3b82f6" />
        </svg>
        """;

    private const string Recipe = """
        <recipe xmlns="https://svg.skia/expr/1.0">
          <code>
            <param name="hue" type="number" default="217" min="0" max="360" />
          </code>
          <replace color="#3b82f6">hsl(hue, 74%, 55%)</replace>
        </recipe>
        """;

    [Fact]
    public void An_Svgc_Project_Becomes_One_That_Holds_Its_Drawings()
    {
        Write("badge.svg", Drawing);

        var source = SvgcProjectDocument.Load(Write("icons.svgcproj", """
            <svgc>
              <namespace>Demo.Icons</namespace>
              <singleFile>Icons.cs</singleFile>

              <svg input="badge.svg" class="Badge" />

              <group namespace="Demo.Icons.Large" scale="2">
                <svg input="badge.svg" class="BadgeLarge" output="Large/BadgeLarge.cs" />
              </group>
            </svgc>
            """));

        var notes = new List<string>();
        var target = Path.Combine(_directory, "icons.svgstudio");
        var project = ProjectImport.FromSvgc(source, notes, target);

        // Named before it is written: the conversion is held until somebody saves it.
        Assert.Equal(target, project.Path);
        Assert.False(File.Exists(target));

        Assert.Empty(notes);
        Assert.Equal("Demo.Icons", project.Root.Namespace);
        Assert.Equal("Icons.cs", project.Root.SingleFile);

        var drawings = project.Root.Drawings.ToList();

        Assert.Equal(2, drawings.Count);

        // Named after the file they were read from, and holding it rather than pointing at it.
        Assert.Equal("badge", drawings[0].Name);
        Assert.Contains("<rect width=\"24\"", drawings[0].Text, StringComparison.Ordinal);
        Assert.Equal("Badge", drawings[0].Class);

        var group = Assert.IsType<ProjectGroup>(project.Root.Children[1]);

        Assert.Equal("Large", group.Name);
        Assert.Equal(2f, group.Scale);
        Assert.Equal("Large/BadgeLarge.cs", drawings[1].Output);

        // One file built twice is two drawings now, each holding its own copy of the art: editing
        // one is no longer editing the other.
        Assert.Contains("<rect width=\"24\" height=\"24\" fill=\"#3b82f6\" />", drawings[1].Text, StringComparison.Ordinal);
        Assert.NotSame(drawings[0].Element, drawings[1].Element);
    }

    [Fact]
    public void A_Recipe_Is_Baked_Into_The_Drawings_It_Painted()
    {
        Write("badge.svg", Drawing);
        Write("badge.recipe", Recipe);

        var source = SvgcProjectDocument.Load(Write("icons.svgcproj", """
            <svgc>
              <recipe>badge.recipe</recipe>
              <svg input="badge.svg" class="Badge" />
            </svgc>
            """));

        var notes = new List<string>();
        var text = ProjectImport.FromSvgc(source, notes, Path.Combine(_directory, "icons.svgstudio"))
            .Root.Drawings.Single().Text;

        Assert.Empty(notes);

        // The parameters the recipe declared are what drove the drawing, so they come with it —
        // which is also what makes a family of drawings share a slider again.
        Assert.Contains("<e:param name=\"hue\"", text, StringComparison.Ordinal);
        Assert.Contains("fill=\"{{ hsl(hue, 74%, 55%) }}\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Drawing_That_Cannot_Be_Read_Is_Left_Out_And_Said()
    {
        var source = SvgcProjectDocument.Load(Write("icons.svgcproj", """
            <svgc>
              <svg input="missing.svg" class="Missing" />
            </svgc>
            """));

        var notes = new List<string>();
        var project = ProjectImport.FromSvgc(source, notes, Path.Combine(_directory, "icons.svgstudio"));

        Assert.Empty(project.Root.Drawings);
        Assert.Contains("missing.svg could not be read", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void A_PaintCode_Document_Becomes_A_Group_Of_Drawings()
    {
        var path = Path.Combine(_directory, "sample.pcvd");

        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestAssets", "sample.pcvd"), path, overwrite: true);

        var document = PaintCodeDocument.Load(path);
        var notes = new List<PaintCodeImportNote>();

        var project = ProjectImport.FromPaintCode(
            document,
            new PaintCodeImportOptions(_directory),
            notes,
            Path.Combine(_directory, "symbols.svgstudio"));

        var group = Assert.IsType<ProjectGroup>(Assert.Single(project.Root.Children));

        // The document's own name, which is what the folder import puts there too.
        Assert.Equal("Symbols", project.Root.Namespace);
        Assert.Equal("Desk", group.Name);
        Assert.Equal("Symbols.Desk", group.Namespace);

        var badge = project.Root.Drawings.First();

        Assert.Equal("badge", badge.Name);
        Assert.Equal("Badge", badge.Class);
        Assert.Contains("<e:param name=\"colorPurple\" type=\"color\"", badge.Text, StringComparison.Ordinal);
    }
}
