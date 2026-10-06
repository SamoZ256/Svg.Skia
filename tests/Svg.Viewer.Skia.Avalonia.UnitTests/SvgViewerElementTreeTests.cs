using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Svg.Expressions;
using Svg.SourceEditing;
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

    /// <summary>A window height that leaves the element tree a row to aim at below the rows pinned over it, which at 500 it has not.</summary>
    private const double Room = 1000d;

    private static async Task<(Window Window, SvgViewer Viewer)> Host(string markup = Markup, double height = 500d)
    {
        var viewer = new SvgViewer();
        var window = new Window { Width = 700, Height = height, Background = Brushes.White, Content = viewer };

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

    /// <summary>
    /// Several rows can be selected at once, and the first of them is the one everything follows.
    /// </summary>
    /// <remarks>
    /// The anchor is what keeps every follower written for one row honest while the set grows: the
    /// property panel, the note line and the outline all still have exactly one row to read.
    /// </remarks>
    [AvaloniaFact]
    public async Task Several_Rows_Can_Be_Selected_At_Once()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect(new[] { "1/0", "1/1" }));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            "rect text",
            string.Join(" ", viewer.Elements.SelectedNodes.Select(node => node.Label)));

        Assert.Equal("rect", viewer.Elements.SelectedNode!.Label);
    }

    /// <summary>
    /// Selecting several rows says so once, with the set complete.
    /// </summary>
    /// <remarks>
    /// The control says it changed once per row, and empties the set before it fills it — so six
    /// rows told the host seven times, one of those with nothing selected. Every follower redrew on
    /// each, which is the flicker and the pane that blinks empty halfway through.
    /// </remarks>
    [AvaloniaFact]
    public async Task Selecting_Several_Rows_Says_So_Once()
    {
        var (_, viewer) = await Host();

        var said = 0;
        var seen = string.Empty;

        viewer.Elements.Selected += (_, _) =>
        {
            said++;
            seen = string.Join(" ", viewer.Elements.SelectedNodes.Select(node => node.Label));
        };

        Assert.True(viewer.Elements.TrySelect(new[] { "1/0", "1/1" }));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, said);
        Assert.Equal("rect text", seen);
    }

    /// <summary>
    /// A filter hides rows; it does not shrink what is selected.
    /// </summary>
    /// <remarks>
    /// A host reads what is selected to know what a drag will move. Answering that with what is on
    /// screen drops elements from the selection because of what somebody typed in a box.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Filter_Does_Not_Shrink_A_Selection()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect(new[] { "1/0", "1/1" }));
        Dispatcher.UIThread.RunJobs();

        viewer.Elements.Filter = "rect";
        Dispatcher.UIThread.RunJobs();

        // One row is on screen, and both are still selected.
        Assert.Equal("rect", string.Join(" ", viewer.Elements.SelectedNodes.Select(node => node.Label)));
        Assert.Equal("1/0 1/1", string.Join(" ", viewer.Elements.SelectedAddresses));
    }

    /// <summary>
    /// A drawing let go of takes its selection with it, whatever is typed in the filter.
    /// </summary>
    /// <remarks>
    /// A group builds one file several ways, so its drawings spell the same addresses for different
    /// shapes. Addresses left alive because a filter was typed came back on the next drawing and
    /// selected rows nobody picked.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Drawing_Let_Go_Of_Takes_Its_Selection_With_It()
    {
        var (_, viewer) = await Host();

        viewer.Elements.Filter = "rect";
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.Elements.TrySelect("1/0"));
        Dispatcher.UIThread.RunJobs();

        viewer.Elements.Forget();
        viewer.Elements.Show(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(viewer.Elements.SelectedAddresses);

        viewer.Elements.Show(viewer.Canvas.Svg?.SourceDocument);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(viewer.Elements.SelectedNodes);
    }

    /// <summary>A selection of several survives a rebuild, as one does.</summary>
    [AvaloniaFact]
    public async Task A_Selection_Of_Several_Survives_A_Rebuild()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect(new[] { "1/0", "1/1" }));
        Dispatcher.UIThread.RunJobs();

        viewer.SetSource(Markup.Replace("width=\"24\" height=\"24\" fill", "width=\"20\" height=\"20\" fill"));

        Assert.True(viewer.Rebuild());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            "rect text",
            string.Join(" ", viewer.Elements.SelectedNodes.Select(node => node.Label)));
    }

    /// <summary>A rebuild drops the rows that have gone and keeps the ones that have not.</summary>
    [AvaloniaFact]
    public async Task A_Rebuild_Drops_Only_The_Rows_That_Have_Gone()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect(new[] { "1/0", "1/1" }));
        Dispatcher.UIThread.RunJobs();

        viewer.SetSource(Markup.Replace("    <text x=\"2\" y=\"20\">hi</text>\n", string.Empty));

        Assert.True(viewer.Rebuild());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("rect", string.Join(" ", viewer.Elements.SelectedNodes.Select(node => node.Label)));
    }

    /// <summary>Restoring several rows tells whoever is listening once, not once per row.</summary>
    [AvaloniaFact]
    public async Task A_Rebuild_Restoring_Several_Rows_Says_So_Once()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect(new[] { "1/0", "1/1" }));
        Dispatcher.UIThread.RunJobs();

        var said = 0;

        viewer.Elements.Selected += (_, _) => said++;

        viewer.SetSource(Markup.Replace("width=\"24\" height=\"24\" fill", "width=\"20\" height=\"20\" fill"));

        Assert.True(viewer.Rebuild());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, said);
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
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
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

    /// <summary>
    /// A click that misses every shape but lands on the page selects the page.
    /// </summary>
    /// <remarks>
    /// This used to leave the selection alone, and the reason given was a good one: clearing on a
    /// near miss is the design tool's convention, and it would throw away the row and the place in
    /// the text somebody was reading. What changed is that a miss now lands on something — the page
    /// is a thing in its own right, the way a group's frame is on a board — so the row goes because
    /// something else was selected rather than because nothing was.
    /// </remarks>
    [AvaloniaFact]
    public async Task Clicking_The_Page_Off_Every_Shape_Selects_The_Page()
    {
        var (window, viewer) = await Host(Tiles);

        Arrange(window);

        Click(window, viewer.Canvas, Over(viewer.Canvas, 5d, 5d));

        Assert.Equal("0", viewer.Elements.SelectedNode!.AddressKey);
        Assert.False(viewer.IsPageSelected);

        // Inside the 40x20 page, and between the two tiles rather than on either.
        Click(window, viewer.Canvas, Over(viewer.Canvas, 35d, 15d));

        Assert.True(viewer.IsPageSelected);
        Assert.Null(viewer.Elements.SelectedNode);

        // And the ring is the page itself, which is the same shape a selected group wears.
        Assert.Equal(new SKRect(0f, 0f, 40f, 20f), viewer.Canvas.Highlight!.Bounds);
    }

    /// <summary>A click off the drawing altogether still leaves the selection alone.</summary>
    /// <remarks>
    /// The half of the old rule that keeps its reason. Clearing on a miss would throw away the row
    /// and the place in the text somebody was reading, and a click in the grey is not a request for
    /// anything — it is the page that is now selectable, not the board around it.
    ///
    /// Worth asserting rather than assuming, because the viewer could not tell this from a click on
    /// the page's own margin until the page answered a hit test: unprojecting a point succeeds
    /// anywhere on the control, so the two were one event.
    /// </remarks>
    [AvaloniaFact]
    public async Task Clicking_Off_The_Drawing_Leaves_The_Selection_Alone()
    {
        var (window, viewer) = await Host(Tiles);

        Arrange(window);

        Click(window, viewer.Canvas, Over(viewer.Canvas, 5d, 5d));

        Assert.Equal("0", viewer.Elements.SelectedNode!.AddressKey);

        Click(window, viewer.Canvas, Over(viewer.Canvas, -30d, -30d));

        Assert.Equal("0", viewer.Elements.SelectedNode!.AddressKey);
        Assert.False(viewer.IsPageSelected);
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

    [AvaloniaFact]
    public async Task A_Deleted_Row_Leaves_The_Drawing_And_The_Selection()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect("1/1"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.Elements.DeleteRequested!(viewer.Elements.SelectedAddresses.ToList()));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "svg", "defs", "code", "param", "g #wrap", "rect" }, Rows(viewer));
        Assert.DoesNotContain("<text", viewer.Source);
        // The expression beside it is the file's own bytes still.
        Assert.Contains("fill=\"{{ tint }}\"", viewer.Source);

        Assert.Empty(viewer.Elements.SelectedAddresses);
        Assert.Null(viewer.SelectedElement);
    }

    [AvaloniaFact]
    public async Task A_Duplicate_Is_Written_After_Its_Original_And_Selected()
    {
        var (_, viewer) = await Host();

        Assert.True(viewer.Elements.DuplicateRequested!(new[] { "1/0" }));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "svg", "defs", "code", "param", "g #wrap", "rect", "rect", "text" }, Rows(viewer));
        Assert.Equal(new[] { "1/1" }, viewer.Elements.SelectedAddresses);
        Assert.Equal(2, viewer.Source.Split("fill=\"{{ tint }}\"").Length - 1);
    }

    [AvaloniaFact]
    public async Task Deleting_A_Sweep_Of_Rows_From_The_Canvas_Is_One_Step_To_Take_Back()
    {
        var (window, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect(new[] { "1/0", "1/1" }));
        Dispatcher.UIThread.RunJobs();

        var was = viewer.Source;

        viewer.Canvas.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "svg", "defs", "code", "param", "g #wrap" }, Rows(viewer));

        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(was, viewer.Source);
    }

    [AvaloniaFact]
    public async Task The_Command_Key_With_D_Duplicates_What_Is_Picked()
    {
        var (window, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect("1/1"));
        viewer.Canvas.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "svg", "defs", "code", "param", "g #wrap", "rect", "text", "text" }, Rows(viewer));
        Assert.Equal(new[] { "1/2" }, viewer.Elements.SelectedAddresses);
    }

    [AvaloniaFact]
    public async Task A_Key_With_Nothing_Picked_Is_Not_The_Canvas_To_Answer()
    {
        var (window, viewer) = await Host();

        var was = viewer.Source;

        viewer.Canvas.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(was, viewer.Source);
    }

    /// <summary>
    /// A drawing built from the file rather than being it — an svgc recipe.
    /// </summary>
    /// <remarks>
    /// What a recipe does: a block of declarations in front, and a literal turned into an expression
    /// that names one of them. The tree lists the drawing, which is the rewritten document, and the
    /// pane edits the file. The two spell different addresses, so an edit that took the tree's own
    /// key would write somewhere else in the file.
    /// </remarks>
    private static async Task<SvgViewer> Recipe()
    {
        const string file = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#ff0000" />
              <circle cx="12" cy="12" r="6" fill="#0000ff" />
            </svg>
            """;

        var viewer = new SvgViewer
        {
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

        return viewer;
    }

    [AvaloniaFact]
    public async Task A_Duplicate_Under_A_Recipe_Writes_The_File_And_Follows_The_Copy()
    {
        var viewer = await Recipe();

        Assert.True(viewer.Elements.DuplicateRequested!(new[] { "2" }));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#ff0000" />
              <circle cx="12" cy="12" r="6" fill="#0000ff" />
              <circle cx="12" cy="12" r="6" fill="#0000ff" />
            </svg>
            """,
            viewer.Source);

        // The copy as the tree spells it, past the recipe's block.
        Assert.Equal(new[] { "3" }, viewer.Elements.SelectedAddresses);
    }

    [AvaloniaFact]
    public async Task A_Move_Under_A_Recipe_Writes_The_File_And_Not_What_Was_Made_Of_It()
    {
        var viewer = await Recipe();

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
        Assert.Null(tree.DeleteRequested);
        Assert.Null(tree.DuplicateRequested);
        Assert.Null(tree.ClipRequested);
        Assert.Null(tree.NewClipRequested);
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
        // for a strip of nothing.
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

    /// <summary>What clips or masks an element is said beside it, and the filter finds it by that.</summary>
    [AvaloniaFact]
    public async Task An_Element_Says_What_Clips_Or_Masks_It()
    {
        var (_, viewer) = await Host("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 30 30" width="30" height="30">
              <defs>
                <clipPath id="window"><rect width="10" height="10" /></clipPath>
                <mask id="sweep"><rect width="10" height="10" fill="#ffffff" /></mask>
              </defs>
              <rect width="20" height="20" clip-path="url(#window)" />
              <rect id="r" width="20" height="20" style="mask:url(#sweep)" />
              <rect width="20" height="20" clip-path="url(#missing)" />
            </svg>
            """);

        var rows = Rows(viewer);

        Assert.Contains("rect clip #window", rows);
        Assert.Contains("rect #r mask #sweep", rows);
        Assert.Contains("rect", rows);

        viewer.Elements.Filter = "clip #window";
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("rect clip #window", Rows(viewer));
        Assert.DoesNotContain("rect #r mask #sweep", Rows(viewer));
    }

    /// <summary>A mask named by clip-path, or a clip path named by mask, applies nothing, and says nothing.</summary>
    [AvaloniaFact]
    public async Task A_Reference_To_The_Wrong_Kind_Is_Not_Said()
    {
        var (_, viewer) = await Host("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 30 30" width="30" height="30">
              <defs>
                <clipPath id="window"><rect width="10" height="10" /></clipPath>
                <mask id="sweep"><rect width="10" height="10" fill="#ffffff" /></mask>
              </defs>
              <rect id="a" width="20" height="20" mask="url(#window)" />
              <rect id="b" width="20" height="20" clip-path="url(#sweep)" />
            </svg>
            """);

        var rows = Rows(viewer);

        Assert.Contains("rect #a", rows);
        Assert.Contains("rect #b", rows);
    }

    // ---- clipping and masking from the menu ----------------------------------------------------

    /// <summary>A moved rect, a group with one inside it, and a circle painted over both.</summary>
    private const string Clippable = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40" width="40" height="40">
          <rect id="a" transform="translate(6 2)" x="4" y="4" width="20" height="20" fill="#3366cc" stroke="#000000" stroke-width="2" />
          <g id="pair" transform="translate(10 10)"><rect id="b" width="10" height="10" fill="#cc3366" /></g>
          <circle id="spot" cx="20" cy="20" r="6" fill="#33cc66" />
        </svg>
        """;

    /// <summary>What a menu entry reads as: its header, or the text block standing in for it.</summary>
    private static string? Said(MenuItem item) => item.Header as string ?? (item.Header as TextBlock)?.Text;

    /// <summary>Opens the rows' menu as a right click does, and reads what it offers.</summary>
    private static MenuItem[] Offered(SvgViewer viewer)
    {
        var tree = viewer.Elements.FindControl<TreeView>("Tree")!;

        tree.RaiseEvent(new ContextRequestedEventArgs());
        Dispatcher.UIThread.RunJobs();

        var offered = tree.ContextMenu!.Items.OfType<MenuItem>().Where(item => item.IsVisible).ToArray();

        tree.ContextMenu.Close();
        Dispatcher.UIThread.RunJobs();

        return offered;
    }

    private static void Choose(SvgViewer viewer, string header)
    {
        var item = Offered(viewer).Single(item => Said(item) == header);

        Assert.True(item.IsEnabled);

        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static string WrittenOn(SvgViewer viewer, string id, string name)
        => SvgSourceDocument.Read(viewer.Source, out _)!.Document.Descendants()
            .Single(element => (string?)element.Attribute("id") == id)
            .Attribute(name)?.Value ?? "-";

    /// <summary>The seed covers the row in its own space, is selected, and has one user, so it is held and reshaped on the row.</summary>
    [AvaloniaFact]
    public async Task A_New_Clip_Path_Covers_The_Row_And_Its_Seed_Is_Held_There()
    {
        var (window, viewer) = await Host(Clippable);

        Assert.True(viewer.Elements.TrySelect("0"));
        Dispatcher.UIThread.RunJobs();

        Choose(viewer, "New clip path");

        Assert.Equal("url(#a-clip)", WrittenOn(viewer, "a", "clip-path"));
        Assert.Contains("""<clipPath id="a-clip"><path d="M 3 3 L 25 3 L 25 25 L 3 25 Z" /></clipPath>""", viewer.Source.Replace("\n", "").Replace("  ", ""));
        Assert.IsType<SvgPath>(viewer.SelectedElement);

        var box = viewer.Canvas.Gizmo!.Value;

        Assert.Equal(9f, box.TL.X, 1);
        Assert.Equal(5f, box.TL.Y, 1);
        Assert.Equal(31f, box.BR.X, 1);

        viewer.Canvas.Focus();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            new[] { new SKPoint(9f, 5f), new SKPoint(31f, 5f), new SKPoint(31f, 27f), new SKPoint(9f, 27f) },
            viewer.Canvas.Points!.Anchors);

        Arrange(window);

        Press(window, viewer.Canvas, Over(viewer.Canvas, 31d, 5d));
        Move(window, viewer.Canvas, Over(viewer.Canvas, 35d, 9d));
        Release(window, viewer.Canvas, Over(viewer.Canvas, 35d, 9d));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("M3 3 L29 7 L25 25 L3 25 Z", SvgSourceDocument.Read(viewer.Source, out _)!.Document.Descendants()
            .Single(element => element.Name.LocalName == "path").Attribute("d")!.Value);
    }

    [AvaloniaFact]
    public async Task A_New_Mask_Starts_White()
    {
        var (_, viewer) = await Host(Clippable);

        Assert.True(viewer.Elements.TrySelect("0"));
        Dispatcher.UIThread.RunJobs();

        Choose(viewer, "New mask");

        Assert.Equal("url(#a-mask)", WrittenOn(viewer, "a", "mask"));
        Assert.Contains(
            """<mask id="a-mask" maskUnits="userSpaceOnUse" x="3" y="3" width="22" height="22"><path d="M 3 3 L 25 3 L 25 25 L 3 25 Z" fill="white" /></mask>""",
            viewer.Source.Replace("\n", "").Replace("  ", ""));
    }

    /// <summary>A straight line has no bounding box to show a mask in, so a new one shows the box round its stroke instead.</summary>
    [AvaloniaFact]
    public async Task A_New_Mask_On_A_Straight_Line_Still_Shows_It()
    {
        var (_, viewer) = await Host("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <path id="bar" d="M3 12h18" stroke="#000000" stroke-width="1.5" />
            </svg>
            """);

        Assert.True(viewer.Elements.TrySelect("0"));
        Dispatcher.UIThread.RunJobs();

        Choose(viewer, "New mask");

        Assert.Equal("url(#bar-mask)", WrittenOn(viewer, "bar", "mask"));

        var svg = new Svg.Skia.SKSvg();

        Assert.NotNull(svg.FromSvg(viewer.Source));

        using var bitmap = new SkiaSharp.SKBitmap(24, 24);

        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(SkiaSharp.SKColors.Transparent);
            canvas.DrawPicture(svg.Picture);
        }

        Assert.True(bitmap.GetPixel(12, 11).Alpha > 0);
    }

    /// <summary>The drawing itself has no place of its own to cover, and an &lt;svg&gt; is clipped but never drawn masked.</summary>
    [AvaloniaFact]
    public async Task New_Clip_Path_And_New_Mask_Are_Offered_Only_Where_They_Would_Be_Drawn()
    {
        var (_, viewer) = await Host("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40" width="40" height="40">
              <svg id="inner" width="20" height="20"><rect width="20" height="20" fill="#3366cc" /></svg>
            </svg>
            """);

        Assert.True(viewer.Elements.TrySelect(string.Empty));
        Dispatcher.UIThread.RunJobs();

        Assert.All(
            Offered(viewer).Where(item => Said(item) is "New clip path" or "New mask"),
            item => Assert.False(item.IsEnabled));

        Assert.True(viewer.Elements.TrySelect("0"));
        Dispatcher.UIThread.RunJobs();

        var offered = Offered(viewer);

        Assert.True(offered.Single(item => Said(item) == "New clip path").IsEnabled);
        Assert.False(offered.Single(item => Said(item) == "New mask").IsEnabled);
    }

    /// <summary>Whichever was picked first, the shape is the row painted later: a sibling written after, or a child of the group.</summary>
    [AvaloniaFact]
    public async Task Clip_With_Takes_The_Row_Painted_Last_As_The_Shape()
    {
        var (_, viewer) = await Host(Clippable);

        Assert.True(viewer.Elements.TrySelect(new[] { "1/0", "1" }));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("Clip g #pair with rect #b", Offered(viewer).Select(Said));

        Assert.True(viewer.Elements.TrySelect(new[] { "2", "0" }));
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(Offered(viewer), item => Said(item) == "New clip path" && item.IsEnabled);

        Choose(viewer, "Clip rect #a with circle #spot");

        Assert.Equal("url(#a-clip)", WrittenOn(viewer, "a", "clip-path"));
        Assert.Equal("clipPath", SvgSourceDocument.Read(viewer.Source, out _)!.Document.Descendants()
            .Single(element => (string?)element.Attribute("id") == "spot").Parent!.Name.LocalName);
        Assert.Equal("spot", viewer.SelectedElement?.ID);
    }

    /// <summary>
    /// A clip path, a mask or a shape in &lt;defs&gt; picked with a drawn row is what clips it, though
    /// &lt;defs&gt; comes first; a clip path only clips and a mask only masks.
    /// </summary>
    [AvaloniaFact]
    public async Task Clip_With_Takes_What_Is_Kept_Off_The_Canvas_As_What_Clips()
    {
        var (_, viewer) = await Host(Kept);

        string?[] Picked(params string[] keys)
        {
            Assert.True(viewer.Elements.TrySelect(keys));
            Dispatcher.UIThread.RunJobs();

            return Offered(viewer).Select(Said).Where(said => said is { } && said.Contains(" with ", StringComparison.Ordinal)).ToArray();
        }

        Assert.Equal(new[] { "Clip rect #a with clipPath #window" }, Picked("0/0", "1"));
        Assert.Equal(new[] { "Mask rect #a with mask #sweep" }, Picked("1", "0/1"));

        // Both kept off the canvas: the earlier is what would be clipped, and a clip path is not drawn.
        Assert.Empty(Picked("0/0", "0/2"));

        Assert.Equal(new[] { "Clip rect #a with circle #spare", "Mask rect #a with circle #spare" }, Picked("0/2", "1"));

        Choose(viewer, "Mask rect #a with circle #spare");

        Assert.Equal("url(#a-mask)", WrittenOn(viewer, "a", "mask"));
        Assert.Equal("spare", viewer.SelectedElement?.ID);

        Picked("0/0", "1");
        Choose(viewer, "Clip rect #a with clipPath #window");

        Assert.Equal("url(#window)", WrittenOn(viewer, "a", "clip-path"));
    }

    /// <summary>A &lt;use&gt; of a symbol clips with nothing, so it is offered, and dropped, only as a mask.</summary>
    [AvaloniaFact]
    public async Task A_Use_Of_A_Symbol_In_Defs_Is_Only_A_Mask()
    {
        var (window, viewer) = await Host("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40" width="40" height="40">
              <defs><symbol id="icon"><circle cx="5" cy="5" r="5" /></symbol><use id="badge" href="#icon" /></defs>
              <rect id="a" width="20" height="20" fill="#3366cc" />
            </svg>
            """, height: Room);

        Assert.True(viewer.Elements.TrySelect(new[] { "0/1", "1" }));
        Dispatcher.UIThread.RunJobs();

        var offered = Offered(viewer).Select(Said).ToArray();

        Assert.Contains("Mask rect #a with use #badge", offered);
        Assert.DoesNotContain("Clip rect #a with use #badge", offered);

        var asked = Asked(viewer.Elements);

        Arrange(window);

        Carry(window, SvgViewerElementTree.Carrying("0/1", "mask"), Aim(window, Row(viewer.Elements, "1"), 0.5));

        Assert.Equal(new[] { "0/1 1 mask" }, asked);
    }

    /// <summary>Moved into the space of what it masks, a shape is carried back by that space's move and stays where it was.</summary>
    [AvaloniaFact]
    public async Task A_Shape_Masked_With_Is_Ringed_Where_It_Was_Drawn()
    {
        var (_, viewer) = await Host(Clippable);

        Assert.True(viewer.Elements.TrySelect("2"));
        Dispatcher.UIThread.RunJobs();

        var was = viewer.Canvas.Highlight!.Bounds;

        Assert.True(viewer.Elements.TrySelect(new[] { "1/0", "2" }));
        Dispatcher.UIThread.RunJobs();

        Choose(viewer, "Mask rect #b with circle #spot");

        Assert.Equal("url(#b-mask)", WrittenOn(viewer, "b", "mask"));
        Assert.Equal("translate(-10, -10)", WrittenOn(viewer, "spot", "transform"));
        Assert.Equal("spot", viewer.SelectedElement?.ID);
        AssertRect(was, viewer.Canvas.Highlight!.Bounds);
    }

    // ---- clipping and masking by dropping a row ------------------------------------------------

    /// <summary>A clip path, a mask and a spare circle kept in &lt;defs&gt;, a rect drawn, and a group with one in it.</summary>
    private const string Kept = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40" width="40" height="40">
          <defs>
            <clipPath id="window"><rect x="5" y="5" width="20" height="20" /></clipPath>
            <mask id="sweep"><rect width="40" height="40" fill="#ffffff" /></mask>
            <circle id="spare" cx="20" cy="20" r="8" />
          </defs>
          <rect id="a" x="4" y="4" width="20" height="20" fill="#3366cc" />
          <g id="pair"><rect id="b" x="10" y="10" width="10" height="10" fill="#cc3366" /></g>
        </svg>
        """;

    /// <summary>The row at <paramref name="key"/> as the tree draws it, scrolled to.</summary>
    private static TreeViewItem Row(SvgViewerElementTree tree, string key)
    {
        var row = tree.GetVisualDescendants().OfType<TreeViewItem>()
            .Single(item => (item.DataContext as SvgViewerElementNode)?.AddressKey == key);

        row.BringIntoView();
        Dispatcher.UIThread.RunJobs();

        // Drawn twice, because a drop is hit-tested against the scene as last drawn and a headless
        // window only draws when told: one tick still finds the rows where they were before the scroll.
        for (var tick = 0; tick < 2; tick++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        return row;
    }

    /// <summary>Where in the window the row's name is, a fraction of the way down the row itself rather than its branch.</summary>
    private static Point Aim(Window window, TreeViewItem row, double down)
    {
        var name = row.GetVisualDescendants().OfType<TextBlock>().First();
        var header = row.GetTemplateDescendants().OfType<Control>().Single(part => part.Name == "PART_Header");
        var at = header.TranslatePoint(new Point(0, header.Bounds.Height * down), window);
        var across = name.TranslatePoint(new Point(name.Bounds.Width / 2d, 0), window);

        Assert.NotNull(at);
        Assert.NotNull(across);

        return new Point(across!.Value.X, at!.Value.Y);
    }

    /// <summary>Carries <paramref name="carried"/> to a point of the window and lets it go there, unless told not to.</summary>
    /// <remarks>
    /// Injected rather than started, as the element panel's suite does it: no drag source runs
    /// headlessly, and the tree reads the row from what the drag carries.
    /// </remarks>
    private static void Carry(
        Window window,
        IDataTransfer carried,
        Point at,
        RawInputModifiers keys = RawInputModifiers.None,
        bool drop = true,
        DragDropEffects effects = DragDropEffects.Move | DragDropEffects.Link)
    {
        var stages = drop
            ? new[] { RawDragEventType.DragEnter, RawDragEventType.DragOver, RawDragEventType.Drop }
            : new[] { RawDragEventType.DragEnter, RawDragEventType.DragOver };

        foreach (var stage in stages)
        {
            window.DragDrop(at, stage, carried, effects, keys);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Records what the tree asks to clip, as content, target and property, and writes nothing.</summary>
    private static List<string> Asked(SvgViewerElementTree tree)
    {
        var asked = new List<string>();

        tree.ClipRequested = (content, target, property) =>
        {
            asked.Add($"{content} {target} {property}");

            return true;
        };

        return asked;
    }

    /// <summary>A clip path from &lt;defs&gt; applies over the whole of a row, even the quarter that would put a row before it; on a group, the middle applies rather than going in.</summary>
    [AvaloniaFact]
    public async Task A_Clip_Path_Dropped_On_A_Drawn_Row_Is_Applied_As_One()
    {
        var (window, viewer) = await Host(Kept, height: Room);

        Arrange(window);

        Carry(window, SvgViewerElementTree.Carrying("0/0", "clip-path"), Aim(window, Row(viewer.Elements, "1"), 0.1));

        Assert.Equal("url(#window)", WrittenOn(viewer, "a", "clip-path"));
        Assert.Equal("a", viewer.SelectedElement?.ID);

        Carry(window, SvgViewerElementTree.Carrying("0/1", "mask"), Aim(window, Row(viewer.Elements, "2"), 0.5));

        Assert.Equal("url(#sweep)", WrittenOn(viewer, "pair", "mask"));
        Assert.Equal(
            new[] { "svg", "defs", "clipPath #window", "rect", "mask #sweep", "rect", "circle #spare", "rect #a clip #window", "g #pair mask #sweep", "rect #b" },
            Rows(viewer));
    }

    /// <summary>A clip path written beside the shapes can still be moved among them, so only the middle of a row applies it.</summary>
    [AvaloniaFact]
    public async Task A_Clip_Path_Written_Beside_The_Shapes_Moves_On_A_Rows_Edges()
    {
        var (window, viewer) = await Host("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40" width="40" height="40">
              <clipPath id="loose"><rect width="20" height="20" /></clipPath>
              <rect id="a" width="30" height="30" fill="#3366cc" />
            </svg>
            """, height: Room);
        var asked = Asked(viewer.Elements);
        var moved = new List<string>();

        viewer.Elements.MoveRequested = (dragged, target, where) =>
        {
            moved.Add($"{dragged} {target} {where}");

            return true;
        };

        Arrange(window);

        Carry(window, SvgViewerElementTree.Carrying("0", "clip-path"), Aim(window, Row(viewer.Elements, "1"), 0.1));
        Carry(window, SvgViewerElementTree.Carrying("0", "clip-path"), Aim(window, Row(viewer.Elements, "1"), 0.5));
        Carry(window, SvgViewerElementTree.Carrying("0", "clip-path"), Aim(window, Row(viewer.Elements, "1"), 0.9));

        Assert.Equal(new[] { "0 1 Before", "0 1 After" }, moved);
        Assert.Equal(new[] { "0 1 clip-path" }, asked);
    }

    /// <summary>A shape kept in &lt;defs&gt; could be either, and is a mask while ⌥ is down as it is let go, whatever it was on the way.</summary>
    [AvaloniaFact]
    public async Task Holding_Option_Makes_A_Spare_Shape_A_Mask_Instead()
    {
        var (window, viewer) = await Host(Kept, height: Room);
        var asked = Asked(viewer.Elements);

        Arrange(window);

        var spare = SvgViewerElementTree.Carrying("0/2", "clip-path", "mask");
        var at = Aim(window, Row(viewer.Elements, "1"), 0.5);

        Carry(window, spare, at);
        Carry(window, spare, at, RawInputModifiers.Alt);

        Carry(window, spare, at, drop: false);
        window.DragDrop(at, RawDragEventType.Drop, spare, DragDropEffects.Move | DragDropEffects.Link, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "0/2 1 clip-path", "0/2 1 mask", "0/2 1 mask" }, asked);
    }

    /// <summary>
    /// macOS narrows what a drag offers to a copy while ⌥ is held and turns down any other answer,
    /// so an apply answers a copy where no link is offered, and a link where one is.
    /// </summary>
    [AvaloniaFact]
    public void An_Apply_Answers_A_Copy_Where_The_Drag_Offers_No_Link()
    {
        var tree = new SvgViewerElementTree();
        var window = new Window { Width = 300, Height = 500, Content = tree };
        var answered = new List<DragDropEffects>();

        window.Show();
        tree.Show(SvgDocument.FromSvg<SvgDocument>(Kept));
        Dispatcher.UIThread.RunJobs();

        window.AddHandler(DragDrop.DragOverEvent, (_, e) => answered.Add(e.DragEffects), RoutingStrategies.Bubble, handledEventsToo: true);

        var asked = Asked(tree);
        var spare = SvgViewerElementTree.Carrying("0/2", "clip-path", "mask");
        var at = Aim(window, Row(tree, "1"), 0.5);

        Carry(window, spare, at, RawInputModifiers.Alt, effects: DragDropEffects.Copy);

        Assert.Equal(DragDropEffects.Copy, answered.Last());

        Carry(window, spare, at, effects: DragDropEffects.Move | DragDropEffects.Link | DragDropEffects.Copy);

        Assert.Equal(DragDropEffects.Link, answered.Last());
        Assert.Equal(new[] { "0/2 1 mask", "0/2 1 clip-path" }, asked);
    }

    /// <summary>A mask on an &lt;svg&gt; is never drawn, and the drawing itself has no place of its own, so neither row takes one.</summary>
    [AvaloniaFact]
    public void A_Spare_Shape_Masks_No_Svg_And_Applies_Nothing_To_The_Drawing_Itself()
    {
        var tree = new SvgViewerElementTree();
        var window = new Window { Width = 300, Height = 500, Content = tree };

        window.Show();
        tree.Show(SvgDocument.FromSvg<SvgDocument>("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40" width="40" height="40">
              <defs><circle id="spare" cx="5" cy="5" r="4" /></defs>
              <svg id="inner" width="20" height="20"><rect width="20" height="20" fill="#3366cc" /></svg>
            </svg>
            """));
        Dispatcher.UIThread.RunJobs();

        var asked = Asked(tree);
        var spare = SvgViewerElementTree.Carrying("0/0", "clip-path", "mask");

        Carry(window, spare, Aim(window, Row(tree, "1"), 0.5), RawInputModifiers.Alt);
        Carry(window, spare, Aim(window, Row(tree, string.Empty), 0.5));
        Carry(window, spare, Aim(window, Row(tree, "1"), 0.5));
        Carry(window, spare, Aim(window, Row(tree, "1/0"), 0.5), RawInputModifiers.Alt);

        Assert.Equal(new[] { "0/0 1 clip-path", "0/0 1/0 mask" }, asked);
    }

    /// <summary>The box a drop would apply in is the canvas's green for a clip and violet for a mask, says which, and follows ⌥; a move is still a blue line.</summary>
    [AvaloniaFact]
    public async Task The_Row_Says_Clip_Or_Mask_As_Option_Is_Pressed_And_Let_Go()
    {
        var (window, viewer) = await Host(Kept, height: Room);
        var line = viewer.Elements.FindControl<Border>("DropLine")!;
        var word = viewer.Elements.FindControl<TextBlock>("DropWord")!;

        Arrange(window);

        var spare = SvgViewerElementTree.Carrying("0/2", "clip-path", "mask");
        var at = Aim(window, Row(viewer.Elements, "1"), 0.5);

        Carry(window, spare, at, drop: false);

        Assert.True(line.IsVisible);
        Assert.True(word.IsVisible);
        Assert.Equal("clip", word.Text);
        Assert.Equal(Color.Parse("#3FB950"), Assert.IsAssignableFrom<ISolidColorBrush>(line.BorderBrush).Color);

        window.DragDrop(at, RawDragEventType.DragOver, spare, DragDropEffects.Move | DragDropEffects.Link, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("mask", word.Text);
        Assert.Equal(Color.Parse("#9B6CFF"), Assert.IsAssignableFrom<ISolidColorBrush>(line.BorderBrush).Color);

        window.DragDrop(at, RawDragEventType.DragOver, spare, DragDropEffects.Move | DragDropEffects.Link, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("clip", word.Text);

        // Leaving for another control takes it down.
        window.DragDrop(at, RawDragEventType.DragLeave, spare, DragDropEffects.Move | DragDropEffects.Link, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(line.IsVisible);

        // Among the rows it is kept with, it moves.
        Carry(window, spare, Aim(window, Row(viewer.Elements, "0/0"), 0.1), drop: false);

        Assert.True(line.IsVisible);
        Assert.False(word.IsVisible);
        Assert.Equal(2d, line.Height);
        Assert.Equal(Color.Parse("#4C9BE8"), Assert.IsAssignableFrom<ISolidColorBrush>(line.BorderBrush).Color);
    }

    /// <summary>
    /// Pressing a row picks it, which would turn the element panel away from what it is to be dropped
    /// on; picking it up puts back what was picked, so its Clip path box takes the drop.
    /// </summary>
    [AvaloniaFact]
    public async Task Dragging_A_Clip_Path_Leaves_The_Picked_Element_Picked()
    {
        var (window, viewer) = await Host(Kept, height: Room);

        window.Width = 900;
        window.Height = 700;

        Assert.True(viewer.Elements.TrySelect("1"));
        Dispatcher.UIThread.RunJobs();

        var at = Aim(window, Row(viewer.Elements, "0/0"), 0.5);

        window.MouseDown(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "0/0" }, viewer.Elements.SelectedAddresses);

        window.MouseMove(at + new Point(0, 12), RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(at + new Point(0, 12), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "1" }, viewer.Elements.SelectedAddresses);
        Assert.Equal("a", viewer.SelectedElement?.ID);

        var box = window.GetVisualDescendants().OfType<TextBox>().Single(box => Equals(box.Tag, "clip-path"));

        box.BringIntoView();
        Dispatcher.UIThread.RunJobs();

        Carry(window, SvgViewerElementTree.Carrying("0/0", "clip-path"), box.TranslatePoint(new Point(box.Bounds.Width / 2d, box.Bounds.Height / 2d), window)!.Value);

        Assert.Equal("url(#window)", WrittenOn(viewer, "a", "clip-path"));
    }

    /// <summary>
    /// Typing in the filter to find the clip path hides the picked row, which is still picked; picking
    /// the clip path up puts that back, and the panel with it, though there is no row to select.
    /// </summary>
    [AvaloniaFact]
    public async Task Dragging_A_Clip_Path_Found_By_The_Filter_Leaves_The_Hidden_Element_Picked()
    {
        var (window, viewer) = await Host(Kept, height: Room);

        window.Width = 900;
        window.Height = 700;

        Assert.True(viewer.Elements.TrySelect("1"));
        Dispatcher.UIThread.RunJobs();

        viewer.Elements.Filter = "clip";
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(viewer.Elements.GetVisualDescendants().OfType<TreeViewItem>(), item => (item.DataContext as SvgViewerElementNode)?.AddressKey == "1");

        var at = Aim(window, Row(viewer.Elements, "0/0"), 0.5);

        window.MouseDown(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(at + new Point(0, 12), RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(at + new Point(0, 12), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "1" }, viewer.Elements.SelectedAddresses);

        var box = window.GetVisualDescendants().OfType<TextBox>().Single(box => Equals(box.Tag, "clip-path"));

        box.BringIntoView();
        Dispatcher.UIThread.RunJobs();

        Carry(window, SvgViewerElementTree.Carrying("0/0", "clip-path"), box.TranslatePoint(new Point(box.Bounds.Width / 2d, box.Bounds.Height / 2d), window)!.Value);

        Assert.Equal("url(#window)", WrittenOn(viewer, "a", "clip-path"));
    }

    /// <summary>Row 1 beside row 10: an address that begins with another's is not in its branch unless a slash follows.</summary>
    [AvaloniaFact]
    public async Task A_Row_Can_Land_Beside_One_Whose_Address_Begins_With_Its_Own()
    {
        var (window, viewer) = await Host($"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 12 1" width="12" height="1">
            {string.Concat(Enumerable.Range(0, 12).Select(i => $"<rect id=\"r{i}\" x=\"{i}\" width=\"1\" height=\"1\" />"))}
            </svg>
            """, height: Room);

        Arrange(window);

        Carry(window, SvgViewerElementTree.Carrying("1"), Aim(window, Row(viewer.Elements, "10"), 0.1));

        Assert.Equal(
            new[] { "r0", "r2", "r3", "r4", "r5", "r6", "r7", "r8", "r9", "r1", "r10", "r11" },
            SvgSourceDocument.Read(viewer.Source, out _)!.Document.Root!.Elements().Select(element => (string?)element.Attribute("id")));
    }

    /// <summary>The bands are the row's own and not its open branch's, so the foot of a group's row is after the group.</summary>
    [AvaloniaFact]
    public async Task A_Row_Dropped_On_The_Foot_Of_An_Open_Group_Lands_After_It()
    {
        var (window, viewer) = await Host(Kept, height: Room);

        Arrange(window);

        Carry(window, SvgViewerElementTree.Carrying("1"), Aim(window, Row(viewer.Elements, "2"), 0.9));

        var moved = SvgSourceDocument.Read(viewer.Source, out _)!.Document.Descendants().Single(element => (string?)element.Attribute("id") == "a");

        Assert.Equal("svg", moved.Parent!.Name.LocalName);
        Assert.Equal("pair", (string?)moved.ElementsBeforeSelf().Last().Attribute("id"));
    }

    /// <summary>A board's tree moves nothing, so a drawn row lands nowhere; a clip path still applies, and is picked up with the selection kept.</summary>
    [AvaloniaFact]
    public async Task A_Tree_That_Moves_Nothing_Still_Takes_A_Clip_Path_Dropped_On_A_Row()
    {
        var tree = new SvgViewerElementTree();
        var window = new Window { Width = 300, Height = 500, Content = tree };

        window.Show();
        tree.Show(SvgDocument.FromSvg<SvgDocument>(Kept));
        Dispatcher.UIThread.RunJobs();

        var asked = Asked(tree);
        var line = tree.FindControl<Border>("DropLine")!;

        Carry(window, SvgViewerElementTree.Carrying("1"), Aim(window, Row(tree, "2"), 0.1), drop: false);

        Assert.False(line.IsVisible);

        Carry(window, SvgViewerElementTree.Carrying("1"), Aim(window, Row(tree, "2"), 0.1));
        Carry(window, SvgViewerElementTree.Carrying("0/0", "clip-path"), Aim(window, Row(tree, "1"), 0.1));

        Assert.Equal(new[] { "0/0 1 clip-path" }, asked);

        Assert.True(tree.TrySelect("1"));
        Dispatcher.UIThread.RunJobs();

        var at = Aim(window, Row(tree, "0/0"), 0.5);

        window.MouseDown(at, MouseButton.Left);
        window.MouseMove(at + new Point(0, 12), RawInputModifiers.LeftMouseButton);
        window.MouseUp(at + new Point(0, 12), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "1" }, tree.SelectedAddresses);
    }
}
