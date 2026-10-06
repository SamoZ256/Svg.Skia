using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>The rows a tree is scrolled inside of, pinned at its top.</summary>
public class SvgViewerStickyRowsTests
{
    /// <summary>
    /// Ten rows, then two groups one inside the other holding thirty, then ten more: every row is
    /// as tall as the next, so where each sits is its index times that.
    /// </summary>
    private static string Markup()
    {
        string Rects(string name, int count) => string.Concat(Enumerable.Range(0, count).Select(index => $"<rect id=\"{name}{index}\" width=\"1\" height=\"1\" />"));

        return $"<svg xmlns=\"http://www.w3.org/2000/svg\">{Rects("head", 10)}<g id=\"outer\"><g id=\"inner\">{Rects("r", 30)}</g></g>{Rects("tail", 10)}</svg>";
    }

    private static (Window Window, SvgViewerElementTree Tree) Host()
    {
        var tree = new SvgViewerElementTree();
        var window = new Window { Width = 300, Height = 260, Background = Brushes.White, Content = tree };

        window.Show();
        tree.Show(SvgDocument.FromSvg<SvgDocument>(Markup()));

        foreach (var node in tree.Root!.Flatten())
        {
            node.IsExpanded = true;
        }

        Settle(window);

        return (window, tree);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Dispatcher.UIThread.RunJobs();
    }

    private static ScrollViewer Scroller(SvgViewerElementTree tree)
        => tree.GetVisualDescendants().OfType<TreeView>().Single().GetVisualDescendants().OfType<ScrollViewer>().First();

    private static TreeViewItem Item(SvgViewerElementTree tree, string label)
        => tree.GetVisualDescendants().OfType<TreeViewItem>().Single(item => item.DataContext?.ToString() == label);

    private static Border Row(TreeViewItem item)
        => item.GetVisualDescendants().OfType<Border>().First(border => border.Name == "PART_LayoutRoot");

    /// <summary>Where <paramref name="label"/>'s row is drawn in the viewport, pinned or not.</summary>
    private static double Top(SvgViewerElementTree tree, string label)
        => Row(Item(tree, label)).TranslatePoint(default, Scroller(tree))!.Value.Y;

    /// <summary>The pinned rows, top first.</summary>
    private static string[] Pinned(SvgViewerElementTree tree)
        => tree.GetVisualDescendants().OfType<TreeViewItem>()
            .Where(item => Row(item).RenderTransform is { })
            .Select(item => item.DataContext!.ToString()!)
            .ToArray();

    private static void Scroll(Window window, SvgViewerElementTree tree, double y)
    {
        Scroller(tree).Offset = new Vector(0, y);
        Settle(window);
    }

    /// <summary>The height of one row, which the fixture's numbers are counted in.</summary>
    private static double Height(SvgViewerElementTree tree) => Row(Item(tree, "svg")).Bounds.Height;

    private static void Press(Window window, SvgViewerElementTree tree, string label, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var row = Row(Item(tree, label));
        var at = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;

        window.MouseDown(at, MouseButton.Left, modifiers);
        window.MouseUp(at, MouseButton.Left, modifiers);
        Settle(window);
    }

    [AvaloniaFact]
    public void Nothing_Is_Pinned_Before_The_Tree_Is_Scrolled()
    {
        var (_, tree) = Host();

        Assert.Empty(Pinned(tree));
    }

    [AvaloniaFact]
    public void Scrolled_Inside_Two_Groups_Their_Rows_Are_Pinned_One_Under_Another()
    {
        var (window, tree) = Host();
        var h = Height(tree);

        // Twenty rows down: inside inner, whose own row is the thirteenth.
        Scroll(window, tree, 20 * h);

        Assert.Equal(new[] { "svg", "g #outer", "g #inner" }, Pinned(tree));
        Assert.Equal(0d, Top(tree, "svg"), 3);
        Assert.Equal(h, Top(tree, "g #outer"), 3);
        Assert.Equal(2 * h, Top(tree, "g #inner"), 3);

        // Solid, so the rows scrolled under them do not show through.
        Assert.All(new[] { "svg", "g #outer", "g #inner" }, label => Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(Row(Item(tree, label)).Background).Color));
    }

    /// <summary>As the last rows of a group go by, its pinned row goes up with them rather than covering the row after it.</summary>
    [AvaloniaFact]
    public void A_Pinned_Row_Is_Pushed_Up_By_The_End_Of_Its_Group()
    {
        var (window, tree) = Host();
        var h = Height(tree);

        // Both groups end after row 43; scrolled so that is 90 pixels down the viewport, a little
        // short of the bottom of the third pinned row.
        Scroll(window, tree, 43 * h - 90d);

        Assert.Equal(new[] { "svg", "g #outer", "g #inner" }, Pinned(tree));
        Assert.Equal(h, Top(tree, "g #outer"), 3);
        Assert.Equal(90d - h, Top(tree, "g #inner"), 3);
        Assert.Equal(90d, Top(tree, "rect #tail0"), 3);
    }

    /// <summary>A press on a pinned row picks it, as on any row, and scrolls it into its own place under the rows above it.</summary>
    [AvaloniaFact]
    public void Pressing_A_Pinned_Row_Picks_It_Where_It_Is()
    {
        var (window, tree) = Host();
        var h = Height(tree);

        Scroll(window, tree, 20 * h);
        Press(window, tree, "g #outer");

        Assert.Equal("g #outer", tree.SelectedNode?.ToString());

        // Where it was drawn, and in its own place: ten rows down with the root pinned above it.
        Assert.Equal(10 * h, Scroller(tree).Offset.Y, 3);
        Assert.Equal(h, Top(tree, "g #outer"), 3);
        Assert.Equal(new[] { "svg" }, Pinned(tree));
    }

    [AvaloniaFact]
    public void A_Pinned_Row_Is_Added_To_The_Selection_As_Any_Row_Is()
    {
        var (window, tree) = Host();
        var h = Height(tree);

        Scroll(window, tree, 20 * h);
        Press(window, tree, "rect #r10");
        Press(window, tree, "g #inner", RawInputModifiers.Control);

        Assert.Equal(new[] { "rect #r10", "g #inner" }, tree.SelectedNodes.Select(node => node.ToString()).OrderByDescending(label => label));
    }

    /// <summary>A row brought into view from above lands under the rows pinned over it rather than behind them.</summary>
    [AvaloniaFact]
    public void A_Row_Brought_Into_View_Is_Not_Hidden_Behind_The_Pins()
    {
        var (window, tree) = Host();
        var h = Height(tree);

        Scroll(window, tree, 35 * h);

        // r5 is row 18, so above the viewport.
        Item(tree, "rect #r5").BringIntoView();
        Settle(window);

        Assert.Equal(new[] { "svg", "g #outer", "g #inner" }, Pinned(tree));
        Assert.Equal(3 * h, Top(tree, "rect #r5"), 3);
    }
}
