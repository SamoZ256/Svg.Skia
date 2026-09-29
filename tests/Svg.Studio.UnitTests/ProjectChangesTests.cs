using System;
using System.Linq;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// Comparing two versions of a project node by node, and merging two sides of one over their base.
/// </summary>
/// <remarks>
/// Every version here is the base with a few words replaced, so what a test changed is exactly what
/// its replacements say.
/// </remarks>
public class ProjectChangesTests
{
    private const string Base = """
        <?xml version="1.0" encoding="utf-8"?>
        <studio namespace="Demo.Icons">
          <drawing name="sun" x="0" y="0">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
              <circle cx="12" cy="12" r="10" fill="#ffcc00" />
            </svg>
          </drawing>
          <drawing name="cloud">
            <svg xmlns="http://www.w3.org/2000/svg"
                 viewBox="0 0 24 24"><rect width="20"   height="10" /></svg>
          </drawing>
          <group name="Large" scale="2">
            <drawing name="badge">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="1" /></svg>
            </drawing>
            <drawing name="badge">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="2" /></svg>
            </drawing>
          </group>
        </studio>

        """;

    private const string Moon = """
            <drawing name="moon">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><circle r="4" /></svg>
            </drawing>
          </group>
        """;

    private const string Star = """
            <drawing name="star" x="40" y="8">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M 0 0 L 4 4" /></svg>
            </drawing>
          </group>
        """;

    private const string GroupEnd = """
          </group>
        """;

    private static ProjectDocument Read(string xml) => ProjectDocument.Parse(xml);

    private static ProjectDocument Edited(params string[] replacements)
    {
        var text = Base;

        for (var i = 0; i < replacements.Length; i += 2)
        {
            Assert.Contains(replacements[i], text, StringComparison.Ordinal);
            text = text.Replace(replacements[i], replacements[i + 1], StringComparison.Ordinal);
        }

        return Read(text);
    }

    private static ProjectDrawing Named(ProjectDocument document, string name, int occurrence = 0)
        => document.Root.Drawings.Where(one => one.Name == name).ElementAt(occurrence);

    [Fact]
    public void A_Key_Is_The_Group_Path_And_The_Position_Among_Same_Named_Siblings()
    {
        var document = Read(Base);

        Assert.Equal(string.Empty, ProjectChanges.KeyOf(document.Root));
        Assert.Equal("sun#0", ProjectChanges.KeyOf(Named(document, "sun")));
        Assert.Equal("Large#0/badge#1", ProjectChanges.KeyOf(Named(document, "badge", 1)));
    }

    [Fact]
    public void An_Unchanged_Project_Has_No_Changes()
    {
        Assert.Empty(ProjectChanges.Compare(Read(Base), Read(Base)));
    }

    [Fact]
    public void Each_Kind_Of_Change_Is_Told_Apart()
    {
        var after = Edited(
            "fill=\"#ffcc00\"", "fill=\"#ff0000\"",
            "<drawing name=\"cloud\">", "<drawing name=\"cloud\" x=\"30\" y=\"0\">",
            "scale=\"2\"", "scale=\"3\"",
            "<rect width=\"1\" />", "<rect width=\"9\" />",
            GroupEnd, Moon);

        var changes = ProjectChanges.Compare(Read(Base), after);

        Assert.Equal(
            new[]
            {
                ("sun#0", ProjectChangeKind.Changed),
                ("cloud#0", ProjectChangeKind.Moved),
                ("Large#0", ProjectChangeKind.Settings),
                ("Large#0/badge#0", ProjectChangeKind.Changed),
                ("Large#0/moon#0", ProjectChangeKind.Added)
            },
            changes.Select(change => (change.Key, change.Kind)));
    }

    [Fact]
    public void A_Drawing_Redrawn_And_Moved_Is_Changed()
    {
        var after = Edited("<drawing name=\"sun\" x=\"0\" y=\"0\">", "<drawing name=\"sun\" x=\"5\" y=\"5\">",
            "r=\"10\"", "r=\"11\"");

        var change = Assert.Single(ProjectChanges.Compare(Read(Base), after));

        Assert.Equal(ProjectChangeKind.Changed, change.Kind);
    }

    [Fact]
    public void A_Drawing_That_Names_A_Class_Has_Changed_Its_Settings()
    {
        var after = Edited("<drawing name=\"sun\" x=\"0\" y=\"0\">", "<drawing name=\"sun\" x=\"0\" y=\"0\" class=\"Sun\">");

        Assert.Equal(ProjectChangeKind.Settings, Assert.Single(ProjectChanges.Compare(Read(Base), after)).Kind);
    }

