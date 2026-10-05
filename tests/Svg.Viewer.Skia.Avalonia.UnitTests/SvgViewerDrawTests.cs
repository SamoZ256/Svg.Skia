// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Svg.SourceEditing;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// Drawing a shape onto the drawing with a tool armed.
/// </summary>
/// <remarks>
/// Every number asserted is in the parent's own units, which is the point: the pointer is on screen,
/// the drawing has a viewBox, and the shape may land inside a group that scales what it holds. What
/// the file says is the only thing that matters, so the tests read it back by address rather than
/// by an id a new shape is not given.
/// </remarks>
public class SvgViewerDrawTests
{
    private const string Plain = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect id="back" x="0" y="0" width="4" height="4" fill="#3366cc" />
        </svg>
        """;

    /// <summary>A page drawn at twice its own units, so the picture and the file disagree about every number.</summary>
    private const string Scaled = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="48" height="48">
          <rect id="back" x="0" y="0" width="4" height="4" fill="#3366cc" />
        </svg>
        """;

    private const string Grouped = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <g id="wrap" transform="translate(10 0) scale(2)">
            <rect x="0" y="0" width="2" height="2" />
          </g>
          <defs>
            <clipPath id="cut"><rect width="1" height="1" /></clipPath>
          </defs>
        </svg>
        """;

    private const string Icon = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <g id="strokes" fill="none" stroke="currentColor" stroke-width="2">
            <path d="M2 2h4" />
          </g>
        </svg>
        """;

    private const RawInputModifiers Held = RawInputModifiers.LeftMouseButton;

    private static async Task<(Window Window, SvgViewer Viewer)> Host(string drawing = Plain, bool snapping = false)
    {
        var viewer = new SvgViewer { Grid = new SvgViewerGrid(8f, 30f), SnapsToGrid = snapping };
        var window = new Window { Width = 400, Height = 400, Background = Brushes.White, Content = viewer };

        window.Show();

        Assert.True(await viewer.LoadTextAsync(drawing));
        Dispatcher.UIThread.RunJobs();

        viewer.Canvas.Focus();
        Dispatcher.UIThread.RunJobs();

        return (window, viewer);
    }

    /// <summary>Where a point of the drawing is in the window.</summary>
    private static Point At(Window window, SvgViewer viewer, float x, float y)
    {
        Assert.True(viewer.Canvas.TryGetControlPoint(new SKPoint(x, y), out var point));

        return viewer.Canvas.TranslatePoint(point, window)
               ?? throw new InvalidOperationException("The canvas is not in the window.");
    }

