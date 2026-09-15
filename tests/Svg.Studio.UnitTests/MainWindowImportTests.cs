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
/// Importing a PaintCode document: the drawings and the project it writes, and the project pane it
/// leaves open on them.
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
    public async Task An_Import_Opens_The_Project_It_Wrote()
    {
        var window = Shown();

        Assert.True(await window.ImportPaintCodeAsync(Sample(), Path.Combine(_directory, "icons")));

        Dispatcher.UIThread.RunJobs();

        Assert.Equal("sample.svgcproj", window.Workspace!.Name);
        Assert.True(File.Exists(Path.Combine(_directory, "icons", "desk", "badge.svg")));
        Assert.True(File.Exists(Path.Combine(_directory, "icons", "desk", "host.svg")));
    }

    [AvaloniaFact]
    public async Task A_Dropped_Document_Is_Imported_Beside_Itself()
    {
        var window = Shown();
        var source = Sample();

        await window.OpenAsync(new[] { source });

        Dispatcher.UIThread.RunJobs();

        Assert.Equal("sample.svgcproj", window.Workspace!.Name);
        Assert.True(File.Exists(Path.Combine(_directory, "sample", "sample.svgcproj")));
    }

    [AvaloniaFact]
    public async Task A_Drawing_Keeps_The_Expression_That_Drove_It_In_PaintCode()
    {
        var window = Shown();

        await window.ImportPaintCodeAsync(Sample(), Path.Combine(_directory, "icons"));

        var text = File.ReadAllText(Path.Combine(_directory, "icons", "desk", "badge.svg"));

        Assert.Contains("<e:param name=\"colorPurple\" type=\"color\"", text);
        Assert.Contains("fill=\"{{ isLight ? colorPurple : colorPurple }}\"", text);
    }

    [AvaloniaFact]
    public async Task An_Import_That_Could_Not_Carry_Something_Across_Says_So()
    {
        var window = Shown();

        await window.ImportPaintCodeAsync(Sample(), Path.Combine(_directory, "icons"));

        Assert.Contains("could not be carried across", _said);

        // What the document itself is missing is listed apart from the rest and before it: a symbol
        // naming a canvas the document has no copy of is not something this converter can ever fix,
        // and among a hundred caveats about what SVG cannot say it read as one more of them.
        Assert.Contains("the document itself is missing:", _said);
        Assert.Contains("the document has no canvas called", _said);
    }

    private string _said = string.Empty;

    // Every one of these imports has something to report, and the dialog that reports it waits for a
    // click that a headless run never makes.
    private MainWindow Shown()
    {
        var window = new MainWindow();
        window.Announce = (_, message) =>
        {
            _said = message;

            return Task.CompletedTask;
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
