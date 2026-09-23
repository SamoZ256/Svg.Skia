// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// Dragging the drawing's own edges.
/// </summary>
/// <remarks>
/// Dragging a page moves the drawing's edges and leaves what is drawn inside them alone — the other
/// thing from <c>Edit → Resize…</c>, which changes the size the drawing is and takes the ink with
/// it. So what these ask is that the room appeared on the side that was dragged: the viewport and
/// the viewBox move together, which is what holds the scale between them still.
/// </remarks>
public class SvgViewerPageTests
{
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect x="4" y="4" width="16" height="16" fill="#3366cc" />
        </svg>
        """;

    private static async Task<(Window Window, SvgViewer Viewer)> Host()
    {
        var viewer = new SvgViewer();
        var window = new Window { Width = 700, Height = 500, Background = Brushes.White, Content = viewer };

        window.Show();

        Assert.True(await viewer.LoadTextAsync(Drawing));
        Dispatcher.UIThread.RunJobs();

        window.Measure(new Size(700, 500));
        window.Arrange(new Rect(0, 0, 700, 500));
        Dispatcher.UIThread.RunJobs();

        return (window, viewer);
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
            MouseButton.Left));

    /// <summary>Selects the page by clicking it where none of its ink is.</summary>
    private static void SelectPage(Window window, SvgViewer viewer)
    {
        var at = Over(viewer.Canvas, 1d, 1d);

        Press(window, viewer.Canvas, at);
        Release(window, viewer.Canvas, at);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.IsPageSelected);
    }

    /// <summary>Drags from one point of the drawing to another, as a hand would.</summary>
    private static void Drag(Window window, SvgViewer viewer, Point from, Point to)
    {
        Press(window, viewer.Canvas, from);
        Dispatcher.UIThread.RunJobs();

        Move(window, viewer.Canvas, to);
        Dispatcher.UIThread.RunJobs();

        Release(window, viewer.Canvas, to);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task A_Selected_Page_Wears_Handles_And_No_Stalk()
    {
        var (window, viewer) = await Host();

        Assert.Null(viewer.Canvas.Gizmo);

        SelectPage(window, viewer);

        Assert.NotNull(viewer.Canvas.Gizmo);

        var box = viewer.Canvas.Gizmo!.Value;

        // The drawing's own edges, which is what a page is.
        Assert.Equal(0f, box.TL.X, 3);
        Assert.Equal(0f, box.TL.Y, 3);
        Assert.Equal(24f, box.BR.X, 3);
        Assert.Equal(24f, box.BR.Y, 3);

        // A page cannot be turned, so the stalk is not drawn for it.
        Assert.False(viewer.Canvas.GizmoTurns);
    }

    [AvaloniaFact]
    public async Task Dragging_A_Side_Writes_One_Dimension()
    {
        var (window, viewer) = await Host();

        SelectPage(window, viewer);

        // The right edge, halfway down it.
        Drag(window, viewer, Over(viewer.Canvas, 24d, 12d), Over(viewer.Canvas, 48d, 12d));

        Assert.Contains("width=\"48\"", viewer.Source, StringComparison.Ordinal);
        Assert.Contains("height=\"24\"", viewer.Source, StringComparison.Ordinal);

        // And the frame with it, which is what leaves the drawing the size it was: the page gained
        // 24 units of room on the right rather than the picture growing into them.
        Assert.Contains("viewBox=\"0 0 48 24\"", viewer.Source, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Dragging_A_Corner_Writes_Both()
    {
        var (window, viewer) = await Host();

        SelectPage(window, viewer);

        Drag(window, viewer, Over(viewer.Canvas, 24d, 24d), Over(viewer.Canvas, 48d, 36d));

        Assert.Contains("width=\"48\"", viewer.Source, StringComparison.Ordinal);
        Assert.Contains("height=\"36\"", viewer.Source, StringComparison.Ordinal);
        Assert.Contains("viewBox=\"0 0 48 36\"", viewer.Source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole drag is one thing to take back, and it is called what the dialog calls it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Drag_Is_One_Undo_Step()
    {
        var (window, viewer) = await Host();

        SelectPage(window, viewer);

        Drag(window, viewer, Over(viewer.Canvas, 24d, 12d), Over(viewer.Canvas, 48d, 12d));

        Assert.Equal("resize the page", viewer.UndoLabel);

        Assert.True(viewer.Undo());
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("width=\"24\"", viewer.Source, StringComparison.Ordinal);
        Assert.False(viewer.CanUndo);
    }

    /// <summary>A drag that ends where it began is not an edit.</summary>
    [AvaloniaFact]
    public async Task A_Drag_That_Came_To_Nothing_Writes_Nothing()
    {
        var (window, viewer) = await Host();

        SelectPage(window, viewer);

        var was = viewer.Source;

        Drag(window, viewer, Over(viewer.Canvas, 24d, 12d), Over(viewer.Canvas, 24d, 12d));

        Assert.Equal(was, viewer.Source);
        Assert.False(viewer.CanUndo);
        Assert.False(viewer.IsSourceModified);
    }

    /// <summary>
    /// A page dragged inside out stops at the least it can be rather than turning over.
    /// </summary>
    /// <remarks>
    /// The size model refuses anything that is not positive, so without a clamp the drag would throw
    /// — and a page of nothing has no handle left to drag it back out by.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Page_Dragged_Past_Itself_Stops_At_The_Least_It_Can_Be()
    {
        var (window, viewer) = await Host();

        SelectPage(window, viewer);

        Drag(window, viewer, Over(viewer.Canvas, 24d, 12d), Over(viewer.Canvas, -40d, 12d));

        Assert.Contains("width=\"1\"", viewer.Source, StringComparison.Ordinal);
        Assert.Contains("height=\"24\"", viewer.Source, StringComparison.Ordinal);

        // A page of one unit shows one unit of the drawing, rather than the whole of it shrunk.
        Assert.Contains("viewBox=\"0 0 1 24\"", viewer.Source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Dragging the left edge adds the room on the left, and the drawing does not move.
    /// </summary>
    /// <remarks>
    /// The case that proves which edge is the anchor. Growing the page leftwards has to move the
    /// frame's own origin with it, or the shape inside would slide across the page as it grew.
    /// </remarks>
    [AvaloniaFact]
    public async Task Dragging_The_Left_Edge_Adds_Room_On_The_Left()
    {
        var (window, viewer) = await Host();

        SelectPage(window, viewer);

        Drag(window, viewer, Over(viewer.Canvas, 0d, 12d), Over(viewer.Canvas, -24d, 12d));

        Assert.Contains("width=\"48\"", viewer.Source, StringComparison.Ordinal);

        // The frame starts 24 further left, so the rect written at x="4" is still 4 from the ink's
        // own origin rather than having slid into the middle of a wider page.
        Assert.Contains("viewBox=\"-24 0 48 24\"", viewer.Source, StringComparison.Ordinal);
    }

    /// <summary>Escape puts the page back, having written nothing on the way.</summary>
    [AvaloniaFact]
    public async Task Escape_Leaves_The_Page_The_Size_It_Was()
    {
        var (window, viewer) = await Host();

        SelectPage(window, viewer);

        var was = viewer.Source;

        Press(window, viewer.Canvas, Over(viewer.Canvas, 24d, 12d));
        Move(window, viewer.Canvas, Over(viewer.Canvas, 60d, 12d));
        Dispatcher.UIThread.RunJobs();

        viewer.Canvas.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Escape
        });

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(was, viewer.Source);

        // And the box is back on the edges the file says, not where the pointer left it.
        Assert.Equal(24f, viewer.Canvas.Gizmo!.Value.BR.X, 3);
    }
}
