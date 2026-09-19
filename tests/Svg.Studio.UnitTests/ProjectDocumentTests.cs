using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// The .svgstudio format: a project and the drawings it holds, in one file.
/// </summary>
/// <remarks>
/// The contract is the first test here and most of the rest are the reasons it is hard: what this
/// reads it writes back byte for byte, drawings included, and an edit changes what it was made to
/// and nothing else. A project that reformatted the drawings it holds would turn every save into a
/// diff nobody asked for.
/// </remarks>
public class ProjectDocumentTests : IDisposable
{
    /// <summary>
    /// Everything a drawing can carry that writing the tree back the ordinary way would change:
    /// attributes one to a line, a path split over two, an empty element written both ways, a
    /// numeric escape, and a stylesheet indented for itself.
    /// </summary>
    private const string Project = """
        <?xml version="1.0" encoding="utf-8"?>
        <!-- kept, so an edit is proven not to reformat the file -->
        <studio namespace="Demo.Icons" singleFile="Icons.cs">

          <drawing name="Badge" class="Badge">
            <svg xmlns="http://www.w3.org/2000/svg"
                 viewBox="0 0 24 24">
              <style>
                .on { fill: #3b82f6 }
              </style>
              <path d="M 0 0
                       L 24 24" />
              <g></g>
              <rect/>
              <text>a &#38; b</text>
            </svg>
          </drawing>

          <group name="Large" namespace="Demo.Icons.Large" scale="2">
            <drawing name="BadgeLarge" output="Large/BadgeLarge.cs">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" /></svg>
            </drawing>
          </group>

        </studio>
        """;

    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">
          <circle cx="8" cy="8" r="8" fill="#ff0000" />
        </svg>
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static ProjectDocument Read(string xml = Project) => ProjectDocument.Parse(xml, string.Empty);

    private static ProjectDrawing First(ProjectDocument document) => document.Root.Drawings.First();

    [Fact]
    public void A_Project_Is_Written_Back_As_It_Was_Read()
    {
        Assert.Equal(Project, Read().ToXml());
    }

    [Fact]
    public void A_Drawing_Is_Handed_Out_As_It_Was_Written()
    {
        var start = Project.IndexOf("<svg", StringComparison.Ordinal);
        var end = Project.IndexOf("</svg>", StringComparison.Ordinal) + "</svg>".Length;

        // The file's own bytes: a path over two lines, an empty element written both ways, an
        // escape spelled as a number, and a stylesheet indented for itself.
        Assert.Equal(Project[start..end], First(Read()).Text);
    }

    [Fact]
    public void A_Drawing_Put_Back_Unedited_Leaves_The_File_As_It_Was()
    {
        var document = Read();
        var drawing = First(document);

        Assert.Null(drawing.SetText(drawing.Text));
        Assert.Equal(Project, document.ToXml());
    }

    [Fact]
    public void An_Edited_Drawing_Is_The_Only_Thing_That_Changes()
    {
        var document = Read();
        var drawing = First(document);

        Assert.Null(drawing.SetText(drawing.Text.Replace("#3b82f6", "#ff0000", StringComparison.Ordinal)));

        var written = document.ToXml();

        Assert.Equal(Project.Replace("#3b82f6", "#ff0000", StringComparison.Ordinal), written);
    }

    [Fact]
    public void Text_That_Is_Not_A_Drawing_Is_Refused_And_Changes_Nothing()
    {
        var document = Read();
        var drawing = First(document);

        Assert.NotNull(drawing.SetText("<not-a-drawing />"));
        Assert.NotNull(drawing.SetText("<svg xmlns=\"http://www.w3.org/2000/svg\">"));
        Assert.Equal(Project, document.ToXml());
    }

    [Fact]
    public void A_Setting_Is_Written_Where_It_Was_And_Nowhere_Else()
    {
        var document = Read();

        document.Root.SingleFile = "Other.cs";

        Assert.Equal(Project.Replace("Icons.cs", "Other.cs", StringComparison.Ordinal), document.ToXml());
    }

