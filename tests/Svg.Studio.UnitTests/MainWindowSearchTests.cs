using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// Searching what is open: the text in the source pane, and the rows in the project tree.
/// </summary>
/// <remarks>
/// Two searches rather than one, because they answer different questions — where a string is in the
/// drawing being edited, and which row of the project is the one meant. Driven by calling the
/// window and typing in the box, since the gesture that reaches the first of them is a native menu
/// item's and no headless keystroke carries it.
/// </remarks>
public class MainWindowSearchTests : IDisposable
{
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect width="24" height="24" fill="#00ff00" />
        </svg>
        """;

    private const string Project = """
        <svgc>
          <namespace>Demo.Icons</namespace>

          <svg input="home.svg" class="Home" />

          <group namespace="Demo.Icons.Large" scale="2">
            <svg input="badge.svg" class="BadgeLarge" />
          </group>
        </svgc>
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);

        File.WriteAllText(path, text);

        return path;
    }

    private static async Task<MainWindow> Host(string path)
    {
        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        await window.OpenAsync(new[] { path });

        Dispatcher.UIThread.RunJobs();

        return window;
    }

    /// <summary>A project, with both its drawings on disk beside it.</summary>
    private async Task<MainWindow> Opened()
    {
        Write("home.svg", Drawing);
        Write("badge.svg", Drawing);

        return await Host(Write("icons.svgcproj", Project));
    }

    private static SvgViewer Viewer(MainWindow window)
        => window.GetVisualDescendants().OfType<SvgViewer>().First();

    private static TextEditor Editor(SvgViewer viewer)
        => viewer.GetVisualDescendants().OfType<TextEditor>().First();

    private static TreeView Tree(MainWindow window) => window.FindControl<TreeView>("ProjectTree")!;

    private static TextBox Box(MainWindow window) => window.FindControl<TextBox>("ProjectSearch")!;

    private static string? Count(MainWindow window)
        => window.FindControl<TextBlock>("ProjectSearchCount")!.Text;

    private static TreeViewItem Root(MainWindow window)
        => Tree(window).Items.OfType<TreeViewItem>().First();

    /// <summary>Every row the tree holds, flattened, by what it shows.</summary>
    private static string[] Rows(TreeViewItem item)
        => new[] { (string)item.Header! }
            .Concat(item.Items.OfType<TreeViewItem>().SelectMany(Rows))
            .ToArray();

    private static string Selected(MainWindow window)
        => (string)((TreeViewItem)Tree(window).SelectedItem!).Header!;

    private static void Type(MainWindow window, string? query)
    {
        Box(window).Text = query;

        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(MainWindow window, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        Box(window).RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers
        });

        Dispatcher.UIThread.RunJobs();
    }

    // ---- the source pane -----------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Finding_Opens_The_Pane_And_Its_Box()
    {
        var window = await Host(Write("home.svg", Drawing));
        var viewer = Viewer(window);

        // The pane starts closed, and a search over a closed pane would search an empty buffer.
        Assert.False(viewer.ShowSource);

        window.Find();
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.ShowSource);
        Assert.True(Editor(viewer).SearchPanel.IsOpened);
    }

    /// <summary>The tab being looked at owns the keystroke, as Undo does.</summary>
    [AvaloniaFact]
    public async Task Finding_In_A_Recipe_Opens_That_Editor_Rather_Than_The_Drawing()
    {
        var window = await Opened();
        var recipe = window.ShowRecipe(Write("icons.recipe", "# nothing in particular"));

        Dispatcher.UIThread.RunJobs();

        window.Find();
        Dispatcher.UIThread.RunJobs();

        Assert.True(recipe.GetVisualDescendants().OfType<TextEditor>().Single().SearchPanel.IsOpened);
    }

    [AvaloniaFact]
    public void Finding_With_Nothing_Open_Does_Nothing()
    {
        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.Find();
    }

    // ---- the project tree ----------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Typing_Opens_The_Tree_Down_To_The_Match()
    {
        var window = await Opened();
        var group = (TreeViewItem)Root(window).Items[1]!;

        group.IsExpanded = false;

        Type(window, "badge");

        Assert.True(group.IsExpanded);
        Assert.Equal("badge.svg - BadgeLarge", Selected(window));
        Assert.Equal("1/1", Count(window));
    }

    /// <summary>Nothing is hidden — which is the whole of why this is a jump and not a filter.</summary>
    [AvaloniaFact]
    public async Task The_Tree_Keeps_Every_Row()
    {
        var window = await Opened();
        var before = Rows(Root(window));

        Type(window, "badge");

        Assert.Equal(before, Rows(Root(window)));
    }

    [AvaloniaFact]
    public async Task Enter_Walks_The_Matches_And_Comes_Back_Round()
    {
        var window = await Opened();

        // Both drawings, and neither group.
        Type(window, ".svg");

        Assert.Equal("home.svg - Home", Selected(window));
        Assert.Equal("1/2", Count(window));

        Press(window, Key.Enter);

        Assert.Equal("badge.svg - BadgeLarge", Selected(window));
        Assert.Equal("2/2", Count(window));

        Press(window, Key.Enter);

        Assert.Equal("home.svg - Home", Selected(window));
        Assert.Equal("1/2", Count(window));

        Press(window, Key.Enter, KeyModifiers.Shift);

        Assert.Equal("badge.svg - BadgeLarge", Selected(window));
    }

    /// <summary>What was being looked at is worth more than the answer that there is no match.</summary>
    [AvaloniaFact]
    public async Task A_Query_That_Matches_Nothing_Leaves_The_Selection_Alone()
    {
        var window = await Opened();

        Type(window, "badge");

        var was = Tree(window).SelectedItem;

        Type(window, "badgering");

        Assert.Same(was, Tree(window).SelectedItem);
        Assert.Equal("none", Count(window));
    }

    [AvaloniaFact]
    public async Task Escape_Clears_The_Box()
    {
        var window = await Opened();

        Type(window, "badge");
        Press(window, Key.Escape);

        Assert.True(string.IsNullOrEmpty(Box(window).Text));
        Assert.True(string.IsNullOrEmpty(Count(window)));
    }

    /// <summary>A project closed takes its search with it, or the next one opens under it.</summary>
    [AvaloniaFact]
    public async Task Closing_The_Project_Clears_The_Box()
    {
        var window = await Opened();

        Type(window, "badge");

        Assert.True(await window.CloseProjectAsync());

        Assert.True(string.IsNullOrEmpty(Box(window).Text));
    }
}
