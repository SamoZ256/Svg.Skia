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

    // ---- measuring and view modes ------------------------------------------------------------

    private static SvgViewer Viewer(MainWindow window) => (SvgViewer)Front(window)!;

    /// <summary>The panels the window's dock holds, which Focus Mode takes out of the window and puts back.</summary>
    private static bool Panelled(MainWindow window) => window.GetVisualDescendants().OfType<SvgViewerElementTree>().Any();

    [AvaloniaFact]
    public async Task Outline_And_The_Background_Are_Settings_Every_Tab_Follows()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var board = (GroupPanel)Front(window)!;

        Assert.True(Item(window, "Theme Background").IsChecked);

        Pick(Item(window, "Outline"));
        Pick(Item(window, "Checker Background"));

        Assert.True(StudioSettings.Outline);
        Assert.Equal(SvgViewerGround.Checker, StudioSettings.Ground);
        Assert.DoesNotContain("outline", File.ReadAllText(StudioSettings.Store), StringComparison.Ordinal);
        Assert.True(Canvas(board).Outlined);
        Assert.Equal(SvgViewerGround.Checker, Canvas(board).Ground);
        Assert.Equal(
            new[] { false, true, false, false },
            new[] { "Theme Background", "Checker Background", "Light Background", "Dark Background" }.Select(header => Item(window, header).IsChecked));

        await window.ShowAsync(window.Workspace!.Document.Root.Children[0]);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Viewer(window).Canvas.Outlined);
        Assert.Equal(SvgViewerGround.Checker, Viewer(window).Canvas.Ground);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Fit_Selection_Waits_For_Something_Picked_And_Fits_It()
    {
        var window = await Host(Write("one.svg", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">
              <rect x="10" y="10" width="10" height="10" fill="#3366cc" />
            </svg>
            """));
        var viewer = Viewer(window);

        Assert.False(window.Registry["fitselection"].CanExecute());

        Assert.True(viewer.Elements.TrySelect(new[] { "0" }));
        Dispatcher.UIThread.RunJobs();

        var scale = viewer.Canvas.Scale;

        Assert.True(await window.Registry.Run("fitselection"));

        Assert.True(viewer.Canvas.Scale > scale * 5d);
        Assert.True(viewer.Canvas.TryGetControlPoint(new SKPoint(15f, 15f), out var middle));
        Assert.Equal(viewer.Canvas.Bounds.Width / 2d, middle.X, 0);

        window.Close();
    }

    /// <summary>Focus Mode takes every panel away and puts each back where it was, without the arrangement ever changing.</summary>
    [AvaloniaFact]
    public async Task Focus_Mode_Hides_Every_Panel_And_Puts_Them_Back_As_They_Were()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var line = window.Layout;
        var saved = StudioSettings.Layout;

        Assert.True(Panelled(window));

        Assert.True(await window.Registry.Run("zen"));

        Assert.False(Panelled(window));
        Assert.True(Item(window, "Focus Mode").IsChecked);
        Assert.Equal(line, window.Layout);

        Assert.True(await window.Registry.Run("zen"));

        Assert.True(Panelled(window));
        Assert.Equal(line, window.Layout);
        Assert.Equal(saved, StudioSettings.Layout);

        window.Close();
    }

    /// <summary>F on the canvas is Focus Mode; in a box it is an F.</summary>
    [AvaloniaFact]
    public async Task F_Is_Focus_Mode_On_The_Canvas_And_A_Letter_In_A_Box()
    {
        var window = await Host(Write("one.svg", Drawing));

        Viewer(window).Canvas.Focus();
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(Panelled(window));

        var box = window.GetVisualDescendants().OfType<TextBox>().First(field => field.IsEffectivelyVisible);

        box.Focus();
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(Panelled(window));

        window.Close();
    }

    /// <summary>F again brings the panels back with no click between: hiding them moves the canvas, which must keep the keyboard.</summary>
    [AvaloniaFact]
    public async Task F_Again_Brings_The_Panels_Back_Without_A_Click()
    {
        var window = await Host(Write("one.svg", Drawing));

        Viewer(window).Canvas.Focus();
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(Panelled(window));

        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Panelled(window));
        Assert.True(Viewer(window).Canvas.IsFocused);

        window.Close();
    }

    /// <summary>Focus Mode from the menu with a box in a panel focused gives the keyboard to the canvas, the one thing left to type at.</summary>
    [AvaloniaFact]
    public async Task Hiding_The_Box_That_Had_The_Keyboard_Gives_It_To_The_Canvas()
    {
        // A project, whose tree has a search box in its panel.
        var window = await Host(Write("icons.svgstudio", Project));
        var box = window.GetVisualDescendants().OfType<TextBox>()
            .First(field => field.IsEffectivelyVisible && !field.GetVisualAncestors().OfType<Border>().Any(bar => bar.Name is "ControlBar" or "StatusBar"));

        box.Focus();

        Assert.True(await window.Registry.Run("zen"));

        Assert.True(Canvas((GroupPanel)Front(window)!).IsFocused);

        window.Close();
    }

    /// <summary>Anything that arranges the panels shows them first: arranged while hidden, they would change where nobody can see.</summary>
    [AvaloniaTheory]
    [InlineData("panel")]
    [InlineData("reset-layout")]
    [InlineData("close-project")]
    public async Task Arranging_The_Panels_Leaves_Focus_Mode(string what)
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var panels = window.Registry.All.Where(command => command.Id.StartsWith("panel-", StringComparison.Ordinal)).ToList();
        var shown = panels.Count(panel => panel.IsChecked!());

        // A panel that is out, so its item would take it off were the panels not hidden.
        var id = what == "panel" ? window.Registry.All.First(command => command.Id.StartsWith("panel-", StringComparison.Ordinal) && command.IsChecked!()).Id : what;

        Assert.True(await window.Registry.Run("zen"));
        Assert.False(window.Registry[id].IsChecked?.Invoke() ?? false);

        Assert.True(await window.Registry.Run(id));

        Assert.False(Item(window, "Focus Mode").IsChecked);

        // A panel picked while every panel was hidden is shown and brought to the front, not taken off.
        if (what == "panel")
        {
            Assert.True(window.Registry[id].IsChecked!());
            Assert.Equal(shown, panels.Count(panel => panel.IsChecked!()));
        }

        window.Close();
    }

    /// <summary>Outline is the session's: another settings file, as a test or a relaunch has, starts without it.</summary>
    [AvaloniaFact]
    public void Another_Settings_File_Starts_Without_Outline()
    {
        StudioSettings.Outline = true;
        StudioSettings.Store = StudioSettings.Store;

        Assert.False(StudioSettings.Outline);
    }

    [AvaloniaFact]
    public void A_Background_The_File_Cannot_Name_Is_The_Theme()
    {
        File.WriteAllText(StudioSettings.Store, "ground=7\n");

        Assert.Equal(SvgViewerGround.Theme, StudioSettings.Ground);
    }

    /// <summary>On macOS AppKit puts Enter Full Screen in the View menu itself, so Studio's own item is left out there.</summary>
    [AvaloniaFact]
    public async Task Full_Screen_Is_Listed_Once_Wherever_The_System_Lists_It()
    {
        var window = await Host(Write("one.svg", Drawing));
        var view = NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>().Single(item => item.Header == "View").Menu!;

        Assert.Equal(!OperatingSystem.IsMacOS(), view.Items.OfType<NativeMenuItem>().Any(item => item.Header == "Full Screen"));

        window.Close();
    }

    [AvaloniaFact]
    public async Task Full_Screen_Goes_Back_To_What_The_Window_Was()
    {
        var window = await Host(Write("one.svg", Drawing));

        window.WindowState = WindowState.Maximized;

        Assert.True(await window.Registry.Run("fullscreen"));
        Assert.Equal(WindowState.FullScreen, window.WindowState);

        Assert.True(await window.Registry.Run("fullscreen"));
        Assert.Equal(WindowState.Maximized, window.WindowState);

        window.Close();
    }

    /// <summary>On a board a reading is said in the status bar, named as the tool in hand, and the project is not touched.</summary>
    [AvaloniaFact]
    public async Task A_Board_Measures_In_The_Status_Bar_And_Writes_Nothing()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var board = (GroupPanel)Front(window)!;
        var canvas = Canvas(board);
        var home = Placed(canvas, "home");
        var text = ((ProjectDrawing)window.Workspace!.Document.Root.Children[0]).Text;

        canvas.Focus();
        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Drag(window, canvas, Over(canvas, home.At.X + 2f, home.At.Y + 2f), Over(canvas, home.At.X + 12f, home.At.Y + 2f));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("Distance 10, angle 0°", board.Notice ?? string.Empty, StringComparison.Ordinal);
        Assert.NotNull(canvas.Measured);
        Assert.Contains(" · Measure: ", Said(window, "Status"), StringComparison.Ordinal);
        Assert.Equal(text, ((ProjectDrawing)window.Workspace.Document.Root.Children[0]).Text);

        window.Close();
    }

    private static string Said(MainWindow window, string name)
        => window.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == name).Text ?? string.Empty;
}
