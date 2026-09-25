// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Linq;
using Svg.Expressions;
using Svg.SourceEditing;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// Renaming a variable a group declares, which is a rename of every drawing under it.
/// </summary>
/// <remarks>
/// A group holds the declaration and its drawings hold the uses, so the two halves of a rename are
/// in different documents. Writing only the block left every drawing naming something gone — and not
/// even visibly, because what gets spliced into a drawing is narrowed to the names that drawing
/// reaches: the renamed parameter stops being reached, so it stops being written in at all and the
/// drawing is built with a use and no declaration.
///
/// Driven through <see cref="GroupTarget"/> rather than through a window. It is where the branch is
/// walked, and <c>Svg.Studio.UnitTests</c> builds its own <c>Application</c>, so a test that does not
/// need one should not ask for one.
/// </remarks>
public class GroupRenameTests
{
    /// <summary>
    /// A group declaring one parameter, with everything under it that a rename has to reach.
    /// </summary>
    /// <remarks>
    /// Two drawings using it two ways — in an attribute and in what a <c>&lt;text&gt;</c> says — a
    /// nested group whose let names it, a drawing under that, and one drawing that never names it at
    /// all, which is the one that has to come back untouched.
    /// </remarks>
    private const string Project = """
        <studio namespace="Demo.Icons">

          <group name="Badges">
            <e:code xmlns:e="https://svg.skia/expr/1.0">
              <e:param name="tint" type="color" default="#ff0000" />
            </e:code>

            <drawing name="Filled">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                <rect width="24" height="24" fill="{{ tint }}" />
              </svg>
            </drawing>

            <drawing name="Labelled">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                <text x="1" y="8">{{ tint }}</text>
              </svg>
            </drawing>

            <drawing name="Plain">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                <rect width="24" height="24" fill="#00ff00" />
              </svg>
            </drawing>

            <group name="Inner">
              <e:code xmlns:e="https://svg.skia/expr/1.0">
                <e:let name="deep">mix(tint, #000000, 0.5)</e:let>
              </e:code>

              <drawing name="Deeper">
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <rect width="24" height="24" fill="{{ deep }}" stroke="{{ tint }}" />
                </svg>
              </drawing>
            </group>

          </group>

        </studio>
        """;

    private static (ProjectWorkspace Workspace, ProjectGroup Group) Open(string xml = Project)
    {
        var workspace = new ProjectWorkspace(ProjectDocument.Parse(xml));

        return (workspace, workspace.Document.Root.Children.OfType<ProjectGroup>().Single());
    }

    /// <summary>Renames the group's <c>tint</c>, as the ⋯ on its row does.</summary>
    private static string? Rename(ProjectWorkspace workspace, ProjectGroup group, string to, GroupTarget? target = null)
        => (target ?? new GroupTarget(workspace, group)).Commit(
            $"change tint",
            source => SvgDeclarationEditor.Update(source, "tint", new SvgExpressionParameter(to, ExprType.Color, "#ff0000")),
            new SvgDeclarationRename("tint", to));

    private static ProjectDrawing Drawing(ProjectGroup group, string name)
        => group.Drawings.Single(drawing => drawing.Name == name);

    private static ProjectGroup Inner(ProjectGroup group)
        => group.Children.OfType<ProjectGroup>().Single();

    // ---- carrying the rename ----

    [Fact]
    public void A_Group_Rename_Carries_Every_Drawing_Under_It()
    {
        var (workspace, group) = Open();

        Assert.Null(Rename(workspace, group, "shade"));

        Assert.Contains("name=\"shade\"", group.CodeText);

        // In an attribute, and in what an element says -- the second of which the walk used to miss
        // even inside one document.
        Assert.Contains("fill=\"{{ shade }}\"", Drawing(group, "Filled").Text);
        Assert.Contains("<text x=\"1\" y=\"8\">{{ shade }}</text>", Drawing(group, "Labelled").Text);

        Assert.DoesNotContain("tint", Drawing(group, "Filled").Text);
        Assert.DoesNotContain("tint", Drawing(group, "Labelled").Text);
    }

