using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// What the shell does about work that is not on disk.
/// </summary>
/// <remarks>
/// The prompt is replaced rather than driven: a modal is the one thing a test cannot answer, and
/// leaving it undriveable would leave the only guard against losing an edit untested. What is
/// asserted is that the question is asked, what it says, and that the answer is obeyed.
/// </remarks>
public class MainWindowClosingTests
{
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect width="24" height="24" fill="#00ff00" />
        </svg>
        """;

    private static async Task<(MainWindow Window, List<string> Asked)> Host(bool answer)
    {
        var path = Path.Combine(Path.GetTempPath(), $"svg-closing-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, Drawing);

        var window = new MainWindow(path);
        var asked = new List<string>();

        window.ConfirmDiscard = message =>
        {
            asked.Add(message);
            return Task.FromResult(answer);
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        await Task.Delay(100).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();

        return (window, asked);
    }

    /// <summary>Types into the open drawing's pane, which is what makes it unsaved.</summary>
    private static void Edit(MainWindow window)
    {
        var viewer = window.GetVisualDescendants().OfType<SvgViewer>().First();

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        window.GetVisualDescendants().OfType<TextEditor>().First().Document.Text = Drawing + "<!-- edited -->";
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.IsSourceModified, "the drawing was not made unsaved");
    }

    [AvaloniaFact]
    public async Task Closing_A_Window_With_Nothing_Unsaved_Asks_Nothing()
    {
        var (window, asked) = await Host(answer: true);

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(asked);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task Closing_A_Window_With_Unsaved_Work_Asks_And_Obeys_A_Refusal()
    {
        var (window, asked) = await Host(answer: false);

        Edit(window);

        window.Close();
        Dispatcher.UIThread.RunJobs();

        // Named rather than counted, because one drawing is a drawing and not a number of them.
        Assert.Contains(asked, m => m.Contains(".svg has changes that have not been saved", StringComparison.Ordinal));

        // The window is still there, which is the whole point of asking.
        Assert.True(window.IsVisible);

        window.ConfirmDiscard = _ => Task.FromResult(true);
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Closing_A_Window_With_Unsaved_Work_Goes_Through_On_A_Yes()
    {
        var (window, asked) = await Host(answer: true);

        Edit(window);

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(asked);
        Assert.False(window.IsVisible);
    }
}
