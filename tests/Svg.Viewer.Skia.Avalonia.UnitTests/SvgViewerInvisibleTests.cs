// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// The boxes a drawing reserves for its host (<c>e:bounds</c>): unpainted, so found by the dashed
/// edge the canvas draws for them, and named in the Attributes pane.
/// </summary>
public class SvgViewerBoxTests
{
    // PaintCode's analog-level box: 2, 6, 28, 24 in a 30 by 30 drawing, with ink inside it.
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 30 30" width="30" height="30">
          <rect id="level" x="2" y="6" width="26" height="18" fill="none" e:bounds="LevelRect" />
          <circle id="ink" cx="15" cy="15" r="3" fill="#3366cc" />
          <rect id="plain" x="1" y="1" width="2" height="2" fill="#3366cc" />
        </svg>
        """;

    private static async Task<(Window Window, SvgViewer Viewer)> Host(string drawing = Drawing)
    {
        var viewer = new SvgViewer();
        var window = new Window { Width = 700, Height = 500, Background = Brushes.White, Content = viewer };

        window.Show();

        Assert.True(await viewer.LoadTextAsync(drawing));
        Dispatcher.UIThread.RunJobs();

        window.Measure(new Size(700, 500));
        window.Arrange(new Rect(0, 0, 700, 500));
        Dispatcher.UIThread.RunJobs();

        return (window, viewer);
    }

    private static Point Over(SvgViewerCanvas canvas, double x, double y)
        => new(x * canvas.Scale + canvas.OffsetX, y * canvas.Scale + canvas.OffsetY);

    private static void Click(Window window, SvgViewer viewer, double x, double y)
    {
        var canvas = viewer.Canvas;
        var at = canvas.TranslatePoint(Over(canvas, x, y), window) ?? Over(canvas, x, y);

        canvas.RaiseEvent(new PointerPressedEventArgs(
            canvas,
            new Pointer(0, PointerType.Mouse, true),
            window,
            at,
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None)
        {
            RoutedEvent = InputElement.PointerPressedEvent
        });

        canvas.RaiseEvent(new PointerReleasedEventArgs(
            canvas,
            new Pointer(0, PointerType.Mouse, true),
            window,
            at,
            0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None,
            MouseButton.Left));

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task A_Box_Is_Picked_By_Its_Edge_And_Wears_No_Stalk()
    {
        var (window, viewer) = await Host();

        Click(window, viewer, 2d, 15d);

        Assert.Equal("level", viewer.SelectedElement?.ID);
        Assert.False(viewer.IsPageSelected);

        // A turned box would be reported as the upright box round it, a different box.
        Assert.False(viewer.Canvas.GizmoTurns);

        // And the tree says what it is reported as.
        Assert.Contains("▭ LevelRect", viewer.Elements.SelectedNode?.Id, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Ink_Inside_A_Box_Still_Wins_And_Empty_Room_Is_The_Page()
    {
        var (window, viewer) = await Host();

        Click(window, viewer, 15d, 15d);

        Assert.Equal("ink", viewer.SelectedElement?.ID);
        Assert.True(viewer.Canvas.GizmoTurns);

        Click(window, viewer, 8d, 10d);

        Assert.True(viewer.IsPageSelected);
    }

    [AvaloniaFact]
    public async Task A_Hidden_Box_Layer_Picks_Nothing()
    {
        var (window, viewer) = await Host();

        viewer.ShowsBoxes = false;

        Click(window, viewer, 2d, 15d);

        Assert.True(viewer.IsPageSelected);
    }

    /// <summary>What the class could not declare is said while it is typed, not when the export fails.</summary>
    [AvaloniaFact]
    public async Task A_Box_Name_The_Class_Cannot_Declare_Is_Refused_As_It_Is_Typed()
    {
        var (_, viewer) = await Host();

        viewer.ClassName = () => "AnalogLevel";

        var panel = viewer.GetLogicalDescendants().OfType<SvgViewerElementPanel>().Single();

        panel.Show("2");
        Dispatcher.UIThread.RunJobs();

        Assert.False(panel.Set("e:bounds", "2Rect"));
        Assert.False(panel.Set("e:bounds", "AnalogLevel"));
        Assert.False(panel.Set("e:bounds", "LevelRect"));

        Assert.True(panel.Set("e:bounds", "PlainRect"));
        Assert.Contains("e:bounds=\"PlainRect\"", viewer.Source, StringComparison.Ordinal);
    }

    /// <summary>A first box in a drawing with no expressions declares the prefix it is written with.</summary>
    [AvaloniaFact]
    public async Task Marking_A_Shape_In_A_Plain_Drawing_Declares_The_Prefix()
    {
        var (_, viewer) = await Host("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 30 30" width="30" height="30">
              <rect x="2" y="6" width="26" height="18" fill="none" />
            </svg>
            """);

        var panel = viewer.GetLogicalDescendants().OfType<SvgViewerElementPanel>().Single();

        panel.Show("0");
        Dispatcher.UIThread.RunJobs();

        Assert.True(panel.Set("e:bounds", "LevelRect"));
        Assert.Contains("xmlns:e=\"https://svg.skia/expr/1.0\"", viewer.Source, StringComparison.Ordinal);
        Assert.Contains("e:bounds=\"LevelRect\"", viewer.Source, StringComparison.Ordinal);
    }
}