    [Fact]
    public void A_Removal_Is_Listed_Where_It_Used_To_Be()
    {
        var before = Read(Base);
        var after = Edited("""
              <drawing name="cloud">
                <svg xmlns="http://www.w3.org/2000/svg"
                     viewBox="0 0 24 24"><rect width="20"   height="10" /></svg>
              </drawing>

            """, string.Empty, "fill=\"#ffcc00\"", "fill=\"#ff0000\"");

        var changes = ProjectChanges.Compare(before, after);

        Assert.Equal(new[] { "sun#0", "cloud#0" }, changes.Select(change => change.Key));
        Assert.Equal(ProjectChangeKind.Removed, changes[1].Kind);
        Assert.Same(Named(before, "cloud"), changes[1].Before);
        Assert.Null(changes[1].After);
    }

    [Fact]
    public void Same_Named_Drawings_Are_Matched_By_Position()
    {
        var after = Edited("<rect width=\"2\" />", "<rect width=\"7\" />");

        var change = Assert.Single(ProjectChanges.Compare(Read(Base), after));

        Assert.Equal("Large#0/badge#1", change.Key);
        Assert.Same(Named(after, "badge", 1), change.After);
    }

    [Fact]
    public void With_Nothing_Before_Everything_Is_Added()
    {
        var changes = ProjectChanges.Compare(null, Read(Base));

        Assert.Equal(new[] { "sun#0", "cloud#0", "Large#0", "Large#0/badge#0", "Large#0/badge#1" },
            changes.Select(change => change.Key));
        Assert.All(changes, change => Assert.Equal(ProjectChangeKind.Added, change.Kind));
    }

    [Fact]
    public void Both_Sides_Appending_To_One_Group_Keeps_Both()
    {
        var merge = ProjectMerge.Of(Read(Base), Edited(GroupEnd, Moon), Edited(GroupEnd, Star));

        Assert.Empty(merge.Conflicts);

        var merged = Read(merge.ToXml());
        var large = (ProjectGroup)merged.Root.Children[2];

        Assert.Equal(new[] { "badge", "badge", "moon", "star" }, large.Children.Select(child => child.Name));
        Assert.Equal(40f, large.Children[3].X);
        Assert.Equal(8f, large.Children[3].Y);
        Assert.Equal(Edited(GroupEnd, Moon.Replace(GroupEnd, string.Empty) + Star).ToXml(), merge.ToXml());
    }

    [Fact]
    public void A_Change_On_One_Side_Is_Taken()
    {
        var merge = ProjectMerge.Of(Read(Base), Read(Base), Edited("r=\"10\"", "r=\"3\""));

        Assert.Empty(merge.Conflicts);
        Assert.Equal(Edited("r=\"10\"", "r=\"3\"").ToXml(), merge.ToXml());
    }

    [Fact]
    public void Redrawn_On_One_Side_And_Moved_On_The_Other_Keeps_Both()
    {
        var merge = ProjectMerge.Of(
            Read(Base),
            Edited("r=\"10\"", "r=\"3\""),
            Edited("<drawing name=\"sun\" x=\"0\" y=\"0\">", "<drawing name=\"sun\" x=\"6\" y=\"7\">"));

        Assert.Empty(merge.Conflicts);

        var sun = Named(Read(merge.ToXml()), "sun");

        Assert.Contains("r=\"3\"", sun.Text, StringComparison.Ordinal);
        Assert.Equal((6f, 7f), (sun.X!.Value, sun.Y!.Value));
    }

    [Fact]
    public void A_Drawing_Changed_Differently_On_Both_Sides_Conflicts()
    {
        var theirs = Edited("r=\"10\"", "r=\"5\"");
        var merge = ProjectMerge.Of(Read(Base), Edited("r=\"10\"", "r=\"3\""), theirs);

        var conflict = Assert.Single(merge.Conflicts);

        Assert.Equal("sun#0", conflict.Key);
        Assert.False(conflict.IsDeletion);
        Assert.Contains("r=\"3\"", Named(Read(merge.ToXml()), "sun").Text, StringComparison.Ordinal);

        conflict.Choice = ProjectSide.Theirs;

        Assert.Equal(theirs.ToXml(), merge.ToXml());
    }

