using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Svg;
using Svg.Pathing;
using Svg.Transforms;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>A command run on a parsed copy of a drawing, and what of it is written back into the file's text.</summary>
public class SvgDomEditTests
{
    private const string Declared = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
          <defs>
            <e:code>
              <e:param name="accent" type="color" default="#ff0000" />
              <e:param name="w" type="number" default="2" />
              <e:param name="a" type="number" default="30" />
              <e:param name="label" type="string" default="Hi" />
            </e:code>
          </defs>
          BODY
        </svg>
        """;

    private static string Drawing(string body) => Declared.Replace("BODY", body, StringComparison.Ordinal);

    private sealed record Outcome(string Text, string? Refusal, string? Note, IReadOnlyList<string> Landed);

    private static Outcome Run(string text, Func<SvgDomEditContext, string?> command)
    {
        var source = SvgSourceDocument.Read(text, out var unread);

        Assert.True(source is { }, unread);

        var refusal = SvgDomEdit.Apply(source!, command, out var landed, out var note);

        return new Outcome(source!.ToText(), refusal, note, landed);
    }

    private static Outcome Edit(string text, Action<SvgDocument> command) => Run(text, context =>
    {
        command(context.Document);

        return null;
    });

    private static Outcome Written(string text, Action<SvgDocument> command)
    {
        var outcome = Edit(text, command);

        Assert.Null(outcome.Refusal);

        return outcome;
    }

    private static T Get<T>(SvgDocument document, string id) where T : SvgElement => (T)document.GetElementById(id);

    // ---- what is left alone -------------------------------------------------------------------

    [Fact]
    public void A_Command_That_Changes_Nothing_Writes_Nothing()
    {
        var text = Drawing("""<rect id="r" x='1'   y="2" width="3" height="4" fill="{{ accent }}" /><!-- note -->""");

        var outcome = Written(text, _ => { });

        Assert.Equal(text, outcome.Text);
        Assert.Empty(outcome.Landed);
    }

    [Fact]
    public void A_Changed_Value_Is_Written_Over_The_Old_One_And_Nothing_Else_Moves()
    {
        var text = Drawing("""
            <!-- before -->
              <rect id="r"   x='1' y="2" width="3" height="4" fill="#00f" /><!-- after -->
            """);

        var outcome = Written(text, document => Get<SvgRectangle>(document, "r").X = 10.5f);

        Assert.Equal(text.Replace("x='1'", "x='10.5'", StringComparison.Ordinal), outcome.Text);
        Assert.Equal(new[] { "1" }, outcome.Landed);
    }

    [Fact]
    public void A_Value_Set_In_The_Style_Is_Written_There()
    {
        var outcome = Written(Drawing("""<rect id="r" width="3" height="4" fill="red" style="fill: blue; stroke: red" />"""), document => Get<SvgRectangle>(document, "r").Fill = new SvgColourServer(Color.Lime));

        Assert.Contains("""fill="red" style="fill: #00ff00; stroke: red" """, outcome.Text);
    }

    [Fact]
    public void A_Value_Taken_Away_Is_Taken_Out_Of_The_Tag()
    {
        var outcome = Written(Drawing("""<rect id="r" width="3" height="4" stroke="red" />"""), document => Get<SvgRectangle>(document, "r").Stroke = null);

        Assert.Contains("""<rect id="r" width="3" height="4" />""", outcome.Text);
    }

    // ---- expressions ----------------------------------------------------------------------------

    [Fact]
    public void A_Value_An_Expression_Sets_Is_Not_Overwritten()
    {
        var text = Drawing("""<rect id="r" width="3" height="4" fill="{{ accent }}" />""");

        var outcome = Edit(text, document => Get<SvgRectangle>(document, "r").Fill = new SvgColourServer(Color.Lime));

        Assert.Equal("'fill' on <rect> is set by {{ accent }}, which a command cannot change.", outcome.Refusal);
    }

    [Fact]
    public void A_Driven_Value_Copied_To_Another_Element_Carries_Its_Expression()
    {
        var outcome = Written(
            Drawing("""<rect id="r" width="3" height="4" fill="{{ accent }}" /><circle id="c" r="2" stroke="red" />"""),
            document => Get<SvgCircle>(document, "c").Stroke = Get<SvgRectangle>(document, "r").Fill);

        Assert.Contains("""<circle id="c" r="2" stroke="{{ accent }}" />""", outcome.Text);
    }

    [Fact]
    public void A_Driven_Value_Inherited_From_A_Group_And_Copied_Carries_Its_Expression()
    {
        var outcome = Written(
            Drawing("""<g fill="{{ accent }}"><rect id="r" width="3" height="4" /></g><circle id="c" r="2" />"""),
            document => Get<SvgCircle>(document, "c").Stroke = Get<SvgRectangle>(document, "r").Fill);

        Assert.Contains("""<circle id="c" r="2" stroke="{{ accent }}" />""", outcome.Text);
    }

    [Fact]
    public void A_Value_Worked_Out_From_An_Expression_Is_Refused()
    {
        var outcome = Edit(
            Drawing("""<rect id="r" width="3" height="4" stroke-width="{{ w }}" /><circle id="c" r="2" />"""),
            document => Get<SvgCircle>(document, "c").StrokeWidth = new SvgUnit(Get<SvgRectangle>(document, "r").StrokeWidth.Value * 2));

        Assert.Equal("'stroke-width' on <circle> would be worked out from what an expression stands in for while the drawing is edited, rather than from its value.", outcome.Refusal);
    }

    [Fact]
    public void A_Transform_Put_In_Front_Of_A_Driven_One_Is_Composed_Around_Its_Text()
    {
        var outcome = Written(
            Drawing("""<rect id="r" width="3" height="4" transform="rotate({{ a }} 5 5)" />"""),
            document => Get<SvgRectangle>(document, "r").Transforms.Insert(0, new SvgTranslate(5, 0)));

        Assert.Contains("""transform="translate(5) rotate({{ a }} 5 5)" """, outcome.Text);
    }

    [Fact]
    public void A_Transform_Put_After_A_Driven_One_Is_Composed_After_Its_Text()
    {
        var outcome = Written(
            Drawing("""<rect id="r" width="3" height="4" transform="rotate({{ a }})" />"""),
            document => Get<SvgRectangle>(document, "r").Transforms.Add(new SvgScale(-1, 1)));

        Assert.Contains("""transform="rotate({{ a }}) scale(-1 1)" """, outcome.Text);
    }

    [Fact]
    public void A_Term_Put_After_A_Written_Transform_Keeps_How_It_Was_Written()
    {
        var outcome = Written(
            Drawing("""<rect id="r" width="3" height="4" transform="translate(20,0)" />"""),
            document => Get<SvgRectangle>(document, "r").Transforms.Add(new SvgScale(2)));

        Assert.Contains("""transform="translate(20,0) scale(2)" """, outcome.Text);
    }

    [Fact]
    public void A_Transform_That_Read_As_Nothing_Is_Written_Afresh()
    {
        var outcome = Written(
            Drawing("""<rect id="r" width="3" height="4" transform="qwe" />"""),
            document => Get<SvgRectangle>(document, "r").Transforms.Add(new SvgScale(2)));

        Assert.Contains("""transform="scale(2)" """, outcome.Text);
    }

    [Fact]
    public void A_Driven_Transform_Replaced_Is_Refused()
    {
        var outcome = Edit(
            Drawing("""<rect id="r" width="3" height="4" transform="rotate({{ a }})" />"""),
            document =>
            {
                var transforms = Get<SvgRectangle>(document, "r").Transforms;

                transforms.Clear();
                transforms.Add(new SvgTranslate(1, 1));
            });

        Assert.Equal("'transform' on <rect> is set by rotate({{ a }}), which a command cannot change.", outcome.Refusal);
    }

    [Fact]
    public void A_Driven_Text_Is_Not_Overwritten()
    {
        var outcome = Edit(Drawing("""<text id="t">{{ label }}</text>"""), document => Get<SvgText>(document, "t").Text = "Bye");

        Assert.Equal("The text of <text> is set by {{ label }}, which a command cannot change.", outcome.Refusal);
    }

    [Fact]
    public void A_Literal_Written_Under_A_Driven_Group_Says_What_It_Shadows()
    {
        var outcome = Written(
            Drawing("""<g fill="{{ accent }}"><rect id="r" width="3" height="4" /></g>"""),
            document => Get<SvgRectangle>(document, "r").Fill = new SvgColourServer(Color.Lime));

        Assert.Contains("""<rect id="r" width="3" height="4" fill="#00ff00" />""", outcome.Text);
        Assert.Equal("The fill written on <rect> now shadows {{ accent }} from <g>.", outcome.Note);
    }

    [Fact]
    public void A_Driven_Value_Taken_Away_Is_Refused()
    {
        var outcome = Edit(Drawing("""<rect id="r" width="3" height="4" fill="{{ accent }}" />"""), document => Get<SvgRectangle>(document, "r").Fill = null);

        Assert.Equal("'fill' on <rect> is set by {{ accent }}, which a command cannot change.", outcome.Refusal);
    }

    [Fact]
    public void A_Driven_Value_Is_Not_Carried_Where_No_Expression_Can_Go()
    {
        var outcome = Edit(
            Drawing("""<rect id="r" width="3" height="4" stroke-width="{{ w }}" /><rect id="s" width="3" height="4" />"""),
            document => Get<SvgRectangle>(document, "s").X = Get<SvgRectangle>(document, "r").StrokeWidth);

        Assert.Equal("'x' on <rect> would be worked out from what an expression stands in for while the drawing is edited, rather than from its value.", outcome.Refusal);
    }

    [Fact]
    public void A_Transform_Worked_Out_From_An_Expression_Is_Not_Composed()
    {
        var outcome = Edit(
            Drawing("""<rect id="r" width="3" height="4" stroke-width="{{ w }}" transform="rotate({{ a }})" />"""),
            document =>
            {
                var rect = Get<SvgRectangle>(document, "r");

                rect.Transforms.Insert(0, new SvgTranslate(rect.StrokeWidth.Value, 0));
            });

        Assert.Equal("'transform' on <rect> is set by rotate({{ a }}), which a command cannot change.", outcome.Refusal);
    }

    [Fact]
    public void A_New_Element_Given_A_Value_Worked_Out_From_An_Expression_Is_Refused()
    {
        var outcome = Edit(
            Drawing("""<rect id="r" width="3" height="4" stroke-width="{{ w }}" />"""),
            document => document.Children.Add(new SvgCircle { Radius = 2, StrokeWidth = new SvgUnit(Get<SvgRectangle>(document, "r").StrokeWidth.Value + 1) }));

        Assert.Equal("'stroke-width' on <circle> would be worked out from what an expression stands in for while the drawing is edited, rather than from its value.", outcome.Refusal);
    }

    [Fact]
    public void An_Expression_The_Command_Unbinds_Is_Refused()
    {
        var outcome = Edit(
            Drawing("""<rect id="r" width="3" height="4" fill="{{ accent }}" />"""),
            document => Get<SvgRectangle>(document, "r").CustomAttributes.Remove(SvgExpressionAttributes.KeyFor("fill")));

        Assert.Equal("The command changed which expression sets 'fill' on <rect>.", outcome.Refusal);
    }

    [Fact]
    public void A_Command_That_Makes_Something_Only_Once_Is_Refused()
    {
        var calls = 0;

        var outcome = Edit(Drawing("""<rect id="r" width="3" height="4" />"""), document =>
        {
            if (calls++ == 0)
            {
                document.Children.Add(new SvgCircle { Radius = 1 });
            }
        });

        Assert.Equal("The command did not do the same thing on two readings of the drawing, so what it would write cannot be trusted.", outcome.Refusal);
    }

    [Fact]
    public void A_Command_That_Does_Something_Else_Each_Time_Is_Refused()
    {
        var calls = 0;

        var outcome = Edit(Drawing("""<rect id="r" width="3" height="4" />"""), document => Get<SvgRectangle>(document, "r").X = ++calls);

        Assert.Equal("The command did not do the same thing on two readings of the drawing, so what it would write cannot be trusted.", outcome.Refusal);
    }

    [Fact]
    public void A_Command_Refusing_Writes_Nothing_And_Says_Why()
    {
        var text = Drawing("""<rect id="r" width="3" height="4" />""");

        var outcome = Run(text, _ => "No.");

        Assert.Equal(("No.", text), (outcome.Refusal, outcome.Text));
    }

    // ---- elements -------------------------------------------------------------------------------

    [Fact]
    public void An_Element_Taken_Out_Goes_With_Its_Line()
    {
        var outcome = Written(
            Drawing("""
                <rect id="r" width="3" height="4" />
                  <circle id="c" r="2" />
                """),
            document => document.GetElementById("c").Parent.Children.Remove(document.GetElementById("c")));

        Assert.DoesNotContain("circle", outcome.Text);
        Assert.Contains("""<rect id="r" width="3" height="4" />""" + "\n</svg>", outcome.Text);
    }

    [Fact]
    public void A_New_Element_Is_Written_Where_The_Command_Put_It()
    {
        var outcome = Written(
            Drawing("""<rect id="r" width="3" height="4" />"""),
            document =>
            {
                var rect = document.GetElementById("r");

                rect.Parent.Children.Insert(rect.Parent.Children.IndexOf(rect) + 1, new SvgCircle { Radius = 2 });
            });

        Assert.Contains("""<rect id="r" width="3" height="4" />""" + "\n  <circle r=\"2\" />", outcome.Text);
    }

    [Fact]
    public void A_Copy_Of_A_Driven_Element_Keeps_Its_Expressions()
    {
        var outcome = Written(
            Drawing("""<rect id="r" width="3" height="4" fill="{{ accent }}" transform="rotate({{ a }})" />"""),
            document =>
            {
                var rect = document.GetElementById("r");
                var copy = (SvgElement)rect.DeepCopy();

                copy.ID = null;
                rect.Parent.Children.Add(copy);
            });

        Assert.True(outcome.Text.Contains("""<rect width="3" height="4" transform="rotate({{ a }})" fill="{{ accent }}" />""", StringComparison.Ordinal), outcome.Text);
    }

    [Fact]
    public void A_New_Element_Given_A_Driven_Value_Carries_Its_Expression()
    {
        var outcome = Written(
            Drawing("""<rect id="r" width="3" height="4" fill="{{ accent }}" />"""),
            document => document.Children.Add(new SvgCircle { Radius = 2, Fill = Get<SvgRectangle>(document, "r").Fill }));

        Assert.Contains("""<circle r="2" fill="{{ accent }}" />""", outcome.Text);
    }

    [Fact]
    public void New_Elements_Land_In_The_Order_The_Command_Put_Them()
    {
        var outcome = Written(
            Drawing("""<g id="g"><rect id="r" width="3" height="4" /></g>"""),
            document =>
            {
                var group = document.GetElementById("g");

                group.Children.Insert(0, new SvgCircle { Radius = 1 });
                group.Children.Insert(1, new SvgCircle { Radius = 2 });
                group.Children.Add(new SvgCircle { Radius = 3 });
                group.Children.Add(new SvgCircle { Radius = 4 });
            });

        var order = new[] { "r=\"1\"", "r=\"2\"", "id=\"r\"", "r=\"3\"", "r=\"4\"" }.Select(each => outcome.Text.IndexOf(each, StringComparison.Ordinal)).ToList();

        Assert.True(order.All(at => at > 0) && order.SequenceEqual(order.OrderBy(at => at)), outcome.Text);
    }

    [Fact]
    public void Children_Reordered_Move_Only_What_Has_To()
    {
        var text = Drawing("""
            <rect id="a" width="1" height="1" />
              <g id="b"><!-- inside --><circle r="1" /></g>
              <rect id="c" width="1" height="1" />
              <rect id="d" width="1" height="1" />
            """);

        var outcome = Written(text, document =>
        {
            var d = document.GetElementById("d");

            document.Children.Remove(d);
            document.Children.Insert(document.Children.IndexOf(document.GetElementById("a")), d);
        });

        Assert.Equal(
            text.Replace("\n  <rect id=\"d\" width=\"1\" height=\"1\" />", string.Empty, StringComparison.Ordinal)
                .Replace("<rect id=\"a\"", "<rect id=\"d\" width=\"1\" height=\"1\" />\n  <rect id=\"a\"", StringComparison.Ordinal),
            outcome.Text);
    }

    [Fact]
    public void A_Replacement_Is_Written_As_The_Element_Retagged()
    {
        var replaced = Run(
            Drawing("""<rect id="r" class="k"  x="1" y="2" width="3" height="4" fill="{{ accent }}" /><!-- kept -->"""),
            context =>
            {
                var rect = context.Element("1")!;
                var path = new SvgPath { PathData = new SvgPathSegmentList { new SvgMoveToSegment(false, new PointF(1, 2)), new SvgLineSegment(false, new PointF(4, 6)) } };

                path.CustomAttributes["class"] = "k";

                foreach (var pair in rect.CustomAttributes)
                {
                    path.CustomAttributes[pair.Key] = pair.Value;
                }

                path.Fill = rect.Fill;
                context.Replace(rect, path);
                path.ID = "r";

                return null;
            });

        Assert.Null(replaced.Refusal);
        Assert.True(replaced.Text.Contains("""<path id="r" class="k" fill="{{ accent }}" d="M 1 2 L 4 6" /><!-- kept -->""", StringComparison.Ordinal), replaced.Text);
    }

    [Fact]
    public void A_Replaced_Element_Put_Back_Is_Refused()
    {
        var outcome = Run(Drawing("""<rect id="r" width="3" height="4" />"""), context =>
        {
            var rect = context.Element("1")!;

            context.Replace(rect, new SvgCircle { Radius = 1 });
            context.Document.Children.Add(rect);

            return null;
        });

        Assert.Equal("A <rect> the command replaced is still in the drawing.", outcome.Refusal);
    }

    [Fact]
    public void A_Class_The_Command_Sets_Is_Written()
    {
        var outcome = Written(Drawing("""<rect id="r" width="3" height="4" />"""), document => Get<SvgRectangle>(document, "r").CustomAttributes["class"] = "k");

        Assert.Contains("""<rect id="r" width="3" height="4" class="k" />""", outcome.Text);
    }

    [Fact]
    public void A_Style_Only_Property_With_Nowhere_To_Go_Is_Refused()
    {
        var outcome = Edit(Drawing("""<rect id="r" width="3" height="4" />"""), document => Get<SvgRectangle>(document, "r").CustomAttributes["mix-blend-mode"] = "multiply");

        Assert.Equal("'mix-blend-mode' on <rect> would have to be added to its style, which a command does not write yet.", outcome.Refusal);
    }

    [Fact]
    public void An_Href_Is_Written_As_The_File_Writes_It()
    {
        var outcome = Written(
            """<svg xmlns="http://www.w3.org/2000/svg" xmlns:l="http://www.w3.org/1999/xlink"><rect id="a" width="1" height="1" /><rect id="b" width="1" height="1" /><use id="u" l:href="#a" /></svg>""",
            document => Get<SvgUse>(document, "u").ReferencedElement = new Uri("#b", UriKind.Relative));

        Assert.Contains("""<use id="u" l:href="#b" />""", outcome.Text);
    }

    [Fact]
    public void Elements_Sharing_An_Id_Can_Be_Reordered()
    {
        var outcome = Written(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect id="x" width="1" height="1" /><circle id="x" r="1" /></svg>""",
            document =>
            {
                var circle = document.Children[1];

                document.Children.Remove(circle);
                document.Children.Insert(0, circle);
            });

        Assert.True(outcome.Text.IndexOf("<circle", StringComparison.Ordinal) < outcome.Text.IndexOf("<rect", StringComparison.Ordinal), outcome.Text);
    }

    [Fact]
    public void A_New_Element_Under_A_Sheet_Takes_What_The_Sheet_Gives_It()
    {
        var outcome = Written(
            Drawing("""<style>rect { stroke: red }</style><rect id="r" width="3" height="4" />"""),
            document => document.Children.Add(new SvgRectangle { Width = 1, Height = 1 }));

        Assert.Contains("""<rect width="1" height="1" />""", outcome.Text);
    }

    [Fact]
    public void An_Element_Moved_Into_A_New_One_Is_Refused()
    {
        var outcome = Edit(
            Drawing("""<rect id="r" width="3" height="4" />"""),
            document =>
            {
                var rect = document.GetElementById("r");
                var group = new SvgGroup();

                document.Children.Remove(rect);
                group.Children.Add(rect);
                document.Children.Add(group);
            });

        Assert.Equal("A <rect> already in the drawing was put inside one the command made, which is not written yet.", outcome.Refusal);
    }

    [Fact]
    public void Text_A_Command_Writes_Is_Written_Between_The_Tags()
    {
        var outcome = Written(Drawing("""<text id="t" x="1">Hi</text>"""), document => Get<SvgText>(document, "t").Text = "Bye & so");

        Assert.Contains("""<text id="t" x="1">Bye &amp; so</text>""", outcome.Text);
    }

    // ---- what the file cannot say ---------------------------------------------------------------

    [Fact]
    public void A_Value_A_Sheet_Would_Override_Is_Refused_Rather_Than_Written_Wrong()
    {
        var outcome = Edit(
            Drawing("""<style>.k { fill: red }</style><rect id="r" class="k" width="3" height="4" />"""),
            document => Get<SvgRectangle>(document, "r").Fill = new SvgColourServer(Color.Lime));

        Assert.Equal("What the command made would not read back from the file: 'fill' on <rect> would read as something else.", outcome.Refusal);
    }
}
