// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Svg.Expressions;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// Declaring, rewriting and reordering a let from its row in the panel.
/// </summary>
/// <remarks>
/// The splice itself is pinned in Svg.SourceEditing.UnitTests against far more awkward documents;
/// here it only has to arrive. What is new is that a row is edited in place rather than through a
/// form, so what these ask is when a half-typed row reaches the drawing — which is never — and what
/// the row says back while it is being typed.
/// </remarks>
public class SvgViewerLetEditingTests
{
    private const string Grouped = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code>
              <e:param name="tint" type="color" default="#ff0000" />
              <e:let name="deep">mix(tint, #000000, 0.5)</e:let>
            </e:code>
          </defs>
          <rect x="0" y="0" width="24" height="24" fill="{{ deep }}" />
        </svg>
        """;

    private const string Two = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code>
              <e:param name="t" type="number" default="0.25" />
              <e:let name="a">t * 2</e:let>
              <e:let name="b">t * 3</e:let>
            </e:code>
          </defs>
          <rect x="0" y="0" width="24" height="24" fill="#ff0000" opacity="{{ a }}" />
        </svg>
        """;

    private static async Task<(Window Window, SvgViewer Viewer)> HostLoaded(string markup)
    {
        var viewer = new SvgViewer();

        var window = new Window
        {
            Width = 500,
            Height = 400,
            Background = Brushes.White,
            Content = viewer
        };

        window.Show();

        Assert.True(await viewer.LoadTextAsync(markup));
        Dispatcher.UIThread.RunJobs();

        return (window, viewer);
    }

    /// <summary>Waits for the rebuild the debounce holds back.</summary>
    private static async Task Settle()
    {
        Dispatcher.UIThread.RunJobs();

        // Real time, because the debounce is a real timer: the point of it is that it waits.
        await Task.Delay(400).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();
    }

    private static TextEditor Pane(SvgViewer viewer)
        => viewer.GetVisualDescendants().OfType<TextEditor>().First(c => c.Name == "SourceEditor");

    private static Button AddLetButton(SvgViewer viewer)
        => viewer.GetVisualDescendants().OfType<Button>().First(c => c.Name == "AddLetButton");

    private static SvgViewerLet Row(SvgViewer viewer, string name)
        => viewer.Lets.Single(let => let.Name == name);

    /// <summary>One of a row's two boxes, told apart by what it prompts for.</summary>
    private static TextBox Box(SvgViewer viewer, SvgViewerLet row, string placeholder)
        => viewer.GetVisualDescendants()
            .OfType<TextBox>()
            .First(box => ReferenceEquals(box.DataContext, row) && box.PlaceholderText == placeholder);

    // ---- adding ----

    [AvaloniaFact]
    public async Task The_Add_Button_Leaves_An_Empty_Row_To_Type_Into()
    {
        var (window, viewer) = await HostLoaded(Grouped);

        AddLetButton(viewer).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var draft = Assert.Single(viewer.Lets, let => let.IsDraft);

        Assert.Equal(string.Empty, draft.Name);
        Assert.Equal(string.Empty, draft.Expression);

        // Nothing has been written: an empty row is an intention, not a declaration.
        Assert.False(viewer.IsSourceModified);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Let_Filled_In_From_Its_Row_Reaches_The_Drawing()
    {
        var (window, viewer) = await HostLoaded(Grouped);

        AddLetButton(viewer).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var draft = Assert.Single(viewer.Lets, let => let.IsDraft);

        draft.Name = "deeper";
        draft.Expression = "mix(deep, #000000, 0.5)";

        Assert.True(viewer.CommitLet(draft));
        await Settle();

        Assert.Equal(new[] { "deep", "deeper" }, viewer.Lets.Select(let => let.Name).ToArray());

        // Below the let it names, in the drawing's own text.
        Assert.Contains("""<e:let name="deeper">mix(deep, #000000, 0.5)</e:let>""", Pane(viewer).Text);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Row_Nobody_Typed_Into_Is_Not_Written()
    {
        var (window, viewer) = await HostLoaded(Grouped);

        AddLetButton(viewer).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var draft = Assert.Single(viewer.Lets, let => let.IsDraft);

        // Only half of it, which is what a row looks like when somebody thinks better of it.
        draft.Name = "unfinished";

        Assert.False(viewer.CommitLet(draft));
        await Settle();

        Assert.DoesNotContain("unfinished", Pane(viewer).Text);
        Assert.False(viewer.IsSourceModified);

        window.Close();
    }

    // ---- rewriting ----

    [AvaloniaFact]
    public async Task Renaming_A_Let_From_Its_Row_Moves_Its_Uses()
    {
        var (window, viewer) = await HostLoaded(Grouped);

        var row = Row(viewer, "deep");

        row.Name = "shadow";

        Assert.True(viewer.CommitLet(row));
        await Settle();

        var text = Pane(viewer).Text;

        Assert.Contains("""<e:let name="shadow">""", text);
        Assert.Contains("{{ shadow }}", text);
        Assert.DoesNotContain("deep", text);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Changing_A_Body_Changes_What_Is_Drawn()
    {
        var (window, viewer) = await HostLoaded(Grouped);

        var row = Row(viewer, "deep");

        row.Expression = "mix(tint, #ffffff, 0.5)";

        Assert.True(viewer.CommitLet(row));
        await Settle();

        Assert.Contains("""<e:let name="deep">mix(tint, #ffffff, 0.5)</e:let>""", Pane(viewer).Text);

        // Read back from the drawing rather than from the row: what matters is that the picture was
        // rebuilt from the edited text.
        Assert.Equal("mix(tint, #ffffff, 0.5)", Row(viewer, "deep").Declaration!.Expression);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Body_The_Language_Refuses_Marks_The_Row_And_Writes_Nothing()
    {
        var (window, viewer) = await HostLoaded(Grouped);

        var row = Row(viewer, "deep");

        row.Expression = "mix(tint, #000000)";

        Assert.True(row.HasTrouble);
        Assert.NotNull(row.Trouble);

        Assert.False(viewer.CommitLet(row));
        await Settle();

        // The drawing still says what it said, and nothing was written to take back.
        Assert.Equal("mix(tint, #000000, 0.5)", row.Declaration!.Expression);
        Assert.False(viewer.IsSourceModified);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Name_Already_Spoken_For_Marks_The_Row()
    {
        var (window, viewer) = await HostLoaded(Grouped);

        var row = Row(viewer, "deep");

        row.Name = "tint";

        Assert.True(row.HasTrouble);
        Assert.False(viewer.CommitLet(row));

        window.Close();
    }

    [AvaloniaFact]
    public async Task Enter_In_A_Row_Writes_It()
    {
        var (window, viewer) = await HostLoaded(Grouped);

        var row = Row(viewer, "deep");

        row.Expression = "mix(tint, #ffffff, 0.25)";

        Box(viewer, row, "expression").RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
        });

        await Settle();

        Assert.Equal("mix(tint, #ffffff, 0.25)", Row(viewer, "deep").Declaration!.Expression);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Row_Being_Typed_Into_Survives_Something_Else_Editing_The_Drawing()
    {
        var (window, viewer) = await HostLoaded(Grouped);

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var row = Row(viewer, "deep");

        row.Expression = "mix(tint, #ffffff, 0.9)";

        // Somebody typing in the pane, which rebuilds the drawing and every row with it.
        Pane(viewer).Document.Insert(0, "<!-- a comment nobody asked about -->\n");

        await Settle();

        // The same row object, still holding what was typed into it.
        Assert.Same(row, Row(viewer, "deep"));
        Assert.Equal("mix(tint, #ffffff, 0.9)", row.Expression);

        window.Close();
    }

    // ---- what a let is worth right now ----

    [AvaloniaFact]
    public async Task A_Let_Reads_Out_What_It_Evaluates_To()
    {
        var (window, viewer) = await HostLoaded(Grouped);

        // mix halfway between #ff0000 and black.
        Assert.Equal("colour  #800000", Row(viewer, "deep").Readout);

        Assert.True(viewer.TrySetParameterValue("tint", ExprValue.Color(0x00, 0x00, 0xff, 0xff)));
        await Settle();

        Assert.Equal("colour  #000080", Row(viewer, "deep").Readout);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Row_Being_Typed_Into_Reads_Out_Nothing()
    {
        var (window, viewer) = await HostLoaded(Grouped);

        var row = Row(viewer, "deep");

        Assert.NotEqual(string.Empty, row.Readout);

        row.Expression = "mix(tint, #ffffff, 0.25)";

        await Settle();

        // What the drawing is showing is still the declared body, so a value beside the typed one
        // would be a number for an expression nobody has committed.
        Assert.Equal(string.Empty, row.Readout);

        window.Close();
    }

    // ---- reordering ----

    [AvaloniaFact]
    public async Task Two_Lets_That_Do_Not_Name_Each_Other_Can_Be_Swapped()
    {
        var (window, viewer) = await HostLoaded(Two);

        Assert.True(viewer.MoveLet(Row(viewer, "b"), 0));
        await Settle();

        Assert.Equal(new[] { "b", "a" }, viewer.Lets.Select(let => let.Name).ToArray());

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Let_Cannot_Be_Moved_Above_What_It_Names()
    {
        var (window, viewer) = await HostLoaded(Two.Replace("""<e:let name="b">t * 3</e:let>""", """<e:let name="b">a * 3</e:let>"""));

        Assert.False(viewer.MoveLet(Row(viewer, "b"), 0));
        await Settle();

        Assert.Equal(new[] { "a", "b" }, viewer.Lets.Select(let => let.Name).ToArray());

        window.Close();
    }

    // ---- taking one away ----

    [AvaloniaFact]
    public async Task A_Let_Nothing_Names_Is_Removed_From_The_Drawing()
    {
        var (window, viewer) = await HostLoaded(Two);

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        // `a` is on the rect's opacity; `b` is named by nothing.
        Assert.True(viewer.RemoveLet(Row(viewer, "b")));
        await Settle();

        Assert.Equal(new[] { "a" }, viewer.Lets.Select(let => let.Name).ToArray());
        Assert.DoesNotContain("tau / 3", Pane(viewer).Text);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Let_The_Drawing_Still_Uses_Is_Refused()
    {
        var (window, viewer) = await HostLoaded(Two);

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewer.RemoveLet(Row(viewer, "a")));
        await Settle();

        Assert.Contains(viewer.Lets, let => let.Name == "a");
        Assert.Contains("{{ a }}", Pane(viewer).Text);
        Assert.False(viewer.IsSourceModified);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Removing_A_Row_Nobody_Wrote_Just_Takes_The_Row_Away()
    {
        var (window, viewer) = await HostLoaded(Two);

        AddLetButton(viewer).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var draft = Assert.Single(viewer.Lets, let => let.IsDraft);

        Remove(viewer, draft).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        // Nothing to splice: there was never anything in the document to take out.
        Assert.DoesNotContain(viewer.Lets, let => let.IsDraft);
        Assert.False(viewer.IsSourceModified);

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Row_Button_Is_What_Asks_For_It()
    {
        var (window, viewer) = await HostLoaded(Two);

        Remove(viewer, Row(viewer, "b")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();

        Assert.Equal(new[] { "a" }, viewer.Lets.Select(let => let.Name).ToArray());

        window.Close();
    }

    private static Button Remove(SvgViewer viewer, SvgViewerLet row)
        => viewer.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => ReferenceEquals(button.DataContext, row) && button.Content as string == "✕");
}