    [Fact]
    public void Changed_The_Same_Way_On_Both_Sides_Is_Kept()
    {
        var merge = ProjectMerge.Of(Read(Base), Edited("r=\"10\"", "r=\"3\""), Edited("r=\"10\"", "r=\"3\""));

        Assert.Empty(merge.Conflicts);
        Assert.Equal(Edited("r=\"10\"", "r=\"3\"").ToXml(), merge.ToXml());
    }

    private const string Cloud = """
          <drawing name="cloud">
            <svg xmlns="http://www.w3.org/2000/svg"
                 viewBox="0 0 24 24"><rect width="20"   height="10" /></svg>
          </drawing>

        """;

    [Fact]
    public void Deleted_By_Them_And_Left_Alone_By_Us_Is_Deleted()
    {
        var merge = ProjectMerge.Of(Read(Base), Read(Base), Edited(Cloud, string.Empty));

        Assert.Empty(merge.Conflicts);
        Assert.Equal(Edited(Cloud, string.Empty).ToXml(), merge.ToXml());
    }

    [Fact]
    public void Deleted_By_Them_And_Changed_By_Us_Conflicts()
    {
        var merge = ProjectMerge.Of(Read(Base), Edited("height=\"10\"", "height=\"12\""), Edited(Cloud, string.Empty));

        var conflict = Assert.Single(merge.Conflicts);

        Assert.True(conflict.IsDeletion);
        Assert.Null(conflict.Theirs);
        Assert.Contains("height=\"12\"", merge.ToXml(), StringComparison.Ordinal);

        conflict.Choice = ProjectSide.Theirs;

        Assert.Equal(Edited(Cloud, string.Empty).ToXml(), merge.ToXml());
    }

