using System.IO;
using System.Linq;
using Xunit;

namespace Svg.CodeGen.Skia.Projects.UnitTests;

/// <summary>
/// The project as a tree rather than a build: what <see cref="SvgcProject"/> throws away so that a
/// generator does not have to know groups exist, and an editor has nothing else to read it from.
/// </summary>
public class SvgcProjectDocumentTests
{
    private const string Base = "/icons";

    private static SvgcProjectDocument Parse(string xml) => SvgcProjectDocument.Parse(xml, Base);

    [Fact]
    public void The_Tree_Keeps_The_Nesting_That_Flattening_Folds_Away()
    {
        var document = Parse("""
            <svgc>
              <svg input="a.svg" />
              <group namespace="Nav">
                <svg input="b.svg" />
                <group><svg input="c.svg" /></group>
              </group>
            </svgc>
            """);

        var children = document.Root.Children;

        Assert.Equal(2, children.Count);
        Assert.Equal("a.svg", Assert.IsType<SvgcProjectDrawing>(children[0]).Input);

        var nav = Assert.IsType<SvgcProjectGroup>(children[1]);
        Assert.Equal("Nav", nav.Namespace);
        Assert.Equal(2, nav.Children.Count);

        var inner = Assert.IsType<SvgcProjectGroup>(nav.Children[1]);
        Assert.Equal("c.svg", Assert.IsType<SvgcProjectDrawing>(Assert.Single(inner.Children)).Input);

        // The same drawings the build gets, in the same order.
        Assert.Equal(
            new[] { "a.svg", "b.svg", "c.svg" },
            document.Root.Drawings.Select(drawing => drawing.Input));
    }

    [Fact]
    public void An_Effective_Setting_Falls_Through_To_The_Project()
    {
        var document = Parse("""
            <svgc>
              <namespace>Icons</namespace>
              <class>Fallback</class>
              <svg input="a.svg" />
              <group namespace="Icons.Nav">
                <svg input="b.svg" class="B" />
              </group>
            </svgc>
            """);

        var drawings = document.Root.Drawings.ToArray();

        // Unlike the flattened item, which leaves this null for the project to answer later.
        Assert.Equal("Icons", drawings[0].EffectiveNamespace);
        Assert.Null(drawings[0].Namespace);

        Assert.Equal("Icons.Nav", drawings[1].EffectiveNamespace);
        Assert.Equal("B", drawings[1].EffectiveClass);
        Assert.Equal("Fallback", drawings[0].EffectiveClass);
    }

    [Fact]
    public void An_Effective_Size_Obeys_The_Trio_And_Padding_Split()
    {
        var document = Parse("""
            <svgc>
              <scale>2</scale>
              <padding>25%</padding>
              <group width="48">
                <svg input="a.svg" />
                <svg input="b.svg" padding="0" />
              </group>
              <svg input="c.svg" />
            </svgc>
            """);

        var drawings = document.Root.Drawings.ToArray();

        // The group's width replaces the project's scale outright rather than joining it.
        Assert.Equal(48f, drawings[0].EffectiveWidth);
        Assert.Null(drawings[0].EffectiveScale);

        // Padding is not one of the three, so it still reaches through the group that resized.
        Assert.Equal("25%", drawings[0].EffectivePadding);
        Assert.Equal("0", drawings[1].EffectivePadding);

        // Outside the group, so the project's own sizing answers.
        Assert.Equal(2f, drawings[2].EffectiveScale);
        Assert.Null(drawings[2].EffectiveWidth);
    }

    [Fact]
    public void Where_A_Setting_Comes_From_Can_Be_Named()
    {
        var document = Parse("""
            <svgc>
              <namespace>Icons</namespace>
              <group namespace="Icons.Nav" scale="2">
                <svg input="a.svg" />
              </group>
            </svgc>
            """);

        var drawing = document.Root.Drawings.Single();
        var group = document.Root.Children.OfType<SvgcProjectGroup>().Single();

        Assert.Same(group, drawing.OwnerOf("namespace"));
        Assert.Same(group, drawing.OwnerOf("scale"));
        Assert.Null(drawing.OwnerOf("class"));
    }

