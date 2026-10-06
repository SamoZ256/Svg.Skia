using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// A view a drag is held near the edge of scrolls, so a drop can reach what is out of sight.
/// </summary>
/// <remarks>
/// Injected drags, as the tree's and the panel's suites make them, and real time passing: the scroll
/// that matters is the one kept up while the pointer is held still, with no update arriving.
/// </remarks>
public class SvgViewerDragScrollTests
{
    /// <summary>Sixty shapes, so the tree has far more rows than its pane.</summary>
    private static readonly string s_long =
        """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10" width="10" height="10">"""
        + string.Concat(Enumerable.Range(0, 60).Select(i => $"""<rect id="r{i}" width="1" height="1" />"""))
        + "</svg>";

    /// <summary>A clip path and a mask kept in &lt;defs&gt;, and a rect whose Clip path and Mask boxes they are dropped on.</summary>
    private const string Kept = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40" width="40" height="40">
          <defs>
            <clipPath id="window"><rect width="20" height="20" /></clipPath>
            <mask id="veil"><rect width="20" height="20" fill="white" /></mask>
          </defs>
          <rect id="a" width="30" height="30" fill="#3366cc" />
        </svg>
        """;

    /// <summary>Forty parameters, so the declarations have far more rows than their pane.</summary>
    private static readonly string s_declared =
        """<svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 10 10" width="10" height="10"><defs><e:code>"""
        + string.Concat(Enumerable.Range(0, 40).Select(i => $"""<e:param name="p{i}" type="number" default="{i}" />"""))
        + """</e:code></defs><rect width="10" height="10" /></svg>""";

    private const DragDropEffects Offered = DragDropEffects.Move | DragDropEffects.Link;

    private static async Task<(Window Window, SvgViewer Viewer)> Host(string markup, double height = 1000d)
    {
        var viewer = new SvgViewer();
        var window = new Window { Width = 700, Height = height, Background = Brushes.White, Content = viewer };

        window.Show();

        Assert.True(await viewer.LoadTextAsync(markup));
        Dispatcher.UIThread.RunJobs();

        return (window, viewer);
    }

    /// <summary>The tree's own scroller, which is the one its rows move in.</summary>
    private static ScrollViewer Scroller(SvgViewerElementTree tree)
        => tree.FindControl<TreeView>("Tree")!.GetVisualDescendants().OfType<ScrollViewer>().First();

    /// <summary>A point of the window a little in from the left of <paramref name="view"/>, that far down it, or up from its foot where negative.</summary>
    private static Point Along(Window window, Visual view, double down)
        => view.TranslatePoint(new Point(40d, down < 0d ? view.Bounds.Height + down : down), window)!.Value;

    /// <summary>The address of the row under a point of the window, <see cref="Render"/>ed first.</summary>
    private static string? RowAt(Window window, Point at)
    {
        Render();

        return ((window.InputHitTest(at) as Visual)?.FindAncestorOfType<TreeViewItem>(true)?.DataContext as SvgViewerElementNode)?.AddressKey;
    }

