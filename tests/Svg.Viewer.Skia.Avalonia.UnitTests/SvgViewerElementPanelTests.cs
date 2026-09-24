using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.LogicalTree;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Svg.Expressions;
using Svg.SourceEditing;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// One element's attributes, each editable as the file writes it.
/// </summary>
/// <remarks>
/// The panel is driven through its own seams rather than through a pointer: a box is a box, and what
/// is worth pinning is which rows exist, what they say, and what reaches the text.
/// </remarks>
public class SvgViewerElementPanelTests
{
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24">
          <defs><e:code><e:param name="tint" type="color" default="#ff0000" /></e:code></defs>
          <g id="wrap">
            <rect x="0" y="0" width="24" height="24" fill="#00ff00" />
          </g>
        </svg>
        """;

    /// <summary>The panel over a text it owns, so a test can read back what was written.</summary>
    private sealed class Held
    {
        public Held(string text = Drawing)
        {
            Text = text;

            Panel = new SvgViewerElementPanel(
                () => Text,
                () => Text,
                (label, edit) =>
                {
                    if (SvgSourceDocument.Read(Text, out var unreadable) is not { } source)
                    {
                        return unreadable;
                    }

                    if (edit(source) is { } refusal)
                    {
                        return refusal;
                    }

                    Text = source.ToText();
                    Panel!.Refresh();

                    return null;
                },
                () => ExprEvaluator.Create(SvgExpressionDeclarations.Parse(Text, out _)));
        }

        public string Text { get; private set; }

        public SvgViewerElementPanel Panel { get; }

        public Window Show(string? address)
        {
            var window = new Window { Width = 400, Height = 600, Background = Brushes.White, Content = Panel };

            window.Show();
            Panel.Show(address);
            Dispatcher.UIThread.RunJobs();

            return window;
        }
    }

    /// <summary>
    /// An edit made before the panel has ever been shown.
    /// </summary>
    /// <remarks>
    /// Through a real viewer rather than the harness above, because the harness applies edits to a
    /// string of its own and so cannot see this: the panel measures an edit against the drawing's
    /// own text, and the pane it is spliced into is empty until something fills it. Typing
    /// rotate(90) into the transform row of an unopened drawing crashed Studio with
    /// "0 &lt;= offset &lt;= 0 … Actual value was 355".
    /// </remarks>
    [AvaloniaFact]
    public async Task An_Attribute_Set_Before_The_Pane_Is_Opened_Reaches_The_Drawing()
    {
        var viewer = new SvgViewer();
        var window = new Window { Width = 500, Height = 400, Background = Brushes.White, Content = viewer };

        window.Show();
        Assert.True(await viewer.LoadTextAsync(Drawing));
        Dispatcher.UIThread.RunJobs();

        // The panel is a tab's content, so it is a logical child before that tab is ever shown.
        var panel = viewer.GetLogicalDescendants().OfType<SvgViewerElementPanel>().Single();

        panel.Show("1/0");
        Dispatcher.UIThread.RunJobs();

        Assert.True(panel.Set("transform", "rotate(90)"));
        Assert.Contains("transform=\"rotate(90)\"", viewer.Source);
    }

    [AvaloniaFact]
    public void The_Rows_Are_What_The_Element_Is_Written_With()
    {
        var held = new Held();

        held.Show("1/0");

        // In the order the file writes them, and nothing the file does not say.
        Assert.Equal(
            new[] { "x", "y", "width", "height", "fill" },
            held.Panel.Attributes.TakeWhile(name => name != "stroke").ToArray());

        Assert.Equal("#00ff00", held.Panel.Shown("fill"));
        Assert.Equal("24", held.Panel.Shown("width"));
    }

    [AvaloniaFact]
    public void The_Ones_It_Could_Take_Follow_The_Ones_It_Has()
    {
        var held = new Held();

        held.Show("1/0");

        // Empty, so typing into one adds it.
        Assert.Contains("opacity", held.Panel.Attributes);
        Assert.Equal(string.Empty, held.Panel.Shown("opacity"));

        // Every attribute an expression can drive is offered.
        foreach (var name in SvgExpressionAttributes.Supported)
        {
            Assert.Contains(name, held.Panel.Attributes);
        }
    }

    [AvaloniaFact]
    public void Nothing_Picked_Says_To_Pick_Something()
    {
        var held = new Held();

        held.Show(null);

        Assert.Empty(held.Panel.Attributes);
    }

    [AvaloniaFact]
    public void An_Expression_Is_Written_Into_The_Attribute()
    {
        var held = new Held();

        held.Show("1/0");

        Assert.True(held.Panel.Set("fill", "{{ tint }}"));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("fill=\"{{ tint }}\"", held.Text);

        // And the row shows the expression back, not the placeholder the parser puts in its place.
        Assert.Equal("{{ tint }}", held.Panel.Shown("fill"));
    }

    /// <summary>The box holds the value, so binding and unbinding are the same gesture.</summary>
    [AvaloniaFact]
    public void A_Literal_Typed_Over_An_Expression_Puts_It_Back()
    {
        var held = new Held();

        held.Show("1/0");

        Assert.True(held.Panel.Set("fill", "{{ tint }}"));
        Assert.True(held.Panel.Set("fill", "#123456"));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("fill=\"#123456\"", held.Text);
        Assert.DoesNotContain("{{ tint }}", held.Text);
    }

    [AvaloniaFact]
    public void An_Empty_Box_Takes_The_Attribute_Away()
    {
        var held = new Held();

        held.Show("1/0");

        Assert.True(held.Panel.Set("fill", string.Empty));
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("fill=", held.Text);
        Assert.Contains("""<rect x="0" y="0" width="24" height="24" />""", held.Text);
    }

    [AvaloniaFact]
    public void An_Attribute_It_Does_Not_Have_Is_Added()
    {
        var held = new Held();

        held.Show("1/0");

        Assert.True(held.Panel.Set("opacity", "{{ 0.5 }}"));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("opacity=\"{{ 0.5 }}\"", held.Text);
    }

    [AvaloniaFact]
    public void An_Expression_Of_The_Wrong_Type_Is_Refused()
    {
        var held = new Held();

        held.Show("1/0");

        var was = held.Text;

        // Well formed and wrong: a fill is a colour slot.
        Assert.False(held.Panel.Set("fill", "{{ 1 + 1 }}"));

        Assert.Equal(was, held.Text);
        Assert.Contains("colour", held.Panel.Fault);
    }

    /// <summary>
    /// An attribute the parser lifts nothing out of says so, rather than writing braces that are
    /// read as an ordinary value.
    /// </summary>
    [AvaloniaFact]
    public void An_Expression_Where_One_Does_Nothing_Is_Refused()
    {
        var held = new Held();

        held.Show("1/0");

        var was = held.Text;

        Assert.False(held.Panel.Set("width", "{{ tint }}"));

        Assert.Equal(was, held.Text);
        Assert.Contains("does not take an expression", held.Panel.Fault);

        // A literal in the same box is nobody's business but the parser's, and is written.
        Assert.True(held.Panel.Set("width", "48"));
        Assert.Contains("width=\"48\"", held.Text);
    }

    [AvaloniaFact]
    public void An_Element_Only_The_Drawing_Has_Says_So()
    {
        var held = new Held("""<svg xmlns="http://www.w3.org/2000/svg"><rect /></svg>""");

        held.Show("9");

        Assert.Empty(held.Panel.Attributes);
    }

    [AvaloniaFact]
    public void An_Element_Written_With_Nothing_Still_Takes_An_Attribute()
    {
        var held = new Held("""<svg xmlns="http://www.w3.org/2000/svg"><rect /></svg>""");

        held.Show("0");

        Assert.True(held.Panel.Set("fill", "#00ff00"));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("""<rect fill="#00ff00" />""", held.Text);
    }

    [AvaloniaFact]
    public void A_Prefixed_Attribute_Is_Written_As_Itself_Rather_Than_Twice()
    {
        // Reading xlink:href back as href and then writing href is how a drawing comes to hold
        // both, pointing two ways at once, with the panel reporting that it worked.
        const string drawing = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
              <use xlink:href="#a" x="1" />
            </svg>
            """;

