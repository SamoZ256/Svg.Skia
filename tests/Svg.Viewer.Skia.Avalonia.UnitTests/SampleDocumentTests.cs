using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// The drawing the sample application opens on launch, so a broken one is a failing test rather than
/// a blank window someone has to notice.
/// </summary>
public class SampleDocumentTests
{
    private static string SamplePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is { } && !File.Exists(Path.Combine(directory.FullName, "Svg.Skia.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!.FullName, "src", "Svg.Studio", "Assets", "parametric.svg");
    }

    [AvaloniaFact]
    public async Task The_Sample_Drawing_Opens_With_Every_Kind_Of_Parameter()
    {
        var path = SamplePath();
        Assert.True(File.Exists(path), $"The sample drawing is missing: {path}");

        var viewer = new SvgViewer();
        var window = new Window { Width = 600, Height = 400, Background = Brushes.White, Content = viewer };
        window.Show();

        Assert.True(await viewer.LoadAsync(path));
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

        Assert.Null(viewer.Document!.DeclarationError);
        Assert.Equal(4, viewer.Parameters.Count);

        // One of each, which is what makes it worth shipping as the thing the app opens with.
        Assert.Contains(viewer.Parameters, p => p is SvgViewerNumberParameter);
        Assert.Contains(viewer.Parameters, p => p is SvgViewerColorParameter);
        Assert.Contains(viewer.Parameters, p => p is SvgViewerBooleanParameter);

        // The declared ranges reached the rows rather than the 0..1 fallback.
        var hue = viewer.Parameters.OfType<SvgViewerNumberParameter>().Single(p => p.Name == "hue");
        Assert.Equal(0d, hue.Minimum);
        Assert.Equal(360d, hue.Maximum);
        Assert.Equal(1d, hue.Step);

        Assert.NotNull(viewer.Svg!.ExpressionValues);

        window.Close();
    }
}
