// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.IO;
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
/// Reshaping a drawn shape a point at a time with the points tool.
/// </summary>
/// <remarks>
/// What is asserted is what the file says, read back by address: the drag is on screen, the numbers
/// are the element's own, and a press is two pointer events through the canvas like any other.
/// </remarks>
public class SvgViewerPointsTests
{
    private const string Bent = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <path d="M4 4 L20 4 L20 20" fill="none" stroke="#000000" />
        </svg>
        """;

    private const string Grouped = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <g transform="translate(10 0) scale(2)">
            <path d="M0 0 L4 0 L4 4" fill="none" stroke="#000000" />
          </g>
        </svg>
        """;

    /// <summary>A stroke of four points, so several can be chosen and some still left.</summary>
    private const string Square = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <path d="M4 4 L20 4 L20 20 L4 20" fill="none" stroke="#000000" />
        </svg>
        """;

    private const RawInputModifiers Held = RawInputModifiers.LeftMouseButton;

    private static async Task<(Window Window, SvgViewer Viewer)> Host(string drawing = Bent, bool snapping = false)
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

    private static Point At(Window window, SvgViewer viewer, float x, float y)
    {
        Assert.True(viewer.Canvas.TryGetControlPoint(new SKPoint(x, y), out var point));

        return viewer.Canvas.TranslatePoint(point, window)
               ?? throw new InvalidOperationException("The canvas is not in the window.");
    }

    private static void Click(Window window, SvgViewer viewer, float x, float y)
    {
        var at = At(window, viewer, x, y);

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void ShiftClick(Window window, SvgViewer viewer, float x, float y)
    {
        var at = At(window, viewer, x, y);

        window.MouseDown(at, MouseButton.Left, RawInputModifiers.Shift);
        window.MouseUp(at, MouseButton.Left, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The chosen points, where they are drawn.</summary>
    private static SKPoint[] Chosen(SvgViewer viewer)
        => viewer.Canvas.Points!.Chosen.Select(chosen => chosen.At).OrderBy(at => at.Y).ThenBy(at => at.X).ToArray();

    /// <summary>Two clicks in one place, the second counted as the second of a run.</summary>
    /// <remarks>Raised directly with its count, rather than left to two real clicks coming close enough together.</remarks>
    private static void DoubleClick(Window window, SvgViewer viewer, float x, float y)
    {
        Click(window, viewer, x, y);

        // A pointer event reports its position by way of the visual root, so the point is the window's.
        var canvas = viewer.Canvas;
        var at = At(window, viewer, x, y);
        var pointer = new Pointer(0, PointerType.Mouse, true);

        canvas.RaiseEvent(new PointerPressedEventArgs(
            canvas,
            pointer,
            window,
            at,
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None,
            2)
        {
            RoutedEvent = InputElement.PointerPressedEvent
        });
        canvas.RaiseEvent(new PointerReleasedEventArgs(
            canvas,
            pointer,
            window,
            at,
            0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None,
            MouseButton.Left)
        {
            RoutedEvent = InputElement.PointerReleasedEvent
        });
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

    private static void Press(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static string Written(SvgViewer viewer, string addressKey, string name)
        => SvgAttributeEditor.Attribute(SvgSourceDocument.Read(viewer.Source, out _)!, addressKey, name) ?? "-";

    /// <summary>What the file spells for an attribute of the element with <paramref name="id"/>.</summary>
    private static string WrittenOn(SvgViewer viewer, string id, string name)
        => SvgSourceDocument.Read(viewer.Source, out _)!.Document.Descendants()
            .Single(element => (string?)element.Attribute("id") == id)
            .Attribute(name)?.Value ?? "-";

    /// <summary>Selects by name, the way the tree reaches what a click cannot.</summary>
    private static void SelectById(SvgViewer viewer, string id)
    {
        var element = viewer.Canvas.Svg?.SourceDocument?.GetElementById(id);

        Assert.NotNull(element);
        Assert.True(viewer.Elements.TrySelect(SvgElementAddress.Create(element!).Key));

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(id, viewer.SelectedElement?.ID);
    }

    private static ToggleButton Tool(SvgViewer viewer, string tool)
        => viewer.GetVisualDescendants().OfType<ToggleButton>().Single(button => Equals(button.Tag, tool));

    /// <summary>Selects the path by its ink and takes hold of its points the way a hand would: twice.</summary>
    private static async Task<(Window Window, SvgViewer Viewer)> Reshaping(string drawing = Bent, bool snapping = false, (float, float)? ink = null)
    {
        var (window, viewer) = await Host(drawing, snapping);
        var on = ink ?? (drawing == Grouped ? (14f, 0f) : (12f, 4f));

        DoubleClick(window, viewer, on.Item1, on.Item2);

        Assert.True(Tool(viewer, "points").IsChecked);

        return (window, viewer);
    }

    [AvaloniaFact]
    public async Task A_Double_Click_On_A_Shape_Shows_Its_Points()
    {
        var (_, viewer) = await Reshaping();

        Assert.Null(viewer.Canvas.Gizmo);
        Assert.Equal(
            new[] { new SKPoint(4f, 4f), new SKPoint(20f, 4f), new SKPoint(20f, 20f) },
            viewer.Canvas.Points!.Anchors);
    }

    [AvaloniaFact]
    public async Task A_And_Enter_Take_Hold_Of_The_Selected_Shapes_Points()
    {
        var (window, viewer) = await Host();

        Click(window, viewer, 12f, 4f);
        Press(window, PhysicalKey.A);

        Assert.True(Tool(viewer, "points").IsChecked);
        Assert.NotNull(viewer.Canvas.Points);

        Press(window, PhysicalKey.V);

        Assert.True(Tool(viewer, "select").IsChecked);
        Assert.Null(viewer.Canvas.Points);

        Press(window, PhysicalKey.Enter);

        Assert.True(Tool(viewer, "points").IsChecked);
    }

    [AvaloniaFact]
    public async Task Dragging_A_Point_Writes_The_Path_As_One_Step()
    {
        var (window, viewer) = await Reshaping();
        var before = viewer.Source;

        Drag(window, viewer, (20f, 4f), (22f, 6f));

        Assert.Equal("M4 4 L22 6 L20 20", Written(viewer, "0", "d"));

        // The point is still the chosen one on the shape the commit rebuilt.
        Assert.Equal(new SKPoint(22f, 6f), viewer.Canvas.Points!.Chosen.Single().At);

        Assert.True(viewer.Undo());
        Assert.Equal(before, viewer.Source);
    }

    /// <summary>The marks follow the hand while it is down, not only once the drag is written.</summary>
    [AvaloniaFact]
    public async Task The_Points_Follow_The_Drag_Before_It_Is_Let_Go()
    {
        var (window, viewer) = await Reshaping();

        window.MouseDown(At(window, viewer, 20f, 4f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        window.MouseMove(At(window, viewer, 22f, 6f), Held);
        Dispatcher.UIThread.RunJobs();

        var marks = viewer.Canvas.Points!;

        Assert.Equal(new SKPoint(22f, 6f), marks.Chosen.Single().At);
        Assert.Contains(new SKPoint(22f, 6f), marks.Anchors);
        Assert.DoesNotContain(new SKPoint(20f, 4f), marks.Anchors);

        window.MouseUp(At(window, viewer, 22f, 6f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task A_Point_Inside_A_Transformed_Group_Is_Written_In_The_Groups_Units()
    {
        var (window, viewer) = await Reshaping(Grouped);

        Drag(window, viewer, (18f, 0f), (20f, 2f));

        Assert.Equal("M0 0 L5 1 L4 4", Written(viewer, "0/0", "d"));
    }

    [AvaloniaFact]
    public async Task A_Dragged_Point_Snaps_To_The_Grid()
    {
        var (window, viewer) = await Reshaping(snapping: true);

        Drag(window, viewer, (20f, 4f), (23f, 9f));

        Assert.Equal("M4 4 L24 8 L20 20", Written(viewer, "0", "d"));
    }

    /// <summary>Between the points the press is not the tool's, and the shape does not go with it.</summary>
    [AvaloniaFact]
    public async Task A_Press_On_The_Outline_Does_Not_Carry_The_Shape()
    {
        var (window, viewer) = await Reshaping();

        Drag(window, viewer, (12f, 4f), (12f, 12f));

        Assert.Equal("M4 4 L20 4 L20 20", Written(viewer, "0", "d"));
    }

    [AvaloniaFact]
    public async Task Delete_Takes_Out_The_Chosen_Point_And_Not_The_Shape()
    {
        var (window, viewer) = await Reshaping();

        Click(window, viewer, 20f, 4f);
        Press(window, PhysicalKey.Delete);

        Assert.Equal("M4 4 L20 20", Written(viewer, "0", "d"));
        Assert.Empty(viewer.Canvas.Points!.Chosen);
    }

    [AvaloniaFact]
    public async Task Escape_Lets_Go_Of_The_Point_And_Then_Of_The_Tool()
    {
        var (window, viewer) = await Reshaping();

        Click(window, viewer, 20f, 4f);

        Assert.Equal(new SKPoint(20f, 4f), viewer.Canvas.Points!.Chosen.Single().At);

        Press(window, PhysicalKey.Escape);

        Assert.Empty(viewer.Canvas.Points!.Chosen);
        Assert.True(Tool(viewer, "points").IsChecked);

        Press(window, PhysicalKey.Escape);

        Assert.True(Tool(viewer, "select").IsChecked);
        Assert.Null(viewer.Canvas.Points);
    }

    [AvaloniaFact]
    public async Task A_Double_Click_On_The_Outline_Adds_A_Point_There()
    {
        var (window, viewer) = await Reshaping();

        DoubleClick(window, viewer, 12f, 4f);

        Assert.Equal("M4 4 L12 4 L20 4 L20 20", Written(viewer, "0", "d"));
        Assert.Equal(new SKPoint(12f, 4f), viewer.Canvas.Points!.Chosen.Single().At);
    }

    /// <summary>
    /// The first click of a double-click on another shape selects it, so the second is on a shape
    /// whose outline was never clicked once, and adds nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Double_Click_On_Another_Shape_Only_Selects_It()
    {
        const string two = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <path d="M4 4 L20 4 L20 20" fill="none" stroke="#000000" />
              <path d="M4 12 L12 12" fill="none" stroke="#000000" />
            </svg>
            """;

