using System;
using System.Linq;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// Comparing two versions of a project node by node.
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

    private const string GroupEnd = """
          </group>
        """;

    private static ProjectDocument Read(string xml) => TestPackage.Parse(xml);

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
    public void A_Key_Is_Where_The_Node_Is_Saved_Inside_The_Project()
    {
        var document = Read(Base);

        Assert.Equal(string.Empty, ProjectChanges.KeyOf(document.Root));
        Assert.Equal("sun.svg", ProjectChanges.KeyOf(Named(document, "sun")));
        Assert.Equal("Large", ProjectChanges.KeyOf(document.Root.Children.OfType<ProjectGroup>().Single()));
        Assert.Equal("Large/badge~2.svg", ProjectChanges.KeyOf(Named(document, "badge", 1)));
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
                ("cloud.svg", ProjectChangeKind.Moved),
                ("Large", ProjectChangeKind.Settings),
                ("Large/badge.svg", ProjectChangeKind.Changed),
                ("Large/moon.svg", ProjectChangeKind.Added),
                ("sun.svg", ProjectChangeKind.Changed)
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
    public void A_Removal_Is_Listed_After_What_Is_Still_There()
    {
        var before = Read(Base);
        var after = Edited("""
              <drawing name="cloud">
                <svg xmlns="http://www.w3.org/2000/svg"
                     viewBox="0 0 24 24"><rect width="20"   height="10" /></svg>
              </drawing>

            """, string.Empty, "fill=\"#ffcc00\"", "fill=\"#ff0000\"");

        var changes = ProjectChanges.Compare(before, after);

        Assert.Equal(new[] { "sun.svg", "cloud.svg" }, changes.Select(change => change.Key));
        Assert.Equal(ProjectChangeKind.Removed, changes[1].Kind);
        Assert.Same(Named(before, "cloud"), changes[1].Before);
        Assert.Null(changes[1].After);
    }

    [Fact]
    public void Same_Named_Drawings_Are_Matched_By_The_File_Each_Is_In()
    {
        var after = Edited("<rect width=\"2\" />", "<rect width=\"7\" />");

        var change = Assert.Single(ProjectChanges.Compare(Read(Base), after));

        Assert.Equal("Large/badge~2.svg", change.Key);
        Assert.Same(Named(after, "badge", 1), change.After);
    }

    [Fact]
    public void A_Drawing_Renamed_Or_Filed_Elsewhere_Is_One_Row_Moved()
    {
        var before = Read(Base);
        var after = Read(Base
            .Replace("<drawing name=\"cloud\">", "<drawing name=\"cumulus\">", StringComparison.Ordinal)
            .Replace("<drawing name=\"sun\" x=\"0\" y=\"0\">", "<group name=\"Sky\">\n    <drawing name=\"sun\" x=\"0\" y=\"0\">", StringComparison.Ordinal)
            .Replace("""
                </svg>
              </drawing>
              <drawing name="cumulus">
            """, """
                </svg>
              </drawing>
              </group>
              <drawing name="cumulus">
            """, StringComparison.Ordinal));

        var changes = ProjectChanges.Compare(before, after);

        Assert.Equal(
            new[] { ("cumulus.svg", ProjectChangeKind.Moved), ("Sky", ProjectChangeKind.Added), ("Sky/sun.svg", ProjectChangeKind.Moved) },
            changes.Select(change => (change.Key, change.Kind)));
        Assert.Same(Named(before, "cloud"), changes[0].Before);
    }

    [Fact]
    public void With_Nothing_Before_Everything_Is_Added()
    {
        var changes = ProjectChanges.Compare(null, Read(Base));

        Assert.Equal(new[] { "cloud.svg", "Large", "Large/badge.svg", "Large/badge~2.svg", "sun.svg" },
            changes.Select(change => change.Key));
        Assert.All(changes, change => Assert.Equal(ProjectChangeKind.Added, change.Kind));
    }
}
