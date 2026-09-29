// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;
using Svg.Editor.Skia;
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

    /// <summary>
    /// A multi-line label, and a run small enough that its own handles cover it.
    /// </summary>
    /// <remarks>
    /// A page this big is what makes the small run small: a handle keeps its size on screen, so the
    /// band it answers to is wider in the drawing's own units the further out the page is fitted.
    /// The label's glyphs are placed by its tspans, which is how anybody writes two lines.
    /// </remarks>
    private const string Runs = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1000 800" width="1000" height="800">
          <text id="lines" x="100" y="200" font-size="40" fill="#3366cc"><tspan x="100" y="200">one</tspan><tspan x="100" y="260">two</tspan></text>
          <text id="small" x="600" y="600" font-size="10" fill="#cc3366">ab</text>
        </svg>
        """;

    /// <summary>
    /// A drawing the size a PaintCode import comes in at, where the default ten-unit step is most of
    /// the artwork: the run is about thirteen units across.
    /// </summary>
    private const string Imported = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 24" width="40" height="24">
          <g transform="translate(4, 3)">
            <rect x="0" y="0" width="32" height="18" fill="#3366cc" />
            <text id="t" x="12" y="9" font-size="12.3582" text-anchor="middle" dominant-baseline="central" fill="#ffffff">-6</text>
          </g>
        </svg>
        """;

    /// <summary>A shape whose transform is written by a parameter rather than by a number.</summary>
    private const string Driven = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 100 100" width="100" height="100">
          <defs><e:code><e:param name="shift" type="number" default="0" /></e:code></defs>
          <rect id="box" x="20" y="20" width="20" height="20" fill="#3366cc" transform="translate({{ shift }}, 0)" />
        </svg>
        """;

    /// <summary>A line lying flat, whose bounds are twenty wide and nothing tall.</summary>
    private const string Flat = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <line id="box" x1="20" y1="30" x2="40" y2="30" stroke="#3366cc" stroke-width="4" />
        </svg>
        """;

    /// <summary>A shape twice as wide as it is tall, whose diagonal is nothing like forty five degrees.</summary>
    private const string Oblong = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <rect id="box" x="20" y="20" width="40" height="20" fill="#3366cc" />
        </svg>
        """;

    /// <summary>A group an expression moves, which has no geometry of its own to be moved by.</summary>
    private const string DrivenGroup = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 100 100" width="100" height="100">
          <defs><e:code><e:param name="shift" type="number" default="0" /></e:code></defs>
          <g id="group" transform="translate({{ shift }}, 0)"><rect x="20" y="20" width="20" height="20" fill="#3366cc" /></g>
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
    private static string? Written(SvgViewer viewer, string id = "box") => Attribute(viewer, id, "transform");

    /// <summary>What the file now spells for one of the shape's attributes, or null for none.</summary>
    private static string? Attribute(SvgViewer viewer, string id, string name)
    {
        var source = viewer.Source;
        var at = source.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);

        Assert.True(at >= 0, "The drawing no longer has the shape the test drags.");

        var tag = source.LastIndexOf('<', at);
        var end = source.IndexOf('>', at);
        var element = source.Substring(tag, end - tag);

        var written = element.IndexOf($" {name}=\"", StringComparison.Ordinal);

        if (written < 0)
        {
            return null;
        }

        var value = written + name.Length + 3;

        return element.Substring(value, element.IndexOf('"', value) - value);
    }

    /// <summary>The four numbers a box-shaped element is written with, so a resize is one assertion.</summary>
    private static string Box(SvgViewer viewer, string id = "box")
        => string.Join(
            " ",
            new[] { "x", "y", "width", "height" }.Select(name => Attribute(viewer, id, name) ?? "?"));

    /// <summary>A shape whose position is spelt in its style attribute, where the attribute cannot win.</summary>
    /// <remarks>
    /// The style shadows whatever the drag would write, which for a rect is now its own x — the
    /// refusal is by attribute name, so moving the declaration moves which drag it refuses.
    /// </remarks>
    private const string Shadowed = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <rect id="box" x="20" y="20" width="20" height="20" fill="#3366cc" style="x: 20" />
        </svg>
        """;

    /// <summary>
    /// A release the file will not take puts the element back where the drag found it.
    /// </summary>
    /// <remarks>
    /// The drag is applied to the built document as it is made, and whether the file will take it is
    /// only found out on release — a style declaration beats the attribute under it, so writing the
    /// attribute is refused. Without a way back the drawing keeps a transform its own text does not
    /// have, and it keeps it for good: a host that rebuilds only what changed has no reason to read
    /// a file whose text never moved.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Release_The_File_Refuses_Puts_The_Element_Back()
    {
        var (window, viewer) = await Host(Shadowed);

        Select(window, viewer, 30f, 30f);

        Dispatcher.UIThread.RunJobs();

        var before = ((SvgRectangle)Element(viewer)).X.Value;
        var text = viewer.Source;

        Drag(window, viewer, (30f, 30f), (50f, 30f));

        // Refused, and said so.
        Assert.Equal(text, viewer.Source);

        // And the drawing is not left carrying what the file does not say.
        Assert.Equal(before, ((SvgRectangle)Element(viewer)).X.Value);

        window.Close();
    }

    /// <summary>A refused drag can be made again, and composes from where it started.</summary>
    [AvaloniaFact]
    public async Task An_Element_Put_Back_Can_Be_Dragged_Again()
    {
        var (window, viewer) = await Host(Shadowed);

        Select(window, viewer, 30f, 30f);

        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (30f, 30f), (50f, 30f));
        Drag(window, viewer, (30f, 30f), (50f, 30f));

        // Not doubled: the second drag starts from what the first put back, not from what it drew.
        Assert.Equal(20f, ((SvgRectangle)Element(viewer)).X.Value);

        window.Close();
    }

    /// <summary>
    /// The handles stay on an element dragged past its drawing's own edge.
    /// </summary>
    /// <remarks>
    /// The canvas cuts a drawing's ink at its page, so a shape dragged off it is not painted there.
    /// The handles are drawn outside that clip on purpose: clipped with it, a shape pushed off the
    /// page could never be taken hold of and dragged back, which would make an easy mistake
    /// permanent.
    /// </remarks>
    [AvaloniaFact]
    public async Task The_Handles_Stay_On_An_Element_Dragged_Past_The_Page_Edge()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        Dispatcher.UIThread.RunJobs();

        // Clean off a 100x100 page.
        Drag(window, viewer, (30f, 30f), (95f, 30f));

        var box = viewer.Canvas.Gizmo;

        Assert.NotNull(box);
        Assert.True(box!.Value.TL.X > 80f, $"the handles are at {box.Value.TL}, not on the shape that was dragged off");

        window.Close();
    }

    /// <summary>
    /// A shape with no thickness can still be taken hold of.
    /// </summary>
    /// <remarks>
    /// Its bounds are a line, and a drag used to be refused outright for covering nothing — which
    /// took a horizontal line, the shape most likely to want stretching, out of the editor entirely.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Flat_Line_Can_Be_Dragged_At_All()
    {
        var (window, viewer) = await Host(Flat);

        Select(window, viewer, 30f, 30f);

        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (30f, 30f), (50f, 30f));

        Assert.Equal("40 30 60 30", Ends(viewer));

        window.Close();
    }

    /// <summary>
    /// And stretched along the axis it does have, by the handle at its own end.
    /// </summary>
    /// <remarks>
    /// The axis with no extent scales by 1 whatever the pointer does, so the line grows end to end
    /// and stays where it was laid — and its stroke, which a transform would have stretched along
    /// with it, is not touched at all.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Flat_Line_Stretches_Along_Its_Own_Axis()
    {
        var (window, viewer) = await Host(Flat);

        Select(window, viewer, 30f, 30f);

        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (40f, 30f), (60f, 30f));

        Assert.Equal("20 30 60 30", Ends(viewer));
        Assert.Null(Written(viewer));

        window.Close();
    }

    /// <summary>The four numbers a line is written with.</summary>
    private static string Ends(SvgViewer viewer)
        => string.Join(" ", new[] { "x1", "y1", "x2", "y2" }.Select(name => Attribute(viewer, "box", name)));

    /// <summary>Two shapes, swept up together and then dragged as one.</summary>
    private const string Two = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <rect id="one" x="20" y="20" width="20" height="20" fill="#3366cc" />
          <rect id="two" x="60" y="60" width="20" height="20" fill="#cc3366" />
        </svg>
        """;

    /// <summary>
    /// A sweep selects what it caught, and the whole of it moves as one.
    /// </summary>
    /// <remarks>
    /// The feature end to end: the press misses the handles, so it sweeps; the release selects both
    /// shapes; and a drag from inside one of them carries the other with it, in one commit that one
    /// undo takes back.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Swept_Selection_Is_Dragged_As_One()
    {
        var (window, viewer) = await Host(Two);

        Dispatcher.UIThread.RunJobs();

        // Round both shapes, from a corner of the page that is over neither of them.
        Drag(window, viewer, (5f, 5f), (95f, 95f));

        Assert.Equal(2, viewer.Elements.SelectedNodes.Count);

        // And now from inside the first, which carries both.
        Drag(window, viewer, (30f, 30f), (50f, 40f));

        Assert.Equal("40 30 20 20", Box(viewer, "one"));
        Assert.Equal("80 70 20 20", Box(viewer, "two"));

        // One gesture, one thing to take back.
        Assert.True(viewer.Undo());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("20 20 20 20", Box(viewer, "one"));
        Assert.Equal("60 60 20 20", Box(viewer, "two"));

        window.Close();
    }

    /// <summary>
    /// What a sweep has caught is rung while it is still being drawn.
    /// </summary>
    /// <remarks>
    /// Growing the rectangle over the second shape rings the second shape, before anybody has let
    /// go — and the ring is what says which of the two answers the question you are asking.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Sweep_Rings_What_It_Is_Over_As_It_Is_Drawn()
    {
        var (window, viewer) = await Host(Two);

        Dispatcher.UIThread.RunJobs();

        window.MouseDown(At(window, viewer, 5f, 5f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // Round the first shape only, which spans 20..40.
        window.MouseMove(At(window, viewer, 45f, 45f), Held);
        Dispatcher.UIThread.RunJobs();

        var one = viewer.Canvas.Highlight?.Bounds;

        Assert.NotNull(one);
        Assert.True(one!.Value.Right < 50f, $"the ring is {one}, which is more than the first shape");

        // And on over the second, which spans 60..80.
        window.MouseMove(At(window, viewer, 95f, 95f), Held);
        Dispatcher.UIThread.RunJobs();

        var both = viewer.Canvas.Highlight?.Bounds;

        Assert.NotNull(both);
        Assert.True(both!.Value.Right > 75f, $"the ring is {both}, which has not reached the second shape");

        // Taken back, the ring goes back to what is actually selected, which is nothing.
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewer.Canvas.Highlight);

        window.MouseUp(At(window, viewer, 95f, 95f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        window.Close();
    }

    /// <summary>
    /// A sweep that goes on catching the same things keeps the ring it already drew.
    /// </summary>
    /// <remarks>
    /// Most of the moves of most sweeps grow the rectangle across empty canvas. Tracing again there
    /// would build a path a frame — each left for the finalizer, since the canvas will not free a
    /// ring the render thread may still be drawing. The path's own identity is the exact observable
    /// for whether one was built.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Sweep_That_Catches_The_Same_Things_Keeps_Its_Ring()
    {
        var (window, viewer) = await Host(Two);

        Dispatcher.UIThread.RunJobs();

        window.MouseDown(At(window, viewer, 5f, 5f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // Round the first shape.
        window.MouseMove(At(window, viewer, 45f, 45f), Held);
        Dispatcher.UIThread.RunJobs();

        var first = viewer.Canvas.Highlight;

        Assert.NotNull(first);

        // Further across empty canvas, catching nothing new.
        window.MouseMove(At(window, viewer, 52f, 52f), Held);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(first, viewer.Canvas.Highlight);

        // And on over the second, which is something new.
        window.MouseMove(At(window, viewer, 95f, 95f), Held);
        Dispatcher.UIThread.RunJobs();

        Assert.NotSame(first, viewer.Canvas.Highlight);

        window.MouseUp(At(window, viewer, 95f, 95f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        window.Close();
    }

    /// <summary>A sweep that caught nothing puts the selection away.</summary>
    [AvaloniaFact]
    public async Task A_Sweep_Over_Nothing_Clears_The_Selection()
    {
        var (window, viewer) = await Host(Two);

        Select(window, viewer, 30f, 30f);

        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(viewer.SelectedElement);

        var said = 0;

        viewer.Elements.Selected += (_, _) => said++;

        // A corner of the page with nothing on it.
        Drag(window, viewer, (85f, 5f), (95f, 15f));

        Assert.Equal(1, said);
        Assert.Empty(viewer.Elements.SelectedNodes);
        Assert.Null(viewer.Canvas.Gizmo);

        window.Close();
    }

    /// <summary>The element the tests drag, as the live document holds it.</summary>
    private static Svg.SvgElement Element(SvgViewer viewer, string id = "box")
    {
        var element = viewer.Canvas.Svg?.SourceDocument?.GetElementById(id);

        Assert.NotNull(element);

        return element!;
    }

    [AvaloniaFact]
    public async Task A_Drag_Moves_The_Element_By_What_The_Pointer_Moved()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (30f, 30f), (50f, 40f));

        Assert.Equal("40 30 20 20", Box(viewer));
        Assert.Null(Written(viewer));
    }

    /// <summary>
    /// The correction the existing editor does without: the delta is read in the element's own
    /// space, so under a group that doubles everything the shape has to be told to move half as far.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Drag_Under_A_Scaled_Group_Writes_The_Delta_That_Group_Reads()
    {
        var (window, viewer) = await Host(Nested);

        Select(window, viewer, 30f, 30f);

        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (30f, 30f), (50f, 40f));

        Assert.Equal("20 15 10 10", Box(viewer));
    }

    /// <summary>
    /// A second drag of a shape carries on from the numbers the first one left.
    /// </summary>
    /// <remarks>
    /// Nothing has to fold for that to be true, which is the quiet advantage of writing an
    /// element's own geometry: the second drag reads what the first one wrote.
    /// </remarks>
    [AvaloniaFact]
    public async Task Dragging_Twice_Carries_On_From_What_The_First_Wrote()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (30f, 30f), (40f, 30f));
        Drag(window, viewer, (40f, 30f), (50f, 30f));

        Assert.Equal("40 20 20 20", Box(viewer));
    }

    /// <summary>A second drag of a group folds into the transform the first one wrote.</summary>
    /// <remarks>
    /// A group has no geometry of its own, so it is the case that still has to fold — and the one
    /// that would grow a translate per drag if the folding were ever dropped.
    /// </remarks>
    [AvaloniaFact]
    public async Task Dragging_A_Group_Twice_Writes_One_Transform()
    {
        var (window, viewer) = await Host(Shapes);

        SelectById(viewer, "group");

        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (30f, 30f), (40f, 30f));
        Drag(window, viewer, (40f, 30f), (50f, 30f));

        Assert.Equal("translate(20, 0)", Written(viewer, "group"));
    }

    /// <summary>The whole gesture is one thing to take back, not one per frame of it.</summary>
    [AvaloniaFact]
    public async Task A_Drag_Is_One_Undo_Step()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

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

        Assert.Equal("40 20 20 20", Box(viewer));

        Assert.True(viewer.Undo());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("20 20 20 20", Box(viewer));
    }

    /// <summary>Dragging one corner leaves the opposite one where it was, which is what a handle means.</summary>
    [AvaloniaFact]
    public async Task A_Corner_Handle_Scales_About_The_Corner_Opposite_It()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        Dispatcher.UIThread.RunJobs();

        // The bottom right of a shape spanning 20..40, dragged out to 60: twice as wide and twice as
        // tall, about the top left at 20,20.
        Drag(window, viewer, (40f, 40f), (60f, 60f));

        Assert.Equal("20 20 40 40", Box(viewer));
    }

    /// <summary>A side handle stretches one axis and leaves the other alone.</summary>
    [AvaloniaFact]
    public async Task A_Side_Handle_Scales_One_Axis()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (40f, 30f), (60f, 30f));

        Assert.Equal("20 20 40 20", Box(viewer));
    }

    /// <summary>
    /// A corner dragged unevenly keeps the shape's proportions, which is what the lock is for.
    /// </summary>
    /// <remarks>
    /// Twice as wide and half again as tall is asked for, and the corner lands between the two: the
    /// pointer is dropped onto the diagonal it was pressed on, which on this square shape runs at
    /// forty five degrees from 20,20 — so 60,50 comes to 1.75 rather than to either axis's own 2
    /// or 1.5.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Locked_Corner_Follows_The_Diagonal_It_Was_Pressed_On()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        viewer.LocksAspectRatio = true;
        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (40f, 40f), (60f, 50f));

        Assert.Equal("20 20 35 35", Box(viewer));
    }

    /// <summary>
    /// The line a locked corner runs along is the shape's own diagonal, not forty five degrees.
    /// </summary>
    /// <remarks>
    /// The one case that tells the two apart, and the reason it is worth a test of its own: on a
    /// 40x20 shape the diagonal out of 20,20 is twice as flat as a forty five degree line, and a
    /// pointer taken straight out to 80,40 reads 1.4 along it. Following the wider axis on its own
    /// would say 1.5, and a true forty five degree track about 1.32.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Locked_Corner_Runs_Along_The_Shapes_Own_Diagonal()
    {
        var (window, viewer) = await Host(Oblong);

        Select(window, viewer, 40f, 30f);

        viewer.LocksAspectRatio = true;
        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (60f, 40f), (80f, 40f));

        Assert.Equal("20 20 56 28", Box(viewer));
    }

    /// <summary>The same drag without the lock stretches the shape by each axis on its own.</summary>
    [AvaloniaFact]
    public async Task An_Unlocked_Corner_Scales_Each_Axis_By_Its_Own_Drag()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (40f, 40f), (60f, 50f));

        Assert.Equal("20 20 40 30", Box(viewer));
    }

    /// <summary>
    /// A side handle under the lock grows the other axis too, about the middle of the shape.
    /// </summary>
    /// <remarks>
    /// The handle opposite is what a scale turns about, and a side handle's opposite is the far
    /// edge at the shape's own middle — so the axis nobody dragged grows both ways at once and the
    /// shape stays where it was.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Locked_Side_Handle_Scales_Both_Axes()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        viewer.LocksAspectRatio = true;
        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (40f, 30f), (60f, 30f));

        Assert.Equal("20 10 40 40", Box(viewer));
    }

    /// <summary>Turning the shape writes an angle about the middle of its own bounds.</summary>
    [AvaloniaFact]
    public async Task The_Stalk_Turns_The_Element_About_Its_Middle()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

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

        Dispatcher.UIThread.RunJobs();

        window.MouseDown(At(window, viewer, 30f, 30f), MouseButton.Left);
        window.MouseMove(At(window, viewer, 50f, 50f), Held);
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        window.MouseUp(At(window, viewer, 50f, 50f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("20 20 20 20", Box(viewer));
    }

    /// <summary>
    /// A run whose glyphs its tspans place follows the pointer, and is written as a transform.
    /// </summary>
    /// <remarks>
    /// The whole of the reported symptom: the drag fired, the document was rewritten and an undo step
    /// was spent, while nothing on screen moved at all. The run's own x and y place no glyph once a
    /// tspan carries its own, so the writer now refuses them and the transform carries the move.
    /// Asserted mid-drag, before the release, because being seen on the way is the point.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Run_Its_Tspans_Place_Is_Carried_By_A_Transform()
    {
        var (window, viewer) = await Host(Runs);

        SelectById(viewer, "lines");

        var box = viewer.Canvas.Gizmo!.Value;
        var was = Glyphs(viewer);

        // Three runs: the label's two lines, and the small run that has to stay where it is.
        Assert.Equal(3, was.Count);

        window.MouseDown(At(window, viewer, box.Center.X, box.Center.Y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        window.MouseMove(At(window, viewer, box.Center.X + 60f, box.Center.Y + 40f), Held);
        Dispatcher.UIThread.RunJobs();

        // Mid-drag: the glyphs themselves, in the model the canvas is painting from.
        Assert.Equal(Carried(was, 60f, 40f, "one", "two"), Glyphs(viewer));

        window.MouseUp(At(window, viewer, box.Center.X + 60f, box.Center.Y + 40f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("translate(60, 40)", Written(viewer, "lines"));

        // And the tspans still say where they sit, which is what made the run's own numbers useless.
        Assert.Equal("100", Attribute(viewer, "lines", "x"));

        window.Close();
    }

    /// <summary>
    /// A press in the gap between two lines takes hold of the run rather than sweeping a marquee.
    /// </summary>
    /// <remarks>
    /// A text node is hit tested per character cell while its box is the measured extent of the whole
    /// run, so the inside of that box has holes in it -- the gap between two lines, between two
    /// letters, above the ascenders. A press in one used to fall through to the marquee, which swept a
    /// rubber band and dropped the selection on the way up.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Press_In_The_Gap_Between_Two_Lines_Takes_Hold()
    {
        var (window, viewer) = await Host(Runs);

        SelectById(viewer, "lines");

        var box = viewer.Canvas.Gizmo!.Value;
        var marqueed = false;

        viewer.Canvas.Marqueed += (_, _) => marqueed = true;

        // The precondition, and the reason this press was ever a problem: nothing was drawn here.
        Assert.Null(viewer.Canvas.Svg?.HitTestTopmostElement(
            new ShimSkiaSharp.SKPoint(box.Center.X, box.Center.Y)));

        var was = Glyphs(viewer);

        Drag(window, viewer, (box.Center.X, box.Center.Y), (box.Center.X + 60f, box.Center.Y + 40f));

        Assert.False(marqueed);
        Assert.Equal("lines", viewer.SelectedElement?.ID);
        Assert.Equal(Carried(was, 60f, 40f, "one", "two"), Glyphs(viewer));

        window.Close();
    }

    /// <summary>
    /// A run too small for its own handles is carried by a press in the middle, not scaled by one.
    /// </summary>
    /// <remarks>
    /// The band a handle answers to is a constant on screen, so at this zoom it is wider than the run:
    /// every point inside the box was a handle, and a press meant to carry the text wrote a scale that
    /// flipped it instead. A move writes a small run's own x and y, so a transform of any kind here
    /// would be the scale coming back.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Run_Too_Small_For_Its_Handles_Is_Carried()
    {
        var (window, viewer) = await Host(Runs);

        SelectById(viewer, "small");

        var box = viewer.Canvas.Gizmo!.Value;

        // The precondition: by the arithmetic the handles are hit tested with, the middle of this box
        // is one of them.
        Assert.True(new SelectionService().HitHandle(box, box.Center, (float)viewer.Canvas.Scale, out _) >= 0);

        Drag(window, viewer, (box.Center.X, box.Center.Y), (box.Center.X + 60f, box.Center.Y + 40f));

        Assert.Null(Written(viewer, "small"));
        Assert.Equal("660", Attribute(viewer, "small", "x"));
        Assert.Equal("640", Attribute(viewer, "small", "y"));

        window.Close();
    }

    /// <summary>
    /// A grid coarser than the shape does not take the drag: the shape follows the pointer instead.
    /// </summary>
    /// <remarks>
    /// The reported case, and the whole of why a drag looked dead: a drawing a few tens of units
    /// across against the default ten-unit step has three lines in it, so the corner reached the
    /// nearest one on the first frame and stayed there for the rest of the gesture. The run here is
    /// 13 x 12.8 units against a step of 10.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Grid_Coarser_Than_The_Shape_Does_Not_Take_The_Move()
    {
        var (window, viewer) = await Host(Runs);

        viewer.SnapsToGrid = true;
        viewer.Grid = new SvgViewerGrid(200f, 15f);

        SelectById(viewer, "lines");

        Dispatcher.UIThread.RunJobs();

        var box = viewer.Canvas.Gizmo!.Value;
        var was = Glyphs(viewer);

        // The label is about 68 x 100 units, so two hundred is a grid it can never line up with.
        Assert.True(box.BR.X - box.TL.X < 200f);
        Assert.True(box.BR.Y - box.TL.Y < 200f);

        Drag(window, viewer, (box.Center.X, box.Center.Y), (box.Center.X + 60f, box.Center.Y + 40f));

        // Where the pointer went, not where the nearest line was.
        Assert.Equal(Carried(was, 60f, 40f, "one", "two"), Glyphs(viewer));

        window.Close();
    }

    /// <summary>
    /// A grid the shape is big enough for still takes it, which is what the setting is for.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Grid_The_Shape_Fits_Still_Takes_The_Move()
    {
        var (window, viewer) = await Host(Runs);

        viewer.SnapsToGrid = true;
        viewer.Grid = new SvgViewerGrid(10f, 15f);

        SelectById(viewer, "lines");

        Dispatcher.UIThread.RunJobs();

        var box = viewer.Canvas.Gizmo!.Value;
        var was = Glyphs(viewer);

        // The label spans about 68 units across and 100 down, so ten has plenty to say about it.
        Assert.True(box.BR.X - box.TL.X > 10f);
        Assert.True(box.BR.Y - box.TL.Y > 10f);

        Drag(window, viewer, (box.Center.X, box.Center.Y), (box.Center.X + 63f, box.Center.Y + 41f));

        var now = Glyphs(viewer);

        // It moved, and it moved to the line rather than to the pointer: which line is the grid's
        // business, and the run's own left edge is not on a round number to begin with.
        Assert.NotEqual(was, now);
        Assert.NotEqual(Carried(was, 63f, 41f, "one", "two"), now);

        window.Close();
    }

    /// <summary>
    /// A snap never squashes a shape away: no line is worth less shape than the grid can express.
    /// </summary>
    /// <remarks>
    /// The reported case, measured. A thirteen-unit run against the default ten-unit step: the corner
    /// snapped to the line half a unit from its own pivot, so the run came out a fortieth of its
    /// width — <c>scale(0.038, 0.31)</c> written to the file and the text gone from the screen mid
    /// drag. The line is refused instead and the corner keeps the pointer.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Snap_Never_Scales_A_Shape_Away()
    {
        var (window, viewer) = await Host(Imported);

        viewer.SnapsToGrid = true;

        SelectById(viewer, "t");

        Dispatcher.UIThread.RunJobs();

        var box = viewer.Canvas.Gizmo!.Value;

        // One step is most of this run, which is what makes the nearest line a bad place to land.
        Assert.True(box.BR.X - box.TL.X < 2f * viewer.Grid.Step);

        var to = new SKPoint(
            box.TL.X + (box.BR.X - box.TL.X) * 0.2f,
            box.TL.Y + (box.BR.Y - box.TL.Y) * 0.2f);

        Drag(window, viewer, (box.BR.X, box.BR.Y), (to.X, to.Y));

        var scaled = viewer.Canvas.Gizmo!.Value;

        // It scaled, the pivot held, and the corner is where the pointer left it.
        Assert.True(scaled.BR.X - scaled.TL.X < (box.BR.X - box.TL.X) / 2f, "nothing was scaled");
        Assert.Equal(box.TL.X, scaled.TL.X, 3);
        Assert.Equal(box.TL.Y, scaled.TL.Y, 3);
        Assert.Equal(to.X, scaled.BR.X, 2);
        Assert.Equal(to.Y, scaled.BR.Y, 2);

        window.Close();
    }

    /// <summary>
    /// The handles still answer from outside the box, on a run whose middle has been given to the move.
    /// </summary>
    /// <remarks>
    /// Half of a handle's square lies outside the box it belongs to, so on a run this small most of the
    /// pressable handle is out there. Handing that press to the move as well began nothing at all — the
    /// canvas had already taken it as an edit, so it was not a pick or a marquee either, and the press
    /// died in silence. A scale is written as a transform, a move into the run's own x.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Small_Run_Still_Scales_From_Outside_Its_Box()
    {
        var (window, viewer) = await Host(Runs);

        SelectById(viewer, "small");

        var box = viewer.Canvas.Gizmo!.Value;
        var scale = (float)viewer.Canvas.Scale;

        // Half a handle out from the corner: on the square that is drawn, off the box it sits on.
        var on = new SKPoint(
            box.TL.X - SelectionService.HandleSize / 4f / scale,
            box.TL.Y - SelectionService.HandleSize / 4f / scale);

        Assert.Equal(0, new SelectionService().HitHandle(box, on, scale, out _));

        Drag(window, viewer, (on.X, on.Y), (on.X - 60f, on.Y - 40f));

        Assert.Contains("scale", Written(viewer, "small") ?? "nothing was written");
        Assert.Equal("600", Attribute(viewer, "small", "x"));

        window.Close();
    }

    /// <summary>Where every glyph run of the drawing is being painted, in the drawing's own space.</summary>
    /// <remarks>
    /// Read from the recorded model rather than from the file, because what the file says and what is
    /// on screen disagreeing is the whole of what went wrong here.
    /// </remarks>
    private static List<string> Glyphs(SvgViewer viewer)
    {
        var found = new List<string>();

        if (viewer.Canvas.Svg?.Model is not { } picture)
        {
            return found;
        }

        void Walk(ShimSkiaSharp.SKPicture part, ShimSkiaSharp.SKMatrix inherited)
        {
            var total = inherited;
            var saved = new Stack<ShimSkiaSharp.SKMatrix>();

            foreach (var command in part.Commands ?? new List<ShimSkiaSharp.CanvasCommand>())
            {
                switch (command)
                {
                    case ShimSkiaSharp.SaveCanvasCommand:
                    case ShimSkiaSharp.SaveLayerCanvasCommand:
                        saved.Push(total);

                        break;
                    case ShimSkiaSharp.RestoreCanvasCommand:
                        total = saved.Count > 0 ? saved.Pop() : total;

                        break;
                    case ShimSkiaSharp.SetMatrixCanvasCommand matrix:
                        total = matrix.TotalMatrix;

                        break;
                    case ShimSkiaSharp.DrawTextCanvasCommand text:
                    {
                        var at = total.MapPoint(new ShimSkiaSharp.SKPoint(text.X, text.Y));

                        found.Add(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{text.Text} {at.X:0.##},{at.Y:0.##}"));

                        break;
                    }

                    // A drawing's glyphs are recorded a picture down, not beside the rest of it.
                    case ShimSkiaSharp.DrawPictureCanvasCommand { Picture: { } nested }:
                        Walk(nested, total);

                        break;
                }
            }
        }

        Walk(picture, ShimSkiaSharp.SKMatrix.CreateIdentity());

        return found;
    }

    /// <summary>
    /// The same glyph runs with the named ones moved by the drag: what the model should say after it.
    /// </summary>
    /// <remarks>
    /// The runs not named are left where they were, so one comparison says both that the dragged run
    /// moved and that nothing else did.
    /// </remarks>
    private static List<string> Carried(IReadOnlyList<string> was, float x, float y, params string[] only)
        => was.Select(run =>
        {
            var at = run.LastIndexOf(' ') + 1;

            if (!only.Contains(run.Substring(0, at - 1)))
            {
                return run;
            }

            var numbers = run.Substring(at).Split(',');

            var moved = new ShimSkiaSharp.SKPoint(
                float.Parse(numbers[0], CultureInfo.InvariantCulture) + x,
                float.Parse(numbers[1], CultureInfo.InvariantCulture) + y);

            // Invariant, as Glyphs writes them: a decimal comma would both spell a coordinate
            // differently and split one in two.
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{run.Substring(0, at)}{moved.X:0.##},{moved.Y:0.##}");
        }).ToList();

    /// <summary>
    /// Every kind of element answers to a scale handle, each in whatever it has to answer with.
    /// </summary>
    /// <remarks>
    /// The capability table in miniature. An ellipse and a polygon have numbers of their own and
    /// are written in them; a group has none, and a text run has none that says how big it is, so
    /// both of those still take a transform. Every one of the four is a shape the editor stack's
    /// own resize leaves alone entirely.
    /// </remarks>
    [AvaloniaTheory]
    [InlineData("group", "transform")]
    [InlineData("ellipse", "rx")]
    [InlineData("text", "transform")]
    [InlineData("polygon", "points")]
    public async Task Every_Kind_Of_Element_Scales(string id, string attribute)
    {
        var (window, viewer) = await Host(Shapes);

        SelectById(viewer, id);

        Dispatcher.UIThread.RunJobs();

        var box = viewer.Canvas.Gizmo;

        Assert.NotNull(box);

        // The measured corner rather than a declared one: a text run's bounds are the glyphs it
        // drew, and no attribute on it says where those end.
        var corner = box!.Value.BR;
        var before = Attribute(viewer, id, attribute);

        Drag(window, viewer, (corner.X, corner.Y), (corner.X + 10f, corner.Y + 10f));

        Assert.NotEqual(before, Attribute(viewer, id, attribute));
        Assert.NotNull(Attribute(viewer, id, attribute));
    }

    /// <summary>
    /// A shape an expression moves is dragged by its own numbers, and the expression is left alone.
    /// </summary>
    /// <remarks>
    /// This used to be refused outright, because writing the transform back would have spelt the
    /// number the expression came to and thrown away the thing that made it move. A geometry
    /// attribute cannot hold an expression at all — only paint, font and the transform can — so
    /// there is nothing here for the drag to overwrite.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Driven_Transform_Is_Left_Where_It_Is()
    {
        var (window, viewer) = await Host(Driven);

        Select(window, viewer, 30f, 30f);

        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (30f, 30f), (50f, 40f));

        Assert.Equal("40 30 20 20", Box(viewer));
        Assert.Equal("translate({{ shift }}, 0)", Written(viewer));
    }

    /// <summary>A driven element with no geometry is offset rather than overwritten.</summary>
    /// <remarks>
    /// A group has nothing of its own to write, so the gesture has to go into the transform the
    /// expression is in — after it, onto the text the file holds rather than onto the number the
    /// document was built with. Nothing can fold into an expression, so the second drag adds a term
    /// rather than rewriting the first: the growth is the price of not having to read the file's
    /// transform text with a second parser that could disagree with the one that renders it.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Driven_Group_Is_Offset_Rather_Than_Overwritten()
    {
        var (window, viewer) = await Host(DrivenGroup);

        SelectById(viewer, "group");

        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (30f, 30f), (50f, 40f));

        Assert.Equal("translate({{ shift }}, 0) translate(20, 10)", Written(viewer, "group"));

        Drag(window, viewer, (50f, 40f), (60f, 40f));

        Assert.Equal(
            "translate({{ shift }}, 0) translate(20, 10) translate(10, 0)",
            Written(viewer, "group"));
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

        Dispatcher.UIThread.RunJobs();

        // The same shape and the same drag as the rectangle's corner test: 20..40 pulled out to 60.
        Drag(window, viewer, (40f, 40f), (60f, 60f));

        Assert.Equal("20,60 60,60 60,20", Attribute(viewer, "box", "points"));
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

        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (30f, 30f), (50f, 40f));

        Assert.Equal("40", Attribute(viewer, "box", "x"));
        Assert.Equal("30", Attribute(viewer, "box", "y"));
        Assert.Null(Written(viewer));
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

    /// <summary>A left drag never moves the view: it sweeps, and writes nothing.</summary>
    /// <remarks>
    /// One meaning for one drag. Two, settled by a toggle somewhere else, is a pointer being fought
    /// over — and the view has gestures of its own that nothing else wants.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Drag_Sweeps_Rather_Than_Panning()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        var offset = viewer.Canvas.OffsetX;

        Drag(window, viewer, (5f, 5f), (95f, 95f));

        Assert.Null(Written(viewer));
        Assert.Equal(offset, viewer.Canvas.OffsetX);

        // And it selected what it went round, which is the whole of what a sweep is for.
        Assert.Single(viewer.Elements.SelectedNodes);
    }

    /// <summary>The view is moved by the button that was always for moving it.</summary>
    [AvaloniaFact]
    public async Task The_Middle_Button_Moves_The_View()
    {
        var (window, viewer) = await Host(Plain);

        var offset = viewer.Canvas.OffsetX;

        window.MouseDown(At(window, viewer, 30f, 30f), MouseButton.Middle);
        window.MouseMove(At(window, viewer, 60f, 30f), RawInputModifiers.MiddleMouseButton);
        window.MouseUp(At(window, viewer, 60f, 30f), MouseButton.Middle);
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(offset, viewer.Canvas.OffsetX);
    }
}
