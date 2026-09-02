using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// Searching what is open: the text in the source pane.
/// </summary>
/// <remarks>
/// Driven by calling the window rather than by a keystroke, because the gesture that reaches this
/// is a native menu item's and no headless key carries it — the same way the undo tests ask.
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
}
