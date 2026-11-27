// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;
using Svg.Editor.Skia;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>Guides: the ones pulled out of the rulers, and the smart ones every other shape offers a drag.</summary>
/// <remarks>
/// The box is 20 wide at 20,20 and the mark 6 wide at 63,70, so the lines its left edge, centre and
/// right edge offer the box's corner are all apart from the page's 0, 50 and 100.
/// </remarks>
public class SvgViewerGuidesTests
{
    private const string Marked = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <rect id="box" x="20" y="20" width="20" height="20" fill="#3366cc" />
          <rect id="mark" x="63" y="70" width="6" height="10" fill="#cc3366" />
        </svg>
        """;

    private const string Alone = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
          <rect id="box" x="20" y="20" width="20" height="20" fill="#3366cc" />
        </svg>
        """;

    private const RawInputModifiers Held = RawInputModifiers.LeftMouseButton;

    private static async Task<(Window Window, SvgViewer Viewer)> Host(string drawing, bool smart = true)
    {
        var viewer = new SvgViewer { SnapsToSmartGuides = smart };
        var window = new Window { Width = 800, Height = 800, Background = Brushes.White, Content = viewer };

        window.Show();

        Assert.True(await viewer.LoadTextAsync(drawing));

        Dispatcher.UIThread.RunJobs();

        return (window, viewer);
    }

    private static Point At(Window window, SvgViewer viewer, float x, float y)
    {
        Assert.True(viewer.Canvas.TryGetControlPoint(new SKPoint(x, y), out var point));

        return viewer.Canvas.TranslatePoint(point, window) ?? throw new InvalidOperationException("The canvas is not in the window.");
    }

