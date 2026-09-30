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
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions.Recipes;
using Svg.Skia;

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

    /// <summary>What a drag of tiles carries, which is how a drop target tells it from rows or files; the icons are <see cref="Dragged"/>.</summary>
    public static readonly DataFormat<string> DragFormat = DataFormat.CreateStringApplicationFormat("StreamlineIcons");

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

    private readonly ItemsControl _tiles;

    private readonly ScrollViewer _tileScroll;

    /// <summary>Whether the search showing has a page after the ones shown, and whether one is on its way.</summary>
    private bool _hasMore, _paging;

    private readonly Button _import = new()
    {
        Content = "Import…",
        IsEnabled = false,
        Classes = { "accent" },
        Margin = new Thickness(6, 0, 0, 0)
    };

    private readonly StackPanel _family = new() { Spacing = 4, IsVisible = false };

    private readonly TextBlock _familyProgress = new() { TextWrapping = TextWrapping.Wrap };

    private readonly ProgressBar _familyBar = new() { Minimum = 0 };

    private readonly Button _cancel = new() { Content = "Cancel" };

    private readonly DispatcherTimer _typing = new() { Interval = TimeSpan.FromMilliseconds(400) };

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

    /// <summary>Whether icons are on their way into the project through the import window, which the button, a drop and an update wait for.</summary>
    private bool _opening;

    private PointerPressedEventArgs? _pressed;
    private Point _pressedAt;
    private int _pressedIndex;

    /// <summary>The pick a press on a picked tile makes at its release, which a drag cancels so it carries every pick.</summary>
    private KeyModifiers? _deferred;

    private readonly Dictionary<string, Task<Bitmap?>> _thumbnails = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<StreamlineDownload?>> _downloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StreamlineFamily?> _families = new(StringComparer.Ordinal);
    private int _columns = 1;
    private ProjectGroup? _selected;
    private CancellationTokenSource? _running;

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
        _import.Click += async (_, _) => await OpenImportAsync().ConfigureAwait(true);
        _cancel.Click += (_, _) => _running?.Cancel();

        _tiles = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<TileRow>((row, _) => row is null ? new Panel() : Tiles(row), supportsRecycling: false)
        };

        // On the list rather than each tile, which a pick rebuilds under the pointer.
        _tiles.AddHandler(PointerPressedEvent, OnTilePressed, RoutingStrategies.Tunnel);
        _tiles.AddHandler(PointerMovedEvent, OnTileMoved, RoutingStrategies.Tunnel);
        _tiles.AddHandler(PointerReleasedEvent, OnTileReleased, RoutingStrategies.Tunnel);

        _tileScroll = new ScrollViewer { Content = _tiles, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _tileScroll.SizeChanged += (_, _) =>
        {
            if (Columns() != _columns)
            {
                ShowTiles();
            }
        };

        // On these three rather than ScrollChanged, which waits for the layout manager's own pass.
        _tileScroll.PropertyChanged += (_, changed) =>
        {
            if (changed.Property == ScrollViewer.ExtentProperty || changed.Property == ScrollViewer.OffsetProperty || changed.Property == ScrollViewer.ViewportProperty)
            {
                PageIfShort();
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
        DockPanel.SetDock(_import, Dock.Right);
        DockPanel.SetDock(_style, Dock.Right);
        search.Children.Add(_import);
        search.Children.Add(_style);
        search.Children.Add(_query);

        _family.Children.Add(_familyProgress);
        _family.Children.Add(_familyBar);
        _family.Children.Add(_cancel);

        var body = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(8),
            RowSpacing = 6,
            IsVisible = false
        };

        Place(body, search, 0);
        Place(body, _said, 1);
        Place(body, _tileScroll, 2);
        Place(body, _family, 3);
        _body = body;

        Content = new Panel { Children = { _noKey, _body } };

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

    /// <summary>How a drag of tiles starts. Replaceable, since a headless test has no platform drag to start.</summary>
    public Func<PointerPressedEventArgs, DataTransfer, Task> StartDrag { get; set; }
        = (pressed, data) => DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Copy);

    /// <summary>The icons a drag of tiles carries, and none while no drag is under way.</summary>
    public IReadOnlyList<StreamlineIcon> Dragged { get; set; } = Array.Empty<StreamlineIcon>();

    /// <summary>Whether <see cref="DropAsync"/> would take what is being dragged, for a drop target to say so.</summary>
    internal bool CanDrop => Dragged.Count > 0 && _client is { } && _window.Workspace is { } && _running is null && !_opening;

    /// <summary>The group picked in the tree; imports go there, or into the project where none is.</summary>
    public ProjectGroup? Target
    {
        get => _selected ?? _window.Workspace?.Document.Root;
        set
        {
            // Closing the project, which is also the first step of opening another, ends a family import going into it.
            if (_window.Workspace is null)
            {
                _running?.Cancel();
            }

            _selected = _window.Workspace is { } ? value : null;

            ShowImport();
        }
    }

    public IReadOnlyList<StreamlineIcon> Results => _results;

    public IReadOnlyList<StreamlineIcon> Picked => _picked;

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

        ShowImport();
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

        _paging = true;

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
            _hasMore = page.HasMore;

            Say(_results.Count == 0 ? $"Nothing on Streamline matches '{query}'." : null);
            ShowTiles();

            // Once the page is laid out: a page shorter than the strip changes none of the three
            // the scroll viewer reports, since content that does not fill it is given its size.
            Dispatcher.UIThread.Post(PageIfShort, DispatcherPriority.Background);
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
            _paging = false;
        }
    }

    /// <summary>The next page, as the last row comes into view and while a page leaves the strip unfilled.</summary>
    /// <remarks>
    /// A page is one request against the thousand an hour, and a search can run to two hundred of
    /// them, so they come as they are looked at rather than all at once. Not while the strip has no
    /// height: a panel folded away would otherwise page through the whole search unseen.
    /// </remarks>
    private void PageIfShort()
    {
        if (_hasMore && !_paging && _tileScroll.Viewport.Height > 0
            && _tileScroll.Offset.Y + _tileScroll.Viewport.Height >= _tileScroll.Extent.Height - (TileSize + TileGap))
        {
            _ = SearchAsync(more: true);
        }
    }

    /// <summary>Picks the result at <paramref name="index"/> as a click with <paramref name="modifiers"/> would.</summary>
    /// <remarks>Nothing is downloaded until the picks are imported, since a download counts against the limit.</remarks>
    public Task PickAsync(int index, KeyModifiers modifiers)
    {
        var icon = _results[index];
        var adding = (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;

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

        return Task.CompletedTask;
    }

    // By hash: a search answers with new records, and their colour lists make them unequal to the same icon's last ones.
    private bool IsPicked(StreamlineIcon icon) => _picked.Exists(one => one.Hash == icon.Hash);

    /// <summary>Downloads what is picked and opens the import window on it, then imports what it settles on at the target's end.</summary>
    /// <remarks>With no project open each icon opens in a tab of its own, as delivered, and no window is needed.</remarks>
    public async Task OpenImportAsync()
    {
        if (_opening || _running is { } || _picked.Count == 0 || _client is null)
        {
            return;
        }

        var workspace = _window.Workspace;
        var target = workspace is { } ? Target : null;
        var picked = _picked.ToList();

        _opening = true;
        ShowImport();

        try
        {
            var downloads = await DownloadAsync(picked).ConfigureAwait(true);
            var failed = picked.Where((_, index) => downloads[index] is null).Select(icon => icon.Hash).ToHashSet(StringComparer.Ordinal);
            var fetched = downloads.OfType<StreamlineDownload>().ToList();

            // Unpicked rather than left to be asked for again on every later click, each time counting against the limit.
            _picked.RemoveAll(icon => failed.Contains(icon.Hash));
            ShowTiles();

            if (fetched.Count == 0)
            {
                return;
            }

            if (workspace is null || target is null)
            {
                _picked.RemoveAll(icon => fetched.Exists(one => one.Icon.Hash == icon.Hash));
                ShowTiles();

                foreach (var download in fetched)
                {
                    await _window.OpenTextAsync(download.Svg, download.Icon.Name).ConfigureAwait(true);
                }

                return;
            }

            if (Gone(workspace, target) is { } gone)
            {
                Say(gone);

                return;
            }

            await ImportAsync(new StreamlineImport(workspace, target, fetched), target.Children.Count, null, show: true).ConfigureAwait(true);
        }
        finally
        {
            _opening = false;
            ShowImport();
        }
    }

    /// <summary>
    /// Shows <paramref name="import"/> in the import window and, where it is taken, brings its icons
    /// in at <paramref name="index"/>, as one step, or replaces the drawing it updates.
    /// </summary>
    private async Task ImportAsync(StreamlineImport import, int index, SkiaSharp.SKPoint? at, bool show)
    {
        var workspace = import.Workspace;

        if (!await _window.ShowImport(import).ConfigureAwait(true))
        {
            return;
        }

        // The window is modal, but a project closed or a group undone away under it would take the import into nothing.
        if (Gone(workspace, import.Target) is { } gone)
        {
            Say(gone);

            return;
        }

        foreach (var row in import.Rows)
        {
            row.Remember();
        }

        if (import.Updating is { } updating)
        {
            var row = import.Rows[0];

            await _window.UpdateAsync(updating, new TemplateImport(updating.Name, row.Downloads[0].Prepared.Text, row.Recipe(0), updating.Source)).ConfigureAwait(true);

            return;
        }

        var imported = import.Rows.SelectMany(row => row.Icons).Select(icon => icon.Hash).ToHashSet(StringComparer.Ordinal);

        // Before the import shows its result, so the button cannot bring them in a second time meanwhile.
        if (_picked.RemoveAll(icon => imported.Contains(icon.Hash)) > 0)
        {
            ShowTiles();
        }

        // Clamped, since an undo can have taken rows out of the group while the window was up.
        await _window.ImportAsync(import.Target, Math.Min(index, import.Target.Children.Count), Placed(import.Imports(), at).Imports, show).ConfigureAwait(true);
    }

    /// <summary>
    /// Imports what is being dragged into <paramref name="group"/> at <paramref name="index"/>: each
    /// batch whose template is sure straight away, and the rest through the import window, aimed there.
    /// </summary>
    /// <param name="at">Where on the group's board the first lands, the rest in a row after it, or null to join its spread.</param>
    /// <param name="show">Whether the last drawing imported opens, as it does from the tree; a board drop stays on the board.</param>
    public async Task DropAsync(ProjectGroup group, int index, SkiaSharp.SKPoint? at, bool show)
    {
        // Before the first await: the drag can end, and clear it, before the drop is done with it.
        var dragged = Dragged.ToList();

        if (!CanDrop || _window.Workspace is not { } workspace)
        {
            return;
        }

        _opening = true;
        ShowImport();

        try
        {
            var downloads = await DownloadAsync(dragged).ConfigureAwait(true);

            if (Gone(workspace, group) is { } gone)
            {
                Say(gone);

                return;
            }

            var fetched = downloads.OfType<StreamlineDownload>().ToList();
            var library = new TemplateLibrary(workspace.Document.Root);

            // What the suggested card's dot would show: the batch's first icon's leading suggestion.
            var leading = TemplateLibrary.Batch(fetched.Select(download => download.Prepared))
                .Where(batch => library.Suggest(batch.First(), group)[0].Sure)
                .SelectMany(batch => batch)
                .ToHashSet(ReferenceEqualityComparer.Instance);
            var sure = fetched.Where(download => leading.Contains(download.Prepared)).ToList();
            var amber = fetched.Where(download => !leading.Contains(download.Prepared)).ToList();
            IReadOnlyList<ProjectDrawing> added = Array.Empty<ProjectDrawing>();

            if (sure.Count > 0)
            {
                var (imports, next) = Placed(Imports(library, group, sure), at);

                added = await _window.ImportAsync(group, index, imports, show).ConfigureAwait(true);
                at = next;

                // So the Import button cannot bring them in a second time.
                if (_picked.RemoveAll(icon => sure.Exists(one => one.Icon.Hash == icon.Hash)) > 0)
                {
                    ShowTiles();
                }
            }

            if (amber.Count == 0 || Gone(workspace, group) is { })
            {
                return;
            }

            var import = new StreamlineImport(workspace, group, amber);
            var after = index + added.Count;

            // Posted, so the window comes up once the drop that asked for it is over.
            await Dispatcher.UIThread.InvokeAsync(() => ImportAsync(import, after, at, show)).ConfigureAwait(true);
        }
        finally
        {
            _opening = false;
            ShowImport();
        }
    }

    /// <summary><paramref name="imports"/> laid in a row from <paramref name="at"/>, and the point after the last; as they are where there is no point.</summary>
    private static (List<TemplateImport> Imports, SkiaSharp.SKPoint? Next) Placed(IReadOnlyList<TemplateImport> imports, SkiaSharp.SKPoint? at)
    {
        if (at is not { } next)
        {
            return (imports.ToList(), null);
        }

        var placed = new List<TemplateImport>();

        foreach (var import in imports)
        {
            using var drawn = new SKSvg();

            var width = import.Recipe?.Size ?? drawn.FromSvg(import.Text)?.CullRect.Width ?? 0f;

            placed.Add(import with { At = next });

            // Spaced as a spread spaces a board: a tenth of the drawing.
            next = new SkiaSharp.SKPoint(next.X + width + (Math.Max(width * 0.05f, 1f) * 2f), next.Y);
        }

        return (placed, next);
    }

    /// <summary>Downloads the icon <paramref name="drawing"/> came from again, and opens the import window to replace what the drawing draws with it.</summary>
    /// <remarks>The picks are left as they are: this is about the drawing, not what was being gathered.</remarks>
    public async Task UpdateAsync(ProjectDrawing drawing)
    {
        if (drawing?.Source is not { } source || !source.StartsWith(SourcePrefix, StringComparison.Ordinal)
            || _window.Workspace is not { } workspace || drawing.Parent is not { } parent)
        {
            return;
        }

        if (_client is not { } client)
        {
            await _window.Announce("Update from Streamline", "Updating an icon needs a Streamline API key, which is set in Settings.").ConfigureAwait(true);

            return;
        }

        if (_opening)
        {
            Say("Other icons are on their way in; update this one once they are.");

            return;
        }

        _opening = true;
        ShowImport();

        try
        {
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

            // Again, as asked, rather than the copy an earlier pick left in the session.
            _downloads.Remove(icon.Hash);

            if ((await DownloadAsync(new[] { icon }).ConfigureAwait(true))[0] is not { } download)
            {
                return;
            }

            if (Gone(workspace, parent) is { } gone)
            {
                Say(gone);

                return;
            }

            await ImportAsync(new StreamlineImport(workspace, parent, new[] { download }, drawing), 0, null, show: true).ConfigureAwait(true);
        }
        finally
        {
            _opening = false;
            ShowImport();
        }
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
        ShowImport();

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
            ShowImport();
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

    /// <summary>Why icons can no longer land in <paramref name="into"/>, or null while they can.</summary>
    /// <remarks>
    /// An undo of the first page takes the group it made out, and closing the project takes the lot.
    /// Asked of the XML: an undo restores its parent's children without clearing the group's Parent.
    /// </remarks>
    private string? Gone(ProjectWorkspace workspace, ProjectGroup into)
        => !ReferenceEquals(_window.Workspace, workspace) ? "Stopped: the project the icons were going into was closed."
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

        // A group about to be made declares nothing, so what it would inherit is the target's.
        var imports = Imports(new TemplateLibrary(workspace.Document.Root), group ?? target, fetched);

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

    /// <summary>The downloads as imports into <paramref name="into"/>, each batch through what its first icon is suggested, as a row would take it.</summary>
    private static List<TemplateImport> Imports(TemplateLibrary library, ProjectGroup into, IReadOnlyList<StreamlineDownload> downloads)
    {
        var imports = new List<TemplateImport>();

        foreach (var batch in TemplateLibrary.Batch(downloads.Select(download => download.Prepared)))
        {
            var suggested = batch.Select(icon => (Icon: icon, Suggestions: library.Suggest(icon, into))).ToList();
            var chosen = suggested[0].Suggestions[0].Template.Name;

            foreach (var (icon, suggestions) in suggested)
            {
                var download = downloads.First(one => ReferenceEquals(one.Prepared, icon));

                imports.Add(new TemplateImport(download.Icon.Name, icon.Text, StreamlineRow.Recipe(suggestions, chosen), SourceOf(download.Icon)));
            }
        }

        return imports;
    }

    private void Progress(StreamlineFamily family, int done, int skipped)
    {
        _familyBar.Value = done + skipped;
        _familyProgress.Text = $"{family.Name}: {done} imported, {skipped} already there, "
                               + $"{Math.Max(0, family.IconCount - done - skipped)} to go.";
    }

    /// <summary>Each icon's download, or null for one that failed and is forgotten, so asking for it again by hand does ask again.</summary>
    /// <remarks>Said while it runs, and each failure after it.</remarks>
    private async Task<StreamlineDownload?[]> DownloadAsync(IReadOnlyList<StreamlineIcon> icons)
    {
        Say(icons.Count == 1 ? $"Downloading {icons[0].Name}…" : $"Downloading {icons.Count} icons…");

        var loading = icons.Select(Download).ToList();
        var downloads = await Task.WhenAll(loading).ConfigureAwait(true);

        for (var index = 0; index < icons.Count; index++)
        {
            if (downloads[index] is null && _downloads.TryGetValue(icons[index].Hash, out var cached) && cached == loading[index])
            {
                _downloads.Remove(icons[index].Hash);
            }
        }

        if (downloads.All(download => download is { }))
        {
            Say(null);
        }

        return downloads;
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

    /// <summary>What the Import button says and where it takes the picks, and whether it can now.</summary>
    private void ShowImport()
    {
        _import.Content = _picked.Count > 0 ? $"Import {_picked.Count}…" : "Import…";
        _import.IsEnabled = _picked.Count > 0 && !_opening && _running is null;
        ToolTip.SetTip(_import, _window.Workspace is null
            ? "No project is open, so each icon opens in a tab of its own."
            : $"Icons go into {ProjectWorkspace.Label(Target!)}.");
    }

    private void Say(string? said)
    {
        _said.Text = said;
        _said.IsVisible = said is { };
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

        ShowImport();
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
            Tag = index,
            [ToolTip.TipProperty] = icon.Name
        };

        if (IsPicked(icon))
        {
            tile.Bind(Border.BorderBrushProperty, tile.GetResourceObservable("TabItemHeaderSelectedPipeFill"));
        }

        if (icon.FamilySlug is { })
        {
            var family = new MenuItem { Header = $"Import family {icon.FamilyName ?? icon.FamilySlug}…" };

            family.Click += async (_, _) => await ImportFamilyAsync(icon).ConfigureAwait(true);

            tile.ContextMenu = new ContextMenu { Items = { family } };
        }

        if (_client is { } client)
        {
            _ = Thumbnail(client, icon, image, name);
        }

        return tile;
    }

    /// <summary>
    /// Picks the tile pressed, unless it is picked already and the press is plain or adding: then
    /// the pick waits for the release, so dragging it carries every pick. A double click imports it alone.
    /// </summary>
    private async void OnTilePressed(object? sender, PointerPressedEventArgs e)
    {
        _pressed = null;
        _deferred = null;

        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(one => one.Tag is int)?.Tag is not int index
            || index >= _results.Count
            || !e.GetCurrentPoint(_tiles).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;

        // Counted by the pointer from time and place, so it survives the first press rebuilding the tile, which DoubleTapped does not.
        if (e.ClickCount == 2)
        {
            await PickAsync(index, KeyModifiers.None).ConfigureAwait(true);
            Dispatcher.UIThread.Post(async () => await OpenImportAsync().ConfigureAwait(true));

            return;
        }

        _pressed = e;
        _pressedAt = e.GetPosition(_tiles);
        _pressedIndex = index;

        if (IsPicked(_results[index]) && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            _deferred = e.KeyModifiers;

            return;
        }

        await PickAsync(index, e.KeyModifiers).ConfigureAwait(true);
    }

    private async void OnTileMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } pressed)
        {
            return;
        }

        if (!e.GetCurrentPoint(_tiles).Properties.IsLeftButtonPressed || _pressedIndex >= _results.Count)
        {
            _pressed = null;
            _deferred = null;

            return;
        }

        var travelled = e.GetPosition(_tiles) - _pressedAt;

        if (Math.Abs(travelled.X) < MainWindow.DragThreshold && Math.Abs(travelled.Y) < MainWindow.DragThreshold)
        {
            return;
        }

        var icon = _results[_pressedIndex];
        var data = new DataTransfer();

        data.Add(DataTransferItem.Create(DragFormat, string.Empty));

        _pressed = null;
        _deferred = null;
        Dragged = IsPicked(icon) ? _picked.ToList() : new[] { icon };

        // The tile a pick rebuilt is out of the window, and the platform finds the window it drags from through the source.
        pressed.Source = _tiles;

        try
        {
            await StartDrag(pressed, data).ConfigureAwait(true);
        }
        finally
        {
            Dragged = Array.Empty<StreamlineIcon>();
        }
    }

    private async void OnTileReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressed = null;

        if (_deferred is not { } modifiers || _pressedIndex >= _results.Count)
        {
            return;
        }

        _deferred = null;

        await PickAsync(_pressedIndex, modifiers).ConfigureAwait(true);
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

    /// <remarks>
    /// The CDN serves only the previews it has already made, and makes new ones for the website
    /// alone, so one icon in twenty has none to give. The icon's details carry its SVG on a plan
    /// that may download it, and that is drawn instead: a request, where a download is metered.
    /// </remarks>
    private async Task Thumbnail(StreamlineClient client, StreamlineIcon icon, Image image, TextBlock name)
    {
        var key = icon.ImagePreviewUrl ?? icon.Hash;

        if (!_thumbnails.TryGetValue(key, out var loading))
        {
            _thumbnails[key] = loading = Task.Run(async () =>
            {
                try
                {
                    if (icon.ImagePreviewUrl is { } url)
                    {
                        using var stream = new MemoryStream(await client.Thumbnail(url).ConfigureAwait(false));

                        return new Bitmap(stream);
                    }
                }
                catch (Exception)
                {
                    // Anything, from the CDN or the decoder: the icon's own drawing is asked for instead.
                }

                try
                {
                    return (await client.Icon(icon.Hash).ConfigureAwait(false)).Svg is { } svg ? Drawn(svg) : null;
                }
                catch (Exception)
                {
                    // A tile without its preview still says its name.
                    return null;
                }
            });
        }

        if (await loading.ConfigureAwait(true) is { } bitmap)
        {
            image.Source = bitmap;
            name.IsVisible = false;
        }
    }

    /// <summary><paramref name="svg"/> drawn at the size of a tile, or null where it does not draw.</summary>
    private static Bitmap? Drawn(string svg)
    {
        using var drawing = new SKSvg();

        if (drawing.FromSvg(svg) is not { } picture || picture.CullRect.Width <= 0f || picture.CullRect.Height <= 0f)
        {
            return null;
        }

        var scale = (float)(TileSize * 2d) / Math.Max(picture.CullRect.Width, picture.CullRect.Height);

        using var stream = new MemoryStream();

        return drawing.Save(stream, SkiaSharp.SKColors.Transparent, scaleX: scale, scaleY: scale) && stream.Length > 0
            ? new Bitmap(new MemoryStream(stream.ToArray()))
            : null;
    }

    /// <summary>A line of tiles. A class rather than a record, so a line rebuilt to show a new pick is not equal to the old one and is drawn again.</summary>
    private sealed class TileRow((StreamlineIcon Icon, int Index)[] icons)
    {
        public IReadOnlyList<(StreamlineIcon Icon, int Index)> Icons { get; } = icons;
    }
}
