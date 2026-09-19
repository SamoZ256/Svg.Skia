using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions;
using Svg.Expressions.Recipes;
using Svg.PaintCode;
using Svg.Skia;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>
/// The shell: one tab per open drawing.
/// </summary>
/// <remarks>
/// The viewer holds one document, so the tabs are the shell's: it puts a viewer in each and handles
/// their <c>OpenRequested</c>. Reordering is the shell's too, since <see cref="TabControl"/> has
/// none — a tab is moved within <see cref="ItemsControl.Items"/>, which works because the items are
/// the containers, with no data behind them to keep in step.
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>How far the pointer travels before a press on a tab is a drag and not a click.</summary>
    private const double DragThreshold = 4d;

    /// <summary>How far one wheel notch scrolls the strip.</summary>
    private const double WheelStep = 50d;

    private readonly TabControl _tabs;

    private readonly TreeView _projectTree;
    private readonly ColumnDefinition _projectColumn;
    private readonly Border _projectPaneHost;
    private readonly GridSplitter _projectSplitter;
    private readonly TextBlock _projectName;
    private readonly TextBox _projectSearch;
    private readonly TextBlock _projectSearchCount;
    private readonly Border _dropLine;
    private readonly Grid _dropHost;

    /// <summary>The open project, or null. The window works on one at a time, as a workspace is.</summary>
    private ProjectWorkspace? _workspace;

    /// <summary>The copy kept of the open project while it has work that is not on disk.</summary>
    private ProjectRecovery? _recovery;

    /// <summary>The settings window while one is open, so a second asking brings that one forward.</summary>
    private SettingsWindow? _settings;

    /// <summary>Tabs holding a drawing the project has resized since it was last on screen.</summary>
    /// <remarks>
    /// Rebuilt when the tab is next looked at rather than the moment the project changes. A tab
    /// that is not selected holds no content in the tree — <see cref="TabControl"/> presents one at
    /// a time — and a drawing rebuilt into a detached viewer came back blank until the tab was
    /// closed and opened again. Waiting is also less work: a project rarely resizes one drawing.
    /// </remarks>
    private readonly HashSet<TabItem> _stale = new();

    private TabItem? _pressed;
    private Point _pressedAt;
    private double _grabbedAt;
    private bool _dragging;

    /// <summary>Where the dragged tab is drawn relative to the slot it has been laid out in.</summary>
    private readonly TranslateTransform _carry = new();

    public MainWindow()
        : this(null)
    {
    }

    /// <param name="path">A drawing to open instead of the bundled sample.</param>
    public MainWindow(string? path)
    {
        AvaloniaXamlLoader.Load(this);

        ConfirmDiscard = AskDiscard;
        ConfirmDiscardRecovery = message => Ask("Recovered work", message, "Discard the copy", "Restore");
        ConfirmConvert = AskConvert;
        AskWhereToSave = AskSaveProject;
        ShowSettings = ShowSettingsWindow;
        ConfirmRemove = message => Ask("Remove from the project", message, "Remove", "Cancel");
        Announce = (title, message) => Ask(title, message, null, "Close");
        ShowOnDisk = Reveal;

        _tabs = this.FindControl<TabControl>("Tabs")!;
        _tabs.SelectionChanged += (_, _) =>
        {
            UpdateTitle();
            UpdateMenu();
            Refill();
            Reveal();
        };

        _projectTree = this.FindControl<TreeView>("ProjectTree")!;
        _projectColumn = this.FindControl<Grid>("Shell")!.ColumnDefinitions[0];
        _projectPaneHost = this.FindControl<Border>("ProjectPaneHost")!;
        _projectSplitter = this.FindControl<GridSplitter>("ProjectSplitter")!;
        _projectName = this.FindControl<TextBlock>("ProjectName")!;
        _projectSearch = this.FindControl<TextBox>("ProjectSearch")!;
        _projectSearchCount = this.FindControl<TextBlock>("ProjectSearchCount")!;
        _projectSearch.TextChanged += (_, _) => JumpToMatch(0);
        _projectSearch.KeyDown += OnProjectSearchKeyDown;
        _projectTree.KeyDown += OnProjectTreeKeyDown;
        _dropLine = this.FindControl<Border>("DropLine")!;
        _dropHost = (Grid)_dropLine.Parent!;

        // Tunnelling, because TreeViewItem takes a press itself to become selected.
        _projectTree.AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Tunnel);
        _projectTree.PointerMoved += OnRowMoved;
        _projectTree.AddHandler(DragDrop.DragOverEvent, OnRowDragOver);
        _projectTree.AddHandler(DragDrop.DropEvent, OnRowDrop);
        _projectTree.AddHandler(DragDrop.DragLeaveEvent, (_, _) => HideDrop());
        _tabs.TemplateApplied += OnTabsTemplateApplied;

        // On the strip, and tunnelling, because TabItem handles a press itself to become selected
        // and a bubbling handler would never see it.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        _tabs.AddHandler(PointerPressedEvent, OnTabPointerPressed, RoutingStrategies.Tunnel);
        _tabs.AddHandler(PointerMovedEvent, OnTabPointerMoved, RoutingStrategies.Tunnel);
        _tabs.AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Tunnel);
        _tabs.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(null));

        // On the window and not on the shell inside it: neither the shell nor the tab strip's
        // template paints where a tab's content would be, and a panel with no background is not hit
        // tested — so a drop on an empty window lands on the window's own chrome, above them both.
        AddHandler(DragDrop.DragOverEvent, OnFilesDragOver);
        AddHandler(DragDrop.DropEvent, OnFilesDropped);

        ShowMenuGestures();
        UpdateMenu();
        ShowRecent();

        // Once a session, and here rather than in a static constructor: this touches the disk, and
        // a static one runs on whichever thread happens to reach the type first.
        ProjectRecovery.Sweep();

        // Nothing open, and no tab standing in for nothing. A window used to start on a bundled
        // sample in a tab called Untitled, which meant a project opened from the command line came
        // up beside a tab holding neither the project nor anything else.
        if (path is { } startup && File.Exists(startup))
        {
            _ = OpenAsync(new[] { startup });
        }
    }

    /// <summary>Adds an empty tab, selects it, and returns the viewer that fills it.</summary>
    private SvgViewer AddTab()
    {
        var viewer = new SvgViewer { FileDialogService = new StudioFileDialogService() };

        // Both are dressed by the window's styles, which is also where the trimming that keeps one
        // long file name from filling the strip lives.
        var title = new TextBlock { Text = "Untitled", Classes = { "title" } };
        var marker = new TextBlock { Classes = { "marker" } };

        var close = new Button
        {
            Content = "✕",
            Classes = { "close" },
            [ToolTip.TipProperty] = "Close this drawing"
        };

        var item = new TabItem
        {
            Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { marker, title, close }
            },
            Content = viewer
        };

        close.Click += async (_, _) => await CloseTabAsync(item);

        var name = "drawing";

        viewer.DocumentOpened += (_, document) =>
        {
            name = document.Path is { } path ? Path.GetFileName(path) : "drawing";
            title.Text = name;
            item[ToolTip.TipProperty] = document.Path;

            // A reload keeps whatever the pane still holds — reopening a drawing at a new size does
            // not save it, so the mark has no business being cleared by one.
            Mark(item);

            UpdateTitle();

            // The document arrives after the tab does, and exporting needs one.
            UpdateMenu();
        };

        viewer.SourceModifiedChanged += (_, _) => Mark(item);

        viewer.OpenRequested += (_, request) =>
        {
            request.Handled = true;

            // Handed back rather than discarded, so whoever asked — the toolbar, a drop, a test —
            // waits for the drawings instead of for the request being taken.
            request.Completion = OpenAsync(viewer, request.Paths);
        };

        _tabs.Items.Add(item);
        _tabs.SelectedItem = item;


        return viewer;
    }

    /// <summary>Opens each path in a tab of its own.</summary>
    /// <remarks>
    /// The tab that asked is reused while it holds nothing, so opening from a freshly closed window —
    /// or dropping several files at once — does not leave an empty tab in front of the drawings.
    /// </remarks>
    /// <summary>Opens each path in a tab of its own, whether or not anything is open already.</summary>
    /// <remarks>
    /// Public for the reason <see cref="ShowAsync"/> is: the way in without a menu or a pointer.
    /// A window with nothing open has no viewer to ask through, so this is also how the window's own
    /// Open reaches a file now that it does not keep an empty tab standing.
    /// </remarks>
    public Task OpenAsync(IReadOnlyList<string> paths) => OpenAsync(null, paths);

    private async Task OpenAsync(SvgViewer? source, IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (IsProject(path))
            {
                await OpenProjectAsync(path).ConfigureAwait(true);
                continue;
            }

            // Opening one converts it, which is a different thing from opening a drawing and is
            // asked about rather than done. Where it goes is not asked here: a conversion is held in
            // the window until it is saved, and the save panel is what asks that.
            if (IsPaintCode(path))
            {
                if (await ConfirmConvert(path).ConfigureAwait(true) is { } asked)
                {
                    await ImportPaintCodeAsync(path, asked.Integers, asked.Organize).ConfigureAwait(true);
                }

                continue;
            }

            var viewer = source is { Document: null } ? source : AddTab();

            if (await viewer.LoadAsync(path).ConfigureAwait(true))
            {
                Remember(path);
            }
        }
    }

    /// <summary>
    /// Takes a file dropped anywhere on the window, wherever there is nothing else to take it.
    /// </summary>
    /// <remarks>
    /// A drawing's viewer answers a drop on itself and marks it taken, so this is what is left: the
    /// empty window, the tab strip, and the project pane. It used to be that a window always held a
    /// viewer, so the drop target was wherever the drawing was — and a window with nothing open had
    /// nowhere to drop at all.
    ///
    /// Only a drag carrying files. A row being dragged in the project tree passes through here too,
    /// and it neither carries files nor is finished with: touching its effects would show it as
    /// refused all the way across the tree.
    ///
    /// Drawings dropped on the tree never reach here — the tree marks those taken and adds them to
    /// the project. What is left is everything else, which is still opened.
    /// </remarks>
    private void OnFilesDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer?.TryGetFiles() is { Length: > 0 })
        {
            e.DragEffects &= DragDropEffects.Copy | DragDropEffects.Link;
        }
    }

    /// <inheritdoc cref="OnFilesDragOver"/>
    private async void OnFilesDropped(object? sender, DragEventArgs e)
    {
        if (Dropped(e) is { Count: > 0 } paths)
        {
            e.Handled = true;

            await OpenAsync(paths).ConfigureAwait(true);
        }
    }

    /// <summary>The local paths a drag is carrying, or none where it carries no files.</summary>
    private static List<string>? Dropped(DragEventArgs e)
        => e.DataTransfer?.TryGetFiles()
            ?.Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToList();

    /// <summary>Whether a path names a project rather than a drawing.</summary>
    private static bool IsProject(string path)
        => Path.GetExtension(path).Equals(".svgstudio", StringComparison.OrdinalIgnoreCase) || IsSvgc(path);

    /// <summary>Whether a path names an svgc project, which opens by being converted into one.</summary>
    private static bool IsSvgc(string path)
        => Path.GetExtension(path).Equals(".svgcproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a path names a PaintCode document, which an import reads.</summary>
    private static bool IsPaintCode(string path)
        => Path.GetExtension(path).Equals(".pcvd", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a path names a drawing — what the project is a list of.</summary>
    /// <remarks>The two extensions the picker offers when a drawing is being added to one.</remarks>
    private static bool IsDrawing(string path)
        => Path.GetExtension(path) is { } extension
            && (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".svgz", StringComparison.OrdinalIgnoreCase));

    // ---- the project pane ---------------------------------------------------------------------

    /// <summary>The open project, for a test to read. Null while none is open.</summary>
    public ProjectWorkspace? Workspace => _workspace;

    /// <summary>The copy being kept of the open project, for a test to drive. Null while none is.</summary>
    public ProjectRecovery? Recovery => _recovery;

    private async void OnNewProject(object? sender, EventArgs e) => await NewProjectAsync();

    /// <summary>
    /// Opens a project that is nothing yet.
    /// </summary>
    /// <remarks>
    /// Nothing is asked and nothing is written: naming a file is what saving is for, and a project
    /// named before there was anything in it left an empty file behind whenever somebody changed
    /// their mind. Public for the reason <see cref="ExportAsync"/> is: a way in without the menu.
    /// </remarks>
    public async Task NewProjectAsync()
        => await OpenProjectAsync(ProjectDocument.Empty(string.Empty), false, Array.Empty<string>(), null)
            .ConfigureAwait(true);

    /// <summary>
    /// Converts <paramref name="source"/> into a project, and opens it.
    /// </summary>
    /// <remarks>
    /// One project, with the drawings in it: what the document has to say is what a project is made
    /// of now, so nothing is written beside it and nothing is named — the conversion is held in the
    /// window until somebody has looked at it and been asked where it goes.
    /// </remarks>
    /// <param name="integers">
    /// Whether a whole-valued number variable becomes an <c>integer</c> parameter -- a guess the
    /// author makes, which is why the dialog asks rather than this deciding.
    /// </param>
    public async Task<bool> ImportPaintCodeAsync(string source, bool integers = false, bool organize = true)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var notes = new List<PaintCodeImportNote>();
        var directory = Path.GetDirectoryName(Path.GetFullPath(source)) ?? string.Empty;
        var options = new PaintCodeImportOptions(directory) { Integers = integers };

        ProjectDocument document;

        try
        {
            // Off the UI thread: the document this was written for is 16 MB and a thousand drawings.
            document = await Task.Run(
                () => ProjectImport.FromPaintCode(PaintCodeDocument.Load(source), options, notes, directory, organize))
                .ConfigureAwait(true);
        }
        catch (Exception failure) when (failure is PaintCodeException or SvgcProjectException or IOException or UnauthorizedAccessException)
        {
            await Announce("The document couldn't be imported", failure.Message).ConfigureAwait(true);

            return false;
        }

        // Opened on what was just built rather than written and read back: nothing goes to disk
        // until the author has seen the conversion and saved it, and a thousand drawings are not
        // serialised and parsed again to show them.
        await OpenProjectAsync(document, true, Array.Empty<string>(), Named(source)).ConfigureAwait(true);

        // Only when something could not be carried across. The project opening on the drawings is
        // the rest of the answer, and a dialog saying so would be one click for nothing.
        if (notes.Count > 0)
        {
            await Announce("Imported", Said(document, notes)).ConfigureAwait(true);
        }

        return true;
    }

    private static string Said(ProjectDocument document, IReadOnlyList<PaintCodeImportNote> notes)
    {
        var drawn = document.Root.Drawings.Count();
        var carried = $"{drawn} drawing{(drawn == 1 ? string.Empty : "s")}.";
        var missing = notes.Where(note => note.Severity is PaintCodeImportSeverity.Missing).ToList();
        var rest = notes.Where(note => note.Severity is not PaintCodeImportSeverity.Missing).ToList();
        var lines = new List<string> { $"{carried} {notes.Count} could not be carried across:" };

        // First, and never trimmed away: this is the document asking for a canvas it does not have,
        // which is the one thing in here to take back to PaintCode rather than to this converter.
        if (missing.Count > 0)
        {
            lines.Add($"{missing.Count} the document itself is missing:");
            lines.AddRange(missing.Select(note => "  " + note));
        }

        lines.AddRange(rest.Take(Listed).Select(note => note.ToString()));

        if (rest.Count > Listed)
        {
            lines.Add($"and {rest.Count - Listed} more.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>What to call a project converted from <paramref name="source"/>, when somebody is asked.</summary>
    /// <remarks>
    /// A suggestion and not a decision: where it goes is the save panel's question, and the panel is
    /// what asks about a name already taken.
    /// </remarks>
    private static string Named(string source) => Path.GetFileNameWithoutExtension(source) + ".svgstudio";

    /// <summary>
    /// Opens a project into the pane.
    /// </summary>
    /// <remarks>
    /// No tab of its own: a project is what the window is working on rather than one of the things
    /// it is showing, so it lives beside the tabs and only the nodes chosen out of it become tabs.
    /// One at a time, which is what makes it a workspace — a second replaces the first.
    /// </remarks>
    private async Task OpenProjectAsync(string path)
    {
        ProjectDocument document;
        var notes = new List<string>();

        try
        {
            document = IsSvgc(path) ? Converted(path, notes) : ProjectDocument.Load(path);
        }
        catch (Exception failure) when (failure is SvgcProjectException or SvgRecipeException or IOException or UnauthorizedAccessException)
        {
            // Before anything is closed: a file that cannot be read is no reason to take away the
            // project somebody is working on.
            await Announce("The project couldn't be opened", failure.Message).ConfigureAwait(true);
            return;
        }

        await OpenProjectAsync(document, IsSvgc(path), notes, IsSvgc(path) ? Named(path) : null)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Opens a project the window is holding, whether or not it is on disk.
    /// </summary>
    /// <remarks>
    /// The document rather than a path, because an import and a conversion build one that no file
    /// holds yet. It is also the one place a document becomes a workspace, which is what lets the
    /// recovery copy be asked about once rather than at every way in.
    /// </remarks>
    /// <param name="edited">Whether what is being opened is already unsaved work.</param>
    /// <param name="suggested">What to call it when it is saved, for a project that has no file.</param>
    private async Task OpenProjectAsync(
        ProjectDocument document,
        bool edited,
        IReadOnlyList<string> notes,
        string? suggested)
    {
        if (!await CloseProjectAsync().ConfigureAwait(true))
        {
            return;
        }

        if (document.Path is { } named && ProjectRecovery.Waiting(named, document.ToXml()) is { } waiting)
        {
            if (await ConfirmDiscardRecovery(Recovered(named, waiting)).ConfigureAwait(true))
            {
                ProjectRecovery.Delete(waiting);
            }
            else if (Restored(named, waiting) is { } recovered)
            {
                document = recovered;

                // Given back, not saved: the file is still what it was, and the window says so
                // until somebody writes it.
                edited = true;
            }
        }

        var workspace = new ProjectWorkspace(document, edited, suggested);

        _workspace = workspace;

        // An edited setting decides what everything under it inherits, so the tree's names and the
        // drawings already open both have to follow it.
        workspace.Edited += (_, _) =>
        {
            BuildTree();
            Retitle();
            Rebuild();
            UpdateMenu();
        };

        // A write changes nothing the tree or the boards are showing — only whether there is
        // anything left to write, which is all the chrome is asking.
        var remembered = false;

        workspace.Saved += (_, _) =>
        {
            // The pane's name is written where the tree is built, which a save deliberately does
            // not do — but the first save is where a project stops being Untitled.
            _projectName.Text = workspace.Name;

            UpdateTitle();
            UpdateMenu();

            // A project that existed only in memory — an import, a conversion, a new one — has
            // nowhere to be remembered from, and nothing to key a copy of unsaved work by, until it
            // has been written once. One opened from a file is already in the list.
            if (!remembered && workspace.Document.Path is { } written)
            {
                remembered = true;

                Remember(written);
                Cover(workspace, written);
            }
        };

        if (document.Path is { } kept)
        {
            Cover(workspace, kept);
        }

        ShowProjectPane(true);
        BuildTree();

        // On the project itself, rather than on nothing. What the window has just been pointed at is
        // the project, and its settings are the one row of the tree that is always there — opening
        // the file and then having to find that row to see anything was a step with no decision in
        // it. Whatever a drawing is opened from here replaces nothing: it is a tab of its own.
        await ShowAsync(workspace.Document.Root).ConfigureAwait(true);

        UpdateMenu();

        // Only a file that is there. A project written nowhere yet would be offered by Open Recent
        // and then quietly dropped from it, since the list keeps only what exists; the save that
        // writes it is what remembers it.
        if (document.Path is { } opened && File.Exists(opened))
        {
            remembered = true;

            Remember(opened);
        }

        if (notes.Count > 0)
        {
            await Announce("Converted", string.Join(Environment.NewLine + Environment.NewLine, notes)).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// An svgc project as one of these, held in the window.
    /// </summary>
    /// <remarks>
    /// One way, and the old project is left exactly as it was: the two formats say different things
    /// about where a drawing lives, and a conversion that wrote back would have to put the drawings
    /// out into files again. What it does lose is said in the notes — a recipe is baked into the
    /// drawings it painted, and a file the old project named twice becomes two drawings.
    /// </remarks>
    private static ProjectDocument Converted(string path, ICollection<string> notes)
    {
        var document = ProjectImport.FromSvgc(SvgcProjectDocument.Load(path), notes);

        notes.Add(
            $"{Path.GetFileName(path)} was converted into a project holding the drawings themselves. Nothing has "
            + "been written and nothing is named yet — saving it asks where it goes. The project it came from and "
            + "the drawings it named are left where they are.");

        return document;
    }

    /// <summary>Starts keeping a copy of this project's unsaved work, under the file it now has.</summary>
    /// <remarks>
    /// A project is covered from the moment it has a file to be keyed by — when it is opened from
    /// one, or when it is first saved to one. Before that there is nothing to look a copy up under
    /// again, and a copy nobody can find is worse than none.
    /// </remarks>
    private void Cover(ProjectWorkspace workspace, string path)
    {
        _recovery?.Stop();

        _recovery = new ProjectRecovery(workspace, path)
        {
            // Posted for the reason the close prompt is: a dialog opened from inside a timer tick
            // would run the window's message loop from under the tick.
            Trouble = (title, message) =>
                Dispatcher.UIThread.Post(async () => await Announce(title, message).ConfigureAwait(true))
        };
    }

    /// <summary>The project a recovered copy holds, or null when it cannot be read.</summary>
    /// <remarks>
    /// Parsed at the project's own path rather than loaded from where the copy lives, so what opens
    /// is the project and not a file in application data. A copy that cannot be read is said once
    /// and then ignored — the project itself is still there to open.
    /// </remarks>
    private ProjectDocument? Restored(string path, string recovery)
    {
        try
        {
            return ProjectRecovery.Read(recovery) is { } text
                ? ProjectDocument.Parse(text, Path.GetDirectoryName(path) ?? string.Empty, path)
                : null;
        }
        catch (Exception failure) when (failure is SvgcProjectException or SvgRecipeException)
        {
            _ = Announce("The copy couldn't be read", failure.Message);

            return null;
        }
    }

    /// <summary>What a copy of unsaved work says for itself when the project is opened again.</summary>
    private static string Recovered(string path, string recovery)
    {
        var copied = File.GetLastWriteTime(recovery);
        var name = Path.GetFileName(path);

        if (!File.Exists(path))
        {
            return $"Svg Studio kept a copy of {name} from {Stamp(copied)}, holding changes that were never saved. "
                   + "Nothing has been written at that name yet.";
        }

        var saved = File.GetLastWriteTime(path);

        return $"Svg Studio kept a copy of {name} from {Stamp(copied)}, holding changes that were never saved. "
               + $"The file itself was last saved {Stamp(saved)}"
               + (saved > copied ? ", so something else has written it since. " : ". ")
               + "Restoring puts those changes back in the window; the file is not touched until you save.";
    }

    private static string Stamp(DateTime at) => at.ToString("g", CultureInfo.CurrentCulture);

    /// <summary>Closes the open project and everything it opened.</summary>
    /// <remarks>Public for the reason <see cref="ExportAsync"/> is: it is the way in without a menu.</remarks>
    /// <returns>Whether it closed, or false when unsaved work was kept.</returns>
    public async Task<bool> CloseProjectAsync()
    {
        if (_workspace is not { } workspace)
        {
            return true;
        }

        // Asked once for all of them rather than tab by tab, since closing the project is one act
        // and being stopped halfway through it would leave half a workspace open.
        var owned = _tabs.Items.OfType<TabItem>().Where(Owned).ToList();

        var unsaved = Unsaved();
        var project = Unwritten;

        if ((unsaved.Count > 0 || project is { })
            && !await ConfirmDiscard(Describe(unsaved, project)).ConfigureAwait(true))
        {
            return false;
        }

        // The tabs are the project's, so they go with it rather than being left pointing at nothing.
        foreach (var item in owned)
        {
            CloseTab(item);
        }

        // Whatever was not saved has just been thrown away on purpose, so the copy of it goes too:
        // offering it back on the next open would be handing back what somebody declined to keep.
        _recovery?.Drop();
        _recovery?.Stop();
        _recovery = null;

        _workspace = null;

        _projectTree.Items.Clear();
        ShowProjectPane(false);
        UpdateTitle();
        UpdateMenu();

        return true;
    }

    /// <summary>Whether a tab belongs to the open project, and goes when the project does.</summary>
    private static bool Owned(TabItem item) => item.Tag is ProjectNode;

    private async void OnCloseProject(object? sender, EventArgs e) => await CloseProjectAsync();

    private async void OnBuild(object? sender, EventArgs e) => await BuildAsync();

    /// <summary>
    /// Writes the open project's outputs, as svgc would.
    /// </summary>
    /// <remarks>
    /// Through the same build svgc runs rather than one of its own, so what this writes and what
    /// the tool writes cannot come to differ — they would differ silently, since both outputs
    /// compile.
    /// </remarks>
    /// <returns>Whether anything was written.</returns>
    public async Task<bool> BuildAsync()
    {
        if (_workspace is not { } workspace)
        {
            return false;
        }

        // A project with no file has no directory either, so an output written relative to it would
        // land wherever Studio was started from. Saving first is the question that answers where —
        // and it comes before the flatten, which resolves every output against that answer.
        if (workspace.Document.Path is null && !await WriteAsync(workspace).ConfigureAwait(true))
        {
            return false;
        }

        var project = workspace.Document.Flatten();
        var log = new List<string>();

        IReadOnlyList<string> written;

        try
        {
            // Off the UI thread: a project of any size compiles every drawing it names.
            written = await Task.Run(
                () => SvgcProjectBuild.Run(
                    project,
                    SvgcBuildSettings.For(project),
                    new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings())),
                    line => log.Add(line))).ConfigureAwait(true);
        }
        catch (Exception failure) when (failure is SvgcProjectException or SvgRecipeException or ExprException or IOException or UnauthorizedAccessException)
        {
            await Announce("The project couldn't be built", failure.Message).ConfigureAwait(true);

            return false;
        }

        // The warnings are the half worth reading — a recipe that matched nothing, a default that
        // will not reach a signature — and there is nowhere else they would be seen.
        var said = log.Where(line => line.StartsWith("warning:", StringComparison.Ordinal)).ToList();

        await Announce(
            "Built",
            said.Count > 0
                ? string.Join(Environment.NewLine + Environment.NewLine, said.Prepend(Wrote(written)))
                : Wrote(written)).ConfigureAwait(true);

        return true;
    }

    /// <summary>How many things a dialog names before it starts counting instead.</summary>
    /// <remarks>
    /// The dialog sizes itself to what it holds and has nothing to scroll, so a project with an
    /// output on every drawing — an ordinary icon set — would make a window taller than the screen.
    /// </remarks>
    private const int Listed = 10;

    /// <summary>What a build came to, for the sentence that reports it.</summary>
    /// <remarks>
    /// In full. A project decides where its own output goes, and the name alone said nothing about
    /// where that was — which is the one thing a build cannot be read back off the screen.
    /// </remarks>
    private static string Wrote(IReadOnlyList<string> written)
    {
        if (written.Count == 1)
        {
            return $"Wrote {written[0]}";
        }

        var lines = new List<string> { $"Wrote {written.Count} files:" };

        lines.AddRange(written.Take(Listed));

        if (written.Count > Listed)
        {
            lines.Add($"…and {written.Count - Listed} more.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void ShowProjectPane(bool show)
    {
        _projectPaneHost.IsVisible = show;
        _projectSplitter.IsVisible = show;
        _projectColumn.Width = show ? new GridLength(260) : new GridLength(0);
        _projectColumn.MinWidth = show ? 180 : 0;

        if (!show)
        {
            // The next project opens on its own rows rather than under what was being looked for in
            // the last one. Clearing it empties the tally through the box's own handler.
            _projectSearch.Text = null;

            // And nothing is left held from a project that is no longer open, which would otherwise
            // be pasted into the next one as a row of a document it does not belong to.
            _held = null;

            // Nor anything open in it, which would otherwise hold the closed document's nodes alive
            // for as long as the window is.
            _expanded.Clear();
        }
    }

    /// <param name="select">The node to leave selected, or null to keep whatever was.</param>
    private void BuildTree(ProjectNode? select = null)
    {
        if (_workspace is not { } workspace)
        {
            return;
        }

        _projectName.Text = workspace.Name;

        var selected = select ?? (_projectTree.SelectedItem as TreeViewItem)?.Tag;

        _projectTree.Items.Clear();
        _projectTree.Items.Add(Branch(workspace.Document.Root, selected));

        // A row that was just added, pasted or moved can land inside a group the reader left folded,
        // and a selection nobody can see is no selection at all. Only when this rebuild is about that
        // row: rebuilding for anything else must not reopen what was deliberately folded.
        if (select is { })
        {
            Reveal(select);
        }
    }

    /// <summary>One node and everything under it, folded unless the reader opened it.</summary>
    /// <remarks>
    /// Folded rather than open: a project is usually a handful of rows, but it does not have to be
    /// — an imported PaintCode document is ten groups holding 1014 drawings, and opening all of it
    /// buried the ten rows anybody would start from. The root is the exception, because folding the
    /// only row that is always there would leave the pane showing one word.
    /// </remarks>
    private TreeViewItem Branch(ProjectNode node, object? selected)
    {
        var item = new TreeViewItem
        {
            Header = ProjectWorkspace.Label(node),
            Tag = node,
            IsExpanded = node is ProjectRoot || _expanded.Contains(node),
            IsSelected = ReferenceEquals(node, selected)
        };

        // PropertyChanged rather than the Expanded and Collapsed events, which bubble: a nested row
        // opening raises them on every group above it too, and each would record itself as opened.
        item.PropertyChanged += (_, changed) =>
        {
            if (changed.Property != TreeViewItem.IsExpandedProperty)
            {
                return;
            }

            if (item.IsExpanded)
            {
                _expanded.Add(node);
            }
            else
            {
                _expanded.Remove(node);
            }
        };

        // Tapped, not DoubleTapped: TreeViewItem takes a double tap on its header to fold the node
        // away, and wires that to the header — which is below this in the bubble route, so its
        // handler runs first and marking the event handled here is too late. Opening on a double
        // tap opened the tab and collapsed the group on the way. Tapped is not raised by the arrow
        // keys either, so walking the tree still costs nothing.
        item.Tapped += async (_, e) =>
        {
            // Handled whatever happens, so a tap inside a nested row does not reach the group above
            // and open that as well.
            e.Handled = true;

            // The release that finishes a drag raises this too, which opened whatever had just been
            // dropped. Cleared by the next press, so a drag that ends without one swallows nothing.
            if (_rowDragged)
            {
                return;
            }

            // The chevron folds; it does not open.
            if (e.Source is Visual source && source.FindAncestorOfType<ToggleButton>(true) is { })
            {
                return;
            }

            await ShowAsync(node);
        };

        item.ContextMenu = Commands(node);

        if (node is ProjectGroup group)
        {
            foreach (var child in Sorted(group.Children))
            {
                item.Items.Add(Branch(child, selected));
            }
        }

        return item;
    }

    /// <summary>A group's rows, in the order the pane shows them.</summary>
    /// <remarks>
    /// By name rather than in the order the file writes them. The document's order still decides
    /// what the board lays out and what the generated C# follows; it is only the pane that sorts,
    /// so opening a project and closing it leaves the file exactly as it was found.
    ///
    /// Invariant rather than the current culture, so the pane reads the same on every machine, and
    /// ordinal after it so two names differing only in case keep a settled order instead of
    /// swapping about between rebuilds.
    /// </remarks>
    private static IEnumerable<ProjectNode> Sorted(IReadOnlyList<ProjectNode> children)
        => children
            .OrderBy(ProjectWorkspace.Label, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(ProjectWorkspace.Label, StringComparer.Ordinal);

    /// <summary>What can be done to a row, on the row rather than in the menu bar.</summary>
    /// <remarks>
    /// Per row, so what is acted on is what was clicked — a right click does not select, and a menu
    /// reading the selection would act on whatever was opened last.
    /// </remarks>
    private ContextMenu Commands(ProjectNode node)
    {
        var menu = new ContextMenu();
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        // The project is the file; it can be neither taken out of the tree nor put back into it.
        if (node.Parent is { })
        {
            Add("Cut", () => Hold(node, cut: true), new KeyGesture(Key.X, command));
            Add("Copy", () => Hold(node, cut: false), new KeyGesture(Key.C, command));
        }

        // Always, unlike Cut and Copy: what a paste would land is on the system clipboard as often
        // as it is a row held here, and looking there to find out is a read the platform wants a
        // paste behind. So the command is offered and answers for itself.
        Add("Paste", async () => await PasteAsync(node), new KeyGesture(Key.V, command));

        menu.Items.Add(new Separator());

        Add("Add group", async () => await AddGroupAsync(node));
        Add("Add SVG…", async () => await AddDrawingAsync(node));

        // A group has no file of its own, so what it shows is the project it is written in.
        if (OnDisk(node) is { })
        {
            menu.Items.Add(new Separator());
            Add(Revealing, () => ShowOnDisk(OnDisk(node)!));
        }

        if (node.Parent is { })
        {
            menu.Items.Add(new Separator());
            Add("Remove", async () => await RemoveAsync(node));
        }

        return menu;

        void Add(string header, Action command, KeyGesture? gesture = null)
        {
            var item = new MenuItem { Header = header, InputGesture = gesture };

            item.Click += (_, _) => command();

            menu.Items.Add(item);
        }
    }

    /// <summary>What the file manager would be pointed at for this row, or null where nothing would.</summary>
    /// <remarks>
    /// The project, whatever the row: a drawing is in it rather than beside it, so there is no other
    /// file to show. A project parsed from text, and one converted but not saved yet, have no file
    /// at all, and no row of either offers the command — it would point a file manager at nothing.
    /// </remarks>
    private string? OnDisk(ProjectNode node)
        => _workspace?.Document.Path is { } path && File.Exists(path) ? path : null;

    /// <summary>Takes a row, to be pasted somewhere else.</summary>
    private void Hold(ProjectNode node, bool cut)
    {
        _held = node;
        _heldCut = cut;

        // The menus were built when the tree was, and none of them offered Paste.
        BuildTree(node);
    }

    /// <summary>
    /// Puts what was held beside <paramref name="target"/> — the same place an Add goes.
    /// </summary>
    /// <remarks>
    /// A cut is the move a drag makes, so it is refused where a drag would be refused: a group
    /// cannot be pasted into itself or into its own branch. A copy cannot be refused at all, since
    /// what lands is a snapshot taken before it was anywhere.
    ///
    /// A row cut and then removed is gone, and the hold goes with it rather than waiting to fail.
    /// </remarks>
    private void Paste(ProjectNode target)
    {
        if (_workspace is not { } workspace || _held is not { } held)
        {
            return;
        }

        if (_heldCut)
        {
            if (held.Parent is { })
            {
                Move(held, target, target is ProjectGroup ? ProjectDrop.Inside : ProjectDrop.After);
            }

            _held = null;
            BuildTree(held.Parent is { } ? held : null);

            return;
        }

        var (parent, index) = Beside(target);
        var copy = parent.Copy(held, index);

        // Once, as a cut is: the hold is what a paste hears before the system clipboard, and a copy
        // that outlived its paste would go on answering for every paste made afterwards — including
        // the one meant for an icon copied in another program. Twice is Copy, Paste, Copy, Paste.
        _held = null;

        workspace.Edit();
        BuildTree(copy);
    }

    /// <summary>
    /// Puts whatever is waiting to be pasted beside <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// A row held by Cut or Copy first, and the system clipboard where none is. The two cannot be
    /// ranked by age — nothing says when a clipboard was written — so the one this window was told
    /// about wins, and it is spent by the paste that takes it rather than staying to answer the next.
    /// </remarks>
    private async Task PasteAsync(ProjectNode target)
    {
        if (_workspace is not { } workspace)
        {
            return;
        }

        if (_held is { })
        {
            Paste(target);

            return;
        }

        var carried = await ProjectClipboard.ReadAsync(Clipboard).ConfigureAwait(true);
        var (parent, index) = Beside(target);

        // The rule a drop of files obeys, obeyed the same way: all of them drawings, or none of it.
        if (carried.Files is { Count: > 0 } files && files.All(IsDrawing))
        {
            await AddDrawingsAsync(parent, index, files).ConfigureAwait(true);

            return;
        }

        if (carried.Drawing is { } drawing)
        {
            // Straight in, with no file written anywhere: the project holds the drawings, so a
            // pasted one needs nowhere to live but the row it lands on.
            await AddTextAsync(parent, index, "drawing", drawing).ConfigureAwait(true);

            return;
        }

        await Announce("There is nothing to paste", Offering(carried)).ConfigureAwait(true);
    }

    /// <summary>Why a paste did nothing, in terms of what the clipboard actually had on it.</summary>
    /// <remarks>
    /// Naming what was there rather than what was wanted: a drawing program puts several pictures of
    /// the same art on the clipboard at once, and which of them arrived is the whole difference
    /// between a paste that works and one that cannot.
    /// </remarks>
    private static string Offering(ProjectClipboard carried)
    {
        if (carried.Files is { Count: > 0 } files)
        {
            return $"The clipboard holds {string.Join(", ", files.Select(Path.GetFileName))}, which "
                   + "a project has no row for. A drawing is an .svg or an .svgz file.";
        }

        if (carried.Formats.Count == 0)
        {
            return "The clipboard is empty.";
        }

        var formats = carried.Formats.Count <= Listed
            ? string.Join(", ", carried.Formats)
            : $"{string.Join(", ", carried.Formats.Take(Listed))} and {carried.Formats.Count - Listed} more";

        return $"The clipboard holds {formats}, and none of it is a drawing. "
               + "Illustrator writes one when Preferences > Clipboard Handling > Include SVG Code is on.";
    }

    /// <summary>Where something added beside <paramref name="node"/> goes: in a group, after a drawing.</summary>
    private static (ProjectGroup Parent, int Index) Beside(ProjectNode node)
        => node is ProjectGroup group
            ? (group, group.Children.Count)
            : (node.Parent!, node.Parent!.Children.ToList().IndexOf(node) + 1);

    /// <summary>Adds an empty group, and opens it so it can be given a name.</summary>
    /// <remarks>Public for the reason <see cref="ExportAsync"/> is: a way in without the menu.</remarks>
    public async Task AddGroupAsync(ProjectNode beside)
    {
        if (_workspace is not { } workspace)
        {
            return;
        }

        var (parent, index) = Beside(beside);
        var group = parent.AddGroup("group", index);

        workspace.Edit();
        BuildTree(group);

        // Named "group" until something better is typed into its settings, which is what the tab
        // it opens on is for.
        await ShowAsync(group).ConfigureAwait(true);
    }

    /// <summary>Asks which drawing to add, and adds it.</summary>
    private async Task AddDrawingAsync(ProjectNode beside)
    {
        if (_workspace is null || !StorageProvider.CanOpen)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add a drawing to the project",
            AllowMultiple = true,
            FileTypeFilter = new List<FilePickerFileType> { StudioFileDialogService.Drawings }
        }).ConfigureAwait(true);

        var (parent, index) = Beside(beside);

        await AddDrawingsAsync(
            parent,
            index,
            files.Select(file => file.TryGetLocalPath())
                .Where(path => path is { Length: > 0 })
                .Select(path => path!)
                .ToList())
            .ConfigureAwait(true);
    }

    /// <summary>Adds <paramref name="path"/> to the project, and opens it.</summary>
    /// <remarks>Taking the path rather than asking for it, so everything but the panel can be driven.</remarks>
    public Task AddDrawingAsync(ProjectNode beside, string path)
    {
        var (parent, index) = Beside(beside);

        return AddDrawingsAsync(parent, index, new[] { path });
    }

    /// <summary>
    /// Puts drawings into <paramref name="parent"/> from <paramref name="index"/> on, in the order
    /// given, and opens the last of them.
    /// </summary>
    /// <remarks>
    /// The slot is counted here rather than worked out again for each file. Asking where a drop
    /// lands once per file keeps the order for a landing inside a group, whose end moves along with
    /// every insert, and for one before a node, whose index moves up — but reverses it after a node,
    /// which does not move at all, so each file would land immediately after it and push the last
    /// one out.
    ///
    /// One save and one rebuild for the run, and one tab: a folder of drawings dropped on a group
    /// is one act, and it has no business opening twenty of them.
    /// </remarks>
    /// <remarks>Public for the reason <see cref="Move"/> is: the way in without the pointer.</remarks>
    public async Task AddDrawingsAsync(ProjectGroup parent, int index, IReadOnlyList<string> paths)
    {
        if (_workspace is not { } workspace || paths.Count == 0)
        {
            return;
        }

        ProjectDrawing? added = null;

        foreach (var path in paths)
        {
            try
            {
                added = parent.AddDrawing(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path), index++);
            }
            catch (Exception failure) when (failure is SvgcProjectException or IOException or UnauthorizedAccessException)
            {
                await Announce("That drawing couldn't be added", $"{Path.GetFileName(path)}: {failure.Message}").ConfigureAwait(true);
            }
        }

        if (added is null)
        {
            return;
        }

        workspace.Edit();
        BuildTree(added);

        await ShowAsync(added).ConfigureAwait(true);
    }

    /// <summary>Adds one drawing the window has the text of rather than a file for.</summary>
    private async Task AddTextAsync(ProjectGroup parent, int index, string name, string svgText)
    {
        if (_workspace is not { } workspace)
        {
            return;
        }

        ProjectDrawing added;

        try
        {
            added = parent.AddDrawing(name, svgText, index);
        }
        catch (SvgcProjectException failure)
        {
            await Announce("That drawing couldn't be added", failure.Message).ConfigureAwait(true);

            return;
        }

        workspace.Edit();
        BuildTree(added);

        await ShowAsync(added).ConfigureAwait(true);
    }

    /// <summary>
    /// Takes a node out of the project, with everything under it.
    /// </summary>
    /// <remarks>
    /// The tabs go first, so work typed into one still gets its question, and so that nothing is
    /// left editing an element the document no longer holds — a <see cref="GroupPanel"/> over a
    /// removed node goes on writing settings into a detached element and reporting itself saved.
    /// </remarks>
    /// <returns>Whether it was removed, or false when the question was answered against it.</returns>
    public async Task<bool> RemoveAsync(ProjectNode node)
    {
        if (_workspace is not { } workspace || node.Parent is not { } parent)
        {
            return false;
        }

        // Only when it takes something with it. A row removed by mistake is one add away; a branch
        // is not, and there is no undo.
        if (node is ProjectGroup { Children.Count: > 0 } group
            && !await ConfirmRemove(Removing(group)).ConfigureAwait(true))
        {
            return false;
        }

        foreach (var item in _tabs.Items.OfType<TabItem>()
                     .Where(item => item.Tag is ProjectNode held && held.DescendsFrom(node))
                     .ToList())
        {
            if (!await CloseTabAsync(item).ConfigureAwait(true))
            {
                return false;
            }
        }

        parent.Remove(node);

        workspace.Edit();
        BuildTree();

        return true;
    }

    /// <summary>
    /// What a row being dragged carries, which is how a drag of rows is told from a drag of files.
    /// </summary>
    /// <remarks>
    /// A bare name: Avalonia refuses an identifier with a separator in it, and built here rather
    /// than at the drag, where the refusal was an unhandled exception out of an async void handler
    /// and took the application with it. As a field it is a type initialiser instead, which is a
    /// failure every test that opens a window sees.
    /// </remarks>
    private static readonly DataFormat<string> RowFormat = DataFormat.CreateStringApplicationFormat("ProjectNode");

    private ProjectNode? _row;
    private PointerPressedEventArgs? _rowPressed;
    private Point _rowPressedAt;

    /// <summary>Whether the press that is finishing was a drag, so the tap it raises opens nothing.</summary>
    private bool _rowDragged;

    /// <summary>The groups the reader has opened, so a rebuild puts them back as they were.</summary>
    /// <remarks>
    /// The rows are built afresh after every edit, and the tree opens folded — so without this,
    /// adding a drawing would shut every group the reader had just opened to find the place to add
    /// it. Held by node rather than by name because two groups can be called the same thing, and the
    /// document's nodes are the same objects across a rebuild: only the rows are new.
    /// </remarks>
    private readonly HashSet<ProjectNode> _expanded = new();

    /// <summary>The row waiting to be pasted, and whether taking it was a cut rather than a copy.</summary>
    /// <remarks>
    /// The window's own, not the machine's: what is held is a row of this project, and pasting one
    /// into a text editor would mean nothing. Nothing in the app touches the system clipboard.
    /// </remarks>
    private ProjectNode? _held;
    private bool _heldCut;

    private ProjectNode? _dropOn;
    private ProjectDrop _dropWhere;

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        _row = null;
        _rowPressed = null;
        _rowDragged = false;

        if (e.Source is not Visual source
            // The chevron folds the row; it does not pick it up.
            || source.FindAncestorOfType<ToggleButton>(true) is { }
            || source.FindAncestorOfType<TreeViewItem>(true)?.Tag is not ProjectNode node
            // The project is the file. There is nowhere to put it.
            || node is ProjectRoot
            || !e.GetCurrentPoint(_projectTree).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _row = node;
        _rowPressed = e;
        _rowPressedAt = e.GetPosition(_projectTree);
    }

    private async void OnRowMoved(object? sender, PointerEventArgs e)
    {
        if (_row is null || _rowPressed is not { } pressed)
        {
            return;
        }

        if (!e.GetCurrentPoint(_projectTree).Properties.IsLeftButtonPressed)
        {
            _row = null;
            _rowPressed = null;

            return;
        }

        var travelled = e.GetPosition(_projectTree) - _rowPressedAt;

        if (Math.Abs(travelled.X) < DragThreshold && Math.Abs(travelled.Y) < DragThreshold)
        {
            return;
        }

        var data = new DataTransfer();

        data.Add(DataTransferItem.Create(RowFormat, string.Empty));

        _rowPressed = null;
        _rowDragged = true;

        try
        {
            await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Move);
        }
        finally
        {
            _row = null;

            HideDrop();
        }
    }

    /// <summary>
    /// Where the drag under the pointer would land — a row being moved, or drawings being added.
    /// </summary>
    /// <remarks>
    /// Two kinds of drag, one landing: both go before a row, after it, or into a group, and both are
    /// drawn by the same line and outline. Only what is being put there differs.
    ///
    /// A drag of files is taken only when every file in it is a drawing. Filtering one instead would
    /// mean marking the drop taken and dropping the rest of it on the floor — a project dragged in
    /// with a drawing would stop opening, which it does today wherever it lands.
    /// </remarks>
    private void OnRowDragOver(object? sender, DragEventArgs e)
    {
        var over = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(true);

        if (_row is { } dragged)
        {
            if (over is not { Tag: ProjectNode node })
            {
                HideDrop();

                return;
            }

            e.DragEffects = DragDropEffects.Move;

            if (Land(over, node, e) is not { } landing || landing.Parent.DescendsFrom(dragged))
            {
                HideDrop();

                return;
            }

            ShowDrop(over);

            return;
        }

        if (Dropped(e) is not { Count: > 0 } paths || !paths.All(IsDrawing))
        {
            HideDrop();

            return;
        }

        // Nothing under the pointer is the tree's own background, below the last row: the project
        // itself, at the end of it. Anywhere in the tree adds to the project, which is what makes
        // the pane a place to drop rather than a place with places to drop.
        //
        // The effects are left alone: the window's own handler narrows them for a file drag, and it
        // runs after this one.
        var target = over ?? _projectTree.Items.OfType<TreeViewItem>().FirstOrDefault();

        if (target is not { Tag: ProjectNode into } || Land(target, into, e) is null)
        {
            HideDrop();

            return;
        }

        ShowDrop(target);
    }

    /// <summary>Takes down where a drop on this row would go, and says where that is.</summary>
    private (ProjectGroup Parent, int Index)? Land(TreeViewItem row, ProjectNode node, DragEventArgs e)
    {
        _dropOn = node;
        _dropWhere = Bands(e.GetPosition(row).Y, RowHeight(row), node);

        return Landing(_dropOn, _dropWhere);
    }

    private async void OnRowDrop(object? sender, DragEventArgs e)
    {
        var target = _dropOn;
        var where = _dropWhere;

        // Before anything else: the landing is what the pointer said last, and HideDrop forgets it.
        HideDrop();

        if (_row is { } dragged)
        {
            if (target is { })
            {
                Move(dragged, target, where);
            }

            return;
        }

        if (target is null
            || Landing(target, where) is not { } landing
            || Dropped(e) is not { Count: > 0 } paths
            || !paths.All(IsDrawing))
        {
            return;
        }

        // Taken, so the window does not open tabs on the drawings that just became rows.
        e.Handled = true;

        await AddDrawingsAsync(landing.Parent, landing.Index, paths).ConfigureAwait(true);
    }

    /// <summary>
    /// Moves a node to where a drop on <paramref name="target"/> puts it.
    /// </summary>
    /// <remarks>Public for the reason <see cref="ExportAsync"/> is: a way in without the pointer.</remarks>
    /// <returns>Whether it moved. A drop that would take a group into itself does not.</returns>
    public bool Move(ProjectNode node, ProjectNode target, ProjectDrop where)
    {
        if (_workspace is not { } workspace || Landing(target, where) is not { } landing)
        {
            return false;
        }

        try
        {
            landing.Parent.Move(node, landing.Index);
        }
        catch (SvgcProjectException)
        {
            return false;
        }

        workspace.Edit();
        BuildTree(node);

        return true;
    }

    /// <summary>Which group a drop lands in, and where among its children. Null when it lands nowhere.</summary>
    private static (ProjectGroup Parent, int Index)? Landing(ProjectNode target, ProjectDrop where)
    {
        if (where == ProjectDrop.Inside)
        {
            return target is ProjectGroup group ? (group, group.Children.Count) : null;
        }

        if (target.Parent is not { } parent)
        {
            return null;
        }

        var index = parent.Children.ToList().IndexOf(target);

        return (parent, where == ProjectDrop.After ? index + 1 : index);
    }

    /// <summary>Which of the three a drop at <paramref name="y"/> down a row means.</summary>
    /// <remarks>
    /// Quarters for a group, which can be dropped into as well as beside. A drawing holds nothing,
    /// so its middle is not a place, and halves put every drop somewhere it can go. The project has
    /// no siblings to sit beside, so the whole of its row means inside it.
    /// </remarks>
    private static ProjectDrop Bands(double y, double height, ProjectNode target)
    {
        if (target is not ProjectGroup)
        {
            return y < height / 2 ? ProjectDrop.Before : ProjectDrop.After;
        }

        if (target.Parent is null)
        {
            return ProjectDrop.Inside;
        }

        return y < height * 0.25 ? ProjectDrop.Before
            : y > height * 0.75 ? ProjectDrop.After
            : ProjectDrop.Inside;
    }

    /// <summary>
    /// How tall the row itself is, rather than the row and everything under it.
    /// </summary>
    /// <remarks>
    /// Measured to the first child rather than read off the template, which would tie this to a
    /// part name: a TreeViewItem's bounds cover its whole branch, and taking those for the row put
    /// the quarter marks a subtree apart.
    /// </remarks>
    private static double RowHeight(TreeViewItem item)
    {
        if (item.IsExpanded
            && item.Items.Count > 0
            && item.Items[0] is Visual first
            && first.TranslatePoint(new Point(0, 0), item) is { Y: > 0 } at)
        {
            return at.Y;
        }

        return item.Bounds.Height;
    }

    /// <summary>Draws the landing: a line between two rows, or the outline of the group it goes in.</summary>
    private void ShowDrop(TreeViewItem item)
    {
        if (item.TranslatePoint(new Point(0, 0), _dropHost) is not { } at)
        {
            return;
        }

        var height = RowHeight(item);
        var inside = _dropWhere == ProjectDrop.Inside;

        _dropLine.Width = Math.Max(item.Bounds.Width, 1);
        _dropLine.Height = inside ? height : 2d;
        _dropLine.Background = inside ? new SolidColorBrush(Color.Parse("#334C9BE8")) : new SolidColorBrush(Color.Parse("#4C9BE8"));
        _dropLine.BorderThickness = new Thickness(inside ? 1d : 0d);
        _dropLine.Margin = new Thickness(at.X, at.Y + (_dropWhere == ProjectDrop.After ? height - 2d : 0d), 0, 0);
        _dropLine.IsVisible = true;
    }

    private void HideDrop()
    {
        _dropLine.IsVisible = false;
        _dropOn = null;
    }

    private static string Removing(ProjectGroup group)
    {
        var rows = group.Children.Count;

        return $"{ProjectWorkspace.Label(group)} holds {rows} {(rows == 1 ? "row" : "rows")}, "
               + "which will be removed with it. This cannot be undone.";
    }

    /// <summary>Every drawing under <paramref name="node"/>, whatever kind of node it is.</summary>
    private static IEnumerable<ProjectDrawing> Drawings(ProjectNode node)
        => node switch
        {
            ProjectGroup group => group.Drawings,
            ProjectDrawing drawing => new[] { drawing },
            _ => Enumerable.Empty<ProjectDrawing>()
        };

    private async void OnProjectTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if ((_projectTree.SelectedItem as TreeViewItem)?.Tag is not ProjectNode node)
        {
            return;
        }

        // Here rather than in the window's own handler, which tunnels: taken there, these would be
        // the tree's answer to a copy anywhere in the window, including in a box being typed in.
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        if (e.KeyModifiers == command && e.Key is Key.X or Key.C or Key.V)
        {
            e.Handled = true;

            if (e.Key == Key.V)
            {
                await PasteAsync(node);
            }
            else
            {
                Hold(node, cut: e.Key == Key.X);
            }

            return;
        }

        // Back as well as Delete: the two are one key on a Mac keyboard.
        if (e.Key is Key.Delete or Key.Back)
        {
            e.Handled = true;

            await RemoveAsync(node);

            return;
        }

        if (e.Key is not (Key.Enter or Key.Return))
        {
            return;
        }

        e.Handled = true;

        await ShowAsync(node);
    }

    /// <summary>
    /// Brings a node of the project forward, in a tab of its own.
    /// </summary>
    /// <remarks>
    /// A drawing opens in a viewer at the size the project builds it at; anything else is a group,
    /// and opens as its settings and what they come to. Public because a modal-free way in is what
    /// a test drives, the same as <see cref="ExportAsync"/>.
    /// </remarks>
    public async Task ShowAsync(ProjectNode node)
    {
        if (node is null)
        {
            throw new ArgumentNullException(nameof(node));
        }

        if (_workspace is not { } workspace)
        {
            return;
        }

        if (Tab(node) is { } open)
        {
            _tabs.SelectedItem = open;
            return;
        }

        if (node is ProjectGroup group)
        {
            AddNodeTab(new GroupPanel(workspace, group) { TargetOf = DrawingOf }, node, ProjectWorkspace.Label(node));
            return;
        }

        var drawing = (ProjectDrawing)node;

        var viewer = AddTab();

        if (_tabs.SelectedItem is TabItem item)
        {
            item.Tag = node;

            // Beside the drawing's own parameters, in the pane a group keeps its settings in. The
            // class is the whole of it: a project usually builds one file several times, and what
            // tells those rows apart is settable nowhere else.
            var settings = new GroupPanel(workspace, drawing);

            settings.ModifiedChanged += (_, _) => Mark(item);

            viewer.SidePanels = new[] { new SvgViewerPane("Project", settings) };
        }

        viewer.SizeRequest = ProjectWorkspace.SizeOf(drawing);

        // What the groups above it declare, written into it on the way to being drawn. Source stays
        // the drawing's own, so the tab still shows, edits and saves the drawing.
        viewer.Rewrite = own => ProjectDeclarations.Built(drawing, own);

        // And the three halves of an inherited row being editable where it is shown: where the edit
        // goes, how often the name is used outside the block that declares it, and what the row says
        // about where it came from.
        // A fresh target each time is the same place to write: GroupTarget is the group it is over.
        viewer.DeclarationTargetOf = name => Declaring(drawing, name) is { } holder
            ? new GroupTarget(workspace, holder)
            : null;

        viewer.DeclaredBy = name => ProjectWorkspace.Label(
            Declaring(drawing, name) ?? (ProjectNode)drawing);

        await viewer.LoadTextAsync(drawing.Text, drawing.Name).ConfigureAwait(true);
    }

    /// <summary>Which group above <paramref name="drawing"/> declares <paramref name="name"/>, if any.</summary>
    /// <remarks>
    /// Null for a name the drawing declares itself, which is every name in an ordinary project and
    /// the answer the viewer reads as "this drawing". The chain is walked innermost first only so
    /// the loop can stop; a name declared twice down it is refused before a drawing is built at all.
    /// </remarks>
    private static ProjectGroup? Declaring(ProjectDrawing drawing, string name)
    {
        foreach (var holder in ProjectDeclarations.Chain(drawing))
        {
            if (SvgExpressionDeclarations.Parse(holder.CodeText, out _) is { } declared
                && (declared.Parameters.Any(parameter => string.Equals(parameter.Name, name, StringComparison.Ordinal))
                    || declared.Lets.Any(let => string.Equals(let.Name, name, StringComparison.Ordinal))))
            {
                return holder;
            }
        }

        return null;
    }

    /// <summary>
    /// Where a drawing's text is written, for a host that wants to edit one.
    /// </summary>
    /// <remarks>
    /// The tab it is open in, so the edit lands in a buffer somebody can take back. Null is answered
    /// by the caller with the project itself, which takes the edit with nothing to take it back.
    /// By the row rather than by a file: a drawing is one row of the project, so one tab.
    /// </remarks>
    private ISvgViewerDeclarationTarget? DrawingOf(ProjectDrawing drawing)
        => Tab(drawing)?.Content as SvgViewer;

    /// <summary>A tab for something that is not a drawing, which the viewer's own tab does not fit.</summary>
    private void AddNodeTab(Control content, object tag, string name)
    {
        var title = new TextBlock { Classes = { "title" }, Text = name };
        var marker = new TextBlock { Classes = { "marker" } };
        var close = new Button { Classes = { "close" }, Content = "✕" };

        var item = new TabItem
        {
            Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { marker, title, close }
            },
            Content = content,
            Tag = tag
        };

        close.Click += async (_, _) => await CloseTabAsync(item);

        if (content is GroupPanel panel)
        {
            panel.ModifiedChanged += (_, _) => Mark(item);
        }

        _tabs.Items.Add(item);
        _tabs.SelectedItem = item;

    }

    private TabItem? Tab(ProjectNode node)
        => _tabs.Items.OfType<TabItem>().FirstOrDefault(item => ReferenceEquals(item.Tag, node));

    /// <summary>
    /// Reads the open drawings again as the project's settings now say to build them.
    /// </summary>
    /// <remarks>
    /// A drawing with unsaved edits of its own is left alone: reloading it would throw
    /// them away, and following a setting is not worth that.
    ///
    /// Viewers only. A group's tab follows the same event itself, so refreshing it from here was a
    /// second call to the one method, and every save rebuilt every open board twice.
    /// </remarks>
    private void Rebuild()
    {
        foreach (var item in _tabs.Items.OfType<TabItem>())
        {
            if (item.Tag is not ProjectDrawing drawing || item.Content is not SvgViewer viewer)
            {
                continue;
            }

            var request = ProjectWorkspace.SizeOf(drawing);

            // The drawing itself can have been written from somewhere else — a group's Element tab,
            // which writes into the project as it goes.
            var changed = !string.Equals(viewer.Source, drawing.Text, StringComparison.Ordinal);

            if ((request.Equals(viewer.SizeRequest) && !changed) || viewer.IsSourceModified)
            {
                continue;
            }

            viewer.SizeRequest = request;

            if (!ReferenceEquals(_tabs.SelectedItem, item))
            {
                _stale.Add(item);

                continue;
            }

            Read(viewer, drawing, changed);
        }
    }

    /// <summary>
    /// Opens the tree down to whatever the selected tab is showing, and marks its row.
    /// </summary>
    /// <remarks>
    /// The tree says where in the project you are, and a group folded away made it say nothing at
    /// all about a tab from inside it. Picking a tab is the answer to "where is this?", so the row
    /// comes back into sight rather than being left for the reader to go and find.
    ///
    /// </remarks>
    private void Reveal()
    {
        if ((_tabs.SelectedItem as TabItem)?.Tag is ProjectNode node)
        {
            Reveal(node);
        }
    }

    /// <summary>Opens the tree down to one node and puts its row in sight.</summary>
    /// <remarks>The jump a search makes, and the one picking a tab makes: the same one.</remarks>
    private void Reveal(ProjectNode node)
    {
        if (_projectTree.Items.OfType<TreeViewItem>().FirstOrDefault() is not { } root
            || Route(root, node) is not { } path)
        {
            return;
        }

        // Every group above it. The row itself is left as it is — folding a group open to see a
        // drawing inside it is not a reason to unfold the drawing's own children.
        for (var above = 0; above < path.Count - 1; above++)
        {
            path[above].IsExpanded = true;
        }

        var row = path[path.Count - 1];

        // The rows above were only just opened, and a TreeViewItem inside a group that has never
        // been open has no container in the tree yet — so selecting it would be selecting something
        // the TreeView cannot see, and the selection would come back null. Laying out first is what
        // gives the row a container to select.
        _projectTree.UpdateLayout();

        _projectTree.SelectedItem = row;

        // Opened is not the same as in sight: a long project scrolls.
        row.BringIntoView();
    }

    /// <summary>
    /// Walks to the match after the row the tree is on, or the one before it.
    /// </summary>
    /// <remarks>
    /// Nothing is hidden: the tree stays whole and the match is opened down to and selected, so a
    /// drawing is still found where it lives rather than in a list of what is left. A query that
    /// matches nothing leaves the selection where it was — a search that closed what was being
    /// looked at would cost more than it found.
    ///
    /// Rebuilt from the rows on every keystroke rather than kept: the tree is built again after any
    /// edit to the project, and a remembered list of rows would be a list of rows that are gone.
    /// </remarks>
    /// <param name="step">1 for the next match, -1 for the one before, 0 to stay on one that still matches.</param>
    private void JumpToMatch(int step)
    {
        var query = _projectSearch.Text;

        if (string.IsNullOrEmpty(query)
            || _projectTree.Items.OfType<TreeViewItem>().FirstOrDefault() is not { } root)
        {
            _projectSearchCount.Text = null;

            return;
        }

        // What the row says, read back off it: a search can only find what the tree is showing.
        var matches = Rows(root)
            .Where(row => row.Header is string label
                && label.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            _projectSearchCount.Text = "none";

            return;
        }

        var at = matches.IndexOf((_projectTree.SelectedItem as TreeViewItem)!);
        var index = at < 0 ? 0 : (at + step + matches.Count) % matches.Count;

        if (matches[index].Tag is ProjectNode node)
        {
            Reveal(node);
        }

        _projectSearchCount.Text = $"{index + 1}/{matches.Count}";
    }

    /// <summary>Enter for the next match, Shift+Enter for the one before, Escape to be done.</summary>
    private void OnProjectSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            e.Handled = true;

            JumpToMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;

            _projectSearch.Text = null;

            // Back to the rows, where Delete and Enter mean what they mean in a tree: leaving the
            // focus in an empty box makes the next keystroke type rather than act.
            _projectTree.Focus();
        }
    }

    /// <summary>Every row under this one, itself first, in the order the tree draws them.</summary>
    private static IEnumerable<TreeViewItem> Rows(TreeViewItem item)
    {
        yield return item;

        foreach (var child in item.Items.OfType<TreeViewItem>())
        {
            foreach (var row in Rows(child))
            {
                yield return row;
            }
        }
    }

    /// <summary>The rows from the root down to <paramref name="node"/>, or null when it has none.</summary>
    private static List<TreeViewItem>? Route(TreeViewItem item, ProjectNode node)
    {
        if (ReferenceEquals(item.Tag, node))
        {
            return new List<TreeViewItem> { item };
        }

        foreach (var child in item.Items.OfType<TreeViewItem>())
        {
            if (Route(child, node) is { } found)
            {
                found.Insert(0, item);

                return found;
            }
        }

        return null;
    }

    /// <summary>Builds the selected tab's drawing again, if it went out of date while out of sight.</summary>
    private void Refill()
    {
        if (_tabs.SelectedItem is not TabItem item
            || !_stale.Remove(item)
            || item.Content is not SvgViewer viewer)
        {
            return;
        }

        if (item.Tag is ProjectDrawing drawing)
        {
            _ = viewer.LoadTextAsync(drawing.Text, drawing.Name);

            return;
        }

        // A drawing of its own, so off the disk.
        if (viewer.DocumentPath is { } path)
        {
            _ = viewer.LoadAsync(path);
        }
    }

    /// <summary>
    /// Brings the drawing being looked at up to date, off the disk only where it has to be.
    /// </summary>
    /// <remarks>
    /// A size changes what the same text comes to, not the text, so the drawing is built again
    /// from what the pane is already holding. Reading it in again for that dropped the pane's
    /// buffer and its caret on every keystroke somebody made elsewhere, and flashed a load in the
    /// status line while they typed. Only text that has actually changed is taken again.
    /// </remarks>
    private static void Read(SvgViewer viewer, ProjectDrawing drawing, bool changed)
    {
        if (changed || !viewer.Rebuild())
        {
            _ = viewer.LoadTextAsync(drawing.Text, drawing.Name);
        }
    }

    // ---- reordering -------------------------------------------------------------------------

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source
            // The close button is a button, not a drag handle.
            || source.FindAncestorOfType<Button>(true) is { }
            // A press anywhere but on a tab — the drawing, the toolbar — is not a drag either. Only
            // the headers are inside a TabItem; the selected tab's content is not.
            || source.FindAncestorOfType<TabItem>(true) is not { } item
            || item.GetVisualParent() is not { } strip)
        {
            return;
        }

        if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pressed = item;
        _pressedAt = e.GetPosition(strip);
        _grabbedAt = _pressedAt.X - item.Bounds.X;
        _dragging = false;
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } dragged || dragged.GetVisualParent() is not Layoutable strip)
        {
            return;
        }

        // A release the window never saw — the button let go outside it, or over another
        // application — leaves a drag that would otherwise resume the moment the pointer comes back.
        if (!e.GetCurrentPoint(strip).Properties.IsLeftButtonPressed)
        {
            EndDrag(e.Pointer);
            return;
        }

        var position = e.GetPosition(strip);

        if (!_dragging)
        {
            if (Math.Abs(position.X - _pressedAt.X) < DragThreshold)
            {
                return;
            }

            // The strip is captured, not the tab: reordering takes the tab out of Items, and a
            // captured control that leaves the tree loses the capture — which ended the drag after
            // its own first swap.
            _dragging = true;
            dragged.ZIndex = 1;
            dragged.RenderTransform = _carry;
            e.Pointer.Capture(_tabs);
        }

        var from = _tabs.Items.IndexOf(dragged);
        var to = from;

        for (var index = 0; index < _tabs.Items.Count; index++)
        {
            if (index == from || _tabs.Items[index] is not TabItem neighbour)
            {
                continue;
            }

            // Half of a neighbour, not its edge: trading on contact leaves the pointer over the tab
            // it displaced and trades straight back. Every neighbour, because a quick drag lands
            // several tabs along.
            if (index > from && position.X > neighbour.Bounds.Center.X)
            {
                to = Math.Max(to, index);
            }
            else if (index < from && position.X < neighbour.Bounds.Center.X)
            {
                to = Math.Min(to, index);
            }
        }

        if (to != from)
        {
            MoveTab(dragged, to);

            // The tab is placed against its own laid-out position below, and a move it has not been
            // arranged for yet would put it a whole tab-width off for one frame.
            strip.UpdateLayout();
        }

        // The one transform is moved rather than replaced, so a drag allocates nothing per frame.
        _carry.X = position.X - _grabbedAt - dragged.Bounds.X;
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e) => EndDrag(e.Pointer);

    /// <summary>Puts the dragged tab down where the strip has already made room for it.</summary>
    private void EndDrag(IPointer? pointer)
    {
        if (_pressed is { } dragged)
        {
            dragged.RenderTransform = null;
            dragged.ZIndex = 0;
        }

        if (_dragging)
        {
            pointer?.Capture(null);
        }

        _pressed = null;
        _dragging = false;
    }

    /// <summary>Moves a tab within the strip, keeping it the selected one.</summary>
    /// <remarks>
    /// Removing the selected item clears the selection, and a tab that deselected itself halfway
    /// through being dragged would swap the drawing under the pointer.
    /// </remarks>
    private void MoveTab(TabItem item, int index)
    {
        _tabs.Items.Remove(item);
        _tabs.Items.Insert(index, item);
        _tabs.SelectedItem = item;
    }

    private void OnTabsTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (e.NameScope.Find<ScrollViewer>("PART_TabStrip") is not { } strip)
        {
            return;
        }

        // The strip only scrolls sideways, and a wheel that does nothing over an overflowing row of
        // tabs reads as the row being stuck.
        strip.AddHandler(
            PointerWheelChangedEvent,
            (_, wheel) =>
            {
                strip.Offset = strip.Offset.WithX(strip.Offset.X - (wheel.Delta.Y + wheel.Delta.X) * WheelStep);
                wheel.Handled = true;
            },
            RoutingStrategies.Tunnel);
    }

    // ---- exporting -------------------------------------------------------------------------

    private static readonly FilePickerFileType SvgFileType = new("Svg Files")
    {
        Patterns = new[] { "*.svg" },
        AppleUniformTypeIdentifiers = new[] { "public.svg-image" },
        MimeTypes = new[] { "image/svg+xml" }
    };

    private static readonly FilePickerFileType CSharpFileType = new("C# Files")
    {
        Patterns = new[] { "*.cs" },
        AppleUniformTypeIdentifiers = new[] { "public.source-code" },
        MimeTypes = new[] { "text/plain" }
    };

    /// <summary>
    /// Opens a drawing, in a tab of its own.
    /// </summary>
    /// <remarks>
    /// Through the viewer rather than around it: asking is <see cref="SvgViewer.OpenAsync()"/>, and
    /// what it raises is the request this window already turns into tabs, for a drop as much as for
    /// this. The viewer's toolbar had this button until the menu could hold it.
    /// </remarks>
    private async void OnOpen(object? sender, EventArgs e)
    {
        if (Selected() is { } viewer)
        {
            await viewer.OpenAsync();

            return;
        }

        // Nothing is open, so there is no viewer to ask through. The same picker one would have
        // shown, and the answer makes the tab — rather than a tab being kept standing empty in case
        // a file is ever opened into it.
        if (await new StudioFileDialogService().OpenSvgAsync(this).ConfigureAwait(true) is { } picked)
        {
            await OpenAsync(new[] { picked }).ConfigureAwait(true);
        }
    }

    /// <summary>Puts what was just opened at the front of File → Open Recent.</summary>
    /// <remarks>
    /// Called where the file was actually read rather than where it was asked for, so a drop, the
    /// command line, the picker and the viewer's own toolbar all reach it, and a path that failed
    /// to open reaches none of them.
    /// </remarks>
    private void Remember(string path)
    {
        RecentFiles.Add(path);
        ShowRecent();
    }

    /// <summary>Fills File → Open Recent from the list on disk.</summary>
    /// <remarks>
    /// Rebuilt outright each time rather than edited: the list is short, and the item that moved to
    /// the front is the one thing about it that changes.
    /// </remarks>
    private void ShowRecent()
    {
        if (Item(NativeMenu.GetMenu(this), "Open Recent") is not { Menu: { } recent } item)
        {
            return;
        }

        recent.Items.Clear();

        foreach (var path in RecentFiles.Paths)
        {
            // The file's name, as every other Open Recent names one: a menu of full paths is wider
            // than the window and still trimmed on macOS.
            var entry = new NativeMenuItem(Path.GetFileName(path));

            entry.Click += async (_, _) => await OpenAsync(new[] { path }).ConfigureAwait(true);

            recent.Items.Add(entry);
        }

        // Nothing has been opened yet: an item that opens onto an empty menu reads as broken.
        item.IsEnabled = recent.Items.Count > 0;
    }

    private async void OnSettings(object? sender, EventArgs e) => await ShowSettingsAsync();

    /// <summary>The settings, as a window, and only ever one of them.</summary>
    /// <remarks>
    /// Held while it is open because it can be asked for twice: on macOS the application menu keeps
    /// its gesture live over the window it opened, and a second modal on the same owner is a dialog
    /// nobody can reach the first of.
    /// </remarks>
    private async Task ShowSettingsWindow()
    {
        if (_settings is { } open)
        {
            open.Activate();

            return;
        }

        var window = new SettingsWindow();

        _settings = window;

        try
        {
            await window.ShowDialog(this).ConfigureAwait(true);
        }
        finally
        {
            _settings = null;
        }
    }

    /// <summary>
    /// Shows the settings, and does what the window cannot about what was changed in it.
    /// </summary>
    /// <remarks>
    /// The window writes the setting itself and this asks it again afterwards, rather than the two
    /// of them agreeing a value between them: what has to happen when the copies are switched off is
    /// that the ones already kept go, and only the window that keeps them can do that.
    ///
    /// Public because the application menu on macOS is the application's and not this window's, so
    /// something outside has to be able to ask for it.
    /// </remarks>
    public async Task ShowSettingsAsync()
    {
        await ShowSettings().ConfigureAwait(true);

        if (!StudioSettings.Autosave)
        {
            _recovery?.Drop();
            ProjectRecovery.Clear();
        }

        // Every board, not only the one in front: a caption size is about the screen, and the tabs
        // behind this one are on the same screen.
        foreach (var board in _tabs.Items.OfType<TabItem>().Select(item => item.Content).OfType<GroupPanel>())
        {
            board.Recaption();
        }
    }

    private async void OnSave(object? sender, EventArgs e) => await SaveAsync();

    private async void OnSaveAs(object? sender, EventArgs e) => await SaveAsAsync();

    private async void OnExport(object? sender, EventArgs e) => await ExportAsync();

    private void OnUndo(object? sender, EventArgs e) => Undo();

    private void OnRedo(object? sender, EventArgs e) => Redo();

    /// <summary>Takes back the last edit, in whatever is being typed in.</summary>
    /// <remarks>
    /// A menu item's gesture is the window's on macOS, so it arrives here wherever the caret is —
    /// including a box in the parameter panel, which keeps its own stack and would otherwise have
    /// its keystroke taken by the drawing's.
    /// </remarks>
    /// <returns>Whether there was anything to take back.</returns>
    public bool Undo()
    {
        // A TextBox says nothing about whether it had anything to take back, so being the one that
        // was asked is the answer.
        if (Focused() is TextBox box)
        {
            box.Undo();

            return true;
        }

        return Board()?.Undo() ?? Selected()?.Undo() == true;
    }

    /// <inheritdoc cref="Undo"/>
    public bool Redo()
    {
        if (Focused() is TextBox box)
        {
            box.Redo();

            return true;
        }

        return Board()?.Redo() ?? Selected()?.Redo() == true;
    }

    /// <summary>The group tab being looked at, whose board has a move of its own to take back.</summary>
    /// <remarks>
    /// A group tab holds no drawing, so the Edit menu's Undo reached nothing over one and the drag
    /// that arranged a board could not be taken back at all.
    /// </remarks>
    private GroupPanel? Board() => (_tabs.SelectedItem as TabItem)?.Content as GroupPanel;

    private IInputElement? Focused() => FocusManager?.GetFocusedElement();

    /// <summary>
    /// Shows each command's gesture beside it, as the platform spells that gesture.
    /// </summary>
    /// <remarks>
    /// Read from the keymap rather than written down, so the menu cannot come to disagree with what
    /// the pane answers to — they are the same list. The first of the ones the platform names, since
    /// a menu item shows one and Redo has two.
    /// </remarks>
    /// <summary>Offers only what the selected tab can do.</summary>
    /// <remarks>
    /// Exporting is a drawing's, and a group tab holds none — the item stayed live over it and did
    /// nothing at all when it was picked, which reads as the export having failed silently.
    /// </remarks>
    private void UpdateMenu()
    {
        var menu = NativeMenu.GetMenu(this);

        if (Item(menu, "Export…") is { } export)
        {
            export.IsEnabled = Selected() is { Document: { } };
        }

        if (Item(menu, "Save") is { } save)
        {
            save.IsEnabled = Savable();
        }

        // Not among the items below: it is the application's settings and not a command on a
        // project, so it is live whether or not one is open — and absent on macOS, where the
        // application menu has it.
        if (Item(menu, "Settings…") is { } settings)
        {
            settings.IsVisible = !OperatingSystem.IsMacOS();
        }

        // Not for a drawing the project holds: Save As points a tab at the file it wrote, and one
        // of those has no file to be pointed at. Export writes it out instead.
        if (Item(menu, "Save As…") is { } saveAs)
        {
            saveAs.IsEnabled = Selected() is { Document: { } } && _tabs.SelectedItem is not TabItem { Tag: ProjectDrawing };
        }

        // Both act on the project, and both did nothing at all when picked without one.
        foreach (var header in new[] { "Build", "Close" })
        {
            if (Item(menu, header) is { } item)
            {
                item.IsEnabled = _workspace is { };
            }
        }
    }

    private void ShowMenuGestures()
    {
        if (this.GetPlatformSettings()?.HotkeyConfiguration is not { } hotkeys)
        {
            return;
        }

        Show("Undo", hotkeys.Undo);
        Show("Redo", hotkeys.Redo);

        // Written rather than read: the platform's keymap has no Save in it, so this is the one
        // gesture the menu and the window have to be told separately. OnKeyDown spells the same
        // modifier the same way.
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        if (Item(NativeMenu.GetMenu(this), "Save") is { } save)
        {
            save.Gesture = new KeyGesture(Key.S, command);
        }

        if (Item(NativeMenu.GetMenu(this), "Save As…") is { } saveAs)
        {
            saveAs.Gesture = new KeyGesture(Key.S, command | KeyModifiers.Shift);
        }

        void Show(string header, IReadOnlyList<KeyGesture> gestures)
        {
            if (gestures.Count > 0 && Item(NativeMenu.GetMenu(this), header) is { } item)
            {
                item.Gesture = gestures[0];
            }
        }
    }

    /// <summary>The menu item under <paramref name="menu"/> with this header.</summary>
    /// <remarks>
    /// By header, because a NativeMenuItem has no name to give it in the markup — it is not a
    /// control, and x:Name has nothing to bind to on one.
    /// </remarks>
    private static NativeMenuItem? Item(NativeMenu? menu, string header)
    {
        foreach (var entry in menu?.Items ?? new List<NativeMenuItemBase>())
        {
            if (entry is not NativeMenuItem item)
            {
                continue;
            }

            if (string.Equals(item.Header, header, StringComparison.Ordinal))
            {
                return item;
            }

            if (Item(item.Menu, header) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>Asks the viewer to resize the drawing, which is where the form and the edit live.</summary>
    private async void OnResize(object? sender, EventArgs e)
    {
        if (Selected() is { } viewer)
        {
            await viewer.ResizeAsync();
        }
    }

    /// <summary>
    /// Asks where the selected drawing goes and in which form, and writes it there.
    /// </summary>
    /// <remarks>
    /// One question, because the panel's own type list answers both halves of it: the suggested
    /// name is given without an extension, and the panel appends the one belonging to the type
    /// chosen in it. <see cref="FilePickerSaveOptions.DefaultExtension"/> is left unset for the
    /// same reason — measured on Avalonia 12.1.0, setting it to <c>svg</c> overrode the chosen
    /// type, and a name saved under "C# Files" came back as <c>.svg</c>.
    /// </remarks>
    /// <returns>Whether anything was written.</returns>
    public async Task<bool> ExportAsync()
    {
        if (Selected() is not { Document: { } } viewer || !StorageProvider.CanSave)
        {
            return false;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export drawing",
            SuggestedFileName = _tabs.SelectedItem is TabItem { Tag: ProjectDrawing drawing }
                ? drawing.Name
                : viewer.DocumentPath is { } path
                    ? Path.GetFileNameWithoutExtension(path)
                    : "drawing",
            FileTypeChoices = new List<FilePickerFileType> { SvgFileType, CSharpFileType }
        });

        return file?.TryGetLocalPath() is { Length: > 0 } target && await ExportAsync(target);
    }

    /// <summary>
    /// Writes the selected drawing to <paramref name="target"/>: as C# if it is named <c>.cs</c>,
    /// as SVG otherwise.
    /// </summary>
    /// <remarks>
    /// Taking the path rather than asking for it, so everything but the panel can be driven.
    /// </remarks>
    public async Task<bool> ExportAsync(string target)
    {
        if (Selected() is not { Document: { } document } viewer)
        {
            return false;
        }

        try
        {
            SvgExport.Write(document, viewer.Source, target, viewer.SizeRequest);
        }
        catch (Exception failure)
            when (failure is IOException or UnauthorizedAccessException or InvalidOperationException or ExprException)
        {
            // The drawing is still open and still fine; what failed is one command, so it is
            // reported rather than thrown out of a handler nothing is waiting on.
            await Ask("Export failed", failure.Message, null, "OK");
            return false;
        }

        return true;
    }

    // ---- lifetime ----------------------------------------------------------------------------

    /// <summary>
    /// How the window asks whether work that is not on disk may be thrown away.
    /// </summary>
    /// <remarks>
    /// Replaceable for the reason the file picker is: a modal is the one thing a test cannot drive.
    /// Given the whole sentence rather than a name, since closing can be about several drawings.
    /// </remarks>
    public Func<string, Task<bool>> ConfirmDiscard { get; set; }

    /// <summary>
    /// How the window says something there is nothing to answer.
    /// </summary>
    /// <remarks>
    /// Replaceable for the reason <see cref="ConfirmDiscard"/> is: a modal is the one thing a test
    /// cannot drive, and a build that reports itself would otherwise wait for a button nobody is
    /// there to press.
    /// </remarks>
    public Func<string, string, Task> Announce { get; set; }

    /// <summary>
    /// How the window asks whether a copy of unsaved work may be thrown away.
    /// </summary>
    /// <remarks>
    /// Replaceable for the reason <see cref="ConfirmDiscard"/> is, and answering the same way round:
    /// true throws the copy away. Restoring is the button that loses nothing, so it is the one the
    /// panel dismisses with — Enter and the close box both land on it.
    /// </remarks>
    public Func<string, Task<bool>> ConfirmDiscardRecovery { get; set; }

    /// <summary>
    /// How the window shows the settings.
    /// </summary>
    /// <remarks>
    /// Replaceable for the reason <see cref="ConfirmDiscard"/> is: a window shown over this one is
    /// another thing a test cannot drive. What a test wants from it is the setting, which the window
    /// writes, so a test sets that and answers this with nothing.
    /// </remarks>
    public Func<Task> ShowSettings { get; set; }

    /// <summary>
    /// How the window asks where a project that has no file yet should go.
    /// </summary>
    /// <remarks>
    /// Replaceable for the reason <see cref="ConfirmDiscard"/> is: a panel is the one thing a test
    /// cannot drive. Given the name to offer, and answering with the path chosen or null for a save
    /// nobody went through with.
    /// </remarks>
    public Func<string?, Task<string?>> AskWhereToSave { get; set; }

    /// <summary>
    /// How the window asks whether a PaintCode document may be converted, and on what terms.
    /// </summary>
    /// <remarks>
    /// Replaceable for the reason <see cref="Announce"/> is. Given the document alone: there is no
    /// second file to name, since what the conversion produces is held in the window until it is
    /// saved.
    /// </remarks>
    /// <returns>
    /// What the conversion was asked for, or null where the document is not to be converted.
    /// </returns>
    public Func<string, Task<(bool Integers, bool Organize)?>> ConfirmConvert { get; set; }

    /// <summary>
    /// How the window asks whether a branch of the project may go.
    /// </summary>
    /// <remarks>
    /// Its own rather than <see cref="ConfirmDiscard"/> widened: the two differ in their title and
    /// in both button labels, so one seam carrying the wording would take four arguments and every
    /// caller that only reads the message would have to pass values it ignores.
    /// </remarks>
    public Func<string, Task<bool>> ConfirmRemove { get; set; }

    /// <summary>
    /// How the window shows a file where it lives.
    /// </summary>
    /// <remarks>
    /// Replaceable for the reason <see cref="ConfirmDiscard"/> is: starting another program is the
    /// other thing a test cannot drive, and a suite that really opened Finder would leave a window
    /// per run behind it.
    /// </remarks>
    public Action<string> ShowOnDisk { get; set; }

    /// <summary>What the command is called here, since each desktop names its own file manager.</summary>
    private static string Revealing => OperatingSystem.IsMacOS()
        ? "Reveal in Finder"
        : OperatingSystem.IsWindows()
            ? "Reveal in File Explorer"
            : "Open Containing Folder";

    /// <summary>
    /// Shows a file where it lives, with the file itself picked out where the platform can.
    /// </summary>
    /// <remarks>
    /// Finder and Explorer both select a named file; a Linux desktop is only asked to open the
    /// directory, since there is no command every file manager answers to for the rest of it.
    /// Nothing is reported when it fails: the file manager is not the app's to answer for, and a
    /// dialog about one would be worse than the shrug.
    /// </remarks>
    private static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", new[] { "-R", path });
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start("explorer.exe", new[] { $"/select,{path}" });
            }
            else
            {
                Process.Start("xdg-open", new[] { Path.GetDirectoryName(path) ?? path });
            }
        }
        catch (Exception failure) when (failure is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
        }
    }

    /// <returns>Whether the tab closed, or false when its unsaved work was kept.</returns>
    private async Task<bool> CloseTabAsync(TabItem item)
    {
        // A close button is one click away from losing an edit, and nothing else would have said so.
        // Only what this tab is holding: the project's own unsaved work is not lost by closing a tab.
        if (Unsaved(item) is { } name && !await ConfirmDiscard(Describe(new[] { name }, null)))
        {
            return false;
        }

        CloseTab(item);

        return true;
    }

    /// <summary>Whether the close has already been answered for, so the second one goes through.</summary>
    private bool _closeConfirmed;

    /// <summary>
    /// Asks before the window takes every unsaved drawing with it.
    /// </summary>
    /// <remarks>
    /// Closing is synchronous and asking is not, so the close is called off, the question put, and
    /// the close started again once there is an answer.
    /// </remarks>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (_closeConfirmed || e.Cancel)
        {
            return;
        }

        var unsaved = Unsaved();
        var project = Unwritten;

        if (unsaved.Count == 0 && project is null)
        {
            return;
        }

        e.Cancel = true;

        // Posted, so the close finishes being called off first: a prompt that answered immediately
        // would re-enter Close from inside OnClosing, as a test's stub does.
        Dispatcher.UIThread.Post(async () => await ConfirmThenClose(Describe(unsaved, project)));
    }

    /// <summary>
    /// Leaves nothing behind for a window that closed on purpose.
    /// </summary>
    /// <remarks>
    /// What gives the copy its meaning: closing runs this and crashing does not, so a copy waiting
    /// on the next open is one Studio never got to throw away.
    /// </remarks>
    protected override void OnClosed(EventArgs e)
    {
        _recovery?.Drop();
        _recovery?.Stop();
        _recovery = null;

        base.OnClosed(e);
    }

    private async Task ConfirmThenClose(string message)
    {
        if (!await ConfirmDiscard(message))
        {
            return;
        }

        _closeConfirmed = true;

        Close();
    }

    /// <summary>The viewer in the selected tab, or null while there is none.</summary>
    private SvgViewer? Selected() => (_tabs.SelectedItem as TabItem)?.Content as SvgViewer;

    /// <summary>The tabs holding changes that are not on disk.</summary>
    private IReadOnlyList<string> Unsaved()
        => _tabs.Items.OfType<TabItem>()
            .Select(Unsaved)
            .Where(name => name is { })
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>The project's own unwritten work, named by its file, or null where there is none.</summary>
    /// <remarks>
    /// Beside the tabs rather than among them, because it is not one: a row dragged in the tree or a
    /// board arranged belongs to the project, and there is no tab to close to get it back.
    /// </remarks>
    private string? Unwritten => _workspace is { IsEdited: true } workspace ? workspace.Name : null;

    /// <summary>Whether the dot belongs on the window: work that is not on disk, wherever it sits.</summary>
    private bool Marked()
        => Unwritten is { } || (_tabs.SelectedItem is TabItem tab && Unsaved(tab) is { });

    /// <summary>Whether Save has anything to do.</summary>
    /// <remarks>
    /// Wider than the dot by one case: a project that has never been written can be saved even with
    /// nothing typed into it, because saving is how it gets a name. It wears no dot for that — there
    /// is nothing to lose — so the two questions stop being the same one here.
    /// </remarks>
    private bool Savable() => Marked() || _workspace is { Document.Path: null };

    /// <summary>
    /// What a tab is holding that is not on disk, named, or null when it is holding nothing.
    /// </summary>
    /// <remarks>
    /// A drawing's tab answers for two things: the drawing's own text, and the project settings
    /// riding in its right pane. Either of them unsaved is the tab unsaved.
    /// </remarks>
    private string? Unsaved(TabItem item) => item.Content switch
    {
        SvgViewer viewer when viewer.IsSourceModified || Settings(viewer) is { IsModified: true }
            => Named(viewer),
        GroupPanel panel when panel.IsModified => ProjectWorkspace.Label(panel.Node),
        _ => null
    };

    /// <summary>The project's say over the drawing a viewer is showing, when it came from a project.</summary>
    private static GroupPanel? Settings(SvgViewer viewer)
        => viewer.SidePanels.Select(pane => pane.Content).OfType<GroupPanel>().FirstOrDefault();

    private static TextBlock Marker(TabItem item) => (TextBlock)((StackPanel)item.Header!).Children[0];

    private static TextBlock Titled(TabItem item) => (TextBlock)((StackPanel)item.Header!).Children[1];

    /// <summary>Puts the name a node now reads under on the tab that is open on it.</summary>
    /// <remarks>
    /// The header was written once, when the tab was, so a namespace typed into a group renamed its
    /// row and left the tab open on that very row saying what the group used to be called. Only the
    /// settings tabs: a drawing's tab is its file name, which no setting renames.
    /// </remarks>
    private void Retitle()
    {
        foreach (var item in _tabs.Items.OfType<TabItem>())
        {
            if (item.Content is GroupPanel panel)
            {
                Titled(item).Text = ProjectWorkspace.Label(panel.Node);
            }
        }

        // The window title is that same label read off the selected tab, so it goes stale with it.
        UpdateTitle();
    }

    /// <summary>Puts the dot on the tab, or takes it off, according to what the tab is holding.</summary>
    private void Mark(TabItem item)
    {
        Marker(item).Classes.Set("unsaved", Unsaved(item) is { });

        UpdateTitle();

        // Save is offered by whether there is anything to save, which is what has just changed.
        UpdateMenu();
    }

    private string Named(SvgViewer viewer) => Tabbed(viewer) is { Tag: ProjectDrawing drawing }
        ? drawing.Name
        : viewer.DocumentPath is { } path ? Path.GetFileName(path) : "A drawing";

    /// <summary>The tab a viewer is the content of, or null where it is in none.</summary>
    private TabItem? Tabbed(SvgViewer viewer)
        => _tabs.Items.OfType<TabItem>().FirstOrDefault(item => ReferenceEquals(item.Content, viewer));



    /// <summary>What is about to be thrown away, in a sentence.</summary>
    /// <remarks>
    /// The project is named and the tabs are counted: it is one thing wearing a file's name, and
    /// counting it among them would be wrong twice over. "Tabs" and not "drawings", since a group's
    /// settings are unsaved work too and the window closes over both.
    /// </remarks>
    private static string Describe(IReadOnlyList<string> tabs, string? project) => (tabs.Count, project) switch
    {
        (0, { } named) => $"{named} has changes that have not been saved.",
        (1, { } named) => $"{named} and {tabs[0]} have changes that have not been saved.",
        (_, { } named) => $"{named} and {tabs.Count} tabs have changes that have not been saved.",
        (1, null) => $"{tabs[0]} has changes that have not been saved.",
        _ => $"{tabs.Count} tabs have changes that have not been saved."
    };

    /// <summary>Asks where a project goes, the first time anybody saves it.</summary>
    /// <remarks>
    /// <see cref="FilePickerSaveOptions.DefaultExtension"/> is set, unlike the drawing panel's,
    /// because there is one type here and nothing for it to override.
    /// </remarks>
    private async Task<string?> AskSaveProject(string? suggested)
    {
        if (StorageProvider is not { CanSave: true })
        {
            return null;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save project",
            SuggestedFileName = suggested ?? "Untitled.svgstudio",
            DefaultExtension = "svgstudio",
            FileTypeChoices = new List<FilePickerFileType> { StudioFileDialogService.Projects }
        }).ConfigureAwait(true);

        return file?.TryGetLocalPath() is { Length: > 0 } path ? path : null;
    }

    /// <summary>Asks whether edits that are not on disk may be thrown away.</summary>
    private Task<bool> AskDiscard(string message)
        => Ask("Unsaved changes", message, "Discard changes", "Keep editing");

    /// <summary>Says what opening a PaintCode document does, and asks the one question it cannot.</summary>
    /// <remarks>
    /// The integers box is here rather than in a setting because it is a guess about one document:
    /// PaintCode stores every number as a real, so a slider that happens to sit on whole ends is
    /// retyped along with the step enum this is for, and only the author knows which it was.
    /// </remarks>
    private async Task<(bool Integers, bool Organize)?> AskConvert(string source)
    {
        var integers = new CheckBox
        {
            Content = "Write whole numbers as integers",
            IsChecked = false
        };

        // On, so unticking it is the opt-out. A PaintCode document declares its variables once for
        // the whole library and the conversion gives every drawing a copy of what it uses; leaving
        // them that way is a project where no slider moves more than one drawing.
        var organize = new CheckBox
        {
            Content = "Automatically organize variables",
            IsChecked = true
        };

        var asked = await Ask(
            "Convert to a project",
            $"{Path.GetFileName(source)} is a PaintCode document. Opening it converts the document into a Svg "
                + "Studio project, which is held here until you save it — saving asks where it goes. The document "
                + "itself is left alone.",
            "Convert",
            "Cancel",
            new StackPanel
            {
                Spacing = 8d,
                Children = { integers, organize }
            }).ConfigureAwait(true);

        return asked ? (integers.IsChecked is true, organize.IsChecked is true) : null;
    }

    /// <summary>
    /// Puts a message up and waits for an answer.
    /// </summary>
    /// <remarks>
    /// One button when <paramref name="accept"/> is null: an export that failed is to be read, not
    /// answered, and a second button would offer a choice that is not there.
    /// </remarks>
    /// <param name="extra">
    /// A control put between the message and the buttons, for a question that is part of the answer
    /// rather than another dialog. The caller reads it once this returns.
    /// </param>
    /// <returns>Whether <paramref name="accept"/> was the answer.</returns>
    private async Task<bool> Ask(string title, string message, string? accept, string dismiss, Control? extra = null)
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8d,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20d),
                Spacing = 16d,
                Children =
                {
                    new TextBlock { Text = message, MaxWidth = 480d, TextWrapping = TextWrapping.Wrap },
                    buttons
                }
            }
        };

        if (extra is { })
        {
            ((StackPanel)dialog.Content).Children.Insert(1, extra);
        }

        if (accept is { })
        {
            var confirm = new Button { Content = accept };

            confirm.Click += (_, _) => dialog.Close(true);
            buttons.Children.Add(confirm);
        }

        var close = new Button { Content = dismiss, IsDefault = true };

        close.Click += (_, _) => dialog.Close(false);
        buttons.Children.Add(close);

        return await dialog.ShowDialog<bool>(this);
    }

    /// <summary>The picker, widened to the projects this window can also open.</summary>
    /// <remarks>
    /// The viewer's own offers drawings, which is all it can open. Given to every viewer so File →
    /// Open, a drop and the toolbar all reach the same set of files through the request the window
    /// already handles.
    /// </remarks>
    private sealed class StudioFileDialogService : ISvgViewerFileDialogService
    {
        /// <summary>
        /// Everything Open can do something with, which is what it offers first.
        /// </summary>
        /// <remarks>
        /// A picker shows one filter at a time and opens on the first — on macOS as a popup in the
        /// panel's accessory view, allowing only the types the chosen one names. Offering drawings
        /// first meant a project was greyed out until somebody thought to change a popup they had no
        /// reason to look at. Open takes either kind, so the filter it opens on says so.
        /// </remarks>
        private static readonly FilePickerFileType OpenableFileType = new("Drawings and projects")
        {
            Patterns = new[] { "*.svg", "*.svgz", "*.svgstudio", "*.svgcproj", "*.pcvd" },
            MimeTypes = new[] { "image/svg+xml", "application/gzip", "application/xml" }
        };

        /// <remarks>
        /// No Apple type identifier, unlike the drawings below. macOS decides a file's type from
        /// its extension, and <c>.svgstudio</c> is whatever the machine happens to have claimed it —
        /// on this one, the editor it was last opened with. Either way it conforms to nothing, and
        /// <c>public.xml</c> was a claim about it that no machine agrees with, narrowing the filter
        /// to files macOS reads as XML rather than widening it. The extension is what matches.
        ///
        /// The svgc project is here too, since Open takes one by converting it.
        /// </remarks>
        internal static readonly FilePickerFileType Projects = new("Svg Studio Projects")
        {
            Patterns = new[] { "*.svgstudio", "*.svgcproj" },
            MimeTypes = new[] { "application/xml" }
        };

        internal static readonly FilePickerFileType Drawings = new("Svg Files")
        {
            Patterns = new[] { "*.svg", "*.svgz" },
            AppleUniformTypeIdentifiers = new[] { "public.svg-image" },
            MimeTypes = new[] { "image/svg+xml", "application/gzip" }
        };

        /// <remarks>The pattern Avalonia's own "all files" uses; <c>*.*</c> asks for a dot.</remarks>
        private static readonly FilePickerFileType AllFileType = new("All")
        {
            Patterns = new[] { "*" },
            MimeTypes = new[] { "*/*" }
        };

        private readonly SvgViewerFileDialogService _drawings = new();

        public async Task<string?> OpenSvgAsync(TopLevel? owner)
        {
            var storage = owner?.StorageProvider;

            if (storage is null || !storage.CanOpen)
            {
                return null;
            }

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open drawing or project",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType> { OpenableFileType, Drawings, Projects, AllFileType }
            }).ConfigureAwait(true);

            return files?.Select(file => file.TryGetLocalPath())
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        }

        public Task<string?> SaveSvgAsync(TopLevel? owner, string? suggested)
            => _drawings.SaveSvgAsync(owner, suggested);
    }

    private void CloseTab(TabItem item)
    {
        _tabs.Items.Remove(item);
        _stale.Remove(item);

        // Nothing else disposes the documents a discarded tab is holding — a viewer's one, or the
        // whole group a board was built from, which is kept now while the tab is merely not on top.
        (item.Content as SvgViewer)?.Close();
        (item.Content as GroupPanel)?.Close();

        UpdateTitle();
        UpdateMenu();
    }

    /// <summary>Saves the drawing in the selected tab, or opens the find box over its text.</summary>
    /// <remarks>
    /// The modifier follows the platform rather than being spelled Control, so this is Cmd+S on
    /// macOS and Ctrl+S everywhere else, which is what each of them means by "save".
    /// </remarks>
    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        if (e.Key != Key.S)
        {
            return;
        }

        if (e.KeyModifiers == command)
        {
            e.Handled = true;

            await SaveAsync();
        }
        else if (e.KeyModifiers == (command | KeyModifiers.Shift))
        {
            e.Handled = true;

            await SaveAsAsync();
        }
    }

    /// <summary>Asks where to put the open drawing, and puts it there.</summary>
    private async Task<bool> SaveAsAsync()
    {
        if (Selected() is not { Document: { } document } viewer)
        {
            return false;
        }

        var picked = await new StudioFileDialogService()
            .SaveSvgAsync(this, document.Path is { } was ? Path.GetFileName(was) : null)
            .ConfigureAwait(true);

        return picked is { Length: > 0 } && await SaveAsAsync(picked).ConfigureAwait(true);
    }

    /// <summary>
    /// Writes the open drawing to <paramref name="target"/>, and points its tab at it.
    /// </summary>
    /// <remarks>
    /// Taking the path rather than asking for it, so everything but the panel can be driven, the
    /// same as <see cref="ExportAsync"/>.
    ///
    /// The text is the viewer's <c>Source</c> rather than its SaveSourceAsync, which puts up a file
    /// dialog of its own. Written through the document, so a file that came in with a byte order
    /// mark keeps it.
    ///
    /// Reading it back is what makes this Save As and not save-a-copy: the tab takes the new file's
    /// name and ⌘S writes there afterwards. It costs the drawing's undo history, which is what
    /// saving under a new name costs everywhere.
    /// </remarks>
    public async Task<bool> SaveAsAsync(string target)
    {
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        if (Selected() is not { Document: { } document } viewer)
        {
            return false;
        }

        try
        {
            document.Write(viewer.Source, target);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            await Announce("The drawing couldn't be saved", failure.Message).ConfigureAwait(true);

            return false;
        }

        await viewer.LoadAsync(target).ConfigureAwait(true);

        return true;
    }

    /// <summary>
    /// Hands what the selected tab is holding to the project, and writes the project.
    /// </summary>
    /// <remarks>
    /// One write, wherever the work was typed: the project is one file, so committing the tab and
    /// then writing once is the whole of a save. A tab still hands over only what was typed in it —
    /// what another tab is holding is in its own boxes and cannot be written from here — and the
    /// write carries whatever else the document has been given since, which is what a save has
    /// always carried.
    ///
    /// Public for the reason <see cref="ExportAsync"/> is: a way in without the keyboard.
    /// </remarks>
    public async Task SaveAsync()
    {
        var selected = _tabs.SelectedItem as TabItem;

        // A drawing with a file of its own: the viewer writes it, and no project is involved.
        var elsewhere = selected is { Tag: not ProjectNode, Content: SvgViewer };

        if (selected is { Tag: not ProjectNode, Content: SvgViewer alone })
        {
            await alone.SaveSourceAsync().ConfigureAwait(true);
        }
        else if (selected?.Content is GroupPanel panel)
        {
            panel.Commit();
        }
        else if (selected?.Content is SvgViewer viewer && _workspace is { } holder)
        {
            // Both halves, since both are the tab's: the project's say over the drawing, and its text.
            Settings(viewer)?.Commit();

            if (viewer.IsSourceModified && selected.Tag is ProjectDrawing drawing)
            {
                if (drawing.SetText(viewer.Source) is { } refusal)
                {
                    await Announce("The drawing couldn't be saved", refusal).ConfigureAwait(true);

                    return;
                }

                holder.Edit();
            }
        }

        if (_workspace is not { } workspace)
        {
            return;
        }

        // A project that is a file is written when it is holding something, since a save that
        // touched the file's date for no edit would be a change to a file nobody edited. One that is
        // not a file is written whether or not anything has been typed into it, because that is how
        // it gets a name — but never from another drawing's tab, which would answer a save of that
        // drawing with a panel about the project.
        if (workspace.Document.Path is { } ? !workspace.IsEdited : elsewhere)
        {
            return;
        }

        if (!await WriteAsync(workspace).ConfigureAwait(true))
        {
            return;
        }

        // The bytes are the project's now, so the tab is told rather than asked to write them:
        // SaveSourceAsync would go looking for a file this drawing has not got.
        if (selected is { Tag: ProjectDrawing, Content: SvgViewer held })
        {
            held.MarkSaved();
        }
    }

    /// <summary>
    /// Writes the project, asking where it goes when it has nowhere yet.
    /// </summary>
    /// <remarks>
    /// The one place a project is written, so the panel in front of it is asked once however the
    /// save was reached — a tab, the menu, or a build that needs somewhere to put its outputs.
    /// </remarks>
    /// <returns>Whether it was written.</returns>
    private async Task<bool> WriteAsync(ProjectWorkspace workspace)
    {
        string? target = null;

        if (workspace.Document.Path is null)
        {
            target = await AskWhereToSave(workspace.Suggested).ConfigureAwait(true);

            if (target is null)
            {
                return false;
            }
        }

        try
        {
            if (target is { })
            {
                workspace.Save(target);
            }
            else
            {
                workspace.Save();
            }
        }
        catch (Exception failure)
            when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await Announce("The project couldn't be saved", failure.Message).ConfigureAwait(true);

            return false;
        }

        return true;
    }

    private void UpdateTitle()
    {
        // Before the name, as on the tab: the two say the same thing about the same file and
        // should be read the same way round. The window answers for the project as well as for the
        // tab, since work dragged into the project wears no tab's mark.
        var mark = Marked() ? "• " : string.Empty;

        if ((_tabs.SelectedItem as TabItem)?.Content is GroupPanel group)
        {
            Title = $"{mark}{ProjectWorkspace.Label(group.Node)} — {group.Workspace.Name}";
            return;
        }

        if (_tabs.SelectedItem is TabItem { Tag: ProjectDrawing drawing } && _workspace is { } workspace)
        {
            Title = $"{mark}{drawing.Name} — {workspace.Name}";
            return;
        }

        Title = Selected()?.DocumentPath is { } path ? $"{mark}{Path.GetFileName(path)} — SVG viewer" : "SVG viewer";
    }
}
