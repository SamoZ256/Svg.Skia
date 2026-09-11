using System.Collections.Generic;
using System.Linq;
using Svg.Expressions;
using Svg.SourceEditing;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// Declaring parameters and lets into the tree.
/// </summary>
/// <remarks>
/// Two things are asserted throughout, and the second is the point of editing this way: that the
/// document reads the declaration back, and that everything else in it is unchanged — the comments,
/// the attribute order, the indentation somebody chose. Nothing here asserts on the wording of a
/// refusal that comes from the language's own rules, which has one place to change.
///
/// The sources are written with EXPR-NS standing in for the namespace, because a drawing holding
/// <c>{{ … }}</c> and an interpolated string holding <c>{ … }</c> cannot both have the braces.
/// </remarks>
public class SvgDeclarationEditorDocumentTests
{
    private static string Svg(string svgText) => svgText.Replace("EXPR-NS", SvgExpressionDeclarations.Namespace);

    private static readonly string Block = Svg("""
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS" width="10" height="10">
          <defs>
            <e:code>
              <e:param name="hue" type="number" default="217" />
              <e:let name="tint">hue / 2</e:let>
            </e:code>
          </defs>
          <rect fill="{{ tint }}" />
        </svg>
        """);

    private static SvgSourceDocument Read(string svgText)
    {
        var source = SvgSourceDocument.Read(svgText, out var refusal);

        Assert.NotNull(source);
        Assert.Null(refusal);

        return source!;
    }

    private static SvgExpressionParameter Number(string name = "radius", string? @default = "40")
        => new(name, ExprType.Number, @default);

    private static SvgExpressionParameter Declared(string svgText, string name)
        => Assert.Single(SvgExpressionDeclarations.Parse(svgText, out _).Parameters, p => p.Name == name);

    [Fact]
    public void A_Parameter_Joins_The_Parameters_A_Drawing_Already_Has()
    {
        var source = Read(Block);

        Assert.Null(SvgDeclarationEditor.Add(source, Number()));

        var edited = source.ToText();

        Assert.Equal("40", Declared(edited, "radius").DefaultExpression);

        // Beside the declaration above it, and above the lets rather than after them.
        Assert.Contains(
            "<e:param name=\"hue\" type=\"number\" default=\"217\" />\n"
            + "      <e:param name=\"radius\" type=\"number\" default=\"40\" />\n"
            + "      <e:let name=\"tint\">hue / 2</e:let>",
            edited);
    }

    [Fact]
    public void A_Let_Goes_Below_The_Lets_Already_There()
    {
        var source = Read(Block);

        Assert.Null(SvgDeclarationEditor.AddLet(source, "shade", "tint / 2"));

        Assert.Contains(
            "<e:let name=\"tint\">hue / 2</e:let>\n"
            + "      <e:let name=\"shade\">tint / 2</e:let>",
            source.ToText());
    }

