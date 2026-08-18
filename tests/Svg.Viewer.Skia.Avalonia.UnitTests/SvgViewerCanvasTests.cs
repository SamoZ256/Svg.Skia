using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// The view transform. Deliberately exact: fit and one-to-one are arithmetic, not judgement.
/// </summary>
public class SvgViewerCanvasTests
{
    // 100x50 in a 400x200 pane: fit is bounded by width, so the two axes disagree and a bug that
    // picks the wrong one shows up.
    private const string Wide = """
        <svg xmlns="http://www.w3.org/2000/svg" width="100" height="50" viewBox="0 0 100 50">
          <rect x="0" y="0" width="100" height="50" fill="#ff0000" />
        </svg>
        """;

    private static (Window Window, SvgViewerCanvas Canvas, SvgViewerDocument Document) Host(
        string markup = Wide,
        double width = 400,
        double height = 200)
    {
        var document = SvgViewerDocument.LoadFromSvg(markup);
        var canvas = new SvgViewerCanvas { Svg = document.Svg };

        var window = new Window
        {
            Width = width,
            Height = height,
            Background = Brushes.White,
            Content = canvas
        };

        window.Show();
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));

        return (window, canvas, document);
    }

    [AvaloniaFact]
    public void A_Loaded_Drawing_Starts_Fitted()
    {
        var (window, canvas, document) = Host();

        // min(400/100, 200/50) = 4, and the excess height is split evenly.
        Assert.Equal(4d, canvas.Scale, 6);
        Assert.Equal(0d, canvas.OffsetX, 6);
        Assert.Equal(0d, canvas.OffsetY, 6);

        window.Close();
        document.Dispose();
    }

    [AvaloniaFact]
    public void Fit_Centres_On_The_Constrained_Axis()
    {
        // 100x50 in a 400x400 pane: fit is 4 on width, leaving 200 of slack in height.
        var (window, canvas, document) = Host(width: 400, height: 400);

        Assert.Equal(4d, canvas.Scale, 6);
        Assert.Equal(0d, canvas.OffsetX, 6);
        Assert.Equal(100d, canvas.OffsetY, 6);

        window.Close();
        document.Dispose();
    }

    [AvaloniaFact]
    public void ActualSize_Is_One_To_One_And_Centred()
    {
        var (window, canvas, document) = Host();

        canvas.ActualSize();

        Assert.Equal(1d, canvas.Scale, 6);
        Assert.Equal(150d, canvas.OffsetX, 6);
        Assert.Equal(75d, canvas.OffsetY, 6);

        window.Close();
        document.Dispose();
    }

    [AvaloniaFact]
    public void ResetView_Returns_To_The_Fitted_View()
    {
        var (window, canvas, document) = Host();

        canvas.ZoomTo(9d, new Point(10, 10));
        Assert.Equal(9d, canvas.Scale, 6);

        canvas.ResetView();

        Assert.Equal(4d, canvas.Scale, 6);
        Assert.Equal(0d, canvas.OffsetX, 6);

        window.Close();
        document.Dispose();
    }

    [AvaloniaFact]
    public void Zooming_Leaves_The_Anchor_Where_It_Was()
    {
        var (window, canvas, document) = Host();

        var anchor = new Point(310, 140);
        Assert.True(canvas.TryGetDrawingPoint(anchor, out var before));

        canvas.ZoomTo(canvas.Scale * 2.5d, anchor);

        Assert.True(canvas.TryGetDrawingPoint(anchor, out var after));
        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);

        window.Close();
        document.Dispose();
    }

    [AvaloniaFact]
    public void The_Scale_Is_Clamped_At_Both_Ends()
    {
        var (window, canvas, document) = Host();

        canvas.ZoomTo(1000d, new Point(0, 0));
        Assert.Equal(SvgViewerCanvas.MaximumScale, canvas.Scale, 6);

        canvas.ZoomTo(0.0001d, new Point(0, 0));
        Assert.Equal(SvgViewerCanvas.MinimumScale, canvas.Scale, 6);

        window.Close();
        document.Dispose();
    }

    [AvaloniaFact]
    public void ZoomIn_And_ZoomOut_Are_Inverses_About_The_Centre()
    {
        var (window, canvas, document) = Host();

        var scale = canvas.Scale;
        var offsetX = canvas.OffsetX;

        canvas.ZoomIn();
        canvas.ZoomOut();

        Assert.Equal(scale, canvas.Scale, 6);
        Assert.Equal(offsetX, canvas.OffsetX, 6);

        window.Close();
        document.Dispose();
    }

    [AvaloniaFact]
    public void A_View_Change_Is_Announced_Once()
    {
        var (window, canvas, document) = Host();

        var raised = 0;
        canvas.ViewChanged += (_, _) => raised++;

        canvas.ActualSize();
        Assert.Equal(1, raised);

        // Setting the same view again is not a change.
        canvas.ActualSize();
        Assert.Equal(1, raised);

        window.Close();
        document.Dispose();
    }

    [AvaloniaFact]
    public void Resizing_Keeps_The_Drawing_Fitted_Until_The_View_Is_Adjusted()
    {
        var (window, canvas, document) = Host();

        Assert.Equal(4d, canvas.Scale, 6);

        // Growing the pane re-fits, so a drawing does not sit in a corner after a window resize.
        canvas.Measure(new Size(800, 400));
        canvas.Arrange(new Rect(0, 0, 800, 400));
        Assert.Equal(8d, canvas.Scale, 6);

        // Once the view has been set by hand, a resize must not throw away what is being looked at.
        canvas.ZoomTo(20d, new Point(0, 0));
        canvas.Measure(new Size(400, 200));
        canvas.Arrange(new Rect(0, 0, 400, 200));
        Assert.Equal(20d, canvas.Scale, 6);

        // Fit gives back the automatic behaviour.
        canvas.Fit();
        Assert.Equal(4d, canvas.Scale, 6);

        window.Close();
        document.Dispose();
    }
}
