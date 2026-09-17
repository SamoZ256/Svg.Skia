using System;
using System.IO;
using System.Linq;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Projects;
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

    [Fact]
    public void An_Empty_Project_Is_A_Project_A_Drawing_Can_Be_Added_To()
    {
        var document = ProjectDocument.Empty(_directory);

        Assert.Empty(document.Root.Children);

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
