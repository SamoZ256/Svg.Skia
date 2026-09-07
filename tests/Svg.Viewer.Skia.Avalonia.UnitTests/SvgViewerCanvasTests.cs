using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using SkiaSharp;
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

    /// <summary>
    /// Several drawings are one thing to look at: fitted, zoomed and panned together.
    /// </summary>
    /// <remarks>
    /// The union rather than the first of them, which is what a set laid out by a host needs — and
    /// what makes the same surface serve a project's group without a second canvas being written.
    /// </remarks>
    [AvaloniaFact]
    public void Several_Drawings_Are_Fitted_As_One()
    {
        using var left = SvgViewerDocument.LoadFromSvg(Wide);
        using var right = SvgViewerDocument.LoadFromSvg(Wide);

        var canvas = new SvgViewerCanvas();
        var window = new Window { Width = 400, Height = 200, Content = canvas };

        window.Show();

        canvas.Show(new[]
        {
            new SvgViewerPlacement(left.Svg, new SKPoint(0f, 0f)),
            new SvgViewerPlacement(right.Svg, new SKPoint(100f, 50f))
        });

        canvas.Measure(new Size(400, 200));
        canvas.Arrange(new Rect(0, 0, 400, 200));

        Assert.Equal(2, canvas.Placements.Count);

        // The two together are 200x100, so the fit is min(400/200, 200/100) = 2 either way.
        Assert.Equal(2d, canvas.Scale, 6);

        // And the one-drawing property is the one-drawing case of the same list.
        canvas.Svg = left.Svg;

        Assert.Same(left.Svg, canvas.Svg);
        Assert.Equal(4d, canvas.Scale, 6);

        window.Close();
    }

    [AvaloniaFact]
    public void A_Point_Says_Which_Drawing_It_Fell_On_And_Where()
    {
        // TryGetDrawingPoint answers in the space the drawings are arranged in, which is one
        // drawing's own space only when there is one of them at the origin. A host showing several
        // has to know which was clicked before it can ask that one anything.
        using var first = SvgViewerDocument.LoadFromSvg(Wide);
        using var second = SvgViewerDocument.LoadFromSvg(Wide);
        using var third = SvgViewerDocument.LoadFromSvg(Wide);

        var canvas = new SvgViewerCanvas();
        var window = new Window { Width = 400, Height = 200, Content = canvas };

        window.Show();

        var placements = new[]
        {
            new SvgViewerPlacement(first.Svg, new SKPoint(0f, 0f)),
            new SvgViewerPlacement(second.Svg, new SKPoint(100f, 0f)),
            new SvgViewerPlacement(third.Svg, new SKPoint(0f, 50f))
        };

        canvas.Show(placements);
        canvas.Measure(new Size(400, 200));
        canvas.Arrange(new Rect(0, 0, 400, 200));

        // 200x100 arranged into 400x200: the fit is 2 and the origin is 0,0.
        Assert.Equal(2d, canvas.Scale, 6);

        // Ten in and ten down from the second drawing's own top left, which is at 100,0 arranged
        // and therefore at 220,20 on the control.
        Assert.True(canvas.TryGetPlacementAt(new Point(220, 20), out var placement, out var drawingPoint));

        Assert.Same(placements[1], placement);
        Assert.Equal(10f, drawingPoint.X, 3);
        Assert.Equal(10f, drawingPoint.Y, 3);

        // And the third, below the first: 0,50 arranged is 0,100 on the control.
        Assert.True(canvas.TryGetPlacementAt(new Point(20, 120), out placement, out drawingPoint));

        Assert.Same(placements[2], placement);
        Assert.Equal(10f, drawingPoint.X, 3);
        Assert.Equal(10f, drawingPoint.Y, 3);

        // The gap the arrangement leaves at the bottom right is on no drawing at all.
        Assert.False(canvas.TryGetPlacementAt(new Point(300, 120), out placement, out _));
        Assert.Null(placement);

        window.Close();
    }

    /// <summary>What the frame is painted at one point of the control.</summary>
    private static SKColor Painted(Window window, int x, int y)
    {
        var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("No rendered frame was captured.");

        var path = Path.Combine(Path.GetTempPath(), $"svg-viewer-at-{Guid.NewGuid():N}.png");
        frame.Save(path);

        try
        {
            using var bitmap = SKBitmap.Decode(path);

            return bitmap!.GetPixel(x, y);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// An arrangement that does not begin at the origin is drawn where everything else says it is.
    /// </summary>
    /// <remarks>
    /// The fit centres the arrangement's <em>size</em> and the offset is where its top left goes, so
    /// the drawings have to be moved to start there. They were not, while
    /// <see cref="SvgViewerCanvas.TryGetDrawingPoint"/> mapped back as though they had been — so a
    /// pointer and a picture disagreed by however far from the origin the arrangement was laid out.
    /// A single drawing sits at the origin and never noticed; a project group centres each drawing
    /// in a column as wide as its caption, and the first of them can start hundreds of units in.
    /// </remarks>
    [AvaloniaFact]
    public void An_Arrangement_Away_From_The_Origin_Is_Drawn_Where_It_Is_Mapped()
    {
        using var first = SvgViewerDocument.LoadFromSvg(Blue);
        using var second = SvgViewerDocument.LoadFromSvg(Blue);

        var canvas = new SvgViewerCanvas { ShowBounds = false };
        var window = new Window { Width = 400, Height = 200, Background = Brushes.White, Content = canvas };

        window.Show();

        // Nothing is placed at the origin: the union runs from 50 to 300 across.
        canvas.Show(new[]
        {
            new SvgViewerPlacement(first.Svg, new SKPoint(50f, 0f)),
            new SvgViewerPlacement(second.Svg, new SKPoint(200f, 0f))
        });

        canvas.Measure(new Size(400, 200));
        canvas.Arrange(new Rect(0, 0, 400, 200));

        // 250x50 in 400x200: the fit is bounded by width at 1.6, and the height is centred.
        Assert.Equal(1.6d, canvas.Scale, 6);

        // Well inside the first drawing: the arrangement starts at 50, so the control's own left
        // edge is arranged 50, and 20 across is arranged 62.5 — a fifth of the way into it.
        Assert.True(canvas.TryGetPlacementAt(new Point(20, 100), out var placement, out var drawingPoint));

        Assert.Same(canvas.Placements[0], placement);
        Assert.Equal(12.5f, drawingPoint.X, 3);
        Assert.Equal(25f, drawingPoint.Y, 3);

        // And that is where the ink is. Mapping and painting have to agree, or a click lands on
        // whatever the difference between them happens to point at. Drawn without the arrangement's
        // origin taken off, this column is 80 pixels left of anything painted at all.
        var painted = Painted(window, 20, 100);

        Assert.True(painted.Blue > 200 && painted.Red < 100, $"{painted} is not the drawing's blue");
    }

    /// <summary>A drawing that is not orange, so the ring cannot be confused with its ink.</summary>
    private const string Blue = """
        <svg xmlns="http://www.w3.org/2000/svg" width="100" height="50" viewBox="0 0 100 50">
          <rect x="0" y="0" width="100" height="50" fill="#0000ff" />
        </svg>
        """;

    /// <summary>How much of a rectangle of the frame the selection ring paints.</summary>
    private static int Ringed(Window window, PixelRect where)
    {
        var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("No rendered frame was captured.");

        var path = Path.Combine(Path.GetTempPath(), $"svg-viewer-ring-{Guid.NewGuid():N}.png");
        frame.Save(path);

        try
        {
            using var bitmap = SKBitmap.Decode(path);

            var found = 0;

            for (var x = where.X; x < where.Right && x < bitmap!.Width; x++)
            {
                for (var y = where.Y; y < where.Bottom && y < bitmap.Height; y++)
                {
                    var pixel = bitmap.GetPixel(x, y);

                    // Orange: red up, blue down, and green in between — which the blue drawing under
                    // it, the grey bounds outline and the white ground are all outside.
                    if (pixel.Red > 180 && pixel.Green is > 70 and < 230 && pixel.Blue < 110)
                    {
                        found++;
                    }
                }
            }

            return found;
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The ring belongs to the arrangement, not to each drawing in it.
    /// </summary>
    /// <remarks>
    /// It used to be drawn inside every placement's transform, so a canvas showing a project group's
    /// drawings rang the same shape on all of them and only one of those was the element picked.
    /// </remarks>
    [AvaloniaFact]
    public void The_Ring_Is_Drawn_Once_Where_It_Was_Put()
    {
        using var left = SvgViewerDocument.LoadFromSvg(Blue);
        using var right = SvgViewerDocument.LoadFromSvg(Blue);

        var canvas = new SvgViewerCanvas { ShowBounds = false };
        var window = new Window { Width = 400, Height = 200, Background = Brushes.White, Content = canvas };

        window.Show();

        canvas.Show(new[]
        {
            new SvgViewerPlacement(left.Svg, new SKPoint(0f, 0f)),
            new SvgViewerPlacement(right.Svg, new SKPoint(100f, 50f))
        });

        canvas.Measure(new Size(400, 200));
        canvas.Arrange(new Rect(0, 0, 400, 200));

        // The two together are 200x100 in a 400x200 pane, so the fit is 2 and the origin is 0,0.
        Assert.Equal(2d, canvas.Scale, 6);

        var ring = new SKPath();
        ring.AddRect(new SKRect(10f, 10f, 90f, 40f));

        canvas.Highlight = ring;

        canvas.Measure(new Size(400, 200));
        canvas.Arrange(new Rect(0, 0, 400, 200));

        // Where it was put: 10,10..90,40 at scale 2 is 20,20..180,80.
        Assert.True(Ringed(window, new PixelRect(0, 0, 200, 100)) > 0, "the ring was not drawn where it was put");

        // And where the second placement would have repeated it: the same rectangle offset by
        // 100,50, which at scale 2 lands in the opposite quarter of the frame.
        Assert.Equal(0, Ringed(window, new PixelRect(200, 100, 200, 100)));

        window.Close();
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


    [AvaloniaFact]
    public void A_Fractional_Wheel_Delta_Zooms_Smoothly()
    {
        // A trackpad two finger scroll arrives as a wheel event with a fractional delta, where a
        // mouse notch is 1. Both have to land on the same curve, or a trackpad either does nothing
        // or jumps.
        var (window, canvas, document) = Host();

        var start = canvas.Scale;
        canvas.RaiseEvent(Wheel(canvas, 0.1d, new Point(200, 100)));
        var nudged = canvas.Scale;

        Assert.True(nudged > start, "A fractional delta should still zoom.");
        Assert.True(nudged < start * 1.2d, "A fractional delta should zoom less than a full notch.");
        Assert.Equal(start * Math.Pow(1.2d, 0.1d), nudged, 6);

        window.Close();
        document.Dispose();
    }

    [AvaloniaFact]
    public void A_Wheel_Notch_Zooms_About_The_Pointer()
    {
        var (window, canvas, document) = Host();

        var anchor = new Point(310, 140);
        Assert.True(canvas.TryGetDrawingPoint(anchor, out var before));

        canvas.RaiseEvent(Wheel(canvas, 1d, anchor));

        Assert.Equal(4d * 1.2d, canvas.Scale, 6);
        Assert.True(canvas.TryGetDrawingPoint(anchor, out var after));
        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);

        window.Close();
        document.Dispose();
    }

    [AvaloniaTheory]
    [InlineData(Key.OemPlus, true)]
    [InlineData(Key.OemMinus, false)]
    public void The_Accelerator_Zooms(Key key, bool expectLarger)
    {
        var (window, canvas, document) = Host();

        var start = canvas.Scale;
        canvas.Focus();
        canvas.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = KeyModifiers.Control
        });

        Assert.Equal(expectLarger, canvas.Scale > start);

        window.Close();
        document.Dispose();
    }

    [AvaloniaFact]
    public void The_Accelerator_Fits_And_Sizes()
    {
        var (window, canvas, document) = Host();

        canvas.ZoomTo(9d, new Point(0, 0));

        Press(canvas, Key.D1);
        Assert.Equal(1d, canvas.Scale, 6);

        Press(canvas, Key.D0);
        Assert.Equal(4d, canvas.Scale, 6);

        window.Close();
        document.Dispose();
    }

    [AvaloniaFact]
    public void An_Unmodified_Key_Does_Not_Zoom()
    {
        var (window, canvas, document) = Host();

        var start = canvas.Scale;
        canvas.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.OemPlus,
            KeyModifiers = KeyModifiers.None
        });

        Assert.Equal(start, canvas.Scale, 6);

        window.Close();
        document.Dispose();
    }

    private static void Press(SvgViewerCanvas canvas, Key key)
    {
        canvas.Focus();
        canvas.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = KeyModifiers.Control
        });
    }

    private static PointerWheelEventArgs Wheel(SvgViewerCanvas canvas, double delta, Point position)
        => new(
            canvas,
            new Pointer(0, PointerType.Mouse, true),
            canvas,
            position,
            0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
            KeyModifiers.None,
            new Vector(0, delta))
        {
            RoutedEvent = InputElement.PointerWheelChangedEvent
        };
}
