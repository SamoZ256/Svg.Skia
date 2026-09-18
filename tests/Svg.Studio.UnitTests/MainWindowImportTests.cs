using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// Importing a PaintCode document: the project it makes, with the drawings in it, and the project
/// pane it leaves open on them — unsaved, until somebody who has seen it writes it.
/// </summary>
/// <remarks>
/// The fixture is two canvases, one of them used as a symbol by the other, with a bound fill and a
/// name the second instance rebinds — small enough to read in a diff of the test that asserts it.
/// Everything the real thing exercises is covered against the real thing by the opt-in facts in
/// Svg.PaintCode.UnitTests, which a 16 MB product document cannot be committed for.
/// </remarks>
public class MainWindowImportTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [AvaloniaFact]
    public async Task An_Import_Opens_The_Project_It_Made()
    {
        var window = Shown();

        Assert.True(await window.ImportPaintCodeAsync(Sample()));

        Dispatcher.UIThread.RunJobs();

        // Nothing is named until somebody says where it goes.
        Assert.Equal("Untitled", window.Workspace!.Name);
        Assert.Null(window.Workspace.Document.Path);

        Assert.Equal(new[] { "badge", "host" }, window.Workspace.Document.Root.Drawings.Select(drawing => drawing.Name).OrderBy(name => name).ToArray());
    }

    /// <summary>
    /// A conversion is unsaved work like any other, and unnamed as well. It is held in the window
    /// until somebody has looked at it and said where it goes, and the document it came from is the
    /// only file in the directory until they do.
    /// </summary>
    [AvaloniaFact]
    public async Task An_Import_Writes_Nothing_Until_It_Is_Saved()
    {
        var window = Shown();
        var target = Path.Combine(_directory, "icons.svgstudio");

        window.AskWhereToSave = suggested =>
        {
            _offered = suggested;

            return Task.FromResult<string?>(target);
        };

        Assert.True(await window.ImportPaintCodeAsync(Sample()));

        Dispatcher.UIThread.RunJobs();

        Assert.True(window.Workspace!.IsEdited);
        Assert.StartsWith("• ", window.Title, StringComparison.Ordinal);
        Assert.Equal(new[] { "sample.pcvd" }, Directory.EnumerateFileSystemEntries(_directory).Select(Path.GetFileName).ToArray());

        await window.SaveAsync();

        // Asked where, with the document's own name offered — and written there and nowhere else.
        Assert.Equal("sample.svgstudio", _offered);
        Assert.False(window.Workspace.IsEdited);
        Assert.Equal("icons.svgstudio", window.Workspace.Name);
        Assert.Equal(new[] { "icons.svgstudio", "sample.pcvd" }, Directory.EnumerateFileSystemEntries(_directory).Select(Path.GetFileName).OrderBy(name => name).ToArray());
        var written = ProjectDocument.Load(target);

        Assert.Equal(
            new[] { "badge", "host" },
            written.Root.Drawings.Select(drawing => drawing.Name).OrderBy(name => name).ToArray());

        // Where each canvas sat survives the write and the read, which is what the format's
        // both-or-neither rule is checked by: a lone x would not have loaded at all.
        Assert.All(written.Root.Drawings, drawing => Assert.True(drawing.HasPosition));
    }

    /// <summary>
    /// The window's own path takes the arrangement too — the conversion runs off the UI thread, and
    /// a place dropped on the way back would show as a board that is a grid again.
    /// </summary>
    [AvaloniaFact]
    public async Task An_Import_Opens_On_A_Placed_Project()
    {
        var window = Shown();

        Assert.True(await window.ImportPaintCodeAsync(Sample()));

        Dispatcher.UIThread.RunJobs();

        var root = window.Workspace!.Document.Root;

        Assert.All(root.Children.OfType<ProjectGroup>(), group => Assert.True(group.HasPosition));
        Assert.All(root.Drawings, drawing => Assert.True(drawing.HasPosition));
    }

    [AvaloniaFact]
    public async Task A_Save_Nobody_Goes_Through_With_Writes_Nothing()
    {
        var window = Shown();

        window.AskWhereToSave = _ => Task.FromResult<string?>(null);

        Assert.True(await window.ImportPaintCodeAsync(Sample()));

        Dispatcher.UIThread.RunJobs();

        await window.SaveAsync();

        // Still in the window, still unsaved, and the directory as it was.
        Assert.True(window.Workspace!.IsEdited);
        Assert.Null(window.Workspace.Document.Path);
        Assert.Equal(new[] { "sample.pcvd" }, Directory.EnumerateFileSystemEntries(_directory).Select(Path.GetFileName).ToArray());
    }

    [AvaloniaFact]
    public async Task An_Opened_Document_Is_Converted_Into_The_Window()
    {
        var window = Shown();
        var source = Sample();

        await window.OpenAsync(new[] { source });

        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Untitled", window.Workspace!.Name);

        // The document, because that is all the dialog has to name: what it produces is not a file.
        Assert.Equal(source, _asked);
    }

    [AvaloniaFact]
    // Opening a drawing cannot be declined, so the document that is converted instead of opened is
    // the one thing here that has to be, and declining has to leave the disk as it was.
    public async Task A_Conversion_That_Is_Declined_Writes_Nothing()
    {
        var window = Shown();

        _convert = null;

        await window.OpenAsync(new[] { Sample() });

        Dispatcher.UIThread.RunJobs();

        Assert.Null(window.Workspace);
        Assert.Equal(new[] { "sample.pcvd" }, Directory.EnumerateFileSystemEntries(_directory).Select(Path.GetFileName).ToArray());
    }

    [AvaloniaTheory]
    [InlineData(false, "type=\"number\"")]
    [InlineData(true, "type=\"integer\"")]
    public async Task The_Box_The_Dialog_Offers_Decides_How_A_Whole_Number_Is_Written(bool integers, string written)
    {
        var window = Shown();

        _convert = integers;

        await window.OpenAsync(new[] { Sample() });

        Dispatcher.UIThread.RunJobs();

        var text = window.Workspace!.Document.Root.Drawings.First(drawing => drawing.Name == "badge").Text;

        Assert.Contains("<e:param name=\"level\" " + written, text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Drawing_Keeps_The_Expression_That_Drove_It_In_PaintCode()
    {
        var window = Shown();

        await window.ImportPaintCodeAsync(Sample());

        var text = window.Workspace!.Document.Root.Drawings.First(drawing => drawing.Name == "badge").Text;

        Assert.Contains("<e:param name=\"colorPurple\" type=\"color\"", text);
        Assert.Contains("fill=\"{{ isLight ? colorPurple : colorPurple }}\"", text);
    }

    [AvaloniaFact]
    public async Task An_Import_That_Could_Not_Carry_Something_Across_Says_So()
    {
        var window = Shown();

        await window.ImportPaintCodeAsync(Sample());

        Assert.Contains("could not be carried across", _said);

        // What the document itself is missing is listed apart from the rest and before it: a symbol
        // naming a canvas the document has no copy of is not something this converter can ever fix,
        // and among a hundred caveats about what SVG cannot say it read as one more of them.
        Assert.Contains("the document itself is missing:", _said);
        Assert.Contains("the document has no canvas called", _said);
    }

    private string _said = string.Empty;

    private bool? _convert = false;

    private string? _asked;

    /// <summary>The name the save panel was told to offer, for the test that reads it.</summary>
    private string? _offered;

    // Every one of these imports has something to report, and the dialogs -- the one that reports it
    // and the one that asks whether to convert at all -- wait for a click a headless run never makes.
    private MainWindow Shown()
    {
        var window = new MainWindow();
        window.Announce = (_, message) =>
        {
            _said = message;

            return Task.CompletedTask;
        };

        window.ConfirmConvert = source =>
        {
            _asked = source;

            return Task.FromResult(_convert);
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    private string Sample()
    {
        var path = Path.Combine(_directory, "sample.pcvd");

        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestAssets", "sample.pcvd"), path, overwrite: true);

        return path;
    }
}
