// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>History: what Undo would take back, a click on a row taking back down to it.</summary>
[Collection("settings")]
public class HistoryPanelTests : IDisposable
{
    private const string Project = """
        <studio namespace="Demo.Icons">
          <drawing name="home" class="Home">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#00ff00" />
            </svg>
          </drawing>
        </studio>
        """;

    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect width="24" height="24" fill="#3366cc" />
        </svg>
        """;

    private readonly string _was = StudioSettings.Store;
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public HistoryPanelTests() => StudioSettings.Store = Path.Combine(_directory, "settings");

    public void Dispose()
    {
        StudioSettings.Store = _was;

        Directory.Delete(_directory, recursive: true);
    }

    private async Task<(MainWindow Window, SvgViewer Viewer)> Host()
    {
        var path = Path.Combine(_directory, "icons.svgstudio");

        TestPackage.Write(path, Project);

        var window = new MainWindow { Announce = (_, _) => Task.CompletedTask };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        await window.OpenAsync(new[] { path });
        await window.ShowAsync(window.Workspace!.Document.Root.Children[0]);
        Dispatcher.UIThread.RunJobs();

        return (window, (SvgViewer)((TabItem)window.FindControl<TabControl>("Tabs")!.SelectedItem!).Content!);
    }

    /// <summary>What the panel says, a heading and then each row, in order.</summary>
    private static string[] Listed(HistoryPanel history)
        => history.GetLogicalDescendants()
            .Select(found => found switch
            {
                Button { Content: string row } => row,
                TextBlock { Text: { } heading, Parent: StackPanel } => "# " + heading,
                _ => null
            })
            .OfType<string>()
            .ToArray();

    private static void Click(HistoryPanel history, string row)
    {
        history.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, row))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Edit(SvgViewer viewer, string label)
    {
        Assert.True(viewer.SetSource(viewer.Source + $"<!-- {label} -->", label));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task History_Lists_The_Drawing_Then_The_Project()
    {
        var (window, viewer) = await Host();

        window.Workspace!.Edit("one");
        Edit(viewer, "a");
        Edit(viewer, "b");

        Assert.Equal(new[] { "# Drawing", "b", "a", "# Project", "one" }, Listed(window.History));

        window.Close();
    }

    /// <summary>A row is that many presses of Undo: the drawing's steps first, then the project's.</summary>
    [AvaloniaFact]
    public async Task A_Click_Takes_Back_Down_To_That_Row()
    {
        var (window, viewer) = await Host();

        window.Workspace!.Edit("one");
        window.Workspace!.Edit("two");
        Edit(viewer, "a");
        Edit(viewer, "b");
        Edit(viewer, "c");

        Click(window.History, "b");

        Assert.Equal(new[] { "a" }, viewer.UndoLabels);
        Assert.Equal(new[] { "b", "c" }, viewer.RedoLabels);

        Click(window.History, "two");

        Assert.Empty(viewer.UndoLabels);
        Assert.Equal(new[] { "one" }, window.Workspace.UndoLabels);
        Assert.Equal(new[] { "# Project", "one" }, Listed(window.History));

        window.Close();
    }

    /// <summary>A list made before something else was done is read again rather than counted from.</summary>
    [AvaloniaFact]
    public async Task A_Row_From_A_Stale_List_Does_Nothing()
    {
        var (window, viewer) = await Host();

        Edit(viewer, "a");

        // On its own, so nothing tells it of the next edit.
        var history = new HistoryPanel { Drawing = () => viewer };

        history.Refresh();
        Edit(viewer, "b");

        Click(history, "a");

        Assert.Equal(new[] { "b", "a" }, viewer.UndoLabels);
        Assert.Equal(new[] { "# Drawing", "b", "a" }, Listed(history));

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_List_Follows_Edits_And_The_Front_Tab()
    {
        var (window, viewer) = await Host();

        window.Workspace!.Edit("one");
        Edit(viewer, "a");

        Assert.Contains("a", Listed(window.History));

        // Undone by key rather than from the list, which is told all the same.
        Assert.True(window.Undo());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "# Project", "one" }, Listed(window.History));

        Edit(viewer, "b");
        await window.OpenAsync(new[] { Write("other.svg") });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "# Project", "one" }, Listed(window.History));

        // A drawing of no project, whose edits only its own tab hears of: the second, since the first
        // also marks the tab unsaved, which says something has changed by another route.
        var other = (SvgViewer)((TabItem)window.FindControl<TabControl>("Tabs")!.SelectedItem!).Content!;

        Edit(other, "x");
        Edit(other, "y");

        Assert.Equal(new[] { "# Drawing", "y", "x", "# Project", "one" }, Listed(window.History));

        window.FindControl<TabControl>("Tabs")!.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "# Drawing", "b", "# Project", "one" }, Listed(window.History));

        window.Close();
    }

    private string Write(string name)
    {
        var path = Path.Combine(_directory, name);

        File.WriteAllText(path, Drawing);

        return path;
    }
}
