// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// Naming the drawings on a board.
/// </summary>
/// <remarks>
/// A caption is written into the placement when the board is laid out rather than drawn from a
/// flag every frame, so switching it lays the board out again — which is the half of this most
/// likely to go wrong, and the reason the zoom is asserted across the toggle.
///
/// In the settings collection because it repoints the one static store, like every other class that
/// writes a setting.
/// </remarks>
[Collection("settings")]
public class BoardCaptionTests : IDisposable
{
    private const string Project = """
        <studio namespace="Demo.Icons">

          <drawing name="home" class="Home">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#00ff00" />
            </svg>
          </drawing>

          <drawing name="badge" class="Badge">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#0000ff" />
            </svg>
          </drawing>

        </studio>
        """;

    /// <summary>Five rows nobody has placed, so the spread is three columns and two of them.</summary>
    private static string Rows() => $"""
        <studio namespace="Demo.Icons">
        {Holding("one")}
        {Holding("two")}
        {Holding("three")}
        {Holding("four")}
        {Holding("five")}
        </studio>
        """;

    private static string Holding(string name) => $"""
          <drawing name="{name}">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#00ff00" />
            </svg>
          </drawing>
        """;

    private readonly string _was = StudioSettings.Store;
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public BoardCaptionTests() => StudioSettings.Store = Path.Combine(_directory, "settings");

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

    private static GroupPanel Board(MainWindow window)
        => (GroupPanel)((TabItem)Tabs(window).SelectedItem!).Content!;

    private static SvgViewerCanvas Canvas(GroupPanel panel)
        => panel.GetVisualDescendants().OfType<SvgViewerCanvas>().Single();

    private static IReadOnlyList<SvgViewerPlacement> Drawn(GroupPanel panel) => Canvas(panel).Placements;

    /// <summary>The board's own captions toggle.</summary>
    private static ToggleButton Toggle(GroupPanel panel)
        => panel.GetVisualDescendants()
            .OfType<ToggleButton>()
            .Single(button => Equals(button.Content, "Captions"));

    /// <summary>Where a placement sits in the arrangement, as a rectangle.</summary>
    private static SKRect Area(SvgViewerPlacement placement)
    {
        var cull = placement.Svg.Picture!.CullRect;

        return new SKRect(
            placement.At.X + cull.Left,
            placement.At.Y + cull.Top,
            placement.At.X + cull.Right,
            placement.At.Y + cull.Bottom);
    }

    /// <summary>A board names what is on it until somebody says otherwise.</summary>
    /// <remarks>
    /// The row's own name and nothing else. What used to be written was that and the class and the
    /// size over two lines, in room the arrangement kept free for it — which is what made a board of
    /// icons something to read rather than to look at.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Board_Names_Its_Drawings()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        Assert.Equal(
            new[] { "home", "badge" },
            Drawn(Board(window)).Select(placed => placed.Label).ToArray());

        Assert.True(Toggle(Board(window)).IsChecked);

        window.Close();
    }

    /// <summary>The toggle takes them away and brings them back, without moving the view.</summary>
    /// <remarks>
    /// A lay-out that fitted would throw away wherever the reader had got to, which is the half of
    /// this most likely to regress: the caption is not a render flag, so the toggle really does lay
    /// the board out again.
    /// </remarks>
    [AvaloniaFact]
    public async Task Captions_Go_When_The_Toggle_Does()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var panel = Board(window);

        var scale = Canvas(panel).Scale;

        Toggle(panel).IsChecked = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(StudioSettings.DrawingCaptions);
        Assert.All(Drawn(panel), placed => Assert.Null(placed.Label));
        Assert.Equal(scale, Canvas(panel).Scale);

        Toggle(panel).IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            new[] { "home", "badge" },
            Drawn(panel).Select(placed => placed.Label).ToArray());

        window.Close();
    }

    /// <summary>Naming the drawings moves none of them.</summary>
    /// <remarks>
    /// The whole of what a name now costs, which is nothing. It used to widen every column to hold
    /// the writing and keep two lines free under every row, so a board that named what was on it
    /// sprawled — and that, rather than the names, is what made it something to switch off. A name
    /// is written inside the page at whatever size fits across it, so the arrangement never hears
    /// about it.
    /// </remarks>
    [AvaloniaFact]
    public async Task Naming_The_Drawings_Moves_None_Of_Them()
    {
        var window = await Host(Write("icons.svgstudio", Rows()));
        var panel = Board(window);

        var named = Drawn(panel).Select(Area).ToList();

        Toggle(panel).IsChecked = false;
        Dispatcher.UIThread.RunJobs();

        var bare = Drawn(panel).Select(Area).ToList();

        Assert.Equal(named.Count, bare.Count);

        for (var index = 0; index < named.Count; index++)
        {
            Assert.Equal(named[index].Left, bare[index].Left, 3);
            Assert.Equal(named[index].Top, bare[index].Top, 3);
        }

        window.Close();
    }

    /// <summary>The settings box, the board's toggle and every other tab are one switch.</summary>
    [AvaloniaFact]
    public async Task The_Settings_Box_Is_The_Same_Switch()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var panel = Board(window);

        var settings = new SettingsWindow();

        settings.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(settings.DrawingCaptions.IsChecked);

        settings.DrawingCaptions.IsChecked = false;
        Dispatcher.UIThread.RunJobs();

        settings.Close();

        Assert.False(StudioSettings.DrawingCaptions);

        // Through the window's own seam, since a headless run cannot show a modal: what the settings
        // window does to the setting is tested on the settings window.
        window.ShowSettings = () => Task.CompletedTask;

        await window.ShowSettingsAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(Toggle(panel).IsChecked);
        Assert.All(Drawn(panel), placed => Assert.Null(placed.Label));

        window.Close();
    }
}
