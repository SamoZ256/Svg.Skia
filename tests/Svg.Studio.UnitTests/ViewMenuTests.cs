// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Svg.Editor.Skia;
using Svg.Viewer.Skia.Avalonia;
using Xunit;
using static Svg.Studio.UnitTests.Gestures;

namespace Svg.Studio.UnitTests;

/// <summary>The View menu's rulers and guides, from the settings file to every tab and board.</summary>
[Collection("settings")]
public class ViewMenuTests : IDisposable
{
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect width="24" height="24" fill="#00ff00" />
        </svg>
        """;

    /// <summary>Two drawings, the second's user units starting four to the left of its page.</summary>
    private const string Project = """
        <studio namespace="Demo.Icons">
          <drawing name="home" class="Home">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect id="home" x="4" y="4" width="8" height="8" fill="#00ff00" />
            </svg>
          </drawing>
          <drawing name="badge" class="Badge">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="-4 0 24 24" width="24" height="24">
              <rect id="badge" x="0" y="14" width="8" height="8" fill="#0000ff" />
            </svg>
          </drawing>
        </studio>
        """;

    private static readonly RawInputModifiers Primary = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private readonly string _was = StudioSettings.Store;
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public ViewMenuTests() => StudioSettings.Store = Path.Combine(_directory, "settings");

    public void Dispose()
    {
        StudioSettings.Store = _was;

        Directory.Delete(_directory, recursive: true);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);

        if (name.EndsWith(".svgstudio", StringComparison.Ordinal))
        {
            return TestPackage.Write(path, text);
        }

        File.WriteAllText(path, text);

        return path;
    }

    private static async Task<MainWindow> Host(params string[] paths)
    {
        var window = new MainWindow { Announce = (_, _) => Task.CompletedTask };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        await window.OpenAsync(paths);
        Dispatcher.UIThread.RunJobs();

        window.Measure(new Size(900, 600));
        window.Arrange(new Rect(0, 0, 900, 600));
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    private static NativeMenuItem Item(MainWindow window, string header)
        => NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>().Single(item => item.Header == "View")
            .Menu!.Items.OfType<NativeMenuItem>().Single(entry => entry.Header == header);

    private static void Pick(NativeMenuItem item)
    {
        ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();
        Dispatcher.UIThread.RunJobs();
    }

    private static object? Front(MainWindow window)
        => ((TabItem)window.GetVisualDescendants().OfType<TabControl>().First(tabs => tabs.Name == "Tabs").SelectedItem!).Content;

    private static SvgViewerRulers Rulers(GroupPanel board) => board.GetVisualDescendants().OfType<SvgViewerRulers>().Single();

    [AvaloniaFact]
    public async Task Out_Of_The_Box_The_Rulers_Are_Off_And_Both_Kinds_Of_Guide_On()
    {
        var window = await Host(Write("one.svg", Drawing));
        var viewer = (SvgViewer)Front(window)!;

        Assert.False(viewer.Rulers.ShowsRulers);
        Assert.True(viewer.Canvas.ShowsGuides);
        Assert.True(viewer.SnapsToSmartGuides);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Each_Toggle_Is_A_Setting_Every_Tab_Follows()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var board = (GroupPanel)Front(window)!;

        Pick(Item(window, "Rulers"));
        Pick(Item(window, "Guides"));
        Pick(Item(window, "Smart Guides"));

        Assert.True(StudioSettings.Rulers);
        Assert.False(StudioSettings.Guides);
        Assert.False(StudioSettings.SmartGuides);
        Assert.True(Item(window, "Rulers").IsChecked);
        Assert.False(Item(window, "Guides").IsChecked);

        Assert.True(Rulers(board).ShowsRulers);
        Assert.False(Canvas(board).ShowsGuides);

        // And a drawing's tab opened after the change starts with it.
        await window.ShowAsync(window.Workspace!.Document.Root.Children[0]);
        Dispatcher.UIThread.RunJobs();

        var viewer = (SvgViewer)Front(window)!;

        Assert.True(viewer.Rulers.ShowsRulers);
        Assert.False(viewer.Canvas.ShowsGuides);
        Assert.False(viewer.SnapsToSmartGuides);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Guides_Answer_Their_Key()
    {
        var window = await Host(Write("one.svg", Drawing));

        window.FocusManager!.Focus(null);
        window.KeyPressQwerty(PhysicalKey.Semicolon, Primary);
        Dispatcher.UIThread.RunJobs();

        Assert.False(StudioSettings.Guides);

        window.Close();
    }

    /// <summary>A board's rulers count the drawing being read, and the board's own units before one is.</summary>
    [AvaloniaFact]
    public async Task A_Boards_Rulers_Count_The_Drawing_Being_Read()
    {
        StudioSettings.Rulers = true;

        var window = await Host(Write("icons.svgstudio", Project));
        var board = (GroupPanel)Front(window)!;
        var canvas = Canvas(board);
        var badge = Placed(canvas, "badge");

        Assert.Equal(0f, Rulers(board).Units.TransX, 3);

        Click(window, canvas, badge.At.X + 12f, badge.At.Y + 12f);

        // The badge's zero is four in from its page's left edge.
        Assert.Equal(badge.At.X + SvgViewerCanvas.Extent(badge)!.Value.Left + 4f, Rulers(board).Units.TransX, 3);
        Assert.Equal(badge.At.Y, Rulers(board).Units.TransY, 3);

        window.Close();
    }

    /// <summary>A shape dragged on a board lines up with a shape in the drawing beside it.</summary>
    [AvaloniaFact]
    public async Task A_Shape_On_A_Board_Lines_Up_With_Its_Neighbours()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var board = (GroupPanel)Front(window)!;
        var canvas = Canvas(board);
        var home = Placed(canvas, "home");
        var badge = Placed(canvas, "badge");

        // The badge's shape starts 14 down its page; the drag leaves the home shape's top a pixel short of it.
        var top = badge.At.Y + 14f;
        var by = top - (home.At.Y + 4f) - (float)(1d / canvas.Scale);

        // The first click reads the drawing, the second picks the shape in it.
        Click(window, canvas, home.At.X + 8f, home.At.Y + 8f);
        Click(window, canvas, home.At.X + 8f, home.At.Y + 8f);

        Assert.Equal(home.At.X + 4f, canvas.Gizmo!.Value.TL.X, 2);

        Press(window, canvas, Over(canvas, home.At.X + 8f, home.At.Y + 8f));
        Move(window, canvas, Over(canvas, home.At.X + 8f, home.At.Y + 8f + by));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(canvas.Aligned ?? Array.Empty<SvgEditorGuide>(), guide => guide.Origin.Y == top);

        Release(window, canvas, Over(canvas, home.At.X + 8f, home.At.Y + 8f + by));

        Assert.Null(canvas.Aligned);

        Assert.Equal(top - home.At.Y, Number(((ProjectDrawing)window.Workspace!.Document.Root.Children[0]).Text, "y"), 2);

        window.Close();
    }

    /// <summary>A drawing carried about a board lands on a ruler guide, as it does on the grid.</summary>
    [AvaloniaFact]
    public async Task A_Drawing_Carried_On_A_Board_Lands_On_A_Guide()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var board = (GroupPanel)Front(window)!;
        var canvas = Canvas(board);
        var home = Placed(canvas, "home");
        var line = home.At.X + 30f;

        canvas.Guides = new[] { new SvgEditorGuide("guide", new SKPoint(line, 0f), new SKPoint(0f, 1f)) };
        Dispatcher.UIThread.RunJobs();

        var by = 30f - (float)(1d / canvas.Scale);

        Drag(window, canvas, Over(canvas, home.At.X, home.At.Y + 6f), Over(canvas, home.At.X + by, home.At.Y + 6f));

        Assert.Equal(line, Placed(canvas, "home").At.X, 2);

        window.Close();
    }

    /// <summary>The drawing on the board holding the shape called <paramref name="id"/>; a board orders them by name.</summary>
    private static SvgViewerPlacement Placed(SvgViewerCanvas canvas, string id)
        => canvas.Placements.Single(placed => placed.Svg.SourceDocument?.GetElementById(id) is { });

    private static void Click(MainWindow window, SvgViewerCanvas canvas, float x, float y)
    {
        Press(window, canvas, Over(canvas, x, y));
        Release(window, canvas, Over(canvas, x, y));
    }

    private static float Number(string text, string name)
    {
        var at = text.IndexOf($" {name}=\"", StringComparison.Ordinal) + name.Length + 3;

        return float.Parse(text[at..text.IndexOf('"', at)], CultureInfo.InvariantCulture);
    }
}
