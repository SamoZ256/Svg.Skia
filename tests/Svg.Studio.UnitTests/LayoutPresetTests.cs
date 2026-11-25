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
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>The Drawing, Board and Review layouts, and the control bar's picker that names the one in use.</summary>
/// <remarks>In the settings collection because choosing a layout writes it.</remarks>
[Collection("settings")]
public class LayoutPresetTests : IDisposable
{
    /// <summary>The panels Studio supplies, so a dock built here settles exactly as the window's does.</summary>
    private static readonly string[] Supplied = { "tree", "changes", "history", "assistant", "project", "variables", "element", "elements", "streamline" };

    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect width="24" height="24" fill="#00ff00" />
        </svg>
        """;

    private readonly string _was = StudioSettings.Store;
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public LayoutPresetTests() => StudioSettings.Store = Path.Combine(_directory, "settings");

    public void Dispose()
    {
        StudioSettings.Store = _was;

        Directory.Delete(_directory, recursive: true);
    }

    private static SvgViewerDock Dock(string line) => new(new Border())
    {
        Fallback = StudioSettings.DefaultLayout,
        Regions = Supplied.Select(id => new SvgViewerRegion(id, id, new Border())).ToList(),
        Layout = line
    };

    /// <summary>Each names every panel, in the dock's own spelling, so settling one adds and rewrites nothing.</summary>
    [AvaloniaFact]
    public void Every_Preset_Reads_Back_As_Written()
    {
        foreach (var (_, line) in StudioSettings.Layouts)
        {
            Assert.Equal(line, Dock(line).Layout);
        }
    }

    /// <summary>Each is only itself, with the project's panels on the arrangement or off it.</summary>
    [AvaloniaFact]
    public void Presets_Are_Told_Apart()
    {
        foreach (var project in new[] { true, false })
        {
            foreach (var (name, line) in StudioSettings.Layouts)
            {
                var dock = Dock(line);

                if (!project)
                {
                    dock.Show("changes", false);
                    dock.Show("tree", false);
                }

                foreach (var (other, otherLine) in StudioSettings.Layouts)
                {
                    Assert.True(dock.Matches(otherLine) == (name == other), $"{name}{(project ? string.Empty : " with no project")} read as {other}");
                }
            }
        }
    }

    private static Point Middle(Window window, Visual visual)
        => visual.TranslatePoint(new Point(visual.Bounds.Width / 2d, visual.Bounds.Height / 2d), window)!.Value;

    private async Task<MainWindow> Host(string name, string text)
    {
        var path = Path.Combine(_directory, name);

        if (name.EndsWith(".svgstudio", StringComparison.Ordinal))
        {
            TestPackage.Write(path, text);
        }
        else
        {
            File.WriteAllText(path, text);
        }

        var window = new MainWindow { Announce = (_, _) => Task.CompletedTask };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        await window.OpenAsync(new[] { path });
        window.Measure(new Size(1200, 700));
        window.Arrange(new Rect(0, 0, 1200, 700));
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    /// <summary>In the window Studio opens at, the Drawing layout leaves most of the height to the drawing.</summary>
    /// <remarks>Streamline open under it took a third, and left the drawing a fifth of the window.</remarks>
    [AvaloniaFact]
    public async Task The_Drawing_Has_Most_Of_The_Window()
    {
        var window = await Host("one.svg", Drawing);

        window.Width = 1180;
        window.Height = 760;
        window.Measure(new Size(1180, 760));
        window.Arrange(new Rect(0, 0, 1180, 760));
        Dispatcher.UIThread.RunJobs();

        var canvas = ((SvgViewer)((TabItem)window.FindControl<TabControl>("Tabs")!.SelectedItem!).Content!).Canvas;

        Assert.True(canvas.Bounds.Height > 0.7d * 760d, $"the drawing is {canvas.Bounds.Height} of 760 high");

        window.Close();
    }

    /// <summary>The picker naming a saved arrangement does not put the layout's own sizes over it.</summary>
    [AvaloniaFact]
    public async Task Naming_A_Layout_Leaves_Its_Sizes_Alone()
    {
        var resized = StudioSettings.DefaultLayout.Replace("/312px", "/400px", StringComparison.Ordinal);

        StudioSettings.Layout = resized;

        var window = await Host("one.svg", Drawing);

        Assert.Equal("Drawing", window.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "Layouts").SelectedItem);
        Assert.Equal(resized, StudioSettings.Layout);
        Assert.Contains("/400px", window.Layout, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>A project opens on its tree, in the Drawing layout still, from a line saved while there was none.</summary>
    /// <remarks>
    /// Such a line has History alone in the tree's run and on top of it; the tree and Changes go back in
    /// front of it, in their places.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Project_Opens_On_Its_Tree_In_The_Layout_It_Was()
    {
        StudioSettings.Layout = StudioSettings.DefaultLayout.Replace("tree+changes+history/1.4/tree/open", "history/1.4/history/open", StringComparison.Ordinal);

        var window = await Host("icons.svgstudio", """
            <studio namespace="Demo.Icons">
              <drawing name="home" class="Home">
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24"><rect width="24" height="24" /></svg>
              </drawing>
            </studio>
            """);

        Assert.StartsWith("row(col(tree+changes+history/1.4/tree/open,", window.Layout, StringComparison.Ordinal);
        Assert.True(window.Registry["layout-drawing"].IsChecked!());

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Picker_Names_The_Layout_In_Use()
    {
        var path = Path.Combine(_directory, "one.svg");

        File.WriteAllText(path, Drawing);

        var window = new MainWindow { Announce = (_, _) => Task.CompletedTask };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        await window.OpenAsync(new[] { path });
        window.Measure(new Size(1200, 700));
        window.Arrange(new Rect(0, 0, 1200, 700));
        Dispatcher.UIThread.RunJobs();

        var picker = window.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "Layouts");

        // With no project, so without the tree and Changes on it.
        Assert.Equal("Drawing", picker.SelectedItem);

        // Opened with the pointer and never left holding the keyboard: a tool letter typed afterwards is
        // the drawing's, not the picker's search for a layout starting with it.
        window.MouseDown(Middle(window, picker), MouseButton.Left);
        window.MouseUp(Middle(window, picker), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        picker.IsDropDownOpen = false;
        Dispatcher.UIThread.RunJobs();

        Assert.IsNotType<ComboBox>(window.FocusManager!.GetFocusedElement());

        window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);
        window.KeyTextInput("r");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(StudioSettings.DefaultLayout, StudioSettings.Layout);

        Assert.True(await window.Registry.Run("layout-board"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(StudioSettings.BoardLayout, StudioSettings.Layout);
        Assert.Equal("Board", picker.SelectedItem);
        Assert.True(window.Registry["layout-board"].IsChecked!());
        Assert.False(window.Registry["layout-drawing"].IsChecked!());

        // Moved somewhere no layout puts it, the arrangement is none of them.
        window.GetVisualDescendants().OfType<Border>()
            .First(tab => tab.Classes.Contains("pane") && tab.Child is TextBlock { Text: "Variables" })
            .ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Move Below"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(-1, picker.SelectedIndex);
        Assert.Equal("Custom", picker.PlaceholderText);

        // And one chosen in the picker is put on.
        picker.SelectedItem = "Review";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(StudioSettings.ReviewLayout, StudioSettings.Layout);
        Assert.Equal("Review", picker.SelectedItem);

        window.Close();
    }
}
