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

    /// <summary>The strip's button for a tool, by the tool it carries.</summary>
    private static ToggleButton Tool(SvgViewer viewer, string shape)
        => viewer.GetVisualDescendants().OfType<ToggleButton>().Single(button => Equals(button.Tag, shape));

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

    // ---- point tools ---------------------------------------------------------------------------

    /// <summary>Arms a tool that has no key, by its button.</summary>
    private static void Press(SvgViewer viewer, string shape)
    {
        Tool(viewer, shape).IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        viewer.Canvas.Focus();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task A_Polygon_Is_Its_Clicks_And_Enter_Finishes_It()
    {
        var (window, viewer) = await Host();

        Press(viewer, "polygon");
        Click(window, viewer, 2f, 2f);
        Click(window, viewer, 10f, 2f);

        // Not yet a shape: two corners, and the file untouched.
        Assert.Equal(1, viewer.Source.Split("<").Length - 1 - 2);

        Click(window, viewer, 6f, 8f);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("polygon", Name(viewer, "1"));
        Assert.Equal("points=2,2 10,2 6,8", Written(viewer, "1", "points"));
        Assert.Equal("fill=-", Written(viewer, "1", "fill"));
        Assert.Equal(new[] { "1" }, viewer.Elements.SelectedAddresses);
        Assert.Equal("draw a polygon", viewer.UndoLabel);
    }

    [AvaloniaFact]
    public async Task Clicking_The_First_Corner_Again_Finishes_A_Polygon_And_Backspace_Takes_One_Back()
    {
        var (window, viewer) = await Host();

        Press(viewer, "polygon");
        Click(window, viewer, 2f, 2f);
        Click(window, viewer, 10f, 2f);
        Click(window, viewer, 12f, 12f);

        window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Click(window, viewer, 6f, 8f);
        Click(window, viewer, 2f, 2f);

        Assert.Equal("points=2,2 10,2 6,8", Written(viewer, "1", "points"));
    }

    [AvaloniaFact]
    public async Task A_Polyline_Is_Open_Gets_A_Stroke_And_No_Fill_And_Ends_On_Its_Last_Point()
    {
        var (window, viewer) = await Host();

        Press(viewer, "polyline");
        Click(window, viewer, 2f, 2f);
        Click(window, viewer, 10f, 2f);
        Click(window, viewer, 10f, 10f);

        // What a double click is: a second click on the last point.
        Click(window, viewer, 10f, 10f);

        Assert.Equal("polyline", Name(viewer, "1"));
        Assert.Equal("points=2,2 10,2 10,10 stroke=currentColor fill=none", Written(viewer, "1", "points", "stroke", "fill"));
    }

    [AvaloniaFact]
    public async Task A_Point_Dragged_Lands_Where_It_Was_Let_Go()
    {
        var (window, viewer) = await Host();

        Press(viewer, "polyline");
        Drag(window, viewer, (2f, 2f), (4f, 4f));
        Click(window, viewer, 10f, 10f);
        Click(window, viewer, 10f, 10f);

        Assert.Equal("points=4,4 10,10", Written(viewer, "1", "points"));
    }

    [AvaloniaFact]
    public async Task The_Pen_Writes_A_Corner_For_A_Click_And_A_Curve_For_A_Drag_And_Closes_On_Its_First_Point()
    {
        var (window, viewer) = await Host();

        Arm(window, PhysicalKey.P);
        Click(window, viewer, 2f, 2f);
        Drag(window, viewer, (10f, 2f), (12f, 4f));
        Click(window, viewer, 2f, 2f);

        Assert.Equal("path", Name(viewer, "1"));

        // The drag is the handle leaving its point, and its mirror is the one arriving; a point
        // with no handle puts its control point on itself, so the curve is straight at that end.
        Assert.Equal("d=M 2 2 C 2 2 8 0 10 2 C 12 4 2 2 2 2 Z", Written(viewer, "1", "d"));
        Assert.Equal("fill=- stroke=-", Written(viewer, "1", "fill", "stroke"));
        Assert.Equal("draw a path", viewer.UndoLabel);
    }

    [AvaloniaFact]
    public async Task An_Open_Path_Ends_With_Enter_And_Is_Stroked()
    {
        var (window, viewer) = await Host();

        Arm(window, PhysicalKey.P);
        Click(window, viewer, 2f, 2f);
        Click(window, viewer, 10f, 2f);
        Click(window, viewer, 10f, 10f);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("d=M 2 2 L 10 2 L 10 10 stroke=currentColor fill=none", Written(viewer, "1", "d", "stroke", "fill"));
    }

    [AvaloniaFact]
    public async Task Escape_Drops_A_Shape_In_The_Making_And_Enter_Needs_Enough_Points()
    {
        var (window, viewer) = await Host();
        var was = viewer.Source;

        Press(viewer, "polygon");
        Click(window, viewer, 2f, 2f);
        Click(window, viewer, 10f, 2f);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(was, viewer.Source);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        // Dropped, and the tool still armed: three fresh clicks make a polygon of their own.
        Click(window, viewer, 4f, 4f);
        Click(window, viewer, 12f, 4f);
        Click(window, viewer, 8f, 12f);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("points=4,4 12,4 8,12", Written(viewer, "1", "points"));
    }

    [AvaloniaFact]
    public async Task A_Rebuild_Under_A_Shape_In_The_Making_Drops_It()
    {
        var (window, viewer) = await Host();

        Press(viewer, "polygon");
        Click(window, viewer, 2f, 2f);
        Click(window, viewer, 10f, 2f);

        // An edit from elsewhere — a typed source, an undo — replaces the drawing under the points.
        Assert.True(viewer.SetSource(viewer.Source.Replace("<!-- -->", string.Empty) + "<!-- typed -->"));
        Dispatcher.UIThread.RunJobs();

        Click(window, viewer, 6f, 8f);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("<polygon", viewer.Source);
    }

    [AvaloniaFact]
    public async Task A_Tool_Keeps_Its_Cursor_Past_A_Click_And_A_Button_Hands_The_Canvas_The_Keys()
    {
        var (window, viewer) = await Host();

        // Nothing has focused the canvas: the strip's button is what the hand pressed.
        window.Focus();
        Dispatcher.UIThread.RunJobs();

        Tool(viewer, "rect").IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.Canvas.IsFocused);
        Assert.NotNull(viewer.Canvas.Cursor);

        // Giving the pointer up is a capture lost, which used to put the arrow back.
        Click(window, viewer, 10f, 10f);

        Assert.NotNull(viewer.Canvas.Cursor);
        Assert.True(Tool(viewer, "rect").IsChecked);

        Drag(window, viewer, (8f, 8f), (12f, 12f));

        Assert.Equal("rect", Name(viewer, "1"));
        Assert.Null(viewer.Canvas.Cursor);
    }

    [AvaloniaFact]
    public async Task The_Palette_Follows_The_Key_And_The_Key_Follows_The_Palette()
    {
        var (window, viewer) = await Host();

        var rect = Tool(viewer, "rect");
        var select = Tool(viewer, "select");

        Assert.True(select.IsChecked);

        Arm(window, PhysicalKey.R);

        Assert.True(rect.IsChecked);
        Assert.False(select.IsChecked);
        Assert.Null(viewer.Canvas.Gizmo);

        select.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(rect.IsChecked);

        Drag(window, viewer, (8f, 8f), (20f, 14f));

        // A sweep, since nothing is armed: the drawing is as it was.
        Assert.Equal(1, viewer.Source.Split("<rect").Length - 1);

        // Pressing the tool that is down puts it away, which is Select again.
        rect.IsChecked = true;
        rect.IsChecked = false;
        Dispatcher.UIThread.RunJobs();

        Assert.True(select.IsChecked);
    }

    // ---- a clip path or a mask for what is drawn -----------------------------------------------

    private static Svg.Skia.SKSvg Load(string markup)
    {
        var svg = new Svg.Skia.SKSvg();

        Assert.NotNull(svg.FromSvg(markup));

        return svg;
    }

    private static SvgElement ById(Svg.Skia.SKSvg svg, string id) => svg.SourceDocument!.GetElementById(id);

    /// <summary>In the element's own space, and round its ink: a group's stroke is its children's.</summary>
    [AvaloniaFact]
    public void A_New_Clip_Path_Starts_As_The_Box_Round_The_Element_And_Its_Stroke()
    {
        var svg = Load("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40" width="80" height="80">
              <rect id="framed" transform="translate(20 0)" x="2" y="2" width="8" height="8" fill="#3366cc" stroke="#000000" stroke-width="2" />
              <g id="strokes" transform="scale(2)" fill="none" stroke="#000000" stroke-width="2"><path d="M2 12 L8 12" /></g>
              <g id="empty" />
            </svg>
            """);

        Assert.Null(SvgViewerDraw.Covering(svg, ById(svg, "framed"), mask: false, out var clip, out var region));
        Assert.Equal("""<path d="M 1 1 L 11 1 L 11 11 L 1 11 Z" />""", clip!.ToString());
        Assert.Equal("1 1 10 10", region);

        Assert.Null(SvgViewerDraw.Covering(svg, ById(svg, "strokes"), mask: true, out var mask, out _));
        Assert.Equal("""<path d="M 2 11 L 8 11 L 8 13 L 2 13 Z" fill="white" />""", mask!.ToString());

        Assert.Equal(
            "Nothing of that is drawn, so a clip path would have nothing to cover.",
            SvgViewerDraw.Covering(svg, ById(svg, "empty"), mask: false, out var none, out var nowhere));
        Assert.Null(none);
        Assert.Null(nowhere);
    }

    /// <summary>A marker is drawn under the shape's clip too, so the box takes in the arrowhead as well as the line.</summary>
    [AvaloniaFact]
    public void A_New_Clip_Path_Covers_The_Markers_A_Line_Ends_In()
    {
        var svg = Load("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
              <defs>
                <marker id="arrow" markerWidth="6" markerHeight="6" refX="0" refY="3" orient="auto" markerUnits="userSpaceOnUse">
                  <path d="M0 0 L6 3 L0 6 Z" />
                </marker>
              </defs>
              <line id="pointer" x1="10" y1="50" x2="90" y2="50" stroke="#000000" stroke-width="2" marker-end="url(#arrow)" />
            </svg>
            """);

        Assert.Null(SvgViewerDraw.Covering(svg, ById(svg, "pointer"), mask: false, out var clip, out var region));
        Assert.Equal("""<path d="M 10 47 L 96 47 L 96 53 L 10 53 Z" />""", clip!.ToString());
        Assert.Equal("10 47 86 6", region);
    }

    /// <summary>A shape drawn beside a moved group goes into that group's space, so it is carried back by as much.</summary>
    [AvaloniaFact]
    public void A_Shape_Outside_A_Moved_Group_Is_Carried_Back_By_The_Groups_Move()
    {
        var svg = Load("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40" width="80" height="80">
              <g transform="translate(10 5)"><rect id="moved" width="10" height="10" /><circle id="beside" cx="5" cy="5" r="2" /></g>
              <g transform="scale(2)"><rect id="doubled" width="10" height="10" /></g>
              <circle id="spot" cx="15" cy="10" r="4" />
            </svg>
            """);

        Assert.Equal("translate(-10, -5)", SvgViewerDraw.Carried(svg, ById(svg, "spot"), ById(svg, "moved")));
        Assert.Equal("matrix(0.5, 0, 0, 0.5, 0, 0)", SvgViewerDraw.Carried(svg, ById(svg, "spot"), ById(svg, "doubled")));

        // Already in the space it would be drawn in.
        Assert.Null(SvgViewerDraw.Carried(svg, ById(svg, "beside"), ById(svg, "moved")));
    }

    /// <summary>A &lt;switch&gt; draws what it holds where it stands, so a shape in one is carried like a shape in a group.</summary>
    [AvaloniaFact]
    public void A_Shape_In_A_Switch_Is_Carried_Back_By_The_Groups_Above_It()
    {
        var svg = Load("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40" width="40" height="40">
              <rect id="target" width="10" height="10" />
              <g transform="translate(10 0)"><switch><rect id="switched" width="5" height="5" /></switch></g>
            </svg>
            """);

        Assert.Equal("translate(10, 0)", SvgViewerDraw.Carried(svg, ById(svg, "switched"), ById(svg, "target")));
    }

    [AvaloniaFact]
    public void A_Shape_In_Defs_Is_Taken_As_Written()
    {
        var svg = Load("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40" width="40" height="40">
              <defs><circle id="spare" cx="5" cy="5" r="4" /></defs>
              <g transform="translate(10 5)"><rect id="moved" width="10" height="10" /></g>
            </svg>
            """);

        Assert.Null(SvgViewerDraw.Carried(svg, ById(svg, "spare"), ById(svg, "moved")));
    }
}
