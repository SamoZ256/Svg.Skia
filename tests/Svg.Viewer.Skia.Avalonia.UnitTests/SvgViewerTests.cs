using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Svg.Expressions;
using Svg.Highlighting;
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

        var element = runs.First(r => r.Text == "rect").Foreground;
        var name = runs.First(r => r.Text == "tint").Foreground;
        var fence = runs.First(r => r.Text == "{{").Foreground;

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
        // Colouring used to stop above 5,000 tokens, which 43% of this repository's own sample
        // drawings exceed. Rows that virtualise have no such limit: the cost is the screenful.
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

        var lines = Pane(viewer).ItemsSource!.Cast<SvgSourceLine>().ToList();

        Assert.True(lines.Count > 4_000, $"only {lines.Count} lines were prepared");

        var realised = Pane(viewer).GetVisualDescendants().OfType<SelectableTextBlock>().Count();

        Assert.True(
            realised < 200,
            $"{realised} rows were built for a {lines.Count}-line drawing; the list is not virtualising");

        // Still coloured, all the way down: the rows that exist carry expression runs, split into
        // the language rather than left as one piece.
        Assert.Contains(RealisedRuns(viewer), r => r.Text == "tint");
        Assert.Contains(RealisedRuns(viewer), r => r.Text == "{{");

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Minified_Drawing_Colours_What_It_Can_And_Still_Shows_All_Of_It()
    {
        // Virtualising by line bounds what a document costs, not what a line costs, and a minified
        // drawing is the whole file on one: 132KB of it took 1.4s as a single row, 340ms once the
        // row stopped colouring past its limit.
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

        var runs = RealisedRuns(viewer);

        Assert.True(
            runs.Count <= SvgSourceHighlighter.RowTokenLimit + 1,
            $"the row was built from {runs.Count} runs");

        // Bounded, but nothing is missing: what is not coloured is still shown.
        Assert.Equal(markup, string.Concat(runs.Select(r => r.Text)));
    }

    [AvaloniaFact]
    public async Task The_Palette_Follows_The_Theme()
    {
        var (window, viewer) = await HostLoaded();

        window.RequestedThemeVariant = ThemeVariant.Dark;
        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        var dark = RealisedRuns(viewer).First(r => r.Text == "rect").Foreground;

        window.RequestedThemeVariant = ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();

        var light = RealisedRuns(viewer).First(r => r.Text == "rect").Foreground;

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

        var runs = RealisedRuns(viewer);
        var wrong = runs.First(r => r.Text == "tnit");

        Assert.NotNull(wrong.TextDecorations);

        // Only the piece that is wrong: the drawing around it reads normally.
        Assert.All(
            runs.Where(r => r.Text is "rect" or "fill" or "{{"),
            r => Assert.Null(r.TextDecorations));

        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Drawing_With_Nothing_Wrong_Is_Not_Marked()
    {
        var (window, viewer) = await HostLoaded();

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        Assert.All(RealisedRuns(viewer), r => Assert.Null(r.TextDecorations));

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

        var rows = Pane(viewer).GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
        var marked = rows.Where(r => ToolTip.GetTip(r) is string).ToList();

        var one = Assert.Single(marked);

        Assert.Contains("tnit", (string)ToolTip.GetTip(one)!, StringComparison.Ordinal);
        Assert.Contains("tnit", string.Concat(one.Inlines!.OfType<Run>().Select(r => r.Text)), StringComparison.Ordinal);

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

        var marked = RealisedRuns(viewer).Where(r => r.TextDecorations is { }).ToList();

        Assert.Equal("0", Assert.Single(marked).Text);

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

        var rows = Pane(viewer).GetVisualDescendants().OfType<SelectableTextBlock>().ToList();

        Assert.Single(rows, r => ToolTip.GetTip(r) is string);

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

        Assert.Equal("clamp", Assert.Single(RealisedRuns(viewer), r => r.TextDecorations is { }).Text);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Every_Piece_Of_The_Pane_Is_Painted_With_Something()
    {
        // A brush key is a string, and a rename that catches one silently paints nothing: the line
        // numbers disappeared exactly that way, because "SvgViewerSourceLineNumberBrush" contains
        // the name of a type that was renamed around it.
        var (window, viewer) = Host();

        Assert.True(await viewer.LoadTextAsync(Parametric));

        viewer.ShowSource = true;
        Dispatcher.UIThread.RunJobs();

        Assert.All(RealisedRuns(viewer), r => Assert.NotNull(r.Foreground));

        var numbers = Pane(viewer).GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t is not SelectableTextBlock)
            .ToList();

        Assert.NotEmpty(numbers);
        Assert.All(numbers, n => Assert.NotNull(n.Foreground));
        Assert.All(numbers, n => Assert.False(string.IsNullOrEmpty(n.Text)));

        window.Close();
    }

    private static ItemsControl Pane(SvgViewer viewer)
        => viewer.GetVisualDescendants().OfType<ItemsControl>().First(c => c.Name == "SourceLines");

    /// <summary>What the pane is showing, joined back into one string.</summary>
    /// <remarks>
    /// From the lines it was given rather than from the rows on screen, because the rows on screen
    /// are deliberately only the handful that fit.
    /// </remarks>
    private static string PaneText(SvgViewer viewer)
        => string.Join(
            Environment.NewLine,
            Pane(viewer).ItemsSource!.Cast<SvgSourceLine>()
                .Select(line => string.Concat(line.Tokens.Select(t => t.Text))));

    /// <summary>The runs of every row the list has actually realised.</summary>
    private static List<Run> RealisedRuns(SvgViewer viewer)
        => Pane(viewer).GetVisualDescendants().OfType<SelectableTextBlock>()
            .SelectMany(t => t.Inlines?.OfType<Run>() ?? Enumerable.Empty<Run>())
            .ToList();

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
