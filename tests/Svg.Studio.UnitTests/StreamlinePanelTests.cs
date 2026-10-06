using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Svg.Viewer.Skia.Avalonia;
using Xunit;
using static Svg.Studio.UnitTests.Gestures;

namespace Svg.Studio.UnitTests;

/// <summary>The Streamline panel and its import window: where it sits, searching, picking, and importing through the project's templates.</summary>
/// <remarks>
/// In the settings collection because it writes the layout and remembers template choices, both
/// kept in files one static each points at. Streamline itself is a stub answering by path.
/// </remarks>
[Collection("settings")]
public class StreamlinePanelTests : IDisposable
{
    /// <summary>The line Studio wrote before the panel existed, as a user who arranged nothing has it saved.</summary>
    private const string Legacy =
        "row(col(tree/1.4/tree/open,variables/1/variables/open)/300px,*/1,"
        + "col(project+elements/1/project/open,element/1/element/open)/300px)";

    private const string Project = """
        <?xml version="1.0" encoding="utf-8"?>
        <studio namespace="Demo.Icons">
          <e:templates xmlns:e="https://svg.skia/expr/1.0">
            <e:recipe name="Accent glyph">
              <e:match colors="1" />
              <e:slot name="glyph" rest="true">stateAccentColor</e:slot>
            </e:recipe>
            <e:recipe name="Mono glyph">
              <e:match colors="1" />
              <e:slot name="glyph" rest="true">stateBlackColor</e:slot>
            </e:recipe>
          </e:templates>
          <group name="Scheme">
            <e:code xmlns:e="https://svg.skia/expr/1.0">
              <e:param name="state" type="integer" default="0" />
              <e:param name="isLight" type="boolean" default="false" />
              <e:let name="stateAccentColor">isLight ? #000000 : (state == 1 ? #fb3e72 : #7a7a7a)</e:let>
              <e:let name="stateBlackColor">isLight ? #ffffff : #000000</e:let>
            </e:code>
            <drawing name="Bell">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M0 0h24" fill="{{ stateAccentColor }}" /></svg>
            </drawing>
            <group name="Core Duo">
              <drawing name="a" source="streamline:ico_a">
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M0 0h24" fill="{{ stateAccentColor }}" /></svg>
              </drawing>
            </group>
          </group>
        </studio>

        """;

    private const string Glyph = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48"><path d="M2 2h40v40z" fill="#000000" /></svg>""";

    private const string Duo = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48"><path d="M2 2h40v40z" fill="#000000" /><path d="M2 46h40v-40z" fill="#fb3e72" /></svg>""";

    /// <summary>A template for <see cref="Duo"/>, added to <see cref="Project"/> before its first recipe.</summary>
    private const string DuoRecipe = """
            <e:recipe name="Two-tone">
              <e:match colors="2" />
              <e:slot name="ink" by="lightness" rank="1">stateBlackColor</e:slot>
              <e:slot name="wash" rest="true">stateAccentColor</e:slot>
            </e:recipe>
        """;

    private readonly string _was = StudioSettings.Store;
    private readonly string _choices = TemplateLibrary.ChoicesStore;
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public StreamlinePanelTests()
    {
        StudioSettings.Store = Path.Combine(_directory, "settings");
        TemplateLibrary.ChoicesStore = Path.Combine(_directory, "templates-choices");
    }

