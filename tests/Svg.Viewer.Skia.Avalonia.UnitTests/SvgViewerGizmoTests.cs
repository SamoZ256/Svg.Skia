// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// Dragging an element about the drawing.
/// </summary>
/// <remarks>
/// Through the pointer rather than against the gizmo directly: what is being checked is the whole
/// path from a press on a pixel to a transform in the text, and the two arithmetics worth doubting —
/// the space a delta is measured in, and the corner a scale turns about — are both in the middle of
/// it.
///
/// The numbers are exact. A drag of twenty units is twenty units, and a tolerance wide enough to
/// hide the ancestor-scale bug is wide enough to hide every other one.
/// </remarks>
public class SvgViewerGizmoTests
{
    /// <summary>A 20x20 shape at 20,20 on a 100x100 drawing, with room to be dragged anywhere.</summary>
    private const string Plain = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <rect id="box" x="20" y="20" width="20" height="20" fill="#3366cc" />
        </svg>
        """;

    /// <summary>The same shape, under a group that doubles everything below it.</summary>
    /// <remarks>
    /// So the shape covers the same pixels as <see cref="Plain"/>'s and answers to the same drag,
    /// and the only thing that can differ is what gets written.
    /// </remarks>
    private const string Nested = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <g transform="scale(2)">
            <rect id="box" x="10" y="10" width="10" height="10" fill="#3366cc" />
          </g>
        </svg>
        """;

    /// <summary>
    /// A shape of each kind the editor stack's own resize refuses, plus the group around them.
    /// </summary>
    /// <remarks>
    /// <c>SelectionService.ResizeElement</c> writes geometry attributes and has no default case, so
    /// an ellipse, a text run, a polygon and a group are all silently left alone by it. Composing
    /// the gesture into the transform instead is what makes them draggable, and these are the four
    /// that would go quiet if anybody swapped that back.
    /// </remarks>
    private const string Shapes = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <g id="group"><rect x="20" y="20" width="20" height="20" fill="#3366cc" /></g>
          <ellipse id="ellipse" cx="70" cy="30" rx="10" ry="10" fill="#cc3366" />
          <text id="text" x="20" y="70" font-size="10">ab</text>
          <polygon id="polygon" points="60,60 80,60 80,80" fill="#33cc66" />
        </svg>
        """;

    /// <summary>A shape whose transform is written by a parameter rather than by a number.</summary>
    private const string Driven = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 100 100" width="100" height="100">
          <defs><e:code><e:param name="shift" type="number" default="0" /></e:code></defs>
          <rect id="box" x="20" y="20" width="20" height="20" fill="#3366cc" transform="translate({{ shift }}, 0)" />
        </svg>
        """;