    [Fact]
    public void Deleted_By_Us_And_Changed_By_Them_Conflicts()
    {
        var merge = ProjectMerge.Of(Read(Base), Edited(Cloud, string.Empty), Edited("height=\"10\"", "height=\"12\""));

        var conflict = Assert.Single(merge.Conflicts);

        Assert.True(conflict.IsDeletion);
        Assert.Null(conflict.Ours);
        Assert.DoesNotContain("cloud", merge.ToXml(), StringComparison.Ordinal);

        conflict.Choice = ProjectSide.Theirs;

        var merged = Read(merge.ToXml());

        Assert.Equal(new[] { "sun", "cloud", "Large" }, merged.Root.Children.Select(child => child.Name));
        Assert.Equal(Edited("height=\"10\"", "height=\"12\"").ToXml(), merge.ToXml());
        Assert.Contains("height=\"12\"", Named(merged, "cloud").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Attributes_Are_Merged_One_At_A_Time()
    {
        var merge = ProjectMerge.Of(
            Read(Base),
            Edited("<studio namespace=\"Demo.Icons\">", "<studio namespace=\"Demo.Mine\">", "scale=\"2\"", "scale=\"3\""),
            Edited("<studio namespace=\"Demo.Icons\">", "<studio namespace=\"Demo.Icons\" class=\"Icons\">", "scale=\"2\"", "scale=\"4\""));

        var conflict = Assert.Single(merge.Conflicts);

        Assert.Equal("Large#0", conflict.Key);

        var merged = Read(merge.ToXml());

        Assert.Equal("Demo.Mine", merged.Root.Namespace);
        Assert.Equal("Icons", merged.Root.Class);
        Assert.Equal(3f, merged.Root.Children[2].Scale);

        conflict.Choice = ProjectSide.Theirs;

        Assert.Equal(4f, Read(merge.ToXml()).Root.Children[2].Scale);
    }

    [Fact]
    public void Declarations_They_Added_To_A_Group_Are_Taken()
    {
        var theirs = Edited("<group name=\"Large\" scale=\"2\">", """
            <group name="Large" scale="2">
                <e:code xmlns:e="https://svg.skia/expr/1.0">
                  <e:param name="ring" type="number" default="2" />
                </e:code>
            """);

        Assert.Equal(ProjectChangeKind.Settings, Assert.Single(ProjectChanges.Compare(Read(Base), theirs)).Kind);

        var merge = ProjectMerge.Of(Read(Base), Edited("r=\"10\"", "r=\"3\""), theirs);

        Assert.Empty(merge.Conflicts);
        Assert.Contains("name=\"ring\"", ((ProjectGroup)Read(merge.ToXml()).Root.Children[2]).CodeText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Group_They_Added_Is_Copied_Whole()
    {
        var theirs = Edited("</studio>", """
              <group name="Small" scale="0.5" x="100" y="0">
                <drawing name="dot">
                  <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 4 4"><circle r="2" /></svg>
                </drawing>
              </group>
            </studio>
            """);

        var merge = ProjectMerge.Of(Read(Base), Edited("r=\"10\"", "r=\"3\""), theirs);

        Assert.Empty(merge.Conflicts);

        var merged = Read(merge.ToXml());
        var small = Assert.IsAssignableFrom<ProjectGroup>(merged.Root.Children[3]);

        Assert.Equal(("Small", 100f, 0f), (small.Name, small.X!.Value, small.Y!.Value));
        Assert.Equal("dot", Assert.Single(small.Children).Name);
        Assert.Contains("r=\"3\"", Named(merged, "sun").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Our_Formatting_Is_Kept_Outside_What_Was_Merged()
    {
        var ours = Edited("<rect width=\"20\"   height=\"10\" />", "<rect width=\"20\"   height=\"11\" />");
        var merge = ProjectMerge.Of(Read(Base), ours, Edited("r=\"10\"", "r=\"3\"", GroupEnd, Star));

        var merged = Read(merge.ToXml());

        Assert.Equal(Named(ours, "cloud").Text, Named(merged, "cloud").Text);
        Assert.Contains("""
                <svg xmlns="http://www.w3.org/2000/svg"
                     viewBox="0 0 24 24"><rect width="20"   height="11" /></svg>
            """, merge.ToXml(), StringComparison.Ordinal);
        Assert.Equal(
            Edited("<rect width=\"20\"   height=\"10\" />", "<rect width=\"20\"   height=\"11\" />",
                "r=\"10\"", "r=\"3\"", GroupEnd, Star).ToXml(),
            merge.ToXml());
    }

    [Fact]
    public void With_No_Base_The_Same_Addition_Is_Kept_And_A_Different_One_Conflicts()
    {
        var merge = ProjectMerge.Of(null, Edited("r=\"10\"", "r=\"3\""), Read(Base));

        var conflict = Assert.Single(merge.Conflicts);

        Assert.Equal("sun#0", conflict.Key);
        Assert.Null(conflict.Base);

        conflict.Choice = ProjectSide.Theirs;

        Assert.Equal(Base, merge.ToXml());
    }

    private const string FirstBadge = """
            <drawing name="badge">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="1" /></svg>
            </drawing>

        """;

    private static string Pasted(int r) => $"""
            <drawing name="drawing">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><circle r="{r}" /></svg>
            </drawing>
          </group>
        """;

    private static string[] Widths(ProjectDocument document)
        => ((ProjectGroup)document.Root.Children[2]).Children
            .Select(child => child.Name + ":" + ((ProjectDrawing)child).Svg.Elements().Single().Attributes().Single().Value)
            .ToArray();

    [Fact]
    public void Both_Sides_Adding_A_Drawing_Of_One_Name_Keeps_Both()
    {
        var merge = ProjectMerge.Of(Read(Base), Edited(GroupEnd, Pasted(1)), Edited(GroupEnd, Pasted(2)));

        Assert.Empty(merge.Conflicts);
        Assert.Equal(new[] { "badge:1", "badge:2", "drawing:1", "drawing:2" }, Widths(Read(merge.ToXml())));
    }

    [Fact]
    public void A_Drawing_Inserted_Ahead_Of_A_Same_Named_One_Leaves_That_One_Matched()
    {
        var theirs = Edited("<group name=\"Large\" scale=\"2\">\n", "<group name=\"Large\" scale=\"2\">\n" + FirstBadge.Replace("width=\"1\"", "width=\"5\"", StringComparison.Ordinal));
        var merge = ProjectMerge.Of(Read(Base), Edited("<rect width=\"1\" />", "<rect width=\"9\" />"), theirs);

        Assert.Empty(merge.Conflicts);
        Assert.Equal(new[] { "badge:5", "badge:9", "badge:2" }, Widths(Read(merge.ToXml())));
    }

    [Fact]
    public void A_Drawing_Deleted_Ahead_Of_A_Same_Named_One_Leaves_That_One_Matched()
    {
        var merge = ProjectMerge.Of(Read(Base), Edited("<rect width=\"2\" />", "<rect width=\"7\" />"), Edited(FirstBadge, string.Empty));

        Assert.Empty(merge.Conflicts);
        Assert.Equal(new[] { "badge:7" }, Widths(Read(merge.ToXml())));

        var change = Assert.Single(ProjectChanges.Compare(Read(Base), Edited(FirstBadge, string.Empty)));
        Assert.Equal(ProjectChangeKind.Removed, change.Kind);
        Assert.Contains("width=\"1\"", ((ProjectDrawing)change.Before!).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Drawing_Moved_To_Another_Group_On_One_Side_Takes_The_Other_Sides_Edit_With_It()
    {
        var moved = Edited(Cloud, string.Empty, GroupEnd, Cloud.Replace("\n", "\n  ", StringComparison.Ordinal).TrimEnd(' ') + GroupEnd);

        foreach (var merge in new[]
                 {
                     ProjectMerge.Of(Read(Base), Edited("height=\"10\"", "height=\"12\""), moved),
                     ProjectMerge.Of(Read(Base), moved, Edited("height=\"10\"", "height=\"12\""))
                 })
        {
            Assert.Empty(merge.Conflicts);

            var merged = Read(merge.ToXml());
            var cloud = Assert.Single(merged.Root.Drawings, drawing => drawing.Name == "cloud");

            Assert.Equal(new[] { "sun", "Large" }, merged.Root.Children.Select(child => child.Name));
            Assert.Same(merged.Root.Children[1], cloud.Parent);
            Assert.Contains("height=\"12\"", cloud.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_Drawing_Renamed_On_One_Side_Takes_The_Other_Sides_Edit()
    {
        var renamed = Edited("<drawing name=\"sun\"", "<drawing name=\"sunny\"");

        foreach (var merge in new[]
                 {
                     ProjectMerge.Of(Read(Base), renamed, Edited("r=\"10\"", "r=\"5\"")),
                     ProjectMerge.Of(Read(Base), Edited("r=\"10\"", "r=\"5\""), renamed)
                 })
        {
            Assert.Empty(merge.Conflicts);

            var merged = Read(merge.ToXml());

            Assert.Equal(new[] { "sunny", "cloud", "Large" }, merged.Root.Children.Select(child => child.Name));
            Assert.Contains("r=\"5\"", Named(merged, "sunny").Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_Reorder_On_Their_Side_Is_Taken_And_Compared_As_A_Move()
    {
        var sun = Base[Base.IndexOf("  <drawing name=\"sun\"", StringComparison.Ordinal)..Base.IndexOf(Cloud, StringComparison.Ordinal)];
        var reordered = Edited(sun + Cloud, Cloud + sun);

        var change = Assert.Single(ProjectChanges.Compare(Read(Base), reordered));
        Assert.Equal(("cloud#0", ProjectChangeKind.Moved), (change.Key, change.Kind));

        var merge = ProjectMerge.Of(Read(Base), Edited("scale=\"2\"", "scale=\"3\""), reordered);

        Assert.Empty(merge.Conflicts);
        Assert.Equal(Edited(sun + Cloud, Cloud + sun, "scale=\"2\"", "scale=\"3\"").ToXml(), merge.ToXml());
    }

    [Fact]
    public void Declarations_Leaning_On_The_Roots_Prefix_Are_Taken_And_Copied()
    {
        const string Bound = "<studio namespace=\"Demo.Icons\" xmlns:e=\"https://svg.skia/expr/1.0\">";
        var theirs = Edited(
            "<studio namespace=\"Demo.Icons\">", Bound,
            "<group name=\"Large\" scale=\"2\">", """
            <group name="Large" scale="2">
                <e:code>
                  <e:param name="ring" type="number" default="2" />
                </e:code>
            """,
            "</studio>", """
              <group name="Small">
                <e:code><e:param name="dot" type="number" default="1" /></e:code>
              </group>
            </studio>
            """);

        var merge = ProjectMerge.Of(
            Edited("<studio namespace=\"Demo.Icons\">", Bound),
            Edited("<studio namespace=\"Demo.Icons\">", Bound, "r=\"10\"", "r=\"3\""),
            theirs);

        Assert.Empty(merge.Conflicts);

        var merged = Read(merge.ToXml());

        Assert.Contains("name=\"ring\"", ((ProjectGroup)merged.Root.Children[2]).CodeText, StringComparison.Ordinal);
        Assert.Contains("name=\"dot\"", ((ProjectGroup)merged.Root.Children[3]).CodeText, StringComparison.Ordinal);
    }
}
