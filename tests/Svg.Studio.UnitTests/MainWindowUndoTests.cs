using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// Edit → Undo and Redo.
/// </summary>
/// <remarks>
/// The pane binds the same gestures itself, so what the menu adds is a place to find them and, on
/// macOS, a key equivalent that arrives wherever the caret is — which is why these are about where
/// the command lands rather than about the undo stack, whose behaviour is the viewer's.
/// </remarks>
public class MainWindowUndoTests
{
    private static async Task<(MainWindow Window, TextEditor Pane)> Host()
    {
        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var viewer = window.GetVisualDescendants().OfType<SvgViewer>().First();

        for (var attempt = 0; attempt < 200 && viewer.Document is null; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.NotNull(viewer.Document);

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var pane = window.GetVisualDescendants().OfType<TextEditor>().First(editor => editor.Name == "SourceEditor");

        return (window, pane);
    }

    [AvaloniaFact]
    public async Task Undo_And_Redo_Reach_The_Drawing_In_The_Selected_Tab()
    {
        var (window, pane) = await Host();

        pane.Document.Insert(pane.Document.TextLength, "<!-- typed -->");
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.Undo());
        Assert.DoesNotContain("typed", pane.Text);

        Assert.True(window.Redo());
        Assert.Contains("typed", pane.Text);
    }

    [AvaloniaFact]
    public async Task A_Box_Being_Typed_In_Keeps_Its_Own_Undo()
    {
        // The menu's gesture is the window's on macOS, so it arrives even while somebody is editing
        // a parameter; the drawing's stack must not answer for the box's.
        var (window, pane) = await Host();

        pane.Document.Insert(pane.Document.TextLength, "<!-- typed -->");
        Dispatcher.UIThread.RunJobs();

        // The bundled drawing declares parameters, so the panel is already showing value boxes.
        var box = window.GetVisualDescendants().OfType<TextBox>().First();

        box.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.Undo());

        // The drawing is untouched: the box was asked, and answered for itself.
        Assert.Contains("typed", pane.Text);
    }

    [AvaloniaFact]
    public async Task The_Menu_Shows_The_Gesture_The_Platform_Uses()
    {
        var (window, _) = await Host();

        var edit = Assert.IsType<NativeMenuItem>(
            NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>().Single(item => item.Header == "Edit"));

        var undo = edit.Menu!.Items.OfType<NativeMenuItem>().Single(item => item.Header == "Undo");
        var expected = window.GetPlatformSettings()!.HotkeyConfiguration.Undo[0];

        Assert.Equal(expected, undo.Gesture);
    }
}
