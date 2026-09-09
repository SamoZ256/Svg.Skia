// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// Taking back an edit to the drawing's text.
/// </summary>
/// <remarks>
/// AvaloniaEdit binds the undo and redo commands and no keys to them — it asks the keymap for a
/// gesture for every other command it defines and never for these two — so a pane in a plain host
/// has an undo stack that nothing can reach. These press the keys.
/// </remarks>
public class SvgViewerUndoTests
{
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect width="24" height="24" fill="#3366cc" />
        </svg>
        """;

    private static async Task<(Window Window, TextEditor Pane)> Host()
    {
        var viewer = new SvgViewer();
        var window = new Window { Width = 600, Height = 500, Background = Brushes.White, Content = viewer };

        window.Show();

        Assert.True(await viewer.LoadTextAsync(Drawing));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var pane = viewer.GetVisualDescendants().OfType<TextEditor>().First(editor => editor.Name == "SourceEditor");

        pane.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();

        return (window, pane);
    }

    /// <summary>The gestures the headless platform names, which is what the pane binds.</summary>
    private static RawInputModifiers Command
        => RawInputModifiers.Control;

    [AvaloniaFact]
    public async Task An_Edit_Can_Be_Taken_Back_And_Put_Again()
    {
        var (window, pane) = await Host();

        pane.Document.Insert(pane.Document.TextLength, "<!-- typed -->");
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("typed", pane.Text);

        window.KeyPressQwerty(PhysicalKey.Z, Command);
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("typed", pane.Text);

        window.KeyPressQwerty(PhysicalKey.Z, Command | RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("typed", pane.Text);
    }

    [AvaloniaFact]
    public async Task Redo_Answers_To_Both_Of_Its_Gestures()
    {
        var (window, pane) = await Host();

        pane.Document.Insert(pane.Document.TextLength, "<!-- typed -->");
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.Z, Command);
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("typed", pane.Text);

        // The platform names two, and a pane that bound only the first would leave the other dead.
        window.KeyPressQwerty(PhysicalKey.Y, Command);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("typed", pane.Text);
    }

    [AvaloniaFact]
    public async Task A_Resize_Is_One_Step_To_Take_Back()
    {
        // The point of splicing spans rather than assigning the text: a resize arrives on the undo
        // stack as a step, not as a new document.
        var (window, pane) = await Host();

        var viewer = window.GetVisualDescendants().OfType<SvgViewer>().Single();

        Assert.True(viewer.Resize(new global::Svg.Skia.SvgSizeRequest(48f, null, null)));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("width=\"48\"", pane.Text);

        window.KeyPressQwerty(PhysicalKey.Z, Command);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("width=\"24\"", pane.Text);
        Assert.DoesNotContain("width=\"48\"", pane.Text);
    }
}
