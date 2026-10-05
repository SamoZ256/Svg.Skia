// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Svg.Expressions;
using Svg.Skia;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// What a drawing has but does not paint — the boxes it reserves for its host (<c>e:bounds</c>), the
/// content of its masks and clips, unpainted shapes and hidden elements — found by the dashed edge
/// the canvas draws for each. Boxes are also named in the Attributes pane.
/// </summary>
public class SvgViewerInvisibleTests
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

        viewer.ShowsInvisible = false;

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

    private static string Drawing30(string content)
        => $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 30 30" width="30" height="30">
              {content}
            </svg>
            """;

    [AvaloniaTheory]
    // An unpainted shape needs no e:bounds to be found.
    [InlineData("""<rect id="it" x="2" y="6" width="26" height="18" fill="none" />""", 2d, 15d, "it")]
    [InlineData("""<rect id="it" x="4" y="4" width="22" height="22" fill="#3366cc" display="none" />""", 4d, 15d, "it")]
    [InlineData("""<rect id="it" x="4" y="4" width="22" height="22" fill="#3366cc" visibility="hidden" />""", 4d, 15d, "it")]
    // A hidden group is outlined as one, round its children, and picks the group.
    [InlineData("""<g id="it" display="none"><rect x="4" y="4" width="22" height="22" fill="#3366cc" /></g>""", 4d, 15d, "it")]
    [InlineData("""<g id="it" visibility="hidden"><rect x="4" y="4" width="22" height="22" fill="#3366cc" /></g>""", 4d, 15d, "it")]
    // Mask and clip content, where it stands over what it masks or clips.
    [InlineData("""
        <defs><mask id="m"><circle id="it" cx="15" cy="15" r="12" fill="#ffffff" /></mask></defs>
        <rect x="10" y="10" width="10" height="10" fill="#3366cc" mask="url(#m)" />
        """, 3d, 15d, "it")]
    [InlineData("""
        <defs><clipPath id="c"><rect id="it" x="2" y="2" width="26" height="26" /></clipPath></defs>
        <circle cx="15" cy="15" r="6" fill="#3366cc" clip-path="url(#c)" />
        """, 2d, 15d, "it")]
    [InlineData("""
        <defs><clipPath id="c" clipPathUnits="objectBoundingBox"><rect id="it" x="-0.5" y="-0.5" width="2" height="2" /></clipPath></defs>
        <circle cx="15" cy="15" r="5" fill="#3366cc" clip-path="url(#c)" />
        """, 5d, 15d, "it")]
    public async Task What_Paints_Nothing_Is_Picked_By_Its_Edge(string content, double x, double y, string id)
    {
        var (window, viewer) = await Host(Drawing30(content));

        Click(window, viewer, x, y);

        Assert.Equal(id, viewer.SelectedElement?.ID);
        Assert.False(viewer.IsPageSelected);

        // A fresh viewer, since a click inside what is selected keeps it.
        (window, viewer) = await Host(Drawing30(content));
        viewer.ShowsInvisible = false;
        Click(window, viewer, x, y);

        Assert.True(viewer.IsPageSelected);
    }

    /// <summary>On the line it draws, not on the box round it: a circle is found on its rim.</summary>
    [AvaloniaFact]
    public async Task An_Unpainted_Circle_Is_Picked_On_Its_Rim_And_Not_Its_Corner()
    {
        var (window, viewer) = await Host(Drawing30("""<circle id="ring" cx="15" cy="15" r="10" fill="none" />"""));

        Click(window, viewer, 5d, 15d);

        Assert.Equal("ring", viewer.SelectedElement?.ID);

        (window, viewer) = await Host(Drawing30("""<circle id="ring" cx="15" cy="15" r="10" fill="none" />"""));
        Click(window, viewer, 6d, 6d);

        Assert.True(viewer.IsPageSelected);
    }

    /// <summary>Mask content stands in the space of what it masks, wherever that has been moved.</summary>
    [AvaloniaFact]
    public async Task Mask_Content_Is_Outlined_Where_The_Masked_Element_Stands()
    {
        var (window, viewer) = await Host(Drawing30("""
            <defs><mask id="m"><circle id="spot" cx="15" cy="15" r="12" fill="#ffffff" /></mask></defs>
            <g transform="translate(2 0)"><rect x="10" y="10" width="10" height="10" fill="#3366cc" mask="url(#m)" /></g>
            """));

        Click(window, viewer, 3d, 15d);

        Assert.True(viewer.IsPageSelected);

        Click(window, viewer, 5d, 15d);

        Assert.Equal("spot", viewer.SelectedElement?.ID);

        // Ringed there too.
        var ring = viewer.Canvas.Highlight;
        Assert.NotNull(ring);
        Assert.Equal(5f, ring!.Bounds.Left, 0);
        Assert.Equal(29f, ring.Bounds.Right, 0);
    }

    [AvaloniaFact]
    public async Task Clip_Content_Is_Ringed_Where_It_Clips()
    {
        var (window, viewer) = await Host(Drawing30("""
            <defs><clipPath id="c" clipPathUnits="objectBoundingBox"><rect id="hole" x="-0.5" y="-0.5" width="2" height="2" /></clipPath></defs>
            <circle cx="15" cy="15" r="5" fill="#3366cc" clip-path="url(#c)" />
            """));

        Click(window, viewer, 5d, 15d);

        Assert.Equal("hole", viewer.SelectedElement?.ID);

        var ring = viewer.Canvas.Highlight;
        Assert.NotNull(ring);
        Assert.Equal(5f, ring!.Bounds.Left, 0);
        Assert.Equal(25f, ring.Bounds.Right, 0);
    }

    /// <summary>
    /// A bound value moves the masked element without recompiling anything, and the outline goes with it.
    /// </summary>
    [AvaloniaFact]
    public void Mask_Content_Follows_A_Bound_Value()
    {
        var svg = new SKSvg();
        Assert.NotNull(svg.FromSvg("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 100 100" width="100" height="100">
              <defs>
                <e:code><e:param name="dx" type="number" default="0" /></e:code>
                <mask id="m"><circle id="spot" cx="50" cy="50" r="20" fill="#ffffff" /></mask>
              </defs>
              <g transform="translate({{ dx }} 0)"><rect x="40" y="40" width="20" height="20" fill="#3366cc" mask="url(#m)" /></g>
            </svg>
            """));

        var canvas = new SvgViewerCanvas();

        Assert.Equal("spot", canvas.InvisibleAt(svg, new SkiaSharp.SKPoint(30f, 50f))?.ID);

        svg.SetExpressionValues(new Dictionary<string, ExprValue>(StringComparer.Ordinal) { ["dx"] = ExprValue.Number(20f) });

        Assert.Null(canvas.InvisibleAt(svg, new SkiaSharp.SKPoint(30f, 50f)));
        Assert.Equal("spot", canvas.InvisibleAt(svg, new SkiaSharp.SKPoint(50f, 50f))?.ID);

        var spot = svg.SourceDocument!.Descendants().Single(element => element.ID == "spot");
        using var ring = SvgViewerOutline.Of(svg, spot);

        Assert.NotNull(ring);
        Assert.Equal(50f, ring!.Bounds.Left, 1);
    }

    private static string Drawing100(string content)
        => $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
              {content}
            </svg>
            """;

    private static SKSvg Load(string markup)
    {
        var svg = new SKSvg();
        Assert.NotNull(svg.FromSvg(markup));

        return svg;
    }

    private static SvgElement ById(SKSvg svg, string id)
        => svg.SourceDocument!.Descendants().Single(element => element.ID == id);

    [AvaloniaTheory]
    // A line has no fill at all, rather than none, and still paints nothing without a stroke.
    [InlineData("""<line id="it" x1="10" y1="50" x2="90" y2="50" />""", 50f, 50f, "it")]
    // A label written in no paint.
    [InlineData("""<text id="it" x="10" y="60" font-size="30" fill="none">HELLO</text>""", 10f, 50f, "it")]
    // Content that cuts inside what it masks or clips is found on its line, ahead of that element's ink.
    [InlineData("""
        <defs><mask id="m"><circle id="it" cx="50" cy="50" r="20" fill="#ffffff" /></mask></defs>
        <rect x="10" y="10" width="80" height="80" fill="#3366cc" mask="url(#m)" />
        """, 30f, 50f, "it")]
    [InlineData("""
        <defs><clipPath id="c"><circle id="it" cx="50" cy="50" r="20" /></clipPath></defs>
        <rect x="10" y="10" width="80" height="80" fill="#3366cc" clip-path="url(#c)" />
        """, 30f, 50f, "it")]
    // A clip used by mask content clips where that content stands.
    [InlineData("""
        <defs>
          <clipPath id="c"><rect id="it" x="0" y="0" width="60" height="100" /></clipPath>
          <mask id="m"><rect x="40" y="40" width="40" height="40" fill="#ffffff" clip-path="url(#c)" /></mask>
        </defs>
        <rect width="100" height="100" fill="#3366cc" mask="url(#m)" />
        """, 60f, 20f, "it")]
    public void An_Edge_Picks_What_Paints_Nothing_There(string content, float x, float y, string id)
    {
        var svg = Load(Drawing100(content));

        Assert.Equal(id, new SvgViewerCanvas().ElementAt(svg, new SkiaSharp.SKPoint(x, y), out _)?.ID);
    }

    /// <summary>A child made visible again is not inside its hidden group's outline.</summary>
    [AvaloniaFact]
    public void A_Hidden_Group_Is_Outlined_Round_What_Stays_Hidden()
    {
        var svg = Load(Drawing100("""
            <g id="group" visibility="hidden">
              <rect x="10" y="10" width="20" height="20" fill="#3366cc" />
              <rect id="shown" x="60" y="60" width="30" height="30" fill="#cc3355" visibility="visible" />
            </g>
            """));

        var canvas = new SvgViewerCanvas();

        Assert.Equal("group", canvas.InvisibleAt(svg, new SkiaSharp.SKPoint(10f, 20f))?.ID);
        Assert.Null(canvas.InvisibleAt(svg, new SkiaSharp.SKPoint(91f, 75f)));
    }

    /// <summary>
    /// A copy a &lt;use&gt; drew of clip content does not take the ring or the handles away from where the
    /// content clips.
    /// </summary>
    [AvaloniaFact]
    public async Task Clip_Content_Drawn_Again_By_A_Use_Is_Still_Held_Where_It_Clips()
    {
        var (window, viewer) = await Host("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 30 30" width="30" height="30">
              <defs><clipPath id="c"><rect id="hole" x="2" y="2" width="10" height="10" /></clipPath></defs>
              <rect x="0" y="0" width="20" height="20" fill="#3366cc" clip-path="url(#c)" />
              <use xlink:href="#hole" x="15" y="15" fill="#22aa66" />
            </svg>
            """);

        Click(window, viewer, 2d, 7d);

        Assert.Equal("hole", viewer.SelectedElement?.ID);
        Assert.Equal(2f, viewer.Canvas.Gizmo!.Value.TL.X, 1);

        var ring = viewer.Canvas.Highlight;
        Assert.NotNull(ring);
        Assert.Equal(2f, ring!.Bounds.Left, 0);
    }

    /// <summary>A shape nested in a group inside a mask is ringed where the mask puts it.</summary>
    [AvaloniaFact]
    public void Mask_Content_Nested_In_A_Group_Is_Ringed()
    {
        var svg = Load(Drawing100("""
            <defs><mask id="m"><g transform="translate(30 40)"><rect id="deep" width="20" height="10" fill="#ffffff" /></g></mask></defs>
            <rect width="100" height="100" fill="#3366cc" mask="url(#m)" />
            """));

        using var ring = SvgViewerOutline.Of(svg, ById(svg, "deep"));

        Assert.NotNull(ring);
        Assert.Equal((30f, 40f, 50f, 50f), (ring!.Bounds.Left, ring.Bounds.Top, ring.Bounds.Right, ring.Bounds.Bottom));
    }

    /// <summary>With something invisible selected, another outline inside its box is still picked.</summary>
    [AvaloniaFact]
    public async Task An_Outline_Inside_A_Selected_One_Is_Still_Picked()
    {
        var (window, viewer) = await Host(Drawing30("""
            <rect id="outer" x="2" y="2" width="26" height="26" fill="none" />
            <rect id="inner" x="8" y="8" width="14" height="14" fill="none" />
            """));

        Click(window, viewer, 2d, 15d);

        Assert.Equal("outer", viewer.SelectedElement?.ID);

        Click(window, viewer, 8d, 15d);

        Assert.Equal("inner", viewer.SelectedElement?.ID);
    }
}
