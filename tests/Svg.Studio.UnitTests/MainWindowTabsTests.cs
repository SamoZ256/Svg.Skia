using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// The shell's tab strip: a tab per drawing, dragged into the order its owner wants, scrolling
/// sideways once there are more than fit.
/// </summary>
/// <remarks>
/// Driven through simulated pointer input rather than by calling the handlers, because everything
/// that makes reordering work is in the plumbing — that a tunnelling handler sees a press
/// <see cref="TabItem"/> handles itself, that the capture keeps moves arriving once the pointer has
/// left the tab, and that the close button is not a drag handle. Calling the handlers directly would
/// assert none of it.
/// </remarks>
public class MainWindowTabsTests
{
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect width="24" height="24" fill="#00ff00" />
        </svg>
        """;

    /// <summary>Opens a window holding <paramref name="count"/> drawings beyond the one it starts on.</summary>
    private static async Task<(MainWindow Window, TabControl Tabs)> Host(int count)
    {
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tabs = window.FindControl<TabControl>("Tabs")!;

        await Settle(tabs);

        var paths = new string[count];

        for (var index = 0; index < count; index++)
        {
            paths[index] = Path.Combine(Path.GetTempPath(), $"svg-viewer-tab-{index}-{Guid.NewGuid():N}.svg");
            File.WriteAllText(paths[index], Drawing);
        }

        try
        {
            var first = (SvgViewer)((TabItem)tabs.Items[0]!).Content!;

            // The same request the toolbar's Open and a drop both raise, which is what the window
            // turns into a tab each.
            Assert.True(await first.OpenAsync(paths));

            // Laid out, for the tests that measure tabs. The drawings are already in: the window
            // hands back what it started and OpenAsync waits on it.
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            foreach (var path in paths)
            {
                File.Delete(path);
            }
        }

        return (window, tabs);
    }

    /// <summary>Waits for the drawing the window opens with.</summary>
    /// <remarks>
    /// The window starts loading its bundled sample as it is constructed, and that load leaves the
    /// UI thread with nothing to await it by. A tab holding nothing is reused rather than added to,
    /// so a test that opens files before the sample lands gets one tab fewer than it asked for — on
    /// a busy machine only, which is how it first showed up in a full solution run.
    /// </remarks>
    private static async Task Settle(TabControl tabs)
    {
        var viewer = (SvgViewer)((TabItem)tabs.Items[0]!).Content!;

        for (var attempt = 0; attempt < 200 && viewer.Document is null; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.NotNull(viewer.Document);
    }

    /// <summary>The drawing in each tab, in strip order, by file name.</summary>
    private static string[] Order(TabControl tabs) => tabs.Items
        .OfType<TabItem>()
        .Select(item => ((SvgViewer)item.Content!).DocumentPath is { } path
            ? Path.GetFileName(path)
            : "<empty>")
        .ToArray();

    /// <summary>
    /// The button still down, which a simulated move does not carry on its own.
    /// </summary>
    /// <remarks>
    /// The window reads it to tell a drag from a pointer wandering back over the strip after the
    /// button was let go somewhere it could not see.
    /// </remarks>
    private const RawInputModifiers Held = RawInputModifiers.LeftMouseButton;

    private static Point Centre(Visual root, Visual target)
        => target.TranslatePoint(new Point(target.Bounds.Width / 2d, target.Bounds.Height / 2d), root)
           ?? throw new InvalidOperationException("The control is not in the window's visual tree.");

    private static void Drag(MainWindow window, TabItem tab, Point to, double firstStep)
    {
        var from = Centre(window, tab);

        window.MouseDown(from, MouseButton.Left);

        // Past the threshold first, because one long move would reorder without ever proving that a
        // press on its own does not.
        window.MouseMove(new Point(from.X + firstStep, from.Y), Held);
        Dispatcher.UIThread.RunJobs();

        window.MouseMove(to, Held);
        Dispatcher.UIThread.RunJobs();

        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void The_Window_Starts_On_One_Tab()
    {
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(window.FindControl<TabControl>("Tabs")!.Items);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Opening_Has_Finished_By_The_Time_The_Call_Returns()
    {
        // The window answers an open request by taking it and loading on its own, so a caller can
        // only be sure the drawing is there if what the window started is handed back and waited on.
        //
        // The drawing is deliberately big. The small ones elsewhere load inside whatever slack the
        // dispatcher has, which is why this same assertion passed here while failing on all three CI
        // runners. Nothing below is pumped or polled, on purpose.
        var (window, tabs) = await Host(0);

        var shapes = new StringBuilder();

        for (var i = 0; i < 4000; i++)
        {
            shapes.Append(CultureInfo.InvariantCulture, $"<rect x=\"{i % 100}\" y=\"{i / 100}\" width=\"1\" height=\"1\" fill=\"#3366cc\" />");
        }

        var path = Path.Combine(Path.GetTempPath(), $"svg-viewer-heavy-{Guid.NewGuid():N}.svg");

        File.WriteAllText(
            path,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 40\" width=\"100\" height=\"40\">{shapes}</svg>");

        try
        {
            var viewer = (SvgViewer)((TabItem)tabs.Items[0]!).Content!;

            Assert.True(await viewer.OpenAsync(new[] { path }));

            Assert.Equal(2, tabs.Items.Count);
            Assert.Equal(Path.GetFileName(path), Order(tabs)[1]);
        }
        finally
        {
            File.Delete(path);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_Strip_Stays_When_A_Drawing_Closes_Back_Down_To_One()
    {
        var (window, tabs) = await Host(1);

        var strip = tabs.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_TabStripBand");

        Assert.Equal(2, tabs.Items.Count);
        Assert.True(strip.IsVisible);

        var second = (TabItem)tabs.Items[1]!;
        var close = (Button)((StackPanel)second.Header!).Children[1];

        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        // The strip is where the drawings are, so it does not come and go with the second one.
        Assert.Single(tabs.Items);
        Assert.True(strip.IsVisible, "the strip went away with the second drawing");

        window.Close();
    }

    [AvaloniaFact]
    public void A_Window_On_Its_Own_Drawing_Still_Shows_The_Strip()
    {
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tabs = window.FindControl<TabControl>("Tabs")!;
        var strip = tabs.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_TabStripBand");

        // The first tab is added before the control is templated, so this is also the check that
        // the strip is there at all once it arrives.
        Assert.Single(tabs.Items);
        Assert.True(strip.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Every_File_Opened_Gets_A_Tab_Of_Its_Own()
    {
        var (window, tabs) = await Host(3);

        Assert.Equal(4, tabs.Items.Count);
        Assert.Equal(4, Order(tabs).Distinct().Count());
        Assert.Same(tabs.Items[^1], tabs.SelectedItem);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Closing_The_Last_Tab_Leaves_An_Empty_One_For_The_Next_Drawing()
    {
        var (window, tabs) = await Host(0);

        var only = (TabItem)tabs.Items[0]!;
        var close = (Button)((StackPanel)only.Header!).Children[1];

        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        // Not the window closing, and not nothing: somewhere to open the next file.
        Assert.Single(tabs.Items);
        Assert.NotSame(only, tabs.Items[0]);
        Assert.Equal(new[] { "<empty>" }, Order(tabs));

        var path = Path.Combine(Path.GetTempPath(), $"svg-viewer-tab-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, Drawing);

        try
        {
            // An empty tab is filled rather than left standing in front of the drawing.
            Assert.True(await ((SvgViewer)((TabItem)tabs.Items[0]!).Content!).OpenAsync(new[] { path }));

            Assert.Single(tabs.Items);
            Assert.Equal(new[] { Path.GetFileName(path) }, Order(tabs));
        }
        finally
        {
            File.Delete(path);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Dragging_The_Last_Tab_To_The_Front_Reorders_It()
    {
        var (window, tabs) = await Host(3);

        var before = Order(tabs);
        var dragged = (TabItem)tabs.Items[^1]!;
        var target = Centre(window, (TabItem)tabs.Items[0]!);

        Drag(window, dragged, new Point(target.X - 2d, target.Y), firstStep: -6d);

        Assert.Equal(new[] { before[^1] }.Concat(before[..^1]), Order(tabs));

        // The dragged tab keeps the drawing under the pointer, which removing the selected item
        // from the strip would otherwise swap.
        Assert.Same(dragged, tabs.SelectedItem);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Dragging_A_Tab_One_Place_To_The_Right_Swaps_It_With_Its_Neighbour()
    {
        var (window, tabs) = await Host(3);

        var before = Order(tabs);
        var dragged = (TabItem)tabs.Items[0]!;
        var neighbour = Centre(window, (TabItem)tabs.Items[1]!);

        Drag(window, dragged, new Point(neighbour.X + 2d, neighbour.Y), firstStep: 6d);

        var expected = before.ToArray();
        (expected[0], expected[1]) = (expected[1], expected[0]);

        Assert.Equal(expected, Order(tabs));

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Dragged_Tab_Follows_The_Pointer_And_Is_Put_Down_On_Release()
    {
        var (window, tabs) = await Host(3);

        var dragged = (TabItem)tabs.Items[0]!;
        var from = Centre(window, dragged);
        var to = new Point(from.X + 40d, from.Y);

        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(from.X + 6d, from.Y), Held);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(to, Held);
        Dispatcher.UIThread.RunJobs();

        // Carried by the pointer, not merely swapped into place: the tab is drawn away from where it
        // has been laid out, by however far the pointer has taken it.
        var carried = Assert.IsType<TranslateTransform>(dragged.RenderTransform);
        Assert.True(Math.Abs(carried.X) > 1d, $"the tab did not move with the pointer: {carried.X}");
        Assert.Equal(1, dragged.ZIndex);

        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(dragged.RenderTransform);
        Assert.Equal(0, dragged.ZIndex);

        window.Close();
    }

    [AvaloniaFact]
    public async Task One_Drag_Can_Pass_Several_Tabs_Without_Being_Let_Go_Of()
    {
        var (window, tabs) = await Host(3);

        var before = Order(tabs);
        var dragged = (TabItem)tabs.Items[0]!;
        var from = Centre(window, dragged);

        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(from.X + 6d, from.Y), Held);
        Dispatcher.UIThread.RunJobs();

        // Each move crosses one more neighbour, which is what a real drag does: the swap must not
        // end the gesture that caused it.
        foreach (var index in new[] { 1, 2, 3 })
        {
            var neighbour = Centre(window, (TabItem)tabs.Items[index]!);
            window.MouseMove(new Point(neighbour.X + 2d, neighbour.Y), Held);
            Dispatcher.UIThread.RunJobs();

            Assert.True(
                tabs.Items.IndexOf(dragged) == index,
                $"the drag stopped after {index - 1} swap(s): {string.Join(", ", Order(tabs))}");
        }

        window.MouseUp(Centre(window, dragged), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before[1..].Append(before[0]), Order(tabs));

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Button_Released_Outside_The_Window_Does_Not_Leave_The_Tab_Dragging()
    {
        var (window, tabs) = await Host(3);

        var before = Order(tabs);
        var dragged = (TabItem)tabs.Items[0]!;
        var from = Centre(window, dragged);

        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(from.X + 6d, from.Y), Held);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(dragged.RenderTransform);

        // The pointer comes back over the strip with the button up, which is all the window ever
        // learns about a release that happened somewhere else.
        var neighbour = Centre(window, (TabItem)tabs.Items[2]!);
        window.MouseMove(new Point(neighbour.X + 2d, neighbour.Y));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(dragged.RenderTransform);
        Assert.Equal(before, Order(tabs));

        // And moving on does not pick the tab back up.
        window.MouseMove(new Point(neighbour.X + 20d, neighbour.Y));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(dragged.RenderTransform);
        Assert.Equal(before, Order(tabs));

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Press_That_Barely_Moves_Is_A_Click_And_Not_A_Drag()
    {
        var (window, tabs) = await Host(3);

        var before = Order(tabs);
        var point = Centre(window, (TabItem)tabs.Items[0]!);

        window.MouseDown(point, MouseButton.Left);
        window.MouseMove(new Point(point.X + 1d, point.Y), Held);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before, Order(tabs));
        Assert.Same(tabs.Items[0], tabs.SelectedItem);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Pressing_The_Close_Button_Closes_That_Tab_Rather_Than_Dragging_It()
    {
        var (window, tabs) = await Host(3);

        var before = Order(tabs);
        var item = (TabItem)tabs.Items[1]!;
        var close = (Button)((StackPanel)item.Header!).Children[1];
        var point = Centre(window, close);

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before.Where((_, index) => index != 1), Order(tabs));

        window.Close();
    }

    [AvaloniaFact]
    public async Task More_Tabs_Than_Fit_Scroll_Sideways_Instead_Of_Wrapping()
    {
        var (window, tabs) = await Host(24);

        var strip = tabs.GetVisualDescendants().OfType<ScrollViewer>().First(v => v.Name == "PART_TabStrip");

        Assert.True(
            strip.Extent.Width > strip.Viewport.Width,
            $"The strip does not overflow: extent {strip.Extent}, viewport {strip.Viewport}.");

        // Wrapping is what Fluent's own template does, and it pushes the drawing down a row.
        Assert.True(
            strip.Extent.Height <= strip.Viewport.Height + 1d,
            $"The tabs wrapped onto a second row: extent {strip.Extent}, viewport {strip.Viewport}.");

        // Opening scrolled the newest tab into view, so there is room to the left and none to the right.
        var offset = strip.Offset.X;
        Assert.True(offset > 0d, "The newest tab was not scrolled into view.");

        window.MouseWheel(Centre(window, strip), new Vector(0d, 1d));
        Dispatcher.UIThread.RunJobs();

        Assert.True(
            strip.Offset.X < offset,
            $"The wheel did not scroll the strip: {offset} -> {strip.Offset.X}.");

        window.Close();
    }
}
