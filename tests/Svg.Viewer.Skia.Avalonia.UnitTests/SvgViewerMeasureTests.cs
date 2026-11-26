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

/// <summary>The Measure tool: it reads the drawing in its own units and writes nothing.</summary>
public class SvgViewerMeasureTests
{
    /// <summary>Twenty-four units drawn across forty-eight, so a reading in picture units is twice what it should be.</summary>
    private const string Scaled = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="48" height="48">
          <rect id="box" x="4" y="4" width="8" height="8" fill="#3366cc" />
        </svg>
        """;

    private static async Task<(Window Window, SvgViewer Viewer)> Host(string drawing = Scaled, bool snapping = false)
    {
        var viewer = new SvgViewer { Grid = new SvgViewerGrid(8f, 15f), SnapsToGrid = snapping };
        var window = new Window { Width = 800, Height = 800, Background = Brushes.White, Content = viewer };

        window.Show();

        Assert.True(await viewer.LoadTextAsync(drawing));

        Dispatcher.UIThread.RunJobs();

        viewer.Canvas.Focus();
        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.Drafting.Measuring);

        return (window, viewer);
    }

    private static Point At(Window window, SvgViewer viewer, float x, float y)
    {
        Assert.True(viewer.Canvas.TryGetControlPoint(new SKPoint(x, y), out var point));

        return viewer.Canvas.TranslatePoint(point, window) ?? throw new InvalidOperationException("The canvas is not in the window.");
    }

    private static void Drag(Window window, SvgViewer viewer, (float X, float Y) from, (float X, float Y) to, RawInputModifiers keys = RawInputModifiers.None)
    {
        window.MouseDown(At(window, viewer, from.X, from.Y), MouseButton.Left, keys);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(At(window, viewer, to.X, to.Y), RawInputModifiers.LeftMouseButton | keys);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(At(window, viewer, to.X, to.Y), MouseButton.Left, keys);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Measuring_Writes_Nothing()
    {
        var (window, viewer) = await Host();
        var written = viewer.Source;

        Drag(window, viewer, (2f, 2f), (30f, 20f));

        Assert.Equal(written, viewer.Source);
        Assert.False(viewer.IsSourceModified);

        window.Close();
    }

    /// <summary>Twenty of the picture's units across is ten of the drawing's.</summary>
    [AvaloniaFact]
    public async Task A_Reading_Is_In_The_Drawings_Own_Units()
    {
        var (window, viewer) = await Host();

        Drag(window, viewer, (4f, 10f), (24f, 10f));

        Assert.Contains("Distance 10, angle 0°, Δx 10, Δy 0", viewer.Status, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>Measure moves nothing, so the shape left selected is something to measure to.</summary>
    [AvaloniaFact]
    public async Task Measuring_Lands_On_The_Shape_Left_Selected()
    {
        var viewer = new SvgViewer { SnapsToSmartGuides = true };
        var window = new Window { Width = 800, Height = 800, Background = Brushes.White, Content = viewer };

        window.Show();
        Assert.True(await viewer.LoadTextAsync(Scaled));
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.Elements.TrySelect(new[] { "0" }));
        viewer.Canvas.Focus();
        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        // To a third of a unit past the box's left edge, at 8 in the picture, which nothing else has a line at.
        Drag(window, viewer, (2f, 2f), (8.3f, 2f));

        Assert.Contains("Δx 3,", viewer.Status, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>The line is not the selection's ring: a pick retraces the ring and leaves the line and its reading, and Fit Selection has nothing to fit.</summary>
    [AvaloniaFact]
    public async Task A_Pick_Leaves_The_Line_And_Its_Reading()
    {
        var (window, viewer) = await Host();

        Drag(window, viewer, (4f, 10f), (24f, 10f));

        Assert.Null(viewer.Canvas.Highlight);

        Assert.True(viewer.Elements.TrySelect(new[] { "0" }));
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(viewer.Canvas.Measured);
        Assert.Contains("Distance 10", viewer.Status, StringComparison.Ordinal);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Shift_Lays_The_Line_On_The_Nearest_Eighth()
    {
        var (window, viewer) = await Host();

        Drag(window, viewer, (4f, 10f), (24f, 12f), RawInputModifiers.Shift);

        Assert.Contains("angle 0°", viewer.Status, StringComparison.Ordinal);
        Assert.Contains("Δy 0", viewer.Status, StringComparison.Ordinal);

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Ends_Land_On_The_Grid()
    {
        var (window, viewer) = await Host(snapping: true);
        var near = viewer.Canvas.Grid.Pull / 2f;

        // Each end half a pull off a line of the grid of eight: 8 and 24 in the picture, 4 and 12 in the drawing.
        Drag(window, viewer, (8f + near, 8f), (24f - near, 8f));

        Assert.Contains("Δx 8,", viewer.Status, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>A reading is not something wrong, so it is no note and raises no error.</summary>
    [AvaloniaFact]
    public async Task A_Reading_Is_Not_An_Error()
    {
        var (window, viewer) = await Host();
        var raised = false;

        viewer.ErrorRaised += (_, _) => raised = true;

        Drag(window, viewer, (4f, 10f), (24f, 10f));

        Assert.False(raised);
        Assert.Null(viewer.Note);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Escape_Puts_The_Tool_And_The_Reading_Away()
    {
        var (window, viewer) = await Host();

        Drag(window, viewer, (4f, 10f), (24f, 10f));

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewer.Drafting.Measuring);
        Assert.DoesNotContain("Distance", viewer.Status, StringComparison.Ordinal);
        Assert.Null(viewer.Canvas.Measured);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Click_Picks_Nothing_And_Takes_The_Line_Away()
    {
        var (window, viewer) = await Host();

        Drag(window, viewer, (4f, 10f), (24f, 10f));

        window.MouseDown(At(window, viewer, 16f, 16f), MouseButton.Left);
        window.MouseUp(At(window, viewer, 16f, 16f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewer.SelectedElement);
        Assert.DoesNotContain("Distance", viewer.Status, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>A drawing that cannot be edited can still be measured: measuring edits nothing.</summary>
    [AvaloniaFact]
    public async Task A_Drawing_That_Cannot_Be_Edited_Is_Measured()
    {
        var viewer = new SvgViewer();
        var window = new Window { Width = 800, Height = 800, Background = Brushes.White, Content = viewer };

        window.Show();

        // Entities of its own, which the viewer draws and has no source to edit for.
        Assert.True(await viewer.LoadTextAsync("""
            <?xml version="1.0"?>
            <!DOCTYPE svg [ <!ENTITY blue "#3366cc"> ]>
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="48" height="48">
              <rect x="4" y="4" width="8" height="8" fill="&blue;" />
            </svg>
            """));
        Dispatcher.UIThread.RunJobs();

        viewer.Canvas.Focus();
        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (4f, 10f), (24f, 10f));

        Assert.Contains("Distance 10", viewer.Status, StringComparison.Ordinal);

        window.Close();
    }
}
