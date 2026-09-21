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

    /// <summary>
    /// Laying the same board out again keeps the view on it, where showing a new one fits it.
    /// </summary>
    /// <remarks>
    /// A host that rearranges what it is holding -- a drawing moved on a board, or one of them
    /// rebuilt after an edit -- is not opening anything, and re-fitting there throws away whatever
    /// was being looked at. The two are separate calls rather than a flag, so neither can be had by
    /// accident.
    /// </remarks>
    [AvaloniaFact]
    public void Rearranging_Keeps_The_View_And_Showing_Fits_It()
    {
        using var first = SvgViewerDocument.LoadFromSvg(Wide);
        using var second = SvgViewerDocument.LoadFromSvg(Wide);

        var canvas = new SvgViewerCanvas();
        var window = new Window { Width = 400, Height = 200, Content = canvas };

        window.Show();

        canvas.Show(new[]
        {
            new SvgViewerPlacement(first.Svg, new SKPoint(0f, 0f)),
            new SvgViewerPlacement(second.Svg, new SKPoint(100f, 0f))
        });

        canvas.Measure(new Size(400, 200));
        canvas.Arrange(new Rect(0, 0, 400, 200));

        canvas.ZoomTo(9d, new Point(30, 40));

        var scale = canvas.Scale;
        var offsetX = canvas.OffsetX;
        var offsetY = canvas.OffsetY;

        canvas.Rearrange(new[]
        {
            new SvgViewerPlacement(first.Svg, new SKPoint(0f, 0f)),
            new SvgViewerPlacement(second.Svg, new SKPoint(100f, 60f))
        });

        Assert.Equal(scale, canvas.Scale, 6);
        Assert.Equal(offsetX, canvas.OffsetX, 6);
        Assert.Equal(offsetY, canvas.OffsetY, 6);

        // A board nobody has zoomed is held too: a drop that re-fitted would move the thing that
        // was just let go. Fit is what asks for the whole of it back.
        canvas.Fit();

        var fitted = canvas.Scale;

        canvas.Rearrange(new[]
        {
            new SvgViewerPlacement(first.Svg, new SKPoint(0f, 0f)),
            new SvgViewerPlacement(second.Svg, new SKPoint(300f, 0f))
        });

        Assert.Equal(fitted, canvas.Scale, 6);

        // And Show still means a board has arrived.
        canvas.Show(new[]
        {
            new SvgViewerPlacement(first.Svg, new SKPoint(0f, 0f)),
            new SvgViewerPlacement(second.Svg, new SKPoint(300f, 0f))
        });

        Assert.Equal(1d, canvas.Scale, 6);

        window.Close();
    }

    /// <summary>
    /// A board that grows at its top left leaves everything that did not move where it was.
    /// </summary>
    /// <remarks>
    /// The case the view is anchored in the arrangement's own coordinates for. Asserted on a painted
    /// pixel rather than through the mapping, because the mapping and the draw agreeing with each
    /// other is exactly what a view anchored on the union's corner would go on doing while it slid
    /// everything across the control.
    /// </remarks>
    [AvaloniaFact]
    public void A_Board_That_Grows_Leftwards_Holds_The_Rest_Still()
    {
        using var stays = SvgViewerDocument.LoadFromSvg(Blue);
        using var moves = SvgViewerDocument.LoadFromSvg(Wide);

        var canvas = new SvgViewerCanvas();
        var window = new Window { Width = 400, Height = 200, Background = Brushes.White, Content = canvas };

        window.Show();

        canvas.Show(new[]
        {
            new SvgViewerPlacement(moves.Svg, new SKPoint(0f, 0f)),
            new SvgViewerPlacement(stays.Svg, new SKPoint(200f, 0f))
        });

        canvas.Measure(new Size(400, 200));
        canvas.Arrange(new Rect(0, 0, 400, 200));

        // 300x50 in 400x200 fits at 4/3, so the middle of the drawing that stays -- arranged 250,25
        // -- is painted a third of the way down the right-hand third of the control.
        var on = new Point(250d * canvas.Scale + canvas.OffsetX, 25d * canvas.Scale + canvas.OffsetY);

        var was = Painted(window, (int)on.X, (int)on.Y);

        Assert.True(was.Blue > 200 && was.Red < 100, $"{was} is not the drawing that stays");

        // The other one is dragged well to the left of everything, so the union's corner moves by
        // 250 units. Anchored on that corner, the whole board would slide 333 pixels right.
        canvas.Rearrange(new[]
        {
            new SvgViewerPlacement(moves.Svg, new SKPoint(-250f, 0f)),
            new SvgViewerPlacement(stays.Svg, new SKPoint(200f, 0f))
        });

        var now = Painted(window, (int)on.X, (int)on.Y);

        Assert.True(now.Blue > 200 && now.Red < 100, $"{now} is not the drawing that stays");

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

    /// <summary>A drawing whose only shape has been moved clean off its own page.</summary>
    private const string Escaped = """
        <svg xmlns="http://www.w3.org/2000/svg" width="100" height="50" viewBox="0 0 100 50">
          <rect x="0" y="0" width="100" height="50" fill="#ff0000" transform="translate(120, 0)" />
        </svg>
        """;

    /// <summary>
    /// Ink moved off a drawing is cut at the drawing's own edge.
    /// </summary>
    /// <remarks>
    /// A drawing is its page. Every other way one of these pictures is rendered records at the cull
    /// rect and so cuts off ink beyond it — an export, the replay a generated class does. This canvas
    /// draws into a surface the size of the control, so without a clip it is the one renderer that
    /// shows what nothing else will, over whatever is placed beside it and answering no hit test.
    /// </remarks>
    [AvaloniaFact]
    public void Ink_Moved_Off_A_Drawing_Is_Cut_At_Its_Edge()
    {
        using var escaped = SvgViewerDocument.LoadFromSvg(Escaped);
        using var neighbour = SvgViewerDocument.LoadFromSvg(Blue);

        var canvas = new SvgViewerCanvas();
        var window = new Window { Width = 400, Height = 200, Background = Brushes.White, Content = canvas };

        window.Show();

        // The second sits exactly where the first one's shape has been dragged to.
        canvas.Show(new[]
        {
            new SvgViewerPlacement(escaped.Svg, new SKPoint(0f, 0f)),
            new SvgViewerPlacement(neighbour.Svg, new SKPoint(120f, 0f))
        });

        canvas.Measure(new Size(400, 200));
        canvas.Arrange(new Rect(0, 0, 400, 200));

        // The middle of the neighbour, which the escaped red rect covers exactly.
        var on = new Point(170d * canvas.Scale + canvas.OffsetX, 25d * canvas.Scale + canvas.OffsetY);

        var painted = Painted(window, (int)on.X, (int)on.Y);

        Assert.True(painted.Blue > 200 && painted.Red < 100, $"{painted} is the escaped ink, not the drawing placed there");

        window.Close();
    }

    /// <summary>
    /// A press past a drawing's edge falls on whatever is drawn there, not on the ink that escaped.
    /// </summary>
    /// <remarks>
    /// The companion to the clip, and the reason the answer is to clip rather than to widen the hit
    /// rectangle to the ink: two drawings whose ink overlaps would then both answer for the same
    /// point, and which of them won would be decided by the order the project happens to list them
    /// in rather than by what is on the screen.
    /// </remarks>
    [AvaloniaFact]
    public void A_Press_Past_A_Drawings_Edge_Falls_On_What_Is_Drawn_There()
    {
        using var escaped = SvgViewerDocument.LoadFromSvg(Escaped);
        using var neighbour = SvgViewerDocument.LoadFromSvg(Blue);

        var canvas = new SvgViewerCanvas();
        var window = new Window { Width = 400, Height = 200, Content = canvas };

        window.Show();

        canvas.Show(new[]
        {
            new SvgViewerPlacement(escaped.Svg, new SKPoint(0f, 0f)),
            new SvgViewerPlacement(neighbour.Svg, new SKPoint(120f, 0f))
        });

        canvas.Measure(new Size(400, 200));
        canvas.Arrange(new Rect(0, 0, 400, 200));

        var on = new Point(170d * canvas.Scale + canvas.OffsetX, 25d * canvas.Scale + canvas.OffsetY);

        Assert.True(canvas.TryGetPlacementAt(on, out var placement, out _));
        Assert.Same(canvas.Placements[1], placement);

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

        var canvas = new SvgViewerCanvas();
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

    /// <summary>
    /// A frame is part of what is on show, so the fit holds the whole of it.
    /// </summary>
    /// <remarks>
    /// Left out of the union, a frame drawn round a group of drawings would be cut off at the edge
    /// of the ink inside it — and its name, which is written above its top edge, would be off the
    /// top of the control.
    /// </remarks>
    /// <summary>
    /// A frame's name is the same size on the control however far the view is zoomed.
    /// </summary>
    /// <remarks>
    /// A caption names what it sits by; it is not part of what is drawn. Scaled with the drawings it
    /// was unreadable zoomed out and enormous zoomed in, and the strip a frame is taken hold of by
    /// went with it — a target that changed size as you approached it.
    /// </remarks>
    [AvaloniaFact]
    public void A_Frames_Name_Is_The_Same_Size_At_Any_Zoom()
    {
        using var drawing = SvgViewerDocument.LoadFromSvg(Wide);

        var canvas = new SvgViewerCanvas();
        var window = new Window { Width = 400, Height = 200, Content = canvas };

        window.Show();

        var framed = new SvgViewerFrame(new SKRect(-5f, -5f, 105f, 55f), "Large");

        canvas.Show(new[] { new SvgViewerPlacement(drawing.Svg, new SKPoint(0f, 0f)) }, new[] { framed });

        canvas.Measure(new Size(400, 200));
        canvas.Arrange(new Rect(0, 0, 400, 200));

        // Inside the frame rather than above it, so nothing about the arrangement makes room for it.
        Assert.Equal(framed.Bounds.Top, canvas.TitleOf(framed).Top, 3);
        Assert.True(canvas.TitleOf(framed).Bottom < framed.Bounds.Bottom, "the name should sit inside the frame.");

        var wasScale = (float)canvas.Scale;
        var wasBand = canvas.TitleOf(framed).Height;

        canvas.ZoomIn();
        canvas.ZoomIn();

        var nowBand = canvas.TitleOf(framed).Height;

        // In the drawings' own units it shrank, by exactly as much as the view grew — which is what
        // staying one size on the control means.
        Assert.True(nowBand < wasBand, "Zooming in should shrink the band in the drawings' own units.");
        Assert.Equal(wasBand * wasScale, nowBand * (float)canvas.Scale, 3);
    }

    [AvaloniaFact]
    public void A_Frame_With_No_Name_Has_No_Strip_To_Take_It_By()
    {
        using var drawing = SvgViewerDocument.LoadFromSvg(Wide);

        var canvas = new SvgViewerCanvas();
        var window = new Window { Width = 400, Height = 200, Content = canvas };

        window.Show();

        var framed = new SvgViewerFrame(new SKRect(0f, 0f, 140f, 90f));

        canvas.Show(new[] { new SvgViewerPlacement(drawing.Svg, new SKPoint(0f, 0f)) }, new[] { framed });

        canvas.Measure(new Size(400, 200));
        canvas.Arrange(new Rect(0, 0, 400, 200));

        Assert.True(canvas.TitleOf(framed).IsEmpty);
    }

    [AvaloniaFact]
    public void A_Frame_Is_Fitted_With_What_It_Holds()
    {
        using var drawing = SvgViewerDocument.LoadFromSvg(Wide);

        var canvas = new SvgViewerCanvas();
        var window = new Window { Width = 400, Height = 200, Content = canvas };

        window.Show();

        // The drawing is 100x50 at the origin; the frame runs 10 wider and 10 further down, and its
        // name is written inside it rather than needing room of its own.
        canvas.Show(
            new[] { new SvgViewerPlacement(drawing.Svg, new SKPoint(0f, 0f)) },
            new[] { new SvgViewerFrame(new SKRect(-5f, -5f, 105f, 55f), "Large") });

        canvas.Measure(new Size(400, 200));
        canvas.Arrange(new Rect(0, 0, 400, 200));

        Assert.Single(canvas.Frames);

        // 110 across and 60 down, so the height binds. A frame is exactly what its bounds say: its
        // name sits inside, so nothing is added for it and nothing is cut off by leaving it out.
        Assert.Equal(200d / 60d, canvas.Scale, 6);

        window.Close();
    }

    [AvaloniaFact]
    public void A_Frame_Is_Drawn_Round_What_It_Holds_And_Is_Not_A_Drawing()
    {
        using var drawing = SvgViewerDocument.LoadFromSvg(Blue);

        var canvas = new SvgViewerCanvas();
        var window = new Window { Width = 400, Height = 200, Background = Brushes.White, Content = canvas };

        window.Show();

        canvas.Show(
            new[] { new SvgViewerPlacement(drawing.Svg, new SKPoint(20f, 20f)) },
            new[] { new SvgViewerFrame(new SKRect(0f, 0f, 140f, 90f)) });

        canvas.Measure(new Size(400, 200));
        canvas.Arrange(new Rect(0, 0, 400, 200));

        // The frame decides the fit: 140x90 in 400x200 is bounded by height.
        Assert.Equal(200d / 90d, canvas.Scale, 6);

        // A click inside the frame but off the ink falls through it: a frame is furniture, and the
        // host asks the canvas which drawing was hit.
        Assert.False(canvas.TryGetPlacementAt(new Point(20, 20), out var placement, out _));
        Assert.Null(placement);

        // Drawn, though — the top edge of the frame runs along the top of what is on show, and the
        // middle of it is still the ground rather than a fill.
        var edge = 0;

        for (var x = 100; x < 300; x++)
        {
            var pixel = Painted(window, x, (int)canvas.OffsetY + 1);

            if (pixel.Red < 220 && pixel.Blue < 220)
            {
                edge++;
            }
        }

        Assert.True(edge > 10, "the frame's top edge was not painted");

        window.Close();
    }

    /// <summary>How many pixels of a rectangle are not the drawing's own blue.</summary>
    private static int Written(Window window, PixelRect where)
        => Counted(window, where, pixel => pixel.Blue < 200 || pixel.Red > 80);

    /// <summary>
    /// A group's name is drawn over the drawings its frame holds, not under them.
    /// </summary>
    /// <remarks>
    /// A frame is a ground and is drawn first; its name is not. Written with the frame it went
    /// behind every drawing that reaches into the corner it sits in — which, since the name moved
    /// inside, is most of them.
    /// </remarks>
    [AvaloniaTheory]
    [InlineData("Large", true)]
    [InlineData(null, false)]
    public void A_Frames_Name_Is_Drawn_Over_What_It_Holds(string? name, bool written)
    {
        using var drawing = SvgViewerDocument.LoadFromSvg(Blue);

        var canvas = new SvgViewerCanvas();
        var window = new Window { Width = 400, Height = 200, Background = Brushes.White, Content = canvas };

        window.Show();

        // The frame is exactly the drawing, so its corner — where the name goes — is solid blue.
        canvas.Show(
            new[] { new SvgViewerPlacement(drawing.Svg, new SKPoint(0f, 0f)) },
            new[] { new SvgViewerFrame(new SKRect(0f, 0f, 100f, 50f), name) });

        canvas.Measure(new Size(400, 200));
        canvas.Arrange(new Rect(0, 0, 400, 200));

        var corner = new PixelRect(
            (int)canvas.OffsetX + 2,
            (int)canvas.OffsetY + 2,
            80,
            (int)(SvgViewerCanvas.DefaultCaptionSize * 1.6d));

        Assert.Equal(written, Written(window, corner) > 5);

        window.Close();
    }

    /// <summary>A drawing that is not orange, so the ring cannot be confused with its ink.</summary>
    private const string Blue = """
        <svg xmlns="http://www.w3.org/2000/svg" width="100" height="50" viewBox="0 0 100 50">
          <rect x="0" y="0" width="100" height="50" fill="#0000ff" />
        </svg>
        """;

    /// <summary>How much of a rectangle of the frame the selection ring paints.</summary>
    private static int Ringed(Window window, PixelRect where)
        // Orange: red up, blue down, and green in between — which the blue drawing under it, the
        // grey bounds outline and the white ground are all outside.
        => Counted(window, where, pixel => pixel.Red > 180 && pixel.Green is > 70 and < 230 && pixel.Blue < 110);

    /// <summary>How many pixels of a rectangle of the rendered frame <paramref name="is"/> answers for.</summary>
    /// <remarks>
    /// One capture and one decode for the whole rectangle, and one place that asks a window what it
    /// painted: three copies of this had grown, and each of them reached for the deprecated save
    /// that this repository counts the warnings of.
    /// </remarks>
    private static int Counted(Window window, PixelRect where, Func<SKColor, bool> @is)
    {
        var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("No rendered frame was captured.");

        var path = Path.Combine(Path.GetTempPath(), $"svg-viewer-pixels-{Guid.NewGuid():N}.png");

        frame.Save(path);

        try
        {
            using var bitmap = SKBitmap.Decode(path);

            var found = 0;

            for (var x = where.X; x < where.Right && x < bitmap!.Width; x++)
            {
                for (var y = where.Y; y < where.Bottom && y < bitmap.Height; y++)
                {
                    if (@is(bitmap.GetPixel(x, y)))
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

        var canvas = new SvgViewerCanvas();
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
        // or jumps. With the accelerator held, since that is what makes the wheel zoom.
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

    /// <summary>
    /// The wheel on its own moves the view.
    /// </summary>
    /// <remarks>
    /// It used to zoom whatever was held, which left a trackpad with no way to pan: there is no
    /// middle button on one, and a press pans only where it lands on nothing.
    /// </remarks>
    /// <summary>
    /// A pinch on the trackpad zooms, without a modifier and without the wheel.
    /// </summary>
    /// <remarks>
    /// The platform reports it as a gesture of its own, so it reaches the canvas whatever the wheel
    /// has been given to do.
    /// </remarks>
    [AvaloniaFact]
    public void A_Pinch_Zooms_About_The_Pointer()
    {
        var (window, canvas, document) = Host();

        var anchor = new Point(310, 140);
        Assert.True(canvas.TryGetDrawingPoint(anchor, out var before));

        canvas.RaiseEvent(Magnify(canvas, 0.25d, anchor));

        // A quarter larger than it was, and the point under the pointer has not moved.
        Assert.Equal(4d * 1.25d, canvas.Scale, 6);
        Assert.True(canvas.TryGetDrawingPoint(anchor, out var after));
        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);

        window.Close();
        document.Dispose();
    }

    [AvaloniaFact]
    public void A_Pinch_Compounds_The_Way_The_Wheel_Does()
    {
        var (window, canvas, document) = Host();

        var start = canvas.Scale;

        canvas.RaiseEvent(Magnify(canvas, 0.1d, new Point(200, 100)));
        canvas.RaiseEvent(Magnify(canvas, 0.1d, new Point(200, 100)));

        // Multiplied rather than added to: the platform says how much larger than a moment ago.
        Assert.Equal(start * 1.1d * 1.1d, canvas.Scale, 6);

        // And pinching the other way takes it back.
        canvas.RaiseEvent(Magnify(canvas, -0.5d, new Point(200, 100)));

        Assert.True(canvas.Scale < start * 1.1d * 1.1d, "Pinching in should zoom out.");

        window.Close();
        document.Dispose();
    }

    [AvaloniaFact]
    public void A_Pinch_Is_Read_Whichever_Way_The_Backend_Sends_It()
    {
        // macOS has one number and puts it in one of the two; reading the other as well would double
        // the gesture on a backend that filled in both.
        var (window, canvas, document) = Host();

        var start = canvas.Scale;

        canvas.RaiseEvent(Magnify(canvas, 0.2d, new Point(200, 100), across: true));

        Assert.Equal(start * 1.2d, canvas.Scale, 6);

        window.Close();
        document.Dispose();
    }

    [AvaloniaFact]
    public void A_Wheel_With_No_Accelerator_Pans()
    {
        var (window, canvas, document) = Host();

        var scale = canvas.Scale;
        Assert.True(canvas.TryGetDrawingPoint(new Point(200, 100), out var before));

        canvas.RaiseEvent(Wheel(canvas, -1d, new Point(200, 100), KeyModifiers.None, across: 2d));

        Assert.True(canvas.TryGetDrawingPoint(new Point(200, 100), out var after));

        // The view moved and nothing about how close it is changed.
        Assert.Equal(scale, canvas.Scale, 6);
        Assert.NotEqual(before.X, after.X, 3);
        Assert.NotEqual(before.Y, after.Y, 3);

        window.Close();
        document.Dispose();
    }

    [AvaloniaFact]
    public void A_Scroll_Carries_The_Canvas_The_Way_The_Fingers_Went()
    {
        var (window, canvas, document) = Host();

        Assert.True(canvas.TryGetDrawingPoint(new Point(200, 100), out var before));

        // A scroll downwards is a negative delta, and it carries what is on the canvas up — so the
        // point under the pointer is one further down the drawing than it was.
        canvas.RaiseEvent(Wheel(canvas, -1d, new Point(200, 100), KeyModifiers.None));

        Assert.True(canvas.TryGetDrawingPoint(new Point(200, 100), out var after));

        Assert.True(after.Y > before.Y, "Scrolling down should bring what is below into view.");

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

    // ---- taking hold of what is on the canvas ------------------------------------------------

    /// <summary>A canvas of one blue drawing at the origin, laid out in a 400x200 window.</summary>
    private static (Window Window, SvgViewerCanvas Canvas, SvgViewerDocument Drawing) Held()
    {
        var drawing = SvgViewerDocument.LoadFromSvg(Blue);
        var canvas = new SvgViewerCanvas();
        var window = new Window { Width = 400, Height = 200, Background = Brushes.White, Content = canvas };

        window.Show();

        canvas.Show(new[] { new SvgViewerPlacement(drawing.Svg, new SKPoint(0f, 0f)) });

        canvas.Measure(new Size(400, 200));
        canvas.Arrange(new Rect(0, 0, 400, 200));

        return (window, canvas, drawing);
    }

    private static void Grab(SvgViewerCanvas canvas, object item, SKRect bounds)
        => canvas.Grip = at => bounds.Contains(at.X, at.Y) ? (item, bounds) : null;

    /// <summary>
    /// A rectangle is taken hold of by the line round it and by nothing it encloses, with the same
    /// slack on the control however far the view is zoomed.
    /// </summary>
    /// <remarks>
    /// What a drawing is carried by on a board. Inside it belongs to the shapes, which a press has
    /// to reach; and the slack is held in control pixels, so zooming out does not shrink the only
    /// target there is down to nothing.
    /// </remarks>
    [AvaloniaFact]
    public void A_Rectangle_Is_Grabbed_By_Its_Line_And_Not_By_What_It_Holds()
    {
        var (window, canvas, drawing) = Held();

        using var owner = drawing;

        var area = new SKRect(0f, 0f, 100f, 50f);

        // 100x50 fitted in 400x200 is four control pixels to the unit, so the four pixels of slack
        // are one unit either side of the line.
        Assert.Equal(4d, canvas.Scale, 6);

        Assert.True(canvas.Grabs(area, new SKPoint(0f, 25f)), "the line itself");
        Assert.True(canvas.Grabs(area, new SKPoint(-0.9f, 25f)), "just outside the line");
        Assert.True(canvas.Grabs(area, new SKPoint(0.9f, 25f)), "just inside the line");
        Assert.False(canvas.Grabs(area, new SKPoint(1.1f, 25f)), "past the slack");
        Assert.False(canvas.Grabs(area, new SKPoint(50f, 25f)), "the middle, which is the drawing");

        canvas.ActualSize();

        // A quarter of the way zoomed out, so the same four pixels are four units.
        Assert.Equal(1d, canvas.Scale, 6);

        Assert.True(canvas.Grabs(area, new SKPoint(3.9f, 25f)), "inside the line at this zoom");
        Assert.False(canvas.Grabs(area, new SKPoint(4.1f, 25f)), "past the slack at this zoom");

        window.Close();
    }

    [AvaloniaFact]
    public void A_Press_On_Something_Held_Moves_It_Rather_Than_Panning()
    {
        var (window, canvas, drawing) = Held();

        using var owner = drawing;

        var item = new object();

        Grab(canvas, item, new SKRect(0f, 0f, 100f, 50f));

        SvgViewerMove? moved = null;

        canvas.Moved += (_, move) => moved = move;

        var offsetX = canvas.OffsetX;
        var offsetY = canvas.OffsetY;

        // Forty control pixels at the fit's scale of four, which is ten in the arrangement.
        Assert.Equal(4d, canvas.Scale, 6);

        Press(window, canvas, new Point(20, 20));
        Move(window, canvas, new Point(60, 20));
        Release(window, canvas, new Point(60, 20));

        Assert.NotNull(moved);
        Assert.Same(item, moved!.Value.Item);
        Assert.Equal(10f, moved.Value.By.X, 3);
        Assert.Equal(0f, moved.Value.By.Y, 3);

        // The view is where it was: a press on something held is not a pan.
        Assert.Equal(offsetX, canvas.OffsetX, 6);
        Assert.Equal(offsetY, canvas.OffsetY, 6);

        // And nothing was committed — the placement still says where the host put it.
        Assert.Equal(0f, canvas.Placements[0].At.X);

        window.Close();
    }

    [AvaloniaFact]
    public void A_Press_On_Nothing_Held_Still_Pans()
    {
        var (window, canvas, drawing) = Held();

        using var owner = drawing;

        // A grip that answers about one corner only, pressed well outside it.
        Grab(canvas, new object(), new SKRect(0f, 0f, 1f, 1f));

        var moved = false;

        canvas.Moved += (_, _) => moved = true;

        var offsetX = canvas.OffsetX;

        Press(window, canvas, new Point(200, 100));
        Move(window, canvas, new Point(260, 100));
        Release(window, canvas, new Point(260, 100));

        Assert.False(moved);
        Assert.Equal(offsetX + 60d, canvas.OffsetX, 6);

        window.Close();
    }

    [AvaloniaFact]
    public void A_Press_That_Does_Not_Travel_Is_Still_A_Pick()
    {
        var (window, canvas, drawing) = Held();

        using var owner = drawing;

        Grab(canvas, new object(), new SKRect(0f, 0f, 100f, 50f));

        var picked = 0;
        var moved = 0;

        canvas.Picked += (_, _) => picked++;
        canvas.Moved += (_, _) => moved++;

        // Inside the four pixels a hand moves while clicking.
        Press(window, canvas, new Point(20, 20));
        Move(window, canvas, new Point(22, 21));
        Release(window, canvas, new Point(22, 21));

        Assert.Equal(1, picked);
        Assert.Equal(0, moved);

        window.Close();
    }

    [AvaloniaFact]
    public void What_Is_Carried_Follows_The_Pointer_And_The_Rest_Holds_Still()
    {
        var (window, canvas, drawing) = Held();

        using var owner = drawing;

        Grab(canvas, new object(), new SKRect(0f, 0f, 100f, 50f));

        var offsetX = canvas.OffsetX;
        var scale = canvas.Scale;

        Press(window, canvas, new Point(20, 20));
        Move(window, canvas, new Point(120, 20));

        // Painted where the pointer has it, and not where it was: the drawing is 100 units wide at
        // scale 4, so a hundred pixels across is a quarter of its own width.
        var carried = Painted(window, 120, 20);

        Assert.True(carried.Blue > 200 && carried.Red < 100, $"{carried} is not the drawing, carried");

        // The ground did not move under it.
        Assert.Equal(offsetX, canvas.OffsetX, 6);
        Assert.Equal(scale, canvas.Scale, 6);

        Release(window, canvas, new Point(120, 20));

        window.Close();
    }

    /// <summary>
    /// The ring travels with the drawing it is on, while that drawing is being carried.
    /// </summary>
    /// <remarks>
    /// It is drawn once, outside the loop that draws the drawings, so that a canvas holding several
    /// does not put one on each of them — and that also put it outside the offset a carry is drawn
    /// at, so it stayed behind on the board while the drawing it belongs to moved out from under it.
    /// Only visible mid-drag: the drop re-lays the board and traces the ring again, so by then it is
    /// in the right place either way.
    /// </remarks>
    [AvaloniaFact]
    public void A_Ring_Is_Carried_With_What_It_Is_Drawn_On()
    {
        var (window, canvas, drawing) = Held();

        using var owner = drawing;

        // A square well inside the drawing, so it is unambiguously part of what is carried.
        var ring = new SKPath();

        ring.AddRect(new SKRect(10f, 10f, 40f, 40f));

        canvas.Highlight = ring;

        Grab(canvas, new object(), new SKRect(0f, 0f, 100f, 50f));

        Press(window, canvas, new Point(20, 20));
        Move(window, canvas, new Point(120, 20));

        // The pointer has taken the drawing a hundred pixels across, so the ring's own square — 10
        // to 40 in drawing units, which is 40 to 160 on a control at scale 4 — is now 140 to 260.
        // Sampled across its left edge, since a ring is a stroke and its middle is the drawing.
        Assert.True(Ringed(window, new PixelRect(132, 60, 18, 80)) > 0, "the ring was not carried with the drawing");
        Assert.Equal(0, Ringed(window, new PixelRect(32, 60, 18, 80)));

        Release(window, canvas, new Point(120, 20));

        window.Close();
    }

    [AvaloniaFact]
    public void Escape_Takes_A_Move_Back_And_Raises_Nothing()
    {
        var (window, canvas, drawing) = Held();

        using var owner = drawing;

        Grab(canvas, new object(), new SKRect(0f, 0f, 100f, 50f));

        var moved = false;

        canvas.Moved += (_, _) => moved = true;

        Press(window, canvas, new Point(20, 20));
        Move(window, canvas, new Point(120, 20));

        canvas.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Escape,
            KeyModifiers = KeyModifiers.None
        });

        Release(window, canvas, new Point(120, 20));

        Assert.False(moved);

        // Back where it was, rather than left where the pointer had it.
        var back = Painted(window, 20, 20);

        Assert.True(back.Blue > 200 && back.Red < 100, $"{back} is not the drawing, put back");

        window.Close();
    }

    /// <summary>Where <paramref name="at"/> — a point in the canvas's own space — is in the window's.</summary>
    /// <remarks>
    /// A pointer event reports its position by way of the visual root, so a control-local point
    /// handed over as the root's is off by wherever the control sits.
    /// </remarks>
    private static Point Root(Window window, SvgViewerCanvas canvas, Point at)
        => canvas.TranslatePoint(at, window) ?? at;

    private static void Press(Window window, SvgViewerCanvas canvas, Point at)
        => canvas.RaiseEvent(new PointerPressedEventArgs(
            canvas,
            new Pointer(0, PointerType.Mouse, true),
            window,
            Root(window, canvas, at),
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None)
        {
            RoutedEvent = InputElement.PointerPressedEvent
        });

    private static void Move(Window window, SvgViewerCanvas canvas, Point at)
        => canvas.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent,
            canvas,
            new Pointer(0, PointerType.Mouse, true),
            window,
            Root(window, canvas, at),
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));

    private static void Release(Window window, SvgViewerCanvas canvas, Point at)
        => canvas.RaiseEvent(new PointerReleasedEventArgs(
            canvas,
            new Pointer(0, PointerType.Mouse, true),
            window,
            Root(window, canvas, at),
            0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None,
            MouseButton.Left)
        {
            RoutedEvent = InputElement.PointerReleasedEvent
        });

    /// <summary>A trackpad magnify gesture, which the platform reports in one of the two axes.</summary>
    private static PointerDeltaEventArgs Magnify(
        SvgViewerCanvas canvas,
        double magnification,
        Point position,
        bool across = false)
        => new(
            InputElement.PointerTouchPadGestureMagnifyEvent,
            canvas,
            new Pointer(0, PointerType.Mouse, false),
            canvas,
            position,
            0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
            KeyModifiers.None,
            across ? new Vector(magnification, 0) : new Vector(0, magnification));

    /// <summary>A wheel notch, which zooms only while the accelerator is held.</summary>
    private static PointerWheelEventArgs Wheel(
        SvgViewerCanvas canvas,
        double delta,
        Point position,
        KeyModifiers modifiers = KeyModifiers.Meta,
        double across = 0d)
        => new(
            canvas,
            new Pointer(0, PointerType.Mouse, true),
            canvas,
            position,
            0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
            modifiers,
            new Vector(across, delta))
        {
            RoutedEvent = InputElement.PointerWheelChangedEvent
        };
}