    private static void Click(Window window, SvgViewer viewer, float x, float y)
    {
        window.MouseDown(At(window, viewer, x, y), MouseButton.Left);
        window.MouseUp(At(window, viewer, x, y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Drag(Window window, SvgViewer viewer, (float X, float Y) from, (float X, float Y) to)
    {
        window.MouseDown(At(window, viewer, from.X, from.Y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(At(window, viewer, to.X, to.Y), Held);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(At(window, viewer, to.X, to.Y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The x the file now writes for the box.</summary>
    private static float X(SvgViewer viewer, string id = "box") => Number(viewer, id, "x");

    private static float Number(SvgViewer viewer, string id, string name)
        => Number(viewer.Source[viewer.Source.IndexOf($"id=\"{id}\"", StringComparison.Ordinal)..], name);

    /// <summary>The first <paramref name="name"/> written in <paramref name="text"/>.</summary>
    private static float Number(string text, string name)
    {
        var at = text.IndexOf($" {name}=\"", StringComparison.Ordinal) + name.Length + 3;

        return float.Parse(text[at..text.IndexOf('"', at)], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SvgEditorGuide Upright(float x) => new("guide", new SKPoint(x, 0f), new SKPoint(0f, 1f));

    // ---- the grid's pull --------------------------------------------------------------------

    [Fact]
    public void A_Guide_Pulls_With_No_Grid()
    {
        var grid = SvgViewerGrid.None with { Uprights = SvgViewerGrid.Lines(new[] { 13f }, upright: true), Reach = 2f };

        Assert.True(grid.IsOn);
        Assert.Equal(13f, grid.PullX(12f));
        Assert.Equal(12f, grid.PullY(12f));
    }

    [Fact]
    public void A_Guide_Out_Of_Reach_Leaves_The_Value()
    {
        var grid = SvgViewerGrid.None with { Uprights = SvgViewerGrid.Lines(new[] { 13f }, upright: true), Reach = 2f };

        Assert.Equal(10f, grid.PullX(10f));
    }

    [Theory]
    [InlineData(12f, 13f)]
    [InlineData(11f, 10f)]
    public void The_Nearer_Of_A_Grid_Line_And_A_Guide_Wins(float value, float landed)
    {
        var grid = new SvgViewerGrid(10f, 15f) { Pull = 4f, Uprights = SvgViewerGrid.Lines(new[] { 13f }, upright: true), Reach = 4f };

        Assert.Equal(landed, grid.PullX(value));
    }

    [Fact]
    public void A_Tie_Goes_To_The_Guide()
    {
        var grid = new SvgViewerGrid(10f, 15f) { Pull = 4f, Uprights = SvgViewerGrid.Lines(new[] { 12f }, upright: true), Reach = 4f };

        Assert.Equal(12f, grid.PullX(11f));
    }

    [Fact]
    public void A_Value_That_Is_No_Number_Is_Left_Alone()
    {
        var grid = SvgViewerGrid.None with { Uprights = SvgViewerGrid.Lines(new[] { 13f }, upright: true), Reach = 2f };

        Assert.True(float.IsNaN(grid.PullX(float.NaN)));
    }

    [Fact]
    public void A_Guide_Stays_Where_It_Is_On_The_Board()
    {
        var grid = SvgViewerGrid.None with { Uprights = SvgViewerGrid.Lines(new[] { 13f }, upright: true), Reach = 2f };

        // Inside a drawing placed at 5, the board's 13 is the drawing's 8.
        Assert.Equal(8f, grid.From(5f, 0f).PullX(7.5f));
    }

    [Fact]
    public void A_Level_Guide_Pulls_Y_Only()
    {
        var grid = SvgViewerGrid.None with { Levels = SvgViewerGrid.Lines(new[] { 13f }, upright: false), Reach = 2f };

        Assert.Equal(13f, grid.PullY(12f));
        Assert.Equal(12f, grid.PullX(12f));
    }

    // ---- smart guides -----------------------------------------------------------------------

    /// <summary>The box's left edge, centre or right edge lands on the mark's, whichever came near.</summary>
    /// <remarks>
    /// The centre and right edge only land because the gizmo's corner is offered the line at the box's
    /// half and whole width; the page's centre at 50 is a line the corner alone would have gone to.
    /// </remarks>
    [AvaloniaTheory]
    [InlineData(42.4f, 63f)]
    [InlineData(35.4f, 56f)]
    [InlineData(28.6f, 49f)]
    public async Task A_Move_Lands_An_Edge_Or_The_Centre_On_Another_Shapes(float by, float x)
    {
        var (window, viewer) = await Host(Marked);

        Click(window, viewer, 30f, 30f);
        Drag(window, viewer, (30f, 30f), (30f + by, 30f));

        Assert.Equal(x, X(viewer), 2);
        Assert.Equal(20f, Number(viewer, "box", "y"), 2);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Two_Tops_Line_Up()
    {
        var (window, viewer) = await Host(Marked);

        Click(window, viewer, 30f, 30f);
        Drag(window, viewer, (30f, 30f), (30f, 79.4f));

        Assert.Equal(70f, Number(viewer, "box", "y"), 2);

        window.Close();
    }

    /// <summary>A handle lands the edge it drags on the mark's, and never on a line offered only to a carried box.</summary>
    /// <remarks>56 is the mark's centre less half the box, a line for the box's corner and nothing to its edge.</remarks>
    [AvaloniaTheory]
    [InlineData(62.4f, 43f)]
    [InlineData(56.4f, 36.4f)]
    public async Task A_Handle_Lands_The_Edge_It_Drags(float to, float width)
    {
        var (window, viewer) = await Host(Marked);

        Click(window, viewer, 30f, 30f);
        Drag(window, viewer, (40f, 30f), (to, 30f));

        Assert.Equal(width, Number(viewer, "box", "width"), 1);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Turned_Shape_Lands_Its_Box()
    {
        const string turned = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
              <rect id="box" x="20" y="20" width="20" height="20" transform="rotate(90 30 30)" fill="#3366cc" />
              <rect id="mark" x="63" y="70" width="6" height="10" fill="#cc3366" />
            </svg>
            """;

        var (window, viewer) = await Host(turned);

        Click(window, viewer, 30f, 30f);
        Drag(window, viewer, (30f, 30f), (72.4f, 30f));

        // Turned a quarter, the shape's own corner is the box's top right, so the box's left edge
        // landing on 63 is a corner at 83.
        Assert.True(viewer.Canvas.Gizmo is { } box && Math.Abs(SelectionService.GetBoundsRect(box).Left - 63f) < 0.01f, "the box's left edge is not on the mark's");

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Shape_Is_Not_Pulled_Back_To_Where_It_Started()
    {
        var (window, viewer) = await Host(Alone);

        // Six pixels: past the slack a click is allowed, and within reach of the edge it started on.
        var by = (float)(6d / viewer.Canvas.Scale);

        Click(window, viewer, 30f, 30f);
        Drag(window, viewer, (30f, 30f), (30f + by, 30f));

        Assert.Equal(20f + by, X(viewer), 1);

        window.Close();
    }

    /// <summary>A &lt;use&gt;'s copy is under the use in the scene, not under the shape it copies, and it moves with the use.</summary>
    [AvaloniaFact]
    public async Task A_Use_Is_Not_Pulled_Back_To_Its_Own_Copy()
    {
        const string copied = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
              <rect id="shape" x="10" y="10" width="20" height="20" fill="#3366cc" />
              <use id="copy" href="#shape" x="50" />
            </svg>
            """;

        var (window, viewer) = await Host(copied);
        var by = (float)(6d / viewer.Canvas.Scale);

        Click(window, viewer, 70f, 20f);
        Drag(window, viewer, (70f, 20f), (70f + by, 20f));

        Assert.Equal(60f + by, SelectionService.GetBoundsRect(viewer.Canvas.Gizmo!.Value).Left, 1);

        window.Close();
    }

    /// <summary>A page edge dragged a little is not pulled back to where it was, which its own page would be.</summary>
    [AvaloniaFact]
    public async Task A_Page_Edge_Is_Not_Pulled_Back_To_Itself()
    {
        var (window, viewer) = await Host(Alone);
        var by = (float)(6d / viewer.Canvas.Scale);

        Click(window, viewer, 5f, 5f);
        Assert.True(viewer.IsPageSelected);

        Drag(window, viewer, (99f, 50f), (99f + by, 50f));

        Assert.Equal(100f + by, Number(viewer.Source, "width"), 1);

        window.Close();
    }

    /// <summary>A drawing tool moves nothing, so the shape left selected is something to draw against.</summary>
    [AvaloniaFact]
    public async Task A_Drawing_Tool_Lands_On_The_Shape_Left_Selected()
    {
        var (window, viewer) = await Host(Alone);

        Click(window, viewer, 30f, 30f);
        viewer.Drafting.Arm("rect");

        Drag(window, viewer, (40.6f, 60f), (60f, 80f));

        Assert.Equal(40f, Number(viewer.Source[viewer.Source.LastIndexOf("<rect", StringComparison.Ordinal)..], "x"), 2);

        window.Close();
    }

    /// <summary>A click is cancelled as a press before it is a click, so what the press gathered has to outlast it.</summary>
    [AvaloniaFact]
    public async Task A_Point_Put_Down_By_A_Click_Lands_On_Another_Shape()
    {
        var (window, viewer) = await Host(Alone);

        viewer.Drafting.Arm("polyline");

        Click(window, viewer, 12f, 62f);
        Click(window, viewer, 40.6f, 75f);
        Click(window, viewer, 40.6f, 75f);

        var points = viewer.Source[(viewer.Source.IndexOf("points=\"", StringComparison.Ordinal) + 8)..];
        var second = points[..points.IndexOf('"', StringComparison.Ordinal)].Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)[2];

        Assert.Equal(40f, float.Parse(second, System.Globalization.CultureInfo.InvariantCulture), 2);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Text_Placed_By_A_Click_Lands_On_Another_Shape()
    {
        var (window, viewer) = await Host(Alone);

        viewer.Drafting.Arm("text");

        Click(window, viewer, 40.6f, 75f);

        Assert.Equal(40f, Number(viewer.Source[viewer.Source.LastIndexOf("<text", StringComparison.Ordinal)..], "x"), 2);

        // Written, the tool is put away, and what the press gathered goes with it.
        Assert.Null(viewer.Canvas.Grid.Uprights);

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Page_Edge_Pulls()
    {
        var (window, viewer) = await Host(Alone);

        Click(window, viewer, 30f, 30f);
        Drag(window, viewer, (30f, 30f), (10.6f, 30f));

        Assert.Equal(0f, X(viewer), 2);

        window.Close();
    }

    [AvaloniaFact]
    public async Task With_Smart_Guides_Off_Nothing_Pulls()
    {
        var (window, viewer) = await Host(Marked, smart: false);

        Click(window, viewer, 30f, 30f);
        Drag(window, viewer, (30f, 30f), (65.4f, 30f));

        Assert.Equal(55.4f, X(viewer), 1);

        window.Close();
    }

    [AvaloniaFact]
    public async Task What_Is_Not_Drawn_Is_No_Line()
    {
        const string hidden = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
              <defs><rect id="kept" x="11" y="11" width="2" height="2" /></defs>
              <clipPath id="clip"><rect x="17" y="17" width="2" height="2" /></clipPath>
              <mask id="mask"><rect x="35" y="35" width="2" height="2" fill="white" /></mask>
              <pattern id="dots" width="4" height="4" patternUnits="userSpaceOnUse"><rect x="41" y="41" width="2" height="2" /></pattern>
              <rect x="23" y="23" width="2" height="2" display="none" />
              <rect x="29" y="29" width="2" height="2" visibility="hidden" />
              <g display="none"><rect x="47" y="47" width="2" height="2" /></g>
              <g requiredExtensions="http://example.com/nothing"><rect x="59" y="59" width="2" height="2" /></g>
              <rect x="63" y="70" width="6" height="10" clip-path="url(#clip)" />
              <rect x="74" y="70" width="6" height="10" mask="url(#mask)" fill="url(#dots)" />
            </svg>
            """;

        var (window, viewer) = await Host(hidden);
        var guides = new SvgViewerGuides { IsOn = true };

        guides.Begin(viewer.Canvas.Placements, Array.Empty<SvgElement>(), null, null);

        var lines = guides.Over(SvgViewerGrid.None, viewer.Canvas).Uprights!.Guides.Select(guide => guide.Origin.X).ToList();

        Assert.Contains(63f, lines);
        Assert.DoesNotContain(11f, lines);
        Assert.DoesNotContain(17f, lines);
        Assert.DoesNotContain(23f, lines);
        Assert.DoesNotContain(29f, lines);
        Assert.DoesNotContain(35f, lines);
        Assert.DoesNotContain(41f, lines);
        Assert.DoesNotContain(47f, lines);
        Assert.DoesNotContain(59f, lines);

        window.Close();
    }

    [AvaloniaFact]
    public async Task What_Lines_Up_Is_Drawn_While_The_Drag_Lasts()
    {
        var (window, viewer) = await Host(Marked);

        Click(window, viewer, 30f, 30f);

        window.MouseDown(At(window, viewer, 30f, 30f), MouseButton.Left);
        window.MouseMove(At(window, viewer, 65.4f, 30f), Held);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(viewer.Canvas.Aligned ?? Array.Empty<SvgEditorGuide>(), guide => guide.Origin.X == 66f);

        window.MouseUp(At(window, viewer, 65.4f, 30f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewer.Canvas.Aligned);

        window.Close();
    }

    // ---- ruler guides -----------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(true, 56f)]
    [InlineData(false, 55.4f)]
    public async Task A_Move_Lands_On_A_Guide_Only_While_Guides_Show(bool shown, float x)
    {
        var (window, viewer) = await Host(Alone, smart: false);

        viewer.Canvas.Guides = new[] { Upright(56f) };
        viewer.Canvas.ShowsGuides = shown;

        Click(window, viewer, 30f, 30f);
        Drag(window, viewer, (30f, 30f), (65.4f, 30f));

        Assert.Equal(x, X(viewer), 1);

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Grid_A_Gesture_Is_Handed_Has_The_Guides_As_They_Now_Are()
    {
        var (window, viewer) = await Host(Alone, smart: false);

        viewer.Canvas.Guides = new[] { Upright(56f) };

        Assert.Equal(56f, Assert.Single(viewer.Canvas.Grid.Uprights!.Guides).Origin.X);

        viewer.Canvas.ShowsGuides = false;

        Assert.Null(viewer.Canvas.Grid.Uprights);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Guide_Is_Not_Pulled_Back_To_Where_It_Was()
    {
        var (window, viewer) = await Host(Alone, smart: false);

        viewer.Canvas.Guides = new[] { Upright(70f) };

        var by = (float)(6d / viewer.Canvas.Scale);

        Drag(window, viewer, (70f, 80f), (70f + by, 80f));

        Assert.Equal(70f + by, Assert.Single(viewer.Canvas.Guides).Origin.X, 2);

        window.Close();
    }

    /// <summary>A click on a shape lying under a guide picks the shape; only a drag takes the guide.</summary>
    [AvaloniaFact]
    public async Task A_Click_On_A_Shape_Under_A_Guide_Picks_The_Shape()
    {
        var (window, viewer) = await Host(Alone);

        viewer.Canvas.Guides = new[] { Upright(30f) };

        Click(window, viewer, 30f, 30f);

        Assert.NotNull(viewer.SelectedElement);
        Assert.Equal(30f, Assert.Single(viewer.Canvas.Guides).Origin.X);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Guide_Holds_Still_Within_A_Clicks_Slack()
    {
        var (window, viewer) = await Host(Alone);

        viewer.Canvas.Guides = new[] { Upright(70f) };

        var jitter = (float)(2d / viewer.Canvas.Scale);

        window.MouseDown(At(window, viewer, 70f, 80f), MouseButton.Left);
        window.MouseMove(At(window, viewer, 70f + jitter, 80f), Held);
        window.MouseUp(At(window, viewer, 70f + jitter, 80f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(70f, Assert.Single(viewer.Canvas.Guides).Origin.X);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Escape_Puts_A_Guide_Back()
    {
        var (window, viewer) = await Host(Alone);

        viewer.Canvas.Guides = new[] { Upright(70f) };

        window.MouseDown(At(window, viewer, 70f, 80f), MouseButton.Left);
        window.MouseMove(At(window, viewer, 85f, 80f), Held);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.MouseUp(At(window, viewer, 85f, 80f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(70f, Assert.Single(viewer.Canvas.Guides).Origin.X);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Guide_Taken_From_The_Hand_Goes_Back()
    {
        var (window, viewer) = await Host(Alone);
        IPointer? held = null;

        viewer.Canvas.Guides = new[] { Upright(70f) };
        viewer.Canvas.AddHandler(InputElement.PointerPressedEvent, (_, e) => held = e.Pointer, RoutingStrategies.Tunnel, handledEventsToo: true);

        window.MouseDown(At(window, viewer, 70f, 80f), MouseButton.Left);
        window.MouseMove(At(window, viewer, 85f, 80f), Held);
        held!.Capture(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(70f, Assert.Single(viewer.Canvas.Guides).Origin.X);

        window.MouseUp(At(window, viewer, 85f, 80f), MouseButton.Left);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Guide_Lands_On_The_Grid()
    {
        var (window, viewer) = await Host(Alone, smart: false);

        viewer.Grid = new SvgViewerGrid(10f, 15f);
        viewer.SnapsToGrid = true;
        viewer.Canvas.Guides = new[] { Upright(70f) };

        // Half the grid's pull short of the line at 80.
        Drag(window, viewer, (70f, 80f), (80f - viewer.Canvas.Grid.Pull / 2f, 80f));

        Assert.Equal(80f, Assert.Single(viewer.Canvas.Guides).Origin.X, 2);

        window.Close();
    }

    /// <summary>A guide stays at the number the ruler gave it when the drawing's picture moves under its units.</summary>
    [AvaloniaFact]
    public async Task A_Guide_Stays_On_The_Artwork_When_The_Page_Grows_Leftwards()
    {
        var (window, viewer) = await Host(Alone);

        viewer.Canvas.Guides = new[] { Upright(20f) };

        Assert.True(await viewer.LoadTextAsync("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="-10 0 110 100" width="110" height="100">
              <rect id="box" x="20" y="20" width="20" height="20" fill="#3366cc" />
            </svg>
            """));
        Dispatcher.UIThread.RunJobs();

        // User 20 is now 30 into the picture, which starts at -10.
        Assert.Equal(30f, Assert.Single(viewer.Canvas.Guides).Origin.X, 2);

        window.Close();
    }

    /// <summary>Pressed out of the top ruler, a guide runs level where it is let go, and the file never hears of it.</summary>
    [AvaloniaFact]
    public async Task A_Guide_Is_Pulled_Out_Of_The_Top_Ruler()
    {
        var (window, viewer) = await Host(Alone);
        var written = viewer.Source;

        viewer.Rulers.ShowsRulers = true;
        Dispatcher.UIThread.RunJobs();

        window.MouseDown(Ruler(window, viewer, top: true), MouseButton.Left);
        window.MouseMove(At(window, viewer, 30f, 45f), Held);
        window.MouseUp(At(window, viewer, 30f, 45f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var guide = Assert.Single(viewer.Canvas.Guides);

        Assert.False(SvgViewerGuides.Upright(guide));
        Assert.Equal(45f, guide.Origin.Y, 2);
        Assert.Equal(written, viewer.Source);

        window.Close();
    }

    /// <summary>With the guides hidden, a ruler makes none and a guide there cannot be taken.</summary>
    [AvaloniaFact]
    public async Task Hidden_Guides_Are_Neither_Made_Nor_Moved()
    {
        var (window, viewer) = await Host(Alone);

        viewer.Rulers.ShowsRulers = true;
        viewer.Canvas.Guides = new[] { Upright(70f) };
        viewer.Canvas.ShowsGuides = false;
        Dispatcher.UIThread.RunJobs();

        window.MouseDown(Ruler(window, viewer, top: true), MouseButton.Left);
        window.MouseMove(At(window, viewer, 30f, 45f), Held);
        window.MouseUp(At(window, viewer, 30f, 45f), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (70f, 80f), (85f, 80f));

        Assert.Equal(70f, Assert.Single(viewer.Canvas.Guides).Origin.X);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Guide_Let_Go_Off_The_Canvas_Is_Not_Made()
    {
        var (window, viewer) = await Host(Alone);

        viewer.Rulers.ShowsRulers = true;
        Dispatcher.UIThread.RunJobs();

        var band = Ruler(window, viewer, top: false);

        window.MouseDown(band, MouseButton.Left);
        window.MouseMove(band + new Point(0d, 20d), Held);
        window.MouseUp(band + new Point(0d, 20d), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(viewer.Canvas.Guides);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Guide_Is_Moved_And_Put_Away_On_A_Ruler()
    {
        var (window, viewer) = await Host(Alone);

        viewer.Rulers.ShowsRulers = true;
        viewer.Canvas.Guides = new[] { Upright(70f) };
        Dispatcher.UIThread.RunJobs();

        Drag(window, viewer, (70f, 80f), (85f, 80f));

        Assert.Equal(85f, Assert.Single(viewer.Canvas.Guides).Origin.X, 2);

        window.MouseDown(At(window, viewer, 85f, 80f), MouseButton.Left);
        window.MouseMove(Ruler(window, viewer, top: false), Held);
        window.MouseUp(Ruler(window, viewer, top: false), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(viewer.Canvas.Guides);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Drawing_Tool_Draws_Over_A_Guide_Rather_Than_Take_It()
    {
        var (window, viewer) = await Host(Alone);

        viewer.Canvas.Guides = new[] { Upright(70f) };
        viewer.Drafting.Arm("rect");

        Drag(window, viewer, (70f, 60f), (90f, 80f));

        Assert.Equal(70f, Assert.Single(viewer.Canvas.Guides).Origin.X, 2);
        Assert.Equal(2, viewer.Source.Split("<rect", StringSplitOptions.None).Length - 1);

        window.Close();
    }

    /// <summary>Somewhere on a ruler's band, away from the corner they share, in window coordinates.</summary>
    private static Point Ruler(Window window, SvgViewer viewer, bool top)
        => viewer.Rulers.TranslatePoint(
               top ? new Point(SvgViewerRulers.Breadth + 40d, SvgViewerRulers.Breadth / 2d) : new Point(SvgViewerRulers.Breadth / 2d, SvgViewerRulers.Breadth + 40d),
               window)
           ?? throw new InvalidOperationException("The rulers are not in the window.");
}