        var held = new Held(drawing);
        var window = held.Show("0");

        Assert.Contains("xlink:href", held.Panel.Attributes);

        Assert.True(held.Panel.Set("xlink:href", "#b"));

        Assert.Contains("""<use xlink:href="#b" x="1" />""", held.Text);
        Assert.DoesNotContain(" href=", held.Text);

        window.Close();
    }

    // ---- the element's text ----

    /// <summary>A drawing whose text is worth a row, and things that look like one and are not.</summary>
    private const string Words = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24">
          <defs><e:code><e:param name="label" type="string" default="'Hi'" /></e:code></defs>
          <text x="1" y="8">Hi</text>
          <text x="2" y="8"> <tspan>a</tspan> </text>
          <rect x="0" y="0" width="24" height="24" />
        </svg>
        """;

    /// <summary>
    /// The words come first, because they are what the element says.
    /// </summary>
    [AvaloniaFact]
    public void A_Text_Elements_Words_Are_Its_First_Row()
    {
        var held = new Held(Words);

        held.Show("1");

        Assert.Equal(SvgExpressionAttributes.ContentName, held.Panel.Attributes[0]);
        Assert.Equal("Hi", held.Panel.Shown(SvgExpressionAttributes.ContentName));
    }

    [AvaloniaFact]
    public void Words_Typed_Into_The_Row_Reach_The_Drawing()
    {
        var held = new Held(Words);

        held.Show("1");

        Assert.True(held.Panel.Set(SvgExpressionAttributes.ContentName, "Bye"));

        Assert.Contains("<text x=\"1\" y=\"8\">Bye</text>", held.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An expression is accepted and read back as one, which is the whole point of the row.
    /// </summary>
    /// <remarks>
    /// Binding and unbinding is one gesture: the box holds either the words or the expression that
    /// produces them, and nothing else has to be said to move between the two.
    /// </remarks>
    [AvaloniaFact]
    public void The_Row_Takes_An_Expression_And_Gives_It_Back()
    {
        var held = new Held(Words);

        held.Show("1");

        Assert.True(held.Panel.Set(SvgExpressionAttributes.ContentName, "{{ label }}"));

        Assert.Contains("<text x=\"1\" y=\"8\">{{ label }}</text>", held.Text, StringComparison.Ordinal);
        Assert.Equal("{{ label }}", held.Panel.Shown(SvgExpressionAttributes.ContentName));

        // And back to words again.
        Assert.True(held.Panel.Set(SvgExpressionAttributes.ContentName, "Hi"));
        Assert.Contains("<text x=\"1\" y=\"8\">Hi</text>", held.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An expression of the wrong type is refused by the checker rather than thrown over.
    /// </summary>
    /// <remarks>
    /// This is the case that took the pane down before <c>DescribeUse</c> named the string use: the
    /// throw came out of the argument list, and the catch here only ever caught an ExprException.
    /// </remarks>
    [AvaloniaFact]
    public void An_Expression_That_Is_Not_Text_Is_Refused()
    {
        var held = new Held(Words);

        held.Show("1");

        Assert.False(held.Panel.Set(SvgExpressionAttributes.ContentName, "{{ 1 + 1 }}"));

        Assert.Contains("string", held.Panel.Fault!, StringComparison.Ordinal);
        Assert.Contains("<text x=\"1\" y=\"8\">Hi</text>", held.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void An_Emptied_Row_Leaves_The_Element_With_No_Text()
    {
        var held = new Held(Words);

        held.Show("1");

        Assert.True(held.Panel.Set(SvgExpressionAttributes.ContentName, string.Empty));

        Assert.Contains("<text x=\"1\" y=\"8\"></text>", held.Text, StringComparison.Ordinal);
    }

    /// <summary>A space is a value, so the row does not tidy one away.</summary>
    [AvaloniaFact]
    public void The_Row_Does_Not_Trim_What_Is_Typed()
    {
        var held = new Held(Words);

        held.Show("1");

        Assert.True(held.Panel.Set(SvgExpressionAttributes.ContentName, "  spaced  "));

        Assert.Contains("<text x=\"1\" y=\"8\">  spaced  </text>", held.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void An_Element_Whose_Text_Is_In_Its_Children_Has_No_Row()
    {
        var held = new Held(Words);

        held.Show("2");

        Assert.DoesNotContain(SvgExpressionAttributes.ContentName, held.Panel.Attributes);

        Assert.False(held.Panel.Set(SvgExpressionAttributes.ContentName, "no"));
        Assert.StartsWith("This element's text is written in its <tspan> children", held.Panel.Fault!, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void A_Shape_Has_No_Words_To_Show()
    {
        var held = new Held(Words);

        held.Show("3");

        Assert.DoesNotContain(SvgExpressionAttributes.ContentName, held.Panel.Attributes);

        Assert.False(held.Panel.Set(SvgExpressionAttributes.ContentName, "no"));
        Assert.Equal("Only a <text>, a <tspan> or a <textPath> has text of its own to edit.", held.Panel.Fault);
    }

    /// <summary>The regression guard for the throw, on the row that could always reach it.</summary>
    [AvaloniaFact]
    public void A_String_Attribute_Takes_An_Expression()
    {
        var held = new Held(Words);

        held.Show("1");

        Assert.True(held.Panel.Set("font-family", "{{ label }}"));

        Assert.Contains("font-family=\"{{ label }}\"", held.Text, StringComparison.Ordinal);
    }
    // ---- a variable dragged onto a row -----------------------------------------------------------

    /// <summary>A drawing declaring one of each kind, so a drop can be offered the wrong one.</summary>
    private const string Bound = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24">
          <defs><e:code>
            <e:param name="tint" type="color" default="#ff0000" />
            <e:param name="ring" type="number" default="2" />
          </e:code></defs>
          <g id="wrap">
            <rect x="0" y="0" width="24" height="24" fill="#00ff00" transform="rotate(5)" />
          </g>
        </svg>
        """;

    /// <summary>The box a row is edited in, by the attribute it is for.</summary>
    private static TextBox Box(Window window, string name)
        => window.GetVisualDescendants().OfType<TextBox>().Single(box => Equals(box.Tag, name));

    /// <summary>
    /// Carries <paramref name="variable"/> over the middle of <paramref name="box"/> and lets go.
    /// </summary>
    /// <remarks>
    /// The whole sequence, because only the move over a row works out where a drop would land. The
    /// drag is injected rather than started: nothing in this repository drives a real
    /// <c>DoDragDropAsync</c> source headlessly, and what is worth pinning is the end that decides.
    /// </remarks>
    private static void Carry(Window window, TextBox box, string variable, bool drop = true)
    {
        // Scrolled to first, as somebody dragging would have to: the rows outrun their region, and
        // a point worked out for one that is still below it lands on whatever is drawn there.
        box.BringIntoView();
        Dispatcher.UIThread.RunJobs();

        var carried = new DataTransfer();

        carried.Add(DataTransferItem.Create(SvgViewerVariableDrag.Format, variable));

        var at = box.TranslatePoint(new Point(box.Bounds.Width / 2d, box.Bounds.Height / 2d), window);

        Assert.NotNull(at);

        var stages = drop
            ? new[] { RawDragEventType.DragEnter, RawDragEventType.DragOver, RawDragEventType.Drop }
            : new[] { RawDragEventType.DragEnter, RawDragEventType.DragOver };

        foreach (var stage in stages)
        {
            window.DragDrop(at!.Value, stage, carried, DragDropEffects.Link, RawInputModifiers.None);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void A_Variable_Dropped_On_A_Row_Is_Written_Into_It()
    {
        var held = new Held(Bound);
        var window = held.Show("1/0");

        Carry(window, Box(window, "fill"), "tint");

        // The whole value, over the literal that was there: binding and unbinding are the one
        // gesture, so a drop says what the attribute is now and not what it also is.
        Assert.Contains("fill=\"{{ tint }}\"", held.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("#00ff00\" transform", held.Text, StringComparison.Ordinal);
        Assert.Equal("{{ tint }}", held.Panel.Shown("fill"));

        window.Close();
    }

    /// <summary>A row is offered a variable only where the expression would check.</summary>
    /// <remarks>
    /// The same check typing it would have gone through, made before the drop rather than after: a
    /// drag that can be let go anywhere and then refused says nothing while it is being made.
    /// </remarks>
    [AvaloniaFact]
    public void A_Variable_Of_The_Wrong_Type_Is_Refused_Before_It_Lands()
    {
        var held = new Held(Bound);
        var window = held.Show("1/0");

        Carry(window, Box(window, "fill"), "ring");

        // Never offered, so there was nothing to let go of over it.
        Assert.Empty(held.Panel.Offered);

        Assert.Contains("fill=\"#00ff00\"", held.Text, StringComparison.Ordinal);
        Assert.Equal("#00ff00", held.Panel.Shown("fill"));

        window.Close();
    }

    /// <summary>
    /// A transform is written one argument at a time, so nothing lands on the whole of one.
    /// </summary>
    /// <remarks>
    /// No special case anywhere: the check a typed value goes through already says that braces in a
    /// transform have to be one whole function argument, so the row simply never lights up.
    /// </remarks>
    [AvaloniaFact]
    public void A_Transform_Is_Not_A_Row_A_Variable_Lands_On()
    {
        var held = new Held(Bound);
        var window = held.Show("1/0");

        Carry(window, Box(window, "transform"), "ring");

        Assert.Contains("transform=\"rotate(5)\"", held.Text, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>A row the file writes nothing for is where an attribute is added by dropping one.</summary>
    [AvaloniaFact]
    public void A_Row_With_Nothing_In_It_Takes_The_Attribute_On()
    {
        var held = new Held(Bound);
        var window = held.Show("1/0");

        Assert.Equal(string.Empty, held.Panel.Shown("stroke"));

        Carry(window, Box(window, "stroke"), "tint");

        Assert.Contains("stroke=\"{{ tint }}\"", held.Text, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>Every row that would take it says so while the drag is over the panel.</summary>
    /// <remarks>
    /// Worked out once when the drag arrives rather than on every move: the check reparses
    /// everything in scope, which is fine per keystroke and not fine per pointer move.
    /// </remarks>
    [AvaloniaFact]
    public void The_Rows_That_Would_Take_It_Are_Outlined_While_It_Is_Carried()
    {
        var held = new Held(Bound);
        var window = held.Show("1/0");

        Carry(window, Box(window, "fill"), "tint", drop: false);

        Assert.Contains("fill", held.Panel.Offered);
        Assert.Contains("stroke", held.Panel.Offered);

        // Not a row it could not be written into, however close by it is.
        Assert.DoesNotContain("transform", held.Panel.Offered);
        Assert.DoesNotContain("stroke-width", held.Panel.Offered);

        // And the one under the pointer is the one picked out of them.
        Assert.Equal(new Thickness(2d), Box(window, "fill").BorderThickness);
        Assert.NotEqual(new Thickness(2d), Box(window, "stroke").BorderThickness);

        // Nothing is left marked once the drag has gone.
        window.DragDrop(
            new Point(1, 1), RawDragEventType.DragLeave, new DataTransfer(), DragDropEffects.None, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(held.Panel.Offered);
        Assert.NotEqual(new Thickness(2d), Box(window, "fill").BorderThickness);

        window.Close();
    }
    /// <summary>
    /// The drag survives the viewer the panel sits in, which turns away everything carrying no files.
    /// </summary>
    /// <remarks>
    /// The one thing the harness above cannot show. <c>SvgViewer</c> answers a drag over any of it by
    /// refusing whatever carries no file — which is every drag of a variable — so a panel that did
    /// not mark the event handled would kill the gesture before it could land. Studio's window has a
    /// second file handler behind that one again.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Variable_Reaches_A_Row_Through_The_Viewer_Around_It()
    {
        var viewer = new SvgViewer();
        var window = new Window { Width = 900, Height = 700, Background = Brushes.White, Content = viewer };

        window.Show();
        Assert.True(await viewer.LoadTextAsync(Bound));
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.Elements.TrySelect("1/0"));
        Dispatcher.UIThread.RunJobs();

        Carry(window, Box(window, "fill"), "tint");

        Assert.Contains("fill=\"{{ tint }}\"", viewer.Source, StringComparison.Ordinal);

        window.Close();
    }
}
