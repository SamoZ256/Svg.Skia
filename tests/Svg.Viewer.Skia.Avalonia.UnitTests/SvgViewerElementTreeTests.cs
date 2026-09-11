using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Svg.SourceEditing;
using Svg.Expressions;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// The pane listing what the open drawing is made of.
/// </summary>
public class SvgViewerElementTreeTests
{
    private const string Markup = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code><e:param name="tint" type="color" default="#ff0000" /></e:code>
          </defs>
          <g id="wrap">
            <rect x="0" y="0" width="24" height="24" fill="{{ tint }}" />
            <text x="2" y="20">hi</text>
          </g>
        </svg>
        """;

    private static async Task<(Window Window, SvgViewer Viewer)> Host(string markup = Markup)
    {
        var viewer = new SvgViewer();
        var window = new Window { Width = 700, Height = 500, Background = Brushes.White, Content = viewer };

        window.Show();

        Assert.True(await viewer.LoadTextAsync(markup));
        Dispatcher.UIThread.RunJobs();

        return (window, viewer);
    }

    /// <summary>Compares rectangles the way a widened path produces them: to within a rounding.</summary>
    private static void AssertRect(SKRect expected, SKRect actual)
    {
        const float tolerance = 0.001f;

        Assert.True(
            Math.Abs(expected.Left - actual.Left) < tolerance
            && Math.Abs(expected.Top - actual.Top) < tolerance
            && Math.Abs(expected.Right - actual.Right) < tolerance
            && Math.Abs(expected.Bottom - actual.Bottom) < tolerance,
            $"{actual} is not {expected}");
    }

    /// <summary>Every row, in document order, as it reads.</summary>
    private static string[] Rows(SvgViewer viewer)
        => viewer.Elements.Root is { } root
            ? root.Flatten().Select(node => node.ToString()).ToArray()
            : System.Array.Empty<string>();

    [AvaloniaFact]
    public async Task Every_Element_Is_Listed_In_Document_Order()
    {
        // The whole document and not only what is drawn. Half of what a reader wants to find --
        // the declarations block, a gradient, a clip path -- never reaches the canvas at all.
        var (_, viewer) = await Host();

        Assert.Equal(
            new[] { "svg", "defs", "code", "param", "g #wrap", "rect", "text" },
            Rows(viewer));
    }

    [AvaloniaFact]
    public async Task The_Root_And_Its_Children_Start_Open()
    {
        var (_, viewer) = await Host();

        var root = viewer.Elements.Root!;

        Assert.True(root.IsExpanded);
        Assert.All(root.Children, child => Assert.True(child.IsExpanded));

        // And no further: a drawing of any size has more rows than the pane is tall.
        Assert.False(root.Children.Single(node => node.Label == "g").Children[0].IsExpanded);
    }

    [AvaloniaFact]
    public async Task A_Rebuild_From_Edited_Text_Updates_The_Tree()
    {
        // The trap. Rebuilding from the pane raises no DocumentOpened -- only opening a file does --
        // so a tree following that event alone would show the document as it was before the last
        // keystroke, for as long as somebody kept typing.
        var (_, viewer) = await Host();

        Dispatcher.UIThread.RunJobs();

        viewer.SetSource(Markup.Replace("</g>", "  <circle cx=\"12\" cy=\"12\" r=\"4\" />\n  </g>"));

        Assert.True(viewer.Rebuild());
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("circle", Rows(viewer));
    }

    [AvaloniaFact]
    public async Task The_Selection_Survives_A_Rebuild()
    {
        // By address and not by element: a rebuilt drawing shares no element with the one it
        // replaced, so anything holding a reference would lose the selection on every keystroke.
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect("1/0"));
        Assert.Equal("rect", viewer.Elements.SelectedNode!.Label);

        var before = viewer.Elements.SelectedNode.Element;

        Dispatcher.UIThread.RunJobs();

        viewer.SetSource(Markup.Replace("width=\"24\" height=\"24\" fill", "width=\"20\" height=\"20\" fill"));

        Assert.True(viewer.Rebuild());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("rect", viewer.Elements.SelectedNode!.Label);
        Assert.NotSame(before, viewer.Elements.SelectedNode.Element);
    }

    [AvaloniaFact]
    public async Task A_Selection_Whose_Element_Has_Gone_Is_Dropped()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect("1/1"));
        Assert.Equal("text", viewer.Elements.SelectedNode!.Label);

        Dispatcher.UIThread.RunJobs();

        viewer.SetSource(Markup.Replace("<text x=\"2\" y=\"20\">hi</text>", ""));

        Assert.True(viewer.Rebuild());
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewer.Elements.SelectedNode);
    }

    [AvaloniaFact]
    public async Task Filtering_Keeps_The_Matches_And_What_Is_Above_Them()
    {
        // A match with its ancestors cut off says where it is not.
        var (_, viewer) = await Host();

        viewer.Elements.Filter = "rect";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "svg", "g #wrap", "rect" }, Rows(viewer));
    }

    [AvaloniaFact]
    public async Task Filtering_Matches_An_Id_As_Well_As_A_Name()
    {
        var (_, viewer) = await Host();

        viewer.Elements.Filter = "wrap";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "svg", "g #wrap" }, Rows(viewer));
    }

    [AvaloniaFact]
    public async Task A_Filter_Opens_What_It_Keeps_And_Clearing_It_Folds_Back()
    {
        // A match three levels down behind a closed row makes the box look broken; and writing that
        // into what the reader had open would leave the tree unfolded once the box is empty again.
        var (_, viewer) = await Host();

        var group = viewer.Elements.Root!.Children.Single(node => node.Label == "g");

        group.IsExpanded = false;

        viewer.Elements.Filter = "rect";
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.Elements.Root!.Children.Single(node => node.Label == "g").IsExpanded);

        viewer.Elements.Filter = "";
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewer.Elements.Root!.Children.Single(node => node.Label == "g").IsExpanded);
    }

    [AvaloniaFact]
    public async Task A_Filter_That_Keeps_Nothing_Says_So()
    {
        var (_, viewer) = await Host();

        viewer.Elements.Filter = "nothing-is-called-this";
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewer.Elements.Root);
    }

    [AvaloniaFact]
    public async Task A_Selection_The_Filter_Hides_Comes_Back()
    {
        // Hidden is not deleted. Only an element that has gone from the document stops being the
        // selected one.
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect("1/0"));

        viewer.Elements.Filter = "text";
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewer.Elements.SelectedNode);

        viewer.Elements.Filter = "";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("rect", viewer.Elements.SelectedNode!.Label);
    }

    // ---- picking from the drawing --------------------------------------------------------------

    /// <summary>A document with a gap in it: two tiles, and empty space to the right of them.</summary>
    private const string Tiles = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 20" width="40" height="20">
          <rect x="0" y="0" width="10" height="10" fill="#ff0000" />
          <rect x="20" y="0" width="10" height="10" fill="#0000ff" />
        </svg>
        """;