    [Fact]
    public void A_Group_Rename_Carries_A_Nested_Group_And_What_Is_Under_It()
    {
        var (workspace, group) = Open();

        Assert.Null(Rename(workspace, group, "shade"));

        // The nested group's let names it, and the drawing below that names it again.
        Assert.Contains("mix(shade, #000000, 0.5)", Inner(group).CodeText);
        Assert.Contains("stroke=\"{{ shade }}\"", Drawing(group, "Deeper").Text);
    }

    /// <summary>A drawing that never named it is not rewritten, down to the byte.</summary>
    /// <remarks>
    /// The rename is written per document rather than over the branch, so a drawing with nothing to
    /// change is not read out and written back — which would reformat it and put it on the undo
    /// entry for a gesture it had no part in.
    /// </remarks>
    [Fact]
    public void A_Group_Rename_Leaves_A_Drawing_That_Never_Named_It_Alone()
    {
        var (workspace, group) = Open();

        var was = Drawing(group, "Plain").Text;

        Assert.Null(Rename(workspace, group, "shade"));

        Assert.Equal(was, Drawing(group, "Plain").Text);
    }

    [Fact]
    public void Renaming_A_Group_Let_Carries_The_Branch_Too()
    {
        var (workspace, group) = Open();
        var inner = Inner(group);

        var refusal = new GroupTarget(workspace, inner).Commit(
            "change deep",
            source => SvgDeclarationEditor.UpdateLet(source, "deep", "darker", "mix(tint, #000000, 0.5)"),
            new SvgDeclarationRename("deep", "darker"));

        Assert.Null(refusal);

        Assert.Contains("name=\"darker\"", inner.CodeText);
        Assert.Contains("fill=\"{{ darker }}\"", Drawing(group, "Deeper").Text);
    }

    // ---- one thing to take back ----

    /// <summary>
    /// The block and every drawing go back together.
    /// </summary>
    /// <remarks>
    /// The assertion the whole shape is for. A gesture whose capture named only the group would
    /// count as one entry on the history and put back only half of what it did, which is the failure
    /// that reads as an undo having worked.
    /// </remarks>
    [Fact]
    public void A_Group_Rename_Is_One_Thing_To_Take_Back()
    {
        var (workspace, group) = Open();

        var before = workspace.Document.ToXml();

        Assert.Null(Rename(workspace, group, "shade"));
        Assert.True(workspace.CanUndo);

        Assert.True(workspace.Undo());

        Assert.Equal(before, workspace.Document.ToXml());
    }

    [Fact]
    public void Putting_A_Group_Rename_Back_Again_Is_The_Same_Text()
    {
        var (workspace, group) = Open();

        Assert.Null(Rename(workspace, group, "shade"));

        var renamed = workspace.Document.ToXml();

        Assert.True(workspace.Undo());
        Assert.True(workspace.Redo());

        Assert.Equal(renamed, workspace.Document.ToXml());
    }

    // ---- refused whole, or not at all ----

    /// <summary>
    /// A drawing that declares the new name itself stops the rename, and stops all of it.
    /// </summary>
    /// <remarks>
    /// One set of names across the chain, so the drawing would be built with the name declared
    /// twice and refuse. What matters as much as the refusal is that the group's own block is left
    /// saying the old name: a rename that took in the block and nowhere else is the defect.
    /// </remarks>
    [Fact]
    public void A_Rename_Onto_A_Name_A_Drawing_Declares_Is_Refused_Whole()
    {
        var (workspace, group) = Open(Project.Replace(
            """<text x="1" y="8">{{ tint }}</text>""",
            """<defs><e:code xmlns:e="https://svg.skia/expr/1.0"><e:param name="shade" type="color" default="#0000ff" /></e:code></defs>"""
                + "\n      <text x=\"1\" y=\"8\">{{ shade }}</text>"));

        var refusal = Rename(workspace, group, "shade");

        Assert.NotNull(refusal);
        Assert.Contains("Labelled", refusal);
        Assert.Contains("shade", refusal);

        // Nothing moved, the block included, and there is nothing on the history to take back.
        Assert.Contains("name=\"tint\"", group.CodeText);
        Assert.Contains("fill=\"{{ tint }}\"", Drawing(group, "Filled").Text);
        Assert.False(workspace.CanUndo);
    }

