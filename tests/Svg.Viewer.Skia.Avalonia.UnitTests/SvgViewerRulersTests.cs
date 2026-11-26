// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>The rulers count a drawing's own units, which are the viewBox's and not the picture's.</summary>
public class SvgViewerRulersTests
{
    /// <summary>Twenty-four units drawn across forty-eight: one unit is two of the picture's.</summary>
    private const string Scaled = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="48" height="48">
          <rect x="2" y="2" width="20" height="20" fill="#3366cc" />
        </svg>
        """;

    private const string Plain = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48" width="48" height="48">
          <rect x="2" y="2" width="20" height="20" fill="#3366cc" />
        </svg>
        """;

    private static async Task<(Window Window, SvgViewer Viewer)> Host(string drawing)
    {
        var viewer = new SvgViewer();
        var window = new Window { Width = 400, Height = 400, Background = Brushes.White, Content = viewer };

        window.Show();

        Assert.True(await viewer.LoadTextAsync(drawing));

        viewer.Rulers.ShowsRulers = true;
        Dispatcher.UIThread.RunJobs();

        return (window, viewer);
    }

    [AvaloniaFact]
    public async Task The_Rulers_Count_The_Drawings_Own_Units()
    {
        var (window, viewer) = await Host(Scaled);
        var canvas = viewer.Canvas;

        Assert.Equal(2f, viewer.Rulers.Units.ScaleX, 3);

        // Every tick sits where its value is in the picture, which is twice the number it reads.
        var ticks = viewer.Rulers.Ticks(across: true);

        Assert.Contains(ticks, tick => tick.Value > 0d);
        Assert.All(ticks, tick => Assert.Equal(canvas.OffsetX + tick.Value * 2d * canvas.Scale, tick.Pixel, 3));

        window.Close();
    }

    [AvaloniaFact]
    public async Task Hidden_Rulers_Take_No_Room()
    {
        var (window, viewer) = await Host(Plain);

        Assert.Equal(viewer.Rulers.Bounds.Width - SvgViewerRulers.Breadth, viewer.Canvas.Bounds.Width, 3);

        viewer.Rulers.ShowsRulers = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(viewer.Rulers.Bounds.Width, viewer.Canvas.Bounds.Width, 3);

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Rulers_Follow_A_Rebuilt_ViewBox()
    {
        var (window, viewer) = await Host(Scaled);

        Assert.True(await viewer.LoadTextAsync(Plain));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1f, viewer.Rulers.Units.ScaleX, 3);

        window.Close();
    }
}