    /// <summary>A triangle filling the same 20..40 square the rectangle does.</summary>
    private const string Polygon = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <polygon id="box" points="20,40 40,40 40,20" fill="#3366cc" />
        </svg>
        """;

    private const RawInputModifiers Held = RawInputModifiers.LeftMouseButton;

    private static async Task<(Window Window, SvgViewer Viewer)> Host(string drawing)
    {
        var viewer = new SvgViewer();
        var window = new Window { Width = 400, Height = 400, Background = Brushes.White, Content = viewer };

        window.Show();

        Assert.True(await viewer.LoadTextAsync(drawing));

        Dispatcher.UIThread.RunJobs();

        return (window, viewer);
    }

    /// <summary>Where a point of the drawing falls in the window.</summary>
    private static Point At(Window window, SvgViewer viewer, float x, float y)
    {
        Assert.True(viewer.Canvas.TryGetControlPoint(new SKPoint(x, y), out var point));

        return viewer.Canvas.TranslatePoint(point, window)
               ?? throw new InvalidOperationException("The canvas is not in the window.");
    }

    /// <summary>Clicks the shape, which is how anything comes to have handles on it.</summary>
    private static void Select(Window window, SvgViewer viewer, float x, float y)
    {
        var at = At(window, viewer, x, y);

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);

        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(viewer.SelectedElement);
    }

    /// <summary>
    /// Selects by name rather than by clicking, for the elements a click cannot reach.
    /// </summary>
    /// <remarks>
    /// A click picks what was drawn, so it lands on the shape inside a group and never on the group
    /// itself. The tree is the only way to point at a container, and it is how anybody would.
    /// </remarks>
    private static void SelectById(SvgViewer viewer, string id)
    {
        var element = viewer.Canvas.Svg?.SourceDocument?.GetElementById(id);

        Assert.NotNull(element);
        Assert.True(viewer.Elements.TrySelect(SvgElementAddress.Create(element!).Key));

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(id, viewer.SelectedElement?.ID);
    }

    private static void Drag(Window window, SvgViewer viewer, (float X, float Y) from, (float X, float Y) to)
    {
        window.MouseDown(At(window, viewer, from.X, from.Y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        window.MouseMove(At(window, viewer, to.X, to.Y), Held);
        Dispatcher.UIThread.RunJobs();

        window.MouseUp(At(window, viewer, to.X, to.Y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The transform the file now gives the shape, or null where it gives it none.</summary>
    private static string? Written(SvgViewer viewer, string id = "box")
    {
        var source = viewer.Source;
        var at = source.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);

        Assert.True(at >= 0, "The drawing no longer has the shape the test drags.");

        var tag = source.LastIndexOf('<', at);
        var end = source.IndexOf('>', at);
        var element = source.Substring(tag, end - tag);

        var transform = element.IndexOf("transform=\"", StringComparison.Ordinal);

        if (transform < 0)
        {
            return null;
        }

        var value = transform + "transform=\"".Length;

        return element.Substring(value, element.IndexOf('"', value) - value);
    }

    [AvaloniaFact]
    public async Task A_Drag_Moves_The_Element_By_What_The_Pointer_Moved()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        viewer.IsEditing = true;
        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (30f, 30f), (50f, 40f));

        Assert.Equal("translate(20, 10)", Written(viewer));
    }

    /// <summary>
    /// The correction the existing editor does without: a transform is read in the element's parent
    /// space, so under a group that doubles everything the shape has to be told to move half as far.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Drag_Under_A_Scaled_Group_Writes_The_Delta_That_Group_Reads()
    {
        var (window, viewer) = await Host(Nested);

        Select(window, viewer, 30f, 30f);

        viewer.IsEditing = true;
        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (30f, 30f), (50f, 40f));

        Assert.Equal("translate(10, 5)", Written(viewer));
    }

    /// <summary>A second drag carries on from the first rather than writing beside it.</summary>
    [AvaloniaFact]
    public async Task Dragging_Twice_Writes_One_Transform()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        viewer.IsEditing = true;
        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (30f, 30f), (40f, 30f));
        Drag(window, viewer, (40f, 30f), (50f, 30f));

        Assert.Equal("translate(20, 0)", Written(viewer));
    }

    /// <summary>The whole gesture is one thing to take back, not one per frame of it.</summary>
    [AvaloniaFact]
    public async Task A_Drag_Is_One_Undo_Step()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        viewer.IsEditing = true;
        Dispatcher.UIThread.RunJobs();

        window.MouseDown(At(window, viewer, 30f, 30f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // Several moves, so a commit per frame would leave several steps behind it.
        foreach (var x in new[] { 34f, 38f, 42f, 46f, 50f })
        {
            window.MouseMove(At(window, viewer, x, 30f), Held);
            Dispatcher.UIThread.RunJobs();
        }

        window.MouseUp(At(window, viewer, 50f, 30f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("translate(20, 0)", Written(viewer));

        Assert.True(viewer.Undo());
        Dispatcher.UIThread.RunJobs();

        Assert.Null(Written(viewer));
    }

    /// <summary>Dragging one corner leaves the opposite one where it was, which is what a handle means.</summary>
    [AvaloniaFact]
    public async Task A_Corner_Handle_Scales_About_The_Corner_Opposite_It()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        viewer.IsEditing = true;
        Dispatcher.UIThread.RunJobs();

        // The bottom right of a shape spanning 20..40, dragged out to 60: twice as wide and twice as
        // tall, about the top left at 20,20.
        Drag(window, viewer, (40f, 40f), (60f, 60f));

        Assert.Equal("translate(-20, -20) scale(2)", Written(viewer));
    }

    /// <summary>A side handle stretches one axis and leaves the other alone.</summary>
    [AvaloniaFact]
    public async Task A_Side_Handle_Scales_One_Axis()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        viewer.IsEditing = true;
        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (40f, 30f), (60f, 30f));

        Assert.Equal("translate(-20, 0) scale(2, 1)", Written(viewer));
    }

    /// <summary>Turning the shape writes an angle about the middle of its own bounds.</summary>
    [AvaloniaFact]
    public async Task The_Stalk_Turns_The_Element_About_Its_Middle()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        viewer.IsEditing = true;
        Dispatcher.UIThread.RunJobs();

        var box = viewer.Canvas.Gizmo;

        Assert.NotNull(box);

        // From the stalk to the right of the middle, which is a quarter turn from straight up.
        Drag(window, viewer, (box!.Value.RotHandle.X, box.Value.RotHandle.Y), (60f, 30f));

        Assert.Equal("rotate(90, 30, 30)", Written(viewer));
    }

    /// <summary>Letting go of the key puts the shape back rather than leaving it where the drag got to.</summary>
    [AvaloniaFact]
    public async Task Escape_Leaves_The_Element_Where_The_Drag_Found_It()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        viewer.IsEditing = true;
        Dispatcher.UIThread.RunJobs();

        window.MouseDown(At(window, viewer, 30f, 30f), MouseButton.Left);
        window.MouseMove(At(window, viewer, 50f, 50f), Held);
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        window.MouseUp(At(window, viewer, 50f, 50f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(Written(viewer));
    }

    /// <summary>
    /// Every kind of element answers to a scale handle, including the four the editor's own resize
    /// leaves alone.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("group")]
    [InlineData("ellipse")]
    [InlineData("text")]
    [InlineData("polygon")]
    public async Task Every_Kind_Of_Element_Scales(string id)
    {
        var (window, viewer) = await Host(Shapes);

        SelectById(viewer, id);

        viewer.IsEditing = true;
        Dispatcher.UIThread.RunJobs();

        var box = viewer.Canvas.Gizmo;

        Assert.NotNull(box);

        // The measured corner rather than a declared one: a text run's bounds are the glyphs it
        // drew, and no attribute on it says where those end.
        var corner = box!.Value.BR;

        Drag(window, viewer, (corner.X, corner.Y), (corner.X + 10f, corner.Y + 10f));

        Assert.Contains("scale(", Written(viewer, id) ?? string.Empty);
    }

    /// <summary>
    /// A transform an expression writes is refused rather than flattened into the number it happens
    /// to come to.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Driven_Transform_Refuses_The_Drag()
    {
        var (window, viewer) = await Host(Driven);

        Select(window, viewer, 30f, 30f);

        viewer.IsEditing = true;
        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (30f, 30f), (50f, 40f));

        Assert.Equal("translate({{ shift }}, 0)", Written(viewer));
    }

    /// <summary>
    /// A polygon scales by exactly what a rectangle would, which the looser check above would not
    /// have caught: its bounds are measured off its points and nothing declares them.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Polygon_Scales_By_The_Same_Arithmetic_As_A_Rectangle()
    {
        var (window, viewer) = await Host(Polygon);

        Select(window, viewer, 25f, 35f);

        Assert.Equal("box", viewer.SelectedElement?.ID);

        viewer.IsEditing = true;
        Dispatcher.UIThread.RunJobs();

        // The same shape and the same drag as the rectangle's corner test: 20..40 pulled out to 60.
        Drag(window, viewer, (40f, 40f), (60f, 60f));

        Assert.Equal("translate(-20, -20) scale(2)", Written(viewer));
    }

    /// <summary>
    /// A container is dragged by its contents, there being nothing else of it to take hold of.
    /// </summary>
    /// <remarks>
    /// The hit test answers with what was drawn, which inside a group is never the group. Asking
    /// only whether it answered with the selected element left a <c>&lt;g&gt;</c> scalable by its
    /// handles and unmovable by its body, the press falling through to a pan.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Group_Is_Moved_By_Dragging_What_Is_Inside_It()
    {
        var (window, viewer) = await Host(Shapes);

        SelectById(viewer, "group");

        viewer.IsEditing = true;
        Dispatcher.UIThread.RunJobs();

        // On the rectangle the group holds, which is the only part of the group there is to press.
        Drag(window, viewer, (30f, 30f), (50f, 40f));

        Assert.Equal("translate(20, 10)", Written(viewer, "group"));
    }

    /// <summary>What a shape placed by <c>&lt;use&gt;</c> answers to, which is the use and not the
    /// definition.</summary>
    private const string Used = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <defs><rect id="shape" x="0" y="0" width="20" height="20" fill="#3366cc" /></defs>
          <use id="box" href="#shape" x="20" y="20" />
        </svg>
        """;

