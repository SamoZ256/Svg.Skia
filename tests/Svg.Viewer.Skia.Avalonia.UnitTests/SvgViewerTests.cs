using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;
using Svg.Expressions;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

public class SvgViewerTests
{
    private const string Parametric = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code>
              <e:param name="tint" type="color" default="#ff0000" />
              <e:param name="fade" type="number" default="1" min="0" max="1" />
              <e:param name="on" type="boolean" default="true" />
            </e:code>
          </defs>
          <rect x="0" y="0" width="24" height="24" fill="{{ tint }}" opacity="{{ fade }}" visibility="{{ on }}" />
        </svg>
        """;

    private static (Window Window, SvgViewer Viewer) Host()
    {
        var viewer = new SvgViewer();
        var window = new Window
        {
            Width = 500,
            Height = 300,
            Background = Brushes.White,
            Content = viewer
        };

        window.Show();

        return (window, viewer);
    }

    private static async Task<(Window, SvgViewer)> HostLoaded(string markup = Parametric)
    {
        var (window, viewer) = Host();
        Assert.True(await viewer.LoadTextAsync(markup));
        Dispatcher.UIThread.RunJobs();

        return (window, viewer);
    }

    [AvaloniaFact]
    public async Task Loading_Builds_A_Row_Per_Declared_Parameter()
    {
        var (window, viewer) = await HostLoaded();

        Assert.Collection(
            viewer.Parameters,
            p => Assert.IsType<SvgViewerColorParameter>(p),
            p => Assert.IsType<SvgViewerNumberParameter>(p),
            p => Assert.IsType<SvgViewerBooleanParameter>(p));

        window.Close();
    }

    [AvaloniaFact]
    public async Task Loading_Binds_The_Declared_Defaults_Rather_Than_Leaving_Placeholders()
    {
        // A document opened in a viewer should look like what its author declared, not like grey.
        var (window, viewer) = await HostLoaded();

        Assert.NotNull(viewer.Svg!.ExpressionValues);
        Assert.Equal(255, viewer.Svg.ExpressionValues!["tint"].Red);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Changing_A_Value_Reaches_The_Drawing()
    {
        var (window, viewer) = await HostLoaded();

        Assert.True(viewer.TrySetParameterValue("tint", ExprValue.Color(0, 0, 255, 255)));
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

        Assert.Equal(0, viewer.Svg!.ExpressionValues!["tint"].Red);
        Assert.Equal(255, viewer.Svg.ExpressionValues["tint"].Blue);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Changing_A_Value_Changes_What_Is_Painted()
    {
        var (window, viewer) = await HostLoaded();

        viewer.ShowParameterPanel = false;
        viewer.ShowToolBar = false;
        viewer.ShowStatusBar = false;
        Dispatcher.UIThread.RunJobs();

        var before = CentrePixel(window);
        Assert.True(before.Red > 200 && before.Blue < 60, $"Expected the declared red, found {before}.");

        Assert.True(viewer.TrySetParameterValue("tint", ExprValue.Color(0, 0, 255, 255)));
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

        var after = CentrePixel(window);
        Assert.True(after.Blue > 200 && after.Red < 60, $"Expected blue, found {after}.");

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Rejected_Value_Leaves_The_Previous_Rendering_Alone()
    {
        var (window, viewer) = await HostLoaded();

        var picture = viewer.Svg!.Picture;

        // The wrong type for the parameter: the row refuses it, so nothing is even attempted.
        Assert.False(viewer.TrySetParameterValue("tint", ExprValue.Number(1f)));
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

        Assert.Same(picture, viewer.Svg.Picture);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Malformed_Declaration_Block_Shows_An_Error_And_Still_Draws()
    {
        var (window, viewer) = Host();

        var errors = new List<string>();
        viewer.ErrorRaised += (_, message) => errors.Add(message);

        Assert.True(await viewer.LoadTextAsync("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="tint" type="color" min="0" max="1" /></e:code></defs>
              <rect x="0" y="0" width="24" height="24" fill="#ff0000" />
            </svg>
            """));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(errors, m => m.Contains("cannot carry min, max or step", StringComparison.Ordinal));
        Assert.Empty(viewer.Parameters);
        Assert.NotNull(viewer.Svg!.Picture);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Failed_Load_Keeps_The_Document_That_Is_Open()
    {
        var (window, viewer) = await HostLoaded();

        var document = viewer.Document;

        Assert.False(await viewer.LoadTextAsync("this is not svg"));
        Dispatcher.UIThread.RunJobs();

        Assert.Same(document, viewer.Document);
        Assert.NotNull(viewer.Svg!.Picture);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Reloading_The_Same_Parameters_Keeps_The_Values_Already_Set()
    {
        // Opening the same drawing again must not silently discard what someone has set up.
        var (window, viewer) = await HostLoaded();

        Assert.True(viewer.TrySetParameterValue("fade", ExprValue.Number(0.25f)));
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

        Assert.True(await viewer.LoadTextAsync(Parametric));
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

        Assert.Equal(0.25f, viewer.Svg!.ExpressionValues!["fade"].AsNumber, 4);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Resetting_Parameters_Returns_Them_To_Their_Defaults()
    {
        var (window, viewer) = await HostLoaded();

        Assert.True(viewer.TrySetParameterValue("fade", ExprValue.Number(0.25f)));
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

        viewer.ResetParameters();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

        Assert.Equal(1f, viewer.Svg!.ExpressionValues!["fade"].AsNumber, 4);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Document_With_No_Parameters_Still_Opens()
    {
        var (window, viewer) = await HostLoaded("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect x="0" y="0" width="24" height="24" fill="#00ff00" />
            </svg>
            """);

        Assert.Empty(viewer.Parameters);
        Assert.NotNull(viewer.Svg!.Picture);

        window.Close();
    }

    [AvaloniaFact]
    public void The_Chrome_Can_Be_Turned_Off_For_Embedding()
    {
        var (window, viewer) = Host();

        viewer.ShowToolBar = false;
        viewer.ShowParameterPanel = false;
        viewer.ShowStatusBar = false;

        Assert.False(viewer.ShowToolBar);
        Assert.False(viewer.ShowParameterPanel);
        Assert.False(viewer.ShowStatusBar);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Opening_Goes_Through_The_File_Dialog_Service()
    {
        var (window, viewer) = Host();

        var path = Path.Combine(Path.GetTempPath(), $"svg-viewer-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, Parametric);

        try
        {
            viewer.FileDialogService = new StubFileDialogService(path);

            Assert.True(await viewer.OpenAsync());
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(path, viewer.DocumentPath);
            Assert.Equal(3, viewer.Parameters.Count);
        }
        finally
        {
            File.Delete(path);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_Host_That_Takes_The_Open_Request_Gets_The_Paths_And_The_Viewer_Loads_Nothing()
    {
        // What the shell does: every file the user picks or drops belongs in a tab of its own, so the
        // viewer that was asked must not replace the drawing it is showing.
        var (window, viewer) = await HostLoaded();

        var document = viewer.Document;
        var requested = new List<string>();

        viewer.OpenRequested += (_, request) =>
        {
            requested.AddRange(request.Paths);
            request.Handled = true;
        };

        Assert.True(await viewer.OpenAsync(new[] { "one.svg", "two.svg" }));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "one.svg", "two.svg" }, requested);
        Assert.Same(document, viewer.Document);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Handled_Request_Waits_For_What_The_Host_Handed_Back()
    {
        // Opening is asynchronous wherever it happens, and a host that places the paths itself is
        // the only one that knows when they are open. Without this the call returns while the files
        // are still being read, and anything that acts on "opened" acts too early.
        var (window, viewer) = Host();

        var host = new TaskCompletionSource();

        viewer.OpenRequested += (_, request) =>
        {
            request.Handled = true;
            request.Completion = host.Task;
        };

        var open = viewer.OpenAsync(new[] { "one.svg" });

        Assert.False(open.IsCompleted);

        host.SetResult();

        Assert.True(await open);

        window.Close();
    }

    [AvaloniaFact]
    public async Task An_Unhandled_Open_Request_Still_Loads_The_First_Path_That_Works()
    {
        var (window, viewer) = Host();

        var path = Path.Combine(Path.GetTempPath(), $"svg-viewer-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, Parametric);

        try
        {
            Assert.True(await viewer.OpenAsync(new[] { "missing.svg", path }));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(path, viewer.DocumentPath);
        }
        finally
        {
            File.Delete(path);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Closing_Releases_The_Document_And_Empties_The_Viewer()
    {
        // A host that discards a viewer -- a tab being closed -- is the only thing that disposes the
        // last document loaded into it.
        var (window, viewer) = await HostLoaded();

        var document = viewer.Document!;

        viewer.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewer.Document);
        Assert.Empty(viewer.Parameters);
        Assert.Null(document.Svg.Picture);

        window.Close();
    }

    private static SKColor CentrePixel(Window window)
    {
        var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("No rendered frame was captured.");

        var path = Path.Combine(Path.GetTempPath(), $"svg-viewer-frame-{Guid.NewGuid():N}.png");
        frame.Save(path);

        try
        {
            using var bitmap = SKBitmap.Decode(path);
            Assert.NotNull(bitmap);

            return bitmap!.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class StubFileDialogService : ISvgViewerFileDialogService
    {
        private readonly string? _path;

        public StubFileDialogService(string? path) => _path = path;

        public Task<string?> OpenSvgAsync(TopLevel? owner) => Task.FromResult(_path);
    }
}