    [Fact]
    public void A_Group_Settles_What_A_Drawing_Does_Not_Say()
    {
        var document = Read();
        var large = document.Root.Drawings.Last();

        Assert.Equal("Demo.Icons.Large", large.EffectiveNamespace);
        Assert.Equal(2f, large.EffectiveScale);
        Assert.Equal("Demo.Icons", First(document).EffectiveNamespace);
        Assert.Null(First(document).EffectiveScale);

        // The three sizes are one setting, so the group answers for all of them.
        Assert.Equal(document.Root.Children.OfType<ProjectGroup>().Single(), large.OwnerOf("scale"));
    }

    [Fact]
    public void A_Drawing_Added_Is_Indented_Once_And_Keeps_Its_Own_Lines()
    {
        var document = Read();

        document.Root.AddDrawing("Dot", Drawing, document.Root.Children.Count);

        var written = document.ToXml();

        Assert.Contains("""
              <drawing name="Dot">
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">
                  <circle cx="8" cy="8" r="8" fill="#ff0000" />
                </svg>
              </drawing>
            """.TrimEnd(), written, StringComparison.Ordinal);

        // And it reads back as the drawing it was, at the indentation it now sits at.
        Assert.Equal(
            Drawing.Replace("\n", "\n    ", StringComparison.Ordinal),
            document.Root.Drawings.Last().Text);
    }

