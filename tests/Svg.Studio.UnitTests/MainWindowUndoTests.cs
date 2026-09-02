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
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs><e:code><e:param name="hue" type="number" default="217" /></e:code></defs>
          <rect width="24" height="24" fill="{{ hsl(hue, 74%, 55%) }}" />
        </svg>
        """;

    private static async Task<(MainWindow Window, TextEditor Pane)> Host()
    {
        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        // A window starts with nothing open, so the drawing these are about is opened here.
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"svg-studio-undo-{Guid.NewGuid():N}.svg");

        System.IO.File.WriteAllText(path, Drawing);

        try
        {
            await window.OpenAsync(new[] { path });
        }
        finally
        {
            System.IO.File.Delete(path);
        }

        Dispatcher.UIThread.RunJobs();

        var viewer = window.GetVisualDescendants().OfType<SvgViewer>().First();

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
        // On screen, not merely present: the project pane carries a search box of its own, which is
        // built with the window and sits in the tree ahead of these whether a project is open or not.
        var box = window.GetVisualDescendants().OfType<TextBox>().First(box => box.IsEffectivelyVisible);

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
