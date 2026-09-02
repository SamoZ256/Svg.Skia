using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using SkiaSharp;
using Svg.Expressions;
using Svg.Highlighting;
using Svg.SourceEditing;
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

    private const string Plain = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect x="0" y="0" width="24" height="24" fill="#00ff00" />
        </svg>
        """;

    /// <summary>What a recipe makes of <see cref="Plain"/>: a parameter, and a colour driven by it.</summary>
    private const string Rewritten = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs><e:code><e:param name="hue" type="number" default="0" min="0" max="360" /></e:code></defs>
          <rect x="0" y="0" width="24" height="24" fill="{{ hsl(hue, 100%, 50%) }}" />
        </svg>
        """;

    [AvaloniaFact]
    public async Task A_Rewrite_Decides_What_Is_Painted()
    {
        var (window, viewer) = Host();

        viewer.ShowDeclarationPanel = false;
        viewer.ShowToolBar = false;
        viewer.ShowStatusBar = false;

        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".svg");

        File.WriteAllText(path, Plain);

        try
        {
            Assert.True(await viewer.LoadAsync(path));
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

            var plain = CentrePixel(window);
            Assert.True(plain.Green > 200 && plain.Red < 60, $"Expected the file's green, found {plain}.");

            // The same file, drawn through a rewrite that recolours it — what a recipe does.
            viewer.Rewrite = _ => Rewritten;

            Assert.True(await viewer.LoadAsync(path));
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

            var rewritten = CentrePixel(window);
            Assert.True(rewritten.Red > 200 && rewritten.Green < 60, $"Expected the rewrite's red, found {rewritten}.");

            // And the parameter it declared drives the colour, which is the whole point of showing it.
            Assert.True(viewer.TrySetParameterValue("hue", ExprValue.Number(240f)));
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

            var bound = CentrePixel(window);
            Assert.True(bound.Blue > 200 && bound.Red < 60, $"Expected blue, found {bound}.");
        }
        finally
        {
            File.Delete(path);
            window.Close();
        }
    }

    /// <summary>A document of the host's own for the panel to write into, standing in for a recipe.</summary>
    private sealed class Elsewhere : ISvgViewerDeclarationTarget
    {
        public Elsewhere(string text) => Text = text;

        public string Text { get; private set; }

        public bool Apply(IReadOnlyList<SvgTextEdit> edits)
        {
            Text = SvgTextEdit.ApplyAll(Text, edits);

            return true;
        }
    }

    [AvaloniaFact]
    public async Task A_Rewritten_Drawing_Declares_Where_Its_Declarations_Are()
    {
        var (window, viewer) = Host();

        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".svg");

        File.WriteAllText(path, Plain);

        try
        {
            viewer.Rewrite = _ => Rewritten;

            // A drawing built through a rewrite declares things its own text has never heard of, so
            // the panel's commands have to write where they came from.
            var elsewhere = new Elsewhere("""
                <recipe xmlns="https://svg.skia/expr/1.0">
                  <code>
                    <param name="hue" type="number" default="0" min="0" max="360" />
                  </code>
                </recipe>
                """);

            viewer.DeclarationTarget = elsewhere;

            Assert.True(await viewer.LoadAsync(path));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("hue", Assert.Single(viewer.Parameters).Name);

            // The panel is offered, not taken away: the rows are what a recipe is dragged by.
            var add = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "AddButton");

            Assert.True(add.IsVisible);
            Assert.All(
                window.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("edit")),
                button => Assert.True(button.IsVisible));

            Assert.True(viewer.CommitLet(new SvgViewerLet(null) { Name = "accent", Expression = "hsl(hue, 74%, 55%)" }));

            // Into the host's document, and nowhere near the drawing — which is the whole reason a
            // host sets one: a recipe refuses a document that already declares for itself.
            Assert.Contains("""<let name="accent">hsl(hue, 74%, 55%)</let>""", elsewhere.Text);
            Assert.Equal(Plain, viewer.Source);
            Assert.False(viewer.IsSourceModified);

            // Cleared, and the drawing is where the panel writes again.
            viewer.DeclarationTarget = null;

            Assert.True(await viewer.LoadTextAsync(Rewritten));
            Dispatcher.UIThread.RunJobs();

            Assert.True(viewer.CommitLet(new SvgViewerLet(null) { Name = "accent", Expression = "1" }));
            Assert.Contains("accent", viewer.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task A_Side_Panel_Shares_The_Right_Pane_With_The_Parameters()
    {
        var (window, viewer) = await HostLoaded();

        var host = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "DeclarationPanelHost");

        // Nothing set, so the pane is what it always was: the declarations, with no strip over them.
        Assert.IsType<SvgViewerDeclarationPanel>(host.Child);

        var mine = new TextBlock { Text = "the host's own" };

        viewer.SidePanels = new[] { new SvgViewerPane("Project", mine) };
        Dispatcher.UIThread.RunJobs();

        var tabs = Assert.IsType<TabControl>(host.Child);

        // First, and so the one shown, rather than filed behind the parameters: a host sets one
        // because it has something to say.
        Assert.Equal(new[] { "Project", "Parameters" }, tabs.Items.OfType<TabItem>().Select(item => (string)item.Header!));
        Assert.Equal(0, tabs.SelectedIndex);
        Assert.Same(mine, ((TabItem)tabs.Items[0]!).Content);

        // Several of them, in the order they were given, and the parameters still last.
        var second = new TextBlock { Text = "and another" };

        viewer.SidePanels = new[] { new SvgViewerPane("Project", mine), new SvgViewerPane("Colours", second) };
        Dispatcher.UIThread.RunJobs();

        tabs = Assert.IsType<TabControl>(host.Child);

        Assert.Equal(
            new[] { "Project", "Colours", "Parameters" },
            tabs.Items.OfType<TabItem>().Select(item => (string)item.Header!));
        Assert.Same(second, ((TabItem)tabs.Items[1]!).Content);

        viewer.SidePanels = System.Array.Empty<SvgViewerPane>();
        Dispatcher.UIThread.RunJobs();

        // And back, with the declarations panel itself rather than a new one — it is the viewer's,
        // and everything wired to it is still wired.
        Assert.IsType<SvgViewerDeclarationPanel>(host.Child);
        Assert.NotEmpty(viewer.Parameters!);
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

        viewer.ShowDeclarationPanel = false;
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

        // The count and where to look, not the compiler's words: those are marked on the line the
        // range is written on, which is somewhere a status bar cannot point.
        Assert.Contains(errors, m => m.Contains("1 error", StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("Source pane", StringComparison.Ordinal));
        Assert.Contains(viewer.SourceDiagnostics, d => d.Message.Contains("cannot carry min, max or step", StringComparison.Ordinal));

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
    public async Task A_File_That_Will_Not_Open_Says_Which_File()
    {
        // `No drawing open.` is true of the viewer and no answer to someone who handed it a path.
        var (window, viewer) = Host();

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-unreadable.svg");
        File.WriteAllText(path, "<svg><rect");

        try
        {
            Assert.False(await viewer.LoadAsync(path));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal($"{Path.GetFileName(path)} couldn't be opened", Status(viewer).Text);
        }
        finally
        {
            File.Delete(path);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_Pane_With_No_Drawing_Behind_It_Claims_Nothing_About_One()
    {
        var (window, viewer) = Host();

        // Before anything is opened there is no drawing to declare nothing.
        Assert.False(Empty(viewer).IsVisible);

        Assert.False(await viewer.LoadTextAsync("this is not svg"));
        Dispatcher.UIThread.RunJobs();

        Assert.False(Empty(viewer).IsVisible);

        // A drawing that really does declare none is the case the label is for.
        Assert.True(await viewer.LoadTextAsync("<svg xmlns=\"http://www.w3.org/2000/svg\" />"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(Empty(viewer).IsVisible);

        viewer.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(Empty(viewer).IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Failed_Load_Over_An_Open_Drawing_Still_Reports_The_Open_One()
    {
        // The other half of the same branch: something is open, it stayed, and its name is still
        // the true answer to what the status bar is asked.
        var (window, viewer) = await HostLoaded();

        var before = Status(viewer).Text;

        Assert.False(await viewer.LoadTextAsync("this is not svg"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before, Status(viewer).Text);

        // Its rows stayed too, so the label has nothing to say about it either.
        Assert.Equal(3, viewer.Parameters.Count);
        Assert.False(Empty(viewer).IsVisible);

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
        viewer.ShowDeclarationPanel = false;
        viewer.ShowStatusBar = false;

        Assert.False(viewer.ShowToolBar);
        Assert.False(viewer.ShowDeclarationPanel);
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
        // Only the host placing the paths knows when they are open; without this the call returns
        // while the files are still being read.
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

    [AvaloniaFact]
    public async Task The_Source_Pane_Shows_The_Drawing_As_It_Was_Read()
    {
        var (window, viewer) = await HostLoaded();

        // Hidden until asked for: a viewer is for looking at the drawing.
        Assert.False(viewer.ShowSource);

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        // The text the picture was built from, comments, formatting and expressions intact.
        Assert.Equal(Parametric, PaneText(viewer));
        Assert.Contains("{{ tint }}", PaneText(viewer), StringComparison.Ordinal);
        Assert.True(Pane(viewer).Bounds.Height > 0d, "the pane is not laid out");

        window.Close();
    }

    [AvaloniaFact]
    public async Task Hiding_The_Source_Pane_Gives_Its_Room_Back_To_The_Drawing()
    {
        var (window, viewer) = await HostLoaded();

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var withSource = viewer.Canvas.Bounds.Height;

        viewer.ShowSource = false;
        Dispatcher.UIThread.RunJobs();

        // Not merely invisible: a hidden pane whose row kept its height would leave a strip of
        // nothing under the drawing.
        Assert.True(
            viewer.Canvas.Bounds.Height > withSource,
            $"the drawing did not grow back: {withSource} -> {viewer.Canvas.Bounds.Height}");

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Toolbar_Toggle_And_The_Property_Follow_Each_Other()
    {
        var (window, viewer) = await HostLoaded();

        var button = viewer.GetVisualDescendants().OfType<ToggleButton>()
            .First(b => b.Name == "SourceButton");

        button.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewer.ShowSource);

        viewer.ShowSource = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(button.IsChecked);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Drawing_Too_Large_To_Lay_Out_Is_Cut_Rather_Than_Shown_Whole()
    {
        var (window, viewer) = Host();

        // Comfortably past whatever the pane is willing to hold.
        var padding = new string(' ', 2_100_000);
        var markup = $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#00ff00" />{padding}
            </svg>
            """;

        Assert.True(await viewer.LoadTextAsync(markup));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var shown = PaneText(viewer);

        Assert.True(shown.Length < markup.Length, "the whole drawing was handed to one text block");
        Assert.Contains("more characters not shown", shown, StringComparison.Ordinal);

        // The document still carries all of it; only the pane is cut.
        Assert.Equal(markup, viewer.Document!.SourceText);

        window.Close();
    }

    [AvaloniaFact]
    public void A_Drawing_Read_From_A_Stream_Carries_Its_Text_Too()
    {
        // The loader consumes the stream, so this is the case that has to buffer to keep the text.
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Parametric));

        using var document = SvgViewerDocument.Load(stream);

        Assert.Equal(Parametric, document.SourceText);
    }

    [AvaloniaFact]
    public async Task The_Source_Pane_Colours_What_It_Shows()
    {
        var (window, viewer) = await HostLoaded();

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var runs = RealisedRuns(viewer);

        var element = runs.First(r => r.Text == "rect").Brush;
        var name = runs.First(r => r.Text == "tint").Brush;
        var fence = runs.First(r => r.Text == "{{").Brush;

        Assert.NotNull(element);
        Assert.NotNull(name);
        Assert.NotNull(fence);

        // An element, a name inside an expression and the fence around it are three different
        // things, and the pane paints them as three.
        Assert.NotEqual(element, name);
        Assert.NotEqual(name, fence);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Drawing_Of_Any_Size_Is_Coloured_Because_Only_What_Shows_Is_Built()
    {
        // Colouring used to stop above 5,000 tokens, which 43% of this repository's samples exceed.
        var (window, viewer) = Host();

        var shapes = new StringBuilder();

        for (var i = 0; i < 4_000; i++)
        {
            shapes.Append(CultureInfo.InvariantCulture, $"  <rect x=\"{i % 50}\" y=\"1\" width=\"1\" height=\"1\" fill=\"{{{{ tint }}}}\" />\n");
        }

        Assert.True(await viewer.LoadTextAsync(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 50 50\" width=\"50\" height=\"50\">\n{shapes}</svg>"));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var lines = Pane(viewer).Document.LineCount;

        Assert.True(lines > 4_000, $"only {lines} lines were prepared");

        var built = Pane(viewer).TextArea.TextView.VisualLines.Count;

        Assert.True(
            built < 200,
            $"{built} lines were built for a {lines}-line drawing; the editor is not virtualising");

        // Still coloured: the lines that exist carry expression pieces, split into the language
        // rather than left as one.
        Assert.Contains(RealisedRuns(viewer), r => r.Text == "tint");
        Assert.Contains(RealisedRuns(viewer), r => r.Text == "{{");

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Minified_Drawing_Colours_What_It_Can_And_Still_Shows_All_Of_It()
    {
        // A minified drawing is the whole file on one line: 132KB took 1.4s as a single row, 340ms
        // once the row stopped colouring past its limit.
        var (window, viewer) = Host();

        var shapes = new StringBuilder();

        for (var i = 0; i < 500; i++)
        {
            shapes.Append(CultureInfo.InvariantCulture, $"<rect x=\"{i % 50}\" y=\"1\" width=\"1\" height=\"1\" />");
        }

        var markup = $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 50 50\" width=\"50\" height=\"50\">{shapes}</svg>";

        Assert.True(await viewer.LoadTextAsync(markup));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        // The limit, plus the one piece the remainder is left as. That piece is not brushless — it
        // takes the editor's own foreground — so it is counted here like any other.
        var runs = RealisedRuns(viewer);

        Assert.True(
            runs.Count <= SvgSourceHighlighter.RowTokenLimit + 1,
            $"the line was built from {runs.Count} pieces");

        // Bounded, but nothing is missing: what is not coloured is still there to read.
        Assert.Equal(markup, PaneText(viewer));
    }

    [AvaloniaFact]
    public async Task The_Palette_Follows_The_Theme()
    {
        var (window, viewer) = await HostLoaded();

        window.RequestedThemeVariant = ThemeVariant.Dark;
        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var dark = RealisedRuns(viewer).First(r => r.Text == "rect").Brush;

        window.RequestedThemeVariant = ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();

        var light = RealisedRuns(viewer).First(r => r.Text == "rect").Brush;

        // A palette chosen for a dark background is unreadable on a white one.
        Assert.NotEqual(dark?.ToString(), light?.ToString());

        window.Close();
    }


    private const string Mistyped = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code>
              <e:param name="tint" type="color" default="#ff0000" />
            </e:code>
          </defs>
          <rect x="0" y="0" width="24" height="24" fill="{{ tnit }}" />
        </svg>
        """;

    [AvaloniaFact]
    public async Task The_Pane_Underlines_The_Name_Nothing_Declares()
    {
        var (window, viewer) = Host();

        Assert.True(await viewer.LoadTextAsync(Mistyped));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        // The mark is drawn rather than decorated, so what is asserted is where it is drawn: the
        // span the pane holds, resolved against the text it is showing.
        var one = Assert.Single(viewer.SourceDiagnostics);

        Assert.Equal("tnit", PaneText(viewer).Substring(one.Start, one.Length));

        // Something red is actually on screen, and only over that one word.
        Assert.True(ErrorPixels(window, viewer) > 0, "nothing was marked");

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Drawing_With_Nothing_Wrong_Is_Not_Marked()
    {
        var (window, viewer) = await HostLoaded();

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(viewer.SourceDiagnostics);
        Assert.Equal(0, ErrorPixels(window, viewer));

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Message_Is_On_The_Line_That_Carries_The_Mistake()
    {
        // A tooltip rather than a panel: the pane is where the file is, and a message about a name
        // is worth nothing away from the name.
        var (window, viewer) = Host();

        Assert.True(await viewer.LoadTextAsync(Mistyped));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var one = Assert.Single(viewer.SourceDiagnostics);

        // The message names what is wrong, and the span it is filed under is that word and no more,
        // so a pointer resting anywhere on it finds this and a pointer beside it does not.
        Assert.Contains("tnit", one.Message, StringComparison.Ordinal);
        Assert.Equal("tnit", PaneText(viewer).Substring(one.Start, one.Length));

        window.Close();
    }


    private const string BadlyDeclared = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code>
              <e:param name="tint" type="color" min="0" max="1" />
            </e:code>
          </defs>
          <rect x="0" y="0" width="24" height="24" fill="{{ tint }}" />
        </svg>
        """;

    [AvaloniaFact]
    public async Task A_Mistake_In_The_Declarations_Is_Marked_Where_It_Was_Written()
    {
        // It used to be a sentence in a panel above the drawing, which said what was wrong and left
        // finding it to the reader. A range on a colour is four characters in a file of forty lines.
        var (window, viewer) = Host();

        Assert.True(await viewer.LoadTextAsync(BadlyDeclared));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var one = Assert.Single(viewer.SourceDiagnostics);

        Assert.Equal("0", PaneText(viewer).Substring(one.Start, one.Length));
        Assert.True(ErrorPixels(window, viewer) > 0, "nothing was marked");

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Declaration_The_Pane_Marks_Does_Not_Bury_The_Drawing_In_Cascades()
    {
        // With the declaration refused its parameter is missing from the table, so every use of the
        // name it would have declared reads as undeclared. One mistake, one mark.
        var (window, viewer) = Host();

        Assert.True(await viewer.LoadTextAsync(BadlyDeclared));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Single(viewer.SourceDiagnostics);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Default_That_Will_Not_Resolve_Still_Opens_The_Drawing()
    {
        // clamp refuses a reversed range by throwing something that is not the language's own
        // exception, and a viewer that let that out would fail to open a file it can render.
        var (window, viewer) = Host();

        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="clamp(2, 5, 1)" />
                </e:code>
              </defs>
              <rect x="0" y="0" width="24" height="24" fill="#3fb5b5" />
            </svg>
            """;

        Assert.True(await viewer.LoadTextAsync(source));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var one = Assert.Single(viewer.SourceDiagnostics);

        Assert.Equal("clamp", PaneText(viewer).Substring(one.Start, one.Length));

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Source_Is_Shown_In_A_Monospaced_Font()
    {
        // The rows it replaced set no font at all and inherited the UI's, so a drawing's text was
        // shown in a proportional face — which for markup means nothing lines up under anything.
        var (window, viewer) = await HostLoaded();

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var editor = Pane(viewer);
        var typeface = new Typeface(editor.FontFamily);

        static double Width(Typeface typeface, double size, string text)
            => new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                size,
                Brushes.Black).Width;

        var narrow = Width(typeface, editor.FontSize, "iiiiiiii");
        var wide = Width(typeface, editor.FontSize, "mmmmmmmm");

        Assert.True(
            Math.Abs(narrow - wide) < 0.5d,
            $"'{editor.FontFamily}' is not monospaced: eight i's are {narrow:F1}px and eight m's are {wide:F1}px");

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Drawing_With_A_Mistake_Says_So_From_The_Moment_It_Opens()
    {
        // Not a reaction to anything. It used to take moving an unrelated control to find out, which
        // is the wrong way round: the drawing has been wrong since it was opened.
        var (window, viewer) = Host();

        var errors = new List<string>();
        viewer.ErrorRaised += (_, message) => errors.Add(message);

        // Deliberately without opening the pane: whether the drawing is at fault has to be knowable
        // before anyone has asked to read it.
        Assert.True(await viewer.LoadTextAsync(Mistyped));
        Dispatcher.UIThread.RunJobs();

        var opened = Assert.Single(errors.Distinct());

        Assert.Contains("1 error", opened, StringComparison.Ordinal);
        Assert.Contains("Source pane", opened, StringComparison.Ordinal);

        // A note, not a panel: it takes no room from the drawing and nothing is put over it.
        Assert.False(Overlay(viewer).IsVisible);
        Assert.Null(viewer.Canvas.Effect);

        // Not the compiler's words a second time: those belong on the line that carries them.
        Assert.DoesNotContain("tnit", opened, StringComparison.Ordinal);
        Assert.Contains(viewer.SourceDiagnostics, d => d.Message.Contains("tnit", StringComparison.Ordinal));

        // And it says the same thing after a control is touched rather than something new.
        errors.Clear();
        viewer.ResetParameters();
        Dispatcher.UIThread.RunJobs();

        Assert.All(errors, m => Assert.Equal(opened, m));

        window.Close();
    }

    /// <summary>The card put over the drawing for what cannot be said on a line of it.</summary>
    private static Grid Overlay(SvgViewer viewer)
        => viewer.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "ErrorPanel");

    /// <summary>The standing count beside the status, which says what the pane is marking.</summary>
    private static TextBlock Status(SvgViewer viewer)
        => viewer.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "StatusText");

    private static TextBlock Empty(SvgViewer viewer)
        => viewer.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "EmptyLabel");

    private static TextBlock Note(SvgViewer viewer)
        => viewer.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "NoteText");

    [AvaloniaFact]
    public async Task What_Cannot_Be_Marked_Is_Put_Over_The_Drawing_And_Blurs_It()
    {
        // No drawing means no pane to mark, so the only place left is over the one still on screen.
        // This was fill="{{ hue }}" until the pane began checking an expression against its
        // attribute; the test below pins that it is marked there instead.
        var (window, viewer) = Host();

        Assert.False(await viewer.LoadTextAsync("this is not a drawing"));
        Dispatcher.UIThread.RunJobs();

        // On opening it, without touching anything.
        Assert.True(Overlay(viewer).IsVisible);
        Assert.IsType<BlurEffect>(viewer.Canvas.Effect);

        // The frosting is a wash over the whole drawing, and it does not take the pointer with it:
        // the drawing can still be panned while it is being explained.
        var scrim = Overlay(viewer).GetVisualDescendants().OfType<Border>().First(b => b.Name == "FaultScrim");

        Assert.NotNull(scrim.Background);
        Assert.False(scrim.IsHitTestVisible);

        // And it is still there after a parameter is touched.
        viewer.ResetParameters();
        Dispatcher.UIThread.RunJobs();

        Assert.True(Overlay(viewer).IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Warning_Is_Counted_As_One_And_Not_Called_An_Error()
    {
        // <rekt> is not an element this renderer knows, so it draws nothing -- but the drawing still
        // opened, and the status bar saying "1 error" about a file that works would be a lie.
        var (window, viewer) = Host();

        Assert.True(await viewer.LoadTextAsync("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rekt x="0" y="0" width="24" height="24" />
            </svg>
            """));
        Dispatcher.UIThread.RunJobs();

        var one = Assert.Single(viewer.SourceDiagnostics);

        Assert.Equal(SvgSourceSeverity.Warning, one.Severity);
        Assert.Equal("1 warning, marked in the Source pane", Note(viewer).Text);

        // And it is not put over the drawing either: the pane has a line to say it on.
        Assert.False(Overlay(viewer).IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Errors_And_Warnings_Are_Counted_Apart()
    {
        var (window, viewer) = Host();

        Assert.True(await viewer.LoadTextAsync("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rekt />
              <rect width="abc" height="10" />
            </svg>
            """));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("1 error and 1 warning, marked in the Source pane", Note(viewer).Text);

        window.Close();
    }

    [AvaloniaFact]
    public async Task What_The_Pane_Can_Mark_Is_Not_Also_Put_Over_The_Drawing()
    {
        // fill wants a colour and hue is a number. The pane says so on the line that carries it, so
        // the card stays down rather than saying it twice.
        var (window, viewer) = Host();

        Assert.True(await viewer.LoadTextAsync("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="hue" type="number" default="204" /></e:code></defs>
              <rect x="0" y="0" width="24" height="24" fill="{{ hue }}" />
            </svg>
            """));
        Dispatcher.UIThread.RunJobs();

        var marked = Assert.Single(viewer.SourceDiagnostics);

        Assert.Equal(
            "A paint expression must be a colour expression, but this one is a number.",
            marked.Message);

        Assert.False(Overlay(viewer).IsVisible);
        Assert.Null(viewer.Canvas.Effect);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Drawing_With_Nothing_Wrong_Says_Nothing()
    {
        var (window, viewer) = Host();

        var errors = new List<string>();
        viewer.ErrorRaised += (_, message) => errors.Add(message);

        Assert.True(await viewer.LoadTextAsync(Parametric));
        Dispatcher.UIThread.RunJobs();

        viewer.ResetParameters();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(errors);
        Assert.Empty(viewer.SourceDiagnostics);

        window.Close();
    }

    /// <summary>Types into the pane and waits for the rebuild the pause triggers.</summary>
    private static async Task Type(SvgViewer viewer, string text)
    {
        Pane(viewer).Document.Text = text;
        Dispatcher.UIThread.RunJobs();

        // Real time, because the debounce is a real timer: the point of it is that it waits.
        await Task.Delay(400).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Typing_In_The_Pane_Rebuilds_The_Drawing()
    {
        var (window, viewer) = await HostLoaded();

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var before = viewer.Svg!.Picture;

        await Type(viewer, Parametric.Replace("24", "48", StringComparison.Ordinal));

        Assert.NotSame(before, viewer.Svg!.Picture);
        Assert.Contains("48", viewer.Document!.SourceText!, StringComparison.Ordinal);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Typing_Leaves_A_View_Somebody_Adjusted_Where_It_Was()
    {
        var (window, viewer) = await HostLoaded();

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        viewer.Canvas.ZoomIn();
        Dispatcher.UIThread.RunJobs();

        var scale = viewer.Canvas.Scale;
        var offsetX = viewer.Canvas.OffsetX;

        await Type(viewer, Parametric.Replace("default=\"1\"", "default=\"0.5\""));

        Assert.Equal(scale, viewer.Canvas.Scale, 6);
        Assert.Equal(offsetX, viewer.Canvas.OffsetX, 6);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Text_That_Will_Not_Parse_Keeps_The_Drawing_That_Is_Up()
    {
        // The ordinary state of a document halfway through being typed. Losing the picture at every
        // unbalanced bracket would make the pane unusable for the thing it is for.
        var (window, viewer) = await HostLoaded();

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var before = viewer.Svg!.Picture;

        await Type(viewer, "<svg><rect ");

        // The pane took it and the rebuild was attempted; the drawing is what did not follow.
        Assert.Equal("<svg><rect ", PaneText(viewer));
        Assert.Same(before, viewer.Svg!.Picture);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Relative_Image_Survives_An_Edit()
    {
        // Rebuilding through FromSvg loses it: that overload has no base URI, so a drawing renders
        // its image when opened from a path and shows the placeholder the moment it is typed in.
        var directory = Path.Combine(Path.GetTempPath(), "svg-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            using (var bitmap = new SKBitmap(20, 20))
            {
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(new SKColor(0xFF, 0x00, 0x00));
                }

                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var file = File.Create(Path.Combine(directory, "logo.png"));
                data.SaveTo(file);
            }

            var markup = """
                <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"
                     width="20" height="20" viewBox="0 0 20 20">
                  <image xlink:href="logo.png" x="0" y="0" width="20" height="20" />
                </svg>
                """;

            var path = Path.Combine(directory, "drawing.svg");
            File.WriteAllText(path, markup);

            var (window, viewer) = Host();

            Assert.True(await viewer.LoadAsync(path));
            viewer.ShowSource = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new SKColor(0xFF, 0, 0), Centre(viewer));

            await Type(viewer, markup.Replace("</svg>", "<!-- edited --></svg>", StringComparison.Ordinal));

            // The rebuild really happened — otherwise the original picture would still be up and the
            // image would be resolved for the wrong reason.
            Assert.Contains("<!-- edited -->", viewer.Document!.SourceText!, StringComparison.Ordinal);
            Assert.Equal(new SKColor(0xFF, 0, 0), Centre(viewer));

            window.Close();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The middle of the drawing the viewer is holding, rendered on its own.</summary>
    private static SKColor Centre(SvgViewer viewer)
    {
        using var bitmap = new SKBitmap(20, 20);

        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawPicture(viewer.Svg!.Picture);
        }

        return bitmap.GetPixel(10, 10);
    }

    [AvaloniaFact]
    public async Task A_Drawing_Too_Large_To_Show_Whole_Cannot_Be_Edited()
    {
        // Editing a cut document and saving it would behead the file and write the note explaining
        // the cut into it. There is no warning that makes that acceptable.
        var (window, viewer) = Host();

        // Past the pane's own backstop, which it does not expose and this must therefore restate.
        var padding = new string(' ', 2_000_001);

        Assert.True(await viewer.LoadTextAsync(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1 1\" width=\"1\" height=\"1\"><!--{padding}--></svg>"));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(Pane(viewer).IsReadOnly);
        Assert.Contains("too large to edit", PaneText(viewer), StringComparison.Ordinal);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Saving_Writes_The_Pane_Back_And_Clears_The_Mark()
    {
        var path = Path.Combine(Path.GetTempPath(), "svg-save-" + Guid.NewGuid().ToString("N") + ".svg");
        File.WriteAllText(path, Parametric);

        try
        {
            var (window, viewer) = Host();

            Assert.True(await viewer.LoadAsync(path));
            viewer.ShowSource = true;
            Dispatcher.UIThread.RunJobs();

            Assert.False(viewer.IsSourceModified);

            await Type(viewer, Parametric.Replace("24", "48", StringComparison.Ordinal));

            Assert.True(viewer.IsSourceModified);

            Assert.True(await viewer.SaveSourceAsync());

            Assert.False(viewer.IsSourceModified);
            Assert.Contains("48", File.ReadAllText(path), StringComparison.Ordinal);

            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task A_Drawing_Replaced_By_One_That_Fits_The_Same_Still_Reaches_The_Screen()
    {
        // The picture is handed to the renderer by publishing a snapshot, and the fit published one
        // only when it moved the view. So a drawing swapped for one the same size — recoloured, or
        // reframed inside the same box — left the old picture on screen until something else moved
        // the view. Asserted against the frame, because "the model is right" was true throughout.
        var (window, viewer) = await HostLoaded();

        var canvas = viewer.GetVisualDescendants().OfType<SvgViewerCanvas>().Single();

        Assert.True(await viewer.LoadTextAsync(Square("#ff0000")));
        Dispatcher.UIThread.RunJobs();

        Assert.True(Painted(window, SKColors.Red) > 0);

        // The same size and the same view, so nothing about the fit changes.
        Assert.True(await viewer.LoadTextAsync(Square("#0000ff")));
        Dispatcher.UIThread.RunJobs();

        Assert.True(Painted(window, SKColors.Blue) > 0, "the replaced drawing never reached the screen");
        Assert.Equal(0, Painted(window, SKColors.Red));

        window.Close();
    }

    [AvaloniaFact]
    public async Task Rebuilding_The_Same_Drawing_Keeps_The_View_It_Was_Being_Looked_At_Through()
    {
        var path = Path.Combine(Path.GetTempPath(), $"svg-viewer-view-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, Square("#ff0000"));

        try
        {
            var (window, viewer) = Host();

            Assert.True(await viewer.LoadAsync(path));
            Dispatcher.UIThread.RunJobs();

            var canvas = viewer.GetVisualDescendants().OfType<SvgViewerCanvas>().Single();

            canvas.ZoomIn();
            Dispatcher.UIThread.RunJobs();

            var chosen = canvas.Scale;

            // The same file again, which is what a project resizing a drawing asks for. Starting
            // over as if a file had been opened threw away a zoom set to look at the very thing
            // being changed.
            Assert.True(await viewer.LoadAsync(path));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(chosen, canvas.Scale);

            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Square(string fill)
        => $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24"><rect width="24" height="24" fill="{fill}" /></svg>""";

    /// <summary>How much of the frame is painted in <paramref name="wanted"/>.</summary>
    private static int Painted(Window window, SKColor wanted)
    {
        var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("No rendered frame was captured.");

        var path = Path.Combine(Path.GetTempPath(), $"svg-viewer-swap-{Guid.NewGuid():N}.png");
        frame.Save(path);

        try
        {
            using var bitmap = SKBitmap.Decode(path);
            var found = 0;

            for (var y = 0; y < bitmap!.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);

                    if (Math.Abs(pixel.Red - wanted.Red) < 24
                        && Math.Abs(pixel.Green - wanted.Green) < 24
                        && Math.Abs(pixel.Blue - wanted.Blue) < 24)
                    {
                        found++;
                    }
                }
            }

            return found;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task Becoming_Unsaved_Is_Announced_And_Not_Only_Observable()
    {
        // The property flipped while the event never fired, so a host that marks its chrome from
        // SourceModifiedChanged — which is the only thing it is for — never heard about the first
        // edit. AvaloniaEdit raises TextChanged before its undo stack takes the edit, so reading
        // IsOriginalFile from inside that handler still says "saved".
        var (window, viewer) = await HostLoaded();

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var announced = new List<bool>();
        viewer.SourceModifiedChanged += (_, modified) => announced.Add(modified);

        await Type(viewer, Parametric.Replace("24", "48", StringComparison.Ordinal));

        Assert.True(viewer.IsSourceModified);
        Assert.Equal(new[] { true }, announced);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Bound_Value_Survives_An_Edit_That_Adds_A_Parameter()
    {
        // Every value used to go when the declarations changed shape, which was rare when a reload
        // meant reopening a file and is constant while someone is typing one.
        var (window, viewer) = await HostLoaded();

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.TrySetParameterValue("tint", ExprValue.Color(0x11, 0x22, 0x33, 0xFF)));
        Dispatcher.UIThread.RunJobs();

        await Type(
            viewer,
            Parametric.Replace(
                "</e:code>",
                "  <e:param name=\"extra\" type=\"number\" default=\"1\" />\n    </e:code>",
                StringComparison.Ordinal));

        // The rebuild really happened, so the surviving value survived something.
        Assert.Contains(viewer.Parameters, p => p.Name == "extra");

        var tint = Assert.IsType<SvgViewerColorParameter>(viewer.Parameters.Single(p => p.Name == "tint"));

        Assert.Equal(0x11, tint.Color.R);
        Assert.Equal(0x22, tint.Color.G);
        Assert.Equal(0x33, tint.Color.B);

        window.Close();
    }

    /// <summary>One number parameter, so an edit to its range can be watched arriving.</summary>
    private static string Ranged(string step, string fallback = "180") =>
        $"""
         <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
           <defs><e:code><e:param name="hue" type="number" default="{fallback}" min="0" max="360" step="{step}" /></e:code></defs>
           <rect width="24" height="24" fill="#3fb5b5" />
         </svg>
         """;

    [AvaloniaFact]
    public async Task A_Step_Edited_In_The_Source_Reaches_The_Slider()
    {
        // The guard carrying values across a reload compared names and types, and a step is
        // neither — so editing one changed everything but the slider.
        var (window, viewer) = await HostLoaded(Ranged("30"));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(30d, viewer.Parameters.OfType<SvgViewerNumberParameter>().Single().Step);

        await Type(viewer, Ranged("5"));

        var row = viewer.Parameters.OfType<SvgViewerNumberParameter>().Single();

        Assert.Equal(5d, row.Step);
        Assert.Equal(5d, row.TickFrequency);

        // All the way through the binding, not just on the row.
        Assert.Equal(5d, viewer.GetVisualDescendants().OfType<Slider>().First().TickFrequency);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Value_Nobody_Chose_Follows_The_Default_It_Came_From()
    {
        // A value someone dragged is theirs; one still sitting where the default put it is the
        // file's.
        var (window, viewer) = await HostLoaded(Ranged("5"));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        await Type(viewer, Ranged("5", fallback: "90"));

        Assert.Equal(90d, viewer.Parameters.OfType<SvgViewerNumberParameter>().Single().Value);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Value_Somebody_Chose_Survives_An_Edit_To_The_Default()
    {
        var (window, viewer) = await HostLoaded(Ranged("5"));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewer.TrySetParameterValue("hue", ExprValue.Number(45f)));
        Dispatcher.UIThread.RunJobs();

        await Type(viewer, Ranged("5", fallback: "90"));

        Assert.Equal(45d, viewer.Parameters.OfType<SvgViewerNumberParameter>().Single().Value);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Selection_Can_Cross_A_Line()
    {
        // The reason the pane is an editor at all. A control per line could show a file and could
        // colour it, but a reader could never take a piece of it away.
        var (window, viewer) = await HostLoaded();

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var editor = Pane(viewer);
        var second = editor.Document.GetLineByNumber(2);
        var third = editor.Document.GetLineByNumber(3);

        editor.Select(second.Offset, third.EndOffset - second.Offset);

        Assert.Contains("\n", editor.SelectedText, StringComparison.Ordinal);
        Assert.Contains(editor.Document.GetText(second), editor.SelectedText, StringComparison.Ordinal);
        Assert.Contains(editor.Document.GetText(third), editor.SelectedText, StringComparison.Ordinal);

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Windows_Drawing_Splits_Into_The_Same_Lines_Twice()
    {
        // The highlighter and the editor's document both split lines, and a disagreement about a
        // carriage return would put every colour one character out.
        var (window, viewer) = Host();

        var markup = Parametric.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);

        Assert.True(await viewer.LoadTextAsync(markup));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var document = Pane(viewer).Document;
        var lines = SvgSourceHighlighter.Lines(document.Text);

        Assert.Contains("\r\n", document.Text, StringComparison.Ordinal);
        Assert.Equal(document.LineCount, lines.Count);

        for (var number = 1; number <= document.LineCount; number++)
        {
            Assert.Equal(document.GetLineByNumber(number).Offset, lines[number - 1].Start);
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task Every_Brush_The_Pane_Asks_For_Is_There_To_Find()
    {
        // A brush key is a string, and the line numbers disappeared when a rename caught
        // "SvgViewerSourceLineNumberBrush". Asked of the resources, so an unpainted key is checked too.
        var (window, viewer) = Host();

        Assert.True(await viewer.LoadTextAsync(Parametric));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var keys = Enum.GetValues<SvgSourceTokenKind>()
            .Select(kind => $"SvgViewerSource{kind}Brush")
            .Concat(new[] { "SvgViewerSourceLineNumberBrush", "SvgViewerSourceErrorBrush", "SvgViewerSourceWarningBrush" })
            .Distinct()
            .ToList();

        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            foreach (var key in keys)
            {
                Assert.True(
                    viewer.TryFindResource(key, variant, out var brush) && brush is IBrush,
                    $"{key} does not resolve in {variant}");
            }
        }

        // And the pane really did paint with them.
        Assert.All(RealisedRuns(viewer), r => Assert.NotNull(r.Brush));

        window.Close();
    }

    private static TextEditor Pane(SvgViewer viewer)
        => viewer.GetVisualDescendants().OfType<TextEditor>().First(c => c.Name == "SourceEditor");

    /// <summary>What the pane is showing, which is the whole document rather than the rows on screen.</summary>
    private static string PaneText(SvgViewer viewer) => Pane(viewer).Text;

    /// <summary>
    /// The coloured pieces of every line the editor has actually built.
    /// </summary>
    /// <remarks>
    /// A visual line is split into elements wherever the colouriser asked for a colour, so an
    /// element is what a styled run used to be: a stretch of text and the brush it is drawn in.
    /// </remarks>
    private static List<(string Text, IBrush? Brush)> RealisedRuns(SvgViewer viewer)
    {
        var editor = Pane(viewer);
        var runs = new List<(string, IBrush?)>();

        foreach (var line in editor.TextArea.TextView.VisualLines)
        {
            foreach (var element in line.Elements)
            {
                var start = line.FirstDocumentLine.Offset + element.RelativeTextOffset;

                runs.Add((editor.Document.GetText(start, element.DocumentLength), element.TextRunProperties.ForegroundBrush));
            }
        }

        return runs;
    }

    /// <summary>
    /// How much of the frame is painted in the error colour, which is what a squiggle is made of.
    /// </summary>
    /// <remarks>
    /// A drawn mark cannot be asserted the way a TextDecoration could, so this asks the frame. The
    /// error brush is a colour nothing else in the palette is close to, and the count is only ever
    /// compared against zero.
    /// </remarks>
    private static int ErrorPixels(Window window, SvgViewer viewer)
    {
        Assert.True(viewer.TryFindResource("SvgViewerSourceErrorBrush", viewer.ActualThemeVariant, out var value));

        var wanted = ((ISolidColorBrush)value!).Color;

        var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("No rendered frame was captured.");

        var path = Path.Combine(Path.GetTempPath(), $"svg-viewer-marks-{Guid.NewGuid():N}.png");
        frame.Save(path);

        try
        {
            using var bitmap = SKBitmap.Decode(path);
            var found = 0;

            for (var y = 0; y < bitmap!.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);

                    if (Math.Abs(pixel.Red - wanted.R) < 24
                        && Math.Abs(pixel.Green - wanted.G) < 24
                        && Math.Abs(pixel.Blue - wanted.B) < 24)
                    {
                        found++;
                    }
                }
            }

            return found;
        }
        finally
        {
            File.Delete(path);
        }
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

    // ---- the panel's width lives on its column ----

    [AvaloniaFact]
    public async Task The_Panel_Takes_Its_Width_From_Its_Column_And_Not_From_Itself()
    {
        var (window, viewer) = await HostLoaded();

        // A Width on the border would leave the splitter moving the column while the panel stayed
        // the size it was born, which is what dragging it used to do.
        var host = viewer.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "DeclarationPanelHost");

        Assert.True(double.IsNaN(host.Width), "The panel sets its own width, so the splitter cannot.");
        Assert.True(Column(viewer).Width.Value > 0d);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Hiding_The_Panel_Gives_Its_Width_Back_To_The_Drawing()
    {
        var (window, viewer) = await HostLoaded();

        var column = Column(viewer);
        var was = column.Width;

        viewer.ShowDeclarationPanel = false;
        Dispatcher.UIThread.RunJobs();

        // Zeroed, minimum included: a hidden panel that still holds 340px of the window is a strip
        // the drawing pays for and nobody can see.
        Assert.Equal(0d, column.Width.Value);
        Assert.Equal(0d, column.MinWidth);

        viewer.ShowDeclarationPanel = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(was.Value, column.Width.Value);
        Assert.True(column.MinWidth > 0d);

        window.Close();
    }

    private static ColumnDefinition Column(SvgViewer viewer)
        => viewer.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "Drawing").ColumnDefinitions[2];

    private sealed class StubFileDialogService : ISvgViewerFileDialogService
    {
        private readonly string? _path;

        public StubFileDialogService(string? path) => _path = path;

        public Task<string?> OpenSvgAsync(TopLevel? owner) => Task.FromResult(_path);
    }
}
