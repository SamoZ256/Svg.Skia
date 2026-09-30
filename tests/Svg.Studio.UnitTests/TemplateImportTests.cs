using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Svg.Expressions.Recipes;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>A batch of icons going into a group through their templates, and one coming back in from its source.</summary>
public class TemplateImportTests
{
    private const string Project = """
        <?xml version="1.0" encoding="utf-8"?>
        <studio namespace="Demo.Icons">
          <group name="Scheme">
            <e:code xmlns:e="https://svg.skia/expr/1.0">
              <e:param name="state" type="integer" default="0" />
              <e:param name="isLight" type="boolean" default="false" />
              <e:let name="stateAccentColor">state == 1 ? #fb3e72 : #7a7a7a</e:let>
            </e:code>
            <drawing name="Bell">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M0 0h24" fill="{{ stateAccentColor }}" /></svg>
            </drawing>
          </group>
          <group name="Empty">
            <drawing name="Dot">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><circle r="4" /></svg>
            </drawing>
          </group>
          <group name="Outer">
            <group name="Inner">
              <e:code xmlns:e="https://svg.skia/expr/1.0">
                <e:param name="tint" type="number" default="1" />
              </e:code>
              <drawing name="Sub">
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><circle r="4" opacity="{{ tint }}" /></svg>
              </drawing>
            </group>
          </group>
        </studio>

        """;

    // Brings the scheme's name with it, for a group that has not declared it.
    private const string Accent = """
        <recipe xmlns="https://svg.skia/expr/1.0" name="Accent glyph">
          <match colors="1" />
          <code><let name="stateAccentColor">#fb3e72</let></code>
          <slot name="glyph" rest="true">stateAccentColor</slot>
        </recipe>
        """;

    private const string Tinted = """
        <recipe xmlns="https://svg.skia/expr/1.0" name="Tinted">
          <code>
            <param name="tint" type="color" default="#3b82f6" />
            <let name="ink" type="color">tint</let>
          </code>
          <slot name="ink" rest="true">ink</slot>
        </recipe>
        """;

    // Declares tint as well, and differently, so the first template's is the one kept.
    private const string Washed = """
        <recipe xmlns="https://svg.skia/expr/1.0" name="Washed">
          <code>
            <param name="tint" type="color" default="#ff0000" />
            <let name="wash">withAlpha(tint, 0.35)</let>
          </code>
          <slot name="wash" rest="true">wash</slot>
        </recipe>
        """;

    private static string Glyph(string colour) => $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M2 2h20v20z" fill="{colour}" /></svg>""";

    private static (ProjectWorkspace Workspace, ProjectGroup Group) Open(string name)
    {
        var workspace = new ProjectWorkspace(ProjectDocument.Parse(Project));

        return (workspace, workspace.Document.Root.Children.OfType<ProjectGroup>().Single(group => group.Name == name));
    }

    private static TemplateImport Bound(ProjectGroup target, string template, string name, string colour, string? source = null)
    {
        var icon = TemplateLibrary.Prepare(Glyph(colour));
        var recipe = SvgRecipe.Parse(template).Bind(icon.Survey, declared: ProjectDeclarations.Names(target)).Recipe!;

        return new TemplateImport(name, icon.Text, recipe, source);
    }

    private static IReadOnlyList<string> Declared(ProjectGroup group)
        => group.Code!.Elements().Select(element => (string)element.Attribute("name")!).ToList();

