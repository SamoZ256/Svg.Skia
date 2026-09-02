using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// File → Open Recent: what was opened lately, offered again without the picker.
/// </summary>
/// <remarks>
/// Driven through the window rather than through <see cref="RecentFiles"/> alone, because the list
/// on disk is the easy half — what the menu has to get right is that a drawing reaches it however
/// it was opened, and that picking one opens it again.
/// </remarks>
public class MainWindowRecentTests : IDisposable
{
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect width="24" height="24" fill="#00ff00" />
        </svg>
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory("svg-studio-recent-").FullName;

    /// <summary>A list of its own per test: the menu is built from a file, and files outlive a test.</summary>
    private readonly string _was = RecentFiles.Store;

    public MainWindowRecentTests()
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

    /// <summary>The Open Recent item itself, which is also what says whether there is anything in it.</summary>
    private static NativeMenuItem Recent(MainWindow window) => NativeMenu.GetMenu(window)!
        .Items.OfType<NativeMenuItem>().First(item => item.Header == "File")
        .Menu!.Items.OfType<NativeMenuItem>().First(item => item.Header == "Open Recent");

    private static string[] Offered(MainWindow window)
        => Recent(window).Menu!.Items.OfType<NativeMenuItem>().Select(item => item.Header!).ToArray();

    [AvaloniaFact]
    public void A_Window_That_Has_Opened_Nothing_Offers_Nothing()
    {
        var window = Host();

        Assert.Empty(Offered(window));
        Assert.False(Recent(window).IsEnabled);
    }

    [AvaloniaFact]
    public async Task An_Opened_Drawing_Is_Offered_Again()
    {
        var window = Host();

        await window.OpenAsync(new[] { Write("home.svg") });

        Assert.Equal(new[] { "home.svg" }, Offered(window));
        Assert.True(Recent(window).IsEnabled);
    }

    /// <summary>Newest first, and once: reopening a drawing moves it up rather than repeating it.</summary>
    [AvaloniaFact]
    public async Task The_Newest_Is_First_And_Nothing_Is_Listed_Twice()
    {
        var window = Host();

        var home = Write("home.svg");
        var away = Write("away.svg");

        await window.OpenAsync(new[] { home, away, home });

        Assert.Equal(new[] { "home.svg", "away.svg" }, Offered(window));
    }

    /// <summary>A drawing that would not open has no business being offered as one that would.</summary>
    [AvaloniaFact]
    public async Task What_Failed_To_Open_Is_Not_Offered()
    {
        var window = Host();

        var broken = Path.Combine(_directory, "broken.svg");

        File.WriteAllText(broken, "not a drawing");

        await window.OpenAsync(new[] { broken });

        Assert.Empty(Offered(window));
    }

    /// <summary>Read at each build, so a file deleted since it was opened is dropped from the menu.</summary>
    [AvaloniaFact]
    public async Task What_Has_Gone_Since_Is_Dropped()
    {
        var window = Host();

        var path = Write("home.svg");

        await window.OpenAsync(new[] { path });

        File.Delete(path);

        Assert.Empty(Offered(Host()));
    }

    /// <summary>The list is a file, which is the whole of how it survives the window closing.</summary>
    [AvaloniaFact]
    public async Task The_List_Outlives_The_Window()
    {
        await Host().OpenAsync(new[] { Write("home.svg") });

        Assert.Equal(new[] { "home.svg" }, Offered(Host()));
    }

    [AvaloniaFact]
    public async Task Picking_One_Opens_It()
    {
        var window = Host();
        var path = Write("home.svg");

        await window.OpenAsync(new[] { path });

        var reopened = Host();
        var entry = Recent(reopened).Menu!.Items.OfType<NativeMenuItem>().Single();

        // What the platform's own menu calls when the item is picked; NativeMenuItem exposes its
        // Click no other way.
        ((INativeMenuItemExporterEventsImplBridge)entry).RaiseClicked();

        // The handler is async void as every menu handler here is, so the drawing arrives after the
        // click returns.
        await Task.Delay(200).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            new[] { path },
            reopened.FindControl<TabControl>("Tabs")!.Items.OfType<TabItem>()
                .Select(item => ((Svg.Viewer.Skia.Avalonia.SvgViewer)item.Content!).DocumentPath)
                .ToArray());
    }
}