    /// <summary>
    /// A drawing holding an expression nothing can read stops the rename, and stops all of it.
    /// </summary>
    /// <remarks>
    /// A use that cannot be read is still a use, so renaming around it would leave the drawing
    /// naming something gone — the same reason the walk refuses rather than skips within one
    /// document. An unclosed string, because that is a refusal the lexer makes: a trailing operator
    /// tokenises perfectly well and is the parser's complaint, not this walk's.
    /// </remarks>
    [Fact]
    public void A_Drawing_That_Cannot_Be_Read_Refuses_The_Whole_Rename()
    {
        var (workspace, group) = Open(Project.Replace(
            "fill=\"{{ tint }}\"",
            "fill=\"{{ tint }}\" stroke=\"{{ 'red }}\""));

        var refusal = Rename(workspace, group, "shade");

        Assert.NotNull(refusal);
        Assert.Contains("Filled", refusal);

        Assert.Contains("name=\"tint\"", group.CodeText);
        Assert.Contains("<text x=\"1\" y=\"8\">{{ tint }}</text>", Drawing(group, "Labelled").Text);
        Assert.False(workspace.CanUndo);
    }

    /// <summary>
    /// A drawing open with edits nobody has saved refuses the rename.
    /// </summary>
    /// <remarks>
    /// Written into the project, the rename would be written over again the moment that tab was
    /// saved, out of a buffer that still says the old name — and only for that one drawing, and
    /// without a word. Refusing is the answer because writing the buffer as well would put half the
    /// gesture onto that tab's own history rather than the project's.
    /// </remarks>
    [Fact]
    public void A_Drawing_Held_Open_With_Unsaved_Edits_Refuses_The_Rename()
    {
        var (workspace, group) = Open();

        var target = new GroupTarget(workspace, group)
        {
            Held = drawing => drawing.Name == "Labelled" ? drawing.Text + "\n<!-- being edited -->" : null
        };

        var refusal = Rename(workspace, group, "shade", target);

        Assert.NotNull(refusal);
        Assert.Contains("Labelled", refusal);

        Assert.Contains("name=\"tint\"", group.CodeText);
        Assert.False(workspace.CanUndo);
    }

    /// <summary>A tab showing exactly what the project says is not an unsaved edit.</summary>
    [Fact]
    public void A_Drawing_Held_Open_Unchanged_Does_Not_Refuse()
    {
        var (workspace, group) = Open();

        var target = new GroupTarget(workspace, group) { Held = drawing => drawing.Text };

        Assert.Null(Rename(workspace, group, "shade", target));
        Assert.Contains("fill=\"{{ shade }}\"", Drawing(group, "Filled").Text);
    }

    // ---- what is not a rename ----

    /// <summary>
    /// Changing anything but the name writes the block and reads no drawing at all.
    /// </summary>
    /// <remarks>
    /// The branch is walked only where a rename says to, so moving a slider's bounds did not become
    /// a read of every drawing in the project.
    /// </remarks>
    [Fact]
    public void Changing_A_Parameter_Without_Renaming_It_Touches_Only_The_Block()
    {
        var (workspace, group) = Open();

        var was = Drawing(group, "Filled").Text;

        var refusal = new GroupTarget(workspace, group).Commit(
            "change tint",
            source => SvgDeclarationEditor.Update(source, "tint", new SvgExpressionParameter("tint", ExprType.Color, "#00ff00")));

        Assert.Null(refusal);

        Assert.Contains("default=\"#00ff00\"", group.CodeText);
        Assert.Equal(was, Drawing(group, "Filled").Text);
    }
}
