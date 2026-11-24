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
using Avalonia.VisualTree;
using SkiaSharp;
using Svg.SourceEditing;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>Select on a drawing's tab: its keys, the canvas menu's Select, and what it says when nothing matched.</summary>
public class SvgViewerSelectTests
{
    /// <summary>Three rectangles and an outline, after what is not drawn: a <c>&lt;defs&gt;</c>, and the declarations in it.</summary>
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 100 100" width="100" height="100">
          <defs>
            <e:code>
              <e:param name="tint" type="color" default="#ff0000" />
            </e:code>
          </defs>
          <rect id="one" x="20" y="20" width="20" height="20" fill="{{ tint }}" />
          <rect id="two" x="60" y="60" width="20" height="20" fill="#ff0000" />
          <rect id="three" x="20" y="60" width="20" height="20" fill="{{tint}}" />
          <path id="four" d="M70 20 L90 20 L90 40 L70 40" fill="none" stroke="#000000" />
        </svg>
        """;

    private static async Task<(Window Window, SvgViewer Viewer)> Host()
    {
        var viewer = new SvgViewer();
        var window = new Window { Width = 400, Height = 400, Background = Brushes.White, Content = viewer };

        window.Show();

        Assert.True(await viewer.LoadTextAsync(Drawing));

        Dispatcher.UIThread.RunJobs();

        return (window, viewer);
    }

    private static Point At(Window window, SvgViewer viewer, float x, float y)
    {
        Assert.True(viewer.Canvas.TryGetControlPoint(new SKPoint(x, y), out var point));

        return viewer.Canvas.TranslatePoint(point, window)
               ?? throw new InvalidOperationException("The canvas is not in the window.");
    }

    private static void Click(Window window, Point at)
    {
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Headless, the platform's command key is Control.</summary>
    private static void Press(Window window, RawInputModifiers modifiers)
    {
        window.KeyPressQwerty(PhysicalKey.A, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static string[] Picked(SvgViewer viewer) => viewer.Elements.SelectedAddresses.ToArray();

    /// <summary>Opens the canvas's menu as the menu key does, and clicks down through its items by header.</summary>
    private static void Choose(SvgViewer viewer, params string[] path)
    {
        var menu = viewer.Canvas.ContextMenu!;

        viewer.Canvas.RaiseEvent(new ContextRequestedEventArgs());
        Dispatcher.UIThread.RunJobs();

        var items = menu.Items.OfType<MenuItem>();
        MenuItem? item = null;

        foreach (var header in path)
        {
            item = items.Single(candidate => candidate.IsVisible && (candidate.Header as string ?? (candidate.Header as TextBlock)?.Text) == header);
            items = item.Items.OfType<MenuItem>();
        }

        Assert.True(item!.IsEnabled);

        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <remarks>The tree's own Select All took every open row, the drawing and its &lt;defs&gt; with them, and Delete then refused.</remarks>
    [AvaloniaFact]
    public async Task Command_A_In_The_Tree_Selects_What_Is_Drawn_Not_Every_Open_Row()
    {
        var (window, viewer) = await Host();
        var tree = viewer.Elements.FindControl<TreeView>("Tree")!;

        Assert.True(viewer.Elements.TrySelect("1"));
        tree.GetVisualDescendants().OfType<TreeViewItem>().First(row => row.IsSelected).Focus();
        Dispatcher.UIThread.RunJobs();

        Press(window, RawInputModifiers.Control);

        Assert.Equal(new[] { "1", "2", "3", "4" }, Picked(viewer));
    }

    [AvaloniaFact]
    public async Task Command_A_On_The_Canvas_Selects_All_And_Shift_Lets_Go_Of_The_Page_Too()
    {
        var (window, viewer) = await Host();

        Click(window, At(window, viewer, 90, 10));
        Assert.True(viewer.IsPageSelected);

        Press(window, RawInputModifiers.Control);

        Assert.Equal(new[] { "1", "2", "3", "4" }, Picked(viewer));
        Assert.False(viewer.IsPageSelected);

        Click(window, At(window, viewer, 90, 10));
        Assert.True(viewer.IsPageSelected);

        Press(window, RawInputModifiers.Control | RawInputModifiers.Shift);

        Assert.Empty(Picked(viewer));
        Assert.False(viewer.IsPageSelected);
    }

    /// <summary>With the points tool out, ⌘A is of the outline's points, and the elements stay as they were.</summary>
    [AvaloniaFact]
    public async Task Command_A_With_Points_Out_Chooses_Points_And_Leaves_The_Elements()
    {
        var (window, viewer) = await Host();

        Click(window, At(window, viewer, 80, 20));
        viewer.Drafting.Arm(SvgViewerDraw.Tools.Single(tool => tool.Name == "Points").Tool);
        Dispatcher.UIThread.RunJobs();

        Press(window, RawInputModifiers.Control);

        Assert.Equal(4, viewer.Canvas.Points!.Chosen.Count);
        Assert.Equal(new[] { "4" }, Picked(viewer));
    }

    /// <summary>The file is compared, so <c>{{ tint }}</c> is not the red it comes to.</summary>
    [AvaloniaFact]
    public async Task Same_Fill_From_The_Canvas_Menu_Compares_What_The_File_Writes()
    {
        var (window, viewer) = await Host();

        Click(window, At(window, viewer, 30, 30));

        Choose(viewer, "Select", "Same", "Fill");

        Assert.Equal(new[] { "1", "3" }, Picked(viewer));
    }

    [AvaloniaFact]
    public async Task Uses_Of_Lists_The_Declared_Names_And_Selects_What_Reads_One()
    {
        var (_, viewer) = await Host();

        Choose(viewer, "Select", "Uses of", "tint");

        Assert.Equal(new[] { "1", "3" }, Picked(viewer));
    }

    /// <summary>With no drawing open there is nothing to select, and nothing wrong to say.</summary>
    [AvaloniaFact]
    public void Select_All_With_No_Drawing_Does_Nothing_And_Says_Nothing()
    {
        var viewer = new SvgViewer();
        var window = new Window { Width = 400, Height = 400, Content = viewer };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewer.Select(SvgElementSelect.All));
        Assert.False(viewer.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "NoteText").IsVisible);
    }

    [AvaloniaFact]
    public async Task Nothing_Matched_Leaves_The_Selection_And_Says_So()
    {
        var (window, viewer) = await Host();

        Click(window, At(window, viewer, 70, 70));

        Assert.False(viewer.Select(SvgElementSelect.Type, "ellipse"));

        Assert.Equal(new[] { "2" }, Picked(viewer));
        Assert.Equal(
            "Nothing drawn here is a <ellipse>.",
            viewer.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "NoteText").Text);
    }
}