    /// <summary>Lays the window out, so the canvas has a size and a scale to map through.</summary>
    private static void Arrange(Window window)
    {
        window.Measure(new Size(700, 500));
        window.Arrange(new Rect(0, 0, 700, 500));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Where in the control a point of the drawing is, checked by mapping it back.</summary>
    private static Point Over(SvgViewerCanvas canvas, double x, double y)
    {
        var at = new Point(x * canvas.Scale + canvas.OffsetX, y * canvas.Scale + canvas.OffsetY);

        Assert.True(canvas.TryGetDrawingPoint(at, out var back));
        Assert.Equal(x, back.X, 3);
        Assert.Equal(y, back.Y, 3);

        return at;
    }

    /// <summary>
    /// Where <paramref name="at"/> — a point in the canvas's own space — is in the window's.
    /// </summary>
    /// <remarks>
    /// A pointer event reports its position by way of the visual root, so a control-local point
    /// handed over as the root's is off by wherever the control sits. Under a toolbar and beside a
    /// side pane that is enough to land the click on a different shape, or on none.
    /// </remarks>
    private static Point Root(Window window, SvgViewerCanvas canvas, Point at)
        => canvas.TranslatePoint(at, window) ?? at;

    private static void Press(Window window, SvgViewerCanvas canvas, Point at)
        => canvas.RaiseEvent(new PointerPressedEventArgs(
            canvas,
            new Pointer(0, PointerType.Mouse, true),
            window,
            Root(window, canvas, at),
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None)
        {
            RoutedEvent = InputElement.PointerPressedEvent
        });

    private static void Move(Window window, SvgViewerCanvas canvas, Point at)
        => canvas.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent,
            canvas,
            new Pointer(0, PointerType.Mouse, true),
            window,
            Root(window, canvas, at),
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));

    private static void Release(Window window, SvgViewerCanvas canvas, Point at)
        => canvas.RaiseEvent(new PointerReleasedEventArgs(
            canvas,
            new Pointer(0, PointerType.Mouse, true),
            window,
            Root(window, canvas, at),
            0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None,
            MouseButton.Left)
        {
            RoutedEvent = InputElement.PointerReleasedEvent
        });

    private static void Click(Window window, SvgViewerCanvas canvas, Point at)
    {
        Press(window, canvas, at);
        Release(window, canvas, at);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Clicking_A_Shape_Selects_Its_Row()
    {
        var (window, viewer) = await Host(Tiles);

        Arrange(window);

        Click(window, viewer.Canvas, Over(viewer.Canvas, 25d, 5d));

        Assert.Equal("1", viewer.Elements.SelectedNode!.AddressKey);
        Assert.NotNull(viewer.Canvas.Highlight);
    }

    [AvaloniaFact]
    public async Task Dragging_The_Drawing_Picks_Nothing()
    {
        // A drag pans, and what it panned over is not what the reader meant to point at.
        var (window, viewer) = await Host(Tiles);

        Arrange(window);

        var from = Over(viewer.Canvas, 5d, 5d);

        Press(window, viewer.Canvas, from);
        Move(window, viewer.Canvas, from + new Point(60d, 0d));
        Release(window, viewer.Canvas, from + new Point(60d, 0d));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewer.Elements.SelectedNode);
    }

    [AvaloniaFact]
    public async Task Clicking_Nothing_Leaves_The_Selection_Alone()
    {
        // Clearing on a miss is the design tool's convention and the wrong one here: a click two
        // pixels off would throw away the row and the place in the text somebody was reading.
        var (window, viewer) = await Host(Tiles);

        Arrange(window);

        Click(window, viewer.Canvas, Over(viewer.Canvas, 5d, 5d));

        Assert.Equal("0", viewer.Elements.SelectedNode!.AddressKey);

        Click(window, viewer.Canvas, Over(viewer.Canvas, 35d, 15d));

        Assert.Equal("0", viewer.Elements.SelectedNode!.AddressKey);
    }

    // ---- ringing the element on the drawing ----------------------------------------------------

    [AvaloniaFact]
    public async Task Selecting_A_Shape_Rings_It_On_The_Drawing()
    {
        var (_, viewer) = await Host();

        Assert.Null(viewer.Canvas.Highlight);

        Assert.True(viewer.Elements.TrySelect("1/0"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new SKRect(0f, 0f, 24f, 24f), viewer.Canvas.Highlight!.Bounds);
    }

    /// <summary>
    /// The ring after a value moves the element it is already drawn around.
    /// </summary>
    /// <remarks>
    /// Picking rings it and binding moves it, and those are two different events: the ring was
    /// traced once, when the row was picked, and nothing retraced it when the drawing moved
    /// underneath. Every other test here picks after the value is set and so could not see it.
    /// </remarks>
    [AvaloniaFact]
    public async Task The_Ring_Follows_A_Bound_Transform_After_The_Element_Was_Picked()
    {
        const string driven = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 100 100" width="100" height="100">
              <defs><e:code><e:param name="dx" type="number" default="0" min="0" max="100" step="1" /></e:code></defs>
              <rect x="10" y="40" width="20" height="20" transform="translate({{ dx }}, 0)" fill="#ff0000" />
            </svg>
            """;

        var (_, viewer) = await Host(driven);

        Assert.True(viewer.Elements.TrySelect("1"));
        Dispatcher.UIThread.RunJobs();

        var picked = viewer.Canvas.Highlight!.Bounds;

        Assert.True(viewer.TrySetParameterValue("dx", ExprValue.Number(30f)));
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(400).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();

        // The row clamps to the range the parameter declares, which is why it declares one.
        Assert.Equal(picked.Left + 30f, viewer.Canvas.Highlight!.Bounds.Left, 3);
        Assert.Equal(picked.Top, viewer.Canvas.Highlight!.Bounds.Top, 3);
    }

    [AvaloniaFact]
    public async Task The_Ring_Follows_The_Shape_And_Not_The_Box_Around_It()
    {
        // The point of tracing geometry rather than bounds. A circle and the square it fits in have
        // the same bounds and look nothing alike, and two overlapping shapes ringed by their boxes
        // are indistinguishable.
        const string round = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20" width="20" height="20">
              <circle cx="10" cy="10" r="8" fill="#ff0000" />
              <text x="0" y="18" font-size="4">hi</text>
            </svg>
            """;

        var (_, viewer) = await Host(round);

        Assert.True(viewer.Elements.TrySelect("0"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new SKRect(2f, 2f, 18f, 18f), viewer.Canvas.Highlight!.Bounds);
        Assert.False(viewer.Canvas.Highlight.IsRect, "the circle was ringed as a rectangle");

        // Text carries no geometry, so its own measured bounds are the answer rather than an
        // approximation of one.
        Assert.True(viewer.Elements.TrySelect("1"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.Canvas.Highlight!.IsRect);
    }

    [AvaloniaFact]
    public async Task A_Stroked_Shape_Is_Ringed_Round_Its_Stroke()
    {
        // The geometry is the line a stroke is drawn along, not what gets drawn. Ringing it ran the
        // ring down the middle of the stroke, which at any real width hides the thing it points at.
        // This is the icon shape it was reported on: three points, round caps, no fill.
        const string stroked = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24" fill="none">
              <path stroke="#d14eb6" stroke-linecap="round" stroke-linejoin="round"
                    d="m13.5 8.5 -4 5 -2 -1.5" stroke-width="1" />
            </svg>
            """;

        var (_, viewer) = await Host(stroked);

        Assert.True(viewer.Elements.TrySelect("0"));
        Dispatcher.UIThread.RunJobs();

        var ring = viewer.Canvas.Highlight!;

        // The geometry spans 7.5,8.5 to 13.5,13.5. Half a unit of stroke on every side of it, round
        // caps included, is what the drawing actually covers.
        AssertRect(new SKRect(7f, 8f, 14f, 14f), ring.TightBounds);

        // Pinned either side of the edge, so a ring that ignored the width again would fail here
        // rather than merely looking wrong.
        Assert.True(ring.Contains(7.2f, 12f), "the width of the stroke is not inside the ring");
        Assert.False(ring.Contains(6.9f, 12f), "the ring is wider than the stroke it follows");
    }

    [AvaloniaFact]
    public async Task A_Filled_And_Stroked_Shape_Is_Ringed_Once_Round_The_Outside()
    {
        // Widening a stroke gives both of its edges, and the inner one falls inside a filled shape:
        // ringing it would draw a second line through the middle of a solid area.
        const string both = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 12 12" width="12" height="12">
              <rect x="2" y="2" width="8" height="8" fill="#00ff00" stroke="#000000" stroke-width="2" />
            </svg>
            """;

        var (_, viewer) = await Host(both);

        Assert.True(viewer.Elements.TrySelect("0"));
        Dispatcher.UIThread.RunJobs();

        var ring = viewer.Canvas.Highlight!;

        AssertRect(new SKRect(1f, 1f, 11f, 11f), ring.TightBounds);
        Assert.True(ring.Contains(6f, 6f), "the ring is the two edges of the stroke, not the outside");
    }

    [AvaloniaFact]
    public async Task A_Group_Is_Ringed_By_Its_Parts()
    {
        // Not by the box around them, which on a group of scattered children covers mostly nothing.
        const string scattered = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40" width="40" height="40">
              <g id="pair">
                <rect x="0" y="0" width="10" height="10" fill="#ff0000" />
                <rect x="30" y="30" width="10" height="10" fill="#0000ff" />
              </g>
            </svg>
            """;

        var (_, viewer) = await Host(scattered);

        Assert.True(viewer.Elements.TrySelect("0"));
        Dispatcher.UIThread.RunJobs();

        var ring = viewer.Canvas.Highlight!;

        // Both corners are covered, and the middle -- which the group's box would have ringed --
        // is not on the outline at all.
        Assert.Equal(new SKRect(0f, 0f, 40f, 40f), ring.Bounds);
        Assert.False(ring.IsRect, "the two shapes were ringed as one box");
        Assert.False(ring.Contains(20f, 20f), "the empty middle of the group was ringed");
    }

    [AvaloniaFact]
    public async Task Selecting_Something_That_Is_Never_Drawn_Rings_Nothing()
    {
        // Everything under <defs>, the <e:code> block, a <title>. The row still selects and is
        // still shown in the text; there is simply nothing on the canvas to point at.
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect("1/0"));
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(viewer.Canvas.Highlight);

        Assert.True(viewer.Elements.TrySelect("0/0/0"));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewer.Canvas.Highlight);
    }

    [AvaloniaFact]
    public async Task A_Used_Element_Is_Ringed_Wherever_It_Is_Drawn()
    {
        // Ringing the first scene node would point at a copy nobody picked.
        const string used = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 20" width="40" height="20">
              <defs><rect id="tile" width="10" height="10" /></defs>
              <use href="#tile" x="0" y="0" />
              <use href="#tile" x="20" y="0" />
            </svg>
            """;

        var (_, viewer) = await Host(used);

        // The <rect> inside <defs>, which is drawn twice and written once.
        Assert.True(viewer.Elements.TrySelect("0/0"));
        Dispatcher.UIThread.RunJobs();

        var ring = viewer.Canvas.Highlight!;

        Assert.Equal(new SKRect(0f, 0f, 30f, 10f), ring.Bounds);

        // Two tiles with a gap, not one box across both.
        Assert.False(ring.IsRect, "the two uses were ringed as one box");
        Assert.False(ring.Contains(15f, 5f), "the gap between the two uses was ringed");
    }

    [AvaloniaFact]
    public async Task Closing_The_Viewer_Clears_The_Ring()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect("1/0"));
        Dispatcher.UIThread.RunJobs();

        viewer.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewer.Canvas.Highlight);
    }

    // ---- moving a row --------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task A_Row_Dropped_Before_Another_Moves_There()
    {
        var (_, viewer) = await Host();

        // The text is written after the rect; dropped before it, the two swap.
        Assert.True(viewer.Elements.MoveRequested!("1/1", "1/0", SvgElementDrop.Before));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "svg", "defs", "code", "param", "g #wrap", "text", "rect" }, Rows(viewer));
    }

    [AvaloniaFact]
    public async Task A_Row_Dropped_Inside_A_Group_Moves_Into_It()
    {
        var (_, viewer) = await Host();

        // The rect out of #wrap and into the defs' code block is refused; into #wrap from outside
        // is the move worth pinning, so the text goes out first and then back in.
        Assert.True(viewer.Elements.MoveRequested!("1/1", "1", SvgElementDrop.After));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "svg", "defs", "code", "param", "g #wrap", "rect", "text" }, Rows(viewer));

        Assert.True(viewer.Elements.MoveRequested!("2", "1", SvgElementDrop.Inside));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "svg", "defs", "code", "param", "g #wrap", "rect", "text" }, Rows(viewer));
        Assert.Equal("1/1", viewer.Elements.Root!.Flatten().Single(node => node.Label == "text").AddressKey);
    }

    [AvaloniaFact]
    public async Task A_Row_Cannot_Be_Dropped_Into_Its_Own_Branch()
    {
        var (_, viewer) = await Host();

        var was = viewer.Source;

        Assert.False(viewer.Elements.MoveRequested!("1", "1/0", SvgElementDrop.After));

        Assert.Equal(was, viewer.Source);
    }

    [AvaloniaFact]
    public async Task A_New_Group_Is_Written_Beside_The_Picked_Row()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.NewGroupRequested!("1", SvgElementDrop.After));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("<g>", viewer.Source);
        Assert.Equal(new[] { "svg", "defs", "code", "param", "g #wrap", "rect", "text", "g" }, Rows(viewer));
    }

    /// <summary>
    /// A drawing built from the file rather than being it — an svgc recipe.
    /// </summary>
    /// <remarks>
    /// The tree lists the drawing, which is the rewritten document, and the pane edits the file. The
    /// two spell different addresses, so a move that took the tree's own key wrote somewhere else in
    /// the file — and the row that came back carried what the recipe had put into it.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Move_Under_A_Recipe_Writes_The_File_And_Not_What_Was_Made_Of_It()
    {
        const string file = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#ff0000" />
              <circle cx="12" cy="12" r="6" fill="#0000ff" />
            </svg>
            """;

        var viewer = new SvgViewer
        {
            // What a recipe does: a block of declarations in front, and a literal turned into an
            // expression that names one of them.
            Rewrite = svgText => svgText
                .Replace(
                    """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">""",
                    """
                    <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
                      <defs><e:code><e:param name="tint" type="color" default="#ff0000" /></e:code></defs>
                    """)
                .Replace("fill=\"#ff0000\"", "fill=\"{{ tint }}\"")
        };

        var window = new Window { Width = 700, Height = 500, Background = Brushes.White, Content = viewer };

        window.Show();
        // From a path, because that is the load a rewrite reaches — and is how a project opens one.
        var drawing = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".svg");

        File.WriteAllText(drawing, file);

        try
        {
            Assert.True(await viewer.LoadAsync(drawing));
        }
        finally
        {
            File.Delete(drawing);
        }

        Dispatcher.UIThread.RunJobs();

        // The tree lists the built drawing, so the recipe's own block is a row of it.
        Assert.Equal(new[] { "svg", "defs", "code", "param", "rect", "circle" }, Rows(viewer));

        Assert.True(viewer.Elements.MoveRequested!("2", "1", SvgElementDrop.Before));
        Dispatcher.UIThread.RunJobs();

        // The file's own two elements, swapped, with nothing the recipe writes anywhere in it.
        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="12" cy="12" r="6" fill="#0000ff" />
              <rect width="24" height="24" fill="#ff0000" />
            </svg>
            """,
            viewer.Source);
    }

    [AvaloniaFact]
    public async Task A_Tree_Nobody_Wired_Moves_Nothing()
    {
        // A project group's tab shows this tree over a drawing it has no text to edit.
        var tree = new SvgViewerElementTree();
        var window = new Window { Width = 300, Height = 300, Content = tree };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(tree.MoveRequested);
        Assert.Null(tree.NewGroupRequested);
    }

    [AvaloniaFact]
    public async Task Closing_The_Viewer_Empties_The_Tree()
    {
        var (_, viewer) = await Host();

        viewer.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewer.Elements.Root);
    }

    [AvaloniaFact]
    public async Task Hiding_The_Tree_Gives_Its_Height_Back()
    {
        // The row carries the height, so hiding the border alone would leave the parameters paying
        // for a strip of nothing -- the same trap the source pane has.
        var (window, viewer) = await Host();

        window.Measure(new global::Avalonia.Size(700, 500));
        window.Arrange(new global::Avalonia.Rect(0, 0, 700, 500));
        Dispatcher.UIThread.RunJobs();

        var panel = viewer.GetVisualDescendants().OfType<SvgViewerDeclarationPanel>().First();
        var before = panel.Bounds.Height;

        viewer.ShowElementTree = false;

        window.Measure(new global::Avalonia.Size(700, 500));
        window.Arrange(new global::Avalonia.Rect(0, 0, 700, 500));
        Dispatcher.UIThread.RunJobs();

        Assert.True(panel.Bounds.Height > before, $"{panel.Bounds.Height} is not more than {before}");
    }

    [AvaloniaFact]
    public async Task A_Hidden_Tree_Holds_Nothing_And_Fills_Again_When_Shown()
    {
        // What makes turning it off worth anything: the tree is rebuilt every time typing pauses,
        // and a host that hid the pane should not be paying for it.
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect("1/0"));
        Dispatcher.UIThread.RunJobs();

        viewer.ShowElementTree = false;

        Assert.Null(viewer.Elements.Root);
        Assert.Null(viewer.Canvas.Highlight);

        Assert.True(viewer.Rebuild());
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewer.Elements.Root);

        viewer.ShowElementTree = true;

        Assert.Equal(new[] { "svg", "defs", "code", "param", "g #wrap", "rect", "text" }, Rows(viewer));
    }

    [AvaloniaFact]
    public async Task The_Tree_Is_Shown_Unless_A_Host_Says_Otherwise()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.ShowElementTree);

        viewer.ShowElementTree = false;

        Assert.False(viewer.ShowElementTree);
    }
}
