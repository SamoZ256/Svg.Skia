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
/// The .svgstudio format: a folder holding the project's settings, a file per drawing and a folder
/// per group.
/// </summary>
/// <remarks>
/// The contract is the first test here and most of the rest are the reasons it is hard: what this
/// reads it writes back byte for byte, drawings included, and an edit changes the one file it was
/// made to and nothing else. A project that reformatted the drawings it holds would turn every save
/// into a diff nobody asked for.
/// </remarks>
public class ProjectDocumentTests : IDisposable
{
    /// <summary>
    /// Everything a drawing can carry that writing the tree back the ordinary way would change:
    /// attributes one to a line, a path split over two, an empty element written both ways, a
    /// numeric escape, and a stylesheet indented for itself.
    /// </summary>
    private const string Badge = """
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
        """;

    /// <summary>A project's folder as somebody wrote it, which is what reading and writing must leave alone.</summary>
    private static readonly IReadOnlyDictionary<string, string> Files = new Dictionary<string, string>
    {
        ["studio.xml"] = """
            <?xml version="1.0" encoding="utf-8"?>
            <!-- kept, so an edit is proven not to reformat the file -->
            <studio namespace="Demo.Icons" skiaSharp="4">
            </studio>

            """,
        ["Badge.svg"] = Badge.Replace("\n</svg>", "\n\n  <metadata><drawing xmlns=\"https://svg.skia/studio/1.0\" class=\"Badge\" /></metadata>\n</svg>", StringComparison.Ordinal) + "\n",
        ["Large/group.xml"] = """<group namespace="Demo.Icons.Large" scale="2" />""" + "\n",
        ["Large/BadgeLarge.svg"] = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" /></svg>""" + "\n",
    };