    private static void Arm(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Drag(Window window, SvgViewer viewer, (float X, float Y) from, (float X, float Y) to, RawInputModifiers held = RawInputModifiers.None)
    {
        window.MouseDown(At(window, viewer, from.X, from.Y), MouseButton.Left, held);
        Dispatcher.UIThread.RunJobs();

        window.MouseMove(At(window, viewer, to.X, to.Y), Held | held);
        Dispatcher.UIThread.RunJobs();

        window.MouseUp(At(window, viewer, to.X, to.Y), MouseButton.Left, held);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Window window, SvgViewer viewer, float x, float y)
    {
        var at = At(window, viewer, x, y);

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The attributes of the element the file spells at <paramref name="addressKey"/>, as one line.</summary>
    private static string Written(SvgViewer viewer, string addressKey, params string[] names)
    {
        var source = SvgSourceDocument.Read(viewer.Source, out _)!;

        return string.Join(" ", names.Select(name => $"{name}={SvgAttributeEditor.Attribute(source, addressKey, name) ?? "-"}"));
    }

    private static string Name(SvgViewer viewer, string addressKey)
        => viewer.Elements.Root!.Flatten().Single(node => node.AddressKey == addressKey).Label;

    [AvaloniaFact]
    public async Task A_Rectangle_Is_Written_In_The_Drawings_Units_Last_In_The_Root_And_Selected()
    {
        var (window, viewer) = await Host();

        Arm(window, PhysicalKey.R);
        Drag(window, viewer, (8f, 8f), (20f, 14f));

        Assert.Equal("rect", Name(viewer, "1"));
        Assert.Equal("x=8 y=8 width=12 height=6", Written(viewer, "1", "x", "y", "width", "height"));

        // Under a bare root a closed shape inherits the default black fill, so nothing is written.
        Assert.Equal("fill=-", Written(viewer, "1", "fill"));

        // One shot: the new row is picked, with its handles, and the hand is back to selecting.
        Assert.Equal(new[] { "1" }, viewer.Elements.SelectedAddresses);
        Assert.NotNull(viewer.Canvas.Gizmo);
        Assert.Equal("draw a rectangle", viewer.UndoLabel);
    }

    [AvaloniaFact]
    public async Task A_Shape_Is_Written_In_The_ViewBox_Units_Not_The_Pictures()
    {
        var (window, viewer) = await Host(Scaled);

        Arm(window, PhysicalKey.R);

        // The points are the picture's, which is 48 across; the file is 24 across.
        Drag(window, viewer, (16f, 16f), (40f, 28f));

        Assert.Equal("x=8 y=8 width=12 height=6", Written(viewer, "1", "x", "y", "width", "height"));
    }

    [AvaloniaFact]
    public async Task Shift_Squares_The_Box_From_The_Pressed_Corner()
    {
        var (window, viewer) = await Host();

        Arm(window, PhysicalKey.R);
        Drag(window, viewer, (20f, 20f), (8f, 16f), RawInputModifiers.Shift);

        // The longer side, back towards where the pointer went: the pressed corner stays put.
        Assert.Equal("x=8 y=8 width=12 height=12", Written(viewer, "1", "x", "y", "width", "height"));
    }

    [AvaloniaFact]
    public async Task An_Ellipse_Is_Written_By_Its_Centre_And_A_Circle_With_Shift()
    {
        var (window, viewer) = await Host();

        Arm(window, PhysicalKey.O);
        Drag(window, viewer, (2f, 4f), (14f, 10f));

        Assert.Equal("ellipse", Name(viewer, "1"));
        Assert.Equal("cx=8 cy=7 rx=6 ry=3", Written(viewer, "1", "cx", "cy", "rx", "ry"));

        Arm(window, PhysicalKey.O);
        Drag(window, viewer, (2f, 2f), (10f, 6f), RawInputModifiers.Shift);

        Assert.Equal("circle", Name(viewer, "2"));
        Assert.Equal("cx=6 cy=6 r=4", Written(viewer, "2", "cx", "cy", "r"));
        Assert.Equal("draw a circle", viewer.UndoLabel);
    }

    [AvaloniaFact]
    public async Task A_Line_Gets_A_Stroke_Where_It_Would_Have_None_And_Shift_Holds_It_To_Eighths()
    {
        var (window, viewer) = await Host();

        Arm(window, PhysicalKey.L);
        Drag(window, viewer, (2f, 2f), (10f, 6f));

        Assert.Equal("line", Name(viewer, "1"));
        Assert.Equal("x1=2 y1=2 x2=10 y2=6 stroke=currentColor", Written(viewer, "1", "x1", "y1", "x2", "y2", "stroke"));

        Arm(window, PhysicalKey.L);
        Drag(window, viewer, (2f, 2f), (10f, 3f), RawInputModifiers.Shift);

        // Nearly flat, so flat — at the length the hand drew.
        Assert.Equal("x2=10.062 y2=2", Written(viewer, "2", "x2", "y2"));
    }

    [AvaloniaFact]
    public async Task Text_Is_Placed_By_A_Click_At_A_Size_Chosen_On_Screen()
    {
        var (window, viewer) = await Host();

        Arm(window, PhysicalKey.T);
        Click(window, viewer, 4f, 12f);

        Assert.Equal("text", Name(viewer, "1"));
        Assert.Equal("x=4 y=12", Written(viewer, "1", "x", "y"));

        // Sixteen pixels on screen, in a drawing that is 24 units across the canvas's zoom.
        var size = float.Parse(SvgAttributeEditor.Attribute(SvgSourceDocument.Read(viewer.Source, out _)!, "1", "font-size")!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(16f / (float)viewer.Canvas.Scale, size, 2);
        Assert.Contains(">Text</text>", viewer.Source);
        Assert.Equal("add text", viewer.UndoLabel);
    }

    [AvaloniaFact]
    public async Task A_Click_With_A_Box_Tool_Writes_Nothing_And_Leaves_The_Tool_Armed()
    {
        var (window, viewer) = await Host();
        var was = viewer.Source;

        Arm(window, PhysicalKey.R);
        Click(window, viewer, 10f, 10f);

        Assert.Equal(was, viewer.Source);

        // Still armed: the next drag draws.
        Drag(window, viewer, (8f, 8f), (12f, 12f));

        Assert.Equal("rect", Name(viewer, "1"));
    }

    [AvaloniaFact]
    public async Task Escape_Drops_The_Shape_Mid_Drag_And_Then_The_Tool()
    {
        var (window, viewer) = await Host();
        var was = viewer.Source;

        Arm(window, PhysicalKey.R);

        window.MouseDown(At(window, viewer, 8f, 8f), MouseButton.Left);
        window.MouseMove(At(window, viewer, 16f, 16f), Held);
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.MouseUp(At(window, viewer, 16f, 16f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(was, viewer.Source);

        // Idle, Escape puts the tool away: a press is a pick again.
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Click(window, viewer, 2f, 2f);

        Assert.Equal(new[] { "0" }, viewer.Elements.SelectedAddresses);
    }

    [AvaloniaFact]
    public async Task A_Shape_Lands_On_The_Grid_Where_Snapping_Is_On()
    {
        var (window, viewer) = await Host(snapping: true);

        Arm(window, PhysicalKey.R);

        // Near the lines of eight, not on them: the grid is magnetic, and pulls from a few pixels off.
        Drag(window, viewer, (7.8f, 8.3f), (16.2f, 15.7f));

        Assert.Equal("x=8 y=8 width=8 height=8", Written(viewer, "1", "x", "y", "width", "height"));
    }

    [AvaloniaFact]
    public async Task A_Shape_Drawn_Into_A_Selected_Group_Is_Written_In_The_Groups_Own_Units()
    {
        var (window, viewer) = await Host(Grouped);

        Assert.True(viewer.Elements.TrySelect("0"));
        Dispatcher.UIThread.RunJobs();

        Arm(window, PhysicalKey.R);
        Drag(window, viewer, (12f, 4f), (16f, 8f));

        // Inside #wrap, last: the group moves everything ten across and doubles it, so a box from
        // 12 to 16 on the page is from 1 to 3 in the group, and two across.
        Assert.Equal("rect", Name(viewer, "0/1"));
        Assert.Equal("x=1 y=2 width=2 height=2", Written(viewer, "0/1", "x", "y", "width", "height"));
        Assert.Equal(new[] { "0/1" }, viewer.Elements.SelectedAddresses);
    }

    [AvaloniaFact]
    public async Task A_Shape_Drawn_With_A_Shape_Selected_Goes_Just_Above_It()
    {
        var (window, viewer) = await Host(Grouped);

        Assert.True(viewer.Elements.TrySelect("0/0"));
        Dispatcher.UIThread.RunJobs();

        Arm(window, PhysicalKey.O);
        Drag(window, viewer, (12f, 4f), (16f, 8f));

        Assert.Equal(new[] { "svg", "g #wrap", "rect", "ellipse", "defs", "clipPath #cut", "rect" }, viewer.Elements.Root!.Flatten().Select(node => node.ToString()).ToArray());
        Assert.Equal("cx=2 cy=3 rx=1 ry=1", Written(viewer, "0/1", "cx", "cy", "rx", "ry"));
    }

    [AvaloniaFact]
    public async Task A_Selection_Inside_The_Defs_Sends_The_Shape_To_The_Root()
    {
        var (window, viewer) = await Host(Grouped);

        // The clip path's own rect: nothing drawn by hand belongs beside it.
        Assert.True(viewer.Elements.TrySelect("1/0/0"));
        Dispatcher.UIThread.RunJobs();

        Arm(window, PhysicalKey.R);
        Drag(window, viewer, (2f, 2f), (6f, 6f));

        Assert.Equal("rect", Name(viewer, "2"));
        Assert.Equal("x=2 y=2 width=4 height=4", Written(viewer, "2", "x", "y", "width", "height"));
    }

    [AvaloniaFact]
    public async Task A_Shape_Drawn_Into_A_Stroked_Icon_Group_Inherits_Its_Paint()
    {
        var (window, viewer) = await Host(Icon);

        Assert.True(viewer.Elements.TrySelect("0"));
        Dispatcher.UIThread.RunJobs();

        Arm(window, PhysicalKey.R);
        Drag(window, viewer, (4f, 4f), (10f, 10f));

        Arm(window, PhysicalKey.L);
        Drag(window, viewer, (4f, 12f), (10f, 12f));

        // Neither says anything about paint: the group's fill="none" stroke="currentColor" is theirs.
        Assert.Equal("fill=- stroke=-", Written(viewer, "0/1", "fill", "stroke"));
        Assert.Equal("fill=- stroke=-", Written(viewer, "0/2", "fill", "stroke"));
    }

    [AvaloniaFact]
    public async Task A_Drawn_Shape_Is_One_Step_To_Take_Back()
    {
        var (window, viewer) = await Host();
        var was = viewer.Source;

        Arm(window, PhysicalKey.R);
        Drag(window, viewer, (8f, 8f), (20f, 14f));

        Assert.NotEqual(was, viewer.Source);

        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(was, viewer.Source);
    }

    [AvaloniaFact]
    public async Task A_Tool_Armed_Over_The_Selections_Handles_Draws_Rather_Than_Resizes()
    {
        var (window, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect("0"));
        Dispatcher.UIThread.RunJobs();

        Arm(window, PhysicalKey.R);

        // From the selected rect's corner, where a handle sat a moment ago.
        Drag(window, viewer, (4f, 4f), (12f, 12f));

        Assert.Equal("x=0 y=0 width=4 height=4", Written(viewer, "0", "x", "y", "width", "height"));
        Assert.Equal("x=4 y=4 width=8 height=8", Written(viewer, "1", "x", "y", "width", "height"));
    }

    [AvaloniaFact]
    public async Task The_Palette_Follows_The_Key_And_The_Key_Follows_The_Palette()
    {
        var (window, viewer) = await Host();

        var buttons = viewer.GetVisualDescendants().OfType<RadioButton>().ToList();
        var rect = buttons.Single(button => Equals(button.Content, "Rect"));
        var select = buttons.Single(button => Equals(button.Content, "Select"));

        Assert.True(select.IsChecked);

        Arm(window, PhysicalKey.R);

        Assert.True(rect.IsChecked);
        Assert.Null(viewer.Canvas.Gizmo);

        select.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (8f, 8f), (20f, 14f));

        // A sweep, since nothing is armed: the drawing is as it was.
        Assert.Equal(1, viewer.Source.Split("<rect").Length - 1);
    }
}
