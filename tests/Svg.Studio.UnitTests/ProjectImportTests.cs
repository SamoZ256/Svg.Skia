using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Svg.CodeGen.Skia.Projects;
using Svg.PaintCode;
using Svg.PaintCode.UnitTests;
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
        var project = ProjectImport.FromSvgc(source, notes);

        // Held rather than written, and not named either: where it goes is asked when it is saved.
        Assert.Null(project.Path);
        Assert.Equal(_directory, project.BaseDirectory);

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
        var text = ProjectImport.FromSvgc(source, notes).Root.Drawings.Single().Text;

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
        var project = ProjectImport.FromSvgc(source, notes);

        Assert.Empty(project.Root.Drawings);
        Assert.Contains("missing.svg could not be read", Assert.Single(notes), StringComparison.Ordinal);
    }

    /// <summary>
    /// A canvas keeps where it sat, measured from its desk's own corner rather than the desk's
    /// numbers: a place is read against the board holding it, and normalising is what the board
    /// itself would write the moment anybody dragged anything.
    /// </summary>
    /// <remarks>
    /// Turned over on the way across, a desk's y pointing up and a board's down. "three" is the one
    /// at the larger y of the L, so it is the row along the top of the board; subtracting on both
    /// axes put it along the bottom and mirrored every desk in the project.
    /// </remarks>
    [Fact]
    public void A_PaintCode_Canvas_Keeps_Where_It_Sat_On_Its_Desk()
    {
        var project = Placed();

        Assert.Equal((0f, 0f), At(project, "Overlays", "three"));
        Assert.Equal((0f, 50f), At(project, "Overlays", "one"));
        Assert.Equal((40f, 50f), At(project, "Overlays", "two"));

        // A desk whose canvases sit below its own origin comes out the same way round.
        Assert.Equal((0f, 0f), At(project, "Controls", "five"));
        Assert.Equal((0f, 50f), At(project, "Controls", "four"));
    }

    [Fact]
    public void A_Desk_Starts_Where_Its_Own_Canvases_Do()
    {
        var project = Placed();

        foreach (var group in project.Root.Children.OfType<ProjectGroup>())
        {
            var placed = group.Children.Where(child => child.HasPosition).ToList();

            Assert.Equal(0f, placed.Min(child => child.X!.Value));
            Assert.Equal(0f, placed.Min(child => child.Y!.Value));
        }

        // Nothing negative reaches the file, though a desk is free to use negatives.
        Assert.DoesNotContain("=\"-", project.ToXml(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_Canvas_With_No_Bounds_Is_Left_Unplaced()
    {
        var drawing = Placed().Root.Drawings.Single(one => one.Name == "nowhere");

        Assert.False(drawing.HasPosition);
        Assert.Null(drawing.Element.Attribute("x"));
        Assert.Null(drawing.Element.Attribute("y"));
    }

    /// <summary>
    /// Both or neither, which the format asks for and the loader enforces — so saving what the
    /// import wrote and reading it back is the assertion.
    /// </summary>
    [Fact]
    public void A_Place_Is_Written_As_Both_Or_Neither()
    {
        var path = Path.Combine(_directory, "placed.svgstudio");

        Placed().Save(path);

        var reloaded = ProjectDocument.Load(path);

        // Five of the six: the one the document never placed is the sixth.
        Assert.Equal(5, reloaded.Root.Drawings.Count(drawing => drawing.HasPosition));
        Assert.Single(reloaded.Root.Drawings, drawing => !drawing.HasPosition);
    }

    /// <summary>
    /// The desks go down the board in the document's own order, far enough apart to read as two.
    /// </summary>
    /// <remarks>
    /// Which is what makes the arrangement visible at all: a group nobody placed is not a board, so
    /// its drawings would be poured into the grid beside everything else with their own numbers
    /// ignored — and the first drag would then write those grid places over the imported ones.
    /// </remarks>
    [Fact]
    public void Desks_Are_Stacked_Down_The_Project()
    {
        var groups = Placed().Root.Children.OfType<ProjectGroup>().ToList();

        Assert.Equal(new[] { "Overlays", "Controls" }, groups.Select(group => group.Name).ToArray());

        // The largest canvas is 60, so a caption is 3 and the gap between desks is 18. Overlays is
        // 80 tall, which puts Controls at 98.
        Assert.Equal((0f, 0f), (groups[0].X!.Value, groups[0].Y!.Value));
        Assert.Equal((0f, 98f), (groups[1].X!.Value, groups[1].Y!.Value));
    }

    /// <summary>The two desks of <see cref="DeskDocument"/>, imported.</summary>
    private ProjectDocument Placed()
        => ProjectImport.FromPaintCode(
            PaintCodeDocument.Parse(DeskDocument.Bytes()),
            new PaintCodeImportOptions(_directory),
            new List<PaintCodeImportNote>(),
            _directory);

    private static (float X, float Y) At(ProjectDocument project, string desk, string drawing)
    {
        var row = project.Root.Children.OfType<ProjectGroup>().Single(group => group.Name == desk)
            .Drawings.Single(one => one.Name == drawing);

        return (row.X!.Value, row.Y!.Value);
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
            _directory);

        var group = Assert.IsType<ProjectGroup>(Assert.Single(project.Root.Children));

        // The document's own name, which is what the folder import puts there too.
        Assert.Equal("Symbols", project.Root.Namespace);
        Assert.Equal("Desk", group.Name);
        Assert.Equal("Symbols.Desk", group.Namespace);

        var badge = project.Root.Drawings.First();

        Assert.Equal("badge", badge.Name);
        Assert.Equal("Badge", badge.Class);
        // On the desk rather than in the drawing: both canvases use it, so it belongs to the group
        // they share, and the drawing is left with the expression that reads it.
        Assert.Contains("<e:param name=\"colorPurple\" type=\"color\"", group.CodeText, StringComparison.Ordinal);
        Assert.DoesNotContain("e:param", badge.Text, StringComparison.Ordinal);

        // This asset's canvases all sit on one spot, so every place comes out at the corner — and a
        // desk at the board's own origin is still placed, which is what keeps it a board.
        Assert.True(group.HasPosition);
        Assert.Equal((0f, 0f), (badge.X!.Value, badge.Y!.Value));
    }
}
