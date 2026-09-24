// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// How the panels are arranged, from one tab to the settings file and back to every other tab.
/// </summary>
/// <remarks>
/// In the settings collection because it repoints the one static store, like every other class that
/// writes a setting.
///
/// One arrangement for every tab, which is what these are really about: a drawing's tab and a
/// group's board are the same four panels in the same body, so arranging one arranges all of them.
/// </remarks>
[Collection("settings")]
public class LayoutSettingTests : IDisposable
{
    /// <summary>An arrangement nothing would land in by itself, so finding it means it travelled.</summary>
    private const string Arranged =
        "row(tree/260px/tree/open,variables/300px/variables/open,*/1,"
        + "col(project+elements/1/elements/open,element/1/element/open)/340px)";

    private const string Project = """
        <studio namespace="Demo.Icons">
          <drawing name="home" class="Home">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#00ff00" />
            </svg>
          </drawing>
        </studio>
        """;

    private readonly string _was = StudioSettings.Store;
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public LayoutSettingTests() => StudioSettings.Store = Path.Combine(_directory, "settings");

    public void Dispose()
    {
        StudioSettings.Store = _was;

        Directory.Delete(_directory, recursive: true);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);

        File.WriteAllText(path, text);

        return path;
    }

    private static async Task<MainWindow> Host(string path)
    {
        var window = new MainWindow();

        window.Announce = (_, _) => Task.CompletedTask;

        window.Show();
        Dispatcher.UIThread.RunJobs();

        await window.OpenAsync(new[] { path });
        Dispatcher.UIThread.RunJobs();

        window.Measure(new Size(900, 600));
        window.Arrange(new Rect(0, 0, 900, 600));
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    private static TabControl Tabs(MainWindow window)
        => window.GetVisualDescendants().OfType<TabControl>().First();

    [Fact]
    public void An_Arrangement_Is_Written_Down_As_It_Was_Made()
    {
        Assert.Equal(StudioSettings.DefaultLayout, StudioSettings.Layout);

        StudioSettings.Layout = Arranged;

        Assert.Equal($"layout={Arranged}", File.ReadAllText(StudioSettings.Store).Trim());
        Assert.Equal(Arranged, StudioSettings.Layout);
    }

    /// <summary>A file that says nothing about it, or nothing readable, is the default.</summary>
    /// <remarks>
    /// The grammar is the dock's, not this class's: what a line means is answered there, and a line
    /// that means nothing falls back whole rather than being half applied.
    /// </remarks>
    [AvaloniaFact]
    public void A_Line_That_Says_Nothing_Usable_Leaves_The_Default()
    {
        // The flat grammar an earlier arrangement wrote, which is not a tree and so falls back —
        // and falling back is the migration.
        File.WriteAllText(StudioSettings.Store, "layout=right 340 variables/1/variables/open\n");

        // Handed on as it was found — the grammar is not this class's business — and refused there.
        Assert.Equal("right 340 variables/1/variables/open", StudioSettings.Layout);

        var viewer = new SvgViewer { Layout = StudioSettings.Layout };

        // The viewer's own default, not Studio's: a viewer knows nothing about a project tree.
        Assert.Equal(SvgViewerDock.Default, viewer.Layout);
    }

    [AvaloniaFact]
    public async Task A_Tab_Opened_Later_Is_Arranged_The_Way_The_Last_One_Was()
    {
        StudioSettings.Layout = Arranged;

        var window = await Host(Write("icons.svgstudio", Project));

        var board = (GroupPanel)((TabItem)Tabs(window).SelectedItem!).Content!;

        Assert.Equal(Arranged, window.Layout);

        // And it does not move when the tab does: the panels are the window's and show whatever is
        // in front, so changing tab changes what is inside them and nothing about where they are.
        var drawing = ((ProjectGroup)board.Node).Drawings.Single();

        await window.ShowAsync(drawing);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Arranged, window.Layout);

        window.Close();
    }

    /// <summary>Takes a panel by its header, on whatever tab is in front, and drops it on another.</summary>
    private static void Carry(MainWindow window, string header, string onto)
    {
        var from = Middle(window, Tab(window, header));
        var to = Middle(window, Tab(window, onto));

        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(from.X + 10d, from.Y), RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();

        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();

        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static Border Tab(MainWindow window, string header)
        => window.GetVisualDescendants().OfType<Border>()
            .First(found => found.Classes.Contains("pane")
                            && found.Child is TextBlock said
                            && string.Equals(said.Text, header, StringComparison.Ordinal));

    private static Point Middle(MainWindow window, Visual visual)
        => visual.TranslatePoint(new Point(visual.Bounds.Width / 2d, visual.Bounds.Height / 2d), window)!.Value;

    /// <summary>
    /// A panel carried anywhere is written down, and is where it was left whatever tab is in front.
    /// </summary>
    /// <remarks>
    /// The whole route in one: a hand on the panels, the settings file, and the arrangement holding
    /// still across a change of tab — which is what one dock round the window buys over one inside
    /// each of them.
    /// </remarks>
    [AvaloniaFact]
    public async Task Arranging_The_Panels_Holds_Across_Every_Tab()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        var board = (GroupPanel)((TabItem)Tabs(window).SelectedItem!).Content!;
        var drawing = ((ProjectGroup)board.Node).Drawings.Single();

        await window.ShowAsync(drawing);
        Dispatcher.UIThread.RunJobs();

        window.Measure(new Size(900, 600));
        window.Arrange(new Rect(0, 0, 900, 600));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(StudioSettings.DefaultLayout, window.Layout);

        Carry(window, "Variables", "Element");

        Assert.Contains("element+variables", window.Layout, StringComparison.Ordinal);

        // Written down, so it is there again next time.
        Assert.Equal(window.Layout, StudioSettings.Layout);

        // And still there behind whichever tab you go to next.
        await window.ShowAsync(board.Node);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("element+variables", window.Layout, StringComparison.Ordinal);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Resetting_Puts_The_Panels_Back()
    {
        StudioSettings.Layout = Arranged;

        var window = await Host(Write("icons.svgstudio", Project));
        Assert.Equal(Arranged, window.Layout);

        // Through the settings window's own button, and out to the tabs the way closing it does.
        window.ShowSettings = () =>
        {
            var settings = new SettingsWindow();

            settings.ResetLayout.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            return Task.CompletedTask;
        };

        await window.ShowSettingsAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(StudioSettings.DefaultLayout, StudioSettings.Layout);
        Assert.Equal(StudioSettings.DefaultLayout, window.Layout);

        window.Close();
    }
    /// <summary>
    /// Reset reaches what is open before the settings window closes.
    /// </summary>
    /// <remarks>
    /// Everything else on that window is read again when it closes. The panels moving cannot wait
    /// that long: a button that appears to do nothing is a button nobody presses twice.
    /// </remarks>
    [AvaloniaFact]
    public void Resetting_Applies_While_The_Window_Is_Still_Open()
    {
        StudioSettings.Layout = Arranged;

        var applied = 0;
        var settings = new SettingsWindow { Applied = () => applied++ };

        settings.ResetLayout.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(StudioSettings.DefaultLayout, StudioSettings.Layout);
        Assert.Equal(1, applied);
    }
    // ---- the project tree is a panel like any other ----------------------------------------------

    [AvaloniaFact]
    public async Task The_Project_Tree_Is_A_Panel_Of_The_Window()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        // Down the left, where Studio's own default puts it.
        Assert.StartsWith("row(tree/", window.Layout, StringComparison.Ordinal);

        // And it is carried by its header like the rest: dropped on the variables it sits behind
        // them, which is a place three fixed sides could not have put it.
        Carry(window, "Project", "Variables");

        Assert.Contains("variables+tree", window.Layout, StringComparison.Ordinal);
        Assert.Equal(window.Layout, StudioSettings.Layout);

        window.Close();
    }

    /// <summary>
    /// Rearranging the panels does not change which document is in front.
    /// </summary>
    /// <remarks>
    /// The strip of tabs is the middle of the arrangement, so rearranging moves it — and a
    /// TabControl taken out of the tree and put back comes back showing its first tab. Folding a
    /// panel would have put you on a different drawing.
    /// </remarks>
    [AvaloniaFact]
    public async Task Rearranging_The_Panels_Leaves_The_Tab_In_Front_Alone()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        var board = (GroupPanel)((TabItem)Tabs(window).SelectedItem!).Content!;

        await window.ShowAsync(((ProjectGroup)board.Node).Drawings.Single());
        Dispatcher.UIThread.RunJobs();

        var front = Tabs(window).SelectedItem;

        Assert.IsType<SvgViewer>(((TabItem)front!).Content);

        // Anything that rebuilds the body will do; folding a panel is the cheapest to ask for.
        window.Layout = window.Layout.Replace("/elements/open", "/elements/folded", StringComparison.Ordinal);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(front, Tabs(window).SelectedItem);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Closing_Every_Tab_Leaves_The_Tree_Up()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        foreach (var item in Tabs(window).Items.OfType<TabItem>().ToList())
        {
            Tabs(window).Items.Remove(item);
        }

        Dispatcher.UIThread.RunJobs();

        // The tree names the drawings the tabs hold, so it does not go when the last of them does.
        Assert.Contains("tree/", window.Layout, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>Looking a drawing up opens the tree first, or it would scroll nothing.</summary>
    [AvaloniaFact]
    public async Task Revealing_A_Row_Opens_The_Tree_It_Is_In()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        window.Layout = window.Layout.Replace("/tree/open", "/tree/folded", StringComparison.Ordinal);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("/tree/folded", window.Layout, StringComparison.Ordinal);

        // Looked for by name, which is the gesture that ends in a row being brought into view.
        window.FindControl<TextBox>("ProjectSearch")!.Text = "home";
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("/tree/open", window.Layout, StringComparison.Ordinal);

        window.Close();
    }
}
