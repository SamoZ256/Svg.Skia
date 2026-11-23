using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// The window's menus and keys, built from its commands: what each menu holds, what an item does
/// when it is picked, and which keys the window answers rather than the canvas.
/// </summary>
/// <remarks>In the settings collection because View's toggles write settings.</remarks>
[Collection("settings")]
public class MainWindowMenuTests : IDisposable
{
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect width="24" height="24" fill="#00ff00" />
        </svg>
        """;

    private const string Project = """
        <studio namespace="Demo.Icons">
          <drawing name="home" class="Home">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#00ff00" />
            </svg>
          </drawing>
        </studio>
        """;

    private static readonly RawInputModifiers Primary = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private readonly string _was = StudioSettings.Store;
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public MainWindowMenuTests() => StudioSettings.Store = Path.Combine(_directory, "settings");

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

        if (paths.Length > 0)
        {
            await window.OpenAsync(paths);
            Dispatcher.UIThread.RunJobs();
        }

        window.Measure(new Size(900, 600));
        window.Arrange(new Rect(0, 0, 900, 600));
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    private static NativeMenuItem Menu(MainWindow window, string menu)
        => NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>().Single(item => item.Header == menu);

    private static NativeMenuItem Item(MainWindow window, string menu, string item)
        => Menu(window, menu).Menu!.Items.OfType<NativeMenuItem>().Single(entry => entry.Header == item);

    private static void Pick(NativeMenuItem item)
    {
        ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();
        Dispatcher.UIThread.RunJobs();
    }

    private static SvgViewer Front(MainWindow window)
        => (SvgViewer)((TabItem)window.GetVisualDescendants().OfType<TabControl>().First(tabs => tabs.Name == "Tabs").SelectedItem!).Content!;

    [AvaloniaFact]
    public async Task The_Menu_Bar_Holds_Studios_Menus()
    {
        var window = await Host();

        Assert.Equal(
            new[] { "File", "Edit", "View", "Window", "Project", "Help" },
            NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>().Select(item => item.Header));

        window.Close();
    }

    /// <summary>Undo and Redo keep naming what they would reach, however often the name changes.</summary>
    /// <remarks>They used to be found by their header, so the first name they took was the last one.</remarks>
    [AvaloniaFact]
    public async Task Undo_And_Redo_Name_What_They_Would_Reach_Each_Time()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var undo = Menu(window, "Edit").Menu!.Items.OfType<NativeMenuItem>().First();
        var redo = Menu(window, "Edit").Menu!.Items.OfType<NativeMenuItem>().Skip(1).First();

        Assert.True(await window.RemoveAsync(window.Workspace!.Document.Root.Children[0]));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Undo remove home", undo.Header);

        Assert.True(window.Undo());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Undo", undo.Header);
        Assert.Equal("Redo remove home", redo.Header);
        Assert.False(undo.IsEnabled);

        Assert.True(window.Redo());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Undo remove home", undo.Header);
        Assert.True(undo.IsEnabled);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Window_Item_Takes_Its_Panel_Off_And_Puts_It_Back()
    {
        var window = await Host(Write("one.svg", Drawing));
        var streamline = Item(window, "Window", "Streamline");
        var before = window.Layout;

        Assert.True(streamline.IsChecked);

        Pick(streamline);
        Assert.DoesNotContain("streamline/", window.Layout, StringComparison.Ordinal);
        Assert.False(streamline.IsChecked);

        Pick(streamline);
        Assert.Equal(before, window.Layout);
        Assert.True(streamline.IsChecked);

        // The tree and Changes follow the project, so the menu does not offer them.
        var listed = Menu(window, "Window").Menu!.Items.OfType<NativeMenuItem>().Select(item => item.Header).ToList();
        Assert.DoesNotContain("Project", listed);
        Assert.DoesNotContain("Changes", listed);

        window.Close();
    }

    /// <remarks>
    /// The window used to read the arrangement on file again whenever a setting changed, which put
    /// back every panel it had taken off — the project tree, with no project to show in it.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Setting_Changed_Leaves_A_Panel_That_Was_Taken_Off_Off()
    {
        var window = await Host(Write("one.svg", Drawing));
        var streamline = Item(window, "Window", "Streamline");

        Assert.DoesNotContain("tree/", window.Layout, StringComparison.Ordinal);
        Pick(streamline);

        Pick(Item(window, "View", "Snap to Grid"));

        Assert.DoesNotContain("tree/", window.Layout, StringComparison.Ordinal);
        Assert.DoesNotContain("streamline/", window.Layout, StringComparison.Ordinal);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Reset_Layout_Shows_Only_What_Has_Something_To_Show()
    {
        StudioSettings.Layout = "row(*/1,col(element/1/element/open)/300px)";

        var window = await Host(Write("one.svg", Drawing));

        Pick(Item(window, "Window", "Reset Layout"));

        Assert.Equal(StudioSettings.DefaultLayout, StudioSettings.Layout);
        Assert.DoesNotContain("tree/", window.Layout, StringComparison.Ordinal);
        Assert.Contains("streamline/", window.Layout, StringComparison.Ordinal);

        window.Close();
    }

    [AvaloniaFact]
    public async Task View_Toggles_Follow_The_Settings_And_The_Tab()
    {
        var window = await Host(Write("one.svg", Drawing));
        var snap = Item(window, "View", "Snap to Grid");
        var ratio = Item(window, "View", "Lock Ratio");
        var viewer = Front(window);
        var was = StudioSettings.SnapToGrid;

        Assert.Equal(was, snap.IsChecked);

        Pick(snap);
        Assert.Equal(!was, StudioSettings.SnapToGrid);
        Assert.Equal(!was, viewer.SnapsToGrid);
        Assert.Equal(!was, snap.IsChecked);

        Assert.False(viewer.LocksAspectRatio);
        Pick(ratio);
        Assert.True(viewer.LocksAspectRatio);
        Assert.True(ratio.IsChecked);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Zoom_In_Zooms_The_Drawing_In_Front()
    {
        var window = await Host(Write("one.svg", Drawing));
        var canvas = Front(window).Canvas;
        var scale = canvas.Scale;

        Pick(Item(window, "View", "Zoom In"));

        Assert.True(canvas.Scale > scale);

        window.Close();
    }

    /// <summary>Every letter the sheet lists for a tool is one the canvas answers.</summary>
    [AvaloniaFact]
    public void Each_Tool_Letter_Is_One_The_Canvas_Answers()
    {
        var window = new MainWindow();
        var tools = window.Registry.All.Where(command => command.Id.StartsWith("tool-", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(tools);

        foreach (var tool in tools)
        {
            Assert.Null(tool.Execute);
            Assert.True(new SvgViewerDraw().Pressed(new KeyEventArgs { Key = StudioCommands.Gesture(tool.Key!.Value, tool.Modifiers, mac: false).Key }), tool.Id);
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task Control_Tab_Goes_Round_The_Tabs_Both_Ways()
    {
        var window = await Host(Write("one.svg", Drawing), Write("two.svg", Drawing), Write("three.svg", Drawing));
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First(control => control.Name == "Tabs");

        tabs.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, tabs.SelectedIndex);

        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control | RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, tabs.SelectedIndex);

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Save_Key_Saves_The_Drawing_In_Front()
    {
        var path = Write("one.svg", Drawing);
        var window = await Host(path);
        var viewer = Front(window);

        Assert.True(viewer.SetSource(viewer.Source.Replace("#00ff00", "#0000ff", StringComparison.Ordinal)));
        Assert.True(viewer.IsSourceModified);

        window.KeyPressQwerty(PhysicalKey.S, Primary);
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewer.IsSourceModified);
        Assert.Contains("#0000ff", File.ReadAllText(path), StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>A drawing opened from text has no file, and saving is how it gets one.</summary>
    [AvaloniaFact]
    public async Task A_Drawing_Opened_From_Text_Can_Be_Saved()
    {
        var window = await Host();

        await window.OpenTextAsync(Drawing, "icon");
        Dispatcher.UIThread.RunJobs();

        Assert.False(Front(window).IsSourceModified);
        Assert.True(window.Registry["save"].CanExecute());
        Assert.True(Item(window, "File", "Save").IsEnabled);

        window.Close();
    }

    /// <remarks>
    /// A box writes what was typed only when it is left, and the key closes the tab without leaving
    /// it, so it used to go without the question the edit should have raised.
    /// </remarks>
    [AvaloniaFact]
    public async Task Closing_By_Key_Asks_About_What_A_Box_Still_Holds()
    {
        var window = await Host(Write("one.svg", Drawing));
        var viewer = Front(window);
        var asked = new List<string>();

        window.ConfirmDiscard = message =>
        {
            asked.Add(message);

            return Task.FromResult(false);
        };

        Assert.True(viewer.Elements.TrySelect(new[] { "0" }));
        Dispatcher.UIThread.RunJobs();

        var fill = window.GetVisualDescendants().OfType<TextBox>().First(box => box.Text == "#00ff00");

        fill.Focus();
        fill.Text = "#ff0000";
        Assert.False(viewer.IsSourceModified);

        window.KeyPressQwerty(PhysicalKey.W, Primary);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(asked);
        Assert.Contains("#ff0000", viewer.Source, StringComparison.Ordinal);
        Assert.Contains(viewer, window.GetVisualDescendants().OfType<SvgViewer>());

        window.Close();
    }

    /// <summary>The zoom keys the View menu shows are the window's whenever no box has the keyboard.</summary>
    [AvaloniaFact]
    public async Task A_Zoom_Key_Zooms_Without_The_Canvas_Focused()
    {
        var window = await Host(Write("one.svg", Drawing));
        var canvas = Front(window).Canvas;
        var scale = canvas.Scale;

        window.FocusManager!.Focus(null);
        window.KeyPressQwerty(PhysicalKey.Equal, Primary);
        Dispatcher.UIThread.RunJobs();

        Assert.True(canvas.Scale > scale);

        window.Close();
    }

    /// <summary>A letter typed on the canvas is the canvas's: the window takes only its own keys.</summary>
    [AvaloniaFact]
    public async Task A_Tool_Letter_Is_Left_To_The_Canvas()
    {
        var window = await Host(Write("one.svg", Drawing));
        var viewer = Front(window);

        // Nothing focused, so the window is the only thing the key can reach, and it is not its key.
        window.FocusManager!.Focus(null);
        window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(window.GetVisualDescendants().OfType<ToggleButton>().Where(button => button.IsChecked == true && Equals(button.Tag, "rect")));

        // On the canvas the letter arms the tool.
        viewer.Canvas.Focus();
        window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(window.GetVisualDescendants().OfType<ToggleButton>().Where(button => button.IsChecked == true && Equals(button.Tag, "rect")));

        window.Close();
    }

    [AvaloniaFact]
    public async Task Keyboard_Shortcuts_Lists_Every_Key_In_A_Window_Of_Its_Own()
    {
        var window = await Host();

        Pick(Item(window, "Help", "Keyboard Shortcuts"));

        var sheet = Assert.Single(window.OwnedWindows, owned => owned.Title == "Keyboard Shortcuts");
        var texts = sheet.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();

        foreach (var command in window.Registry.All.Where(command => window.Registry.Shortcut(command).Length > 0))
        {
            Assert.Contains(command.Label, texts);
            Assert.Contains(window.Registry.Shortcut(command), texts);
        }
        Assert.False(sheet.IsDialog);

        // Asked again, the one already open comes forward.
        Pick(Item(window, "Help", "Keyboard Shortcuts"));
        Assert.Single(window.OwnedWindows, owned => owned.Title == "Keyboard Shortcuts");

        sheet.Close();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Mcp_Connection_Opens_The_Settings_Where_Claude_Code_Is()
    {
        var window = await Host();
        var shown = window.Registry.Run("mcp-connection");
        Dispatcher.UIThread.RunJobs();

        var settings = Assert.IsType<SettingsWindow>(Assert.Single(window.OwnedWindows));

        Assert.Equal("Accounts", (settings.Tabs.SelectedItem as TabItem)?.Header);

        settings.Close();
        await shown;
        window.Close();
    }

    [AvaloniaFact]
    public async Task About_Says_Which_Version_This_Is()
    {
        var window = await Host();
        var said = new List<(string Title, string Message)>();

        window.Announce = (title, message) =>
        {
            said.Add((title, message));

            return Task.CompletedTask;
        };

        Assert.True(await window.Registry.Run("about"));

        var (title, message) = Assert.Single(said);

        Assert.Equal("About SVG Studio", title);
        Assert.StartsWith("Version ", message, StringComparison.Ordinal);

        window.Close();
    }
}