    /// <summary>Draws the window: a hit test reads the scene as last drawn, which a headless window draws only when told.</summary>
    /// <remarks>Twice, since a tick draws what the one before had the window hand over; after one, a scrolled list was hit where it had been.</remarks>
    private static void Render()
    {
        for (var tick = 0; tick < 2; tick++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>The rect's attribute box for <paramref name="name"/>, and the list it scrolls in, with the rect picked.</summary>
    private static (TextBox Box, ScrollViewer Attributes) Picked(Window window, SvgViewer viewer, string name)
    {
        Assert.True(viewer.Elements.TrySelect("1"));
        Dispatcher.UIThread.RunJobs();

        var box = window.GetVisualDescendants().OfType<TextBox>().Single(candidate => Equals(candidate.Tag, name));

        return (box, box.GetVisualAncestors().OfType<ScrollViewer>().First());
    }

    /// <summary>The attribute box under a point of the window, <see cref="Render"/>ed first.</summary>
    private static TextBox? BoxAt(Window window, Point at)
    {
        Render();

        return (window.InputHitTest(at) as Visual)?.FindAncestorOfType<TextBox>(true);
    }

    /// <summary>A point of the window down the middle of <paramref name="box"/>, <paramref name="up"/> from the foot of <paramref name="view"/>.</summary>
    private static Point Above(Window window, Visual box, Visual view, double up)
        => new(
            box.TranslatePoint(new Point(box.Bounds.Width / 2d, 0d), window)!.Value.X,
            view.TranslatePoint(new Point(0d, view.Bounds.Height - up), window)!.Value.Y);

    /// <summary>Carries <paramref name="carried"/> to <paramref name="at"/> and holds it there.</summary>
    private static void Hold(Window window, Point at, IDataTransfer carried)
    {
        window.DragDrop(at, RawDragEventType.DragEnter, carried, Offered, RawInputModifiers.None);
        window.DragDrop(at, RawDragEventType.DragOver, carried, Offered, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Let(Window window, Point at, RawDragEventType how, IDataTransfer carried)
    {
        window.DragDrop(at, how, carried, Offered, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Lets time pass with the pointer still, until <paramref name="done"/> or five seconds.</summary>
    private static async Task Until(Func<bool> done)
    {
        for (var waited = 0; waited < 250 && !done(); waited++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Long enough for a scroll to show, so that none showing means none happened.</summary>
    private static async Task Pause()
    {
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task A_Drag_Held_Near_The_Foot_Of_A_Long_Tree_Scrolls_It_Down()
    {
        var (window, viewer) = await Host(s_long);
        var scroller = Scroller(viewer.Elements);
        var at = Along(window, scroller, -4d);
        var carried = SvgViewerElementTree.Carrying("0");

        Assert.True(scroller.ScrollBarMaximum.Y > 200d);

        Hold(window, at, carried);
        await Until(() => scroller.Offset.Y > 48d);

        Assert.True(scroller.Offset.Y > 48d, $"{scroller.Offset.Y}");

        Let(window, at, RawDragEventType.DragLeave, carried);
    }

    [AvaloniaFact]
    public async Task A_Drag_Held_Near_The_Top_Of_A_Scrolled_Tree_Scrolls_It_Up()
    {
        var (window, viewer) = await Host(s_long);
        var scroller = Scroller(viewer.Elements);
        var carried = SvgViewerElementTree.Carrying("0");

        scroller.Offset = new Vector(0d, scroller.ScrollBarMaximum.Y);
        Dispatcher.UIThread.RunJobs();

        var end = scroller.Offset.Y;
        var at = Along(window, scroller, 4d);

        Hold(window, at, carried);
        await Until(() => scroller.Offset.Y < end - 48d);

        Assert.True(scroller.Offset.Y < end - 48d, $"{scroller.Offset.Y} of {end}");

        Let(window, at, RawDragEventType.DragLeave, carried);
    }

    [AvaloniaFact]
    public async Task A_Drag_Held_In_The_Middle_Of_A_Tree_Scrolls_Nothing()
    {
        var (window, viewer) = await Host(s_long);
        var scroller = Scroller(viewer.Elements);
        var at = Along(window, scroller, scroller.Bounds.Height / 2d);
        var carried = SvgViewerElementTree.Carrying("0");

        Hold(window, at, carried);
        await Pause();

        Assert.Equal(0d, scroller.Offset.Y);

        Let(window, at, RawDragEventType.DragLeave, carried);
    }

    [AvaloniaFact]
    public async Task Scrolling_Stops_At_The_End_Of_The_Tree()
    {
        var (window, viewer) = await Host(s_long);
        var scroller = Scroller(viewer.Elements);
        var at = Along(window, scroller, -4d);
        var carried = SvgViewerElementTree.Carrying("0");
        var end = scroller.ScrollBarMaximum.Y;

        scroller.Offset = new Vector(0d, end - 20d);
        Dispatcher.UIThread.RunJobs();

        Hold(window, at, carried);
        await Until(() => scroller.Offset.Y >= end);
        await Pause();

        Assert.Equal(end, scroller.Offset.Y);

        Let(window, at, RawDragEventType.DragLeave, carried);
    }

    [AvaloniaFact]
    public async Task Nothing_Scrolls_Once_The_Drag_Has_Left()
    {
        var (window, viewer) = await Host(s_long);
        var scroller = Scroller(viewer.Elements);
        var at = Along(window, scroller, -4d);
        var carried = SvgViewerElementTree.Carrying("0");

        Hold(window, at, carried);
        await Until(() => scroller.Offset.Y > 24d);

        Let(window, at, RawDragEventType.DragLeave, carried);

        var left = scroller.Offset.Y;

        await Pause();

        Assert.True(left > 24d, $"{left}");
        Assert.Equal(left, scroller.Offset.Y);
    }

    [AvaloniaFact]
    public async Task Nothing_Scrolls_Once_The_Drag_Is_Dropped()
    {
        var (window, viewer) = await Host(s_long);
        var scroller = Scroller(viewer.Elements);
        var at = Along(window, scroller, -4d);
        var carried = SvgViewerElementTree.Carrying("0");

        viewer.Elements.MoveRequested = (_, _, _) => true;

        Hold(window, at, carried);
        await Until(() => scroller.Offset.Y > 24d);

        Let(window, at, RawDragEventType.Drop, carried);

        var dropped = scroller.Offset.Y;

        await Pause();

        Assert.True(dropped > 24d, $"{dropped}");
        Assert.Equal(dropped, scroller.Offset.Y);
    }

    /// <summary>
    /// The line comes down while the rows slide under it, and a drop with no move since lands on the
    /// row under the pointer, not the one the last move found there.
    /// </summary>
    /// <remarks>Run to the end of the tree, where the scrolling stops by itself, so nothing moves between reading the row and the drop.</remarks>
    [AvaloniaFact]
    public async Task A_Drop_After_The_Tree_Scrolled_Lands_On_The_Row_Now_Under_The_Pointer()
    {
        var (window, viewer) = await Host(s_long);
        var scroller = Scroller(viewer.Elements);
        var line = viewer.Elements.FindControl<Border>("DropLine")!;
        var at = Along(window, scroller, -16d);
        var carried = SvgViewerElementTree.Carrying("0");
        var moved = new List<string>();
        var end = scroller.ScrollBarMaximum.Y;

        viewer.Elements.MoveRequested = (_, target, _) =>
        {
            moved.Add(target);

            return true;
        };

        scroller.Offset = new Vector(0d, end - 120d);
        Dispatcher.UIThread.RunJobs();

        var before = RowAt(window, at);

        Assert.NotNull(before);

        // Heard rather than read after the hold, which on a slow run can already have waited out the rest.
        var shown = false;

        line.PropertyChanged += (_, e) => shown |= e.Property == Visual.IsVisibleProperty && line.IsVisible;

        Hold(window, at, carried);

        Assert.True(shown);

        await Until(() => scroller.Offset.Y >= end);

        Assert.False(line.IsVisible);

        var now = RowAt(window, at);

        Assert.NotNull(now);
        Assert.NotEqual(before, now);

        Let(window, at, RawDragEventType.Drop, carried);

        Assert.Equal(new[] { now }, moved);
    }

    /// <summary>
    /// The Clip path box slides up into reach under a still pointer, and a drop with no move since
    /// lands on it rather than on what the last move found there.
    /// </summary>
    /// <remarks>
    /// Started with the box just out of sight and held at the inner edge of the band, where the list
    /// creeps a few pixels a step, so the box is found under the pointer before it slides past.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Drop_After_The_Attributes_Scrolled_Lands_On_The_Box_Now_Under_The_Pointer()
    {
        var (window, viewer) = await Host(Kept, height: 700d);
        var (box, scroller) = Picked(window, viewer, "clip-path");
        var carried = SvgViewerElementTree.Carrying("0/0", "clip-path");
        var top = box.TranslatePoint(default, scroller)!.Value.Y;

        Assert.True(top > scroller.Bounds.Height, $"{top} of {scroller.Bounds.Height}");

        scroller.Offset = new Vector(0d, top - scroller.Bounds.Height - 8d);
        Dispatcher.UIThread.RunJobs();

        var at = Above(window, box, scroller, 30d);

        Assert.NotSame(box, BoxAt(window, at));

        Hold(window, at, carried);

        TextBox? under = null;

        await Until(() => ReferenceEquals(under = BoxAt(window, at), box));

        Assert.Same(box, under);

        Let(window, at, RawDragEventType.Drop, carried);

        Assert.Contains("clip-path=\"url(#window)\"", viewer.Source, StringComparison.Ordinal);
    }

    /// <summary>The outline comes off the box aimed at once the list scrolls it out from under the pointer.</summary>
    [AvaloniaFact]
    public async Task The_Box_Aimed_At_Loses_Its_Outline_When_The_Attributes_Scroll_It_Away()
    {
        var (window, viewer) = await Host(Kept, height: 700d);
        var (box, scroller) = Picked(window, viewer, "clip-path");
        var carried = SvgViewerElementTree.Carrying("0/0", "clip-path");
        var middle = box.TranslatePoint(new Point(0d, box.Bounds.Height / 2d), scroller)!.Value.Y;

        // The box's middle on a point of the band, with the Mask row still below it to scroll to.
        scroller.Offset = new Vector(0d, middle - scroller.Bounds.Height + 16d);
        Dispatcher.UIThread.RunJobs();

        var at = Above(window, box, scroller, 16d);
        var start = scroller.Offset.Y;

        Assert.Same(box, BoxAt(window, at));
        Assert.True(start < scroller.ScrollBarMaximum.Y, $"{start} of {scroller.ScrollBarMaximum.Y}");

        // Heard rather than read after the hold, which on a slow run can already have waited out the rest.
        var aimed = false;

        box.PropertyChanged += (_, e) => aimed |= e.Property == TemplatedControl.BorderThicknessProperty && box.BorderThickness == new Thickness(2d);

        Hold(window, at, carried);
        await Until(() => aimed && box.BorderThickness != new Thickness(2d));

        Assert.True(aimed);
        Assert.NotEqual(new Thickness(2d), box.BorderThickness);
        Assert.True(scroller.Offset.Y > start, $"{scroller.Offset.Y}");

        Let(window, at, RawDragEventType.DragLeave, carried);
    }

    /// <summary>
    /// Aiming at the last box in the list keeps the outline on and the list still, with the pointer
    /// in the band at the list's end: the outline changes no size, so the end stays where it was.
    /// </summary>
    [AvaloniaFact]
    public async Task The_Last_Box_Aimed_At_In_The_Band_At_The_End_Of_The_Attributes_Keeps_Its_Outline()
    {
        var (window, viewer) = await Host(Kept, height: 700d);
        var (box, scroller) = Picked(window, viewer, "mask");
        var carried = SvgViewerElementTree.Carrying("0/1", "mask");

        scroller.Offset = new Vector(0d, scroller.ScrollBarMaximum.Y);
        Dispatcher.UIThread.RunJobs();

        var end = scroller.Offset.Y;
        var foot = scroller.Bounds.Height - box.TranslatePoint(new Point(0d, box.Bounds.Height), scroller)!.Value.Y;
        var at = Above(window, box, scroller, foot + 3d);
        var size = box.Bounds.Size;

        Assert.True(end > 0d);
        Assert.True(foot + 3d < 32d, $"{foot}");
        Assert.Same(box, BoxAt(window, at));

        Hold(window, at, carried);

        Assert.Equal(new Thickness(2d), box.BorderThickness);

        // Updates with the pointer still, as macOS sends them, each after the layout the last one left.
        for (var update = 0; update < 4; update++)
        {
            await Task.Delay(100);
            Let(window, at, RawDragEventType.DragOver, carried);
        }

        await Pause();

        Assert.Equal(new Thickness(2d), box.BorderThickness);
        Assert.Equal(size, box.Bounds.Size);
        Assert.Equal(end, scroller.Offset.Y);

        Let(window, at, RawDragEventType.DragLeave, carried);
    }

    /// <summary>The innermost view that can still move takes the scroll; one that cannot passes it out.</summary>
    [AvaloniaFact]
    public async Task A_Nested_View_That_Cannot_Scroll_Hands_The_Scroll_To_The_One_Around_It()
    {
        var inner = new ScrollViewer { Height = 200d, Content = new Border { Height = 200d, Background = Brushes.Gray } };
        var outer = new ScrollViewer
        {
            Content = new StackPanel { Children = { inner, new Border { Height = 1000d, Background = Brushes.Silver } } }
        };
        var window = new Window { Width = 300, Height = 200, Content = outer };
        var carried = new DataTransfer();

        DragDrop.SetAllowDrop(window, true);
        SvgViewerDragScroll.Attach(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The inner view's foot, which is the outer one's as well.
        var at = Along(window, inner, -4d);

        Hold(window, at, carried);
        await Until(() => outer.Offset.Y > 24d);
        Let(window, at, RawDragEventType.DragLeave, carried);

        Assert.True(outer.Offset.Y > 24d, $"{outer.Offset.Y}");
        Assert.Equal(0d, inner.Offset.Y);

        // Given something to scroll, the inner one keeps it.
        ((Border)inner.Content!).Height = 600d;
        outer.Offset = default;
        Dispatcher.UIThread.RunJobs();

        Hold(window, at, carried);
        await Until(() => inner.Offset.Y > 24d);
        Let(window, at, RawDragEventType.DragLeave, carried);

        Assert.True(inner.Offset.Y > 24d, $"{inner.Offset.Y}");
        Assert.Equal(0d, outer.Offset.Y);
    }

    /// <summary>
    /// A box's own scroller is passed over, since sliding its text sideways would move nothing a drop
    /// could reach; the view around it scrolls sideways instead.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Text_Box_Held_Near_Its_Edge_Keeps_Its_Text_Still_And_Scrolls_The_View_Around_It()
    {
        // As wide as the window, so the box's right edge is the view's too, with more of the view beyond.
        var box = new TextBox { Width = 300d, Text = new string('x', 200), TextWrapping = TextWrapping.NoWrap };
        var outer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { box, new Border { Width = 600d, Background = Brushes.Silver } }
            }
        };
        var window = new Window { Width = 300, Height = 100, Content = outer };
        var carried = new DataTransfer();

        DragDrop.SetAllowDrop(window, true);
        SvgViewerDragScroll.Attach(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var scroller = box.GetVisualDescendants().OfType<ScrollViewer>().First();
        var at = scroller.TranslatePoint(new Point(scroller.Bounds.Width - 4d, scroller.Bounds.Height / 2d), window)!.Value;

        Assert.True(scroller.ScrollBarMaximum.X > 0d);

        Hold(window, at, carried);
        await Until(() => outer.Offset.X > 24d);
        Let(window, at, RawDragEventType.DragLeave, carried);

        Assert.True(outer.Offset.X > 24d, $"{outer.Offset.X}");
        Assert.Equal(0d, scroller.Offset.X);
    }

    /// <summary>
    /// An update that finds a new row under a still pointer, as macOS and Windows send while the
    /// rows slide, keeps the scroll going rather than waiting out the rest again.
    /// </summary>
    /// <remarks>Avalonia raises a leave on the old row and an enter on the new one for it.</remarks>
    [AvaloniaFact]
    public async Task An_Update_Onto_A_Row_Slid_Under_The_Pointer_Keeps_The_Scroll_Going()
    {
        var (window, viewer) = await Host(s_long);
        var scroller = Scroller(viewer.Elements);
        var at = Along(window, scroller, -4d);
        var carried = SvgViewerElementTree.Carrying("0");
        var left = 0;

        window.AddHandler(DragDrop.DragLeaveEvent, (_, _) => left++, handledEventsToo: true);

        var first = RowAt(window, at);

        Hold(window, at, carried);
        await Until(() => scroller.Offset.Y > 24d);

        Assert.NotEqual(first, RowAt(window, at));

        left = 0;
        Let(window, at, RawDragEventType.DragOver, carried);

        var updated = scroller.Offset.Y;

        await Task.Delay(60);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, left);
        Assert.True(scroller.Offset.Y > updated + 8d, $"{updated} -> {scroller.Offset.Y}");

        Let(window, at, RawDragEventType.DragLeave, carried);
    }

    /// <summary>
    /// A row dragged by its grip, which runs on a captured pointer rather than a drag and drop,
    /// scrolls the list as well, and goes on trading places with the rows scrolling under the pointer.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Variable_Held_At_The_Foot_Of_A_Long_List_Scrolls_It_Past_The_Rows_Out_Of_Sight()
    {
        var (window, viewer) = await Host(s_declared, height: 600d);
        var panel = viewer.GetVisualDescendants().OfType<SvgViewerDeclarationPanel>().Single();
        var items = panel.FindControl<ItemsControl>("Rows")!;
        var rows = (IList)items.ItemsSource!;
        var scroller = items.FindAncestorOfType<ScrollViewer>()!;
        var dragged = rows[0];
        int? landed = null;

        panel.ParameterMoveRequested = (_, to) =>
        {
            landed = to;

            return true;
        };

        Assert.True(scroller.ScrollBarMaximum.Y > 400d, $"{scroller.ScrollBarMaximum.Y}");

        var grip = panel.GetVisualDescendants().OfType<Border>()
            .First(border => border.Classes.Contains("grip") && ReferenceEquals(border.DataContext, dragged));
        var from = grip.TranslatePoint(new Point(grip.Bounds.Width / 2d, grip.Bounds.Height / 2d), window)!.Value;
        var foot = new Point(from.X, Along(window, scroller, -4d).Y);

        // The furthest a drag to the foot could take it with nothing scrolled: past every row in sight.
        var inSight = Enumerable.Range(0, rows.Count)
            .Select(index => items.ContainerFromIndex(index)!)
            .Count(row => row.TranslatePoint(new Point(0d, row.Bounds.Height / 2d), scroller)!.Value.Y < scroller.Bounds.Height - 4d);

        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(from.X, from.Y + 8d), RawInputModifiers.LeftMouseButton);
        window.MouseMove(foot, RawInputModifiers.LeftMouseButton);

        await Until(() => scroller.Offset.Y > 300d);

        window.MouseUp(foot, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(scroller.Offset.Y > 300d, $"{scroller.Offset.Y}");
        Assert.True(landed > inSight + 1, $"landed at {landed}, with {inSight} rows in sight before the scroll");

        // And let go, the list stays where it was left.
        var released = scroller.Offset.Y;

        await Pause();

        Assert.Equal(released, scroller.Offset.Y);
    }
}