    public void Dispose()
    {
        StudioSettings.Store = _was;
        TemplateLibrary.ChoicesStore = _choices;

        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Answers by path, and says what it was asked.</summary>
    private sealed class Streamline : HttpMessageHandler
    {
        private readonly List<string> _asked = new();

        /// <summary>What was asked so far, copied under the lock a request is added under.</summary>
        /// <remarks>
        /// A copy rather than the list: the panel sends its next request on a pool thread while an
        /// assertion is walking this, which threw "Collection was modified" on Windows and Linux CI.
        /// </remarks>
        public IReadOnlyList<string> Asked
        {
            get
            {
                lock (_asked)
                {
                    return _asked.ToArray();
                }
            }
        }

        public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Paths { get; } = new(StringComparer.Ordinal);

        /// <summary>What a request waits on before it is answered, for a test to act in the meantime.</summary>
        public Func<HttpRequestMessage, Task> Holding { get; set; } = _ => Task.CompletedTask;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;

            lock (_asked)
            {
                _asked.Add(uri.PathAndQuery);
            }

            // Never at once: an answer already there when asked hides the order the panel does things in.
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            await Holding(request).WaitAsync(cancellationToken).ConfigureAwait(false);

            return Paths.TryGetValue(uri.AbsolutePath, out var answer)
                ? answer(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{ "message": "Resource not found" }""") };
        }

        public int Count(string prefix)
        {
            lock (_asked)
            {
                return _asked.Count(asked => asked.StartsWith(prefix, StringComparison.Ordinal));
            }
        }

        public static HttpResponseMessage Refused(HttpStatusCode status, string message)
            => new(status) { Content = new StringContent($$"""{ "message": "{{message}}" }""") };

        public void Json(string path, string body) => Paths[path] = _ => Answer(body, "application/json");

        public void Svg(string hash, string body) => Paths[$"/v1/icons/{hash}/download/svg"] = _ => Answer(body, "image/svg+xml");

        /// <summary>A preview for the icon, so its tile does not fall back on the icon's details.</summary>
        public void Preview(string hash) => Paths[$"/{hash}.png"] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="))
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png") }
            }
        };

        public static HttpResponseMessage Answer(string body, string type)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };
    }

    private static string Icon(string hash, string name, string family = "core-duo", bool free = true, string? colour = "#000000")
        => $$"""
            { "hash": "{{hash}}", "name": "{{name}}", "imagePreviewUrl": "https://assets.streamlinehq.com/{{hash}}.png",
              "isFree": {{(free ? "true" : "false")}}, "hasPremiumAccess": false, "familySlug": "{{family}}", "familyName": "Core Duo"
              {{(colour is { } ? $", \"colors\": [\"{colour}\"]" : "")}} }
            """;

    private static string Page(string list, IEnumerable<string> items, bool more, int next)
        => $$"""{ "{{list}}": [{{string.Join(",", items)}}], "pagination": { "total": 3, "hasMore": {{(more ? "true" : "false")}}, "offset": 0, "nextOffset": {{next}} } }""";

    private async Task<MainWindow> Host(Streamline streamline, string? key = "sk_test", bool project = true, string text = Project)
    {
        var window = new MainWindow();

        window.Announce = (_, _) => Task.CompletedTask;

        // Answered by each test that opens it: a modal the suite cannot close would hang it.
        window.ShowImport = _ => Task.FromResult(false);
        window.Streamline.StoredKey = () => key;
        window.Streamline.Connect = given => new StreamlineClient(given, new HttpClient(streamline));
        await window.Streamline.RefreshAsync();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        if (project)
        {
            var path = Path.Combine(_directory, "icons.svgstudio");

            File.WriteAllText(path, text);

            await window.OpenAsync(new[] { path });
            Dispatcher.UIThread.RunJobs();
        }

        // In a strip of its own, so its tiles are laid out and realised.
        window.Layout = "row(col(tree/1/tree/open,variables/1/variables/open)/200px,*/1,"
                        + "col(project+elements/1/project/open,element/1/element/open)/200px,streamline/320px/streamline/open)";
        window.Measure(new Size(1000, 800));
        window.Arrange(new Rect(0, 0, 1000, 800));
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    private static ProjectGroup Group(MainWindow window, string name)
        => window.Workspace!.Document.Root.Children.OfType<ProjectGroup>().Single(group => group.Name == name);

    private static void Settle(Window window, double width = 1000, double height = 800)
    {
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task The_Panel_Is_In_The_Default_Arrangement_Under_The_Drawing()
    {
        var window = await Opened();

        Assert.Contains("col(*/1.6,streamline/1/streamline/open)/1,", window.Layout, StringComparison.Ordinal);
        Assert.Same(window, TopLevel.GetTopLevel(window.Streamline));
    }

    /// <summary>A line saved before the panel existed does not name it, and the dock places it where the default has it.</summary>
    [AvaloniaFact]
    public async Task A_Layout_Saved_Before_The_Panel_Existed_Still_Has_It()
    {
        StudioSettings.Layout = Legacy;

        var window = await Opened();

        // Under the drawing, as the default has it, rather than a column the width of a side panel.
        Assert.Contains("col(*/1.6,streamline/1/streamline/open)/1,", window.Layout, StringComparison.Ordinal);
        Assert.Same(window, TopLevel.GetTopLevel(window.Streamline));

        // Placed, not written back: the saved line is what somebody arranged, and is left as it was.
        Assert.Equal(Legacy, StudioSettings.Layout);
    }

    /// <summary>A window in whatever arrangement the settings hold, with a project open so the tree is on it.</summary>
    private async Task<MainWindow> Opened()
    {
        var window = new MainWindow();
        var path = Path.Combine(_directory, "icons.svgstudio");

        File.WriteAllText(path, Project);
        window.Show();
        await window.OpenAsync(new[] { path });
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    [AvaloniaFact]
    public async Task Without_A_Key_The_Panel_Is_One_Line_Pointing_At_Settings()
    {
        var streamline = new Streamline();
        string? key = null;
        var window = await Host(streamline, key: null, project: false);

        window.Streamline.StoredKey = () => key;
        window.ShowSettings = () =>
        {
            key = "sk_new";

            return Task.CompletedTask;
        };

        Assert.False(window.Streamline.HasKey);

        var settings = window.Streamline.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Settings…"));

        Assert.True(settings.IsEffectivelyVisible);

        settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // Read off the UI thread, since a locked keychain waits on its prompt.
        await Until(() => window.Streamline.HasKey);
        Settle(window);

        Assert.False(settings.IsEffectivelyVisible);
        Assert.Empty(streamline.Asked);
    }

    /// <summary>A page that leaves the strip unfilled brings the next one without being asked.</summary>
    [AvaloniaFact]
    public async Task A_Search_Fills_The_Tiles_And_Pages_Until_The_Strip_Is_Full()
    {
        var streamline = new Streamline();

        streamline.Paths["/v1/search/global"] = request => Streamline.Answer(
            request.RequestUri!.Query.Contains("offset=0", StringComparison.Ordinal)
                ? Page("results", new[] { Icon("ico_a", "bell"), Icon("ico_b", "bin", free: false) }, more: true, next: 2)
                : Page("results", new[] { Icon("ico_c", "cog") }, more: false, next: 3),
            "application/json");

        var window = await Host(streamline, project: false);
        var panel = window.Streamline;

        await panel.SearchAsync();
        Settle(window);
        await Until(() => panel.Results.Count == 3);
        Settle(window);

        Assert.Equal(new[] { "bell", "bin", "cog" }, panel.Results.Select(icon => icon.Name));
        Assert.Equal(new[] { "bell", "bin", "cog" }, Tiles(panel));

        // Only the one this key cannot download carries the lock.
        // The style box's chevron is a PathIcon as well, so the lock is told by its tip.
        Assert.Single(panel.GetVisualDescendants().OfType<PathIcon>(), icon => ToolTip.GetTip(icon) is string tip && tip.Contains("plan", StringComparison.Ordinal));

        Assert.Contains(streamline.Asked, asked => asked.Contains("offset=2", StringComparison.Ordinal));
        Assert.Null(panel.Said);
    }

    /// <summary>An icon the CDN has no preview for is drawn from the SVG its details carry, one request rather than a metered download.</summary>
    [AvaloniaFact]
    public async Task A_Tile_Without_A_Preview_Draws_The_Icons_Own_Svg()
    {
        var streamline = new Streamline();

        // The preview URL the helper writes is on a path the stub never answers, so the CDN's part is a 404.
        streamline.Json("/v1/search/global", Page("results", new[] { Icon("ico_a", "bell") }, more: false, next: 1));
        streamline.Json("/v1/icons/ico_a", Icon("ico_a", "bell").TrimEnd().TrimEnd('}') + $$""", "svg": "{{Glyph.Replace("\"", "\\\"", StringComparison.Ordinal)}}" }""");

        var window = await Host(streamline, project: false);
        var panel = window.Streamline;

        await panel.SearchAsync();
        Settle(window);

        var tile = panel.GetVisualDescendants().OfType<Border>().Single(border => ToolTip.GetTip(border) is "bell");

        await Until(() => tile.GetVisualDescendants().OfType<Image>().Single().Source is { });
        Assert.False(tile.GetVisualDescendants().OfType<TextBlock>().Single().IsVisible);
        Assert.Single(streamline.Asked, asked => asked == "/v1/icons/ico_a");
    }

    /// <summary>A page that fills the strip waits: the next comes as the last row is scrolled into view.</summary>
    [AvaloniaFact]
    public async Task The_Next_Page_Comes_As_The_Last_Row_Is_Scrolled_To()
    {
        var streamline = new Streamline();

        streamline.Paths["/v1/search/global"] = request => Streamline.Answer(
            request.RequestUri!.Query.Contains("offset=0", StringComparison.Ordinal)
                ? Page("results", Enumerable.Range(0, 200).Select(n => Icon($"ico_{n}", $"bell {n}")), more: true, next: 200)
                : Page("results", new[] { Icon("ico_cog", "cog") }, more: false, next: 201),
            "application/json");

        var window = await Host(streamline, project: false);
        var panel = window.Streamline;

        await panel.SearchAsync();
        Settle(window);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(200, panel.Results.Count);
        Assert.Single(streamline.Asked, asked => asked.Contains("/v1/search/global", StringComparison.Ordinal));

        var tiles = panel.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll => scroll.Content is ItemsControl);

        Assert.True(tiles.Extent.Height > tiles.Viewport.Height, "Two hundred tiles should overrun the strip.");

        tiles.Offset = new Vector(0, tiles.Extent.Height);
        Settle(window);
        await Until(() => panel.Results.Count == 201);

        Assert.Contains(streamline.Asked, asked => asked.Contains("offset=200", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> Tiles(StreamlinePanel panel)
        => panel.GetVisualDescendants().OfType<Border>()
            .Select(border => ToolTip.GetTip(border) as string)
            .Where(tip => tip is "bell" or "bin" or "cog")
            .Cast<string>()
            .ToList();

    [AvaloniaFact]
    public async Task Two_Icons_Picked_Go_Into_The_Group_Through_Its_Template_As_One_Step()
    {
        var streamline = Searching("line-glyphs");

        Sure("line-glyphs");

        var window = await Host(streamline);
        var panel = window.Streamline;
        var scheme = Group(window, "Scheme");

        panel.Target = scheme;

        await panel.SearchAsync();
        await panel.PickAsync(1, KeyModifiers.None);
        await panel.PickAsync(0, KeyModifiers.Shift);

        Assert.Equal(new[] { "bell", "bin" }, panel.Picked.Select(icon => icon.Name));

        await panel.PickAsync(1, KeyModifiers.Control);

        Assert.Equal(new[] { "bell" }, panel.Picked.Select(icon => icon.Name));

        await panel.PickAsync(1, KeyModifiers.Meta);
        Settle(window);

        Assert.Equal(new[] { "bell", "bin" }, panel.Picked.Select(icon => icon.Name));

        var shown = Answering(window, true);
        var before = scheme.Children.Count;

        await panel.OpenImportAsync();
        Dispatcher.UIThread.RunJobs();

        var import = Assert.Single(shown);
        var row = Assert.Single(import.Rows);

        Assert.Same(scheme, import.Target);
        Assert.Null(import.Updating);
        Assert.Equal(new[] { "bell", "bin" }, row.Icons.Select(icon => icon.Name));
        Assert.Equal("Accent glyph", row.Chosen);
        Assert.True(row.Sure);
        Assert.Equal("stateAccentColor", row.Role("#000000"));
        Assert.Empty(row.Declares);

        var added = scheme.Children.Skip(before).ToList();

        Assert.Equal(new[] { "bell", "bin" }, added.Select(drawing => drawing.Name));
        Assert.Equal(new[] { "streamline:ico_a", "streamline:ico_b" }, added.OfType<ProjectDrawing>().Select(drawing => drawing.Source));
        Assert.All(added.OfType<ProjectDrawing>(), drawing => Assert.Contains("fill=\"{{ stateAccentColor }}\"", drawing.Text, StringComparison.Ordinal));
        Assert.Equal("add 2 drawings", window.Workspace!.UndoLabel);
        Assert.Empty(panel.Picked);

        Assert.True(window.Workspace.Undo());
        Assert.Equal(before, scheme.Children.Count);
        Assert.False(window.Workspace.CanUndo);
    }

    /// <summary>Choosing is not remembering: a window cancelled imports nothing and leaves the picks.</summary>
    [AvaloniaFact]
    public async Task A_Window_Cancelled_Imports_And_Remembers_Nothing()
    {
        var window = await Host(Searching("cancelled-glyphs"));
        var panel = window.Streamline;
        var scheme = Group(window, "Scheme");
        var before = scheme.Children.Count;

        panel.Target = scheme;
        window.ShowImport = import =>
        {
            import.Rows[0].Chosen = "Mono glyph";

            return Task.FromResult(false);
        };

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);
        await panel.OpenImportAsync();

        Assert.Equal(before, scheme.Children.Count);
        Assert.False(window.Workspace!.CanUndo);
        Assert.Null(TemplateLibrary.Remembered(TemplateLibrary.Prepare(Glyph, "cancelled-glyphs", null, "bell", new[] { "#000000" })));
        Assert.Equal(new[] { "bell" }, panel.Picked.Select(icon => icon.Name));
    }

    /// <summary>What the window hands the import each time it opens, answered with <paramref name="answer"/>.</summary>
    private static List<StreamlineImport> Answering(MainWindow window, bool answer)
    {
        var shown = new List<StreamlineImport>();

        window.ShowImport = import =>
        {
            shown.Add(import);

            return Task.FromResult(answer);
        };

        return shown;
    }

    [AvaloniaFact]
    public async Task A_Template_Chosen_In_The_Window_Is_Imported_And_Remembered_And_A_Colour_Can_Be_Given_Another_Role()
    {
        var streamline = Searching("remembered-glyphs");
        var window = await Host(streamline);
        var panel = window.Streamline;
        var scheme = Group(window, "Scheme");
        var icon = TemplateLibrary.Prepare(Glyph, "remembered-glyphs", null, "bell", new[] { "#000000" });

        panel.Target = scheme;
        window.ShowImport = import =>
        {
            var row = Assert.Single(import.Rows);

            Assert.Equal(new[] { "Accent glyph", "Mono glyph", TemplateLibrary.KeepColoursName }, row.Choices);
            Assert.Equal("Accent glyph", row.Suggested);
            Assert.False(row.Sure);

            row.Chosen = "Mono glyph";

            Assert.Equal("stateBlackColor", row.Role("#000000"));
            Assert.Equal("glyph → stateBlackColor", row.RoleNote("#000000"));
            Assert.Equal(new[] { StreamlineRow.Keep, "stateAccentColor", "stateBlackColor" }, row.RoleChoices("#000000"));

            row.SetRole("#000000", "stateAccentColor");

            Assert.Contains("fill=\"{{ stateAccentColor }}\"", row.Text(0), StringComparison.Ordinal);
            Assert.Contains("fill=\"{{ stateBlackColor }}\"", row.Text(0, "Mono glyph"), StringComparison.Ordinal);
            Assert.Equal("by hand → stateAccentColor", row.RoleNote("#000000"));
            Assert.False(row.Sure);

            row.SetRole("#000000", StreamlineRow.Keep);

            Assert.Null(row.Recipe(0));
            Assert.Equal("kept", row.RoleNote("#000000"));

            // Choosing the template again starts its colours over.
            row.Chosen = "Mono glyph";

            Assert.Equal("stateBlackColor", row.Role("#000000"));
            Assert.Null(TemplateLibrary.Remembered(icon));

            return Task.FromResult(true);
        };

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);
        await panel.OpenImportAsync();

        Assert.Contains("fill=\"{{ stateBlackColor }}\"", ((ProjectDrawing)scheme.Children[^1]).Text, StringComparison.Ordinal);
        Assert.Equal("Mono glyph", TemplateLibrary.Remembered(icon));
    }

    [AvaloniaFact]
    public async Task A_Colour_Given_A_Role_By_Hand_Imports_Through_It()
    {
        var window = await Host(Searching("role-glyphs"));
        var panel = window.Streamline;
        var scheme = Group(window, "Scheme");

        panel.Target = scheme;
        window.ShowImport = import =>
        {
            import.Rows[0].SetRole("#000000", "stateBlackColor");

            return Task.FromResult(true);
        };

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);
        await panel.OpenImportAsync();

        var bell = (ProjectDrawing)scheme.Children[^1];

        Assert.Equal("bell", bell.Name);
        Assert.Contains("fill=\"{{ stateBlackColor }}\"", bell.Text, StringComparison.Ordinal);

        // The template alone would not bring the role back, so the next bell of the family is not imported unseen through it.
        Assert.Null(TemplateLibrary.Remembered(TemplateLibrary.Prepare(Glyph, "role-glyphs", null, "bell", new[] { "#000000" })));
    }

    private static Streamline Searching(string family)
    {
        var streamline = new Streamline();

        streamline.Json("/v1/search/global", Page("results", new[] { Icon("ico_a", "bell", family), Icon("ico_b", "bin", family) }, more: false, next: 2));
        streamline.Svg("ico_a", Glyph);
        streamline.Svg("ico_b", Glyph.Replace("M2 2", "M4 4", StringComparison.Ordinal));

        return streamline;
    }

    /// <summary>
    /// Shows the import the panel hands over in the window, for <paramref name="drive"/> to work
    /// with, and answers what it returns.
    /// </summary>
    private static void Showing(MainWindow window, Func<StreamlineImportWindow, StreamlineImport, Task<bool>> drive)
        => window.ShowImport = async import =>
        {
            var shown = new StreamlineImportWindow(import, window);

            shown.Show();
            Drawn(shown);

            try
            {
                return await drive(shown, import);
            }
            finally
            {
                shown.Close();
            }
        };

    /// <summary>Lays the window out and lets its drawings, which it loads once idle, be made.</summary>
    private static void Drawn(StreamlineImportWindow shown)
    {
        Settle(shown, 860, 620);
        Settle(shown, 860, 620);
    }

    /// <summary>One card strip per section, in the order of the rows.</summary>
    private static IReadOnlyList<ListBox> Sections(StreamlineImportWindow shown) => shown.GetLogicalDescendants().OfType<ListBox>().ToList();

    private static IReadOnlyList<string?> Cards(ListBox cards) => cards.Items.OfType<ListBoxItem>().Select(AutomationProperties.GetName).ToList();

    private static ListBoxItem Card(ListBox cards, string name) => cards.Items.OfType<ListBoxItem>().Single(item => AutomationProperties.GetName(item) == name);

    /// <summary>The section's "How they will look" drawings.</summary>
    private static IReadOnlyList<SvgViewerCanvas> Previews(ListBox cards)
        => ((StackPanel)cards.Parent!).Children.OfType<WrapPanel>().Single().Children.OfType<Border>().Select(plate => plate.Child).OfType<SvgViewerCanvas>().ToList();

    /// <summary>A glyph and a two-tone icon of one family, picked, under a project that has a template for each.</summary>
    private async Task<(MainWindow Window, StreamlinePanel Panel)> PickedMixed()
    {
        var streamline = new Streamline();

        streamline.Json("/v1/search/global", Page("results", new[] { Icon("ico_a", "bell", "duo-glyphs"), Icon("ico_b", "bin", "duo-glyphs") }, more: false, next: 2));
        streamline.Svg("ico_a", Glyph);
        streamline.Svg("ico_b", Duo);

        var window = await Host(streamline, text: Project.Replace("    <e:recipe name=\"Accent glyph\">", DuoRecipe + "    <e:recipe name=\"Accent glyph\">", StringComparison.Ordinal));
        var panel = window.Streamline;

        panel.Target = Group(window, "Scheme");

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);
        await panel.PickAsync(1, KeyModifiers.Shift);

        return (window, panel);
    }

    /// <summary>A section per batch, each with a card for every template that fits it and the dot on the one suggested.</summary>
    [AvaloniaFact]
    public async Task Each_Batch_Is_A_Section_With_A_Card_Per_Template_That_Fits_It()
    {
        var (window, panel) = await PickedMixed();

        Showing(window, (shown, import) =>
        {
            var sections = Sections(shown);

            Assert.Equal("Import 2 icons into Scheme", shown.Title);
            Assert.Equal(2, sections.Count);
            Assert.Equal(new[] { "Accent glyph", "Mono glyph", TemplateLibrary.KeepColoursName }, Cards(sections[0]));
            Assert.Equal(new[] { "Two-tone", TemplateLibrary.KeepColoursName }, Cards(sections[1]));

            // The glyph's two templates are a point apart, and the two-tone's only other choice is to keep its colours.
            foreach (var (cards, said) in sections.Zip(new[] { "Check the template", "Template is a sure match" }))
            {
                var dotted = cards.Items.OfType<ListBoxItem>().Where(item => item.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Any()).ToList();

                Assert.Equal(cards.Items[0], Assert.Single(dotted));
                Assert.Equal(said, AutomationProperties.GetName(dotted[0].GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single()));
                Assert.Equal(0, cards.SelectedIndex);
            }

            // Only Keep colours, which is the library's own, has nothing to manage.
            Assert.Null(Card(sections[0], TemplateLibrary.KeepColoursName).GetLogicalDescendants().OfType<Button>().SingleOrDefault());
            Assert.NotNull(Card(sections[0], "Mono glyph").GetLogicalDescendants().OfType<Button>().Single().Flyout);

            // Every card and preview drawn once the window is idle.
            Assert.All(shown.GetVisualDescendants().OfType<SvgViewerCanvas>(), canvas => Assert.NotNull(canvas.Svg));
            Assert.Equal(5 + 2, shown.GetVisualDescendants().OfType<SvgViewerCanvas>().Count());
            Assert.Equal("Nothing to declare", Declared(shown));
            Assert.False(shown.GetLogicalDescendants().OfType<Expander>().Single(expander => expander.Header is string header && header.StartsWith("Other", StringComparison.Ordinal)).IsVisible);

            return Task.FromResult(false);
        });

        await panel.OpenImportAsync();
    }

    private static string? Declared(StreamlineImportWindow shown)
        => shown.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text).Single(text => text is "Nothing to declare" || text?.StartsWith("Adds ", StringComparison.Ordinal) == true);

    /// <summary>Choosing a card outlines it and draws the icons through it; a toggle draws them all again with its value.</summary>
    [AvaloniaFact]
    public async Task A_Card_Chosen_Is_Outlined_And_Redraws_The_Icons_And_A_Toggle_Redraws_Them_All()
    {
        var window = await Host(Searching("line-glyphs"));
        var panel = window.Streamline;

        panel.Target = Group(window, "Scheme");

        Showing(window, (shown, import) =>
        {
            var row = import.Rows[0];
            var cards = Sections(shown)[0];
            var previews = Previews(cards);
            var was = previews[0].Svg;

            Assert.Equal(2, previews.Count);
            Assert.Equal(Outline(shown), Frame(Card(cards, "Accent glyph")).BorderBrush);
            Assert.Null(Frame(Card(cards, "Mono glyph")).BorderBrush);

            cards.SelectedItem = Card(cards, "Mono glyph");
            Drawn(shown);

            Assert.Equal("Mono glyph", row.Chosen);
            Assert.Equal(Outline(shown), Frame(Card(cards, "Mono glyph")).BorderBrush);
            Assert.Null(Frame(Card(cards, "Accent glyph")).BorderBrush);
            Assert.NotSame(was, previews[0].Svg);
            Assert.NotNull(previews[0].Svg);

            // The target's one boolean, seeded as the group panel seeds it.
            var toggle = Assert.Single(shown.GetLogicalDescendants().OfType<CheckBox>());

            Assert.Equal("isLight", toggle.Content);
            Assert.False(toggle.IsChecked);
            Assert.False(previews[0].Svg!.ExpressionValues!["isLight"].AsBoolean);

            toggle.IsChecked = true;

            // Every drawing that reaches it: the previews and the cards of both templates, though not the colours kept.
            var reaching = shown.GetVisualDescendants().OfType<SvgViewerCanvas>().Where(canvas => canvas.Svg!.ExpressionValues!.ContainsKey("isLight")).ToList();

            Assert.Equal(4, reaching.Count);
            Assert.All(reaching, canvas => Assert.True(canvas.Svg!.ExpressionValues!["isLight"].AsBoolean));

            return Task.FromResult(true);
        });

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);
        await panel.PickAsync(1, KeyModifiers.Shift);
        await panel.OpenImportAsync();

        Assert.Contains("fill=\"{{ stateBlackColor }}\"", ((ProjectDrawing)Group(window, "Scheme").Children[^1]).Text, StringComparison.Ordinal);

        static Border Frame(ListBoxItem card) => card.GetLogicalDescendants().OfType<Border>().First(border => border.Classes.Contains("card"));

        static object? Outline(Window shown) => shown.FindResource(shown.ActualThemeVariant, "TabItemHeaderSelectedPipeFill");
    }

    /// <summary>A template that fits an icon and still cannot be applied to it says why on its card, and the rest draw.</summary>
    [AvaloniaFact]
    public async Task A_Card_Whose_Template_Cannot_Be_Applied_Shows_Why()
    {
        const string tinted = """
                <e:recipe name="Tinted">
                  <e:match colors="1" />
                  <e:code><e:param name="tint" type="color" default="#ff0000" /></e:code>
                  <e:slot name="glyph" rest="true">tint</e:slot>
                </e:recipe>
            """;

        var streamline = new Streamline();

        streamline.Json("/v1/search/global", Page("results", new[] { Icon("ico_a", "bell", "tinted-glyphs") }, more: false, next: 1));
        streamline.Svg("ico_a", Glyph.Replace("<path", """<e:code xmlns:e="https://svg.skia/expr/1.0"><e:param name="tint" type="color" default="#000000" /></e:code><path""", StringComparison.Ordinal));

        var window = await Host(streamline, text: Project.Replace("    <e:recipe name=\"Accent glyph\">", tinted + "    <e:recipe name=\"Accent glyph\">", StringComparison.Ordinal));
        var panel = window.Streamline;

        panel.Target = Group(window, "Scheme");

        Showing(window, (shown, _) =>
        {
            var cards = Sections(shown)[0];
            var said = Card(cards, "Tinted").GetLogicalDescendants().OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text).ToList();

            Assert.Contains(said, text => text?.Contains("already declares 'tint'", StringComparison.Ordinal) == true);
            Assert.NotNull(Card(cards, "Accent glyph").GetLogicalDescendants().OfType<SvgViewerCanvas>().Single().Svg);

            return Task.FromResult(false);
        });

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);
        await panel.OpenImportAsync();
    }

    /// <summary>Enter imports and Escape cancels, as the buttons they stand for would.</summary>
    [AvaloniaTheory]
    [InlineData(PhysicalKey.Enter, true)]
    [InlineData(PhysicalKey.Escape, false)]
    public async Task Enter_Imports_And_Escape_Cancels(PhysicalKey key, bool imports)
    {
        var window = await Host(Searching("line-glyphs"));
        var panel = window.Streamline;
        var scheme = Group(window, "Scheme");
        var before = scheme.Children.Count;

        panel.Target = scheme;
        window.ShowImport = import =>
        {
            var shown = new StreamlineImportWindow(import, window);
            var answer = shown.ShowDialog<bool>(window);

            Drawn(shown);
            shown.KeyPressQwerty(key, RawInputModifiers.None);

            return answer;
        };

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);
        await panel.OpenImportAsync();

        Assert.Equal(before + (imports ? 1 : 0), scheme.Children.Count);
    }

    [AvaloniaFact]
    public async Task Closing_The_Window_Lets_Go_Of_What_It_Drew()
    {
        var window = await Host(Searching("line-glyphs"));
        var panel = window.Streamline;
        List<SvgViewerCanvas>? canvases = null;

        panel.Target = Group(window, "Scheme");
        window.ShowImport = import =>
        {
            var shown = new StreamlineImportWindow(import, window);

            shown.Show();
            Drawn(shown);
            canvases = shown.GetVisualDescendants().OfType<SvgViewerCanvas>().ToList();

            Assert.All(canvases, canvas => Assert.NotNull(canvas.Svg));

            shown.Close();

            return Task.FromResult(false);
        };

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);
        await panel.OpenImportAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(canvases!);
        Assert.All(canvases!, canvas => Assert.Null(canvas.Svg));
    }

    /// <summary>Resuming is running it again: what the group has is not downloaded, and a spent limit stops it saying when it lifts.</summary>
    [AvaloniaFact]
    public async Task A_Family_Import_Skips_What_Is_There_And_Stops_On_A_Spent_Limit()
    {
        var lifts = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var streamline = Catalogue();

        streamline.Paths["/v1/icons/ico_c/download/svg"] = _ =>
        {
            var spent = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{ "message": "Weekly download limit reached", "statusCode": 429 }""")
            };

            spent.Headers.RetryAfter = new RetryConditionHeaderValue(lifts);

            return spent;
        };

        var window = await Host(streamline);
        var panel = window.Streamline;
        var scheme = Group(window, "Scheme");
        string? asked = null;

        panel.Target = scheme;
        panel.ConfirmFamily = message =>
        {
            asked = message;

            return Task.FromResult(true);
        };

        await panel.ImportFamilyAsync(Parsed(Icon("ico_a", "a")));
        Dispatcher.UIThread.RunJobs();

        Assert.StartsWith("Import the 3 icons of Core Duo into a group called Core Duo under Scheme?", asked, StringComparison.Ordinal);

        var family = scheme.Children.OfType<ProjectGroup>().Single(group => group.Name == "Core Duo");

        Assert.Equal(new[] { "a", "b" }, family.Children.Select(child => child.Name));
        Assert.Equal("streamline:ico_b", family.Drawings.Last().Source);
        Assert.Contains("fill=\"{{ stateAccentColor }}\"", family.Drawings.Last().Text, StringComparison.Ordinal);
        Assert.DoesNotContain(streamline.Asked, one => one.StartsWith("/v1/icons/ico_a/download", StringComparison.Ordinal));
        Assert.Contains("Weekly download limit reached", panel.Said, StringComparison.Ordinal);
        Assert.Contains(lifts.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture), panel.Said, StringComparison.Ordinal);
        Assert.Equal("add b", window.Workspace!.UndoLabel);
    }

    [AvaloniaFact]
    public async Task A_Family_Import_Makes_Its_Group_In_The_Same_Step_As_The_First_Page()
    {
        var streamline = Catalogue();

        streamline.Svg("ico_c", Glyph);

        var window = await Host(streamline);
        var panel = window.Streamline;
        var root = window.Workspace!.Document.Root;
        var before = window.Workspace.Document.ToXml();

        panel.Target = root;
        panel.ConfirmFamily = _ => Task.FromResult(true);

        await panel.ImportFamilyAsync(Parsed(Icon("ico_a", "a")));
        Dispatcher.UIThread.RunJobs();

        var family = root.Children.OfType<ProjectGroup>().Single(group => group.Name == "Core Duo");

        Assert.Equal(new[] { "a", "b", "c" }, family.Children.Select(child => child.Name));
        Assert.Null(panel.Said);
        Assert.Equal("import Core Duo", window.Workspace.UndoLabel);

        Assert.True(window.Workspace.Undo());
        Assert.Equal(before, window.Workspace.Document.ToXml());
    }

    private static Streamline Catalogue()
    {
        var streamline = new Streamline();

        streamline.Json("/v1/family-groups", """[{ "hash": "grp", "slug": "streamline", "name": "Streamline", "productType": "icons" }]""");
        streamline.Json(
            "/v1/family-groups/grp/families",
            """{ "families": [{ "hash": "fam", "slug": "core-duo", "name": "Core Duo", "isFree": false, "iconCount": 3 }], "pagination": { "total": 1, "hasMore": false, "offset": 0, "nextOffset": 1 } }""");
        streamline.Json("/v1/families/fam/icons", Page("icons", new[] { Icon("ico_a", "a"), Icon("ico_b", "b"), Icon("ico_c", "c") }, more: false, next: 3));
        streamline.Svg("ico_a", Glyph);
        streamline.Svg("ico_b", Glyph);

        return streamline;
    }

    /// <summary>The pane is the explorer alone: nothing opens beside the tiles, and a pick costs no download.</summary>
    [AvaloniaFact]
    public async Task A_Pick_Downloads_Nothing_And_Opens_Nothing_Beside_The_Tiles()
    {
        var streamline = Searching("line-glyphs");
        var window = await Host(streamline);
        var panel = window.Streamline;

        panel.Target = Group(window, "Scheme");

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);
        await panel.PickAsync(1, KeyModifiers.Shift);
        Settle(window);

        var tiles = panel.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll => scroll.Content is ItemsControl);
        var right = tiles.TranslatePoint(new Point(tiles.Bounds.Width, 0), panel)!.Value.X;

        Assert.Equal(0, streamline.Count("/v1/icons/ico_a/download") + streamline.Count("/v1/icons/ico_b/download"));
        Assert.Empty(panel.GetLogicalDescendants().OfType<Expander>());
        Assert.True(right >= panel.Bounds.Width - 8.5, $"The tiles end at {right}, short of the panel's {panel.Bounds.Width}.");
    }

    /// <summary>The Import button at the end of the search row says how many are picked, and waits for one.</summary>
    [AvaloniaFact]
    public async Task The_Import_Button_Counts_The_Picks()
    {
        var window = await Host(Searching("line-glyphs"));
        var panel = window.Streamline;
        var import = Import(panel);

        panel.Target = Group(window, "Scheme");

        await panel.SearchAsync();
        Settle(window);

        var query = panel.GetLogicalDescendants().OfType<TextBox>().Single(box => box.PlaceholderText == "Search Streamline");

        Assert.Equal("Import…", import.Content);
        Assert.False(import.IsEnabled);
        Assert.True(import.IsEffectivelyVisible);
        Assert.True(import.TranslatePoint(default, panel)!.Value.X > query.TranslatePoint(new Point(query.Bounds.Width, 0), panel)!.Value.X);

        await panel.PickAsync(0, KeyModifiers.None);
        await panel.PickAsync(1, KeyModifiers.Shift);

        Assert.Equal("Import 2…", import.Content);
        Assert.True(import.IsEnabled);
        Assert.Equal("Icons go into Scheme.", ToolTip.GetTip(import));
    }

    private static Button Import(StreamlinePanel panel)
        => panel.GetLogicalDescendants().OfType<Button>().Single(button => button.Classes.Contains("accent"));

    /// <summary>The button waits while the picks download, and while a family is being imported.</summary>
    [AvaloniaFact]
    public async Task Import_Waits_For_A_Download_And_For_A_Family_Import()
    {
        var streamline = Catalogue();
        var downloading = new TaskCompletionSource();
        var finding = new TaskCompletionSource();

        streamline.Json("/v1/search/global", Page("results", new[] { Icon("ico_a", "a") }, more: false, next: 1));
        streamline.Holding = request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/icons/ico_a/download/svg" => downloading.Task,
            "/v1/family-groups" => finding.Task,
            _ => Task.CompletedTask
        };

        var window = await Host(streamline);
        var panel = window.Streamline;
        var import = Import(panel);
        var shown = Answering(window, false);

        panel.ConfirmFamily = _ => Task.FromResult(false);

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);

        import.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => streamline.Count("/v1/icons/ico_a/download") == 1);

        Assert.False(import.IsEnabled);
        Assert.Equal("Downloading a…", panel.Said);

        downloading.SetResult();
        await Until(() => shown.Count == 1);

        Assert.True(import.IsEnabled);
        Assert.Null(panel.Said);

        var family = panel.ImportFamilyAsync(Parsed(Icon("ico_a", "a")));

        Assert.False(import.IsEnabled);

        finding.SetResult();
        await family;

        Assert.True(import.IsEnabled);
    }

    /// <summary>Icons on their way to the window keep others out, so the same icons are never in two windows: a drop waits for Import, and Import for a drop.</summary>
    [AvaloniaFact]
    public async Task An_Import_And_A_Drop_Wait_For_Each_Other()
    {
        var streamline = Downloading("ico_a", "ico_b");
        var bell = new TaskCompletionSource();
        var bin = new TaskCompletionSource();

        streamline.Json("/v1/search/global", Page("results", new[] { Icon("ico_a", "bell", "line-glyphs"), Icon("ico_b", "bin", "line-glyphs") }, more: false, next: 2));
        streamline.Holding = request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/icons/ico_a/download/svg" => bell.Task,
            "/v1/icons/ico_b/download/svg" => bin.Task,
            _ => Task.CompletedTask
        };

        var window = await Host(streamline);
        var panel = window.Streamline;
        var import = Import(panel);
        var shown = Answering(window, false);
        var effects = new List<DragDropEffects>();

        panel.Target = Group(window, "Scheme");
        window.AddHandler(DragDrop.DragOverEvent, (_, e) => effects.Add(e.DragEffects), RoutingStrategies.Bubble, handledEventsToo: true);

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);

        import.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => streamline.Count("/v1/icons/ico_a/download") == 1);
        Drop(window, OnRow(window, "Scheme"), Carrying(panel, panel.Results[1]));

        Assert.Equal(new[] { DragDropEffects.None }, effects);
        Assert.Equal(0, streamline.Count("/v1/icons/ico_b/download"));

        bell.SetResult();
        await Until(() => shown.Count == 1);

        Drop(window, OnRow(window, "Scheme"), Carrying(panel, panel.Results[1]));
        await Until(() => streamline.Count("/v1/icons/ico_b/download") == 1);

        Assert.False(import.IsEnabled);

        await panel.OpenImportAsync();
        bin.SetResult();
        await Until(() => import.IsEnabled);

        Assert.Equal(2, shown.Count);
        Assert.Equal(new[] { "bin" }, shown[1].Rows.SelectMany(row => row.Icons).Select(icon => icon.Name));
    }

    /// <summary>Counted by the pointer, since the first press builds the tiles again under the second.</summary>
    [AvaloniaFact]
    public async Task A_Double_Click_Opens_The_Window_For_That_Icon_Alone()
    {
        var (window, panel) = await Tiled();
        var shown = Answering(window, false);

        Press(window, Tile(panel, "cog"), new Point(20, 20));
        Settle(window);
        Release(window, Tile(panel, "cog"), new Point(20, 20));

        Assert.Empty(shown);

        Press(window, Tile(panel, "cog"), new Point(20, 20), clicks: 2);
        Settle(window);
        Release(window, Tile(panel, "cog"), new Point(20, 20));
        await Until(() => shown.Count == 1);

        Assert.Equal(new[] { "cog" }, shown[0].Rows.SelectMany(row => row.Icons).Select(icon => icon.Name));
        Assert.Equal(new[] { "cog" }, panel.Picked.Select(icon => icon.Name));
    }

    /// <summary>The next page is of the search showing, not of what has been typed since and not yet searched.</summary>
    [AvaloniaFact]
    public async Task The_Next_Page_Is_Of_The_Search_Showing_Not_What_Is_Typed()
    {
        var streamline = new Streamline();

        streamline.Paths["/v1/search/global"] = request => Streamline.Answer(
            request.RequestUri!.Query.Contains("offset=0", StringComparison.Ordinal)
                ? Page("results", new[] { Icon("ico_a", "bell"), Icon("ico_b", "bin") }, more: true, next: 2)
                : Page("results", new[] { Icon("ico_c", "cog") }, more: false, next: 3),
            "application/json");

        var window = await Host(streamline, project: false);
        var panel = window.Streamline;
        var query = panel.GetLogicalDescendants().OfType<TextBox>().Single(box => box.PlaceholderText == "Search Streamline");

        query.Text = "bell";
        await panel.SearchAsync();

        query.Text = "cat";
        await panel.SearchAsync(more: true);

        Assert.Equal(new[] { "bell", "bin", "cog" }, panel.Results.Select(icon => icon.Name));
        Assert.Contains(streamline.Asked, asked => asked.Contains("offset=2", StringComparison.Ordinal) && asked.Contains("query=bell", StringComparison.Ordinal));
        Assert.DoesNotContain(streamline.Asked, asked => asked.Contains("query=cat", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task A_Style_Picked_Searches_Again_Under_It()
    {
        var streamline = Searching("line-glyphs");
        var window = await Host(streamline, project: false);
        var panel = window.Streamline;

        panel.GetLogicalDescendants().OfType<ComboBox>().First().SelectedItem = "line";

        await Until(() => panel.Results.Count == 2);

        Assert.Contains(streamline.Asked, asked => asked.Contains("style=line", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task A_Key_Streamline_Refuses_Is_Said_In_The_Panel()
    {
        var streamline = new Streamline();

        streamline.Paths["/v1/search/global"] = _ => Streamline.Refused(HttpStatusCode.Unauthorized, "Invalid API key");

        var window = await Host(streamline, project: false);
        var panel = window.Streamline;

        await panel.SearchAsync();

        Assert.Empty(panel.Results);
        Assert.Contains("did not take the key", panel.Said, StringComparison.Ordinal);
        Assert.Contains("Invalid API key", panel.Said, StringComparison.Ordinal);
    }

    /// <summary>A search answers with new records, and the same icon from the last one is still the one picked.</summary>
    [AvaloniaFact]
    public async Task An_Icon_Is_Picked_Once_Whichever_Search_It_Came_From()
    {
        var window = await Host(Searching("line-glyphs"));
        var panel = window.Streamline;

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);
        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.Control);

        Assert.Empty(panel.Picked);
    }

    /// <summary>A locked icon's refusal is said once and unpicks it, and the window opens on the rest, or not at all where nothing is left.</summary>
    [AvaloniaFact]
    public async Task An_Icon_That_Would_Not_Download_Is_Unpicked_And_Not_Asked_For_Again()
    {
        var streamline = Searching("line-glyphs");

        streamline.Paths["/v1/icons/ico_b/download/svg"] = _ => Streamline.Refused(HttpStatusCode.Forbidden, "Premium icon");

        var window = await Host(streamline);
        var panel = window.Streamline;
        var shown = Answering(window, false);

        await panel.SearchAsync();
        await panel.PickAsync(1, KeyModifiers.None);
        await panel.OpenImportAsync();

        Assert.Empty(panel.Picked);
        Assert.Empty(shown);
        Assert.Contains("Premium icon", panel.Said, StringComparison.Ordinal);

        await panel.PickAsync(0, KeyModifiers.None);
        await panel.PickAsync(1, KeyModifiers.Shift);
        await panel.OpenImportAsync();

        Assert.Equal(new[] { "bell" }, panel.Picked.Select(icon => icon.Name));
        Assert.Equal(new[] { "bell" }, Assert.Single(shown).Rows.SelectMany(row => row.Icons).Select(icon => icon.Name));
        Assert.Contains("Premium icon", panel.Said, StringComparison.Ordinal);

        await panel.OpenImportAsync();

        Assert.Equal(2, shown.Count);
        Assert.Equal(2, streamline.Count("/v1/icons/ico_b/download"));
        Assert.Equal(1, streamline.Count("/v1/icons/ico_a/download"));
        Assert.Null(panel.Said);
    }

    /// <summary>The palette is asked for first: it is not counted against the limit, so its failing spends nothing.</summary>
    [AvaloniaFact]
    public async Task An_Icon_Whose_Details_Fail_Is_Not_Downloaded()
    {
        var streamline = new Streamline();

        streamline.Json("/v1/search/global", Page("results", new[] { Icon("ico_d", "dot", colour: null) }, more: false, next: 1));
        streamline.Paths["/v1/icons/ico_d"] = _ => Streamline.Refused(HttpStatusCode.TooManyRequests, "Hourly limit reached");
        streamline.Svg("ico_d", Glyph);
        streamline.Preview("ico_d");

        var window = await Host(streamline);
        var panel = window.Streamline;

        await panel.SearchAsync();

        for (var tries = 0; tries < 2; tries++)
        {
            await panel.PickAsync(0, KeyModifiers.None);
            await panel.OpenImportAsync();
        }

        Assert.Equal(2, streamline.Asked.Count(asked => asked.Split('?')[0] == "/v1/icons/ico_d"));
        Assert.Equal(0, streamline.Count("/v1/icons/ico_d/download"));
        Assert.Contains("Hourly limit reached", panel.Said, StringComparison.Ordinal);
    }

    /// <summary>A second click while the first is still downloading opens nothing more.</summary>
    [AvaloniaFact]
    public async Task Importing_Twice_At_Once_Imports_Once()
    {
        var window = await Host(Searching("line-glyphs"));
        var panel = window.Streamline;
        var scheme = Group(window, "Scheme");
        var before = scheme.Children.Count;
        var shown = Answering(window, true);

        panel.Target = scheme;

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);

        await Task.WhenAll(panel.OpenImportAsync(), panel.OpenImportAsync());

        Assert.Single(shown);
        Assert.Equal(before + 1, scheme.Children.Count);
    }

    [AvaloniaFact]
    public async Task With_No_Project_Open_Each_Icon_Opens_In_A_Tab_Of_Its_Own()
    {
        var window = await Host(Searching("line-glyphs"), project: false);
        var panel = window.Streamline;
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        var before = tabs.Items.Count;
        var shown = Answering(window, true);

        Assert.StartsWith("No project is open", (string?)ToolTip.GetTip(Import(panel)), StringComparison.Ordinal);

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);
        await panel.PickAsync(1, KeyModifiers.Shift);
        await panel.OpenImportAsync();

        Assert.Empty(shown);
        Assert.Equal(before + 2, tabs.Items.Count);
        Assert.Empty(panel.Picked);
    }

    /// <summary>The tree's selection is where icons go: a group itself, or the group holding a drawing.</summary>
    [AvaloniaFact]
    public async Task The_Target_Follows_The_Tree_And_The_Panel_Says_Where()
    {
        var window = await Host(new Streamline());
        var panel = window.Streamline;
        var scheme = Group(window, "Scheme");
        var tree = window.GetLogicalDescendants().OfType<TreeView>().First();
        var bell = Items(tree).Single(item => item.Tag is ProjectDrawing { Name: "Bell" });

        tree.SelectedItem = bell;
        Dispatcher.UIThread.RunJobs();

        Assert.Same(scheme, panel.Target);
        Assert.Equal("Icons go into Scheme.", ToolTip.GetTip(Import(panel)));

        static IEnumerable<TreeViewItem> Items(ItemsControl parent)
            => parent.Items.OfType<TreeViewItem>().SelectMany(item => Items(item).Prepend(item));
    }

    /// <summary>Closing the project ends a family import going into it, before it spends another download.</summary>
    [AvaloniaFact]
    public async Task A_Family_Import_Stops_When_Its_Project_Is_Closed()
    {
        var streamline = Catalogue();
        var held = new TaskCompletionSource();

        streamline.Svg("ico_c", Glyph);
        streamline.Holding = request => request.RequestUri!.AbsolutePath == "/v1/icons/ico_b/download/svg" ? held.Task : Task.CompletedTask;

        var window = await Host(streamline);
        var panel = window.Streamline;

        window.ConfirmDiscard = _ => Task.FromResult(true);
        panel.Target = window.Workspace!.Document.Root;
        panel.ConfirmFamily = _ => Task.FromResult(true);

        var importing = panel.ImportFamilyAsync(Parsed(Icon("ico_a", "a")));

        await Until(() => streamline.Count("/v1/icons/ico_b/download") == 1);

        Assert.True(await window.CloseProjectAsync());

        held.SetResult();
        await importing;

        Assert.Equal(0, streamline.Count("/v1/icons/ico_c/download"));
        Assert.Contains("was closed", panel.Said, StringComparison.Ordinal);
    }

    /// <summary>An undo of the first page takes out the group it made, and the pages after it have nowhere to go.</summary>
    [AvaloniaFact]
    public async Task A_Family_Import_Stops_When_Its_Group_Is_Undone()
    {
        var streamline = Catalogue();
        var held = new TaskCompletionSource();

        streamline.Paths["/v1/families/fam/icons"] = request => Streamline.Answer(
            request.RequestUri!.Query.Contains("offset=0", StringComparison.Ordinal)
                ? Page("icons", new[] { Icon("ico_a", "a") }, more: true, next: 1)
                : Page("icons", new[] { Icon("ico_b", "b") }, more: false, next: 2),
            "application/json");
        streamline.Holding = request => request.RequestUri!.PathAndQuery.StartsWith("/v1/families/fam/icons", StringComparison.Ordinal)
                                        && request.RequestUri.Query.Contains("offset=1", StringComparison.Ordinal)
            ? held.Task
            : Task.CompletedTask;

        var window = await Host(streamline);
        var panel = window.Streamline;
        var root = window.Workspace!.Document.Root;

        panel.Target = root;
        panel.ConfirmFamily = _ => Task.FromResult(true);

        var importing = panel.ImportFamilyAsync(Parsed(Icon("ico_a", "a")));

        await Until(() => root.Children.OfType<ProjectGroup>().Any(group => group.Name == "Core Duo"));

        Assert.True(window.Workspace.Undo());

        held.SetResult();
        await importing;

        Assert.DoesNotContain(root.Children.OfType<ProjectGroup>(), group => group.Name == "Core Duo");
        Assert.Equal(0, streamline.Count("/v1/icons/ico_b/download"));
        Assert.Contains("Core Duo is no longer in the project", panel.Said, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Second_Family_Import_Is_Told_One_Is_Running()
    {
        var streamline = Catalogue();
        var held = new TaskCompletionSource();

        streamline.Holding = request => request.RequestUri!.AbsolutePath == "/v1/family-groups" ? held.Task : Task.CompletedTask;

        var window = await Host(streamline);
        var panel = window.Streamline;

        panel.ConfirmFamily = _ => Task.FromResult(false);

        var first = panel.ImportFamilyAsync(Parsed(Icon("ico_a", "a")));

        Assert.Contains(panel.GetLogicalDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text?.StartsWith("Finding Core Duo", StringComparison.Ordinal) == true);

        await panel.ImportFamilyAsync(Parsed(Icon("ico_b", "b")));

        Assert.Contains("already being imported", panel.Said, StringComparison.Ordinal);

        held.SetResult();
        await first;
    }

    private static StreamlineIcon Parsed(string json)
        => System.Text.Json.JsonSerializer.Deserialize<StreamlineIcon>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

    private static void Named(MainWindow window, string name, bool onlyFamily = false, List<string?>? families = null, List<Window>? owners = null)
        => window.AskTemplateName = (_, family, owner) =>
        {
            families?.Add(family);
            owners?.Add(owner);

            return Task.FromResult<(string Name, bool OnlyFamily)?>((name, onlyFamily));
        };

    [AvaloniaFact]
    public async Task Extract_Template_Adds_The_Drawings_Paints_As_Slots_In_One_Step()
    {
        var window = await Host(new Streamline());
        var workspace = window.Workspace!;
        var was = workspace.Document.ToXml();
        var bell = (ProjectDrawing)Group(window, "Scheme").Children.Single(node => node.Name == "Bell");

        Named(window, "Accent from Bell");

        await window.ExtractTemplateAsync(bell);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(
            """
                <e:recipe name="Accent from Bell">
                  <e:match colors="1" />
                  <e:slot name="rest" rest="true">stateAccentColor</e:slot>
                </e:recipe>
              </e:templates>
            """.ReplaceLineEndings("\n"),
            workspace.Document.ToXml(),
            StringComparison.Ordinal);
        Assert.Equal("add template Accent from Bell", workspace.UndoLabel);

        Assert.True(workspace.Undo());
        Assert.Equal(was, workspace.Document.ToXml());
        Assert.False(workspace.CanUndo);
    }

    /// <summary>Managed from its card's menu, each change one step, and the cards follow: a rename keeps the choice, and a template that no longer parses moves to the others.</summary>
    [AvaloniaFact]
    public async Task A_Template_Is_Renamed_Edited_And_Deleted_In_The_Window_Each_As_One_Step()
    {
        var window = await Host(Searching("line-glyphs"));
        var workspace = window.Workspace!;
        var root = workspace.Document.Root;
        var panel = window.Streamline;
        var was = workspace.Document.ToXml();
        var owners = new List<Window>();

        panel.Target = Group(window, "Scheme");

        Showing(window, async (shown, import) =>
        {
            var row = import.Rows[0];
            var other = shown.GetLogicalDescendants().OfType<Expander>().Single(expander => expander.Header is string header && header.StartsWith("Other", StringComparison.Ordinal));

            Assert.Equal(new[] { "Rename…", "Edit XML…", "Delete" }, ((MenuFlyout)Card(Sections(shown)[0], "Accent glyph").GetLogicalDescendants().OfType<Button>().Single().Flyout!).Items.OfType<MenuItem>().Select(item => item.Header));

            // A name another template has is refused rather than making two a card cannot tell apart.
            Named(window, "Mono glyph", owners: owners);
            await shown.RenameAsync("Accent glyph");

            Assert.Contains("already has a template called 'Mono glyph'", shown.Said, StringComparison.Ordinal);
            Assert.Equal(was, workspace.Document.ToXml());

            Named(window, "Accent", owners: owners);
            await shown.RenameAsync("Accent glyph");

            Assert.Equal("rename template Accent glyph to Accent", workspace.UndoLabel);
            Assert.Equal(new[] { "Accent", "Mono glyph", TemplateLibrary.KeepColoursName }, Cards(Sections(shown)[0]));
            Assert.Equal("Accent", row.Chosen);
            Assert.Equal("Accent", AutomationProperties.GetName((ListBoxItem)Sections(shown)[0].SelectedItem!));
            Assert.Null(shown.Said);

            // Only what the block cannot hold is refused.
            window.AskTemplateText = (_, owner) =>
            {
                owners.Add(owner);

                return Task.FromResult<string?>("<e:recipe");
            };
            await shown.EditAsync("Accent");

            Assert.NotNull(shown.Said);
            Assert.Equal("rename template Accent glyph to Accent", workspace.UndoLabel);

            window.AskTemplateText = (text, _) => Task.FromResult<string?>(text.Replace(">stateAccentColor<", ">stateBlackColor<", StringComparison.Ordinal));
            await shown.EditAsync("Accent");

            Assert.Equal("edit template Accent", workspace.UndoLabel);
            Assert.Contains("fill=\"{{ stateBlackColor }}\"", row.Text(0), StringComparison.Ordinal);

            // One that still parses but no longer fits goes to the others as well, and says so.
            window.AskTemplateText = (text, _) => Task.FromResult<string?>(text.Replace("<e:match colors=\"1\" />", "<e:match colors=\"2\" />", StringComparison.Ordinal));
            await shown.EditAsync("Accent");

            Assert.Equal(new[] { "Mono glyph", TemplateLibrary.KeepColoursName }, Cards(Sections(shown)[0]));
            Assert.Equal("Mono glyph", row.Chosen);
            Assert.Equal("Accent no longer fits these icons, so it is under Other templates.", shown.Said);

            // Kept, where it is a recipe that does not parse: the file can hold it, and the window says where it went.
            window.AskTemplateText = (text, _) => Task.FromResult<string?>(text.Replace("<e:slot name=\"glyph\"", "<e:slot by=\"size\" name=\"glyph\"", StringComparison.Ordinal));
            await shown.EditAsync("Accent");
            Drawn(shown);

            Assert.Equal(new[] { "Mono glyph", TemplateLibrary.KeepColoursName }, Cards(Sections(shown)[0]));
            Assert.Equal("Mono glyph", row.Chosen);
            Assert.Contains("does not parse, so it is under Other templates", shown.Said, StringComparison.Ordinal);
            Assert.True(other.IsVisible);
            Assert.NotNull(other.FindAncestorOfType<ScrollViewer>());
            Assert.Equal("Other templates (1)", other.Header);
            Assert.Contains(other.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text?.Contains("by=\"size\"", StringComparison.Ordinal) == true);

            // Asked first, since the step's undo is out of reach until the window closes.
            var deleting = false;

            window.ConfirmDeleteTemplate = (_, owner) =>
            {
                owners.Add(owner);

                return Task.FromResult(deleting);
            };
            await shown.DeleteAsync("Accent");

            Assert.Equal(2, root.Templates.Count);

            deleting = true;
            await shown.DeleteAsync("Accent");

            Assert.Equal("remove template Accent", workspace.UndoLabel);
            Assert.Equal(new[] { "Mono glyph" }, root.Templates.Select(template => template.Name));
            Assert.False(other.IsVisible);

            // Every dialog it opened was over it rather than the window behind.
            Assert.Equal(5, owners.Count);
            Assert.All(owners, owner => Assert.Same(shown, owner));

            return false;
        });

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);
        await panel.OpenImportAsync();

        // Cancelling kept every change, each its own step.
        for (var steps = 0; steps < 5; steps++)
        {
            Assert.True(workspace.Undo());
        }

        Assert.Equal(was, workspace.Document.ToXml());
        Assert.False(workspace.CanUndo);
    }

    [AvaloniaFact]
    public async Task A_Rows_Mapping_Is_Saved_As_A_Template_And_Becomes_A_Card()
    {
        var window = await Host(Searching("line-glyphs"));
        var workspace = window.Workspace!;
        var panel = window.Streamline;
        var families = new List<string?>();

        panel.Target = Group(window, "Scheme");

        Showing(window, async (shown, import) =>
        {
            var row = import.Rows[0];

            // The colour's role, set where the window offers it.
            var role = shown.GetLogicalDescendants().OfType<ComboBox>().Single();

            role.FindLogicalAncestorOfType<Expander>()!.IsExpanded = true;

            Assert.Equal("stateAccentColor", role.SelectedItem);

            role.SelectedItem = "stateBlackColor";

            Assert.Equal("by hand → stateBlackColor", row.RoleNote("#000000"));
            Assert.Contains(shown.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "by hand → stateBlackColor");

            Named(window, "Line black", onlyFamily: true, families);

            await shown.SaveAsTemplateAsync(row);

            Assert.Equal(new[] { "Core Duo" }, families);
            Assert.Equal("add template Line black", workspace.UndoLabel);
            Assert.Contains("Line black", Cards(Sections(shown)[0]));

            // The colours it was saved from are still open, though every section was built again.
            Assert.True(shown.GetLogicalDescendants().OfType<ComboBox>().Single().FindLogicalAncestorOfType<Expander>()!.IsExpanded);

            return false;
        });

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);
        await panel.OpenImportAsync();

        var saved = workspace.Document.Root.Templates[^1];

        Assert.Equal("Line black", saved.Name);
        Assert.Contains("""<e:match colors="1" strokes="0" family="line-glyphs" />""", saved.Text, StringComparison.Ordinal);
        Assert.Contains("""<e:slot name="rest" rest="true">stateBlackColor</e:slot>""", saved.Text, StringComparison.Ordinal);

        Assert.True(workspace.Undo());
        Assert.Equal(2, workspace.Document.Root.Templates.Count);
    }

    /// <summary>
    /// Between two siblings, so a replace that moved it would show; and suggested what it was imported
    /// with, though a drawing does not keep the style it was searched under.
    /// </summary>
    [AvaloniaFact]
    public async Task Update_From_Streamline_Replaces_What_The_Drawing_Draws_And_Keeps_The_Rest()
    {
        var streamline = new Streamline();

        streamline.Json("/v1/icons/ico_a", Icon("ico_a", "a"));
        streamline.Svg("ico_a", Glyph);

        streamline.Json("/v1/search/global", Page("results", new[] { Icon("ico_z", "zed") }, more: false, next: 1));

        var sibling = """<drawing name="{0}"><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M0 0h2" /></svg></drawing>""";
        var window = await Host(streamline, text: Project.ReplaceLineEndings("\n")
            .Replace("""<drawing name="a" """, string.Format(sibling, "first") + """<drawing name="a" """, StringComparison.Ordinal)
            .Replace("</drawing>\n    </group>", "</drawing>" + string.Format(sibling, "last") + "\n    </group>", StringComparison.Ordinal));
        var workspace = window.Workspace!;
        var group = (ProjectGroup)Group(window, "Scheme").Children.Single(node => node.Name == "Core Duo");
        var drawing = (ProjectDrawing)group.Children.Single(node => node.Name == "a");
        var was = drawing.Text;
        var panel = window.Streamline;

        Assert.Equal(new[] { "first", "a", "last" }, group.Children.Select(node => node.Name));

        // Chosen when it came in from a search under a style.
        TemplateLibrary.Remember(TemplateLibrary.Prepare(Glyph, "core-duo", "line", "a", new[] { "#000000" }), "Mono glyph");

        // Picked beforehand, and left picked: an update is about the drawing.
        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);

        var shown = Answering(window, true);

        MainWindowProjectTests.Pick(window, "a", "Update from Streamline");
        await Until(() => workspace.UndoLabel == "update a");

        var import = Assert.Single(shown);

        Assert.Same(drawing, import.Updating);
        Assert.Same(group, import.Target);
        Assert.Equal("Mono glyph", Assert.Single(import.Rows).Chosen);
        Assert.Contains("M2 2h40v40z", drawing.Text, StringComparison.Ordinal);
        Assert.Contains("fill=\"{{ stateBlackColor }}\"", drawing.Text, StringComparison.Ordinal);
        Assert.Equal("a", drawing.Name);
        Assert.Equal("streamline:ico_a", drawing.Source);
        Assert.Equal(new[] { "first", "a", "last" }, group.Children.Select(node => node.Name));
        Assert.Same(drawing, group.Children[1]);
        Assert.Equal(new[] { "zed" }, panel.Picked.Select(icon => icon.Name));

        Assert.True(workspace.Undo());
        Assert.Equal(was, drawing.Text);
        Assert.False(workspace.CanUndo);
    }

    /// <summary>Extract on every drawing and no group; Update only on a drawing that came from Streamline.</summary>
    [AvaloniaFact]
    public async Task The_Tree_Offers_Extract_On_Drawings_And_Update_On_What_Came_From_Streamline()
    {
        var window = await Host(new Streamline());

        Assert.Contains("Extract template…", MainWindowProjectTests.Offers(MainWindowProjectTests.Row(window, "Bell")));
        Assert.DoesNotContain("Update from Streamline", MainWindowProjectTests.Offers(MainWindowProjectTests.Row(window, "Bell")));
        Assert.Contains("Update from Streamline", MainWindowProjectTests.Offers(MainWindowProjectTests.Row(window, "a")));
        Assert.DoesNotContain("Extract template…", MainWindowProjectTests.Offers(MainWindowProjectTests.Row(window, "Scheme")));
        Assert.DoesNotContain("Update from Streamline", MainWindowProjectTests.Offers(MainWindowProjectTests.Row(window, "Scheme")));

        Named(window, "From Bell");
        MainWindowProjectTests.Pick(window, "Bell", "Extract template…");
        await Until(() => window.Workspace!.Document.Root.Templates.Count == 3);

        Assert.Equal("From Bell", window.Workspace!.Document.Root.Templates[^1].Name);
    }

    /// <summary>A project closed while its update was being chosen takes the update with it, rather than writing into nothing.</summary>
    [AvaloniaFact]
    public async Task An_Update_Is_Dropped_When_Its_Project_Is_Closed()
    {
        var streamline = new Streamline();

        streamline.Json("/v1/icons/ico_a", Icon("ico_a", "a"));
        streamline.Svg("ico_a", Glyph);

        var window = await Host(streamline);
        var panel = window.Streamline;
        var drawing = (ProjectDrawing)((ProjectGroup)Group(window, "Scheme").Children.Single(node => node.Name == "Core Duo")).Children.Single();
        var was = drawing.Text;

        window.ConfirmDiscard = _ => Task.FromResult(true);
        window.ShowImport = async _ =>
        {
            Assert.True(await window.CloseProjectAsync());

            return true;
        };

        await panel.UpdateAsync(drawing);

        Assert.Equal(was, drawing.Text);
        Assert.Contains("was closed", panel.Said, StringComparison.Ordinal);
    }

    // ---- dragging tiles into the project ---------------------------------------------------------

    /// <summary>Three groups for the board drops: one arranged, with a placed frame in it; one still the spread; one empty.</summary>
    private const string Boards = """
        <?xml version="1.0" encoding="utf-8"?>
        <studio namespace="Demo.Icons">
          <group name="Board">
            <drawing name="home" x="0" y="0">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24"><rect width="24" height="24" fill="#00ff00" /></svg>
            </drawing>
            <group name="Frame" x="100" y="0">
              <drawing name="inner" x="0" y="0">
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24"><rect width="24" height="24" fill="#00ff00" /></svg>
              </drawing>
            </group>
          </group>
          <group name="Loose">
            <drawing name="one">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24"><rect width="24" height="24" fill="#00ff00" /></svg>
            </drawing>
            <drawing name="two">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24"><rect width="24" height="24" fill="#00ff00" /></svg>
            </drawing>
          </group>
          <group name="Empty" />
        </studio>

        """;

    /// <summary>What a drag of <paramref name="icons"/> puts in front of a drop target, which is all a headless drop can be.</summary>
    private static DataTransfer Carrying(StreamlinePanel panel, params StreamlineIcon[] icons)
    {
        var carried = new DataTransfer();

        carried.Add(DataTransferItem.Create(StreamlinePanel.DragFormat, string.Empty));
        panel.Dragged = icons;

        return carried;
    }

    /// <summary>Near the top of the row, which is its own header whether or not its branch is open.</summary>
    private static Point OnRow(MainWindow window, string label)
        => MainWindowProjectTests.Row(window, label).TranslatePoint(new Point(12, 6), window)!.Value;

    /// <summary>Makes <paramref name="family"/>'s glyphs sure: their template is the one remembered for icons like them.</summary>
    private static void Sure(string family)
        => TemplateLibrary.Remember(TemplateLibrary.Prepare(Glyph, family, null, "any", new[] { "#000000" }), "Accent glyph");

    private static Streamline Downloading(params string[] hashes)
    {
        var streamline = new Streamline();

        foreach (var hash in hashes)
        {
            streamline.Svg(hash, Glyph);
        }

        return streamline;
    }

    private static ProjectNode Selected(MainWindow window)
        => (ProjectNode)((TreeViewItem)MainWindowProjectTests.Tree(window).SelectedItem!).Tag!;

    [AvaloniaFact]
    public async Task A_Sure_Icon_Dropped_On_A_Group_Row_Imports_At_Its_End_As_One_Step()
    {
        StudioSettings.DropAsks = false;

        var streamline = Downloading("ico_a");

        streamline.Json("/v1/search/global", Page("results", new[] { Icon("ico_a", "cog", "sure-glyphs") }, more: false, next: 1));
        Sure("sure-glyphs");

        var window = await Host(streamline);
        var panel = window.Streamline;
        var scheme = Group(window, "Scheme");
        var before = scheme.Children.Count;

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);

        Drop(window, OnRow(window, "Scheme"), Carrying(panel, panel.Picked.ToArray()));

        // The picks clear once the import has opened its drawing, a load after the group grew.
        await Until(() => scheme.Children.Count == before + 1 && panel.Picked.Count == 0);

        var cog = Assert.IsType<ProjectDrawing>(scheme.Children[^1]);

        Assert.Equal("cog", cog.Name);
        Assert.Equal("streamline:ico_a", cog.Source);
        Assert.Contains("fill=\"{{ stateAccentColor }}\"", cog.Text, StringComparison.Ordinal);
        Assert.Equal("add cog", window.Workspace!.UndoLabel);
        Assert.Empty(panel.Picked);

        Assert.True(window.Workspace.Undo());
        Assert.Equal(before, scheme.Children.Count);
        Assert.False(window.Workspace.CanUndo);
    }

    /// <summary>After the drawing in the order a board and a build see, which is not where the tree sorts the row.</summary>
    [AvaloniaFact]
    public async Task A_Sure_Icon_Dropped_On_A_Drawing_Row_Lands_After_It()
    {
        StudioSettings.DropAsks = false;

        Sure("sure-glyphs");

        var window = await Host(Downloading("ico_a"));
        var panel = window.Streamline;
        var scheme = Group(window, "Scheme");

        MainWindowProjectTests.Row(window, "Scheme").IsExpanded = true;
        Settle(window);

        Drop(window, OnRow(window, "Bell"), Carrying(panel, Parsed(Icon("ico_a", "cog", "sure-glyphs"))));
        await Until(() => scheme.Children.Count == 3);

        Assert.Equal(new[] { "Bell", "cog", "Core Duo" }, scheme.Children.Select(node => node.Name));
    }

    /// <summary>A template that wants checking is not imported unseen: the window opens on the icon, and it lands where it was dropped.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_Amber_Icon_Dropped_Opens_The_Window_And_Imports_Where_It_Was_Dropped(bool taken)
    {
        var window = await Host(Downloading("ico_a"));
        var panel = window.Streamline;
        var scheme = Group(window, "Scheme");
        StreamlineImport? shown = null;

        MainWindowProjectTests.Row(window, "Scheme").IsExpanded = true;
        Settle(window);

        window.ShowImport = import =>
        {
            shown = import;

            // Nothing in before it is answered.
            Assert.Equal(new[] { "Bell", "Core Duo" }, scheme.Children.Select(node => node.Name));
            Assert.False(window.Workspace!.CanUndo);

            return Task.FromResult(taken);
        };

        Drop(window, OnRow(window, "Bell"), Carrying(panel, Parsed(Icon("ico_a", "cog", "line-glyphs"))));
        await Until(() => shown is { });
        Dispatcher.UIThread.RunJobs();

        var row = Assert.Single(shown!.Rows);

        Assert.Same(scheme, shown.Target);
        Assert.Equal("Accent glyph", row.Chosen);
        Assert.False(row.Sure);
        Assert.Equal(taken ? new[] { "Bell", "cog", "Core Duo" } : new[] { "Bell", "Core Duo" }, scheme.Children.Select(node => node.Name));
        Assert.Equal(taken, window.Workspace!.CanUndo);
    }

    [AvaloniaFact]
    public async Task A_Mixed_Drop_Imports_The_Sure_Batch_And_Opens_The_Window_For_The_Other_After_It()
    {
        StudioSettings.DropAsks = false;

        Sure("sure-glyphs");

        var window = await Host(Downloading("ico_a", "ico_b"));
        var panel = window.Streamline;
        var scheme = Group(window, "Scheme");

        window.ShowImport = import =>
        {
            Assert.Equal(new[] { "Bell", "Core Duo", "cog" }, scheme.Children.Select(node => node.Name));
            Assert.Equal(new[] { "gear" }, import.Rows.SelectMany(row => row.Icons).Select(icon => icon.Name));

            return Task.FromResult(true);
        };

        Drop(window, OnRow(window, "Scheme"), Carrying(panel, Parsed(Icon("ico_a", "cog", "sure-glyphs")), Parsed(Icon("ico_b", "gear", "line-glyphs"))));
        await Until(() => scheme.Children.Count == 4);

        Assert.Equal(new[] { "Bell", "Core Duo", "cog", "gear" }, scheme.Children.Select(node => node.Name));
    }

    /// <summary>
    /// A drop asks even where its template is sure, and the box under the question stops that: only
    /// on Import, since Escape answers nothing, after which a sure drop goes straight in.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(PhysicalKey.Enter, true)]
    [InlineData(PhysicalKey.Escape, false)]
    public async Task A_Sure_Drop_Asks_Until_The_Box_Says_Not_To(PhysicalKey key, bool imports)
    {
        Sure("sure-glyphs");

        var window = await Host(Downloading("ico_a", "ico_b"));
        var panel = window.Streamline;
        var scheme = Group(window, "Scheme");
        StreamlineImport? shown = null;

        window.ShowImport = import =>
        {
            var dialog = new StreamlineImportWindow(import, window);
            var answer = dialog.ShowDialog<bool>(window);

            Drawn(dialog);
            dialog.GetLogicalDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, "Don't ask again when dropping")).IsChecked = true;
            dialog.KeyPressQwerty(key, RawInputModifiers.None);

            // Only once answered: Drawn runs the dispatcher, where the test's Until can resume, and an
            // Escape test that finished in there had its dialog closed under the key press.
            shown = import;

            return answer;
        };

        Drop(window, OnRow(window, "Scheme"), Carrying(panel, Parsed(Icon("ico_a", "cog", "sure-glyphs"))));
        await Until(() => shown is { });

        // The import lands after the window answers, which a slower machine has not got to by now.
        await Until(() => !imports || scheme.Children.Count == 3);
        Dispatcher.UIThread.RunJobs();

        Assert.True(shown!.Dropped);
        Assert.True(Assert.Single(shown.Rows).Sure);
        Assert.Equal(imports ? new[] { "Bell", "Core Duo", "cog" } : new[] { "Bell", "Core Duo" }, scheme.Children.Select(node => node.Name));
        Assert.Equal(!imports, StudioSettings.DropAsks);

        if (!imports)
        {
            return;
        }

        window.ShowImport = _ => throw new InvalidOperationException("A sure drop asked after the box said not to.");

        var carried = Carrying(panel, Parsed(Icon("ico_b", "gear", "sure-glyphs")));

        // A drop is refused while another is under way, and the first is under way until whatever it opened has loaded.
        await Until(() => panel.CanDrop);

        Drop(window, OnRow(window, "Scheme"), carried);
        await Until(() => scheme.Children.Count == 4);

        Assert.Equal("gear", scheme.Children[^1].Name);
    }

    /// <summary>Not offered where a drop was already told not to ask and asks only because the match is unsure.</summary>
    [AvaloniaFact]
    public async Task The_Box_Is_Offered_Only_Where_A_Drop_Asks()
    {
        var window = await Host(Downloading("ico_a"));
        var boxes = new List<int>();

        window.ShowImport = import =>
        {
            var dialog = new StreamlineImportWindow(import, window);

            boxes.Add(dialog.GetLogicalDescendants().OfType<CheckBox>().Count(box => Equals(box.Content, "Don't ask again when dropping")));

            return Task.FromResult(false);
        };

        foreach (var asks in new[] { true, false })
        {
            StudioSettings.DropAsks = asks;

            Drop(window, OnRow(window, "Scheme"), Carrying(window.Streamline, Parsed(Icon("ico_a", "cog", "line-glyphs"))));
            await Until(() => boxes.Count == (asks ? 1 : 2));
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(new[] { 1, 0 }, boxes);
    }

    /// <summary>Hosts <see cref="Boards"/> with <paramref name="group"/>'s board in front, wide enough to aim at.</summary>
    private async Task<(MainWindow Window, GroupPanel Board)> Board(Streamline streamline, string group, string text = Boards)
    {
        var window = await Host(streamline, text: text);

        await window.ShowAsync(Group(window, group));
        Settle(window, 1600, 1000);

        return (window, (GroupPanel)((TabItem)window.FindControl<TabControl>("Tabs")!.SelectedItem!).Content!);
    }

    private static Point OnBoard(MainWindow window, GroupPanel board, float x, float y)
    {
        var canvas = Canvas(board);

        return canvas.TranslatePoint(Over(canvas, x, y), window)!.Value;
    }

    [AvaloniaFact]
    public async Task An_Icon_Dropped_In_A_Frame_Goes_Into_Its_Group_Where_It_Was_Let_Go()
    {
        StudioSettings.DropAsks = false;

        var (window, board) = await Board(Downloading("ico_a"), "Board");
        var frame = (ProjectGroup)Group(window, "Board").Children.Single(node => node.Name == "Frame");

        Drop(window, OnBoard(window, board, 110f, 12f), Carrying(window.Streamline, Parsed(Icon("ico_a", "cog"))));
        await Until(() => frame.Children.Count == 2);

        var cog = frame.Children[^1];

        Assert.Equal("cog", cog.Name);
        Assert.Equal(10f, cog.X);
        Assert.Equal(12f, cog.Y);
        Assert.Same(board, ((TabItem)window.FindControl<TabControl>("Tabs")!.SelectedItem!).Content);

        Assert.True(window.Workspace!.Undo());
        Assert.Single(frame.Children);
        Assert.False(window.Workspace.CanUndo);
    }

    [AvaloniaFact]
    public async Task Two_Icons_Dropped_On_A_Board_Are_Placed_In_A_Row()
    {
        StudioSettings.DropAsks = false;

        var (window, board) = await Board(Downloading("ico_a", "ico_b"), "Board");
        var group = Group(window, "Board");

        Drop(window, OnBoard(window, board, 40f, 50f), Carrying(window.Streamline, Parsed(Icon("ico_a", "cog")), Parsed(Icon("ico_b", "gear"))));
        await Until(() => group.Children.Count == 4);

        var (cog, gear) = (group.Children[2], group.Children[3]);

        Assert.Equal(("cog", 40f, 50f), (cog.Name, cog.X!.Value, cog.Y!.Value));

        // Beside it by the drawing's 48 and a tenth of it, as a spread spaces a board.
        Assert.Equal(("gear", 92.8f, 50f), (gear.Name, gear.X!.Value, gear.Y!.Value));
    }

    /// <summary>The other icon lands beside the one the drop imported, and neither import takes the board out of the tab.</summary>
    [AvaloniaFact]
    public async Task A_Mixed_Drop_On_A_Board_Lands_The_Other_Beside_It_And_Stays_On_The_Board()
    {
        StudioSettings.DropAsks = false;

        Sure("sure-glyphs");

        // With Project's templates, so an icon nobody remembered has two to choose from.
        var templates = Project[Project.IndexOf("<e:templates", StringComparison.Ordinal)..(Project.IndexOf("</e:templates>", StringComparison.Ordinal) + "</e:templates>".Length)];
        var (window, board) = await Board(Downloading("ico_a", "ico_b"), "Board", Boards.Replace("<group name=\"Board\">", templates + "<group name=\"Board\">", StringComparison.Ordinal));
        var panel = window.Streamline;
        var frame = (ProjectGroup)Group(window, "Board").Children.Single(node => node.Name == "Frame");
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var shown = Answering(window, true);

        Drop(window, OnBoard(window, board, 110f, 12f), Carrying(panel, Parsed(Icon("ico_a", "cog", "sure-glyphs")), Parsed(Icon("ico_b", "gear", "line-glyphs"))));
        await Until(() => frame.Children.Count == 3);

        Assert.Same(frame, Assert.Single(shown).Target);
        Assert.Equal(("cog", 10f, 12f), (frame.Children[1].Name, frame.Children[1].X!.Value, frame.Children[1].Y!.Value));
        Assert.Equal(("gear", 62.8f, 12f), (frame.Children[2].Name, frame.Children[2].X!.Value, frame.Children[2].Y!.Value));
        Assert.Same(board, ((TabItem)tabs.SelectedItem!).Content);
    }

    /// <summary>An import the board in front already shows, its own group's or a group's inside it, opens no tab; one anywhere else does.</summary>
    [AvaloniaTheory]
    [InlineData("Board", false)]
    [InlineData("Frame", false)]
    [InlineData("Loose", true)]
    public async Task An_Import_Into_The_Board_In_Front_Stays_On_The_Board(string into, bool opens)
    {
        var (window, board) = await Board(Searching("line-glyphs"), "Board");
        var panel = window.Streamline;
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var group = into == "Frame"
            ? (ProjectGroup)Group(window, "Board").Children.Single(node => node.Name == "Frame")
            : Group(window, into);

        var before = tabs.Items.Count;

        panel.Target = group;
        Answering(window, true);

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);
        await panel.OpenImportAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("bell", group.Children[^1].Name);
        Assert.Equal(before + (opens ? 1 : 0), tabs.Items.Count);
        Assert.Equal(opens ? group.Children[^1] : board.Node, ((TabItem)tabs.SelectedItem!).Tag);
    }

    /// <summary>Near a line of the grid, the drop lands on it, as a tile carried there would.</summary>
    [AvaloniaFact]
    public async Task An_Icon_Dropped_On_A_Board_Near_A_Line_Lands_On_It()
    {
        StudioSettings.DropAsks = false;

        StudioSettings.GridSize = 16f;
        StudioSettings.SnapToGrid = true;

        var (window, board) = await Board(Downloading("ico_a"), "Board");
        var frame = (ProjectGroup)Group(window, "Board").Children.Single(node => node.Name == "Frame");
        var pull = Canvas(board).Grid.Pull;

        Assert.True(pull > 0f, "the board's grid is not magnetic");

        Drop(window, OnBoard(window, board, 112f - (pull / 2f), 16f + (pull / 2f)), Carrying(window.Streamline, Parsed(Icon("ico_a", "cog"))));
        await Until(() => frame.Children.Count == 2);

        Assert.Equal((12f, 16f), (frame.Children[1].X!.Value, frame.Children[1].Y!.Value));
    }

    /// <summary>A place given there would be the first on its board and re-lay every row nobody touched.</summary>
    [AvaloniaTheory]
    [InlineData("Empty")]
    [InlineData("Loose")]
    public async Task An_Icon_Dropped_On_An_Empty_Board_Or_A_Spread_Gets_No_Place(string name)
    {
        StudioSettings.DropAsks = false;

        var (window, board) = await Board(Downloading("ico_a"), name);
        var group = Group(window, name);
        var canvas = Canvas(board);
        var before = group.Children.Count;

        Drop(window, canvas.TranslatePoint(new Point(canvas.Bounds.Width / 2d, canvas.Bounds.Height / 2d), window)!.Value, Carrying(window.Streamline, Parsed(Icon("ico_a", "cog"))));
        await Until(() => group.Children.Count == before + 1);

        Assert.Equal("cog", group.Children[^1].Name);
        Assert.All(group.Children, child => Assert.False(child.HasPosition));
    }

    /// <summary>Three tiles, with the first two picked.</summary>
    private async Task<(MainWindow Window, StreamlinePanel Panel)> Tiled()
    {
        var streamline = Downloading("ico_a", "ico_b", "ico_c");

        streamline.Json("/v1/search/global", Page("results", new[] { Icon("ico_a", "bell"), Icon("ico_b", "bin"), Icon("ico_c", "cog") }, more: false, next: 3));

        var window = await Host(streamline);
        var panel = window.Streamline;

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);
        await panel.PickAsync(1, KeyModifiers.Shift);
        Settle(window);

        return (window, panel);
    }

    /// <summary>The tile showing <paramref name="name"/>, found again each time: a pick rebuilds them.</summary>
    private static Border Tile(StreamlinePanel panel, string name)
        => panel.GetVisualDescendants().OfType<Border>().Single(border => ToolTip.GetTip(border) as string == name);

    [AvaloniaFact]
    public async Task A_Press_And_Release_On_A_Picked_Tile_Picks_Only_It()
    {
        var (window, panel) = await Tiled();

        Press(window, Tile(panel, "bin"), new Point(20, 20));

        Assert.Equal(new[] { "bell", "bin" }, panel.Picked.Select(icon => icon.Name));

        Release(window, Tile(panel, "bin"), new Point(20, 20));

        Assert.Equal(new[] { "bin" }, panel.Picked.Select(icon => icon.Name));
    }

    [AvaloniaTheory]
    [InlineData(KeyModifiers.None)]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Meta)]
    public async Task Dragging_A_Picked_Tile_Carries_Every_Pick(KeyModifiers modifiers)
    {
        var (window, panel) = await Tiled();
        List<string>? carried = null;

        panel.StartDrag = (_, data) =>
        {
            Assert.True(((IDataTransfer)data).Contains(StreamlinePanel.DragFormat));
            carried = panel.Dragged.Select(icon => icon.Name).ToList();

            return Task.CompletedTask;
        };

        Drag(window, Tile(panel, "bin"), new Point(20, 20), new Point(20, 60), modifiers: modifiers);

        Assert.Equal(new[] { "bell", "bin" }, carried);
        Assert.Equal(new[] { "bell", "bin" }, panel.Picked.Select(icon => icon.Name));
        Assert.Empty(panel.Dragged);
    }

    [AvaloniaFact]
    public async Task Dragging_An_Unpicked_Tile_Carries_Only_It()
    {
        var (window, panel) = await Tiled();
        List<string>? carried = null;

        panel.StartDrag = (_, _) =>
        {
            carried = panel.Dragged.Select(icon => icon.Name).ToList();

            return Task.CompletedTask;
        };

        Press(window, Tile(panel, "cog"), new Point(20, 20));

        // The press picked it, and the pick built the tiles again under the pointer.
        Settle(window);
        Move(window, Tile(panel, "cog"), new Point(20, 60));

        Assert.Equal(new[] { "cog" }, carried);
    }

    /// <summary>Over the panel itself, a drawing's tab or anything else that takes no tiles, the drag shows it will do nothing.</summary>
    [AvaloniaFact]
    public async Task Tiles_Dragged_Where_Nothing_Takes_Them_Show_None()
    {
        var window = await Host(Searching("line-glyphs"));
        var panel = window.Streamline;
        var shown = new List<DragDropEffects>();

        await panel.SearchAsync();
        Settle(window);

        window.AddHandler(DragDrop.DragOverEvent, (_, e) => shown.Add(e.DragEffects), RoutingStrategies.Bubble, handledEventsToo: true);

        Drop(window, panel.TranslatePoint(new Point(panel.Bounds.Width / 2d, panel.Bounds.Height / 2d), window)!.Value, Carrying(panel, Parsed(Icon("ico_c", "cog"))));

        Assert.Equal(new[] { DragDropEffects.None }, shown);
        Assert.Equal(0, window.Workspace!.Edits);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_A_Key_Or_While_A_Family_Imports_The_Drop_Targets_Refuse(bool importing)
    {
        var streamline = Catalogue();
        var held = new TaskCompletionSource();

        streamline.Holding = request => request.RequestUri!.AbsolutePath == "/v1/family-groups" ? held.Task : Task.CompletedTask;

        var window = await Host(streamline, key: importing ? "sk_test" : null);
        var panel = window.Streamline;
        var scheme = Group(window, "Scheme");
        var shown = new List<DragDropEffects>();

        panel.ConfirmFamily = _ => Task.FromResult(false);

        var family = importing ? panel.ImportFamilyAsync(Parsed(Icon("ico_a", "a"))) : Task.CompletedTask;

        try
        {
            window.AddHandler(DragDrop.DragOverEvent, (_, e) => shown.Add(e.DragEffects), RoutingStrategies.Bubble, handledEventsToo: true);

            Drop(window, OnRow(window, "Scheme"), Carrying(panel, Parsed(Icon("ico_c", "cog"))));

            await window.ShowAsync(scheme);
            Settle(window, 1600, 1000);

            var canvas = Canvas((GroupPanel)((TabItem)window.FindControl<TabControl>("Tabs")!.SelectedItem!).Content!);

            Drop(window, canvas.TranslatePoint(new Point(canvas.Bounds.Width / 2d, canvas.Bounds.Height / 2d), window)!.Value, Carrying(panel, Parsed(Icon("ico_c", "cog"))));
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new[] { DragDropEffects.None, DragDropEffects.None }, shown);
            Assert.Equal(2, scheme.Children.Count);
            Assert.Equal(0, streamline.Count("/v1/icons/ico_c"));
        }
        finally
        {
            held.SetResult();
            await family;
        }
    }
}
