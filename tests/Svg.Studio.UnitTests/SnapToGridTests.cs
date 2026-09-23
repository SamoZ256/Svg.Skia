// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// The grid, from the settings file to where a drawing lands on a board.
/// </summary>
/// <remarks>
/// In the settings collection because it repoints the one static store, like every other class that
/// writes a setting.
///
/// The step is sixteen, which none of the fixture's own numbers is a multiple of, and the drag is
/// forty three — so a tile that ends up on a line got there by being put there.
/// </remarks>
[Collection("settings")]
public class SnapToGridTests : IDisposable
{
    private const float Step = 16f;

    private const string Project = """
        <studio namespace="Demo.Icons">

          <drawing name="home" class="Home">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#00ff00" />
            </svg>
          </drawing>

          <drawing name="badge" class="Badge">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#0000ff" />
            </svg>
          </drawing>

        </studio>
        """;

    private readonly string _was = StudioSettings.Store;
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public SnapToGridTests()
    {
        StudioSettings.Store = Path.Combine(_directory, "settings");
        StudioSettings.GridSize = Step;
    }

    public void Dispose()
    {
        StudioSettings.Store = _was;

        Directory.Delete(_directory, recursive: true);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);

        File.WriteAllText(path, text);

        return path;
    }

    private static async Task<MainWindow> Host(string path)
    {
        var window = new MainWindow();

        window.Announce = (_, _) => Task.CompletedTask;

        window.Show();
        Dispatcher.UIThread.RunJobs();

        await window.OpenAsync(new[] { path });

        Dispatcher.UIThread.RunJobs();

        window.Measure(new Size(900, 600));
        window.Arrange(new Rect(0, 0, 900, 600));
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    private static TabControl Tabs(MainWindow window)
        => window.GetVisualDescendants().OfType<TabControl>().First();

    /// <summary>The board in front, which for a project just opened is its root.</summary>
    private static GroupPanel Board(MainWindow window)
        => (GroupPanel)((TabItem)Tabs(window).SelectedItem!).Content!;

    private static SvgViewerCanvas Canvas(GroupPanel panel)
        => panel.GetVisualDescendants().OfType<SvgViewerCanvas>().Single();

    private static IReadOnlyList<SvgViewerPlacement> Drawn(GroupPanel panel) => Canvas(panel).Placements;

    /// <summary>Where a placement sits in the arrangement, as a rectangle.</summary>
    private static SKRect Area(SvgViewerPlacement placement)
    {
        var cull = placement.Svg.Picture!.CullRect;

        return new SKRect(
            placement.At.X + cull.Left,
            placement.At.Y + cull.Top,
            placement.At.X + cull.Right,
            placement.At.Y + cull.Bottom);
    }

    /// <summary>Where in the control a point of the arrangement is, checked by mapping it back.</summary>
    private static Point Over(SvgViewerCanvas canvas, float x, float y)
    {
        Assert.True(canvas.TryGetDrawingPoint(new Point(canvas.OffsetX, canvas.OffsetY), out var origin));

        var at = new Point(
            (x - origin.X) * canvas.Scale + canvas.OffsetX,
            (y - origin.Y) * canvas.Scale + canvas.OffsetY);

        Assert.True(canvas.TryGetDrawingPoint(at, out var back));
        Assert.Equal(x, back.X, 3);
        Assert.Equal(y, back.Y, 3);

        return at;
    }

    /// <summary>Where a drawing is taken hold of: the line round it, a quarter down its left edge.</summary>
    private static Point Edge(SvgViewerCanvas canvas, SKRect area, float by = 0f)
        => Over(canvas, area.Left + by, area.Top + area.Height / 4f);

    private static void Drag(Window window, SvgViewerCanvas canvas, Point from, Point to)
    {
        var start = canvas.TranslatePoint(from, window)
                    ?? throw new InvalidOperationException("The canvas is not in the window.");

        var end = canvas.TranslatePoint(to, window)
                  ?? throw new InvalidOperationException("The canvas is not in the window.");

        canvas.RaiseEvent(new PointerPressedEventArgs(
            canvas,
            new Pointer(0, PointerType.Mouse, true),
            window,
            start,
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None)
        {
            RoutedEvent = InputElement.PointerPressedEvent
        });

        canvas.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent,
            canvas,
            new Pointer(0, PointerType.Mouse, true),
            window,
            end,
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));

        canvas.RaiseEvent(new PointerReleasedEventArgs(
            canvas,
            new Pointer(0, PointerType.Mouse, true),
            window,
            end,
            0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None,
            MouseButton.Left)
        {
            RoutedEvent = InputElement.PointerReleasedEvent
        });

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The board's own snap toggle.</summary>
    private static ToggleButton Toggle(GroupPanel panel)
        => panel.GetVisualDescendants()
            .OfType<ToggleButton>()
            .Single(button => Equals(button.Content, "Snap"));

    /// <summary>A tile dropped anywhere lands on the nearest line, not under the pointer.</summary>
    [AvaloniaFact]
    public async Task A_Tile_Lands_On_The_Grid()
    {
        StudioSettings.SnapToGrid = true;

        var window = await Host(Write("icons.svgstudio", Project));
        var panel = Board(window);
        var canvas = Canvas(panel);

        var home = Area(Drawn(panel)[0]);
        var text = ((ProjectDrawing)window.Workspace!.Document.Root.Children[0]).Text;

        Drag(window, canvas, Edge(canvas, home), Edge(canvas, home, 43f));

        var left = Area(Drawn(panel)[0]).Left;

        Assert.Equal(Step * MathF.Round((home.Left + 43f) / Step), left, 2);

        // And it really is somewhere else: a drag of forty three onto a grid of sixteen cannot land
        // where the pointer stopped.
        Assert.NotEqual(home.Left + 43f, left, 2);

        // The tile moved and the drawing did not: a place is the project's, not the file's.
        Assert.Equal(text, ((ProjectDrawing)window.Workspace!.Document.Root.Children[0]).Text);

        window.Close();
    }

    /// <summary>With the switch off, the tile is where the pointer left it.</summary>
    [AvaloniaFact]
    public async Task With_Snapping_Off_A_Tile_Is_Where_It_Was_Dropped()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var panel = Board(window);
        var canvas = Canvas(panel);

        var home = Area(Drawn(panel)[0]);

        Drag(window, canvas, Edge(canvas, home), Edge(canvas, home, 43f));

        Assert.Equal(home.Left + 43f, Area(Drawn(panel)[0]).Left, 2);

        window.Close();
    }

    /// <summary>A drag that lands where the tile already was is not an edit.</summary>
    /// <remarks>
    /// The one the grid makes possible: two units across a step of sixteen rounds back to where it
    /// started, and a history that grew an entry for it would be a history of pointer twitches.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Drag_That_Lands_Where_It_Started_Is_Not_An_Edit()
    {
        StudioSettings.SnapToGrid = true;

        var window = await Host(Write("icons.svgstudio", Project));
        var panel = Board(window);
        var canvas = Canvas(panel);

        // Onto a line first, so there is a line to come back to.
        var home = Area(Drawn(panel)[0]);

        Drag(window, canvas, Edge(canvas, home), Edge(canvas, home, 43f));

        var settled = Area(Drawn(panel)[0]);
        var xml = window.Workspace!.Document.ToXml();

        Drag(window, canvas, Edge(canvas, settled), Edge(canvas, settled, 2f));

        Assert.Equal(xml, window.Workspace!.Document.ToXml());

        window.Close();
    }

    /// <summary>The board's toggle is the setting, and every other tab hears about it.</summary>
    [AvaloniaFact]
    public async Task The_Boards_Toggle_Is_The_Setting_Everywhere()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var panel = Board(window);

        Assert.False(Canvas(panel).Grid.IsOn);

        Toggle(panel).IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(StudioSettings.SnapToGrid);
        Assert.True(Canvas(panel).Grid.IsOn);
        Assert.Equal(Step, Canvas(panel).Grid.Step);

        // A drawing's own tab is a viewer rather than a board, and it is on the same screen.
        await window.ShowAsync(window.Workspace!.Document.Root.Children[0]);
        Dispatcher.UIThread.RunJobs();

        var viewer = (SvgViewer)((TabItem)Tabs(window).SelectedItem!).Content!;

        Assert.True(viewer.SnapsToGrid);
        Assert.Equal(Step, viewer.Grid.Step);

        window.Close();
    }

    /// <summary>The settings window writes all three, and the tabs behind it are told.</summary>
    [AvaloniaFact]
    public async Task The_Settings_Window_Writes_The_Grid()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var panel = Board(window);

        var settings = new SettingsWindow();

        settings.Show();
        Dispatcher.UIThread.RunJobs();

        settings.SnapToGrid.IsChecked = true;
        settings.GridSize.Value = 24m;
        settings.RotationStep.Value = 45m;
        Dispatcher.UIThread.RunJobs();

        Assert.True(StudioSettings.SnapToGrid);
        Assert.Equal(24d, StudioSettings.GridSize);
        Assert.Equal(45d, StudioSettings.RotationStep);

        settings.Close();

        // Through the window's own seam, since a headless run cannot show a modal: what the settings
        // window does to the setting is tested on the settings window.
        window.ShowSettings = () => Task.CompletedTask;

        await window.ShowSettingsAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(Canvas(panel).Grid.IsOn);
        Assert.Equal(24f, Canvas(panel).Grid.Step);
        Assert.Equal(45f, Canvas(panel).Grid.Turn);

        // And the toolbar's toggle came along, which is what makes them one switch.
        Assert.True(Toggle(panel).IsChecked);

        window.Close();
    }
}
