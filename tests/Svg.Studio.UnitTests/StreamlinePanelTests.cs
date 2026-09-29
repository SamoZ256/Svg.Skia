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
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>The Streamline panel: where it sits, searching, picking, and importing through the project's templates.</summary>
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
        public List<string> Asked { get; } = new();

        public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Paths { get; } = new(StringComparer.Ordinal);

        /// <summary>What a request waits on before it is answered, for a test to act in the meantime.</summary>
        public Func<HttpRequestMessage, Task> Holding { get; set; } = _ => Task.CompletedTask;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;

            lock (Asked)
            {
                Asked.Add(uri.PathAndQuery);
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
            lock (Asked)
            {
                return Asked.Count(asked => asked.StartsWith(prefix, StringComparison.Ordinal));
            }
        }

        public static HttpResponseMessage Refused(HttpStatusCode status, string message)
            => new(status) { Content = new StringContent($$"""{ "message": "{{message}}" }""") };

        public void Json(string path, string body) => Paths[path] = _ => Answer(body, "application/json");

        public void Svg(string hash, string body) => Paths[$"/v1/icons/{hash}/download/svg"] = _ => Answer(body, "image/svg+xml");

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

    /// <summary>Lets the dispatcher run until <paramref name="done"/>, for what a click starts and nobody awaits.</summary>
    private static async Task Until(Func<bool> done)
    {
        for (var waited = 0; !done(); waited++)
        {
            Assert.True(waited < 500, "What was waited for never happened.");

            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Settle(MainWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(1000, 800));
        window.Arrange(new Rect(0, 0, 1000, 800));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void The_Panel_Is_In_The_Default_Arrangement_Beside_The_Settings()
    {
        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("project+elements+streamline/", window.Layout, StringComparison.Ordinal);
        Assert.Same(window, TopLevel.GetTopLevel(window.Streamline));
    }

    /// <summary>A line saved before the panel existed does not name it, and the dock places it beside its default neighbours.</summary>
    [AvaloniaFact]
    public void A_Layout_Saved_Before_The_Panel_Existed_Still_Has_It()
    {
        StudioSettings.Layout = Legacy;

        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Behind the tab that was in front, which an upgrade has no business changing.
        Assert.Contains("project+elements+streamline/1/project/open", window.Layout, StringComparison.Ordinal);
        Assert.Same(window, TopLevel.GetTopLevel(window.Streamline));

        // Placed, not written back: the saved line is what somebody arranged, and is left as it was.
        Assert.Equal(Legacy, StudioSettings.Layout);
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

    [AvaloniaFact]
    public async Task A_Search_Fills_The_Tiles_And_Load_More_Appends()
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

        Assert.Equal(new[] { "bell", "bin" }, panel.Results.Select(icon => icon.Name));
        Assert.Equal(new[] { "bell", "bin" }, Tiles(panel));

        // Only the one this key cannot download carries the lock.
        // The style box's chevron is a PathIcon as well, so the lock is told by its tip.
        Assert.Single(panel.GetVisualDescendants().OfType<PathIcon>(), icon => ToolTip.GetTip(icon) is string tip && tip.Contains("plan", StringComparison.Ordinal));

        var more = panel.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Load more"));

        Assert.True(more.IsVisible);

        more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => panel.Results.Count == 3);
        Settle(window);

        Assert.Equal(new[] { "bell", "bin", "cog" }, panel.Results.Select(icon => icon.Name));
        Assert.Equal(new[] { "bell", "bin", "cog" }, Tiles(panel));
        Assert.False(more.IsVisible);
        Assert.Contains(streamline.Asked, asked => asked.Contains("offset=2", StringComparison.Ordinal));
        Assert.Null(panel.Said);
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

        var row = Assert.Single(panel.Rows);

        Assert.Equal(new[] { "bell", "bin" }, row.Icons.Select(icon => icon.Name));
        Assert.Equal("Accent glyph", row.Template.SelectedItem);
        Assert.Equal("stateAccentColor", row.Roles["#000000"].SelectedItem);
        Assert.Equal("Nothing to declare", row.Declares.Text);

        // The one boolean the bound fill reaches, as the group panel would seed it.
        var toggle = Assert.Single(row.Toggles);

        Assert.Equal("isLight", toggle.Content);
        Assert.False(toggle.IsChecked);

        toggle.IsChecked = true;

        var before = scheme.Children.Count;
        var added = await panel.ImportAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "bell", "bin" }, added.Select(drawing => drawing.Name));
        Assert.Equal(new[] { "streamline:ico_a", "streamline:ico_b" }, added.Select(drawing => drawing.Source));
        Assert.All(added, drawing => Assert.Contains("fill=\"{{ stateAccentColor }}\"", drawing.Text, StringComparison.Ordinal));
        Assert.Equal("add 2 drawings", window.Workspace!.UndoLabel);
        Assert.Empty(panel.Rows);
        Assert.Empty(panel.Picked);

        Assert.True(window.Workspace.Undo());
        Assert.Equal(before, scheme.Children.Count);
        Assert.False(window.Workspace.CanUndo);
    }

    [AvaloniaFact]
    public async Task A_Template_Changed_By_Hand_Is_Remembered_And_A_Colour_Can_Be_Given_Another_Role()
    {
        var streamline = Searching("remembered-glyphs");
        var window = await Host(streamline);
        var panel = window.Streamline;

        panel.Target = Group(window, "Scheme");

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);

        var row = Assert.Single(panel.Rows);
        var icon = TemplateLibrary.Prepare(Glyph, "remembered-glyphs", null, "bell", new[] { "#000000" });

        Assert.Null(TemplateLibrary.Remembered(icon));

        row.Template.SelectedItem = "Mono glyph";

        Assert.Equal("Mono glyph", TemplateLibrary.Remembered(icon));
        Assert.Equal("stateBlackColor", row.Roles["#000000"].SelectedItem);
        Assert.Contains(Labels(row), label => label == "glyph → stateBlackColor");

        row.Roles["#000000"].SelectedItem = "stateAccentColor";

        Assert.Contains("fill=\"{{ stateAccentColor }}\"", row.Text(0), StringComparison.Ordinal);
        Assert.Contains(Labels(row), label => label == "by hand → stateAccentColor");

        row.Roles["#000000"].SelectedItem = StreamlineRow.Keep;

        Assert.Null(row.Recipe(0));
    }

    private static IEnumerable<string?> Labels(StreamlineRow row) => row.View.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text);

    private static Streamline Searching(string family)
    {
        var streamline = new Streamline();

        streamline.Json("/v1/search/global", Page("results", new[] { Icon("ico_a", "bell", family), Icon("ico_b", "bin", family) }, more: false, next: 2));
        streamline.Svg("ico_a", Glyph);
        streamline.Svg("ico_b", Glyph.Replace("M2 2", "M4 4", StringComparison.Ordinal));

        return streamline;
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

    /// <summary>The next page is of the search showing, not of what has been typed since and not yet searched.</summary>
    [AvaloniaFact]
    public async Task Load_More_Pages_The_Search_Showing_Not_What_Is_Typed()
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

    /// <summary>A locked icon's refusal is said once and unpicks it, rather than being asked again at every later click.</summary>
    [AvaloniaFact]
    public async Task An_Icon_That_Would_Not_Download_Is_Unpicked_And_Not_Asked_For_Again()
    {
        var streamline = Searching("line-glyphs");

        streamline.Paths["/v1/icons/ico_b/download/svg"] = _ => Streamline.Refused(HttpStatusCode.Forbidden, "Premium icon");

        var window = await Host(streamline);
        var panel = window.Streamline;

        await panel.SearchAsync();
        await panel.PickAsync(1, KeyModifiers.None);

        Assert.Empty(panel.Picked);
        Assert.Empty(panel.Rows);
        Assert.Contains("Premium icon", panel.Said, StringComparison.Ordinal);

        await panel.PickAsync(0, KeyModifiers.Control);
        await panel.PickAsync(0, KeyModifiers.Control);
        await panel.PickAsync(0, KeyModifiers.Control);

        Assert.Equal(new[] { "bell" }, panel.Picked.Select(icon => icon.Name));
        Assert.Single(panel.Rows);
        Assert.Equal(1, streamline.Count("/v1/icons/ico_b/download"));
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

        var window = await Host(streamline);
        var panel = window.Streamline;

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);
        await panel.PickAsync(0, KeyModifiers.None);

        Assert.Equal(2, streamline.Asked.Count(asked => asked.Split('?')[0] == "/v1/icons/ico_d"));
        Assert.Equal(0, streamline.Count("/v1/icons/ico_d/download"));
        Assert.Contains("Hourly limit reached", panel.Said, StringComparison.Ordinal);
    }

    /// <summary>A second click while the first import is still showing its result imports nothing more.</summary>
    [AvaloniaFact]
    public async Task Importing_Twice_At_Once_Imports_Once()
    {
        var window = await Host(Searching("line-glyphs"));
        var panel = window.Streamline;
        var scheme = Group(window, "Scheme");
        var before = scheme.Children.Count;

        panel.Target = scheme;

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);

        var first = panel.ImportAsync();
        var second = panel.ImportAsync();

        await Task.WhenAll(first, second);

        Assert.Equal(before + 1, scheme.Children.Count);
        Assert.Empty(await second);
    }

    [AvaloniaFact]
    public async Task With_No_Project_Open_Each_Icon_Opens_In_A_Tab_Of_Its_Own()
    {
        var window = await Host(Searching("line-glyphs"), project: false);
        var panel = window.Streamline;
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        var before = tabs.Items.Count;

        Assert.Contains(panel.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text?.StartsWith("No project is open", StringComparison.Ordinal) == true);

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);
        await panel.PickAsync(1, KeyModifiers.Shift);

        Assert.Equal(TemplateLibrary.KeepColoursName, Assert.Single(panel.Rows).Template.SelectedItem);

        var added = await panel.ImportAsync();

        Assert.Empty(added);
        Assert.Equal(before + 2, tabs.Items.Count);
        Assert.Empty(panel.Rows);
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
        Assert.Contains(panel.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "Icons go into Scheme.");

        static IEnumerable<TreeViewItem> Items(ItemsControl parent)
            => parent.Items.OfType<TreeViewItem>().SelectMany(item => Items(item).Prepend(item));
    }

    /// <summary>Changing one row's template offers it to the rest of that family, and taking the offer applies it.</summary>
    [AvaloniaFact]
    public async Task A_Template_Chosen_For_One_Row_Is_Offered_To_The_Family()
    {
        var streamline = new Streamline();

        streamline.Json("/v1/search/global", Page("results", new[] { Icon("ico_a", "bell", "spread-glyphs"), Icon("ico_b", "bin", "spread-glyphs", colour: "#FF0000") }, more: false, next: 2));
        streamline.Svg("ico_a", Glyph);
        streamline.Svg("ico_b", Glyph.Replace("#000000", "#FF0000", StringComparison.Ordinal));

        var window = await Host(streamline);
        var panel = window.Streamline;

        panel.Target = Group(window, "Scheme");

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);
        await panel.PickAsync(1, KeyModifiers.Shift);

        Assert.Equal(2, panel.Rows.Count);

        var (first, second) = (panel.Rows[0], panel.Rows[1]);

        first.Template.SelectedItem = "Mono glyph";

        Assert.True(first.Spread.IsVisible);
        Assert.Equal("Use Mono glyph for the 1 other of this family", first.Spread.Content);

        first.Spread.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("Mono glyph", second.Template.SelectedItem);
        Assert.False(first.Spread.IsVisible);
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

    private static void Named(MainWindow window, string name, bool onlyFamily = false, List<string?>? families = null)
        => window.AskTemplateName = (_, family) =>
        {
            families?.Add(family);

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
                  <e:match colors="1" strokes="0" />
                  <e:slot name="rest" rest="true">stateAccentColor</e:slot>
                </e:recipe>
              </e:templates>
            """.ReplaceLineEndings("\n"),
            workspace.Document.ToXml(),
            StringComparison.Ordinal);
        Assert.Equal("add template Accent from Bell", workspace.UndoLabel);

        // Listed and selected, with its text to edit.
        var listed = Assert.IsType<TemplateEntry>(window.Streamline.TemplateList.SelectedItem);

        Assert.Equal("Accent from Bell", listed.Name);
        Assert.Null(listed.Error);
        Assert.Contains("stateAccentColor", window.Streamline.TemplateText.Text, StringComparison.Ordinal);

        Assert.True(workspace.Undo());
        Assert.Equal(was, workspace.Document.ToXml());
        Assert.False(workspace.CanUndo);
        Assert.Equal(new[] { "Accent glyph", "Mono glyph" }, window.Streamline.TemplateList.Items.OfType<TemplateEntry>().Select(entry => entry.Name));
    }

    [AvaloniaFact]
    public async Task A_Template_Is_Edited_Renamed_And_Deleted_In_The_Panel_Each_As_One_Step()
    {
        var window = await Host(new Streamline());
        var workspace = window.Workspace!;
        var root = workspace.Document.Root;
        var panel = window.Streamline;

        panel.TemplateList.SelectedIndex = 0;

        Assert.Equal(root.Templates[0].Text, panel.TemplateText.Text);
        Assert.Null(panel.TemplateSaid);

        // Said as it is typed, and refused where the block could not hold it.
        panel.TemplateText.Text = "<e:recipe";
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(panel.TemplateSaid);

        panel.ApplyTemplateEdit();

        Assert.NotNull(panel.TemplateSaid);
        Assert.False(workspace.CanUndo);

        // Kept, and marked, where it is a recipe that does not parse: the file can hold it.
        panel.TemplateText.Text = root.Templates[0].Text.Replace("<e:slot name=\"glyph\"", "<e:slot by=\"size\" name=\"glyph\"", StringComparison.Ordinal);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("by=\"size\"", panel.TemplateSaid, StringComparison.Ordinal);

        panel.ApplyTemplateEdit();

        Assert.NotNull(Assert.IsType<TemplateEntry>(panel.TemplateList.SelectedItem).Error);
        Assert.True(workspace.Undo());

        var was = workspace.Document.ToXml();

        panel.TemplateList.SelectedIndex = 0;
        panel.TemplateText.Text = root.Templates[0].Text.Replace(">stateAccentColor<", ">stateBlackColor<", StringComparison.Ordinal);
        Dispatcher.UIThread.RunJobs();
        panel.ApplyTemplateEdit();

        Assert.Equal("edit template Accent glyph", workspace.UndoLabel);
        Assert.Contains(">stateBlackColor</e:slot>", root.Templates[0].Text, StringComparison.Ordinal);
        Assert.Equal(was.Replace(">stateAccentColor</e:slot>", ">stateBlackColor</e:slot>", StringComparison.Ordinal), workspace.Document.ToXml());
        Assert.True(workspace.Undo());
        Assert.Equal(was, workspace.Document.ToXml());
        Assert.Equal(root.Templates[0].Text, panel.TemplateText.Text);

        // A name another template has is refused rather than making two a picker cannot tell apart.
        Named(window, "Mono glyph");
        await panel.RenameTemplateAsync();

        Assert.Contains("already has a template called 'Mono glyph'", panel.TemplateSaid, StringComparison.Ordinal);
        Assert.Equal(was, workspace.Document.ToXml());

        Named(window, "Accent");
        await panel.RenameTemplateAsync();

        Assert.Equal("rename template Accent glyph to Accent", workspace.UndoLabel);
        Assert.Equal(new[] { "Accent", "Mono glyph" }, root.Templates.Select(template => template.Name));
        Assert.Equal("Accent", Assert.IsType<TemplateEntry>(panel.TemplateList.SelectedItem).Name);

        panel.DeleteTemplate();

        Assert.Equal("remove template Accent", workspace.UndoLabel);
        Assert.Equal(new[] { "Mono glyph" }, root.Templates.Select(template => template.Name));

        Assert.True(workspace.Undo());
        Assert.True(workspace.Undo());
        Assert.Equal(was, workspace.Document.ToXml());
        Assert.Equal(new[] { "Accent glyph", "Mono glyph" }, panel.TemplateList.Items.OfType<TemplateEntry>().Select(entry => entry.Name));
    }

    [AvaloniaFact]
    public async Task A_Rows_Mapping_Is_Saved_As_A_Template_And_The_Selected_One_Previews_On_The_Picked_Icon()
    {
        var window = await Host(Searching("line-glyphs"));
        var workspace = window.Workspace!;
        var panel = window.Streamline;
        var families = new List<string?>();

        panel.Target = Group(window, "Scheme");

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);

        // The first template selected, drawn on the icon picked.
        panel.TemplateList.SelectedIndex = 0;

        Assert.Contains("fill=\"{{ stateAccentColor }}\"", panel.TemplatePreviewText, StringComparison.Ordinal);

        var row = Assert.Single(panel.Rows);

        row.Roles["#000000"].SelectedItem = "stateBlackColor";

        Named(window, "Line black", onlyFamily: true, families);

        await panel.SaveTemplateAsync(row);

        Assert.Equal(new[] { "Core Duo" }, families);
        Assert.Equal("add template Line black", workspace.UndoLabel);

        var saved = workspace.Document.Root.Templates[^1];

        Assert.Equal("Line black", saved.Name);
        Assert.Contains("""<e:match colors="1" strokes="0" family="line-glyphs" />""", saved.Text, StringComparison.Ordinal);
        Assert.Contains("""<e:slot name="rest" rest="true">stateBlackColor</e:slot>""", saved.Text, StringComparison.Ordinal);
        Assert.Equal("Line black", Assert.IsType<TemplateEntry>(panel.TemplateList.SelectedItem).Name);
        Assert.Contains("fill=\"{{ stateBlackColor }}\"", panel.TemplatePreviewText, StringComparison.Ordinal);

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

        MainWindowProjectTests.Pick(window, "a", "Update from Streamline");
        await Until(() => panel.Rows.Count == 1);

        var row = Assert.Single(panel.Rows);

        Assert.Equal("Mono glyph", row.Template.SelectedItem);

        var updated = await panel.ImportAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(drawing, Assert.Single(updated));
        Assert.Equal("update a", workspace.UndoLabel);
        Assert.Contains("M2 2h40v40z", drawing.Text, StringComparison.Ordinal);
        Assert.Contains("fill=\"{{ stateBlackColor }}\"", drawing.Text, StringComparison.Ordinal);
        Assert.Equal("a", drawing.Name);
        Assert.Equal("streamline:ico_a", drawing.Source);
        Assert.Equal(new[] { "first", "a", "last" }, group.Children.Select(node => node.Name));
        Assert.Same(drawing, group.Children[1]);
        Assert.Empty(panel.Rows);

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

    /// <summary>Closing the project ends an update of one of its drawings rather than leaving a button that does nothing.</summary>
    [AvaloniaFact]
    public async Task An_Update_Is_Dropped_When_Its_Project_Is_Closed()
    {
        var streamline = new Streamline();

        streamline.Json("/v1/icons/ico_a", Icon("ico_a", "a"));
        streamline.Svg("ico_a", Glyph);

        var window = await Host(streamline);
        var panel = window.Streamline;
        var drawing = (ProjectDrawing)((ProjectGroup)Group(window, "Scheme").Children.Single(node => node.Name == "Core Duo")).Children.Single();

        await panel.UpdateAsync(drawing);

        Assert.Single(panel.Rows);
        Assert.True(await window.CloseProjectAsync());
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(panel.Rows);
        Assert.Empty(panel.Picked);
    }

    /// <summary>
    /// In the arrangement a window comes up in, opening the section leaves the search in sight and
    /// its own buttons within reach, rather than taking the strip and pushing both out of it.
    /// </summary>
    [AvaloniaFact]
    public async Task The_Templates_Section_Open_In_The_Default_Arrangement_Leaves_Everything_Reachable()
    {
        var window = await Host(new Streamline());
        var panel = window.Streamline;

        window.Layout = StudioSettings.DefaultLayout;
        Named(window, "From Bell");

        await window.ExtractTemplateAsync((ProjectDrawing)Group(window, "Scheme").Children.Single(node => node.Name == "Bell"));

        window.Measure(new Size(1280, 800));
        window.Arrange(new Rect(0, 0, 1280, 800));
        Dispatcher.UIThread.RunJobs();

        var tiles = panel.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll => scroll.Content is ItemsControl);
        var apply = panel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Apply"));
        var section = apply.FindAncestorOfType<ScrollViewer>()!;
        var bottom = section.TranslatePoint(new Point(0, section.Bounds.Height), panel)!.Value.Y;

        Assert.True(panel.IsEffectivelyVisible);
        Assert.True(tiles.Bounds.Height > 0, $"The tiles are {tiles.Bounds.Height}px high.");
        Assert.True(bottom <= panel.Bounds.Height + 0.5, $"The section ends at {bottom}, below the panel's {panel.Bounds.Height}.");
    }

    /// <summary>An edit not applied is kept while another template is looked at, and follows a rename.</summary>
    [AvaloniaFact]
    public async Task An_Edit_Not_Applied_Survives_The_Selection_Moving()
    {
        var window = await Host(new Streamline());
        var workspace = window.Workspace!;
        var root = workspace.Document.Root;
        var panel = window.Streamline;
        var bell = (ProjectDrawing)Group(window, "Scheme").Children.Single(node => node.Name == "Bell");

        panel.TemplateList.SelectedIndex = 1;

        var edit = root.Templates[1].Text.Replace(">stateBlackColor<", ">stateAccentColor<", StringComparison.Ordinal);

        panel.TemplateText.Text = edit;

        // Extract moves the selection to what it made.
        Named(window, "Zed");
        await window.ExtractTemplateAsync(bell);

        Assert.Equal("Zed", Assert.IsType<TemplateEntry>(panel.TemplateList.SelectedItem).Name);
        Assert.Equal(root.Templates[2].Text, panel.TemplateText.Text);

        panel.TemplateList.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(edit, panel.TemplateText.Text);
        Assert.Contains(">stateBlackColor<", root.Templates[1].Text, StringComparison.Ordinal);

        // Renamed with it, so applying it afterwards does not rename it back.
        Named(window, "Mono");
        await panel.RenameTemplateAsync();

        Assert.Equal("Mono", root.Templates[1].Name);
        Assert.Contains("name=\"Mono\"", panel.TemplateText.Text, StringComparison.Ordinal);
        Assert.Contains(">stateAccentColor<", panel.TemplateText.Text, StringComparison.Ordinal);

        panel.ApplyTemplateEdit();

        Assert.Equal("Mono", root.Templates[1].Name);
        Assert.Contains(">stateAccentColor<", root.Templates[1].Text, StringComparison.Ordinal);
        Assert.Equal(root.Templates[1].Text, panel.TemplateText.Text);
    }
}