    /// <summary>The same project in one piece, for the tests about what it holds rather than how it is written.</summary>
    private const string Project = """
        <studio namespace="Demo.Icons" skiaSharp="4">
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
            <drawing name="BadgeLarge">
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

    private static ProjectDocument Read(string xml = Project) => TestPackage.Parse(xml);

    private static ProjectDocument Folder() => ProjectDocument.Read(Files, null);

    private static ProjectDrawing First(ProjectDocument document) => document.Root.Drawings.First(one => one.Name == "Badge");

    /// <summary>Every file of <paramref name="document"/> that differs from <see cref="Files"/>, with what it now says.</summary>
    private static Dictionary<string, string?> Differences(ProjectDocument document)
    {
        var now = document.Files();

        return now.Keys.Union(Files.Keys)
            .Where(path => !now.TryGetValue(path, out var text) || !Files.TryGetValue(path, out var was) || text != was)
            .ToDictionary(path => path, path => now.TryGetValue(path, out var text) ? text : null);
    }

    [Fact]
    public void A_Project_Is_Written_Back_As_It_Was_Read()
    {
        Assert.Empty(Differences(Folder()));
    }

    [Fact]
    public void A_Drawing_Is_Handed_Out_As_It_Was_Written()
    {
        // The file's own bytes, with the settings Studio keeps beside it taken out.
        Assert.Equal(Badge, First(Folder()).Text);
        Assert.Equal("Badge", First(Folder()).Class);
    }

    [Fact]
    public void A_Drawing_Put_Back_Unedited_Leaves_The_Folder_As_It_Was()
    {
        var document = Folder();
        var drawing = First(document);

        Assert.Null(drawing.SetText(drawing.Text));
        Assert.Empty(Differences(document));
    }

    [Fact]
    public void An_Edited_Drawing_Is_The_Only_File_That_Changes()
    {
        var document = Folder();
        var drawing = First(document);

        Assert.Null(drawing.SetText(drawing.Text.Replace("#3b82f6", "#ff0000", StringComparison.Ordinal)));

        var (path, text) = Assert.Single(Differences(document));

        Assert.Equal("Badge.svg", path);
        Assert.Equal(Files["Badge.svg"].Replace("#3b82f6", "#ff0000", StringComparison.Ordinal), text);
    }

    [Fact]
    public void Text_That_Is_Not_A_Drawing_Is_Refused_And_Changes_Nothing()
    {
        var document = Folder();
        var drawing = First(document);

        Assert.NotNull(drawing.SetText("<not-a-drawing />"));
        Assert.NotNull(drawing.SetText("<svg xmlns=\"http://www.w3.org/2000/svg\">"));
        Assert.Empty(Differences(document));
    }

    [Fact]
    public void A_Setting_Is_Written_Where_It_Was_And_Nowhere_Else()
    {
        var document = Folder();

        document.Root.SkiaSharp = SkiaSharpTarget.V3;

        Assert.Equal(
            new Dictionary<string, string?> { ["studio.xml"] = Files["studio.xml"].Replace("skiaSharp=\"4\"", "skiaSharp=\"3\"", StringComparison.Ordinal) },
            Differences(document));
    }

    [Fact]
    public void A_Drawing_Moved_On_Its_Board_Changes_Only_Its_Settings_Line()
    {
        var document = Folder();

        First(document).X = 40f;
        First(document).Y = 8f;

        Assert.Equal(
            Files["Badge.svg"].Replace("class=\"Badge\" />", "class=\"Badge\" x=\"40\" y=\"8\" />", StringComparison.Ordinal),
            Assert.Single(Differences(document)).Value);
    }

    [Fact]
    public void A_Drawing_Saying_Nothing_Of_Its_Own_Is_Its_Drawing_Alone()
    {
        Assert.Equal(Files["Large/BadgeLarge.svg"], Folder().Files()["Large/BadgeLarge.svg"]);
        Assert.DoesNotContain("metadata", Folder().Files()["Large/BadgeLarge.svg"], StringComparison.Ordinal);
    }

    [Fact]
    public void A_Project_In_One_File_Is_Refused_With_A_Sentence_Saying_What_It_Is_Now()
    {
        var refusal = Assert.Throws<SvgcProjectException>(() => ProjectDocument.Read(
            new Dictionary<string, string> { ["studio.xml"] = Project },
            null));

        Assert.Contains("A drawing is an .svg file of its own", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Group_Settles_What_A_Drawing_Does_Not_Say()
    {
        var document = Folder();
        var large = document.Root.Drawings.Single(one => one.Name == "BadgeLarge");

        Assert.Equal("Demo.Icons.Large", large.EffectiveNamespace);
        Assert.Equal(2f, large.EffectiveScale);
        Assert.Equal("Demo.Icons", First(document).EffectiveNamespace);
        Assert.Null(First(document).EffectiveScale);

        // The three sizes are one setting, so the group answers for all of them.
        Assert.Equal(document.Root.Children.OfType<ProjectGroup>().Single(), large.OwnerOf("scale"));
    }

    [Fact]
    public void A_Drawing_Added_Is_A_File_Of_Its_Own_Written_As_It_Came()
    {
        var document = Folder();

        var added = document.Root.AddDrawing("Dot", Drawing);

        Assert.Equal(Drawing, added.Text);
        Assert.Equal(new Dictionary<string, string?> { ["Dot.svg"] = Drawing }, Differences(document));
    }

    [Fact]
    public void A_Stylesheet_Is_Left_As_It_Was_Written()
    {
        var document = Folder();

        var styled = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">
              <style>
            .on { fill: red }
              </style>
            </svg>
            """;

        var drawing = document.Root.AddDrawing("Styled", styled);

