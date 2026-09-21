using System;
using System.Collections.Generic;
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

/// <summary>
/// Sweeping a rectangle over the drawings, rather than panning them.
/// </summary>
/// <remarks>
/// Through the pointer, like the gizmo's own tests: what is being checked is the whole path from a
/// press on a pixel to a rectangle handed to the host, and the two things worth doubting — which
/// claim takes the press, and what the rectangle says afterwards — are at either end of it.
///
/// The numbers are exact. A sweep from 10,10 to 60,40 is that rectangle, and a tolerance wide
/// enough to hide a coordinate space mixed up is wide enough to hide everything else.
/// </remarks>
public class SvgViewerMarqueeTests
{
    /// <summary>A 100x100 drawing with two shapes, twenty apart, each twenty across.</summary>
    private const string Pair = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <rect id="one" x="10" y="10" width="20" height="20" fill="#3366cc" />
          <rect id="two" x="60" y="60" width="20" height="20" fill="#cc3366" />
        </svg>
        """;

    private const RawInputModifiers Held = RawInputModifiers.LeftMouseButton;

    private static (Window Window, SvgViewerCanvas Canvas, SvgViewerDocument Document) Host(string markup = Pair)
    {
        var document = SvgViewerDocument.LoadFromSvg(markup);
        var canvas = new SvgViewerCanvas { Svg = document.Svg, IsMarqueeEnabled = true };

        var window = new Window { Width = 400, Height = 400, Background = Brushes.White, Content = canvas };

        window.Show();
        canvas.Measure(new Size(400, 400));
        canvas.Arrange(new Rect(0, 0, 400, 400));

        Dispatcher.UIThread.RunJobs();

        return (window, canvas, document);
    }

    /// <summary>Where a point of the drawing falls in the window.</summary>
    private static Point At(Window window, SvgViewerCanvas canvas, float x, float y)
    {
        Assert.True(canvas.TryGetControlPoint(new SKPoint(x, y), out var point));

        return canvas.TranslatePoint(point, window)
               ?? throw new InvalidOperationException("The canvas is not in the window.");
    }

    private static void Drag(
        Window window,
        SvgViewerCanvas canvas,
        (float X, float Y) from,
        (float X, float Y) to,
        MouseButton button = MouseButton.Left)
    {
        var held = button == MouseButton.Middle ? RawInputModifiers.MiddleMouseButton : Held;

        window.MouseDown(At(window, canvas, from.X, from.Y), button);
        Dispatcher.UIThread.RunJobs();

        window.MouseMove(At(window, canvas, to.X, to.Y), held);
        Dispatcher.UIThread.RunJobs();

        window.MouseUp(At(window, canvas, to.X, to.Y), button);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Every rectangle a sweep reported, in the order they were swept.</summary>
    private static List<SKRect> Swept(SvgViewerCanvas canvas)
    {
        var swept = new List<SKRect>();

        canvas.Marqueed += (_, rectangle) => swept.Add(rectangle);

        return swept;
    }

    [AvaloniaFact]
    public void A_Left_Drag_Sweeps_Instead_Of_Panning()
    {
        var (window, canvas, document) = Host();
        var swept = Swept(canvas);
        var offset = canvas.OffsetX;

        Drag(window, canvas, (10f, 10f), (60f, 40f));

        Assert.Equal(offset, canvas.OffsetX);
        Assert.Equal(new SKRect(10f, 10f, 60f, 40f), Assert.Single(swept));

        window.Close();
        document.Dispose();
    }

    /// <summary>A rectangle is the two corners, whichever order the hand drew them in.</summary>
    [AvaloniaFact]
    public void A_Sweep_Is_The_Same_Either_Way_Round()
    {
        var (window, canvas, document) = Host();
        var swept = Swept(canvas);

        Drag(window, canvas, (60f, 40f), (10f, 10f));

        Assert.Equal(new SKRect(10f, 10f, 60f, 40f), Assert.Single(swept));

        window.Close();
        document.Dispose();
    }

    /// <summary>The way out: the view is still reachable while the left button sweeps.</summary>
    [AvaloniaFact]
    public void The_Middle_Button_Still_Pans()
    {
        var (window, canvas, document) = Host();
        var swept = Swept(canvas);
        var offset = canvas.OffsetX;

        Drag(window, canvas, (10f, 10f), (60f, 40f), MouseButton.Middle);

        Assert.NotEqual(offset, canvas.OffsetX);
        Assert.Empty(swept);

        window.Close();
        document.Dispose();
    }

    [AvaloniaFact]
    public void A_Left_Drag_Pans_While_The_Sweep_Is_Off()
    {
        var (window, canvas, document) = Host();

        canvas.IsMarqueeEnabled = false;

        var swept = Swept(canvas);
        var offset = canvas.OffsetX;

        Drag(window, canvas, (10f, 10f), (60f, 40f));

        Assert.NotEqual(offset, canvas.OffsetX);
        Assert.Empty(swept);

        window.Close();
        document.Dispose();
    }

    /// <summary>
    /// A sweep that never travels is a click.
    /// </summary>
    /// <remarks>
    /// Load-bearing rather than a nicety: with nothing selected the host's handles answer for
    /// nothing, so in the mode every press reaches the sweep — and without this there would be no
    /// way to select a first element and nothing would ever get handles.
    /// </remarks>
    [AvaloniaFact]
    public void A_Sweep_That_Never_Travels_Is_A_Pick()
    {
        var (window, canvas, document) = Host();
        var swept = Swept(canvas);
        var picked = 0;

        canvas.Picked += (_, _) => picked++;

        var at = At(window, canvas, 10f, 10f);

        window.MouseDown(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // Inside the four pixel slack a press is allowed to wander in.
        window.MouseMove(new Point(at.X + 2d, at.Y), Held);
        Dispatcher.UIThread.RunJobs();

        window.MouseUp(new Point(at.X + 2d, at.Y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, picked);
        Assert.Empty(swept);

        window.Close();
        document.Dispose();
    }

    /// <summary>The handles are the narrower claim, and they answer first.</summary>
    [AvaloniaFact]
    public void The_Handles_Answer_Before_The_Sweep()
    {
        var (window, canvas, document) = Host();
        var swept = Swept(canvas);
        var begun = 0;

        canvas.IsEditTarget = _ => true;
        canvas.EditBegun += (_, _) => begun++;

        Drag(window, canvas, (10f, 10f), (60f, 40f));

        Assert.Equal(1, begun);
        Assert.Empty(swept);

        window.Close();
        document.Dispose();
    }

    [AvaloniaFact]
    public void Escape_Takes_The_Sweep_Back()
    {
        var (window, canvas, document) = Host();
        var swept = Swept(canvas);
        var picked = 0;

        canvas.Picked += (_, _) => picked++;

        window.MouseDown(At(window, canvas, 10f, 10f), MouseButton.Left);
        window.MouseMove(At(window, canvas, 60f, 40f), Held);
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        window.MouseUp(At(window, canvas, 60f, 40f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(swept);
        Assert.Equal(0, picked);

        // And the gesture is not stuck to the pointer: the next one is reported as usual.
        Drag(window, canvas, (10f, 10f), (60f, 40f));

        Assert.Single(swept);

        window.Close();
        document.Dispose();
    }

    /// <summary>A rectangle measured on one arrangement names nothing in the one that replaces it.</summary>
    [AvaloniaFact]
    public void A_Drawing_Swapped_Under_A_Sweep_Drops_It()
    {
        var (window, canvas, document) = Host();
        var swept = Swept(canvas);

        using var other = SvgViewerDocument.LoadFromSvg(Pair);

        window.MouseDown(At(window, canvas, 10f, 10f), MouseButton.Left);
        window.MouseMove(At(window, canvas, 60f, 40f), Held);
        Dispatcher.UIThread.RunJobs();

        canvas.Svg = other.Svg;
        Dispatcher.UIThread.RunJobs();

        window.MouseUp(At(window, canvas, 60f, 40f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(swept);

        window.Close();
        document.Dispose();
    }

    /// <summary>
    /// The ground holds still while a rectangle is being drawn on it.
    /// </summary>
    /// <remarks>
    /// Which is what lets the two corners be held in the drawings' own space: neither of them can be
    /// moved under the hand by a wheel that arrives mid-sweep.
    /// </remarks>
    [AvaloniaFact]
    public void A_Sweep_Holds_The_View_Still()
    {
        var (window, canvas, document) = Host();
        var scale = canvas.Scale;
        var offset = canvas.OffsetX;

        window.MouseDown(At(window, canvas, 10f, 10f), MouseButton.Left);
        window.MouseMove(At(window, canvas, 60f, 40f), Held);
        Dispatcher.UIThread.RunJobs();

        window.MouseWheel(At(window, canvas, 30f, 30f), new Vector(0d, 1d));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(scale, canvas.Scale);
        Assert.Equal(offset, canvas.OffsetX);

        window.MouseUp(At(window, canvas, 60f, 40f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        window.Close();
        document.Dispose();
    }
}
