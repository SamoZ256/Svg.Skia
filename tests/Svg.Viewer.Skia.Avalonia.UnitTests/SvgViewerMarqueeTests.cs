using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>A group of two shapes, and a loose one beside them.</summary>
    private const string Grouped = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <g id="pair">
            <rect id="a" x="10" y="10" width="20" height="20" fill="#3366cc" />
            <rect id="b" x="40" y="10" width="20" height="20" fill="#33cc66" />
          </g>
          <rect id="loose" x="10" y="60" width="20" height="20" fill="#cc3366" />
        </svg>
        """;

    /// <summary>Two shapes that are not drawn, and one that is.</summary>
    private const string Unseen = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <rect id="gone" x="10" y="10" width="20" height="20" display="none" />
          <rect id="unseen" x="40" y="10" width="20" height="20" visibility="hidden" />
          <rect id="drawn" x="70" y="10" width="20" height="20" fill="#3366cc" />
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

    /// <summary>The ids a sweep of that rectangle caught, per drawing, as one string.</summary>
    private static string Caught(SvgViewerCanvas canvas, SKRect marquee)
        => string.Join(
            " | ",
            canvas.Enclosed(marquee).Select(
                found => string.Join(" ", found.Elements.Select(element => element.ID ?? "?"))));

    /// <summary>
    /// A pick names an element through its drawing, and still names it after a rebuild.
    /// </summary>
    /// <remarks>
    /// The reason a selection holds addresses rather than elements: a rebuilt drawing shares no
    /// element with the one it replaced, so anything holding a reference would be pointing at a
    /// document nobody is looking at any more.
    /// </remarks>
    [AvaloniaFact]
    public void A_Pick_Names_An_Element_By_Where_It_Sits()
    {
        var (window, canvas, document) = Host();
        var placement = new SvgViewerPlacement(document.Svg, new SKPoint(200f, 0f));
        var pick = new SvgViewerPick(placement, "0");

        Assert.Equal("one", pick.Element?.ID);

        // And its outline is traced where its drawing sits, not where the drawing's own origin is.
        var traced = pick.Outline();

        Assert.NotNull(traced);
        Assert.True(traced!.Bounds.Left >= 200f, $"the outline is at {traced.Bounds}, not beside its drawing");

        Assert.Null(new SvgViewerPick(placement, "9/9").Element);

        window.Close();
        document.Dispose();
    }

    /// <summary>Several picks are one path, which is what lets one ring show a whole selection.</summary>
    [AvaloniaFact]
    public void Several_Picks_Are_One_Outline()
    {
        var (window, canvas, document) = Host();
        var placement = new SvgViewerPlacement(document.Svg, default);

        var traced = SvgViewerPicks.Outline(
            new[] { new SvgViewerPick(placement, "0"), new SvgViewerPick(placement, "1") });

        Assert.NotNull(traced);

        // Spanning both shapes: 10..30 and 60..80.
        Assert.Equal(10f, traced!.Bounds.Left, 3);
        Assert.Equal(80f, traced.Bounds.Right, 3);

        Assert.Null(SvgViewerPicks.Outline(Array.Empty<SvgViewerPick>()));

        window.Close();
        document.Dispose();
    }

    [AvaloniaFact]
    public void Only_What_Is_Wholly_Inside_Is_Caught()
    {
        var (window, canvas, document) = Host();

        // Round the first shape, and two units short of the second.
        Assert.Equal("one", Caught(canvas, new SKRect(0f, 0f, 78f, 78f)));

        // And round both.
        Assert.Equal("one two", Caught(canvas, new SKRect(0f, 0f, 100f, 100f)));

        // A rectangle inside a shape catches nothing: the shape is not inside it.
        Assert.Equal(string.Empty, Caught(canvas, new SKRect(12f, 12f, 28f, 28f)));

        window.Close();
        document.Dispose();
    }

    /// <summary>
    /// A group caught whole is the answer, rather than the group and everything under it.
    /// </summary>
    /// <remarks>
    /// The walk hands back both, and answering with both would have a gesture applied to a shape
    /// and then again to the group holding it.
    /// </remarks>
    [AvaloniaFact]
    public void A_Group_Is_Caught_Instead_Of_Its_Children()
    {
        var (window, canvas, document) = Host(Grouped);

        Assert.Equal("pair", Caught(canvas, new SKRect(0f, 0f, 70f, 40f)));

        window.Close();
        document.Dispose();
    }

    /// <summary>And a child on its own where the group around it is not caught.</summary>
    [AvaloniaFact]
    public void A_Child_Is_Caught_Where_Its_Group_Is_Not()
    {
        var (window, canvas, document) = Host(Grouped);

        Assert.Equal("a", Caught(canvas, new SKRect(0f, 0f, 35f, 40f)));

        window.Close();
        document.Dispose();
    }

    /// <summary>What is not drawn is not caught, which the rectangle walk does not answer itself.</summary>
    [AvaloniaFact]
    public void What_Is_Not_Drawn_Is_Not_Caught()
    {
        var (window, canvas, document) = Host(Unseen);

        Assert.Equal("drawn", Caught(canvas, new SKRect(0f, 0f, 100f, 100f)));

        window.Close();
        document.Dispose();
    }

    /// <summary>
    /// One sweep catches elements of every drawing it covers.
    /// </summary>
    /// <remarks>
    /// The rectangle is taken into each drawing's own space in turn, which is the whole of how a
    /// board of several drawings is swept by one gesture.
    /// </remarks>
    [AvaloniaFact]
    public void A_Sweep_Catches_Elements_Across_Several_Drawings()
    {
        using var left = SvgViewerDocument.LoadFromSvg(Pair);
        using var right = SvgViewerDocument.LoadFromSvg(Pair);

        var canvas = new SvgViewerCanvas();
        var placed = new[]
        {
            new SvgViewerPlacement(left.Svg, new SKPoint(0f, 0f)),
            new SvgViewerPlacement(right.Svg, new SKPoint(200f, 0f))
        };

        var window = new Window { Width = 400, Height = 400, Background = Brushes.White, Content = canvas };

        window.Show();
        canvas.Show(placed);
        canvas.Measure(new Size(400, 400));
        canvas.Arrange(new Rect(0, 0, 400, 400));
        Dispatcher.UIThread.RunJobs();

        // Over both drawings at once.
        Assert.Equal("one two | one two", Caught(canvas, new SKRect(0f, 0f, 300f, 100f)));

        // And over only the second, which sits a hundred units past the first.
        Assert.Equal("one two", Caught(canvas, new SKRect(150f, 0f, 300f, 100f)));

        var alone = canvas.Enclosed(new SKRect(150f, 0f, 300f, 100f));

        Assert.Same(placed[1], Assert.Single(alone).Placement);

        window.Close();
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
    /// nothing, so every press reaches the sweep — and without this there would be no way to select
    /// a first element and nothing would ever get handles.
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

    /// <summary>
    /// Something to carry answers before the sweep, and the sweep answers everywhere else.
    /// </summary>
    /// <remarks>
    /// The two are offered together on a board of drawings: a left drag over one of them carries
    /// it, and the same drag on bare board draws a rectangle. Carrying is the narrower thing to
    /// have meant, because a grip answers only where a drawing is.
    /// </remarks>
    [AvaloniaFact]
    public void Something_To_Carry_Answers_Before_The_Sweep()
    {
        var (window, canvas, document) = Host();
        var swept = Swept(canvas);
        var carried = 0;

        // A grip over the left half of the drawing, and nothing over the right.
        canvas.Grip = at => at.X < 50f ? ("held", new SKRect(0f, 0f, 50f, 100f)) : null;
        canvas.Moved += (_, _) => carried++;

        Drag(window, canvas, (10f, 10f), (40f, 40f));

        Assert.Equal(1, carried);
        Assert.Empty(swept);

        // And past it, where there is nothing to take hold of.
        Drag(window, canvas, (60f, 10f), (90f, 40f));

        Assert.Equal(1, carried);
        Assert.Single(swept);

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