        // The line inside the stylesheet is what the drawing says, not where it sits: shifting it
        // would be the project editing somebody's CSS.
        Assert.Contains("\n.on { fill: red }\n", drawing.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Group_Removed_Takes_Its_Folder_With_It()
    {
        var document = Folder();

        document.Root.Remove(document.Root.Children.OfType<ProjectGroup>().Single());

        Assert.Equal(
            new Dictionary<string, string?> { ["Large/BadgeLarge.svg"] = null, ["Large/group.xml"] = null },
            Differences(document));
    }

    [Fact]
    public void A_Drawing_Moved_Into_A_Group_Moves_Its_File_Unchanged()
    {
        var document = Folder();
        var group = document.Root.Children.OfType<ProjectGroup>().Single();
        var badge = First(document);

        group.Move(badge);

        Assert.Same(group, badge.Parent);
        Assert.Equal(
            new Dictionary<string, string?> { ["Badge.svg"] = null, ["Large/Badge.svg"] = Files["Badge.svg"] },
            Differences(document));
    }

    [Fact]
    public void A_Rename_Moves_The_File_And_Keeps_What_It_Draws()
    {
        var document = Folder();

        First(document).Name = "Pill";

        Assert.Equal(
            new Dictionary<string, string?> { ["Badge.svg"] = null, ["Pill.svg"] = Files["Badge.svg"] },
            Differences(document));
    }

    [Fact]
    public void A_Name_A_File_Cannot_Carry_Is_Kept_Beside_The_Drawing()
    {
        var document = Folder();

        First(document).Name = "Badge/Large?";

        var files = document.Files();

        Assert.Contains("Badge_Large_.svg", files.Keys);
        Assert.Contains("name=\"Badge/Large?\"", files["Badge_Large_.svg"], StringComparison.Ordinal);
        Assert.Equal("Badge/Large?", ProjectDocument.Read(files, null).Root.Drawings.Single(one => one.Class == "Badge").Name);
    }

    [Fact]
    public void Drawings_Of_One_Name_Are_Kept_Apart_And_Keep_Their_Name()
    {
        var document = Folder();

        document.Root.AddDrawing("Badge", Drawing);
        document.Root.AddDrawing("badge", Drawing);

        var files = document.Files();

        // Apart without regard to case, which is one file on macOS and Windows.
        Assert.Contains("Badge~2.svg", files.Keys);
        Assert.Contains("badge~3.svg", files.Keys);
        Assert.Equal(
            new[] { "Badge", "Badge", "badge" },
            ProjectDocument.Read(files, null).Root.Drawings.Where(one => one.Name.Equals("badge", StringComparison.OrdinalIgnoreCase)).Select(one => one.Name).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void An_Empty_Group_Is_Still_A_File_So_Git_Keeps_It()
    {
        var document = ProjectDocument.Empty();

        document.Root.AddGroup("Later");

        Assert.Equal("<group />\n", document.Files()["Later/group.xml"]);
    }

    [Fact]
    public void A_Copy_Keeps_The_Drawing_It_Was_Made_From()
    {
        var document = Folder();
        var badge = First(document);

        var copy = (ProjectDrawing)document.Root.Copy(badge);

        Assert.Equal(badge.Text, copy.Text);
        Assert.NotSame(badge.Element, copy.Element);

        // A name is a label rather than an identifier, so a copy is allowed to share one.
        Assert.Equal("Badge", copy.Name);
    }

    [Fact]
    public void A_Group_Cannot_Be_Moved_Into_Itself()
    {
        var document = Folder();
        var group = document.Root.Children.OfType<ProjectGroup>().Single();

        Assert.Throws<SvgcProjectException>(() => group.Move(group));
    }

    [Fact]
    public void Windows_Line_Endings_Survive_An_Edit_File_By_File()
    {
        var crlf = Files.ToDictionary(file => file.Key, file => file.Value.Replace("\n", "\r\n", StringComparison.Ordinal));
        var document = ProjectDocument.Read(crlf, null);
        var drawing = First(document);

        Assert.Null(drawing.SetText(drawing.Text.Replace("#3b82f6", "#ff0000", StringComparison.Ordinal)));

        Assert.Equal(crlf["Badge.svg"].Replace("#3b82f6", "#ff0000", StringComparison.Ordinal), document.Files()["Badge.svg"]);
    }

    [Fact]
    public void A_Byte_Order_Mark_Survives_A_Save()
    {
        var path = Path.Combine(_directory, "icons.svgstudio");

        Write(path, Files);
        File.WriteAllText(Path.Combine(path, "Badge.svg"), Files["Badge.svg"], new System.Text.UTF8Encoding(true));

        var document = ProjectDocument.Load(path);

        First(document).Class = "Pill";
        document.Save();

        Assert.Equal(0xEF, File.ReadAllBytes(Path.Combine(path, "Badge.svg"))[0]);
    }

    [Fact]
    public void A_Save_Writes_Only_The_Files_That_Changed()
    {
        var path = Path.Combine(_directory, "icons.svgstudio");
        var long_ago = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Write(path, Files);

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, long_ago);
        }

        var document = ProjectDocument.Load(path);

        First(document).Class = "Pill";
        document.Save();

        Assert.Equal(
            new[] { "Badge.svg" },
            Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Where(file => File.GetLastWriteTimeUtc(file) != long_ago)
                .Select(file => Path.GetRelativePath(path, file).Replace(Path.DirectorySeparatorChar, '/')));
    }

    [Fact]
    public void A_Save_Leaves_Whatever_Else_The_Folder_Holds()
    {
        var path = Path.Combine(_directory, "icons.svgstudio");

        Write(path, Files);
        File.WriteAllText(Path.Combine(path, "README.md"), "notes");

        var document = ProjectDocument.Load(path);

        document.Root.Remove(document.Root.Children.OfType<ProjectGroup>().Single());
        document.Save();

        Assert.True(File.Exists(Path.Combine(path, "README.md")));
        Assert.False(Directory.Exists(Path.Combine(path, "Large")));
    }

    [Fact]
    public void A_Rename_That_Changes_Only_Case_Is_Saved_As_That_Case()
    {
        var path = Path.Combine(_directory, "icons.svgstudio");

        Write(path, Files);

        var document = ProjectDocument.Load(path);

        First(document).Name = "badge";
        document.Save();

        Assert.Equal(
            new[] { "badge.svg", "Large", "studio.xml" },
            Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_Folder_That_Is_Not_A_Project_Is_Not_Saved_Into()
    {
        var path = Path.Combine(_directory, "somebody.svgstudio");

        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "keep.txt"), "mine");

        Assert.Throws<SvgcProjectException>(() => Folder().Save(path));
        Assert.Equal(new[] { "keep.txt" }, Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("<studio><thing name=\"x\" /></studio>", "not allowed")]
    [InlineData("<studio><drawing name=\"x\" wrong=\"1\"><svg /></drawing></studio>", "not a <drawing> setting")]
    [InlineData("<studio><drawing><svg /></drawing></studio>", "missing a name")]
    [InlineData("<studio><drawing name=\"x\" /></studio>", "must hold one <svg>")]
    [InlineData("<studio><drawing name=\"x\"><svg /><svg /></drawing></studio>", "must hold one <svg>")]
    [InlineData("<studio><group><drawing name=\"x\"><svg /></drawing></group></studio>", "missing a name")]
    [InlineData("<svgc />", "must be <studio>")]
    [InlineData("<studio scale=\"nope\" />", "scale")]
    [InlineData("<studio x=\"0\" />", "not a <studio> setting")]
    [InlineData("<studio><drawing name=\"x\" x=\"8\"><svg /></drawing></studio>", "needs both, or neither")]
    [InlineData("<studio><drawing name=\"x\" x=\"left\" y=\"0\"><svg /></drawing></studio>", "is not a position")]
    public void A_Project_That_Says_Something_Unreadable_Is_Refused(string xml, string says)
    {
        var refusal = Assert.ThrowsAny<Exception>(() => TestPackage.Parse(xml));

        Assert.Contains(says, refusal.Message, StringComparison.Ordinal);
    }

    private static void Write(string path, IReadOnlyDictionary<string, string> files)
    {
        foreach (var (relative, text) in files)
        {
            var file = Path.Combine(path, relative.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text, new System.Text.UTF8Encoding(false));
        }
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
        var document = TestPackage.Parse(Placed);
        var group = document.Root.Children.OfType<ProjectGroup>().Single();
        var alt = group.Drawings.Single(one => one.Name == "BadgeAlt");

        Assert.Equal(120f, group.X);
        Assert.Equal(-40f, group.Y);
        Assert.True(group.HasPosition);
        Assert.Equal(60f, alt.X);

        // Relative to the board it sits on, so moving the group is one attribute.
        group.X = 130f;

        Assert.Equal(TestPackage.Normal(Placed.Replace("x=\"120\"", "x=\"130\"", StringComparison.Ordinal)), TestPackage.Dump(document));
    }

    [Fact]
    public void A_Place_Is_Nobody_Elses_To_Inherit()
    {
        var document = TestPackage.Parse(Placed);
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
        var document = TestPackage.Parse(Placed);
        var group = document.Root.Children.OfType<ProjectGroup>().Single();
        var badge = document.Root.Drawings.First();

        // Into another group. Where a row is dropped in the tree says which group holds it and
        // nothing about where it should sit, so the numbers are read again for the board it has
        // arrived on: the group sits at 120, -40, so a row at 0, 0 on the project's board is at
        // -120, 40 on the group's, which is the same spot.
        group.Move(badge);

        Assert.Equal(-120f, badge.X);
        Assert.Equal(40f, badge.Y);

        // A reorder within one board keeps it untouched: document order decides nothing about where
        // a row is drawn any more, so there is nothing to rewrite.
        var alt = group.Drawings.Single(drawing => drawing.Name == "BadgeAlt");
        var was = alt.X;

        group.Move(alt);

        Assert.Equal(was, alt.X);
    }

    [Fact]
    public void A_Copy_Forgets_The_Place_It_Was_Made_From()
    {
        var document = TestPackage.Parse(Placed);
        var group = document.Root.Children.OfType<ProjectGroup>().Single();
        var alt = group.Drawings.Single(drawing => drawing.Name == "BadgeAlt");

        // Not the same story as a move, though the old rule ran them together. A move leaves one row
        // where somebody could already see it; a copy rewritten for the same board would land exactly
        // on top of its original, and the canvas hands a press to whichever was drawn last -- so the
        // original could no longer be picked, or dragged out from under it.
        var copy = (ProjectDrawing)group.Copy(alt);

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
        var document = TestPackage.Parse(Nested);
        var inner = Group(document, "Inner");
        var loose = document.Root.Children.OfType<ProjectDrawing>().Single(drawing => drawing.Name == "Loose");

        inner.Move(loose);

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
        var document = TestPackage.Parse(Nested);
        var inner = Group(document, "Inner");
        var other = Group(document, "Other");
        var deep = inner.Drawings.Single();

        other.Move(inner);

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
        var document = TestPackage.Parse(Nested);
        var right = Group(document, "Right");
        var a = Group(document, "Left").Drawings.Single();

        right.Move(a);

        Assert.Equal(-80f, a.X);
        Assert.Equal(0f, a.Y);
    }

    [Fact]
    public void A_Row_With_No_Place_Arrives_Without_One()
    {
        var document = TestPackage.Parse(Nested);
        var nowhere = document.Root.Children.OfType<ProjectDrawing>().Single(drawing => drawing.Name == "Nowhere");

        Group(document, "Other").Move(nowhere);

        Assert.False(nowhere.HasPosition);
    }

    [Fact]
    public void A_Place_Is_Forgotten_When_The_Board_It_Arrives_On_Is_A_Grid()
    {
        var document = TestPackage.Parse(Nested);
        var loose = document.Root.Children.OfType<ProjectDrawing>().Single(drawing => drawing.Name == "Loose");

        // Rows sit on this board and none of them names a place, so the board is a grid and the grid
        // begins at its own corner. A row arriving with a place would be the first placed row there
        // and would push the rest out to the right of it -- a larger jump than the one this avoids,
        // and on rows nobody dragged.
        Group(document, "Grid").Move(loose);

        Assert.False(loose.HasPosition);
    }

    [Fact]
    public void A_Board_With_Nothing_On_It_Is_Not_A_Grid()
    {
        var document = TestPackage.Parse(Nested);
        var loose = document.Root.Children.OfType<ProjectDrawing>().Single(drawing => drawing.Name == "Loose");

        // Nothing to push: a group holding no rows has no queue for an arriving one to displace.
        Group(document, "Empty").Move(loose);

        Assert.Equal(-290f, loose.X);
        Assert.Equal(10f, loose.Y);
    }

    [Fact]
    public void A_Place_Is_No_Business_Of_The_Build()
    {
        var placed = TestPackage.Parse(Placed).Flatten();
        var plain = TestPackage.Parse(
            Placed.Replace(" x=\"0\" y=\"0\"", string.Empty, StringComparison.Ordinal)
                .Replace(" x=\"120\" y=\"-40\"", string.Empty, StringComparison.Ordinal)
                .Replace(" x=\"60\" y=\"8\"", string.Empty, StringComparison.Ordinal)).Flatten();

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
        Assert.Equal(2, project.Items.Count);

        // Where it goes is not the project's to say: an export lays the file it was asked for over
        // these settings.
        Assert.Null(project.SingleFile);
        Assert.All(project.Items, item => Assert.Null(item.Output));

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
        Assert.Equal(TestPackage.Normal(Declared), TestPackage.Dump(Read(Declared)));
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
        var twice = new Dictionary<string, string>
        {
            ["studio.xml"] = "<studio xmlns:e=\"https://svg.skia/expr/1.0\">\n  <e:code />\n  <e:code />\n</studio>\n"
        };

        var refusal = Assert.Throws<SvgcProjectException>(() => ProjectDocument.Read(twice, null));

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
        Assert.DoesNotContain("e:code", TestPackage.Dump(document)[TestPackage.Dump(document).IndexOf("<group", StringComparison.Ordinal)..], StringComparison.Ordinal);
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
        var written = TestPackage.Dump(document);

        Assert.True(
            written.IndexOf("e:code", StringComparison.Ordinal)
            < written.IndexOf("<drawing name=\"BadgeLarge\"", StringComparison.Ordinal));
    }

    /// <summary>A project declaring three, and a drawing naming one of them.</summary>
    private const string Plenty = """
        <?xml version="1.0" encoding="utf-8"?>
        <studio namespace="Demo.Icons">
          <e:code xmlns:e="https://svg.skia/expr/1.0">
            <e:param name="tint" type="color" default="#00ff00" />
            <e:param name="shade" type="color" default="#0000ff" />
            <e:param name="ring" type="number" default="2" min="0" max="10" />
            <e:let name="edge">mix(tint, shade, 0.5)</e:let>
          </e:code>
          <drawing name="Badge">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
              <circle cx="12" cy="12" r="10" fill="{{ tint }}" />
            </svg>
          </drawing>
        </studio>

        """;

    [Fact]
    public void A_Drawing_Inherits_Only_What_It_Reaches()
    {
        var document = Read(Plenty);
        var built = ProjectDeclarations.Built(First(document), First(document).Text);

        var declarations = SvgExpressionDeclarations.Parse(built, out var diagnostics);

        Assert.Empty(diagnostics);

        // One of three, and not the let: an inherited parameter the drawing never names would
        // otherwise become an argument of its generated Draw that drives nothing.
        Assert.Equal(new[] { "tint" }, declarations.Parameters.Select(one => one.Name).ToArray());
        Assert.Empty(declarations.Lets);
    }

    [Fact]
    public void An_Inherited_Let_Brings_What_It_Reads_With_It()
    {
        var document = Read(Plenty.Replace("fill=\"{{ tint }}\"", "fill=\"{{ edge }}\"", StringComparison.Ordinal));

        var declarations = SvgExpressionDeclarations.Parse(
            ProjectDeclarations.Built(First(document), First(document).Text),
            out _);

        // The let is reached, so what its body names is reached too — however deep it goes.
        Assert.Equal(new[] { "tint", "shade" }, declarations.Parameters.Select(one => one.Name).ToArray());
        Assert.Equal(new[] { "edge" }, declarations.Lets.Select(one => one.Name).ToArray());
    }

    [Fact]
    public void A_Drawings_Own_Let_Reaches_Through_To_What_It_Inherits()
    {
        var document = Read(Plenty.Replace(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">",
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:e=\"https://svg.skia/expr/1.0\" viewBox=\"0 0 24 24\">"
            + "<defs><e:code><e:let name=\"own\">mix(shade, shade, 0.5)</e:let></e:code></defs>",
            StringComparison.Ordinal));

        var declarations = SvgExpressionDeclarations.Parse(
            ProjectDeclarations.Built(First(document), First(document).Text),
            out _);

        // The drawing's own block is spliced whole, so what its bodies read is live.
        Assert.Contains("shade", declarations.Parameters.Select(one => one.Name));
    }

    [Fact]
    public void A_Name_Declared_Twice_Collides_Even_Where_Nothing_Uses_It()
    {
        // Narrowing must not turn the refusal into shadowing for the names nothing reads: the
        // drawing's own declared names seed the reached set for exactly this.
        var document = Read(Plenty.Replace(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">",
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:e=\"https://svg.skia/expr/1.0\" viewBox=\"0 0 24 24\">"
            + "<defs><e:code><e:param name=\"shade\" type=\"color\" default=\"#ff0000\" /></e:code></defs>",
            StringComparison.Ordinal));

        SvgExpressionDeclarations.Parse(
            ProjectDeclarations.Built(First(document), First(document).Text),
            out var diagnostics);

        Assert.Contains(diagnostics, one => one.Message.Contains("declared more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void An_Expression_That_Will_Not_Read_Keeps_The_Whole_Chain()
    {
        // Dropping a declaration that is in fact used breaks the drawing, so the answer when what
        // is reached cannot be told is everything.
        var document = Read(Plenty.Replace("{{ tint }}", "{{ tint ~ }}", StringComparison.Ordinal));

        var declarations = SvgExpressionDeclarations.Parse(
            ProjectDeclarations.Built(First(document), First(document).Text),
            out _);

        Assert.Equal(
            new[] { "tint", "shade", "ring" },
            declarations.Parameters.Select(one => one.Name).ToArray());
    }

    [Fact]
    public void A_Build_Is_Handed_Only_What_The_Drawing_Reaches()
    {
        var item = Read(Plenty).Flatten().Items.Single();

        Assert.Contains("<e:param name=\"tint\"", item.Source!, StringComparison.Ordinal);
        Assert.DoesNotContain("shade", item.Source!, StringComparison.Ordinal);
    }

    /// <summary>Two desks, each with two drawings, declaring copies of one library.</summary>
    /// <remarks>
    /// `tint` is reached in both desks, `ring` only in the first, `lean` by one drawing, and `edge`
    /// is a let two drawings of the first desk share.
    /// </remarks>
    private static string Copies() => """
        <?xml version="1.0" encoding="utf-8"?>
        <studio namespace="Demo.Icons">
          <group name="One">
            <drawing name="a">
              <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24">
                <defs><e:code><e:param name="tint" type="color" default="#00ff00" /><e:param name="ring" type="number" default="2" /><e:let name="edge">mix(tint, tint, 0.5)</e:let></e:code></defs>
                <circle cx="12" cy="12" r="10" fill="{{ edge }}" stroke-width="{{ ring }}" />
              </svg>
            </drawing>
            <drawing name="b">
              <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24">
                <defs><e:code><e:param name="tint" type="color" default="#00ff00" /><e:param name="ring" type="number" default="2" /><e:let name="edge">mix(tint, tint, 0.5)</e:let></e:code></defs>
                <rect width="24" height="24" fill="{{ edge }}" stroke-width="{{ ring }}" />
              </svg>
            </drawing>
          </group>
          <group name="Two">
            <drawing name="c">
              <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24">
                <defs><e:code><e:param name="tint" type="color" default="#00ff00" /><e:param name="lean" type="number" default="1" /></e:code></defs>
                <path d="M12 3 L21 21 Z" fill="{{ tint }}" stroke-width="{{ lean }}" />
              </svg>
            </drawing>
          </group>
        </studio>

        """;

    private static ProjectGroup Named(ProjectDocument document, string name)
        => document.Root.Children.OfType<ProjectGroup>().Single(group => group.Name == name);

    [Fact]
    public void A_Name_Is_Placed_Where_It_Is_Shared()
    {
        var document = Read(Copies());

        ProjectPlacement.Place(document.Root, organize: true);

        // Both desks reach it, so it belongs to the project.
        Assert.Contains("tint", document.Root.CodeText, StringComparison.Ordinal);

        // Only the first desk's drawings do, so it belongs to that desk — and so does the let they
        // share.
        Assert.Contains("ring", Named(document, "One").CodeText, StringComparison.Ordinal);
        Assert.Contains("edge", Named(document, "One").CodeText, StringComparison.Ordinal);

        // One drawing alone holds it, and that is already where it is shared.
        Assert.Null(Named(document, "Two").Code);
        Assert.Contains("lean", Named(document, "Two").Drawings.Single().Text, StringComparison.Ordinal);

        // What moved is gone from the drawings that held it.
        foreach (var drawing in Named(document, "One").Drawings)
        {
            Assert.DoesNotContain("e:param", drawing.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("e:let", drawing.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("<defs", drawing.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Placement_Leaves_Every_Drawing_Built_As_It_Was()
    {
        var document = Read(Copies());

        var before = document.Root.Drawings
            .ToDictionary(
                drawing => drawing.Name,
                drawing => SvgExpressionDeclarations.Parse(ProjectDeclarations.Built(drawing, drawing.Text)));

        ProjectPlacement.Place(document.Root, organize: true);

        foreach (var drawing in document.Root.Drawings)
        {
            var after = SvgExpressionDeclarations.Parse(ProjectDeclarations.Built(drawing, drawing.Text), out var bad);

            Assert.Empty(bad);

            // The whole point: moving a declaration up changes where it is written and nothing about
            // what any drawing is built with.
            Assert.Equal(
                before[drawing.Name].Parameters.Select(one => one.Name).OrderBy(one => one, StringComparer.Ordinal),
                after.Parameters.Select(one => one.Name).OrderBy(one => one, StringComparer.Ordinal));

            Assert.Equal(
                before[drawing.Name].Lets.Select(one => one.Name),
                after.Lets.Select(one => one.Name));
        }
    }

    [Fact]
    public void Everything_Goes_To_The_Project_When_Nothing_Organizes_It()
    {
        var document = Read(Copies());

        ProjectPlacement.Place(document.Root, organize: false);

        foreach (var name in new[] { "tint", "ring", "lean", "edge" })
        {
            Assert.Contains(name, document.Root.CodeText, StringComparison.Ordinal);
        }

        Assert.All(
            document.Root.Children.OfType<ProjectGroup>(),
            group => Assert.Null(group.Code));

        Assert.All(
            document.Root.Drawings,
            drawing => Assert.DoesNotContain("e:code", drawing.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void A_Placed_Block_Is_Written_At_The_Depth_It_Sits_At()
    {
        var document = Read(Copies());

        ProjectPlacement.Place(document.Root, organize: true);

        // A project's files are something somebody reads and diffs, so a block put into one is
        // written the way the rest of the file is rather than the way XLinq would indent it.
        Assert.Contains("""
              <e:code xmlns:e="https://svg.skia/expr/1.0">
                <e:param name="ring" type="number" default="2" />
                <e:let name="edge">mix(tint, tint, 0.5)</e:let>
              </e:code>
            """, document.Files()["One/group.xml"], StringComparison.Ordinal);
    }

    [Fact]
    public void A_Name_Two_Drawings_Mean_Differently_Is_Left_Alone()
    {
        // Hoisting either would silently change what the other draws.
        var document = Read("""
            <?xml version="1.0" encoding="utf-8"?>
            <studio namespace="Demo.Icons">
              <group name="One">
                <drawing name="a">
                  <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24">
                    <defs><e:code><e:param name="tint" type="color" default="#00ff00" /><e:param name="ring" type="number" default="2" /></e:code></defs>
                    <circle cx="12" cy="12" r="10" fill="{{ tint }}" stroke-width="{{ ring }}" />
                  </svg>
                </drawing>
                <drawing name="b">
                  <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24">
                    <defs><e:code><e:param name="tint" type="color" default="#00ff00" /><e:param name="ring" type="number" default="9" /></e:code></defs>
                    <rect width="24" height="24" fill="{{ tint }}" stroke-width="{{ ring }}" />
                  </svg>
                </drawing>
              </group>
            </studio>

            """);

        ProjectPlacement.Place(document.Root, organize: true);

        var group = Named(document, "One");

        // The one they agree about moves; the one they do not stays with each of them.
        Assert.Contains("tint", group.CodeText, StringComparison.Ordinal);
        Assert.DoesNotContain("ring", group.CodeText, StringComparison.Ordinal);

        Assert.All(group.Drawings, drawing => Assert.Contains("ring", drawing.Text, StringComparison.Ordinal));
        Assert.All(group.Drawings, drawing => Assert.DoesNotContain("tint", drawing.Text.Split('>')[0], StringComparison.Ordinal));
    }

    [Fact]
    public void A_Copied_Group_Declares_What_It_Declared()
    {
        var document = Read(Declared);
        var group = (ProjectGroup)document.Root.Children.Single();

        var copy = (ProjectGroup)document.Root.Copy(group);

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
        var document = ProjectDocument.Empty();
        var group = document.Root.AddGroup("Large");

        Assert.Null(group.SetCode(
            "<e:code xmlns:e=\"https://svg.skia/expr/1.0\"><e:param name=\"tint\" type=\"color\" default=\"#00ff00\" /></e:code>"));

        // Its own file, and in it only what it declares: the folder is its name.
        Assert.Equal(
            "<group>\n  <e:code xmlns:e=\"https://svg.skia/expr/1.0\"><e:param name=\"tint\" type=\"color\" default=\"#00ff00\" /></e:code>\n</group>\n",
            document.Files()["Large/group.xml"]);
    }

    [Fact]
    public void An_Empty_Project_Is_A_Project_A_Drawing_Can_Be_Added_To()
    {
        var document = ProjectDocument.Empty();

        // Not a file, and not named one: it becomes one when somebody says where it goes.
        Assert.Empty(document.Root.Children);
        Assert.Null(document.Path);

        document.Root.AddDrawing("Dot", Drawing);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["studio.xml"] = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<studio>\n</studio>\n",
                ["Dot.svg"] = Drawing
            },
            document.Files());
    }
}