    [Fact]
    public void Everything_The_Edit_Did_Not_Touch_Is_Left_Alone()
    {
        var svgText = Svg("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!-- the drawing this file is for -->
            <svg xmlns:e="EXPR-NS" xmlns="http://www.w3.org/2000/svg" height='10' width="10">
                <defs>
                    <e:code>
                        <e:param name="hue" type="number" default="217" />
                    </e:code>
                </defs>
                <!-- the only shape -->
                <rect x='1' fill="{{ hue }}"/>
            </svg>
            """);

        var source = Read(svgText);

        Assert.Null(SvgDeclarationEditor.Add(source, Number()));

        var edited = source.ToText();

        Assert.Contains("<!-- the drawing this file is for -->", edited);
        Assert.Contains("<!-- the only shape -->", edited);
        Assert.Contains("<rect x='1' fill=\"{{ hue }}\"/>", edited);
        Assert.Contains("height='10' width=\"10\">", edited);

        // Four spaces, because that is what this document indents with.
        Assert.Contains("\n            <e:param name=\"radius\" type=\"number\" default=\"40\" />", edited);
    }

    [Fact]
    public void A_Drawing_With_No_Block_Gets_One_Inside_A_Defs()
    {
        const string svgText = """
            <svg xmlns="http://www.w3.org/2000/svg" width="10" height="10">
              <rect />
            </svg>
            """;

        var source = Read(svgText);

        Assert.Null(SvgDeclarationEditor.Add(source, Number()));

        var edited = source.ToText();

        Assert.Equal("40", Declared(edited, "radius").DefaultExpression);
        Assert.Contains("xmlns:e=\"" + SvgExpressionDeclarations.Namespace + "\"", edited);
        Assert.Contains("<defs>", edited);
        Assert.Contains("<rect />", edited);
    }

    [Fact]
    public void A_Recipe_Holds_Its_Declarations_Directly_And_Not_In_A_Defs()
    {
        // <defs> belongs to SVG. Writing one into a recipe makes a file the recipe parser refuses,
        // and every drawing built through that recipe then silently stops following it — so this is
        // the shape the span half has always written, and the tree half has to write it too.
        var svgText = Svg("""
            <?xml version="1.0" encoding="utf-8"?>
            <recipe xmlns="EXPR-NS">
            </recipe>
            """);

        var source = Read(svgText);

        Assert.Null(SvgDeclarationEditor.Add(source, Number("hue", "217")));

        var written = source.ToText();

        Assert.DoesNotContain("defs", written);
        Assert.Equal("217", Declared(written, "hue").DefaultExpression);

        // Directly under the root, which is what the recipe reader expects to find.
        var root = SvgSourceDocument.Read(written, out _)!.Document.Root!;

        Assert.Single(root.Elements(), element => element.Name.LocalName == "code");
    }

    [Fact]
    public void A_Drawing_Written_With_Tabs_Declares_With_Tabs()
    {
        const string svgText = "<svg xmlns=\"http://www.w3.org/2000/svg\">\n\t<rect />\n</svg>";

        var source = Read(svgText);

        Assert.Null(SvgDeclarationEditor.Add(source, Number()));
        Assert.Contains("\n\t<defs>", source.ToText());
    }

    [Fact]
    public void A_Default_Is_Written_Where_It_Stands()
    {
        var source = Read(Block);

        Assert.Null(SvgDeclarationEditor.Set(source, "hue", SvgDeclarationPart.Default, "300"));

        Assert.Contains("<e:param name=\"hue\" type=\"number\" default=\"300\" />", source.ToText());
    }

    [Fact]
    public void Several_Defaults_Are_Written_At_Once()
    {
        var svgText = Block.Replace(
            "<e:param name=\"hue\" type=\"number\" default=\"217\" />",
            "<e:param name=\"hue\" type=\"number\" default=\"217\" />\n"
            + "      <e:param name=\"size\" type=\"number\" default=\"1\" />");

        var source = Read(svgText);

        Assert.Null(SvgDeclarationEditor.SetDefaults(
            source,
            new Dictionary<string, string> { ["hue"] = "300", ["size"] = "2" }));

        var edited = source.ToText();

        Assert.Equal("300", Declared(edited, "hue").DefaultExpression);
        Assert.Equal("2", Declared(edited, "size").DefaultExpression);
    }

    [Fact]
    public void Renaming_A_Parameter_Carries_Its_Uses_With_It()
    {
        var svgText = Svg("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="217" />
                  <e:let name="tint">hue / 2</e:let>
                </e:code>
              </defs>
              <rect fill="{{ hue }}" opacity="{{ hue / 360 }}" />
            </svg>
            """);

        var source = Read(svgText);

        Assert.Null(SvgDeclarationEditor.Update(source, "hue", new SvgExpressionParameter("shade", ExprType.Number, "217")));

        var edited = source.ToText();

        Assert.Contains("<e:param name=\"shade\" type=\"number\" default=\"217\" />", edited);
        Assert.Contains("<e:let name=\"tint\">shade / 2</e:let>", edited);
        Assert.Contains("fill=\"{{ shade }}\"", edited);
        Assert.Contains("opacity=\"{{ shade / 360 }}\"", edited);
        Assert.DoesNotContain("hue", edited);
    }

    [Fact]
    public void A_Parameter_Cannot_Change_Its_Type()
    {
        var source = Read(Block);

        var refusal = SvgDeclarationEditor.Update(source, "hue", new SvgExpressionParameter("hue", ExprType.Color, "#fff"));

        Assert.Contains("cannot become", refusal);
        Assert.Equal(Block, source.ToText());
    }

    [Fact]
    public void A_Let_Is_Rewritten_And_Its_Uses_Renamed()
    {
        var source = Read(Block);

        Assert.Null(SvgDeclarationEditor.UpdateLet(source, "tint", "shade", "hue / 3"));

        var edited = source.ToText();

        Assert.Contains("<e:let name=\"shade\">hue / 3</e:let>", edited);
        Assert.Contains("fill=\"{{ shade }}\"", edited);
    }