    [AvaloniaFact]
    public async Task A_Use_Is_Moved_By_Dragging_What_It_Placed()
    {
        var (window, viewer) = await Host(Used);

        SelectById(viewer, "box");

        viewer.IsEditing = true;
        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (30f, 30f), (50f, 40f));

        Assert.Equal("translate(20, 10)", Written(viewer));
    }

    /// <summary>
    /// The ring round the element follows it while it is being dragged, rather than staying where
    /// the element was.
    /// </summary>
    /// <remarks>
    /// Mid-drag, before the button comes up: the commit rebuilds the drawing and traces the ring
    /// again, so by the end it is right wherever it was during. What somebody watches is the middle.
    /// </remarks>
    [AvaloniaFact]
    public async Task The_Ring_Follows_The_Element_Through_A_Drag()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        viewer.IsEditing = true;
        Dispatcher.UIThread.RunJobs();

        var before = viewer.Canvas.Highlight?.Bounds;

        Assert.NotNull(before);

        window.MouseDown(At(window, viewer, 30f, 30f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        window.MouseMove(At(window, viewer, 50f, 40f), Held);
        Dispatcher.UIThread.RunJobs();

        var during = viewer.Canvas.Highlight?.Bounds;

        Assert.NotNull(during);

        // The shape moved by twenty and ten, so its silhouette did.
        Assert.Equal(before!.Value.Left + 20f, during!.Value.Left, 3);
        Assert.Equal(before.Value.Top + 10f, during.Value.Top, 3);

        window.MouseUp(At(window, viewer, 50f, 40f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>With the mode off the drawing pans as it always did, and nothing is written.</summary>
    [AvaloniaFact]
    public async Task A_Drag_Pans_While_The_Mode_Is_Off()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        var offset = viewer.Canvas.OffsetX;

        Drag(window, viewer, (30f, 30f), (50f, 30f));

        Assert.Null(Written(viewer));
        Assert.NotEqual(offset, viewer.Canvas.OffsetX);
    }
}
