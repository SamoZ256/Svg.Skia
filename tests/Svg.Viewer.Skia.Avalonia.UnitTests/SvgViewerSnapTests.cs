// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// Dragging with a grid under it.
/// </summary>
/// <remarks>
/// What each of these asks is which space the lines are in. A gesture is composed in the element's
/// own geometry — before its transform and every ancestor's — and the grid is the board's, so a
/// snap is the one thing that leaves that space and comes back. <see cref="Nested"/> is the case
/// that tells the two apart: the shape covers the same pixels as a plain one and is written in
/// halves, so a snap done in the wrong space lands it twice as far out.
///
/// The step is eight and the turn thirty, neither of them the default and neither a number the
/// drawings are already multiples of, so nothing here can pass by accident.
/// </remarks>
public class SvgViewerSnapTests
{
    /// <summary>A 20x20 shape at 20,20, whose edges are not on a grid of eight.</summary>
    private const string Plain = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <rect id="box" x="20" y="20" width="20" height="20" fill="#3366cc" />
        </svg>
        """;

    /// <summary>The same shape, under a group that doubles everything below it.</summary>
    private const string Nested = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <g transform="scale(2)">
            <rect id="box" x="10" y="10" width="10" height="10" fill="#3366cc" />
          </g>
        </svg>
        """;

