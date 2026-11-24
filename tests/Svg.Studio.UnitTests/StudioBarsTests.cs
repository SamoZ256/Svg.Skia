// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Svg.Viewer.Skia.Avalonia;
using Xunit;
using static Svg.Studio.UnitTests.Gestures;

namespace Svg.Studio.UnitTests;

/// <summary>The window's control bar and status bar, which say what the tab in front is.</summary>
/// <remarks>In the settings collection because the toggles write settings.</remarks>
[Collection("settings")]
public class StudioBarsTests : IDisposable
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

    /// <summary>Twice as big on the page as in its own units, with room on the page around its shapes.</summary>
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="48" height="48">
          <rect id="a" x="2" y="2" width="6" height="6" fill="#ff0000" />
          <rect id="b" x="10" y="2" width="6" height="6" fill="#0000ff" />
        </svg>
        """;

    /// <summary>A group whose block one drawing reads from, for a board to count.</summary>
    private const string Grouped = """
        <studio namespace="Demo.Icons">
          <group name="Badges">
            <e:code xmlns:e="https://svg.skia/expr/1.0">
              <e:param name="tint" type="color" default="#ff0000" />
            </e:code>

            <drawing name="filled">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
                <rect width="24" height="24" fill="{{ tint }}" />
              </svg>
            </drawing>
          </group>
        </studio>
        """;

    private const string Parametric = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code>
              <e:param name="fade" type="number" default="1" min="0" max="1" />
            </e:code>
          </defs>
          <rect width="24" height="24" fill="#ff0000" opacity="{{ fade }}" />
        </svg>
        """;

    private readonly string _was = StudioSettings.Store;
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public StudioBarsTests() => StudioSettings.Store = Path.Combine(_directory, "settings");

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

        window.Measure(new Size(1200, 700));
        window.Arrange(new Rect(0, 0, 1200, 700));
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    private static TabControl Tabs(MainWindow window)
        => window.GetVisualDescendants().OfType<TabControl>().First(tabs => tabs.Name == "Tabs");

    private static Control Front(MainWindow window) => (Control)((TabItem)Tabs(window).SelectedItem!).Content!;

    private static Border Bar(MainWindow window, string name)
        => window.GetVisualDescendants().OfType<Border>().Single(bar => bar.Name == name);

    private static T In<T>(MainWindow window, string bar, string content)
        where T : Button
        => Bar(window, bar).GetVisualDescendants().OfType<T>().Single(button => Equals(button.Content, content));

    private static string Said(MainWindow window, string name)
        => window.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == name).Text ?? string.Empty;

    private static TextBlock Note(MainWindow window)
        => window.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "Note");

    /// <summary>A brush the window resolves, which is only there while Studio's chrome merges the viewer's own.</summary>
    private static object Brush(MainWindow window, string key)
    {
        Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out var brush), $"{key} is not reachable from the window");

        return brush!;
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static NativeMenuItem Item(MainWindow window, string menu, string item)
        => NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>().Single(entry => entry.Header == menu)
            .Menu!.Items.OfType<NativeMenuItem>().Single(entry => entry.Header == item);

    private static void Pick(NativeMenuItem item)
    {
        ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The welcome screen stands in for the whole editor, the bars included.</summary>
    [AvaloniaFact]
    public async Task The_Bars_Wait_For_Something_To_Be_Open()
    {
        var window = new MainWindow { Announce = (_, _) => Task.CompletedTask };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(Bar(window, "ControlBar").IsVisible);
        Assert.False(Bar(window, "StatusBar").IsVisible);

        await window.OpenAsync(new[] { Write("one.svg", Drawing) });
        Dispatcher.UIThread.RunJobs();

        Assert.True(Bar(window, "ControlBar").IsVisible);
        Assert.True(Bar(window, "StatusBar").IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Bars_Follow_The_Front_Tab()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        Assert.IsType<GroupPanel>(Front(window));
        Assert.True(In<ToggleButton>(window, "ControlBar", "Captions").IsEnabled);
        Assert.False(In<Button>(window, "ControlBar", "Reset parameters").IsEnabled);
        Assert.StartsWith("Project — ", Said(window, "Status"), StringComparison.Ordinal);

        await window.ShowAsync(window.Workspace!.Document.Root.Children[0]);
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<SvgViewer>(Front(window));
        Assert.False(In<ToggleButton>(window, "ControlBar", "Captions").IsEnabled);
        Assert.True(In<Button>(window, "ControlBar", "Reset parameters").IsEnabled);
        Assert.StartsWith("home — ", Said(window, "Status"), StringComparison.Ordinal);

        // The project stays open with no tab in front, and there is nothing left to say.
        while (Tabs(window).ItemCount > 0)
        {
            Assert.True(await window.Registry.Run("close"));
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(string.Empty, Said(window, "Status"));
        Assert.Equal(string.Empty, Said(window, "Selection"));

        window.Close();
    }

    /// <summary>A board counts what its Variables panel lists, and counts again once an edit has rebuilt it.</summary>
    /// <remarks>The window hears of a project edit before the board rebuilds its rows, so a count read then is the old one.</remarks>
    [AvaloniaFact]
    public async Task A_Board_Counts_Its_Parameters_After_An_Edit()
    {
        var window = await Host(Write("icons.svgstudio", Grouped));
        var workspace = window.Workspace!;
        var group = (ProjectGroup)workspace.Document.Root.Children[0];
        var drawing = (ProjectDrawing)group.Children[0];

        await window.ShowAsync(group);
        Dispatcher.UIThread.RunJobs();

        Assert.StartsWith("Badges — 1 parameter · ", Said(window, "Status"), StringComparison.Ordinal);

        workspace.Do(
            "add fade",
            () => ProjectSnapshot.All(ProjectSnapshot.Code(group), ProjectSnapshot.Text(drawing)),
            () =>
            {
                group.SetCode(group.CodeText.Replace("</e:code>", """<e:param name="fade" type="number" default="1" /></e:code>""", StringComparison.Ordinal));
                drawing.SetText(drawing.Text.Replace("fill=\"{{ tint }}\"", "fill=\"{{ tint }}\" opacity=\"{{ fade }}\"", StringComparison.Ordinal));
            });
        Dispatcher.UIThread.RunJobs();

        Assert.StartsWith("Badges — 2 parameters · ", Said(window, "Status"), StringComparison.Ordinal);

        Assert.True(window.Undo());
        Dispatcher.UIThread.RunJobs();

        Assert.StartsWith("Badges — 1 parameter · ", Said(window, "Status"), StringComparison.Ordinal);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Reset_Parameters_Puts_The_Front_Drawings_Values_Back()
    {
        var window = await Host(Write("one.svg", Parametric));
        var fade = ((SvgViewer)Front(window)).Parameters.OfType<SvgViewerNumberParameter>().Single();

        fade.Value = 0.25d;
        Dispatcher.UIThread.RunJobs();

        Click(In<Button>(window, "ControlBar", "Reset parameters"));

        Assert.Equal(1d, fade.Value);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Bar_Toggle_Is_The_Menu_Item()
    {
        var window = await Host(Write("one.svg", Drawing));
        var snap = Item(window, "View", "Snap to Grid");

        Click(In<ToggleButton>(window, "ControlBar", "Snap"));

        Assert.True(StudioSettings.SnapToGrid);
        Assert.True(snap.IsChecked);
        Assert.True(In<ToggleButton>(window, "StatusBar", "Snap").IsChecked);

        Pick(snap);

        Assert.False(StudioSettings.SnapToGrid);
        Assert.False(In<ToggleButton>(window, "ControlBar", "Snap").IsChecked);
        Assert.False(In<ToggleButton>(window, "StatusBar", "Snap").IsChecked);

        window.Close();
    }

    /// <summary>The lock is each tab's own and has no event, so the bars read it, and the rest, again whenever the front tab changes.</summary>
    [AvaloniaFact]
    public async Task Lock_Ratio_And_The_Status_Are_The_Front_Tabs()
    {
        var window = await Host(Write("one.svg", Drawing), Write("two.svg", Drawing));
        var ratio = In<ToggleButton>(window, "ControlBar", "Lock ratio");
        var second = (SvgViewer)Front(window);

        Click(ratio);

        Assert.True(second.LocksAspectRatio);
        Assert.True(ratio.IsChecked);

        Tabs(window).SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        Assert.False(ratio.IsChecked);
        Assert.StartsWith("one.svg — ", Said(window, "Status"), StringComparison.Ordinal);

        Tabs(window).SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        Assert.True(ratio.IsChecked);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Typing_A_Zoom_Zooms_About_The_Middle()
    {
        var window = await Host(Write("one.svg", Drawing));
        var canvas = ((SvgViewer)Front(window)).Canvas;
        var zoom = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "Zoom");
        var middle = new Point(canvas.Bounds.Width / 2d, canvas.Bounds.Height / 2d);

        Assert.True(canvas.TryGetDrawingPoint(middle, out var before));

        Type(zoom, "250%");

        Assert.Equal(2.5d, canvas.Scale, 6);
        Assert.True(canvas.TryGetDrawingPoint(middle, out var after));
        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);
        Assert.Equal((250d).ToString("0", CultureInfo.CurrentCulture) + "%", zoom.Text);

        Type(zoom, "abc");

        Assert.Equal(2.5d, canvas.Scale, 6);
        Assert.Equal(Percent(canvas), zoom.Text);

        // The box follows the view however it was zoomed.
        Click(In<Button>(window, "StatusBar", "+"));
        Assert.Equal(Percent(canvas), zoom.Text);

        // What is being typed is not overwritten under the caret; Escape or leaving the box puts it back.
        zoom.Focus();
        zoom.Text = "25";
        Assert.True(((SvgViewer)Front(window)).Elements.TrySelect("0"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("25", zoom.Text);

        zoom.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Percent(canvas), zoom.Text);

        zoom.Focus();
        zoom.Text = "25";
        canvas.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Percent(canvas), zoom.Text);

        window.Close();

        static string Percent(SvgViewerCanvas canvas) => (canvas.Scale * 100d).ToString("0", CultureInfo.CurrentCulture) + "%";

        static void Type(TextBox box, string text)
        {
            box.Text = text;
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>A zoom half typed for one tab is not left in the box for the next one to take.</summary>
    [AvaloniaFact]
    public async Task A_Zoom_Typed_For_One_Tab_Is_Not_The_Next_Ones()
    {
        var window = await Host(Write("one.svg", Drawing), Write("two.svg", Drawing));
        var zoom = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "Zoom");

        zoom.Focus();
        zoom.Text = "900";

        Tabs(window).SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        var canvas = ((SvgViewer)Front(window)).Canvas;

        Assert.Equal((canvas.Scale * 100d).ToString("0", CultureInfo.CurrentCulture) + "%", zoom.Text);

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Selection_Is_Named()
    {
        var window = await Host(Write("one.svg", Drawing));
        var viewer = (SvgViewer)Front(window);

        Assert.Equal("No selection", Said(window, "Selection"));

        Assert.True(viewer.Elements.TrySelect("0"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("rect #a", Said(window, "Selection"));

        Assert.True(viewer.Elements.TrySelect(new[] { "0", "1" }));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("2 objects", Said(window, "Selection"));

        // The page is picked by a click on it where nothing is drawn, and from nothing selected the
        // click is all that says so.
        Assert.True(viewer.Elements.TrySelect(Array.Empty<string>()));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("No selection", Said(window, "Selection"));

        Assert.True(viewer.Canvas.TryGetControlPoint(new SKPoint(40f, 40f), out var bare));
        Press(window, viewer.Canvas, bare);
        Release(window, viewer.Canvas, bare);
        Assert.True(viewer.IsPageSelected);
        Assert.Equal("Page", Said(window, "Selection"));

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Board_Names_What_It_Picked()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var board = (GroupPanel)Front(window);
        var canvas = Canvas(board);
        var placed = canvas.Placements[0];

        Assert.Equal("No selection", Said(window, "Selection"));

        var at = Over(canvas, placed.At.X + 12f, placed.At.Y + 12f);

        Press(window, canvas, at);
        Release(window, canvas, at);

        Assert.Equal("home", Said(window, "Selection"));

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Pointer_Is_Read_In_User_Units()
    {
        var window = await Host(Write("one.svg", Drawing));
        var canvas = ((SvgViewer)Front(window)).Canvas;

        // The page is 48 wide for a viewBox 24 wide, so 24 on the page is 12 in the drawing's own units.
        Assert.True(canvas.TryGetControlPoint(new SKPoint(24f, 12f), out var at));
        Move(window, canvas, at);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"X {12f:0.0}  Y {6f:0.0}"), Said(window, "Pointer"));

        // The view moved under a pointer standing still: a pan of 24 pixels is 12 page units at this zoom only by chance, so it is read back.
        canvas.Shift(-24d, 0d);
        Dispatcher.UIThread.RunJobs();

        Assert.True(canvas.TryGetPlacementAt(at, out _, out var under));
        Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"X {under.X / 2f:0.0}  Y {under.Y / 2f:0.0}"), Said(window, "Pointer"));

        canvas.RaiseEvent(new PointerEventArgs(InputElement.PointerExitedEvent, canvas, new Pointer(0, PointerType.Mouse, true), window, at, 0, new PointerPointProperties(), KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(string.Empty, Said(window, "Pointer"));

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Armed_Tool_And_Its_Use_Are_Said()
    {
        var window = await Host(Write("one.svg", Drawing));
        var viewer = (SvgViewer)Front(window);

        viewer.Drafting.Arm("rect");
        Dispatcher.UIThread.RunJobs();

        Assert.EndsWith(" · Rectangle: drag a box; Shift for a square", Said(window, "Status"), StringComparison.Ordinal);

        viewer.Drafting.Arm("points");
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(" · Points: ", Said(window, "Status"), StringComparison.Ordinal);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Tabs_Note_Is_On_The_Status_Bar()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var board = (GroupPanel)Front(window);
        var canvas = Canvas(board);
        var placed = canvas.Placements[0];

        // A tool pressed beside every drawing has nothing to draw into, and the board says so.
        board.Drafting.Arm("rect");
        var off = Over(canvas, placed.At.X + 60f, placed.At.Y + 60f);

        // Before the release, which says everything again of its own accord.
        Press(window, canvas, off);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Press on a drawing to draw into it.", board.Notice);
        Assert.Equal(board.Notice, Said(window, "Note"));

        Release(window, canvas, off);

        // Said once, on the window's bar, and not on a line of the board's own as well.
        Assert.DoesNotContain(board.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == board.Notice);

        await window.OpenAsync(new[] { Write("one.svg", Drawing) });
        Dispatcher.UIThread.RunJobs();

        var viewer = Assert.IsType<SvgViewer>(Front(window));

        Assert.Equal(string.Empty, Said(window, "Note"));

        // An edit rather than a load, which tells the window nothing but what the viewer says.
        Assert.True(viewer.SetSource(viewer.Source.Replace("</svg>", "<rekt /></svg>", StringComparison.Ordinal)));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("1 warning", viewer.Note);
        Assert.Equal("1 warning", Said(window, "Note"));
        Assert.Same(Brush(window, "SvgViewerSourceWarningBrush"), Note(window).Foreground);

        Assert.True(viewer.SetSource(viewer.Source.Replace("<rekt />", "<rekt /><rect width=\"abc\" height=\"1\" />", StringComparison.Ordinal)));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("1 error and 1 warning", Said(window, "Note"));
        Assert.Same(Brush(window, "SvgViewerSourceErrorBrush"), Note(window).Foreground);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Tab_Has_No_Chrome_Of_Its_Own()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var board = Assert.IsType<GroupPanel>(Front(window));

        Assert.DoesNotContain(board.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Lock ratio"));

        await window.ShowAsync(window.Workspace!.Document.Root.Children[0]);
        Dispatcher.UIThread.RunJobs();

        var viewer = Assert.IsType<SvgViewer>(Front(window));

        foreach (var name in new[] { "ToolBarPanel", "StatusPanel" })
        {
            Assert.False(viewer.GetVisualDescendants().OfType<Border>().Single(border => border.Name == name).IsEffectivelyVisible);
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task Search_Opens_Command_Search()
    {
        var window = await Host(Write("one.svg", Drawing));
        var search = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "CommandSearch");

        Assert.False(search.IsVisible);

        Click(In<Button>(window, "ControlBar", "Search"));

        Assert.True(search.IsVisible);

        window.Close();
    }
}
