// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions;
using Svg.Expressions.Recipes;
using Svg.SourceEditing;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>Streamline's icons, searched, picked and brought into the project through its templates.</summary>
/// <remarks>
/// The window's rather than a tab's: what it imports into is the tree's selection, which no tab
/// owns, and a search should not start again because a different drawing came to the front.
/// </remarks>
public sealed class StreamlinePanel : UserControl
{
    /// <summary>The size an icon is downloaded at where no template names one.</summary>
    public const int DefaultSize = 48;

    /// <summary>What <see cref="ProjectDrawing.Source"/> starts with for a drawing imported from here.</summary>
    public const string SourcePrefix = "streamline:";

    private const int PageSize = 50;

    private const double TileSize = 56d;

    private const double TileGap = 4d;

    private const string AnyStyle = "Any style";

    /// <summary>The styles a search filters by, as Streamline's reference lists them.</summary>
    private static readonly string[] s_styles =
    {
        AnyStyle, "line", "solid", "flat", "duo", "hand-drawn", "creative", "gradient", "remix", "neon", "pop",
        "light", "glyph", "minimal", "outlined", "geometric", "bold", "stroke", "wireframe", "filled"
    };

    private static readonly Geometry s_lock = Geometry.Parse("M4 7V5a4 4 0 0 1 8 0v2h1v8H3V7zm2 0h4V5a2 2 0 0 0-4 0z");

    private readonly MainWindow _window;

    private readonly TextBox _query = new() { PlaceholderText = "Search Streamline" };

    private readonly ComboBox _style = new() { ItemsSource = s_styles, SelectedIndex = 0, Margin = new Thickness(6, 0, 0, 0) };

    private readonly TextBlock _keyless = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    private readonly Control _noKey;

    private readonly Control _body;