    [Fact]
    public void Paths_Are_Kept_As_Written_And_Resolved_Separately()
    {
        var document = Parse("""
            <svgc>
              <svg input="art/a.svg" output="A.cs" recipe="a.recipe" />
            </svgc>
            """);

        var drawing = document.Root.Drawings.Single();

        // Kept raw, or saving would rewrite every relative path in the file as an absolute one.
        Assert.Equal("art/a.svg", drawing.Input);
        Assert.Equal("A.cs", drawing.Output);
        Assert.Equal("a.recipe", drawing.Recipe);

        Assert.Equal(Path.Combine(Base, "art/a.svg"), drawing.ResolvedInput);
        Assert.Equal(Path.Combine(Base, "A.cs"), drawing.ResolvedOutput);
        Assert.Equal(Path.Combine(Base, "a.recipe"), drawing.ResolvedRecipe);
    }

    [Fact]
    public void Saving_An_Untouched_Project_Gives_Back_What_Was_Read()
    {
        // Comments, attribute order, blank lines and indentation are none of an edit's business.
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <!-- the icons of the thing -->
            <svgc>
              <namespace>Demo.Icons</namespace>
              <singleFile>Icons.cs</singleFile>

              <svg class="Home" input="home.svg" />

              <!-- twice the size, for the header -->
              <group namespace="Demo.Icons.Large" scale="2">
                <svg input="badge.svg" class="BadgeLarge" />
              </group>
            </svgc>
            """;

        Assert.Equal(xml, Parse(xml).ToXml());
    }

    [Fact]
    public void An_Edit_Rewrites_Only_What_It_Changed()
    {
        var document = Parse("""
            <svgc>
              <namespace>Demo.Icons</namespace>

              <!-- kept -->
              <group namespace="Nav" scale="2">
                <svg input="home.svg" class="Home" />
              </group>
            </svgc>
            """);

        document.Root.Children.OfType<SvgcProjectGroup>().Single().Scale = 3f;

        Assert.Equal("""
            <svgc>
              <namespace>Demo.Icons</namespace>

              <!-- kept -->
              <group namespace="Nav" scale="3">
                <svg input="home.svg" class="Home" />
              </group>
            </svgc>
            """, document.ToXml());
    }

    [Fact]
    public void Clearing_A_Setting_Removes_It_So_The_Value_Is_Inherited_Again()
    {
        var document = Parse("""
            <svgc>
              <scale>2</scale>
              <group scale="4"><svg input="a.svg" /></group>
            </svgc>
            """);

        var group = document.Root.Children.OfType<SvgcProjectGroup>().Single();

        group.Scale = null;

        Assert.Equal("""
            <svgc>
              <scale>2</scale>
              <group><svg input="a.svg" /></group>
            </svgc>
            """, document.ToXml());

        // And the drawing goes back to the project's sizing.
        Assert.Equal(2f, document.Root.Drawings.Single().EffectiveScale);
    }

    [Fact]
    public void A_Project_Setting_Added_Lands_Before_The_Drawings()
    {
        var document = Parse("""
            <svgc>
              <namespace>Icons</namespace>
              <svg input="a.svg" />
            </svgc>
            """);

        document.Root.SingleFile = "Icons.cs";

        // After the settings and before the build they describe, which is where the docs put it.
        Assert.Contains("<namespace>Icons</namespace>\n  <singleFile>Icons.cs</singleFile>", document.ToXml());
    }

    [Fact]
    public void A_Setting_Added_Gets_A_Line_Of_Its_Own()
    {
        var document = Parse("""
            <svgc>
              <namespace>Icons</namespace>

              <svg input="a.svg" />
            </svgc>
            """);

        document.Root.SkiaSharp = SkiaSharpTarget.V3;

        // Whitespace is a node once it is preserved, and inserting before an element lands after
        // the indentation in front of it — so this used to come out as
        // "<skiaSharp>3</skiaSharp><svg input=... />" on one line.
        Assert.Equal("""
            <svgc>
              <namespace>Icons</namespace>

              <skiaSharp>3</skiaSharp>

              <svg input="a.svg" />
            </svgc>
            """, document.ToXml());
    }

    [Fact]
    public void A_Setting_Added_To_A_Project_With_No_Drawings_Keeps_The_Closing_Tag_Its_Line()
    {
        var document = Parse("""
            <svgc>
              <namespace>Icons</namespace>
            </svgc>
            """);

        document.Root.SingleFile = "Icons.cs";

        Assert.Equal("""
            <svgc>
              <namespace>Icons</namespace>
              <singleFile>Icons.cs</singleFile>
            </svgc>
            """, document.ToXml());
    }

    [Fact]
    public void A_Drawing_Added_Takes_A_Line_Of_Its_Own()
    {
        var document = Parse("""
            <svgc>
              <svg input="a.svg" />
              <group>
                <svg input="b.svg" />
              </group>
            </svgc>
            """);

        document.Root.AddDrawing("c.svg", 1);
        document.Root.AddDrawing("d.svg", 3);

        // The same trap the settings hit: inserting before an element lands after the indentation
        // in front of it, so without handing that back the two share a line.
        Assert.Equal("""
            <svgc>
              <svg input="a.svg" />
              <svg input="c.svg" />
              <group>
                <svg input="b.svg" />
              </group>
              <svg input="d.svg" />
            </svgc>
            """, document.ToXml());

        // And in the order the build will read them.
        Assert.Equal(
            new[] { "a.svg", "c.svg", "b.svg", "d.svg" },
            document.Root.Drawings.Select(drawing => drawing.Input));
    }

    [Fact]
    public void The_First_Thing_In_A_Group_Is_Indented_One_Level_In()
    {
        var document = Parse("""
            <svgc>
              <group />
            </svgc>
            """);

        var group = document.Root.Children.OfType<SvgcProjectGroup>().Single();

        group.AddDrawing("a.svg", 0);

        // A group holding nothing has no child to copy a line from, and the whitespace in front of
        // its closing tag is its own depth — a child written on that comes out level with it.
        Assert.Equal("""
            <svgc>
              <group>
                <svg input="a.svg" />
              </group>
            </svgc>
            """, document.ToXml());
    }

    [Fact]
    public void Removing_A_Node_Takes_Its_Line_With_It()
    {
        var document = Parse("""
            <svgc>
              <svg input="a.svg" />

              <!-- kept -->
              <group>
                <svg input="b.svg" />
              </group>
            </svgc>
            """);

        document.Root.Remove(document.Root.Children.OfType<SvgcProjectGroup>().Single());

        // The comment stays: it is a sibling, not part of the group, and guessing which of the two
        // it was written about is not worth deleting somebody's note over.
        Assert.Equal("""
            <svgc>
              <svg input="a.svg" />

              <!-- kept -->
            </svgc>
            """, document.ToXml());

        Assert.Equal("a.svg", document.Root.Drawings.Single().Input);
    }

    [Fact]
    public void A_Move_Reparents_What_It_Moves()
    {
        var document = Parse("""
            <svgc>
              <group namespace="A">
                <svg input="a.svg" />
              </group>
              <group namespace="B">
                <svg input="b.svg" />
              </group>
            </svgc>
            """);

        var groups = document.Root.Children.OfType<SvgcProjectGroup>().ToArray();
        var moved = groups[0].Children.OfType<SvgcProjectDrawing>().Single();

        groups[1].Move(moved, 1);

        Assert.Equal("""
            <svgc>
              <group namespace="A">
              </group>
              <group namespace="B">
                <svg input="b.svg" />
                <svg input="a.svg" />
              </group>
            </svgc>
            """, document.ToXml());

        // Reparented rather than rebuilt, so what it inherits follows it and the caller's reference
        // is still the node in the document.
        Assert.Same(groups[1], moved.Parent);
        Assert.Equal("B", moved.EffectiveNamespace);
        Assert.Equal(new[] { "b.svg", "a.svg" }, document.Flatten().Items.Select(item => Path.GetFileName(item.Input)));
    }

    [Fact]
    public void A_Move_Within_One_Group_Lands_Where_The_Caller_Pointed()
    {
        var document = Parse("""
            <svgc>
              <svg input="a.svg" />
              <svg input="b.svg" />
              <svg input="c.svg" />
            </svgc>
            """);

        var drawings = document.Root.Children.OfType<SvgcProjectDrawing>().ToArray();

        // "After b", read against the children as they are now — which is one past where it lands
        // once a.svg has left.
        document.Root.Move(drawings[0], 2);

        Assert.Equal(
            new[] { "b.svg", "a.svg", "c.svg" },
            document.Root.Drawings.Select(drawing => drawing.Input));
    }

    [Fact]
    public void A_Group_Carried_To_A_New_Depth_Takes_Its_Contents_Indentation_With_It()
    {
        var document = Parse("""
            <svgc>
              <svg input="a.svg" />

              <group namespace="Large" scale="2">
                <svg input="b.svg" />

                <group class="Huge">
                  <svg input="c.svg" />
                </group>
              </group>
            </svgc>
            """);

        var large = document.Root.Children.OfType<SvgcProjectGroup>().Single();
        var huge = large.Children.OfType<SvgcProjectGroup>().Single();

        // Out to the top level. The whitespace between a group's children lives inside it, so
        // without shifting it the branch arrives still written for the depth it came from.
        document.Root.Move(huge, 2);

        Assert.Equal("""
            <svgc>
              <svg input="a.svg" />

              <group namespace="Large" scale="2">
                <svg input="b.svg" />
              </group>
              <group class="Huge">
                <svg input="c.svg" />
              </group>
            </svgc>
            """, document.ToXml());

        // And back in again, a level deeper than it has just been written for.
        large.Move(huge, 1);

        Assert.Equal("""
            <svgc>
              <svg input="a.svg" />

              <group namespace="Large" scale="2">
                <svg input="b.svg" />
                <group class="Huge">
                  <svg input="c.svg" />
                </group>
              </group>
            </svgc>
            """, document.ToXml());
    }

    [Fact]
    public void Removing_The_First_Of_Several_Does_Not_Open_The_Group_With_A_Blank_Line()
    {
        var document = Parse("""
            <svgc>
              <group>
                <svg input="a.svg" />

                <svg input="b.svg" />
              </group>
            </svgc>
            """);

        var group = document.Root.Children.OfType<SvgcProjectGroup>().Single();

        group.Remove(group.Children[0]);

        // What goes is the separator behind it, not the indentation in front: that one opens the
        // group, and taking it promoted the break behind the element — blank line and all — to
        // opening the group in its place.
        Assert.Equal("""
            <svgc>
              <group>
                <svg input="b.svg" />
              </group>
            </svgc>
            """, document.ToXml());
    }

    [Fact]
    public void A_Group_Cannot_Be_Moved_Into_Itself()
    {
        var document = Parse("""
            <svgc>
              <group namespace="Outer">
                <group namespace="Inner"><svg input="a.svg" /></group>
              </group>
            </svgc>
            """);

        var outer = document.Root.Children.OfType<SvgcProjectGroup>().Single();
        var inner = outer.Children.OfType<SvgcProjectGroup>().Single();

        // Both refused: it would take the branch out of the document and leave it holding itself.
        Assert.Throws<SvgcProjectException>(() => inner.Move(outer, 0));
        Assert.Throws<SvgcProjectException>(() => outer.Move(outer, 0));
        Assert.Throws<SvgcProjectException>(() => outer.Move(document.Root, 0));
    }

    [Fact]
    public void A_Copied_Drawing_Takes_A_Line_Of_Its_Own()
    {
        var document = Parse("""
            <svgc>
              <svg input="a.svg" class="A" />
              <group namespace="Large" />
            </svgc>
            """);

        var drawing = document.Root.Children.OfType<SvgcProjectDrawing>().Single();
        var large = document.Root.Children.OfType<SvgcProjectGroup>().Single();

        var copy = large.Copy(drawing, 0);

        Assert.NotSame(drawing, copy);
        Assert.Equal("a.svg", Assert.IsType<SvgcProjectDrawing>(copy).Input);

        // The source stays where it was, which is the whole of what makes this a copy.
        Assert.Equal("""
            <svgc>
              <svg input="a.svg" class="A" />
              <group namespace="Large">
                <svg input="a.svg" class="A" />
              </group>
            </svgc>
            """, document.ToXml());
    }

    /// <summary>
    /// The copy of a branch is written for where it lands, not for where it was taken from.
    /// </summary>
    [Fact]
    public void A_Copied_Group_Takes_Its_Contents_To_The_New_Depth()
    {
        var document = Parse("""
            <svgc>
              <group namespace="Large" scale="2">
                <group class="Huge">
                  <svg input="c.svg" />
                </group>
              </group>
            </svgc>
            """);

        var large = document.Root.Children.OfType<SvgcProjectGroup>().Single();
        var huge = large.Children.OfType<SvgcProjectGroup>().Single();

        var copy = Assert.IsType<SvgcProjectGroup>(document.Root.Copy(huge, 1));

        // Live, not a lump of XML: the children came back as nodes.
        Assert.Equal("c.svg", Assert.IsType<SvgcProjectDrawing>(Assert.Single(copy.Children)).Input);

        Assert.Equal("""
            <svgc>
              <group namespace="Large" scale="2">
                <group class="Huge">
                  <svg input="c.svg" />
                </group>
              </group>
              <group class="Huge">
                <svg input="c.svg" />
              </group>
            </svgc>
            """, document.ToXml());
    }

    [Fact]
    public void A_Copy_Is_Edited_Without_Touching_What_It_Came_From()
    {
        var document = Parse("""
            <svgc>
              <svg input="a.svg" class="A" output="A.cs" />
            </svgc>
            """);

        var drawing = document.Root.Children.OfType<SvgcProjectDrawing>().Single();

        var copy = Assert.IsType<SvgcProjectDrawing>(document.Root.Copy(drawing, 1));

        copy.Class = "B";
        copy.Output = "B.cs";

        Assert.Equal("A", drawing.Class);
        Assert.Equal("A.cs", drawing.Output);

        Assert.Equal("""
            <svgc>
              <svg input="a.svg" class="A" output="A.cs" />
              <svg input="a.svg" class="B" output="B.cs" />
            </svgc>
            """, document.ToXml());
    }

    /// <summary>
    /// A class and an output are copied as they were written.
    /// </summary>
    /// <remarks>
    /// Nothing in the format asks for either to be unique — a hand-written project can already
    /// repeat them — so renaming one here would be a rule this document does not otherwise have.
    /// </remarks>
    [Fact]
    public void A_Copy_Keeps_The_Class_And_Output_It_Was_Written_With()
    {
        var document = Parse("""
            <svgc>
              <svg input="a.svg" class="A" output="A.cs" />
            </svgc>
            """);

        var drawing = document.Root.Children.OfType<SvgcProjectDrawing>().Single();
        var copy = Assert.IsType<SvgcProjectDrawing>(document.Root.Copy(drawing, 1));

        Assert.Equal("A", copy.Class);
        Assert.Equal("A.cs", copy.Output);
    }

    [Fact]
    public void The_Project_Itself_Cannot_Be_Copied()
    {
        var document = Parse("<svgc>\n  <svg input=\"a.svg\" />\n</svgc>");

        Assert.Throws<SvgcProjectException>(() => document.Root.Copy(document.Root, 0));
    }

    [Fact]
    public void A_Copy_Survives_A_Save_And_A_Load()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(directory, "icons.svgcproj");

        File.WriteAllText(path, """
            <svgc>
              <group scale="2">
                <svg input="art/home.svg" class="Home" />
              </group>
            </svgc>
            """);

        var document = SvgcProjectDocument.Load(path);
        var group = document.Root.Children.OfType<SvgcProjectGroup>().Single();

        document.Root.Copy(group, 1);
        document.Save();

        var reloaded = SvgcProjectDocument.Load(path);

        Assert.Equal(
            new[] { "art/home.svg", "art/home.svg" },
            reloaded.Root.Drawings.Select(drawing => drawing.Input));

        Assert.All(reloaded.Flatten().Items, item => Assert.Equal(2f, item.Scale));

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void Crlf_Survives_A_Save()
    {
        // XmlReader normalises CRLF to LF as the spec requires, so a Windows file read and written
        // back would otherwise come out with every line changed.
        var document = Parse("<svgc>\r\n  <svg input=\"a.svg\" />\r\n</svgc>");

        Assert.Equal("\r\n", document.NewLine);
        Assert.Equal("<svgc>\r\n  <svg input=\"a.svg\" />\r\n</svgc>", document.ToXml());
    }

    [Fact]
    public void Load_And_Save_Round_Trip_A_File()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(directory, "icons.svgcproj");
        const string xml = """
            <svgc>
              <namespace>Icons</namespace>
              <group scale="2">
                <svg input="art/home.svg" class="Home" />
              </group>
            </svgc>
            """;

        File.WriteAllText(path, xml);

        var document = SvgcProjectDocument.Load(path);

        Assert.Equal(directory, document.BaseDirectory);
        Assert.Equal(Path.Combine(directory, "art/home.svg"), document.Root.Drawings.Single().ResolvedInput);

        document.Root.Namespace = "Icons.Renamed";
        document.Save();

        var reloaded = SvgcProjectDocument.Load(path);

        Assert.Equal("Icons.Renamed", reloaded.Root.Namespace);
        Assert.Equal(2f, reloaded.Flatten().Items.Single().Scale);

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void Flattening_Is_What_The_Generator_Already_Reads()
    {
        const string xml = """
            <svgc>
              <namespace>Icons</namespace>
              <group namespace="Nav" scale="2">
                <svg input="home.svg" class="Home" />
              </group>
            </svgc>
            """;

        var flattened = Parse(xml).Flatten();
        var parsed = SvgcProject.Parse(xml, Base);

        Assert.Equal(parsed.Namespace, flattened.Namespace);
        Assert.Equal(parsed.Items.Single().Namespace, flattened.Items.Single().Namespace);
        Assert.Equal(parsed.Items.Single().Scale, flattened.Items.Single().Scale);
        Assert.Equal(parsed.Items.Single().Input, flattened.Items.Single().Input);
    }
}
