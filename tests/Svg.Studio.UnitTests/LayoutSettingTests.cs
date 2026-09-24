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
        "row(variables/300px/variables/open,*/1,col(project+elements/1/elements/open,element/1/element/open)/340px)";

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
        Assert.Equal(SvgViewerDock.Default, StudioSettings.Layout);

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

        Assert.Equal(SvgViewerDock.Default, viewer.Layout);
    }

    [AvaloniaFact]
    public async Task A_Tab_Opened_Later_Is_Arranged_The_Way_The_Last_One_Was()
    {
        StudioSettings.Layout = Arranged;

        var window = await Host(Write("icons.svgstudio", Project));

        var board = (GroupPanel)((TabItem)Tabs(window).SelectedItem!).Content!;

        Assert.Equal(Arranged, board.Layout);

        var drawing = ((ProjectGroup)board.Node).Drawings.Single();

        await window.ShowAsync(drawing);
        Dispatcher.UIThread.RunJobs();

        var viewer = (SvgViewer)((TabItem)Tabs(window).SelectedItem!).Content!;

        Assert.Equal(Arranged, viewer.Layout);

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
    /// A panel carried on one tab is carried on all of them, and is still there next time.
    /// </summary>
    /// <remarks>
    /// The whole route in one: a hand on a drawing's tab, the settings file, and the board behind it
    /// — which is a different control built by a different class around the same dock.
    /// </remarks>
    [AvaloniaFact]
    public async Task Arranging_One_Tab_Arranges_Every_Other_One()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        var board = (GroupPanel)((TabItem)Tabs(window).SelectedItem!).Content!;
        var drawing = ((ProjectGroup)board.Node).Drawings.Single();

        await window.ShowAsync(drawing);
        Dispatcher.UIThread.RunJobs();

        window.Measure(new Size(900, 600));
        window.Arrange(new Rect(0, 0, 900, 600));
        Dispatcher.UIThread.RunJobs();

        var viewer = (SvgViewer)((TabItem)Tabs(window).SelectedItem!).Content!;

        Assert.Equal(SvgViewerDock.Default, viewer.Layout);
        Assert.Equal(SvgViewerDock.Default, board.Layout);

        Carry(window, "Variables", "Element");

        Assert.Contains("element+variables", viewer.Layout, StringComparison.Ordinal);

        // The file, and the tab behind this one.
        Assert.Equal(viewer.Layout, StudioSettings.Layout);
        Assert.Equal(viewer.Layout, board.Layout);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Resetting_Puts_The_Panels_Back()
    {
        StudioSettings.Layout = Arranged;

        var window = await Host(Write("icons.svgstudio", Project));
        var board = (GroupPanel)((TabItem)Tabs(window).SelectedItem!).Content!;

        Assert.Equal(Arranged, board.Layout);

        // Through the settings window's own button, and out to the tabs the way closing it does.
        window.ShowSettings = () =>
        {
            var settings = new SettingsWindow();

            settings.ResetLayout.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            return Task.CompletedTask;
        };

        await window.ShowSettingsAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SvgViewerDock.Default, StudioSettings.Layout);
        Assert.Equal(SvgViewerDock.Default, board.Layout);

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

        Assert.Equal(SvgViewerDock.Default, StudioSettings.Layout);
        Assert.Equal(1, applied);
    }
}
