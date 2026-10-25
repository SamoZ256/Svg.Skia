using System;
using System.Collections.Generic;
using System.Linq;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions.Recipes;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// The project's import templates, kept in one <c>&lt;e:templates&gt;</c> on its root, and where a
/// drawing was imported from.
/// </summary>
public class ProjectTemplatesTests
{
    private const string Manifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <studio namespace="Demo.Icons">
          <e:templates xmlns:e="https://svg.skia/expr/1.0">
            <e:recipe name="Tinted">
              <e:match colors="1" />
              <e:slot name="ink" rest="true">ink</e:slot>
            </e:recipe>
            <recipe xmlns="https://svg.skia/expr/1.0" name="Kept"/>
          </e:templates>
        </studio>

        """;

    /// <summary>The project's folder: the templates in its own file, and a drawing that says where it came from.</summary>
    private static readonly IReadOnlyDictionary<string, string> Files = new Dictionary<string, string>
    {
        ["studio.xml"] = Manifest,
        ["Line/group.xml"] = "<group />\n",
        ["Line/Bell.svg"] = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M0 0h24" stroke="#000000" />"""
                            + "\n\n  <metadata><drawing xmlns=\"https://svg.skia/studio/1.0\" source=\"streamline:abc123\" /></metadata></svg>\n",
    };

    private const string Tinted = """
        <e:templates xmlns:e="https://svg.skia/expr/1.0">
          <e:recipe name="Tinted"><e:slot name="ink" rest="true">ink</e:slot></e:recipe>
        </e:templates>
        """;

    // The prefix bound on <studio> rather than on the block, and one recipe binding it again itself.
    private const string BoundAbove = """
        <?xml version="1.0" encoding="utf-8"?>
        <studio xmlns:e="https://svg.skia/expr/1.0">
          <e:templates>
            <e:recipe name="A" />
            <e:recipe xmlns:e="https://svg.skia/expr/1.0" name="B" />
          </e:templates>
        </studio>

        """;

    private static ProjectDocument Read() => ProjectDocument.Read(Files, null);

    private static ProjectDocument Holding(string manifest)
        => ProjectDocument.Read(new Dictionary<string, string> { ["studio.xml"] = manifest }, null);

    [Fact]
    public void Templates_And_A_Source_Are_Written_Back_Byte_For_Byte()
    {
        var document = Read();

        Assert.Equal(Files, document.Files());
        Assert.Equal("streamline:abc123", document.Root.Drawings.Single().Source);

        // Not a row: the templates are what the project says, not something in its tree.
        Assert.Single(document.Root.Children);
    }

    [Fact]
    public void Each_Template_Reads_On_Its_Own()
    {
        var templates = Read().Root.Templates;

        Assert.Equal(new[] { "Tinted", "Kept" }, templates.Select(one => one.Name));

        // The prefix is bound on the block, not on the recipe, so the text has to bring it along.
        var tinted = SvgRecipe.Parse(templates[0].Text);

        Assert.Equal("Tinted", tinted.Name);
        Assert.Equal("ink", tinted.Slots.Single().Name);
        Assert.Equal("Kept", SvgRecipe.Parse(templates[1].Text).Name);
    }

    [Fact]
    public void A_Bad_Template_Does_Not_Stop_The_Project_Loading()
    {
        var document = Holding(Manifest.Replace("<e:match colors=\"1\" />", "<e:bogus />", StringComparison.Ordinal));

        Assert.Throws<SvgRecipeException>(() => SvgRecipe.Parse(document.Root.Templates[0].Text));
    }

    [Fact]
    public void Templates_In_A_Group_Are_Refused()
    {
        var files = new Dictionary<string, string>(Files)
        {
            ["Line/group.xml"] = "<group>\n  <e:templates xmlns:e=\"https://svg.skia/expr/1.0\" />\n</group>\n"
        };

        var refusal = Assert.Throws<SvgcProjectException>(() => ProjectDocument.Read(files, null));

        Assert.Contains("in studio.xml", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_Blocks_Of_Templates_Are_Refused()
    {
        var twice = Manifest.Replace("</studio>", "  <e:templates xmlns:e=\"https://svg.skia/expr/1.0\" />\n</studio>", StringComparison.Ordinal);

        var refusal = Assert.Throws<SvgcProjectException>(() => Holding(twice));

        Assert.Contains("more than one <e:templates>", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Group_Cannot_Name_A_Source()
    {
        var files = new Dictionary<string, string>(Files) { ["Line/group.xml"] = "<group source=\"x\" />\n" };

        Assert.Throws<SvgcProjectException>(() => ProjectDocument.Read(files, null));
    }

    [Fact]
    public void A_Project_Without_Templates_Is_Somewhere_To_Keep_Them()
    {
        var document = ProjectDocument.Empty();
        document.Root.AddDrawing("Dot", "<svg xmlns=\"http://www.w3.org/2000/svg\" />");

        Assert.Empty(document.Root.Templates);
        Assert.Null(document.Root.SetTemplates(document.Root.TemplatesText));
        Assert.Null(document.Root.TemplatesBlock);

        Assert.Null(document.Root.SetTemplates(Tinted));

        // A new block arrives written from column zero and is moved to the depth it lands at.
        Assert.Equal("""
            <?xml version="1.0" encoding="utf-8"?>
            <studio>
              <e:templates xmlns:e="https://svg.skia/expr/1.0">
                <e:recipe name="Tinted"><e:slot name="ink" rest="true">ink</e:slot></e:recipe>
              </e:templates>
            </studio>

            """, document.Files()["studio.xml"]);
    }

    [Fact]
    public void Templates_That_Are_Not_A_Block_Are_Refused()
    {
        var document = Read();

        Assert.NotNull(document.Root.SetTemplates("<e:templates"));
        Assert.NotNull(document.Root.SetTemplates("<e:code xmlns:e=\"https://svg.skia/expr/1.0\"><e:param name=\"a\" type=\"number\" default=\"1\" /></e:code>"));
        Assert.Equal(Files, document.Files());
    }

    [Fact]
    public void Undo_Puts_The_Templates_Back()
    {
        var workspace = new ProjectWorkspace(Read());
        var root = workspace.Document.Root;

        workspace.Do("Edit templates", () => ProjectSnapshot.Templates(root), () => Assert.Null(root.SetTemplates(Tinted)));

        Assert.Equal(new[] { "Tinted" }, root.Templates.Select(one => one.Name));

        Assert.True(workspace.Undo());

        Assert.Equal(Files, workspace.Document.Files());
    }

    [Fact]
    public void A_Template_Put_Under_A_Label_Is_Named_So_In_The_Edit_Menu()
    {
        var workspace = new ProjectWorkspace(Read());
        var root = workspace.Document.Root;

        Assert.Null(TemplateLibrary.Put(workspace, root.Templates.Count, "<recipe xmlns=\"https://svg.skia/expr/1.0\" name=\"Mine\" />", "Claude Code: add template Mine"));
        Assert.Equal("Claude Code: add template Mine", workspace.UndoLabel);
        Assert.Equal(new[] { "Tinted", "Kept", "Mine" }, root.Templates.Select(one => one.Name));

        // Without one, the step names itself as before.
        Assert.Null(TemplateLibrary.Put(workspace, root.Templates.Count, "<recipe xmlns=\"https://svg.skia/expr/1.0\" name=\"Yours\" />"));
        Assert.Equal("add template Yours", workspace.UndoLabel);

        Assert.True(workspace.Undo());
        Assert.True(workspace.Undo());
        Assert.Equal(Files, workspace.Document.Files());
    }

    [Fact]
    public void Templates_Bound_From_Above_Read_On_Their_Own()
    {
        var templates = Holding(BoundAbove).Root.Templates;

        Assert.Equal("A", SvgRecipe.Parse(templates[0].Text).Name);
        Assert.Equal("B", SvgRecipe.Parse(templates[1].Text).Name);

        // Brought along only where the recipe does not bind it itself.
        Assert.Contains("xmlns:e", templates[0].Text, StringComparison.Ordinal);
        Assert.Equal(1, templates[1].Text.Split("xmlns:e").Length - 1);
    }

    [Fact]
    public void Templates_Bound_From_Above_Survive_An_Unedited_Save_And_An_Undo()
    {
        var workspace = new ProjectWorkspace(Holding(BoundAbove));
        var root = workspace.Document.Root;

        Assert.Null(root.SetTemplates(root.TemplatesText));
        Assert.Equal(BoundAbove, workspace.Document.Files()["studio.xml"]);

        workspace.Do("Edit templates", () => ProjectSnapshot.Templates(root), () => Assert.Null(root.SetTemplates(Tinted)));
        var edited = workspace.Document.Files()["studio.xml"];

        Assert.True(workspace.Undo());
        Assert.Equal(BoundAbove, workspace.Document.Files()["studio.xml"]);

        Assert.True(workspace.Redo());
        Assert.Equal(edited, workspace.Document.Files()["studio.xml"]);
    }

    [Fact]
    public void Undo_Takes_A_First_Block_Of_Templates_Out_Again()
    {
        var workspace = new ProjectWorkspace(ProjectDocument.Empty());
        var root = workspace.Document.Root;
        var empty = workspace.Document.Files();

        workspace.Do("Edit templates", () => ProjectSnapshot.Templates(root), () => Assert.Null(root.SetTemplates(Tinted)));

        Assert.True(workspace.Undo());
        Assert.Null(root.TemplatesBlock);
        Assert.Equal(empty, workspace.Document.Files());
    }

    [Fact]
    public void A_Source_Is_Set_Cleared_And_Copied()
    {
        var document = Read();
        var group = (ProjectGroup)document.Root.Children.Single();
        var bell = document.Root.Drawings.Single();

        var copy = (ProjectDrawing)group.Copy(bell);

        Assert.Equal("streamline:abc123", copy.Source);

        bell.Source = "  streamline:def456 ";
        Assert.Equal("streamline:def456", bell.Source);

        bell.Source = " ";
        Assert.Null(bell.Source);
        Assert.DoesNotContain("source=\"streamline:def456\"", TestPackage.Dump(document), StringComparison.Ordinal);

        copy.Source = null;
        Assert.Null(copy.Source);
        Assert.DoesNotContain("source=", TestPackage.Dump(document), StringComparison.Ordinal);
    }
}