        var (window, viewer) = await Reshaping(two);

        DoubleClick(window, viewer, 8f, 12f);

        Assert.Equal("M4 12 L12 12", Written(viewer, "1", "d"));
        Assert.Equal("1", viewer.Elements.SelectedAddresses.Single());
        Assert.Equal(2, viewer.Canvas.Points!.Anchors.Count);
    }

    // ---- several points ----------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Shift_Click_Chooses_A_Second_Point_And_A_Drag_Moves_Both_As_One_Step()
    {
        var (window, viewer) = await Reshaping(Square);
        var before = viewer.Source;

        Click(window, viewer, 20f, 4f);
        ShiftClick(window, viewer, 20f, 20f);

        Assert.Equal(new[] { new SKPoint(20f, 4f), new SKPoint(20f, 20f) }, Chosen(viewer));

        Drag(window, viewer, (20f, 4f), (22f, 6f));

        Assert.Equal("M4 4 L22 6 L22 22 L4 20", Written(viewer, "0", "d"));
        Assert.Equal(new[] { new SKPoint(22f, 6f), new SKPoint(22f, 22f) }, Chosen(viewer));

        Assert.True(viewer.Undo());
        Assert.Equal(before, viewer.Source);
    }

    [AvaloniaFact]
    public async Task Shift_Click_On_A_Chosen_Point_Lets_Go_Of_It()
    {
        var (window, viewer) = await Reshaping(Square);

        Click(window, viewer, 20f, 4f);
        ShiftClick(window, viewer, 20f, 20f);
        ShiftClick(window, viewer, 20f, 4f);

        Assert.Equal(new[] { new SKPoint(20f, 20f) }, Chosen(viewer));
    }

    /// <summary>A click on one of several chosen points chooses that one alone, once it is clear it was not a drag.</summary>
    [AvaloniaFact]
    public async Task A_Click_On_One_Of_Several_Chosen_Points_Chooses_It_Alone()
    {
        var (window, viewer) = await Reshaping(Square);

        Click(window, viewer, 20f, 4f);
        ShiftClick(window, viewer, 20f, 20f);
        Click(window, viewer, 20f, 20f);

        Assert.Equal(new[] { new SKPoint(20f, 20f) }, Chosen(viewer));
    }

    /// <summary>
    /// With a shape's points showing, a sweep chooses the points inside it rather than elements,
    /// shows what it has caught while it is drawn, and adds to them with Shift.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Sweep_Chooses_The_Points_Inside_It()
    {
        var (window, viewer) = await Reshaping(Square);

        window.MouseDown(At(window, viewer, 1f, 1f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        window.MouseMove(At(window, viewer, 22f, 6f), Held);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { new SKPoint(4f, 4f), new SKPoint(20f, 4f) }, Chosen(viewer));

        window.MouseUp(At(window, viewer, 22f, 6f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { new SKPoint(4f, 4f), new SKPoint(20f, 4f) }, Chosen(viewer));
        Assert.True(Tool(viewer, "points").IsChecked);

        Drag(window, viewer, (1f, 18f), (6f, 22f), RawInputModifiers.Shift);

        Assert.Equal(new[] { new SKPoint(4f, 4f), new SKPoint(20f, 4f), new SKPoint(4f, 20f) }, Chosen(viewer));
    }

    /// <summary>Shift held from the press of a drag on a chosen point holds it to eight directions, and lets go of nothing.</summary>
    [AvaloniaFact]
    public async Task Shift_Held_From_The_Press_Drags_A_Chosen_Point_Along_A_Line()
    {
        var (window, viewer) = await Reshaping(Square);

        Click(window, viewer, 20f, 4f);
        Drag(window, viewer, (20f, 4f), (30f, 4f), RawInputModifiers.Shift);

        Assert.Equal("M4 4 L30 4 L20 20 L4 20", Written(viewer, "0", "d"));
        Assert.Equal(new[] { new SKPoint(30f, 4f) }, Chosen(viewer));
    }

    /// <summary>
    /// A chosen handle let go of with Shift is still the shape's click, though nothing chosen shows
    /// it any more: the shape stays selected rather than the page being picked behind it.
    /// </summary>
    [AvaloniaFact]
    public async Task Shift_Click_On_A_Chosen_Handle_Lets_Go_Of_It_And_Keeps_The_Shape()
    {
        const string arch = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <path d="M4 12 C4 2 20 2 20 12" fill="none" stroke="#000000" />
            </svg>
            """;

        var (window, viewer) = await Reshaping(arch, ink: (12f, 4.5f));

        Click(window, viewer, 20f, 12f);
        Click(window, viewer, 20f, 2f);

        Assert.False(viewer.Canvas.Points!.Chosen.Single().Anchor);

        ShiftClick(window, viewer, 20f, 2f);

        Assert.Empty(viewer.Canvas.Points!.Chosen);
        Assert.Equal("0", viewer.Elements.SelectedAddresses.Single());
    }

    [AvaloniaFact]
    public async Task Delete_Takes_Out_Every_Chosen_Point_As_One_Step()
    {
        var (window, viewer) = await Reshaping(Square);
        var before = viewer.Source;

        Click(window, viewer, 20f, 4f);
        ShiftClick(window, viewer, 20f, 20f);
        Press(window, PhysicalKey.Delete);

        Assert.Equal("M4 4 L4 20", Written(viewer, "0", "d"));
        Assert.Empty(viewer.Canvas.Points!.Chosen);

        Assert.True(viewer.Undo());
        Assert.Equal(before, viewer.Source);
    }

    [AvaloniaFact]
    public async Task Select_All_Chooses_Every_Point_And_Escape_Lets_Go_Of_Them()
    {
        var (window, viewer) = await Reshaping(Square);

        window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(4, viewer.Canvas.Points!.Chosen.Count);

        Press(window, PhysicalKey.Escape);

        Assert.Empty(viewer.Canvas.Points!.Chosen);
        Assert.True(Tool(viewer, "points").IsChecked);
    }

    /// <summary>A drawing built through a recipe is written at the address its file has, not the built one.</summary>
    [AvaloniaFact]
    public async Task Points_Under_A_Recipe_Are_Written_To_The_File()
    {
        var viewer = new SvgViewer
        {
            Rewrite = text => text.Replace(
                """<path""",
                """<defs xmlns:e="https://svg.skia/expr/1.0"><e:code><e:param name="tint" type="color" default="#ff0000" /></e:code></defs><path""")
        };
        var window = new Window { Width = 400, Height = 400, Background = Brushes.White, Content = viewer };

        window.Show();

        var drawing = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".svg");

        File.WriteAllText(drawing, Bent);

        try
        {
            Assert.True(await viewer.LoadAsync(drawing));
        }
        finally
        {
            File.Delete(drawing);
        }

        Dispatcher.UIThread.RunJobs();
        viewer.Canvas.Focus();

        DoubleClick(window, viewer, 12f, 4f);
        Drag(window, viewer, (20f, 4f), (22f, 6f));

        Assert.Equal("M4 4 L22 6 L20 20", Written(viewer, "0", "d"));
    }

    // ---- mask and clip content -----------------------------------------------------------------

    /// <summary>A clip path's path stands where the element using it is, and is written in its own units.</summary>
    [AvaloniaFact]
    public async Task A_Path_In_A_Clip_Path_Is_Reshaped_Where_It_Clips()
    {
        var (window, viewer) = await Host("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <defs><clipPath id="c"><path id="edge" d="M2 2 L12 2 L12 12 Z" /></clipPath></defs>
              <g transform="translate(4 4)"><rect width="16" height="16" fill="#3366cc" clip-path="url(#c)" /></g>
            </svg>
            """);
        var before = viewer.Source;

        SelectById(viewer, "edge");
        Press(window, PhysicalKey.Enter);

        Assert.True(Tool(viewer, "points").IsChecked);
        Assert.Equal(new[] { new SKPoint(6f, 6f), new SKPoint(16f, 6f), new SKPoint(16f, 16f) }, viewer.Canvas.Points!.Anchors);

        Drag(window, viewer, (16f, 6f), (18f, 8f));

        Assert.Equal("M2 2 L14 4 L12 12 Z", WrittenOn(viewer, "edge", "d"));
        Assert.Equal(new SKPoint(18f, 8f), viewer.Canvas.Points!.Chosen.Single().At);

        Assert.True(viewer.Undo());
        Assert.Equal(before, viewer.Source);
    }

    /// <summary>
    /// A clip path's edge is picked a little way outside it, so the second click of a double-click
    /// lands outside its box and still takes hold of its points.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Double_Click_On_A_Clip_Paths_Edge_Takes_Hold_Of_Its_Points()
    {
        var (window, viewer) = await Host("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <defs><clipPath id="c"><path id="edge" d="M2 4 L22 4 L22 20 Z" /></clipPath></defs>
              <rect width="24" height="24" fill="#3366cc" clip-path="url(#c)" />
            </svg>
            """);

        DoubleClick(window, viewer, 6f, 3.85f);

        Assert.Equal("edge", viewer.SelectedElement?.ID);
        Assert.True(Tool(viewer, "points").IsChecked);
        Assert.Equal(3, viewer.Canvas.Points!.Anchors.Count);
    }

    /// <summary>Taken hold of where a second element doubles it, ten units across that element are five of the path's own.</summary>
    [AvaloniaFact]
    public async Task A_Path_In_A_Mask_Is_Reshaped_Through_The_Use_It_Was_Clicked_On()
    {
        var (window, viewer) = await Host("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
              <defs>
                <mask id="sweep" maskUnits="userSpaceOnUse" x="0" y="0" width="100" height="100">
                  <path id="spot" d="M0 0 L20 0 L20 20 L0 20 Z" fill="#ffffff" />
                </mask>
              </defs>
              <rect width="30" height="30" fill="#3366cc" mask="url(#sweep)" />
              <g transform="translate(50 50) scale(2)"><rect width="20" height="20" fill="#3366cc" mask="url(#sweep)" /></g>
            </svg>
            """);

        DoubleClick(window, viewer, 50f, 70f);

        Assert.Equal("spot", viewer.SelectedElement?.ID);
        Assert.True(Tool(viewer, "points").IsChecked);

        Drag(window, viewer, (90f, 50f), (80f, 50f));

        Assert.Equal("M0 0 L15 0 L20 20 L0 20 Z", WrittenOn(viewer, "spot", "d"));

        // Still on the second element once the commit has rebuilt the drawing.
        Assert.Equal(new SKPoint(80f, 50f), viewer.Canvas.Points!.Chosen.Single().At);
    }

    [AvaloniaFact]
    public async Task A_Path_Nested_In_A_Group_Inside_A_Mask_Is_Reshaped_From_The_Tree()
    {
        var (window, viewer) = await Host("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
              <defs>
                <mask id="m" maskUnits="userSpaceOnUse" x="0" y="0" width="100" height="100">
                  <g transform="translate(30 40)"><path id="deep" d="M0 0 L20 0 L20 10" fill="#ffffff" /></g>
                </mask>
              </defs>
              <rect width="100" height="100" fill="#3366cc" mask="url(#m)" />
            </svg>
            """);

        SelectById(viewer, "deep");
        Press(window, PhysicalKey.Enter);

        Assert.Equal(new[] { new SKPoint(30f, 40f), new SKPoint(50f, 40f), new SKPoint(50f, 50f) }, viewer.Canvas.Points!.Anchors);

        Drag(window, viewer, (50f, 40f), (60f, 45f));

        Assert.Equal("M0 0 L30 5 L20 10", WrittenOn(viewer, "deep", "d"));
    }

    /// <summary>A shape drawn by &lt;use&gt;, and kept in &lt;defs&gt; for it.</summary>
    private const string Used = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 24 24" width="24" height="24">
          <defs><path id="bent" d="M0 0 L8 0 L8 8" fill="none" stroke="#000000" /></defs>
          <use id="copy" xlink:href="#bent" x="4" y="4" />
          <use xlink:href="#bent" x="14" y="14" />
        </svg>
        """;

    /// <summary>The points are the definition's, so the copy clicked and every other one change with them.</summary>
    [AvaloniaFact]
    public async Task A_Double_Click_On_A_Use_Reshapes_The_Shape_It_Draws_There()
    {
        var (window, viewer) = await Host(Used);

        DoubleClick(window, viewer, 8f, 4f);

        Assert.True(Tool(viewer, "points").IsChecked);
        Assert.Equal(new[] { new SKPoint(4f, 4f), new SKPoint(12f, 4f), new SKPoint(12f, 12f) }, viewer.Canvas.Points!.Anchors);

        Drag(window, viewer, (12f, 4f), (14f, 6f));

        Assert.Equal("M0 0 L10 2 L8 8", WrittenOn(viewer, "bent", "d"));
        Assert.Equal("copy", viewer.SelectedElement?.ID);
        Assert.Equal(new SKPoint(14f, 6f), viewer.Canvas.Points!.Chosen.Single().At);
    }

    [AvaloniaFact]
    public async Task A_Defs_Path_Picked_From_The_Tree_Is_Reshaped_Where_Its_First_Use_Draws_It()
    {
        var (window, viewer) = await Host(Used);

        SelectById(viewer, "bent");
        Press(window, PhysicalKey.Enter);

        Assert.Equal(new[] { new SKPoint(4f, 4f), new SKPoint(12f, 4f), new SKPoint(12f, 12f) }, viewer.Canvas.Points!.Anchors);

        Drag(window, viewer, (12f, 12f), (10f, 12f));

        Assert.Equal("M0 0 L8 0 L6 8", WrittenOn(viewer, "bent", "d"));
    }

    /// <summary>A &lt;use&gt; in a clip path has no handles of its own, but the shape it draws has points.</summary>
    [AvaloniaFact]
    public async Task A_Double_Click_On_A_Use_Inside_A_Clip_Path_Reshapes_What_It_Draws()
    {
        var (window, viewer) = await Host("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <path id="bent" d="M0 0 L16 0 L16 16 Z" />
                <clipPath id="c"><use id="u" xlink:href="#bent" x="4" y="4" /></clipPath>
              </defs>
              <rect width="24" height="24" fill="#3366cc" clip-path="url(#c)" />
            </svg>
            """);

        DoubleClick(window, viewer, 10f, 4f);

        Assert.Equal("u", viewer.SelectedElement?.ID);
        Assert.True(Tool(viewer, "points").IsChecked);

        Drag(window, viewer, (20f, 4f), (22f, 6f));

        Assert.Equal("M0 0 L18 2 L16 16 Z", WrittenOn(viewer, "bent", "d"));
        Assert.Equal("u", viewer.SelectedElement?.ID);
    }

    /// <summary>
    /// A shape only a mask's &lt;use&gt; draws is held where the mask puts it, and still there after
    /// the commit, when the mask's own copy of it has been indexed a rebuild behind.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Defs_Path_A_Mask_Uses_Is_Reshaped_Where_It_Masks()
    {
        var (window, viewer) = await Host("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 100 100" width="100" height="100">
              <defs>
                <path id="spot" d="M0 0 L40 0 L40 40 L0 40 Z" fill="#ffffff" />
                <mask id="m" maskUnits="userSpaceOnUse" x="0" y="0" width="100" height="100"><use xlink:href="#spot" x="10" y="20" /></mask>
              </defs>
              <g transform="translate(20 10)"><rect width="80" height="80" fill="#3366cc" mask="url(#m)" /></g>
            </svg>
            """);

        SelectById(viewer, "spot");
        Press(window, PhysicalKey.Enter);

        Assert.True(Tool(viewer, "points").IsChecked);
        Assert.Contains(new SKPoint(70f, 30f), viewer.Canvas.Points!.Anchors);

        Drag(window, viewer, (70f, 30f), (80f, 40f));

        Assert.Equal("M0 0 L50 10 L40 40 L0 40 Z", WrittenOn(viewer, "spot", "d"));
        Assert.Contains(new SKPoint(80f, 40f), viewer.Canvas.Points!.Anchors);

        Drag(window, viewer, (70f, 70f), (60f, 60f));

        Assert.Equal("M0 0 L50 10 L30 30 L0 40 Z", WrittenOn(viewer, "spot", "d"));
    }

    /// <summary>
    /// Redrawing anything else in place indexes the mask's copy of the shape, in the masked element's
    /// space rather than where it lands; the points stay where the mask puts it.
    /// </summary>
    [AvaloniaFact]
    public void A_Mask_Indexed_By_A_Redraw_Elsewhere_Does_Not_Move_The_Points()
    {
        var svg = new Svg.Skia.SKSvg();

        Assert.NotNull(svg.FromSvg("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 100 100" width="100" height="100">
              <defs>
                <path id="spot" d="M0 0 L20 0 L20 20 Z" fill="#ffffff" />
                <mask id="m" maskUnits="userSpaceOnUse" x="0" y="0" width="100" height="100"><use xlink:href="#spot" x="30" y="40" /></mask>
              </defs>
              <g transform="translate(20 10)"><rect width="80" height="90" fill="#3366cc" mask="url(#m)" /></g>
              <rect id="other" width="10" height="10" fill="#cc3366" />
            </svg>
            """));

        var document = svg.SourceDocument!;
        var other = (SvgRectangle)document.GetElementById("other");

        other.X = new SvgUnit(5f);
        Assert.True(svg.TryApplyRetainedSceneMutationAndRender(other, new[] { "x" }, out _));

        var spot = document.GetElementById("spot");
        var points = new SvgViewerPoints();

        points.Track(svg, default, new SvgViewerGizmoMember(spot, SvgElementAddress.Create(spot).Key));

        Assert.Equal(new[] { new SKPoint(50f, 50f), new SKPoint(70f, 50f), new SKPoint(70f, 70f) }, points.Marks()!.Anchors);
    }

    /// <summary>
    /// A &lt;use&gt; of another file draws a shape whose address is that file's, which would name some
    /// other element of this one, so it has no points to take hold of.
    /// </summary>
    [AvaloniaFact]
    public void A_Use_Of_Another_Files_Shape_Has_No_Points()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;

        try
        {
            File.WriteAllText(Path.Combine(folder, "sprites.svg"), """
                <svg xmlns="http://www.w3.org/2000/svg"><path id="arrow" d="M0 0 L10 0 L10 10" /></svg>
                """);

            var drawing = Path.Combine(folder, "drawing.svg");

            File.WriteAllText(drawing, """
                <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 24 24" width="24" height="24">
                  <use id="far" xlink:href="sprites.svg#arrow" x="4" y="4" />
                  <use id="near" xlink:href="#here" />
                  <path id="here" d="M0 0 L8 0" stroke="#000000" />
                </svg>
                """);

            var svg = new Svg.Skia.SKSvg();

            Assert.NotNull(svg.Load(drawing));

            var points = new SvgViewerPoints();

            foreach (var (id, held) in new[] { ("near", true), ("far", false) })
            {
                var use = svg.SourceDocument!.GetElementById(id);

                points.Track(svg, default, new SvgViewerGizmoMember(use, SvgElementAddress.Create(use).Key));

                Assert.Equal(held, points.IsShowing);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A shape in &lt;defs&gt; that nothing draws has nowhere to show its points, so Enter arms nothing.</summary>
    [AvaloniaFact]
    public async Task A_Shape_Nothing_Draws_Is_Not_Taken_Hold_Of()
    {
        var (window, viewer) = await Host("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <defs><path id="spare" d="M0 0 L8 0 L8 8" /></defs>
              <rect width="10" height="10" fill="#3366cc" />
            </svg>
            """);

        SelectById(viewer, "spare");
        Press(window, PhysicalKey.Enter);

        Assert.True(Tool(viewer, "select").IsChecked);
        Assert.Null(viewer.Canvas.Points);
    }
}
