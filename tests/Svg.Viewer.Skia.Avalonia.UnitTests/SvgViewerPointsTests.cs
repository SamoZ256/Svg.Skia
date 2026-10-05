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

    private static ToggleButton Tool(SvgViewer viewer, string tool)
        => viewer.GetVisualDescendants().OfType<ToggleButton>().Single(button => Equals(button.Tag, tool));

    /// <summary>Selects the path by its ink and takes hold of its points the way a hand would: twice.</summary>
    private static async Task<(Window Window, SvgViewer Viewer)> Reshaping(string drawing = Bent, bool snapping = false)
    {
        var (window, viewer) = await Host(drawing, snapping);
        var on = drawing == Grouped ? (14f, 0f) : (12f, 4f);

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
        Assert.Equal(new SKPoint(22f, 6f), viewer.Canvas.Points!.Chosen);

        Assert.True(viewer.Undo());
        Assert.Equal(before, viewer.Source);
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
        Assert.Null(viewer.Canvas.Points!.Chosen);
    }

    [AvaloniaFact]
    public async Task Escape_Lets_Go_Of_The_Point_And_Then_Of_The_Tool()
    {
        var (window, viewer) = await Reshaping();

        Click(window, viewer, 20f, 4f);

        Assert.Equal(new SKPoint(20f, 4f), viewer.Canvas.Points!.Chosen);

        Press(window, PhysicalKey.Escape);

        Assert.Null(viewer.Canvas.Points!.Chosen);
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
        Assert.Equal(new SKPoint(12f, 4f), viewer.Canvas.Points!.Chosen);
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
}