    [Fact]
    public void A_Batch_Under_The_Scheme_Needs_No_Declaration_And_Is_Templated()
    {
        var (workspace, group) = Open("Scheme");
        var code = group.CodeText;
        var notes = new List<string>();

        var added = TemplateLibrary.Import(
            workspace,
            group,
            group.Children.Count,
            new[]
            {
                Bound(group, Accent, "Home", "#000000", "streamline:a"),
                Bound(group, Accent, "Star", "#222222", "streamline:b"),
                Bound(group, Accent, "Flag", "#4147d5", "streamline:c"),
            },
            notes);

        Assert.Empty(notes);
        Assert.Equal(code, group.CodeText);
        Assert.Equal(new[] { "Bell", "Home", "Star", "Flag" }, group.Children.Select(child => child.Name));
        Assert.Equal(new[] { "streamline:a", "streamline:b", "streamline:c" }, added.Select(drawing => drawing.Source));
        Assert.All(added, drawing =>
        {
            Assert.Contains("fill=\"{{ stateAccentColor }}\"", drawing.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("e:code", drawing.Text, StringComparison.Ordinal);
        });
        Assert.Equal("add 3 drawings", workspace.UndoLabel);
    }

    [Fact]
    public void Under_An_Empty_Group_The_Missing_Names_Are_Declared_Once_And_A_Disagreement_Is_Said()
    {
        var (workspace, group) = Open("Empty");
        var notes = new List<string>();

        var added = TemplateLibrary.Import(
            workspace,
            group,
            0,
            new[]
            {
                Bound(group, Tinted, "Home", "#000000"),
                Bound(group, Tinted, "Star", "#222222"),
                Bound(group, Washed, "Flag", "#4147d5"),
            },
            notes);

        Assert.Equal(3, added.Count);
        Assert.Equal(new[] { "tint", "ink", "wash" }, Declared(group));
        Assert.EndsWith("\n      <e:let name=\"wash\">withAlpha(tint, 0.35)</e:let>\n    </e:code>", group.CodeText, StringComparison.Ordinal);
        Assert.Equal("#3b82f6", (string?)group.Code!.Elements().First().Attribute("default"));
        Assert.Equal("color", (string?)group.Code.Elements().ElementAt(1).Attribute("type"));
        Assert.Equal("Two templates declare 'tint' differently, so the first one's was kept.", Assert.Single(notes));
        Assert.Equal(new[] { "Home", "Star", "Flag", "Dot" }, group.Children.Select(child => child.Name));
        Assert.All(added, drawing => Assert.DoesNotContain("e:code", drawing.Text, StringComparison.Ordinal));
        Assert.Contains("fill=\"{{ wash }}\"", added[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void One_Undo_Takes_Back_The_Batch_And_Its_Declarations_Byte_For_Byte()
    {
        var (workspace, group) = Open("Empty");
        var before = workspace.Document.ToXml();

        TemplateLibrary.Import(workspace, group, 1, new[] { Bound(group, Tinted, "Home", "#000000"), Bound(group, Washed, "Flag", "#4147d5") }, new List<string>());

        var after = workspace.Document.ToXml();

        Assert.NotNull(group.Code);
        Assert.True(workspace.Undo());
        Assert.Equal(before, workspace.Document.ToXml());
        Assert.Null(group.Code);
        Assert.Equal(new[] { "Dot" }, group.Children.Select(child => child.Name));
        Assert.False(workspace.CanUndo);

        Assert.True(workspace.Redo());
        Assert.Equal(after, workspace.Document.ToXml());
    }

    [Fact]
    public void A_Drawing_Kept_As_It_Is_Is_Written_As_Given()
    {
        var (workspace, group) = Open("Empty");

        var added = Assert.Single(TemplateLibrary.Import(workspace, group, 0, new[] { new TemplateImport("Home", Glyph("#123456")) }, new List<string>()));

        Assert.Contains("fill=\"#123456\"", added.Text, StringComparison.Ordinal);
        Assert.Null(added.Source);
        Assert.Null(group.Code);
    }

    [Fact]
    public void What_Is_Already_Imported_Is_Named_By_Its_Source()
    {
        var (workspace, group) = Open("Scheme");

        TemplateLibrary.Import(
            workspace,
            group,
            0,
            new[] { Bound(group, Accent, "Home", "#000000", "streamline:a"), Bound(group, Accent, "Star", "#000000", "streamline:b") },
            new List<string>());

        Assert.Equal(
            new[] { "streamline:a", "streamline:b" },
            TemplateLibrary.Imported(group, new[] { "streamline:a", "streamline:c", "streamline:b" }).OrderBy(one => one, StringComparer.Ordinal));
        Assert.Empty(TemplateLibrary.Imported(Open("Empty").Group, new[] { "streamline:a" }));
    }

    [Fact]
    public void Replacing_From_Source_Keeps_Name_Place_And_Source_And_Is_One_Step()
    {
        var (workspace, group) = Open("Empty");

        TemplateLibrary.Import(workspace, group, 0, new[] { new TemplateImport("Home", Glyph("#000000"), null, "streamline:a") }, new List<string>());

        var drawing = (ProjectDrawing)group.Children[0];
        drawing.X = 10f;
        drawing.Y = 20f;

        var before = workspace.Document.ToXml();
        var notes = new List<string>();

        Assert.True(TemplateLibrary.Replace(workspace, drawing, Bound(group, Tinted, "ignored", "#333333", "streamline:other"), notes));

        Assert.Empty(notes);
        Assert.Same(drawing, group.Children[0]);
        Assert.Equal("Home", drawing.Name);
        Assert.Equal("streamline:a", drawing.Source);
        Assert.Equal((10f, 20f), (drawing.X, drawing.Y));
        Assert.Contains("fill=\"{{ ink }}\"", drawing.Text, StringComparison.Ordinal);
        Assert.Equal(new[] { "tint", "ink" }, Declared(group));
        Assert.Equal("update Home", workspace.UndoLabel);

        Assert.True(workspace.Undo());
        Assert.Equal(before, workspace.Document.ToXml());
        Assert.Contains("fill=\"#000000\"", drawing.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Template_Left_Unbound_Is_A_Note_Not_A_Drawing()
    {
        var (workspace, group) = Open("Empty");
        var notes = new List<string>();

        var added = TemplateLibrary.Import(
            workspace,
            group,
            0,
            new[] { new TemplateImport("Home", Glyph("#000000"), SvgRecipe.Parse(Tinted)), new TemplateImport("Star", Glyph("#000000")) },
            notes);

        Assert.Equal("Star", Assert.Single(added).Name);
        Assert.StartsWith("Home: The recipe has slots", Assert.Single(notes), StringComparison.Ordinal);
        Assert.Null(group.Code);
    }

    [Fact]
    public void A_Batch_Where_Nothing_Is_Added_Is_No_Step()
    {
        var (workspace, group) = Open("Empty");
        var notes = new List<string>();

        Assert.Empty(TemplateLibrary.Import(workspace, group, 0, new[] { new TemplateImport("Home", Glyph("#000000"), SvgRecipe.Parse(Tinted)) }, notes));

        Assert.Single(notes);
        Assert.False(workspace.CanUndo);
    }

    [Fact]
    public void A_Name_Declared_Below_The_Target_Is_Refused_Rather_Than_Declared_Twice()
    {
        var (workspace, group) = Open("Outer");
        var before = workspace.Document.ToXml();
        var notes = new List<string>();

        Assert.Empty(TemplateLibrary.Import(workspace, group, 0, new[] { Bound(group, Tinted, "Home", "#000000") }, notes));

        Assert.Equal("'Inner' declares 'tint' of its own, so declaring it on 'Outer' as well would declare it twice.", Assert.Single(notes));
        Assert.Equal(before, workspace.Document.ToXml());
        Assert.False(workspace.CanUndo);
    }

    [Fact]
    public void A_Sized_Template_Sizes_The_Drawing_It_Adds()
    {
        var (workspace, group) = Open("Scheme");

        var added = Assert.Single(TemplateLibrary.Import(
            workspace,
            group,
            0,
            new[] { Bound(group, Accent.Replace("name=\"Accent glyph\"", "name=\"Accent glyph\" size=\"30\"", StringComparison.Ordinal), "Home", "#000000") },
            new List<string>()));

        Assert.Contains("viewBox=\"0 0 30 30\"", added.Text, StringComparison.Ordinal);
        Assert.Contains("<g transform=\"scale(1.25)\"><path d=\"M2 2h20v20z\" fill=\"{{ stateAccentColor }}\" /></g>", added.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Replacing_Where_Nothing_Needs_Declaring_Leaves_The_Block_Alone()
    {
        var (workspace, group) = Open("Scheme");
        var drawing = (ProjectDrawing)group.Children[0];
        var before = workspace.Document.ToXml();
        var code = group.CodeText;

        Assert.True(TemplateLibrary.Replace(workspace, drawing, Bound(group, Accent, "ignored", "#333333"), new List<string>()));

        Assert.Equal(code, group.CodeText);
        Assert.Contains("d=\"M2 2h20v20z\" fill=\"{{ stateAccentColor }}\"", drawing.Text, StringComparison.Ordinal);

        Assert.True(workspace.Undo());
        Assert.Equal(before, workspace.Document.ToXml());
    }

    /// <summary>The group panel's first declaration takes the same path as an import's, so it is placed as one is.</summary>
    [Fact]
    public void A_First_Declaration_On_A_Group_Sits_At_The_Group_Depth()
    {
        var (workspace, group) = Open("Empty");

        Assert.Null(new GroupTarget(workspace, group).Commit("add k", source => Svg.SourceEditing.SvgDeclarationEditor.AddLet(source, "k", "1")));

        Assert.Equal(
            "<e:code xmlns:e=\"https://svg.skia/expr/1.0\">\n      <e:let name=\"k\">1</e:let>\n    </e:code>",
            group.CodeText.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task The_Window_Imports_A_Batch_Shows_The_Last_And_Says_What_It_Noted()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "icons.svgstudio");
        File.WriteAllText(path, Project);

        var window = new MainWindow();
        var said = new List<(string Title, string Message)>();
        window.Announce = (title, message) =>
        {
            said.Add((title, message));

            return Task.CompletedTask;
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        await window.OpenAsync(new[] { path });
        Dispatcher.UIThread.RunJobs();

        var group = window.Workspace!.Document.Root.Children.OfType<ProjectGroup>().Single(one => one.Name == "Empty");

        var added = await window.ImportAsync(group, 0, new[] { Bound(group, Tinted, "Home", "#000000"), Bound(group, Washed, "Flag", "#4147d5") });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "Home", "Flag" }, added.Select(drawing => drawing.Name));
        Assert.Equal(("Imported", "Two templates declare 'tint' differently, so the first one's was kept."), Assert.Single(said));
        Assert.Same(added[^1], ((TabItem)window.FindControl<TabControl>("Tabs")!.SelectedItem!).Tag);
        Assert.Equal("add 2 drawings", window.Workspace.UndoLabel);

        window.Close();
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}
