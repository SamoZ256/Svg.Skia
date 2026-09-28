using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// The screen the window shows while it holds nothing.
/// </summary>
/// <remarks>
/// What it has to get right is when it is there: an empty window used to be blank, and the one thing
/// worse than that is a greeting over a drawing somebody is working on. The list on it is the menu's
/// list, so it is checked here for being the same one rather than for being a list.
/// </remarks>
public class MainWindowWelcomeTests : IDisposable
{
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect width="24" height="24" fill="#00ff00" />
        </svg>
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory("svg-studio-welcome-").FullName;

    /// <summary>A list of its own per test, since the screen is built from a file that outlives one.</summary>
    private readonly string _was = RecentFiles.Store;

    public MainWindowWelcomeTests()
        => RecentFiles.Store = Path.Combine(_directory, "recent");

    public void Dispose()
    {
        RecentFiles.Store = _was;

        Directory.Delete(_directory, recursive: true);
    }

    private string Write(string name)
    {
        var path = Path.Combine(_directory, name);

        File.WriteAllText(path, Drawing);

        return path;
    }

    private static MainWindow Host()
    {
        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    private static Border Screen(MainWindow window) => window.FindControl<Border>("Welcome")!;

    private static StackPanel Rows(MainWindow window) => window.FindControl<StackPanel>("WelcomeRecent")!;

    private static TextBlock Heading(MainWindow window)
        => window.FindControl<TextBlock>("WelcomeRecentHeading")!;

    private static Button[] Offered(MainWindow window)
        => Rows(window).Children.OfType<Button>().ToArray();

    private static TabControl Tabs(MainWindow window) => window.FindControl<TabControl>("Tabs")!;

    /// <summary>What the platform does to a button, which is the only way to reach its handler.</summary>
    private static async Task Press(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // The handlers are async, so what the press opens arrives after it returns.
        await Task.Delay(200).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void A_Window_With_Nothing_Open_Is_Greeted()
        => Assert.True(Screen(Host()).IsVisible);

    /// <summary>
    /// A drawing double-clicked in the finder, which opens a window of its own: the screen has no
    /// business being seen on the way to it, not even for the frame before the file is read.
    /// </summary>
    [AvaloniaFact]
    public void A_Window_Opened_On_A_Path_Is_Never_Greeted()
    {
        var window = new MainWindow(Write("home.svg"));

        Assert.False(Screen(window).IsVisible);

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(Screen(window).IsVisible);
    }

    [AvaloniaFact]
    public async Task A_Drawing_Takes_It_Down()
    {
        var window = Host();

        await window.OpenAsync(new[] { Write("home.svg") });

        Assert.False(Screen(window).IsVisible);
    }

    /// <summary>Which is what "an empty window says it is empty" now amounts to.</summary>
    [AvaloniaFact]
    public async Task Closing_The_Last_Tab_Brings_It_Back()
    {
        var window = Host();

        await window.OpenAsync(new[] { Write("home.svg") });

        var only = (TabItem)Tabs(window).Items[0]!;

        await Press(((StackPanel)only.Header!).Children.OfType<Button>().Single());

        Assert.Empty(Tabs(window).Items);
        Assert.True(Screen(window).IsVisible);
    }

    [AvaloniaFact]
    public async Task A_Project_Takes_It_Down_And_Closing_The_Project_Brings_It_Back()
    {
        var window = Host();

        await window.NewProjectAsync();

        Assert.False(Screen(window).IsVisible);

        await window.CloseProjectAsync();

        Assert.True(Screen(window).IsVisible);
    }

    /// <summary>
    /// The half of the rule a count of tabs would miss: the project is still open, with its pane and
    /// its tree on screen, so there is nothing to greet.
    /// </summary>
    [AvaloniaFact]
    public async Task Closing_A_Projects_Last_Tab_Leaves_It_Down()
    {
        var window = Host();

        await window.NewProjectAsync();

        var only = (TabItem)Tabs(window).Items[0]!;

        await Press(((StackPanel)only.Header!).Children.OfType<Button>().Single());

        Assert.Empty(Tabs(window).Items);
        Assert.NotNull(window.Workspace);
        Assert.False(Screen(window).IsVisible);
    }

    [AvaloniaFact]
    public void A_Window_That_Has_Opened_Nothing_Offers_Nothing()
    {
        var window = Host();

        Assert.Empty(Offered(window));
        Assert.False(Heading(window).IsVisible);
        Assert.False(Rows(window).IsVisible);
    }

    /// <summary>The menu's list, named the way the menu names it, newest first.</summary>
    [AvaloniaFact]
    public async Task What_Was_Opened_Lately_Is_Offered_Again()
    {
        await Host().OpenAsync(new[] { Write("home.svg"), Write("away.svg") });

        var window = Host();

        Assert.Equal(new[] { "away.svg", "home.svg" }, Offered(window).Select(row => row.Content));
        Assert.True(Heading(window).IsVisible);
    }

    /// <summary>Where it is, for two drawings that are called the same thing.</summary>
    [AvaloniaFact]
    public async Task What_Is_Offered_Says_Where_It_Is()
    {
        var path = Write("home.svg");

        await Host().OpenAsync(new[] { path });

        Assert.Equal(path, ToolTip.GetTip(Offered(Host()).Single()));
    }

    [AvaloniaFact]
    public async Task Picking_One_Opens_It()
    {
        var path = Write("home.svg");

        await Host().OpenAsync(new[] { path });

        var window = Host();

        await Press(Offered(window).Single());

        Assert.Equal(
            new[] { path },
            Tabs(window).Items.OfType<TabItem>()
                .Select(item => ((Svg.Viewer.Skia.Avalonia.SvgViewer)item.Content!).DocumentPath));

        Assert.False(Screen(window).IsVisible);
    }

    /// <summary>The window's own New, from a window that has no menu drawn in it on macOS.</summary>
    [AvaloniaFact]
    public async Task A_Project_Can_Be_Started_From_It()
    {
        var window = Host();

        await Press(window.FindControl<Button>("WelcomeNew")!);

        Assert.NotNull(window.Workspace);
        Assert.False(Screen(window).IsVisible);
    }
}