    [Fact]
    public void A_Declaration_Nothing_Names_Is_Taken_Away_With_Its_Line()
    {
        var svgText = Svg("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="217" />
                  <e:param name="size" type="number" default="1" />
                </e:code>
              </defs>
            </svg>
            """);

        var source = Read(svgText);

        Assert.Null(SvgDeclarationEditor.Remove(source, "size"));

        var edited = source.ToText();

        Assert.DoesNotContain("size", edited);
        Assert.Contains(
            "<e:param name=\"hue\" type=\"number\" default=\"217\" />\n"
            + "    </e:code>",
            edited);
    }

    [Fact]
    public void A_Declaration_Something_Still_Names_Is_Refused_And_Counted()
    {
        var source = Read(Block);

        Assert.Equal(
            "'hue' is still used once. Take that use away first, or the drawing stops rendering.",
            SvgDeclarationEditor.Remove(source, "hue"));

        Assert.Equal(Block, source.ToText());
    }

    [Fact]
    public void A_Let_Dragged_Above_What_It_Names_Is_Refused()
    {
        // Reordering is a change of meaning: a let resolves against what is declared above it. The
        // refusal comes from reading the document back, so the tree has already been changed by the
        // time it is raised -- putting it back is the workspace's job, and this asks it to.
        var svgText = Svg("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="217" />
                  <e:let name="tint">hue / 2</e:let>
                  <e:let name="shade">tint / 2</e:let>
                </e:code>
              </defs>
            </svg>
            """);

        var workspace = SvgSourceWorkspace.Open(svgText, out _)!;

        Assert.Contains("unresolved", workspace.Commit("move a let", s => SvgDeclarationEditor.MoveLet(s, "shade", 0)));
        Assert.Equal(svgText, workspace.Text);
        Assert.False(workspace.IsModified);
    }

    [Fact]
    public void A_Let_Moved_Where_It_Still_Resolves_Is_Written_There()
    {
        var svgText = Svg("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS">
              <defs>
                <e:code>
                  <e:let name="one">1</e:let>
                  <e:let name="two">2</e:let>
                </e:code>
              </defs>
            </svg>
            """);

        var source = Read(svgText);

        Assert.Null(SvgDeclarationEditor.MoveLet(source, "two", 0));

        Assert.Contains(
            "<e:let name=\"two\">2</e:let>\n"
            + "      <e:let name=\"one\">1</e:let>",
            source.ToText());
    }

    [Fact]
    public void A_Parameter_Is_Moved_Among_The_Parameters()
    {
        var svgText = Svg("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS">
              <defs>
                <e:code>
                  <e:param name="a" type="number" default="1" />
                  <e:param name="b" type="number" default="2" />
                </e:code>
              </defs>
            </svg>
            """);

        var source = Read(svgText);

        Assert.Null(SvgDeclarationEditor.MoveParameter(source, "b", 0));

        Assert.Contains(
            "<e:param name=\"b\" type=\"number\" default=\"2\" />\n"
            + "      <e:param name=\"a\" type=\"number\" default=\"1\" />",
            source.ToText());
    }

    [Fact]
    public void A_Name_The_Language_Will_Not_Take_Is_Refused()
    {
        var source = Read(Block);

        Assert.NotNull(SvgDeclarationEditor.Add(source, Number("hue")));
        Assert.Equal(Block, source.ToText());
    }

    [Fact]
    public void A_Declaration_That_Is_Not_There_Is_Refused()
    {
        var source = Read(Block);

        Assert.Equal("This drawing declares no param called 'nope'.", SvgDeclarationEditor.Remove(source, "nope"));
        Assert.Equal("This drawing declares no let called 'nope'.", SvgDeclarationEditor.RemoveLet(source, "nope"));
        Assert.Equal(Block, source.ToText());
    }

    [Fact]
    public void A_Block_That_Already_Says_Something_Wrong_Is_Fixed_First()
    {
        var svgText = Svg("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS">
              <defs>
                <e:code>
                  <e:param name="hue" />
                </e:code>
              </defs>
            </svg>
            """);

        var source = Read(svgText);

        Assert.StartsWith("Fix what the declarations already say first:", SvgDeclarationEditor.Add(source, Number()));
        Assert.Equal(svgText, source.ToText());
    }
}
