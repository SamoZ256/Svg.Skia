using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
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
                result =>
                {
                    Text = SvgTextEdit.ApplyAll(Text, result.Edits);
                    Panel!.Refresh();

                    return true;
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
    /// An edit made before the source pane has ever been opened.
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
}
