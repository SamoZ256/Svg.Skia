using System;
using System.Linq;
using System.Xml.Linq;
using Svg.SourceEditing;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// One entry to take back per gesture, over a tree, reversed by the drawing's own text.
/// </summary>
public class SvgSourceWorkspaceTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private const string Source = """
        <!-- a dial -->
        <svg xmlns="http://www.w3.org/2000/svg"
             width='64'
             height='64'>
          <rect x="1" y="2" fill="{{ tint }}"/>
          <circle cx="32" cy="32" r="20" />
        </svg>
        """;

    private static SvgSourceWorkspace Open(string svgText = Source)
    {
        var workspace = SvgSourceWorkspace.Open(svgText, out var refusal);

        Assert.NotNull(workspace);
        Assert.Null(refusal);

        return workspace!;
    }

    private static string? Fill(SvgSourceDocument source, string value)
    {
        source.Document.Root!.Element(Svg + "circle")!.SetAttributeValue("fill", value);

        return null;
    }

    [Fact]
    public void An_Opened_Drawing_Holds_Its_Own_Text_And_Nothing_To_Take_Back()
    {
        var workspace = Open();

        Assert.Equal(Source, workspace.Text);
        Assert.False(workspace.IsModified);
        Assert.False(workspace.CanUndo);
        Assert.False(workspace.CanRedo);
    }

    [Fact]
    public void An_Edit_Is_Kept_And_Taken_Back_Byte_For_Byte()
    {
        var workspace = Open();

        Assert.Null(workspace.Commit("set fill", source => Fill(source, "red")));

        Assert.Contains("""<circle cx="32" cy="32" r="20" fill="red" />""", workspace.Text);
        Assert.True(workspace.IsModified);
        Assert.True(workspace.CanUndo);

        Assert.True(workspace.Undo());

        // The whole point: back to the file, not to something that renders like it.
        Assert.Equal(Source, workspace.Text);
        Assert.False(workspace.IsModified);
        Assert.True(workspace.CanRedo);

        Assert.True(workspace.Redo());
        Assert.Contains("fill=\"red\"", workspace.Text);
        Assert.True(workspace.IsModified);
    }

    [Fact]
    public void The_Document_Is_The_Text_After_Every_Step()
    {
        var workspace = Open();

        workspace.Commit("set fill", source => Fill(source, "red"));
        workspace.Undo();

        // Undo re-reads, so a stale tree would show here as a document that disagrees with the text.
        Assert.Null(workspace.Document.Document.Root!.Element(Svg + "circle")!.Attribute("fill"));
        Assert.Equal(workspace.Text, workspace.Document.ToText());
    }

    [Fact]
    public void Several_Mutations_In_One_Commit_Are_One_Thing_To_Take_Back()
    {
        // What BeginUpdate and EndUpdate were doing around a batch of spans: a resize writes three
        // attributes and is one gesture.
        var workspace = Open();

        workspace.Commit("resize", source =>
        {
            source.Document.Root!.SetAttributeValue("width", "128");
            source.Document.Root!.SetAttributeValue("height", "128");
            source.Document.Root!.SetAttributeValue("viewBox", "0 0 128 128");

            return null;
        });

        Assert.Contains("width='128'", workspace.Text);
        Assert.True(workspace.Undo());
        Assert.Equal(Source, workspace.Text);
        Assert.False(workspace.CanUndo);
    }

    [Fact]
    public void A_Refusal_Puts_Back_Whatever_The_Edit_Had_Already_Changed()
    {
        // The case that makes this the same mechanism as undo: a declaration can only be checked
        // after the tree has been changed, so a refusal has to be able to undo a half-made edit.
        var workspace = Open();

        var refusal = workspace.Commit("set fill", source =>
        {
            source.Document.Root!.Element(Svg + "circle")!.SetAttributeValue("fill", "red");

            return "That name is already taken.";
        });

        Assert.Equal("That name is already taken.", refusal);
        Assert.Equal(Source, workspace.Text);
        Assert.False(workspace.IsModified);
        Assert.False(workspace.CanUndo);
    }

    [Fact]
    public void An_Edit_That_Throws_Leaves_The_Drawing_Where_It_Was()
    {
        var workspace = Open();

        Assert.Throws<InvalidOperationException>(() => workspace.Commit("set fill", source =>
        {
            source.Document.Root!.Element(Svg + "circle")!.SetAttributeValue("fill", "red");

            throw new InvalidOperationException("a fault in an editor");
        }));

        Assert.Equal(Source, workspace.Text);
        Assert.False(workspace.IsModified);
    }

    [Fact]
    public void An_Edit_That_Changes_Nothing_Is_Not_A_Step()
    {
        var workspace = Open();

        Assert.Null(workspace.Commit("set y", source =>
        {
            source.Document.Root!.Element(Svg + "rect")!.SetAttributeValue("y", "2");

            return null;
        }));

        Assert.False(workspace.IsModified);
        Assert.False(workspace.CanUndo);
        Assert.Equal(Source, workspace.Text);
    }

    [Fact]
    public void Editing_After_Taking_Back_Drops_What_Was_Ahead()
    {
        var workspace = Open();

        workspace.Commit("set fill", source => Fill(source, "red"));
        workspace.Commit("set fill", source => Fill(source, "blue"));
        workspace.Undo();
        workspace.Commit("set fill", source => Fill(source, "green"));

        Assert.False(workspace.CanRedo);
        Assert.Contains("fill=\"green\"", workspace.Text);

        Assert.True(workspace.Undo());
        Assert.Contains("fill=\"red\"", workspace.Text);
    }

    [Fact]
    public void Nothing_To_Take_Back_Answers_False_So_A_Host_Can_Ask_What_Is_Behind_It()
    {
        // Studio asks the drawing first and the recipe behind it second, and only reaches the
        // recipe because the drawing answered false.
        var workspace = Open();

        Assert.False(workspace.Undo());
        Assert.False(workspace.Redo());
    }

    [Fact]
    public void Saving_Makes_It_Unmodified_And_Taking_Back_To_There_Does_Too()
    {
        var workspace = Open();

        workspace.Commit("set fill", source => Fill(source, "red"));
        workspace.MarkSaved();

        Assert.False(workspace.IsModified);

        workspace.Commit("set fill", source => Fill(source, "blue"));

        Assert.True(workspace.IsModified);

        workspace.Undo();

        Assert.False(workspace.IsModified);

        workspace.Undo();

        Assert.True(workspace.IsModified);
    }

    [Fact]
    public void Editing_Over_Where_The_File_Was_Written_Does_Not_Read_As_Saved()
    {
        // Save, take it back, then edit: the state the file was written at has been dropped and can
        // never be reached again, so reporting the drawing as saved would let somebody close it and
        // lose the edit. The index alone cannot tell -- it lands on the same number.
        var workspace = Open();

        workspace.Commit("one", source => Fill(source, "red"));
        workspace.MarkSaved();

        Assert.False(workspace.IsModified);

        workspace.Undo();
        workspace.Commit("two", source => Fill(source, "blue"));

        Assert.Contains("fill=\"blue\"", workspace.Text);
        Assert.True(workspace.IsModified);
    }

    [Fact]
    public void A_Menu_Can_Name_What_It_Would_Take_Back()
    {
        var workspace = Open();

        Assert.Null(workspace.UndoLabel);

        workspace.Commit("move <rect>", source => Fill(source, "red"));

        Assert.Equal("move <rect>", workspace.UndoLabel);
        Assert.Null(workspace.RedoLabel);

        workspace.Undo();

        Assert.Equal("move <rect>", workspace.RedoLabel);
        Assert.Null(workspace.UndoLabel);
    }

    [Fact]
    public void The_Modified_Mark_Is_Raised_Only_When_It_Turns_Over()
    {
        var workspace = Open();
        var told = 0;

        workspace.ModifiedChanged += (_, _) => told++;

        workspace.Commit("one", source => Fill(source, "red"));
        workspace.Commit("two", source => Fill(source, "blue"));

        Assert.Equal(1, told);

        workspace.Undo();
        workspace.Undo();

        Assert.Equal(2, told);
    }

    [Fact]
    public void A_Structural_Edit_Goes_Back_And_Forward_Byte_For_Byte()
    {
        var workspace = Open();

        workspace.Commit("move <circle>", source =>
        {
            var circle = source.Document.Root!.Element(Svg + "circle")!;

            circle.Remove();
            source.Document.Root!.Element(Svg + "rect")!.AddBeforeSelf(circle);

            return null;
        });

        var moved = workspace.Text;

        Assert.NotEqual(Source, moved);
        Assert.True(workspace.Undo());
        Assert.Equal(Source, workspace.Text);
        Assert.True(workspace.Redo());
        Assert.Equal(moved, workspace.Text);
        Assert.True(workspace.Undo());
        Assert.Equal(Source, workspace.Text);
    }

    [Fact]
    public void A_Span_Edit_Lands_On_The_Same_Stack_As_A_Tree_One()
    {
        // What lets the editors that still produce spans share this history: the text is the tree's
        // own serialisation, so rewriting it and reading it back loses nothing.
        var workspace = Open();

        Assert.Null(workspace.Commit("set fill", source => Fill(source, "red")));
        Assert.Null(workspace.Commit(
            "widen",
            text => text.Replace("width='64'", "width='128'")));

        Assert.Contains("width='128'", workspace.Text);
        Assert.Contains("fill=\"red\"", workspace.Text);

        // And the tree that comes back is the text, not the tree from before it.
        Assert.Equal("128", workspace.Document.Document.Root!.Attribute("width")!.Value);

        Assert.True(workspace.Undo());
        Assert.Contains("width='64'", workspace.Text);
        Assert.Contains("fill=\"red\"", workspace.Text);

        Assert.True(workspace.Undo());
        Assert.Equal(Source, workspace.Text);
    }

    [Fact]
    public void A_Span_Edit_That_Would_Not_Read_Back_Is_Refused()
    {
        var workspace = Open();

        Assert.StartsWith(
            "This file is not well formed XML:",
            workspace.Commit("break it", text => text.Replace("</svg>", string.Empty)));

        Assert.Equal(Source, workspace.Text);
        Assert.False(workspace.IsModified);
    }

    [Fact]
    public void A_Drawing_That_Cannot_Be_Read_Is_Refused_With_A_Sentence()
    {
        Assert.Null(SvgSourceWorkspace.Open("<svg><rect></svg>", out var refusal));
        Assert.StartsWith("This file is not well formed XML:", refusal);
    }
}