    private readonly TextBlock _said = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };

    private readonly TextBlock _where = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };

    private readonly ItemsControl _tiles;

    private readonly ScrollViewer _tileScroll;

    private readonly Button _more = new() { Content = "Load more", IsVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch };

    private readonly StackPanel _rows = new() { Spacing = 8 };

    private readonly Button _import = new() { Content = "Import", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };

    private readonly StackPanel _family = new() { Spacing = 4, IsVisible = false };

    private readonly TextBlock _familyProgress = new() { TextWrapping = TextWrapping.Wrap };

    private readonly ProgressBar _familyBar = new() { Minimum = 0 };

    private readonly Button _cancel = new() { Content = "Cancel" };

    private readonly DispatcherTimer _typing = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private readonly Expander _templates = new() { Header = "Templates", HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false };

    private readonly ListBox _templateList = new() { MaxHeight = 120 };

    private readonly TextBox _templateText = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Menlo, Consolas, monospace"),
        FontSize = 11,
        Height = 180
    };

    private readonly TextBlock _templateSaid = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };

    private readonly SvgViewerCanvas _templatePreview = new()
    {
        Width = 72,
        Height = 72,
        IsZoomEnabled = false,
        IsPanEnabled = false,
        Margin = new Thickness(0, 0, 8, 0)
    };

    private readonly TextBlock _previewSaid = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };

    private SvgViewerDocument? _previewDocument;

    /// <summary>The selected template as the project holds it, so an edit elsewhere does not overwrite one typed here.</summary>
    private (string Name, string Text)? _templateLoaded;

    /// <summary>Edits typed into a template and not applied, by its name, kept while another is looked at.</summary>
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);

    private bool _listing;

    /// <summary>The drawing an import replaces, where the rows are an update of it rather than new icons.</summary>
    private ProjectDrawing? _updating;

    private StreamlineClient? _client;
    private string? _key;
    private int _refreshes;

    private readonly List<StreamlineIcon> _results = new();
    private (string Query, string? Style) _searched = (string.Empty, null);
    private int _next;
    private int _searches;

    /// <summary>The style each result was searched under, since an icon does not say its own.</summary>
    private readonly Dictionary<string, string?> _searchedStyle = new(StringComparer.Ordinal);

    /// <summary>What is picked, kept across searches so a batch can be gathered from several.</summary>
    private readonly List<StreamlineIcon> _picked = new();
    private int? _anchor;
    private int _picks;

    private readonly Dictionary<string, Task<Bitmap?>> _thumbnails = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<StreamlineDownload?>> _downloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StreamlineFamily?> _families = new(StringComparer.Ordinal);
    private readonly List<StreamlineRow> _shown = new();
    private int _columns = 1;
    private ProjectGroup? _selected;
    private ProjectWorkspace? _suggestedIn;
    private CancellationTokenSource? _running;
    private bool _spreading;

    public StreamlinePanel(MainWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));

        ConfirmFamily = _ => Task.FromResult(false);

        _typing.Tick += async (_, _) => await SearchAsync().ConfigureAwait(true);
        _query.TextChanged += (_, _) =>
        {
            _typing.Stop();
            _typing.Start();
        };
        _query.KeyDown += async (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return)
            {
                e.Handled = true;

                await SearchAsync().ConfigureAwait(true);
            }
        };
        _style.SelectionChanged += async (_, _) => await SearchAsync().ConfigureAwait(true);
        _more.Click += async (_, _) => await SearchAsync(more: true).ConfigureAwait(true);
        _import.Click += async (_, _) => await ImportAsync().ConfigureAwait(true);
        _cancel.Click += (_, _) => _running?.Cancel();

        _tiles = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<TileRow>((row, _) => row is null ? new Panel() : Tiles(row), supportsRecycling: false)
        };

        _tileScroll = new ScrollViewer { Content = _tiles, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _tileScroll.SizeChanged += (_, _) =>
        {
            if (Columns() != _columns)
            {
                ShowTiles();
            }
        };

        var settings = new Button { Content = "Settings…", Margin = new Thickness(8, 0, 0, 0) };

        settings.Click += async (_, _) => await _window.ShowSettingsAsync().ConfigureAwait(true);

        var keyless = new DockPanel { Margin = new Thickness(10), IsVisible = false };
        DockPanel.SetDock(settings, Dock.Right);
        keyless.Children.Add(settings);
        keyless.Children.Add(_keyless);
        _noKey = keyless;

        var search = new DockPanel();
        DockPanel.SetDock(_style, Dock.Right);
        search.Children.Add(_style);
        search.Children.Add(_query);

        _family.Children.Add(_familyProgress);
        _family.Children.Add(_familyBar);
        _family.Children.Add(_cancel);

        var body = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,*,Auto,Auto"),
            Margin = new Thickness(8),
            RowSpacing = 6,
            IsVisible = false
        };

        Place(body, search, 0);
        Place(body, _said, 1);
        Place(body, _where, 2);
        Place(body, _tileScroll, 3);
        Place(body, _more, 4);
        Place(body, new ScrollViewer { Content = _rows }, 5);
        Place(body, _import, 6);
        Place(body, _family, 7);
        _body = body;

        var apply = new Button { Content = "Apply" };
        var rename = new Button { Content = "Rename…" };
        var delete = new Button { Content = "Delete" };

        apply.Click += (_, _) => ApplyTemplateEdit();
        rename.Click += async (_, _) => await RenameTemplateAsync().ConfigureAwait(true);
        delete.Click += (_, _) => DeleteTemplate();
        _templateList.ItemTemplate = new FuncDataTemplate<TemplateEntry>((entry, _) => new TextBlock
        {
            Text = entry is null ? null : entry.Error is null ? entry.Name : $"{entry.Name} (does not parse)",
            [ToolTip.TipProperty] = entry?.Error
        });
        _templateList.SelectionChanged += (_, _) =>
        {
            if (!_listing)
            {
                LoadTemplate();
            }
        };
        _templateText.TextChanged += (_, _) => CheckTemplate();

        var preview = new DockPanel();
        DockPanel.SetDock(_templatePreview, Dock.Left);
        preview.Children.Add(_templatePreview);
        preview.Children.Add(_previewSaid);

        _templates.Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    _templateList,
                    _templateText,
                    _templateSaid,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { apply, rename, delete } },
                    preview
                }
            }
        };

        // Open, the section takes half and scrolls in it: sized to its content, it took the whole
        // default strip and left the search, and its own buttons, out of sight.
        var content = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(_templates, 1);
        _templates.Margin = new Thickness(8, 0, 8, 8);
        _templates.PropertyChanged += (_, changed) =>
        {
            if (changed.Property == Expander.IsExpandedProperty || changed.Property == IsVisibleProperty)
            {
                content.RowDefinitions[1].Height = _templates is { IsExpanded: true, IsVisible: true } ? GridLength.Star : GridLength.Auto;
            }
        };
        content.Children.Add(new Panel { Children = { _noKey, _body } });
        content.Children.Add(_templates);
        Content = content;

        _ = RefreshAsync();

        static void Place(Grid grid, Control control, int row)
        {
            Grid.SetRow(control, row);
            grid.Children.Add(control);
        }
    }

    /// <summary>Where the key comes from. Replaceable so a test answers without the machine's keychain.</summary>
    public Func<string?> StoredKey { get; set; } = () => Keychain.Current?.Get(StreamlineClient.KeyService, StreamlineClient.KeyAccount);

    /// <summary>How a key becomes a client. Replaceable so a test answers in place of the network.</summary>
    public Func<string, StreamlineClient> Connect { get; set; } = key => new StreamlineClient(key);

    /// <summary>How a whole-family import is confirmed. Replaceable for the reason the window's dialogs are.</summary>
    public Func<string, Task<bool>> ConfirmFamily { get; set; }

    /// <summary>The project's templates, each named, with one that does not parse marked.</summary>
    public ListBox TemplateList => _templateList;

    /// <summary>The selected template's XML, which Apply writes back.</summary>
    public TextBox TemplateText => _templateText;

    /// <summary>Why the text does not parse or could not be written, or null.</summary>
    public string? TemplateSaid => _templateSaid.IsVisible ? _templateSaid.Text : null;

    /// <summary>The first picked icon as the template in the text box would import it, or null where it would not.</summary>
    public string? TemplatePreviewText { get; private set; }

    /// <summary>The group picked in the tree; imports go there, or into the project where none is.</summary>
    public ProjectGroup? Target
    {
        get => _selected ?? _window.Workspace?.Document.Root;
        set
        {
            // The rows are suggested against the target and the project's templates, so either changing asks again.
            var changed = !ReferenceEquals(_selected, value) || !ReferenceEquals(_suggestedIn, _window.Workspace);

            _selected = value;

            // Closing the project, which is also the first step of opening another, ends a family
            // import going into it, an update of one of its drawings, and what was typed into its templates.
            if (_window.Workspace is null)
            {
                _running?.Cancel();

                if (_updating is { })
                {
                    Clear();
                }
            }

            ShowWhere();
            ShowTemplates();

            if (_window.Workspace is null)
            {
                _drafts.Clear();
            }

            if (changed && _picked.Count > 0)
            {
                _ = RowsAsync();
            }
        }
    }

    public IReadOnlyList<StreamlineIcon> Results => _results;

    public IReadOnlyList<StreamlineIcon> Picked => _picked;

    public IReadOnlyList<StreamlineRow> Rows => _shown;

    /// <summary>Whether there is a key to search with; without one the panel is a line pointing at Settings.</summary>
    public bool HasKey => _client is { };

    /// <summary>What the panel is saying about the last thing that went wrong, or null.</summary>
    public string? Said => _said.IsVisible ? _said.Text : null;

    /// <summary>What a drawing imported from <paramref name="icon"/> records as its source.</summary>
    public static string SourceOf(StreamlineIcon icon) => SourcePrefix + icon.Hash;

    /// <summary>Reads the key again, and says what the panel can do without one.</summary>
    public async Task RefreshAsync()
    {
        var refresh = ++_refreshes;
        string? key;
        string said;

        try
        {
            // Off the UI thread: the keychain is another process, which a locked one keeps waiting on its prompt.
            key = await Task.Run(StoredKey).ConfigureAwait(true);
            said = "Searching Streamline needs an API key.";
        }
        catch (Exception failure) when (failure is InvalidOperationException or Win32Exception)
        {
            key = null;
            said = failure.Message;
        }

        if (refresh != _refreshes)
        {
            return;
        }

        _keyless.Text = said;

        if (key != _key)
        {
            _key = key;
            _client = key is { Length: > 0 } ? Connect(key) : null;
        }

        _noKey.IsVisible = _client is null;
        _body.IsVisible = _client is { };

        ShowWhere();
    }

    /// <summary>Searches for what is typed, or fetches the next page of the last search.</summary>
    public async Task SearchAsync(bool more = false)
    {
        _typing.Stop();

        if (_client is not { } client)
        {
            return;
        }

        var search = ++_searches;

        // The next page of what is showing, not of what has been typed since and not yet searched.
        var (query, style) = more
            ? _searched
            : (_query.Text?.Trim() ?? string.Empty, _style.SelectedItem as string is { } chosen && chosen != AnyStyle ? chosen : null);

        _more.IsEnabled = false;

        try
        {
            var page = await client.Search(query, offset: more ? _next : 0, limit: PageSize, style: style).ConfigureAwait(true);

            if (search != _searches)
            {
                return;
            }

            if (!more)
            {
                _results.Clear();
                _anchor = null;
                _searched = (query, style);
                Forget(page.Items);
            }

            foreach (var icon in page.Items)
            {
                _searchedStyle[icon.Hash] = style;
            }

            _results.AddRange(page.Items);
            _next = page.NextOffset;
            _more.IsVisible = page.HasMore;

            Say(_results.Count == 0 ? $"Nothing on Streamline matches '{query}'." : null);
            ShowTiles();
        }
        catch (Exception failure) when (Failure(failure) is { } said)
        {
            if (search == _searches)
            {
                Say(said);
            }
        }
        finally
        {
            _more.IsEnabled = true;
        }
    }

    /// <summary>Picks the result at <paramref name="index"/> as a click with <paramref name="modifiers"/> would, and downloads what is picked.</summary>
    public Task PickAsync(int index, KeyModifiers modifiers)
    {
        var icon = _results[index];
        var adding = (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;

        _updating = null;

        if ((modifiers & KeyModifiers.Shift) != 0 && _anchor is { } anchor && anchor < _results.Count)
        {
            if (!adding)
            {
                _picked.Clear();
            }

            foreach (var one in _results.Skip(Math.Min(anchor, index)).Take(Math.Abs(index - anchor) + 1))
            {
                if (!IsPicked(one))
                {
                    _picked.Add(one);
                }
            }
        }
        else if (adding)
        {
            if (_picked.RemoveAll(one => one.Hash == icon.Hash) == 0)
            {
                _picked.Add(icon);
            }

            _anchor = index;
        }
        else
        {
            _picked.Clear();
            _picked.Add(icon);
            _anchor = index;
        }

        ShowTiles();

        return RowsAsync();
    }

    // By hash: a search answers with new records, and their colour lists make them unequal to the same icon's last ones.
    private bool IsPicked(StreamlineIcon icon) => _picked.Exists(one => one.Hash == icon.Hash);

    /// <summary>Imports what the rows hold, as one step, and clears them.</summary>
    /// <remarks>With no project open each icon opens in a tab of its own, as delivered.</remarks>
    public async Task<IReadOnlyList<ProjectDrawing>> ImportAsync()
    {
        if (_shown.Count == 0)
        {
            return Array.Empty<ProjectDrawing>();
        }

        if (_updating is { } updating)
        {
            var row = _shown[0];
            var update = new TemplateImport(updating.Name, row.Downloads[0].Prepared.Text, row.Recipe(0), updating.Source);

            Clear();

            return await _window.UpdateAsync(updating, update).ConfigureAwait(true) ? new[] { updating } : Array.Empty<ProjectDrawing>();
        }

        var target = _window.Workspace is { } ? Target : null;
        var downloads = _shown.SelectMany(row => row.Downloads).ToList();
        var imports = _shown
            .SelectMany(row => row.Downloads.Select((download, index) =>
                new TemplateImport(download.Icon.Name, download.Prepared.Text, row.Recipe(index), SourceOf(download.Icon))))
            .ToList();

        // Before the first await, so a second click while the import is showing its result finds nothing to import.
        Clear();

        if (target is null)
        {
            foreach (var download in downloads)
            {
                await _window.OpenTextAsync(download.Svg, download.Icon.Name).ConfigureAwait(true);
            }

            return Array.Empty<ProjectDrawing>();
        }

        return await _window.ImportAsync(target, target.Children.Count, imports).ConfigureAwait(true);
    }

    /// <summary>Downloads the icon <paramref name="drawing"/> came from again, as a row whose import replaces what the drawing draws.</summary>
    public async Task UpdateAsync(ProjectDrawing drawing)
    {
        if (drawing?.Source is not { } source || !source.StartsWith(SourcePrefix, StringComparison.Ordinal))
        {
            return;
        }

        if (_client is not { } client)
        {
            await _window.Announce("Update from Streamline", "Updating an icon needs a Streamline API key, which is set in Settings.").ConfigureAwait(true);

            return;
        }

        StreamlineIcon icon;

        try
        {
            icon = await client.Icon(source.Substring(SourcePrefix.Length)).ConfigureAwait(true);
        }
        catch (Exception failure) when (Failure(failure) is { } said)
        {
            Say($"{ProjectWorkspace.Label(drawing)}: {said}");

            return;
        }

        _picked.Clear();
        _picked.Add(icon);
        _anchor = null;
        _updating = drawing;

        // Again, as asked, rather than the copy an earlier pick left in the session.
        _downloads.Remove(icon.Hash);

        ShowTiles();

        await RowsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Imports every icon of <paramref name="icon"/>'s family into a group named after it, skipping
    /// what that group already has, a page at a time, until the family ends, Cancel, or a spent limit.
    /// </summary>
    public async Task ImportFamilyAsync(StreamlineIcon icon)
    {
        if (_running is { })
        {
            Say("A family is already being imported; cancel it to start another.");

            return;
        }

        if (_client is not { } client || _window.Workspace is not { } workspace || Target is not { } target
            || icon.FamilySlug is not { } slug)
        {
            Say(_window.Workspace is null ? "A family is imported into a project, and none is open." : null);

            return;
        }

        using var running = new CancellationTokenSource();

        _running = running;

        // Shown from the start: finding the family walks the catalogue, which can take a while and counts against the limit.
        _family.IsVisible = true;
        _familyBar.IsIndeterminate = true;
        _familyProgress.Text = $"Finding {icon.FamilyName ?? slug} in Streamline's catalogue…";
        Say(null);

        try
        {
            if (!_families.TryGetValue(slug, out var family))
            {
                _families[slug] = family = await client.Family(slug, running.Token).ConfigureAwait(true);
            }

            if (family is null)
            {
                Say($"Streamline's catalogue lists no family called {icon.FamilyName ?? slug}.");

                return;
            }

            if (!await ConfirmFamily(
                    $"Import the {family.IconCount} icons of {family.Name} into a group called {family.Name} under "
                    + $"{ProjectWorkspace.Label(target)}? Icons already in that group are skipped, and each of the rest is one download.")
                .ConfigureAwait(true))
            {
                return;
            }

            await LandFamilyAsync(client, workspace, target, family, running.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested)
        {
            Say(Gone(workspace, target) ?? "Stopped by hand.");
        }
        catch (Exception failure) when (Failure(failure) is { } said)
        {
            Say(said);
        }
        finally
        {
            _running = null;
            _family.IsVisible = false;
        }
    }

    // Each page is its own undo step, the first together with the group it makes: a family runs to
    // thousands of downloads, and a Cancel or a spent limit should keep every page already landed.
    private async Task LandFamilyAsync(
        StreamlineClient client,
        ProjectWorkspace workspace,
        ProjectGroup target,
        StreamlineFamily family,
        CancellationToken cancellation)
    {
        var group = target.Children.OfType<ProjectGroup>().FirstOrDefault(child => child.Name == family.Name);
        var size = Size();
        var (done, skipped) = (0, 0);
        var notes = new List<string>();

        _familyBar.IsIndeterminate = false;
        _familyBar.Maximum = Math.Max(1, family.IconCount);

        for (var offset = 0; ;)
        {
            var page = await client.FamilyIcons(family.Hash, offset, PageSize, cancellation).ConfigureAwait(true);
            var present = group is { } ? TemplateLibrary.Imported(group, page.Items.Select(SourceOf)) : new HashSet<string>();
            var fetched = new List<StreamlineDownload>();
            string? stopped = null;

            skipped += present.Count;

            foreach (var one in page.Items.Where(one => !present.Contains(SourceOf(one))))
            {
                Progress(family, done + fetched.Count, skipped);

                if ((stopped = Gone(workspace, group ?? target)) is { })
                {
                    break;
                }

                try
                {
                    var svg = await client.DownloadSvg(one.Hash, size, cancellation: cancellation).ConfigureAwait(true);

                    fetched.Add(new StreamlineDownload(one, svg, TemplateLibrary.Prepare(svg, one.FamilySlug ?? family.Slug, null, one.Name, one.Colors)));
                }
                catch (SvgRecipeException unreadable)
                {
                    notes.Add($"{one.Name}: {unreadable.Message}");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    stopped = Gone(workspace, group ?? target) ?? $"Stopped by hand after {done + fetched.Count} of {family.Name}.";
                }
                catch (Exception failure) when (Failure(failure) is { } said)
                {
                    // A spent limit above all, but any failure stops here so the page fetched so far still lands.
                    stopped = said;
                }

                if (stopped is { })
                {
                    break;
                }
            }

            if (Gone(workspace, group ?? target) is { } gone)
            {
                Say(gone);

                return;
            }

            group = Land(workspace, target, group, family.Name, fetched, notes);
            done += fetched.Count;
            Progress(family, done, skipped);

            if (stopped is { } || !page.HasMore || page.Items.Count == 0)
            {
                Say(string.Join(Environment.NewLine, new[] { stopped }.Concat(notes).OfType<string>()) is { Length: > 0 } said ? said : null);

                return;
            }

            offset = page.NextOffset;
        }
    }

    /// <summary>Why a family import can no longer land in <paramref name="into"/>, or null while it can.</summary>
    /// <remarks>
    /// An undo of the first page takes the group it made out, and closing the project takes the lot.
    /// Asked of the XML: an undo restores its parent's children without clearing the group's Parent.
    /// </remarks>
    private string? Gone(ProjectWorkspace workspace, ProjectGroup into)
        => !ReferenceEquals(_window.Workspace, workspace) ? "Stopped: the project the family was going into was closed."
            : !into.Element.AncestorsAndSelf().Contains(workspace.Document.Root.Element) ? $"Stopped: {into.Name} is no longer in the project."
            : null;

    /// <summary>One page into the family's group, made in the same step where it is not there yet.</summary>
    private static ProjectGroup? Land(
        ProjectWorkspace workspace,
        ProjectGroup target,
        ProjectGroup? group,
        string name,
        IReadOnlyList<StreamlineDownload> fetched,
        ICollection<string> notes)
    {
        if (fetched.Count == 0)
        {
            return group;
        }

        var library = new TemplateLibrary(workspace.Document.Root);
        var imports = new List<TemplateImport>();

        // A group about to be made declares nothing, so what it would inherit is the target's.
        var into = group ?? target;

        foreach (var batch in TemplateLibrary.Batch(fetched.Select(download => download.Prepared)))
        {
            var suggested = batch.Select(icon => (Icon: icon, Suggestions: library.Suggest(icon, into))).ToList();
            var chosen = suggested[0].Suggestions[0].Template.Name;

            foreach (var (icon, suggestions) in suggested)
            {
                var download = fetched.First(one => ReferenceEquals(one.Prepared, icon));

                imports.Add(new TemplateImport(download.Icon.Name, icon.Text, StreamlineRow.Recipe(suggestions, chosen), SourceOf(download.Icon)));
            }
        }

        if (group is { })
        {
            TemplateLibrary.Import(workspace, group, group.Children.Count, imports, notes);

            return group;
        }

        ProjectGroup? made = null;

        workspace.Do(
            $"import {name}",
            () => ProjectSnapshot.Contents(target),
            () =>
            {
                made = target.AddGroup(name, target.Children.Count);

                TemplateLibrary.Import(workspace, made, 0, imports, notes);
            });

        return made;
    }

    private void Progress(StreamlineFamily family, int done, int skipped)
    {
        _familyBar.Value = done + skipped;
        _familyProgress.Text = $"{family.Name}: {done} imported, {skipped} already there, "
                               + $"{Math.Max(0, family.IconCount - done - skipped)} to go.";
    }

    /// <summary>Downloads what is picked, then shows one row per decision.</summary>
    private async Task RowsAsync()
    {
        var picks = ++_picks;
        var picked = _picked.ToList();

        _import.IsEnabled = false;

        var loading = picked.Select(Download).ToList();
        var downloads = await Task.WhenAll(loading).ConfigureAwait(true);
        var failed = new HashSet<string>(StringComparer.Ordinal);

        // Unpicked rather than left to be asked for again on every later click, each time counting
        // against the limit; and uncached, so picking it again by hand does ask again.
        for (var index = 0; index < picked.Count; index++)
        {
            if (downloads[index] is null && failed.Add(picked[index].Hash)
                && _downloads.TryGetValue(picked[index].Hash, out var cached) && cached == loading[index])
            {
                _downloads.Remove(picked[index].Hash);
            }
        }

        _picked.RemoveAll(icon => failed.Contains(icon.Hash));

        if (picks == _picks)
        {
            if (failed.Count == 0)
            {
                Say(null);
            }
            else
            {
                ShowTiles();
            }

            ShowRows(downloads.OfType<StreamlineDownload>().ToList());
        }
    }

    /// <summary>The icon's SVG and palette, downloaded once however often it is picked.</summary>
    private Task<StreamlineDownload?> Download(StreamlineIcon icon)
    {
        if (!_downloads.TryGetValue(icon.Hash, out var downloading))
        {
            _downloads[icon.Hash] = downloading = Downloading(icon);
        }

        return downloading;
    }

    private async Task<StreamlineDownload?> Downloading(StreamlineIcon icon)
    {
        try
        {
            var client = _client!;

            // The details first: they are not counted against the download limit, so a failure there spends nothing.
            var colors = icon.Colors ?? (await client.Icon(icon.Hash).ConfigureAwait(true)).Colors;
            var svg = await client.DownloadSvg(icon.Hash, Size()).ConfigureAwait(true);
            var style = _searchedStyle.TryGetValue(icon.Hash, out var searched) ? searched : null;

            return new StreamlineDownload(icon, svg, TemplateLibrary.Prepare(svg, icon.FamilySlug, style, icon.Name, colors));
        }
        catch (Exception failure) when (Failure(failure) is { } said)
        {
            Say($"{icon.Name}: {said}");

            return null;
        }
    }

    /// <summary>The size the project's first sized template asks for, which is what an icon is mapped onto anyway.</summary>
    private int Size()
        => _window.Workspace is { } workspace
           && new TemplateLibrary(workspace.Document.Root).Templates.Select(template => template.Recipe?.Size).FirstOrDefault(size => size is { }) is { } sized
            ? (int)Math.Round(sized)
            : DefaultSize;

    private void ShowRows(IReadOnlyList<StreamlineDownload> downloads)
    {
        foreach (var row in _shown)
        {
            row.Dispose();
        }

        _shown.Clear();
        _rows.Children.Clear();

        var workspace = _suggestedIn = _window.Workspace;
        var library = workspace is { } ? new TemplateLibrary(workspace.Document.Root) : null;
        var target = workspace is { } ? _updating?.Parent ?? Target : null;

        foreach (var batch in TemplateLibrary.Batch(downloads.Select(download => download.Prepared)))
        {
            var row = new StreamlineRow(batch.Select(icon => downloads.First(one => ReferenceEquals(one.Prepared, icon))).ToList(), library, target);

            row.Chosen += (_, _) => Offer(row);
            row.Spread.Click += (_, _) => Spread(row);
            row.SaveAs.Click += async (_, _) => await SaveTemplateAsync(row).ConfigureAwait(true);

            _shown.Add(row);
            _rows.Children.Add(row.View);
        }

        var count = downloads.Count;

        _import.IsEnabled = count > 0;
        _import.Content = _updating is { } updating && count > 0 ? $"Update {ProjectWorkspace.Label(updating)}"
            : count > 0 ? $"Import {count} icon{(count == 1 ? "" : "s")}"
            : "Import";

        CheckTemplate();
    }

    /// <summary>After a row's template is changed by hand, offers it to the rest of the family.</summary>
    private void Offer(StreamlineRow row)
    {
        if (_spreading || row.Template.SelectedItem is not string chosen)
        {
            return;
        }

        var alike = Alike(row, chosen);

        row.Spread.Content = $"Use {chosen} for the {alike.Count} other{(alike.Count == 1 ? "" : "s")} of this family";
        row.Spread.IsVisible = alike.Count > 0;
    }

    private void Spread(StreamlineRow row)
    {
        if (row.Template.SelectedItem is not string chosen)
        {
            return;
        }

        _spreading = true;

        try
        {
            foreach (var other in Alike(row, chosen))
            {
                other.Template.SelectedItem = chosen;
            }
        }
        finally
        {
            _spreading = false;
        }

        row.Spread.IsVisible = false;
    }

    private List<StreamlineRow> Alike(StreamlineRow row, string chosen)
        => _shown.Where(other => other != row && other.Family == row.Family && other.Offers(chosen) && !Equals(other.Template.SelectedItem, chosen)).ToList();

    private void Clear()
    {
        _picked.Clear();
        _anchor = null;
        _picks++;
        _updating = null;

        ShowRows(Array.Empty<StreamlineDownload>());
        ShowTiles();
    }

    private void ShowWhere()
        => _where.Text = _window.Workspace is null
            ? "No project is open, so each icon opens in a tab of its own."
            : $"Icons go into {ProjectWorkspace.Label(Target!)}.";

    private void Say(string? said)
    {
        _said.Text = said;
        _said.IsVisible = said is { };
    }

    /// <summary>Lists the project's templates again, keeping the one selected, or selecting <paramref name="select"/>.</summary>
    public void ShowTemplates(string? select = null)
    {
        var root = _window.Workspace?.Document.Root;
        var at = _templateList.SelectedIndex;
        var wanted = select ?? (_templateList.SelectedItem as TemplateEntry)?.Name;

        // Keep colours is the library's own and last, and not the project's to edit.
        var entries = root is { } ? new TemplateLibrary(root).Templates.Take(root.Templates.Count).ToList() : new List<TemplateEntry>();
        var found = entries.FindIndex(entry => entry.Name == wanted);

        _templates.IsVisible = root is { };
        _listing = true;

        try
        {
            _templateList.ItemsSource = entries;
            _templateList.SelectedIndex = found >= 0 ? found : Math.Min(Math.Max(at, 0), entries.Count - 1);
        }
        finally
        {
            _listing = false;
        }

        if (select is { })
        {
            _templates.IsExpanded = true;
        }

        LoadTemplate();
    }

    /// <summary>
    /// Puts the selected template's text in the box, unless what is there is an edit of the text the
    /// project still holds; an edit of another is kept until that one is selected again.
    /// </summary>
    private void LoadTemplate()
    {
        var root = _window.Workspace?.Document.Root;
        var index = _templateList.SelectedIndex;
        var stored = root is { } && index >= 0 && index < root.Templates.Count ? root.Templates[index] : ((string Name, string Text)?)null;

        _templateText.IsEnabled = stored is { };

        if (stored != _templateLoaded)
        {
            if (_templateLoaded is { } left && left.Name != stored?.Name && _templateText.Text is { } typed && typed != left.Text)
            {
                _drafts[left.Name] = typed;
            }

            _templateLoaded = stored;
            _templateText.Text = stored is { } now && _drafts.Remove(now.Name, out var draft) ? draft : stored?.Text;
        }

        CheckTemplate();
    }

    /// <summary>Says why the text does not parse, and previews it on the first icon picked.</summary>
    private void CheckTemplate()
    {
        SvgRecipe? recipe = null;
        string? said = null;

        if (_templateText.Text is { Length: > 0 } text)
        {
            try
            {
                recipe = SvgRecipe.Parse(text);
            }
            catch (SvgRecipeException failure)
            {
                said = failure.Message;
            }
        }

        SayTemplate(said);
        PreviewTemplate(recipe);
    }

    private void PreviewTemplate(SvgRecipe? recipe)
    {
        _templatePreview.Svg = null;
        _previewDocument?.Dispose();
        _previewDocument = null;
        TemplatePreviewText = null;

        var target = _window.Workspace is { } ? _updating?.Parent ?? Target : null;

        if (recipe is null || _shown.FirstOrDefault()?.Downloads[0].Prepared is not { } icon)
        {
            _previewSaid.Text = recipe is null ? null : "Pick an icon above to see the template on it.";

            return;
        }

        var binding = recipe.Bind(icon.Survey, icon.Palette, target is { } ? ProjectDeclarations.Names(target) : null);

        if (binding.Recipe is not { } bound)
        {
            _previewSaid.Text = binding.Failure;

            return;
        }

        try
        {
            TemplatePreviewText = SvgRecipeRewriter.Apply(TemplateLibrary.Sized(icon.Text, bound), bound).Svg;
        }
        catch (SvgRecipeException failure)
        {
            _previewSaid.Text = failure.Message;

            return;
        }

        _previewSaid.Text = recipe.Matches(icon.Survey, icon.Family, icon.Style, icon.Name)
            ? $"{icon.Name}, as this template imports it."
            : $"{icon.Name}, which this template's <match> would not offer it for.";

        if ((_previewDocument = StreamlineRow.Drawn(TemplatePreviewText, target)) is { } document)
        {
            try
            {
                document.Svg.SetExpressionValues(GroupPanel.Seeded(document));
            }
            catch (ExprException)
            {
                // Drawn with whatever the drawing says by default, as a row's preview is.
            }

            _templatePreview.Svg = document.Svg;
        }
    }

    private void SayTemplate(string? said)
    {
        _templateSaid.Text = said;
        _templateSaid.IsVisible = said is { };
    }

    /// <summary>Writes the text in the box back as the selected template, as one step.</summary>
    public void ApplyTemplateEdit()
    {
        if (_window.Workspace is not { } workspace || _templateList.SelectedIndex is not (>= 0 and var index))
        {
            return;
        }

        var was = workspace.Document.Root.Templates[index].Name;

        if (TemplateLibrary.Put(workspace, index, _templateText.Text ?? string.Empty) is { } refusal)
        {
            SayTemplate(refusal);

            return;
        }

        // An edit that renames it was kept as a draft of the old name when the list moved on.
        _drafts.Remove(was);
        ShowTemplates();
    }

    /// <summary>Asks for a new name for the selected template, and gives it that name as one step.</summary>
    public async Task RenameTemplateAsync()
    {
        if (_window.Workspace is not { } workspace || _templateList.SelectedIndex is not (>= 0 and var index))
        {
            return;
        }

        var (was, text) = workspace.Document.Root.Templates[index];

        if (await _window.AskTemplateName(was, null).ConfigureAwait(true) is not { } answer || answer.Name == was)
        {
            return;
        }

        // What the project holds, not what is typed: a rename is not an Apply of a half-made edit.
        if (TemplateLibrary.Put(workspace, index, Renamed(text, answer.Name)!) is { } refusal)
        {
            SayTemplate(refusal);

            return;
        }

        ShowTemplates(answer.Name);

        // The half-made edit goes with it, renamed too, rather than staying a draft of a name that is gone.
        if (_drafts.Remove(was, out var draft))
        {
            _templateText.Text = Renamed(draft, answer.Name) ?? draft;
        }

        static string? Renamed(string text, string name)
        {
            if (SvgSourceDocument.Read(text, out _) is not { Document.Root: { } root } read)
            {
                return null;
            }

            root.SetAttributeValue("name", name);

            return read.ToText();
        }
    }

    /// <summary>Takes the selected template out of the project, as one step.</summary>
    public void DeleteTemplate()
    {
        if (_window.Workspace is not { } workspace || _templateList.SelectedIndex is not (>= 0 and var index))
        {
            return;
        }

        var name = workspace.Document.Root.Templates[index].Name;

        SayTemplate(TemplateLibrary.Put(workspace, index, null));
        ShowTemplates();
        _drafts.Remove(name);
    }

    /// <summary>Keeps what <paramref name="row"/> does to its first icon as a template of the project's, asking what to call it.</summary>
    public async Task SaveTemplateAsync(StreamlineRow row)
    {
        if (_window.Workspace is not { } workspace)
        {
            return;
        }

        var download = row.Downloads[0];
        var family = download.Icon.FamilyName ?? download.Prepared.Family;
        var chosen = row.Template.SelectedItem as string ?? TemplateLibrary.KeepColoursName;

        try
        {
            // Once before asking, so a row that turns nothing into an expression says so before anything is typed.
            TemplateLibrary.Save(chosen, download.Prepared, row.Recipe(0), null);
        }
        catch (SvgRecipeException failure)
        {
            Say(failure.Message);

            return;
        }

        if (await _window.AskTemplateName(family is { } ? $"{chosen} ({family})" : chosen, download.Prepared.Family is { } ? family : null)
                .ConfigureAwait(true) is not { } answer)
        {
            return;
        }

        var text = TemplateLibrary.Save(answer.Name, download.Prepared, row.Recipe(0), answer.OnlyFamily ? download.Prepared.Family : null);

        if (TemplateLibrary.Put(workspace, workspace.Document.Root.Templates.Count, text) is { } refusal)
        {
            Say(refusal);

            return;
        }

        Say(null);
        ShowTemplates(answer.Name);
    }

    /// <summary>What to tell somebody about a failure reaching Streamline, or null for one this does not expect.</summary>
    private static string? Failure(Exception failure) => failure switch
    {
        StreamlineException { Status: HttpStatusCode.Unauthorized } refused
            => $"Streamline did not take the key: {refused.Message}. It is set in Settings.",
        StreamlineException { Status: HttpStatusCode.Forbidden } forbidden
            => $"Streamline refused: {forbidden.Message}",
        StreamlineException { Status: HttpStatusCode.TooManyRequests } spent
            => spent.ResetsAt is { } resets
                ? $"{spent.Message}. Streamline's limit lifts {resets.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}."
                : $"{spent.Message}. Streamline did not say when its limit lifts.",
        StreamlineException other => $"Streamline answered {(int)other.Status}: {other.Message}",
        HttpRequestException unreachable => $"Streamline could not be reached: {unreachable.Message}",
        TaskCanceledException => "Streamline did not answer in time.",
        JsonException unread => $"Streamline answered in a shape Studio does not read: {unread.Message}",
        SvgRecipeException unreadable => unreadable.Message,
        _ => null
    };

    /// <summary>How many tiles fit across, from the width the tiles have.</summary>
    private int Columns() => Math.Max(1, (int)((_tileScroll.Bounds.Width - 4d) / (TileSize + TileGap)));

    /// <summary>The results as rows of tiles, which is what lets a stack panel virtualise a grid.</summary>
    private void ShowTiles()
    {
        _columns = Columns();

        _tiles.ItemsSource = _results
            .Select((icon, index) => (icon, index))
            .Chunk(_columns)
            .Select(chunk => new TileRow(chunk))
            .ToList();
    }

    private Control Tiles(TileRow row)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal };

        foreach (var (icon, index) in row.Icons)
        {
            line.Children.Add(Tile(icon, index));
        }

        return line;
    }

    private Control Tile(StreamlineIcon icon, int index)
    {
        var name = new TextBlock
        {
            Text = icon.Name,
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var image = new Image { Stretch = Stretch.Uniform };
        var face = new Panel { Children = { name, image } };

        if (!icon.IsFree && !icon.HasPremiumAccess)
        {
            face.Children.Add(new PathIcon
            {
                Data = s_lock,
                Width = 10,
                Height = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                [ToolTip.TipProperty] = "Needs a Streamline plan this key does not have"
            });
        }

        var tile = new Border
        {
            Width = TileSize,
            Height = TileSize,
            Margin = new Thickness(TileGap / 2d),
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(2),
            Background = Brushes.Transparent,
            Child = face,
            [ToolTip.TipProperty] = icon.Name
        };

        if (IsPicked(icon))
        {
            tile.Bind(Border.BorderBrushProperty, tile.GetResourceObservable("TabItemHeaderSelectedPipeFill"));
        }

        tile.PointerPressed += async (_, e) =>
        {
            if (!e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed)
            {
                return;
            }

            e.Handled = true;

            await PickAsync(index, e.KeyModifiers).ConfigureAwait(true);
        };

        if (icon.FamilySlug is { })
        {
            var family = new MenuItem { Header = $"Import family {icon.FamilyName ?? icon.FamilySlug}…" };

            family.Click += async (_, _) => await ImportFamilyAsync(icon).ConfigureAwait(true);

            tile.ContextMenu = new ContextMenu { Items = { family } };
        }

        if (icon.ImagePreviewUrl is { } url && _client is { } client)
        {
            _ = Thumbnail(client, url, image, name);
        }

        return tile;
    }

    /// <summary>Lets go of the previews of results a new search replaces, other than <paramref name="kept"/>'s.</summary>
    private void Forget(IEnumerable<StreamlineIcon> kept)
    {
        var keep = kept.Select(icon => icon.ImagePreviewUrl).OfType<string>().ToHashSet(StringComparer.Ordinal);

        foreach (var url in _thumbnails.Keys.Where(url => !keep.Contains(url)).ToList())
        {
            _thumbnails.Remove(url, out var loading);
            _ = Dispose(loading!);
        }

        static async Task Dispose(Task<Bitmap?> loading) => (await loading.ConfigureAwait(true))?.Dispose();
    }

    private async Task Thumbnail(StreamlineClient client, string url, Image image, TextBlock name)
    {
        if (!_thumbnails.TryGetValue(url, out var loading))
        {
            _thumbnails[url] = loading = Task.Run(async () =>
            {
                try
                {
                    using var stream = new MemoryStream(await client.Thumbnail(url).ConfigureAwait(false));

                    return new Bitmap(stream);
                }
                catch (Exception)
                {
                    // Anything, from the CDN or the decoder: a tile without its preview still says its name.
                    return (Bitmap?)null;
                }
            });
        }

        if (await loading.ConfigureAwait(true) is { } bitmap)
        {
            image.Source = bitmap;
            name.IsVisible = false;
        }
    }

    /// <summary>A line of tiles. A class rather than a record, so a line rebuilt to show a new pick is not equal to the old one and is drawn again.</summary>
    private sealed class TileRow((StreamlineIcon Icon, int Index)[] icons)
    {
        public IReadOnlyList<(StreamlineIcon Icon, int Index)> Icons { get; } = icons;
    }
}
