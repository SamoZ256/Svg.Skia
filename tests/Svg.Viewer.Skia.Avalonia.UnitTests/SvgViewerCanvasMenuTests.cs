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
using SkiaSharp;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>The element tree's menu, opened by a right click on the canvas.</summary>
public class SvgViewerCanvasMenuTests
{
    private const string Two = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <rect id="one" x="20" y="20" width="20" height="20" fill="#3366cc" />
          <rect id="two" x="60" y="60" width="20" height="20" fill="#cc3366" />
        </svg>
        """;

    private static async Task<(Window Window, SvgViewer Viewer)> Host()
    {
        var viewer = new SvgViewer();
        var window = new Window { Width = 400, Height = 400, Background = Brushes.White, Content = viewer };

        window.Show();

        Assert.True(await viewer.LoadTextAsync(Two));

        Dispatcher.UIThread.RunJobs();

        return (window, viewer);
    }

    private static Point At(Window window, SvgViewer viewer, float x, float y)
    {
        Assert.True(viewer.Canvas.TryGetControlPoint(new SKPoint(x, y), out var point));

        return viewer.Canvas.TranslatePoint(point, window)
               ?? throw new InvalidOperationException("The canvas is not in the window.");
    }

    private static void Click(Window window, Point at, MouseButton button = MouseButton.Left)
    {
        window.MouseDown(at, button);
        window.MouseUp(at, button);
        Dispatcher.UIThread.RunJobs();
    }

    private static ContextMenu Menu(SvgViewer viewer) => viewer.Canvas.ContextMenu!;

    private static void Close(SvgViewer viewer)
    {
        Menu(viewer).Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task A_Right_Click_On_A_Shape_Picks_It_And_Opens_The_Trees_Menu()
    {
        var (window, viewer) = await Host();

        Click(window, At(window, viewer, 30, 30), MouseButton.Right);

        Assert.True(Menu(viewer).IsOpen);
        Assert.Equal(new[] { "0" }, viewer.Elements.SelectedAddresses);
        Assert.Contains(Menu(viewer).Items.OfType<MenuItem>(), item => item.IsVisible && item.IsEnabled && Equals(item.Header, "Delete"));

        Close(viewer);

        // A right press takes no focus of its own: Delete and ⌘D once the menu has gone are the canvas's.
        Assert.True(viewer.Canvas.IsFocused);
    }

    [AvaloniaFact]
    public async Task A_Right_Click_Inside_The_Selection_Keeps_All_Of_It()
    {
        var (window, viewer) = await Host();

        Assert.True(viewer.Elements.TrySelect(new[] { "0", "1" }));

        Click(window, At(window, viewer, 70, 70), MouseButton.Right);

        Assert.True(Menu(viewer).IsOpen);
        Assert.Equal(new[] { "0", "1" }, viewer.Elements.SelectedAddresses.OrderBy(key => key, StringComparer.Ordinal));

        Close(viewer);
    }

    /// <summary>Where a left click takes the page, or lets go, a right click leaves the selection as it was.</summary>
    [AvaloniaFact]
    public async Task A_Right_Click_On_The_Page_Or_Beside_It_Keeps_The_Selection()
    {
        var (window, viewer) = await Host();

        Click(window, At(window, viewer, 30, 30));
        Assert.Equal(new[] { "0" }, viewer.Elements.SelectedAddresses);

        Click(window, At(window, viewer, 90, 10), MouseButton.Right);

        Assert.Equal(new[] { "0" }, viewer.Elements.SelectedAddresses);
        Assert.False(viewer.IsPageSelected);
        Close(viewer);

        Click(window, At(window, viewer, 150, 150), MouseButton.Right);

        Assert.Equal(new[] { "0" }, viewer.Elements.SelectedAddresses);
        Close(viewer);
    }

    [AvaloniaFact]
    public async Task No_Menu_Opens_While_A_Tool_Is_Out()
    {
        var (window, viewer) = await Host();

        foreach (var tool in new[] { "rect", SvgViewerDraw.Tools.Single(tool => tool.Name == "Points").Tool })
        {
            viewer.Drafting.Arm(tool);

            Click(window, At(window, viewer, 70, 70), MouseButton.Right);

            Assert.False(Menu(viewer).IsOpen);
            Assert.Empty(viewer.Elements.SelectedAddresses);
        }
    }

    /// <summary>The menu key opens it over the middle of the canvas, on what is selected.</summary>
    /// <remarks>An anchor of no size is clipped to nothing, which put the menu at the canvas's corner.</remarks>
    [AvaloniaFact]
    public async Task The_Menu_Key_Opens_It_Over_The_Middle_Of_The_Canvas()
    {
        var (window, viewer) = await Host();

        Click(window, At(window, viewer, 30, 30));

        viewer.Canvas.RaiseEvent(new ContextRequestedEventArgs());
        Dispatcher.UIThread.RunJobs();

        Assert.True(Menu(viewer).IsOpen);
        Assert.Equal(new[] { "0" }, viewer.Elements.SelectedAddresses);
        Assert.True(Menu(viewer).PlacementRect is { Width: > 0, Height: > 0 } anchor
                    && Math.Abs(anchor.X - viewer.Canvas.Bounds.Width / 2) < 1 && Math.Abs(anchor.Y - viewer.Canvas.Bounds.Height / 2) < 1);

        Close(viewer);
    }
}
