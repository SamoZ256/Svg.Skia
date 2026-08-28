using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Svg.Expressions;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// File → Export…: the same drawing written out as itself or as the C# that draws it.
/// </summary>
/// <remarks>
/// Driven by handing the window a path rather than through the menu, because the panel that would
/// produce one is a modal — the same reason the closing prompt is replaced rather than answered.
/// </remarks>
public class MainWindowExportTests
{
    /// <summary>
    /// Parametric on purpose: what an export has to get right is that the drawing's parameters
    /// reach the generated code rather than the values the panel happens to be holding.
    /// </summary>
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg"
             xmlns:e="https://svg.skia/expr/1.0"
             viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code>
              <e:param name="size" type="number" default="12" min="1" max="24" />
            </e:code>
          </defs>
          <rect width="{{ size }}" height="{{ size }}" fill="#00ff00" />
        </svg>
        """;

    private static async Task<MainWindow> Host()
    {
        var path = Path.Combine(Path.GetTempPath(), $"svg-export-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, Drawing);

        var window = new MainWindow(path);

        window.Show();
        Dispatcher.UIThread.RunJobs();

        await Task.Delay(100).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(Viewer(window).Document);

        return window;
    }

    private static SvgViewer Viewer(MainWindow window)
        => window.GetVisualDescendants().OfType<SvgViewer>().First();

    private static string Target(string extension)
        => Path.Combine(Path.GetTempPath(), $"svg-export-{Guid.NewGuid():N}{extension}");

    [AvaloniaFact]
    public async Task Exporting_To_Svg_Writes_The_Drawing()
    {
        var window = await Host();
        var target = Target(".svg");

        try
        {
            Assert.True(await window.ExportAsync(target));
            Assert.Equal(Drawing, File.ReadAllText(target));
        }
        finally
        {
            File.Delete(target);
        }
    }

    [AvaloniaFact]
    public async Task Exporting_To_CSharp_Writes_The_Code_That_Draws_It()
    {
        var window = await Host();
        var target = Target(".cs");

        try
        {
            Assert.True(await window.ExportAsync(target));

            var code = File.ReadAllText(target);

            // Named after the file — as an identifier, which a temporary name is not — in the
            // namespace svgc would have put it in.
            Assert.Contains($"public static class {SvgExport.ClassName(target)}", code);
            Assert.Contains("namespace Svg", code);

            // What the drawing declares is what the generated code takes.
            Assert.Contains("float size", code);
        }
        finally
        {
            File.Delete(target);
        }
    }

    /// <summary>
    /// The drawing is generated from its own text, not from the picture on screen: the viewer binds
    /// the panel's values into its model, and generating from that would emit the slider's position
    /// as a constant and leave the parameter unread.
    /// </summary>
    [AvaloniaFact]
    public async Task Exporting_To_CSharp_Ignores_The_Values_The_Panel_Is_Holding()
    {
        var window = await Host();
        var declared = Target(".cs");
        var bound = Target(".cs");

        try
        {
            Assert.True(await window.ExportAsync(declared));

            Assert.True(Viewer(window).TrySetParameterValue("size", ExprValue.Number(7f)));
            Dispatcher.UIThread.RunJobs();

            Assert.True(await window.ExportAsync(bound));

            // The file name is the class name, so they are compared with that difference removed.
            Assert.Equal(
                File.ReadAllText(declared).Replace(SvgExport.ClassName(declared), "Drawing"),
                File.ReadAllText(bound).Replace(SvgExport.ClassName(bound), "Drawing"));
        }
        finally
        {
            File.Delete(declared);
            File.Delete(bound);
        }
    }

    /// <summary>
    /// A path with no extension is a drawing, and is named like one.
    /// </summary>
    /// <remarks>
    /// A backstop for a path that did not come from a picker: the save panel appends the extension
    /// of the type chosen in it, which is what makes the name the answer to which form was meant.
    /// </remarks>
    [AvaloniaFact]
    public async Task Exporting_To_A_Name_Without_An_Extension_Writes_An_Svg()
    {
        var window = await Host();
        var asked = Target(string.Empty);
        var written = SvgExport.PathFor(asked);

        try
        {
            Assert.True(await window.ExportAsync(asked));

            Assert.Equal(asked + ".svg", written);
            Assert.Equal(Drawing, File.ReadAllText(written));
            Assert.False(File.Exists(asked));
        }
        finally
        {
            File.Delete(asked);
            File.Delete(written);
        }
    }

    /// <summary>An edit that has not been saved is part of the drawing, and so part of the export.</summary>
    [AvaloniaFact]
    public async Task Exporting_Writes_What_The_Pane_Is_Holding()
    {
        var window = await Host();
        var viewer = Viewer(window);
        var target = Target(".svg");

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var edited = Drawing.Replace("#00ff00", "#ff0000");

        window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().First().Document.Text = edited;
        Dispatcher.UIThread.RunJobs();

        try
        {
            Assert.True(await window.ExportAsync(target));
            Assert.Equal(edited, File.ReadAllText(target));

            // Exported, not saved: the drawing still has changes that are not in its own file.
            Assert.True(viewer.IsSourceModified);
        }
        finally
        {
            File.Delete(target);
        }
    }
}
