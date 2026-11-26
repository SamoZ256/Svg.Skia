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
            new[] { "File", "Edit", "Select", "View", "Window", "Project", "Help" },
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

    /// <summary>A tab closes what the Window menu can bring back, and nothing it cannot.</summary>
    [AvaloniaFact]
    public async Task A_Tab_Closes_Only_What_The_Window_Menu_Brings_Back()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        ContextMenu MenuOf(string header)
            => window.GetVisualDescendants().OfType<Border>()
                .First(tab => tab.Classes.Contains("pane") && tab.Child is TextBlock { Text: var said } && said == header)
                .ContextMenu!;

        Assert.DoesNotContain(MenuOf("Project").Items.OfType<MenuItem>(), item => Equals(item.Header, "Close"));

        var close = MenuOf("Streamline").Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Close"));

        close.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("streamline/", window.Layout, StringComparison.Ordinal);
        // As the menu reads it when it opens.
        Assert.False(window.Registry["panel-streamline"].IsChecked!());

        Pick(Item(window, "Window", "Streamline"));
        Assert.Contains("streamline/", window.Layout, StringComparison.Ordinal);

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

    // ---- Select ---------------------------------------------------------------------------------

    /// <remarks>On a Mac ⌘A is the menu's over every box, so the menu has to go where the key would have.</remarks>
    [AvaloniaFact]
    public async Task Select_All_From_The_Menu_Goes_Where_The_Key_Would()
    {
        var window = await Host(Write("one.svg", Drawing));
        var viewer = Front(window);

        // The search box, which is one the window always has room for.
        Assert.True(await window.Registry.Run("commands"));
        Type(window, "abc");

        Assert.True(await window.Registry.Run("selectall"));
        Assert.Equal((0, 3), (Query(window).SelectionStart, Query(window).SelectionEnd));
        Assert.Empty(viewer.Elements.SelectedAddresses);

        viewer.Canvas.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.True(await window.Registry.Run("selectall"));
        Assert.Equal(new[] { "0" }, viewer.Elements.SelectedAddresses);

        Assert.True(await window.Registry.Run("deselect"));
        Assert.Empty(viewer.Elements.SelectedAddresses);

        window.Close();
    }

    /// <remarks>Text that can be selected and is not a box: the assistant's answers, and the viewer's error.</remarks>
    [AvaloniaFact]
    public async Task Select_All_From_The_Menu_Selects_Selectable_Text_It_Is_In()
    {
        var window = await Host(Write("one.svg", Drawing));
        var viewer = Front(window);
        var text = viewer.GetVisualDescendants().OfType<SelectableTextBlock>().Single(block => block.Name == "ErrorText");

        viewer.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "ErrorPanel").IsVisible = true;
        text.Text = "broken";
        Dispatcher.UIThread.RunJobs();

        Assert.True(text.Focus());
        Assert.True(await window.Registry.Run("selectall"));

        Assert.Equal((0, 6), (text.SelectionStart, text.SelectionEnd));
        Assert.Empty(viewer.Elements.SelectedAddresses);

        window.Close();
    }

    // ---- command search -----------------------------------------------------------------------

    private static Border Search(MainWindow window) => window.FindControl<Border>("CommandSearch")!;

    private static TextBox Query(MainWindow window) => window.FindControl<TextBox>("CommandQuery")!;

    private static ListBox Results(MainWindow window) => window.FindControl<ListBox>("CommandResults")!;

    private static IReadOnlyList<Svg.Editor.Skia.SvgCommandCatalogEntry> Entries(MainWindow window)
        => (Results(window).ItemsSource as IEnumerable<Svg.Editor.Skia.SvgCommandCatalogEntry>)?.ToList() ?? new List<Svg.Editor.Skia.SvgCommandCatalogEntry>();

    private static IReadOnlyList<string> Found(MainWindow window) => Entries(window).Select(entry => entry.Id).ToList();

    private static void Type(MainWindow window, string text)
    {
        Query(window).Text = text;
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(MainWindow window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Primary_K_Opens_Search_And_Escape_Gives_The_Keyboard_Back()
    {
        var window = await Host(Write("one.svg", Drawing));
        var canvas = Front(window).Canvas;

        canvas.Focus();
        Press(window, PhysicalKey.K, Primary);

        Assert.True(Search(window).IsVisible);
        Assert.True(Query(window).IsFocused);

        // Asked again, it stays open with the keyboard in its box.
        Press(window, PhysicalKey.K, Primary);
        Assert.True(Search(window).IsVisible);

        Press(window, PhysicalKey.Escape);

        Assert.False(Search(window).IsVisible);
        Assert.True(canvas.IsFocused);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Search_Offers_What_Can_Run_Now_And_Not_Itself()
    {
        var window = await Host(Write("one.svg", Drawing));

        Assert.True(await window.Registry.Run("commands"));
        var found = Found(window);

        Assert.Contains("zoomin", found);
        Assert.Contains("tool-rectangle", found);
        Assert.DoesNotContain("commands", found);
        Assert.DoesNotContain("project-cut", found);
        Assert.DoesNotContain("close-project", found);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Down_And_Enter_Run_The_Second_Match_Once_Search_Has_Closed()
    {
        var window = await Host(Write("one.svg", Drawing));
        var canvas = Front(window).Canvas;
        var scale = canvas.Scale;

        Assert.True(await window.Registry.Run("commands"));
        Type(window, "zoom");
        Assert.Equal(new[] { "zoomin", "zoomout" }, Found(window).Take(2));

        Press(window, PhysicalKey.ArrowDown);
        Press(window, PhysicalKey.Enter);

        Assert.False(Search(window).IsVisible);
        Assert.True(canvas.Scale < scale);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Tool_From_Search_Is_Armed()
    {
        var window = await Host(Write("one.svg", Drawing));

        Assert.True(await window.Registry.Run("commands"));
        Type(window, "rectangle");
        Press(window, PhysicalKey.Enter);

        Assert.Single(window.GetVisualDescendants().OfType<ToggleButton>(), button => button.IsChecked == true && Equals(button.Tag, "rect"));

        window.Close();
    }

    /// <remarks>
    /// The order is the point: run while the search box still had the keyboard, Undo took back the
    /// box's typing and left the drawing as it was.
    /// </remarks>
    [AvaloniaFact]
    public async Task Undo_From_Search_Takes_Back_The_Drawings_Edit()
    {
        var window = await Host(Write("one.svg", Drawing));
        var viewer = Front(window);
        var was = viewer.Source;

        Assert.True(viewer.SetSource(was.Replace("#00ff00", "#0000ff", StringComparison.Ordinal)));
        Dispatcher.UIThread.RunJobs();

        viewer.Canvas.Focus();
        Press(window, PhysicalKey.K, Primary);
        Type(window, "undo");
        Press(window, PhysicalKey.Enter);

        Assert.Equal(was, viewer.Source);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Search_Closes_When_The_Keyboard_Goes_Elsewhere()
    {
        var window = await Host(Write("one.svg", Drawing));

        Assert.True(await window.Registry.Run("commands"));
        Assert.True(Search(window).IsVisible);

        Front(window).Canvas.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.False(Search(window).IsVisible);

        window.Close();
    }

    /// <remarks>Search is gone by the second press, which lands on the canvas that was under it.</remarks>
    [AvaloniaFact]
    public async Task A_Double_Click_On_A_Row_Stops_At_The_Row()
    {
        var window = await Host(Write("one.svg", Drawing));
        var viewer = Front(window);
        var was = viewer.Source;

        Assert.True(await window.Registry.Run("commands"));
        Type(window, "text");
        Assert.Equal("tool-type", Found(window)[0]);

        window.UpdateLayout();
        var row = Assert.IsType<ListBoxItem>(Results(window).ContainerFromIndex(0));

        Gestures.Press(window, row, new Point(4, 4));
        Gestures.Release(window, row, new Point(4, 4));

        Assert.False(Search(window).IsVisible);

        var canvas = viewer.Canvas;
        var middle = new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2);

        Gestures.Press(window, canvas, middle, clicks: 2);
        Gestures.Release(window, canvas, middle);

        Assert.Equal(was, viewer.Source);

        window.Close();
    }

    /// <remarks>The window leaves a box on Enter, so a modified one handled nowhere left the keyboard nowhere.</remarks>
    [AvaloniaFact]
    public async Task Shift_Enter_Runs_The_Match_As_Enter_Does()
    {
        var window = await Host(Write("one.svg", Drawing));
        var canvas = Front(window).Canvas;
        var scale = canvas.Scale;

        canvas.Focus();
        Press(window, PhysicalKey.K, Primary);
        Type(window, "zoom out");
        Press(window, PhysicalKey.Enter, RawInputModifiers.Shift);

        Assert.False(Search(window).IsVisible);
        Assert.True(canvas.Scale < scale);
        Assert.True(canvas.IsFocused);

        window.Close();
    }

    /// <remarks>Its own menu takes the keyboard into a popup, and gives it back to the box.</remarks>
    [AvaloniaFact]
    public async Task Search_Stays_Open_Over_The_Boxs_Own_Menu()
    {
        var window = await Host(Write("one.svg", Drawing));

        Assert.True(await window.Registry.Run("commands"));

        var menu = Assert.IsAssignableFrom<FlyoutBase>(Query(window).ContextFlyout);

        menu.ShowAt(Query(window));
        Dispatcher.UIThread.RunJobs();
        menu.Hide();
        Dispatcher.UIThread.RunJobs();

        Assert.True(Search(window).IsVisible);

        window.Close();
    }

    /// <remarks>An undo is labelled by whatever made it, longer than a search entry may be.</remarks>
    [AvaloniaFact]
    public async Task Search_Opens_Over_An_Undo_With_A_Long_Label()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        Assert.True(await window.RemoveAsync(window.Workspace!.Document.Root.Children[0], new string('x', 300)));

        Assert.True(await window.Registry.Run("commands"));
        Assert.Contains("undo", Found(window));

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Drawing_Is_Found_By_Its_Name_Alone()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        Assert.True(await window.Registry.Run("commands"));
        Type(window, "de");

        Assert.DoesNotContain("home", Entries(window).Select(entry => entry.Label));

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Command_That_Is_On_Is_Marked_So()
    {
        var window = await Host(Write("one.svg", Drawing));
        var on = window.Registry.All.First(command => command.Execute is { } && command.CanExecute() && command.IsChecked?.Invoke() == true);

        Assert.True(await window.Registry.Run("commands"));
        Type(window, on.Label);
        Assert.Equal(on.Id, Found(window)[0]);

        window.UpdateLayout();
        var row = Assert.IsType<ListBoxItem>(Results(window).ContainerFromIndex(0));

        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "✓");

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Drawing_Found_In_Search_Opens_In_Its_Tab()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var home = window.Workspace!.Document.Root.Children[0];

        Assert.True(await window.Registry.Run("commands"));
        Type(window, "home");
        Press(window, PhysicalKey.Enter);

        var tabs = window.GetVisualDescendants().OfType<TabControl>().First(control => control.Name == "Tabs");

        Assert.Same(home, (tabs.SelectedItem as TabItem)?.Tag);

        // And the project tree is on its row, as it is for a tab picked any other way.
        Assert.Same(home, (window.FindControl<TreeView>("ProjectTree")!.SelectedItem as TreeViewItem)?.Tag);

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

        Assert.DoesNotContain(window.GetVisualDescendants().OfType<ToggleButton>(), button => button.IsChecked == true && Equals(button.Tag, "rect"));

        // On the canvas the letter arms the tool.
        viewer.Canvas.Focus();
        window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(window.GetVisualDescendants().OfType<ToggleButton>(), button => button.IsChecked == true && Equals(button.Tag, "rect"));

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