    [Fact]
    public void A_Stylesheet_Is_Not_Indented_With_The_Drawing_Around_It()
    {
        var document = Read();

        var styled = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">
              <style>
            .on { fill: red }
              </style>
            </svg>
            """;

        var drawing = document.Root.AddDrawing("Styled", styled, 0);

        // The line inside the stylesheet is what the drawing says, not where it sits: shifting it
        // would be the project editing somebody's CSS.
        Assert.Contains("\n.on { fill: red }\n", drawing.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Drawing_Removed_Takes_Its_Line_With_It()
    {
        var document = Read();
        var group = document.Root.Children.OfType<ProjectGroup>().Single();

        document.Root.Remove(group);

        Assert.Equal(
            Project.Replace("""


              <group name="Large" namespace="Demo.Icons.Large" scale="2">
                <drawing name="BadgeLarge" output="Large/BadgeLarge.cs">
                  <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" /></svg>
                </drawing>
              </group>
            """, string.Empty, StringComparison.Ordinal),
            document.ToXml());
    }

    [Fact]
    public void A_Drawing_Moved_Into_A_Group_Arrives_At_Its_Depth()
    {
        var document = Read();
        var group = document.Root.Children.OfType<ProjectGroup>().Single();
        var badge = First(document);

        group.Move(badge, 0);

        // The drawing's own lines follow it in; the second line of a start tag written over two is
        // the tag's own bytes and stays where its author put it.
        Assert.Contains("""
                <drawing name="Badge" class="Badge">
                  <svg xmlns="http://www.w3.org/2000/svg"
            """.TrimEnd(), document.ToXml(), StringComparison.Ordinal);

        // Two levels deeper than it was written, like everything else the branch holds.
        Assert.Contains("\n        <g></g>", document.ToXml(), StringComparison.Ordinal);

        Assert.Same(group, badge.Parent);
    }

    [Fact]
    public void A_Copy_Keeps_The_Drawing_It_Was_Made_From()
    {
        var document = Read();
        var badge = First(document);

        var copy = (ProjectDrawing)document.Root.Copy(badge, document.Root.Children.Count);

        Assert.Equal(badge.Text, copy.Text);
        Assert.NotSame(badge.Element, copy.Element);

        // A name is a label rather than an identifier, so a copy is allowed to share one.
        Assert.Equal("Badge", copy.Name);
    }

    [Fact]
    public void A_Group_Cannot_Be_Moved_Into_Itself()
    {
        var document = Read();
        var group = document.Root.Children.OfType<ProjectGroup>().Single();

        Assert.Throws<SvgcProjectException>(() => group.Move(group, 0));
    }

    [Fact]
    public void Windows_Line_Endings_Survive_A_Save()
    {
        var document = ProjectDocument.Parse(Project.Replace("\n", "\r\n", StringComparison.Ordinal), string.Empty);
        var drawing = First(document);

        Assert.Null(drawing.SetText(drawing.Text.Replace("#3b82f6", "#ff0000", StringComparison.Ordinal)));

        var written = document.ToXml();

        Assert.DoesNotContain("\n\n", written.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n\n", "\r\n\r\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(Project.Replace("#3b82f6", "#ff0000", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal), written);
    }

    [Fact]
    public void A_Byte_Order_Mark_Survives_A_Save()
    {
        var path = Path.Combine(_directory, "icons.svgstudio");

        File.WriteAllText(path, Project, new System.Text.UTF8Encoding(true));

        var document = ProjectDocument.Load(path);

        document.Save();

        Assert.Equal(0xEF, File.ReadAllBytes(path)[0]);
        Assert.Equal(Project, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("<studio><thing name=\"x\" /></studio>", "not allowed")]
    [InlineData("<studio><drawing name=\"x\" wrong=\"1\"><svg /></drawing></studio>", "not a <drawing> attribute")]
    [InlineData("<studio><drawing><svg /></drawing></studio>", "missing a name")]
    [InlineData("<studio><drawing name=\"x\" /></studio>", "must hold one <svg>")]
    [InlineData("<studio><drawing name=\"x\"><svg /><svg /></drawing></studio>", "must hold one <svg>")]
    [InlineData("<studio><group><drawing name=\"x\"><svg /></drawing></group></studio>", "missing a name")]
    [InlineData("<svgc />", "must be <studio>")]
    [InlineData("<studio scale=\"nope\" />", "scale")]
    [InlineData("<studio x=\"0\" />", "not a <studio> attribute")]
    [InlineData("<studio><drawing name=\"x\" x=\"8\"><svg /></drawing></studio>", "needs both, or neither")]
    [InlineData("<studio><drawing name=\"x\" x=\"left\" y=\"0\"><svg /></drawing></studio>", "is not a position")]
    public void A_Project_That_Says_Something_Unreadable_Is_Refused(string xml, string says)
    {
        var refusal = Assert.ThrowsAny<Exception>(() => ProjectDocument.Parse(xml, string.Empty));

        Assert.Contains(says, refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>The same project as the one above, with every row given a place on its board.</summary>
    /// <remarks>
    /// Its own fixture rather than a place added to <see cref="Project"/>, which a dozen tests above
    /// compare verbatim.
    /// </remarks>
    private const string Placed = """
        <?xml version="1.0" encoding="utf-8"?>
        <studio namespace="Demo.Icons">

          <drawing name="Badge" x="0" y="0">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" /></svg>
          </drawing>

          <group name="Large" scale="2" x="120" y="-40">
            <drawing name="BadgeLarge" x="0" y="0">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" /></svg>
            </drawing>
            <drawing name="BadgeAlt" x="60" y="8">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" /></svg>
            </drawing>
          </group>

        </studio>
        """;

    [Fact]
    public void A_Place_Is_Read_And_Written_Where_It_Was()
    {
        var document = ProjectDocument.Parse(Placed, string.Empty);
        var group = document.Root.Children.OfType<ProjectGroup>().Single();
        var alt = group.Drawings.Last();

        Assert.Equal(120f, group.X);
        Assert.Equal(-40f, group.Y);
        Assert.True(group.HasPosition);
        Assert.Equal(60f, alt.X);

        // Relative to the board it sits on, so moving the group is one attribute.
        group.X = 130f;

        Assert.Equal(Placed.Replace("x=\"120\"", "x=\"130\"", StringComparison.Ordinal), document.ToXml());
    }

    [Fact]
    public void A_Place_Is_Nobody_Elses_To_Inherit()
    {
        var document = ProjectDocument.Parse(Placed, string.Empty);
        var group = document.Root.Children.OfType<ProjectGroup>().Single();
        var large = group.Drawings.First();

        // A group's place is on its parent's board, in its parent's coordinates: offered as this
        // drawing's inherited x it would be a number about somewhere else.
        Assert.Null(large.OwnerOf("x"));
        Assert.Null(large.OwnerOf("y"));

        // And a place is not a size, or a drawing would stop inheriting the scale it builds at the
        // moment somebody moved it.
        Assert.False(large.HasSize);
        Assert.Equal(2f, large.EffectiveScale);
    }

    [Fact]
    public void A_Place_Moved_To_Another_Board_Is_Rewritten_For_It()
    {
        var document = ProjectDocument.Parse(Placed, string.Empty);
        var group = document.Root.Children.OfType<ProjectGroup>().Single();
        var badge = document.Root.Drawings.First();

        // Into another group. Where a row is dropped in the tree says which group holds it and
        // nothing about where it should sit, so the numbers are read again for the board it has
        // arrived on: the group sits at 120, -40, so a row at 0, 0 on the project's board is at
        // -120, 40 on the group's, which is the same spot.
        group.Move(badge, 0);

        Assert.Equal(-120f, badge.X);
        Assert.Equal(40f, badge.Y);

        // A reorder within one board keeps it untouched: document order decides nothing about where
        // a row is drawn any more, so there is nothing to rewrite.
        var alt = group.Drawings.Single(drawing => drawing.Name == "BadgeAlt");
        var was = alt.X;

        group.Move(alt, 0);

        Assert.Equal(was, alt.X);
    }

    [Fact]
    public void A_Copy_Forgets_The_Place_It_Was_Made_From()
    {
        var document = ProjectDocument.Parse(Placed, string.Empty);
        var group = document.Root.Children.OfType<ProjectGroup>().Single();
        var alt = group.Drawings.Single(drawing => drawing.Name == "BadgeAlt");

        // Not the same story as a move, though the old rule ran them together. A move leaves one row
        // where somebody could already see it; a copy rewritten for the same board would land exactly
        // on top of its original, and the canvas hands a press to whichever was drawn last -- so the
        // original could no longer be picked, or dragged out from under it.
        var copy = (ProjectDrawing)group.Copy(alt, group.Children.Count);

        Assert.False(copy.HasPosition);
        Assert.True(alt.HasPosition);
    }

    /// <summary>Boards inside boards, beside boards that nobody has arranged.</summary>
    private const string Nested = """
        <?xml version="1.0" encoding="utf-8"?>
        <studio namespace="Demo.Icons">

          <drawing name="Loose" x="10" y="10">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" /></svg>
          </drawing>

          <drawing name="Nowhere">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" /></svg>
          </drawing>

          <group name="Outer" x="200" y="100">
            <group name="Inner" x="30" y="10">
              <drawing name="Deep" x="5" y="5">
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" /></svg>
              </drawing>
            </group>
          </group>

          <group name="Other" x="60" y="-40">
            <drawing name="Anchor" x="0" y="0">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" /></svg>
            </drawing>
          </group>

          <group name="Grid" x="400" y="0">
            <drawing name="Queued">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" /></svg>
            </drawing>
          </group>

          <group name="Empty" x="300" y="0" />

          <group name="Board">
            <group name="Left" x="10" y="0">
              <drawing name="A" x="0" y="0">
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" /></svg>
              </drawing>
            </group>
            <group name="Right" x="90" y="0">
              <drawing name="B" x="0" y="0">
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" /></svg>
              </drawing>
            </group>
          </group>

        </studio>
        """;

    /// <summary>Every group of <see cref="Nested"/>, by name.</summary>
    private static ProjectGroup Group(ProjectDocument document, string name)
        => Groups(document.Root).Single(group => group.Name == name);

    private static IEnumerable<ProjectGroup> Groups(ProjectGroup group)
        => group.Children.OfType<ProjectGroup>().SelectMany(child => new[] { child }.Concat(Groups(child)));

    [Fact]
    public void A_Place_Is_Rewritten_Down_A_Chain_Of_Boards()
    {
        // The case that was reported: a row put into a group nested inside another one. The chain is
        // summed, not just the group it lands in.
        var document = ProjectDocument.Parse(Nested, string.Empty);
        var inner = Group(document, "Inner");
        var loose = document.Root.Children.OfType<ProjectDrawing>().Single(drawing => drawing.Name == "Loose");

        inner.Move(loose, 0);

        // Outer is at 200, 100 and Inner at 30, 10, so a row at 10, 10 on the project's board is at
        // -220, -100 on Inner's -- and 200 + 30 - 220 is 10 again.
        Assert.Equal(-220f, loose.X);
        Assert.Equal(-100f, loose.Y);
    }

    [Fact]
    public void A_Group_Put_In_Another_Group_Carries_What_It_Holds()
    {
        // A group's own pair is the whole of it: its children are written against it, and laying a
        // board out is a translation, so nothing under it is touched or needs to be.
        var document = ProjectDocument.Parse(Nested, string.Empty);
        var inner = Group(document, "Inner");
        var other = Group(document, "Other");
        var deep = inner.Drawings.Single();

        other.Move(inner, 0);

        Assert.Equal(170f, inner.X);
        Assert.Equal(150f, inner.Y);

        // Other at 60, -40 plus Inner at 170, 150 plus Deep at 5, 5 is 235, 115 -- where it was,
        // by way of Outer at 200, 100 plus Inner at 30, 10 plus the same 5, 5.
        Assert.Equal(5f, deep.X);
        Assert.Equal(5f, deep.Y);
    }

    [Fact]
    public void A_Place_Is_Rewritten_Under_A_Board_Nobody_Placed()
    {
        // Two arranged boards inside a group that names no place of its own. Summing each chain all
        // the way to the project would give up here, and it does not have to: whatever the two share
        // cancels out of the difference, so the walk stops at the nearest node they have in common.
        var document = ProjectDocument.Parse(Nested, string.Empty);
        var right = Group(document, "Right");
        var a = Group(document, "Left").Drawings.Single();

        right.Move(a, 0);

        Assert.Equal(-80f, a.X);
        Assert.Equal(0f, a.Y);
    }

    [Fact]
    public void A_Row_With_No_Place_Arrives_Without_One()
    {
        var document = ProjectDocument.Parse(Nested, string.Empty);
        var nowhere = document.Root.Children.OfType<ProjectDrawing>().Single(drawing => drawing.Name == "Nowhere");

        Group(document, "Other").Move(nowhere, 0);

        Assert.False(nowhere.HasPosition);
    }

    [Fact]
    public void A_Place_Is_Forgotten_When_The_Board_It_Arrives_On_Is_A_Grid()
    {
        var document = ProjectDocument.Parse(Nested, string.Empty);
        var loose = document.Root.Children.OfType<ProjectDrawing>().Single(drawing => drawing.Name == "Loose");

        // Rows sit on this board and none of them names a place, so the board is a grid and the grid
        // begins at its own corner. A row arriving with a place would be the first placed row there
        // and would push the rest out to the right of it -- a larger jump than the one this avoids,
        // and on rows nobody dragged.
        Group(document, "Grid").Move(loose, 0);

        Assert.False(loose.HasPosition);
    }

    [Fact]
    public void A_Board_With_Nothing_On_It_Is_Not_A_Grid()
    {
        var document = ProjectDocument.Parse(Nested, string.Empty);
        var loose = document.Root.Children.OfType<ProjectDrawing>().Single(drawing => drawing.Name == "Loose");

        // Nothing to push: a group holding no rows has no queue for an arriving one to displace.
        Group(document, "Empty").Move(loose, 0);

        Assert.Equal(-290f, loose.X);
        Assert.Equal(10f, loose.Y);
    }

    [Fact]
    public void A_Place_Is_No_Business_Of_The_Build()
    {
        var placed = ProjectDocument.Parse(Placed, string.Empty).Flatten();
        var plain = ProjectDocument.Parse(
            Placed.Replace(" x=\"0\" y=\"0\"", string.Empty, StringComparison.Ordinal)
                .Replace(" x=\"120\" y=\"-40\"", string.Empty, StringComparison.Ordinal)
                .Replace(" x=\"60\" y=\"8\"", string.Empty, StringComparison.Ordinal),
            string.Empty).Flatten();

        Assert.Equal(plain.Items.Count, placed.Items.Count);

        foreach (var (one, other) in placed.Items.Zip(plain.Items))
        {
            Assert.Equal(other.Input, one.Input);
            Assert.Equal(other.Class, one.Class);
            Assert.Equal(other.Scale, one.Scale);
            Assert.Equal(other.Source, one.Source);
        }
    }

    [Fact]
    public void A_Project_Flattens_Into_The_Build_Svgc_Runs()
    {
        var project = Read().Flatten();

        Assert.Equal("Demo.Icons", project.Namespace);
        Assert.Equal("Icons.cs", project.SingleFile);
        Assert.Equal(2, project.Items.Count);

        var badge = project.Items[0];
        var large = project.Items[1];

        // Every drawing carries itself: there is no file for the build to read.
        Assert.Contains("<path d=", badge.Source ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("Badge", badge.Input);
        Assert.Equal("Badge", badge.Class);
        Assert.Null(badge.Namespace);

        Assert.Equal("Demo.Icons.Large", large.Namespace);
        Assert.Equal(2f, large.Scale);

        // Nothing above names a class for it, so it is named after itself rather than after the
        // build's default, which every drawing of a project would share.
        Assert.Equal("BadgeLarge", large.Class);
    }

    /// <summary>
    /// A project whose root and whose group each declare, and a drawing that uses both.
    /// </summary>
    private const string Declared = """
        <?xml version="1.0" encoding="utf-8"?>
        <studio namespace="Demo.Icons">
          <e:code xmlns:e="https://svg.skia/expr/1.0">
            <e:param name="tint" type="color" default="#00ff00" />
          </e:code>
          <group name="Large" scale="2">
            <e:code xmlns:e="https://svg.skia/expr/1.0">
              <e:param name="ring" type="number" default="2" min="0" max="10" />
            </e:code>
            <drawing name="Badge">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                <circle cx="12" cy="12" r="10" fill="{{ tint }}" stroke-width="{{ ring }}" />
              </svg>
            </drawing>
          </group>
        </studio>

        """;

    [Fact]
    public void A_Project_That_Declares_Is_Written_Back_As_It_Was_Read()
    {
        // The blocks are part of the file like everything else in it, and a save must not reformat
        // them any more than it reformats a drawing.
        Assert.Equal(Declared, Read(Declared).ToXml());
    }

    [Fact]
    public void A_Block_Is_Not_A_Row_Of_The_Project()
    {
        var document = Read(Declared);
        var group = (ProjectGroup)document.Root.Children.Single();

        // What a group declares is part of what the group is, like its attributes: it is not
        // something the tree shows, a tab opens, or a drag drops into.
        Assert.Single(document.Root.Children);
        Assert.Single(group.Children);

        Assert.NotNull(document.Root.Code);
        Assert.NotNull(group.Code);
    }

    [Fact]
    public void A_Group_Declares_In_One_Block()
    {
        var twice = Declared.Replace(
            "  <group name=\"Large\" scale=\"2\">",
            "  <e:code xmlns:e=\"https://svg.skia/expr/1.0\" />\n  <group name=\"Large\" scale=\"2\">",
            StringComparison.Ordinal);

        var refusal = Assert.Throws<SvgcProjectException>(() => Read(twice));

        Assert.Contains("more than one <e:code>", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Drawing_Is_Built_With_What_Its_Groups_Declare()
    {
        var document = Read(Declared);
        var built = ProjectDeclarations.Built(First(document), First(document).Text);

        var declarations = SvgExpressionDeclarations.Parse(built, out var diagnostics);

        Assert.Empty(diagnostics);

        // Outermost first, which is the order a positional call and the generated signature read in.
        Assert.Equal(new[] { "tint", "ring" }, declarations.Parameters.Select(one => one.Name).ToArray());

        // And the drawing itself is untouched: what a tab shows, edits and saves is the file.
        Assert.DoesNotContain("e:param", First(document).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Build_Hands_Over_The_Drawing_As_It_Is_Built()
    {
        var item = Read(Declared).Flatten().Items.Single();

        // Or the generated Draw would take none of the parameters that decide what it draws.
        Assert.Contains("<e:param name=\"tint\"", item.Source!, StringComparison.Ordinal);
        Assert.Contains("<e:param name=\"ring\"", item.Source!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Name_Declared_Twice_Down_The_Chain_Is_Refused()
    {
        // The rule comes from the extension rather than from the project: a drawing redeclaring what
        // its group already declares is a document declaring one name twice, which is refused.
        var clashing = Declared.Replace(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">",
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:e=\"https://svg.skia/expr/1.0\" viewBox=\"0 0 24 24\">"
            + "<defs><e:code><e:param name=\"tint\" type=\"color\" default=\"#ff0000\" /></e:code></defs>",
            StringComparison.Ordinal);

        Assert.NotEqual(Declared, clashing);

        var document = Read(clashing);

        SvgExpressionDeclarations.Parse(
            ProjectDeclarations.Built(First(document), First(document).Text),
            out var diagnostics);

        Assert.Contains(diagnostics, one => one.Message.Contains("declared more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Group_That_Declares_Nothing_Carries_No_Block()
    {
        var document = Read(Declared);
        var group = (ProjectGroup)document.Root.Children.Single();

        // An empty block is not something to keep: a group that has had its last parameter taken
        // away declares nothing, and the file should say so.
        Assert.Null(group.SetCode("<e:code xmlns:e=\"https://svg.skia/expr/1.0\" />"));

        Assert.Null(group.Code);
        Assert.DoesNotContain("e:code", document.ToXml()[document.ToXml().IndexOf("<group", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public void A_Group_That_Has_Never_Declared_Is_Somewhere_To_Declare()
    {
        var document = Read();
        var group = document.Root.Children.OfType<ProjectGroup>().Single();

        Assert.Null(group.Code);

        // Never null, because what reads this is an editor that has to be able to declare the first
        // parameter into a group that has none.
        Assert.Contains("e:code", group.CodeText, StringComparison.Ordinal);

        Assert.Null(group.SetCode(
            "<e:code xmlns:e=\"https://svg.skia/expr/1.0\"><e:param name=\"tint\" type=\"color\" default=\"#00ff00\" /></e:code>"));

        Assert.NotNull(group.Code);

        // In front of what the group holds, so it is read before the drawings that inherit it.
        var written = document.ToXml();

        Assert.True(
            written.IndexOf("e:code", StringComparison.Ordinal)
            < written.IndexOf("<drawing name=\"BadgeLarge\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Copied_Group_Declares_What_It_Declared()
    {
        var document = Read(Declared);
        var group = (ProjectGroup)document.Root.Children.Single();

        var copy = (ProjectGroup)document.Root.Copy(group, 1);

        // The block travels with the group, because it is part of what the group is: a copy that
        // dropped it would be a row whose drawings no longer build.
        Assert.NotNull(copy.Code);
        Assert.Contains("ring", copy.CodeText, StringComparison.Ordinal);

        Assert.Equal(
            new[] { "tint", "ring" },
            SvgExpressionDeclarations
                .Parse(ProjectDeclarations.Built(copy.Drawings.Single(), copy.Drawings.Single().Text))
                .Parameters.Select(one => one.Name)
                .ToArray());
    }

    [Fact]
    public void A_Group_Holding_Nothing_Is_Somewhere_To_Declare()
    {
        var document = ProjectDocument.Empty(_directory);
        var group = document.Root.AddGroup("Large", 0);

        Assert.Null(group.SetCode(
            "<e:code xmlns:e=\"https://svg.skia/expr/1.0\"><e:param name=\"tint\" type=\"color\" default=\"#00ff00\" /></e:code>"));

        Assert.Equal("""
            <?xml version="1.0" encoding="utf-8"?>
            <studio>
              <group name="Large">
                <e:code xmlns:e="https://svg.skia/expr/1.0"><e:param name="tint" type="color" default="#00ff00" /></e:code>
              </group>
            </studio>

            """, document.ToXml());
    }

    [Fact]
    public void An_Empty_Project_Is_A_Project_A_Drawing_Can_Be_Added_To()
    {
        var document = ProjectDocument.Empty(_directory);

        // Not a file, and not named one: it becomes one when somebody says where it goes.
        Assert.Empty(document.Root.Children);
        Assert.Null(document.Path);

        document.Root.AddDrawing("Dot", Drawing, 0);

        Assert.Equal("""
            <?xml version="1.0" encoding="utf-8"?>
            <studio>
              <drawing name="Dot">
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">
                  <circle cx="8" cy="8" r="8" fill="#ff0000" />
                </svg>
              </drawing>
            </studio>

            """, document.ToXml());
    }
}
