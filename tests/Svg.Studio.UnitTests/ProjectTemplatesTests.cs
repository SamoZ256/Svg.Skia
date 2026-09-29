using System;
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
    private const string Project = """
        <?xml version="1.0" encoding="utf-8"?>
        <studio namespace="Demo.Icons">
          <e:templates xmlns:e="https://svg.skia/expr/1.0">
            <e:recipe name="Tinted">
              <e:match colors="1" />
              <e:slot name="ink" rest="true">ink</e:slot>
            </e:recipe>
            <recipe xmlns="https://svg.skia/expr/1.0" name="Kept"/>
          </e:templates>

          <group name="Line">
            <drawing name="Bell"   source="streamline:abc123">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M0 0h24" stroke="#000000" /></svg>
            </drawing>
          </group>
        </studio>

        """;

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
          <drawing name="Dot">
            <svg xmlns="http://www.w3.org/2000/svg" />
          </drawing>
        </studio>

        """;

    [Fact]
    public void Templates_And_A_Source_Are_Written_Back_Byte_For_Byte()
    {
        var document = ProjectDocument.Parse(Project);

        Assert.Equal(Project, document.ToXml());
        Assert.Equal("streamline:abc123", document.Root.Drawings.Single().Source);

        // Not a row: the templates are what the project says, not something in its tree.
        Assert.Single(document.Root.Children);
    }

    [Fact]
    public void Each_Template_Reads_On_Its_Own()
    {
        var templates = ProjectDocument.Parse(Project).Root.Templates;

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
        var document = ProjectDocument.Parse(Project.Replace("<e:match colors=\"1\" />", "<e:bogus />", StringComparison.Ordinal));

        Assert.Throws<SvgRecipeException>(() => SvgRecipe.Parse(document.Root.Templates[0].Text));
    }

    [Fact]
    public void Templates_In_A_Group_Are_Refused()
    {
        var xml = Project.Replace(
            "<group name=\"Line\">",
            "<group name=\"Line\">\n    <e:templates xmlns:e=\"https://svg.skia/expr/1.0\" />",
            StringComparison.Ordinal);

        var refusal = Assert.Throws<SvgcProjectException>(() => ProjectDocument.Parse(xml));

        Assert.Contains("on <studio>", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_Blocks_Of_Templates_Are_Refused()
    {
        var xml = Project.Replace(
            "\n  <group",
            "  <e:templates xmlns:e=\"https://svg.skia/expr/1.0\" />\n  <group",
            StringComparison.Ordinal);

        var refusal = Assert.Throws<SvgcProjectException>(() => ProjectDocument.Parse(xml));

        Assert.Contains("more than one <e:templates>", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Group_Cannot_Name_A_Source()
    {
        var xml = Project.Replace("<group name=\"Line\">", "<group name=\"Line\" source=\"x\">", StringComparison.Ordinal);

        Assert.Throws<SvgcProjectException>(() => ProjectDocument.Parse(xml));
    }

    [Fact]
    public void A_Project_Without_Templates_Is_Somewhere_To_Keep_Them()
    {
        var document = ProjectDocument.Empty();
        document.Root.AddDrawing("Dot", "<svg xmlns=\"http://www.w3.org/2000/svg\" />", 0);

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
              <drawing name="Dot">
                <svg xmlns="http://www.w3.org/2000/svg" />
              </drawing>
            </studio>

            """, document.ToXml());
    }

    [Fact]
    public void Templates_That_Are_Not_A_Block_Are_Refused()
    {
        var document = ProjectDocument.Parse(Project);

        Assert.NotNull(document.Root.SetTemplates("<e:templates"));
        Assert.NotNull(document.Root.SetTemplates("<e:code xmlns:e=\"https://svg.skia/expr/1.0\"><e:param name=\"a\" type=\"number\" default=\"1\" /></e:code>"));
        Assert.Equal(Project, document.ToXml());
    }

    [Fact]
    public void Undo_Puts_The_Templates_Back()
    {
        var workspace = new ProjectWorkspace(ProjectDocument.Parse(Project));
        var root = workspace.Document.Root;

        workspace.Do("Edit templates", () => ProjectSnapshot.Templates(root), () => Assert.Null(root.SetTemplates(Tinted)));

        Assert.Equal(new[] { "Tinted" }, root.Templates.Select(one => one.Name));

        Assert.True(workspace.Undo());

        Assert.Equal(Project, workspace.Document.ToXml());
    }

    [Fact]
    public void Templates_Bound_From_Above_Read_On_Their_Own()
    {
        var templates = ProjectDocument.Parse(BoundAbove).Root.Templates;

        Assert.Equal("A", SvgRecipe.Parse(templates[0].Text).Name);
        Assert.Equal("B", SvgRecipe.Parse(templates[1].Text).Name);

        // Brought along only where the recipe does not bind it itself.
        Assert.Contains("xmlns:e", templates[0].Text, StringComparison.Ordinal);
        Assert.Equal(1, templates[1].Text.Split("xmlns:e").Length - 1);
    }

    [Fact]
    public void Templates_Bound_From_Above_Survive_An_Unedited_Save_And_An_Undo()
    {
        var workspace = new ProjectWorkspace(ProjectDocument.Parse(BoundAbove));
        var root = workspace.Document.Root;

        Assert.Null(root.SetTemplates(root.TemplatesText));
        Assert.Equal(BoundAbove, workspace.Document.ToXml());

        workspace.Do("Edit templates", () => ProjectSnapshot.Templates(root), () => Assert.Null(root.SetTemplates(Tinted)));
        var edited = workspace.Document.ToXml();

        Assert.True(workspace.Undo());
        Assert.Equal(BoundAbove, workspace.Document.ToXml());

        Assert.True(workspace.Redo());
        Assert.Equal(edited, workspace.Document.ToXml());
    }

    [Fact]
    public void Undo_Takes_A_First_Block_Of_Templates_Out_Again()
    {
        var workspace = new ProjectWorkspace(ProjectDocument.Empty());
        var root = workspace.Document.Root;
        var empty = workspace.Document.ToXml();

        workspace.Do("Edit templates", () => ProjectSnapshot.Templates(root), () => Assert.Null(root.SetTemplates(Tinted)));

        Assert.True(workspace.Undo());
        Assert.Null(root.TemplatesBlock);
        Assert.Equal(empty, workspace.Document.ToXml());
    }

    [Fact]
    public void A_Source_Is_Set_Cleared_And_Copied()
    {
        var document = ProjectDocument.Parse(Project);
        var group = (ProjectGroup)document.Root.Children.Single();
        var bell = document.Root.Drawings.Single();

        var copy = (ProjectDrawing)group.Copy(bell, 1);

        Assert.Equal("streamline:abc123", copy.Source);

        bell.Source = "  streamline:def456 ";
        Assert.Equal("streamline:def456", bell.Source);

        bell.Source = " ";
        Assert.Null(bell.Source);
        Assert.DoesNotContain("source=\"streamline:def456\"", document.ToXml(), StringComparison.Ordinal);

        copy.Source = null;
        Assert.Null(copy.Source);
        Assert.DoesNotContain("source=", document.ToXml(), StringComparison.Ordinal);
    }
}