    /// <summary>Two shapes, to be swept up and dragged as one.</summary>
    private const string Two = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <rect id="one" x="20" y="20" width="20" height="20" fill="#3366cc" />
          <rect id="two" x="60" y="60" width="20" height="20" fill="#cc3366" />
        </svg>
        """;

    private const RawInputModifiers Held = RawInputModifiers.LeftMouseButton;

    /// <summary>A viewer showing <paramref name="drawing"/>, with the grid already on.</summary>
    private static async Task<(Window Window, SvgViewer Viewer)> Host(string drawing, bool snapping = true)
    {
        var viewer = new SvgViewer
        {
            Grid = new SvgViewerGrid(8f, 30f),
            SnapsToGrid = snapping
        };

        var window = new Window { Width = 400, Height = 400, Background = Brushes.White, Content = viewer };

        window.Show();

        Assert.True(await viewer.LoadTextAsync(drawing));

        Dispatcher.UIThread.RunJobs();

        return (window, viewer);
    }

    private static Point At(Window window, SvgViewer viewer, float x, float y)
    {
        Assert.True(viewer.Canvas.TryGetControlPoint(new SKPoint(x, y), out var point));

        return viewer.Canvas.TranslatePoint(point, window)
               ?? throw new InvalidOperationException("The canvas is not in the window.");
    }

    private static void Select(Window window, SvgViewer viewer, float x, float y)
    {
        var at = At(window, viewer, x, y);

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);

        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(viewer.SelectedElement);
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

    /// <summary>What the file now spells for one of a shape's attributes, or null for none.</summary>
    private static string? Attribute(SvgViewer viewer, string id, string name)
    {
        var source = viewer.Source;
        var at = source.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);

        Assert.True(at >= 0, "The drawing no longer has the shape the test drags.");

        var tag = source.LastIndexOf('<', at);
        var element = source.Substring(tag, source.IndexOf('>', at) - tag);

        var written = element.IndexOf($" {name}=\"", StringComparison.Ordinal);

        if (written < 0)
        {
            return null;
        }

        var value = written + name.Length + 3;

        return element.Substring(value, element.IndexOf('"', value) - value);
    }

    /// <summary>The four numbers a box-shaped element is written with, so a drag is one assertion.</summary>
    private static string Box(SvgViewer viewer, string id = "box")
        => string.Join(
            " ",
            new[] { "x", "y", "width", "height" }.Select(name => Attribute(viewer, id, name) ?? "?"));

    /// <summary>A move puts the shape's own corner on a line, not the pointer.</summary>
    /// <remarks>
    /// The drag is five across and seven down, which would leave the corner at 25,27. What is
    /// written is 24,24 — the nearest lines — and the shape keeps the size it was.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Move_Lands_The_Shape_On_The_Grid()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        Drag(window, viewer, (30f, 30f), (35f, 37f));

        Assert.Equal("24 24 20 20", Box(viewer));

        window.Close();
    }

    /// <summary>
    /// And the lines are the board's, whatever space the shape is written in.
    /// </summary>
    /// <remarks>
    /// The shape is inside a group that doubles it, so it is drawn over the same pixels as the plain
    /// one and written in half of them. Landing its corner on the board's line at 24 is an <c>x</c>
    /// of 12 — snapped in the element's own units it would have gone to 16, which is a corner four
    /// units past the line somebody was aiming at.
    /// </remarks>
    [AvaloniaFact]
    public async Task The_Lines_Are_The_Boards_And_Not_The_Elements()
    {
        var (window, viewer) = await Host(Nested);

        Select(window, viewer, 30f, 30f);

        Drag(window, viewer, (30f, 30f), (35f, 37f));

        Assert.Equal("12 12 10 10", Box(viewer));

        window.Close();
    }

    /// <summary>A handle puts the edge it is dragging on a line, and leaves the other alone.</summary>
    [AvaloniaFact]
    public async Task A_Handle_Lands_The_Edge_On_The_Grid()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        // The right edge, halfway down it, pulled to 47 — which is a line at 48.
        Drag(window, viewer, (40f, 30f), (47f, 30f));

        Assert.Equal("20 20 28 20", Box(viewer));

        window.Close();
    }

    /// <summary>A turn lands on the step, which is its own number because a grid has no angle.</summary>
    [AvaloniaFact]
    public async Task A_Turn_Lands_On_The_Step()
    {
        var (window, viewer) = await Host(Plain);

        Select(window, viewer, 30f, 30f);

        var box = viewer.Canvas.Gizmo;

        Assert.NotNull(box);

        // A hundred and eight degrees round from straight up, which is a step at a hundred and twenty.
        Drag(window, viewer, (box!.Value.RotHandle.X, box.Value.RotHandle.Y), (60f, 40f));

        Assert.Equal("rotate(120, 30, 30)", Attribute(viewer, "box", "transform"));

        window.Close();
    }

    /// <summary>A selection of several lands the box round all of them on a line.</summary>
    /// <remarks>
    /// The union box and not each member: snapping them one at a time would pull the selection
    /// apart, which is the one thing a drag on several of them must not do.
    /// </remarks>
    [AvaloniaFact]
    public async Task Several_Shapes_Land_By_The_Box_Round_Them()
    {
        var (window, viewer) = await Host(Two);

        // Round both, from a corner of the page that is over neither.
        Drag(window, viewer, (5f, 5f), (95f, 95f));

        Assert.Equal(2, viewer.Elements.SelectedNodes.Count);

        Drag(window, viewer, (30f, 30f), (35f, 37f));

        // The box round them starts at 20,20, so both move by the four that puts it on 24,24.
        Assert.Equal("24 24 20 20", Box(viewer, "one"));
        Assert.Equal("64 64 20 20", Box(viewer, "two"));

        window.Close();
    }

    /// <summary>A page edge lands on a line, and takes the frame with it as it always did.</summary>
    [AvaloniaFact]
    public async Task A_Page_Edge_Lands_On_The_Grid()
    {
        var (window, viewer) = await Host(Plain);

        // The page, which is what a click inside the drawing and off its ink selects.
        var corner = At(window, viewer, 5f, 5f);

        window.MouseDown(corner, MouseButton.Left);
        window.MouseUp(corner, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.IsPageSelected);

        // A unit inside the edge rather than on it, which at this zoom is the far right column of
        // the control: the handle reaches well past it, and a press on the boundary itself lands on
        // whatever the canvas is inside.
        Drag(window, viewer, (99f, 50f), (105f, 50f));

        // Five out from the edge, which is a line at 104 — and the frame goes with it, so what is
        // drawn inside the page does not move.
        Assert.Contains("width=\"104\"", viewer.Source, StringComparison.Ordinal);
        Assert.Contains("viewBox=\"0 0 104 100\"", viewer.Source, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>
    /// With the switch off, every one of those writes what it wrote before there was a grid.
    /// </summary>
    /// <remarks>
    /// The steps are still set — what is off is the switch — so this is also what says the two are
    /// held apart, and that a viewer nobody has asked for a grid drags exactly as it used to.
    /// </remarks>
    [AvaloniaFact]
    public async Task With_The_Switch_Off_Nothing_Is_Rounded()
    {
        var (window, viewer) = await Host(Plain, snapping: false);

        Assert.Equal(8f, viewer.Grid.Step);

        Select(window, viewer, 30f, 30f);

        Drag(window, viewer, (30f, 30f), (35f, 37f));

        Assert.Equal("25 27 20 20", Box(viewer));

        window.Close();
    }

    /// <summary>The toolbar's toggle is the property, and says so to whoever is keeping it.</summary>
    [AvaloniaFact]
    public async Task The_Toolbar_Toggle_Is_The_Same_Switch()
    {
        var (window, viewer) = await Host(Plain, snapping: false);

        var said = 0;

        viewer.SnapChanged += (_, _) => said++;

        var toggle = viewer.GetVisualDescendants()
            .OfType<ToggleButton>()
            .Single(button => button.Name == "SnapButton");

        toggle.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.SnapsToGrid);
        Assert.Equal(1, said);

        // And back the other way, which is the host telling the viewer rather than a hand: the
        // button follows, and nobody is told what they have just said.
        viewer.SnapsToGrid = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(toggle.IsChecked);
        Assert.Equal(1, said);

        window.Close();
    }
}
