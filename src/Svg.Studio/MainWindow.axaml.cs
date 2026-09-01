using System;
using System.Collections.Generic;
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
using Svg.Expressions.Recipes;
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
    private readonly Border _dropLine;
    private readonly Grid _dropHost;

    /// <summary>The open project, or null. The window works on one at a time, as a workspace is.</summary>
    private ProjectWorkspace? _workspace;

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
        ConfirmRemove = message => Ask("Remove from the project", message, "Remove", "Cancel");
        Announce = (title, message) => Ask(title, message, null, "Close");

        _tabs = this.FindControl<TabControl>("Tabs")!;
        _tabs.SelectionChanged += (_, _) =>
        {
            UpdateTitle();
            UpdateMenu();
            Refill();
        };

        _projectTree = this.FindControl<TreeView>("ProjectTree")!;
        _projectColumn = this.FindControl<Grid>("Shell")!.ColumnDefinitions[0];
        _projectPaneHost = this.FindControl<Border>("ProjectPaneHost")!;
        _projectSplitter = this.FindControl<GridSplitter>("ProjectSplitter")!;
        _projectName = this.FindControl<TextBlock>("ProjectName")!;
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

        ShowMenuGestures();
        UpdateMenu();

        var viewer = AddTab();

        var startup = path is { } && File.Exists(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, "Assets", "parametric.svg");

        if (IsProject(startup))
        {
            _ = OpenProjectAsync(startup);
        }
        else if (File.Exists(startup))
        {
            _ = viewer.LoadAsync(startup);
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
    private async Task OpenAsync(SvgViewer source, IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (IsProject(path))
            {
                await OpenProjectAsync(path).ConfigureAwait(true);
                continue;
            }

            var viewer = source.Document is null ? source : AddTab();

            await viewer.LoadAsync(path).ConfigureAwait(true);
        }
    }

    /// <summary>Whether a path names an svgc project rather than a drawing.</summary>
    private static bool IsProject(string path)
        => Path.GetExtension(path).Equals(".svgcproj", StringComparison.OrdinalIgnoreCase);

    // ---- the project pane ---------------------------------------------------------------------

    /// <summary>The open project, for a test to read. Null while none is open.</summary>
    public ProjectWorkspace? Workspace => _workspace;

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
        if (!await CloseProjectAsync().ConfigureAwait(true))
        {
            return;
        }

        SvgcProjectDocument document;

        try
        {
            document = SvgcProjectDocument.Load(path);
        }
        catch (Exception failure) when (failure is SvgcProjectException or IOException or UnauthorizedAccessException)
        {
            await Announce("The project couldn't be opened", failure.Message).ConfigureAwait(true);
            return;
        }

        var workspace = new ProjectWorkspace(document);

        _workspace = workspace;

        // A saved setting decides what everything under it inherits, so the tree's names and the
        // drawings already open both have to follow it.
        workspace.Edited += (_, _) =>
        {
            BuildTree();
            Rebuild();
        };

        ShowProjectPane(true);
        BuildTree();
        UpdateMenu();
    }

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
        var owned = _tabs.Items.OfType<TabItem>().Where(item => item.Tag is SvgcProjectNode).ToList();
        var unsaved = owned.Select(Unsaved).Where(name => name is { }).Select(name => name!).ToList();

        if (unsaved.Count > 0 && !await ConfirmDiscard(Describe(unsaved)).ConfigureAwait(true))
        {
            return false;
        }

        // The tabs are the project's, so they go with it rather than being left pointing at nothing.
        foreach (var item in owned)
        {
            CloseTab(item);
        }

        _workspace = null;

        _projectTree.Items.Clear();
        ShowProjectPane(false);
        UpdateTitle();
        UpdateMenu();

        return true;
    }

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
        catch (Exception failure) when (failure is SvgcProjectException or SvgRecipeException or IOException or UnauthorizedAccessException)
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

    /// <summary>How many paths a build names before it starts counting instead.</summary>
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
    }

    /// <param name="select">The node to leave selected, or null to keep whatever was.</param>
    private void BuildTree(SvgcProjectNode? select = null)
    {
        if (_workspace is not { } workspace)
        {
            return;
        }

        _projectName.Text = workspace.Name;

        var selected = select ?? (_projectTree.SelectedItem as TreeViewItem)?.Tag;

        _projectTree.Items.Clear();
        _projectTree.Items.Add(Branch(workspace.Document.Root, selected));
    }

    /// <summary>One node and everything under it, expanded, since a project is a handful of rows.</summary>
    private TreeViewItem Branch(SvgcProjectNode node, object? selected)
    {
        var item = new TreeViewItem
        {
            Header = ProjectWorkspace.Label(node),
            Tag = node,
            IsExpanded = true,
            IsSelected = ReferenceEquals(node, selected)
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

        if (node is SvgcProjectGroup group)
        {
            foreach (var child in group.Children)
            {
                item.Items.Add(Branch(child, selected));
            }
        }

        return item;
    }

    /// <summary>What can be done to a row, on the row rather than in the menu bar.</summary>
    /// <remarks>
    /// Per row, so what is acted on is what was clicked — a right click does not select, and a menu
    /// reading the selection would act on whatever was opened last.
    /// </remarks>
    private ContextMenu Commands(SvgcProjectNode node)
    {
        var menu = new ContextMenu();

        Add("Add group", async () => await AddGroupAsync(node));
        Add("Add SVG…", async () => await AddDrawingAsync(node));

        // The project is the file; there is no tree left without it.
        if (node.Parent is { })
        {
            menu.Items.Add(new Separator());
            Add("Remove", async () => await RemoveAsync(node));
        }

        return menu;

        void Add(string header, Func<Task> command)
        {
            var item = new MenuItem { Header = header };

            item.Click += async (_, _) => await command();

            menu.Items.Add(item);
        }
    }

    /// <summary>Where something added beside <paramref name="node"/> goes: in a group, after a drawing.</summary>
    private static (SvgcProjectGroup Parent, int Index) Beside(SvgcProjectNode node)
        => node is SvgcProjectGroup group
            ? (group, group.Children.Count)
            : (node.Parent!, node.Parent!.Children.ToList().IndexOf(node) + 1);

    /// <summary>Adds an empty group, and opens it so it can be given a name.</summary>
    /// <remarks>Public for the reason <see cref="ExportAsync"/> is: a way in without the menu.</remarks>
    public async Task AddGroupAsync(SvgcProjectNode beside)
    {
        if (_workspace is not { } workspace)
        {
            return;
        }

        var (parent, index) = Beside(beside);
        var group = parent.AddGroup(index);

        workspace.Save();
        BuildTree(group);

        // A group with nothing on it is named by neither of its settings, so it opens as "group"
        // until one is typed — which is what the tab is for.
        await ShowAsync(group).ConfigureAwait(true);
    }

    /// <summary>Asks which drawing to add, and adds it.</summary>
    private async Task AddDrawingAsync(SvgcProjectNode beside)
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

        foreach (var path in files.Select(file => file.TryGetLocalPath()).Where(path => path is { Length: > 0 }))
        {
            await AddDrawingAsync(beside, path!).ConfigureAwait(true);
        }
    }

    /// <summary>Adds <paramref name="path"/> to the project, and opens it.</summary>
    /// <remarks>Taking the path rather than asking for it, so everything but the panel can be driven.</remarks>
    public async Task AddDrawingAsync(SvgcProjectNode beside, string path)
    {
        if (_workspace is not { } workspace)
        {
            return;
        }

        var (parent, index) = Beside(beside);
        var drawing = parent.AddDrawing(Carried(path, workspace.Document.BaseDirectory), index);

        workspace.Save();
        BuildTree(drawing);

        await ShowAsync(drawing).ConfigureAwait(true);
    }

    /// <summary>
    /// The path as the project should carry it: relative to the project's own directory.
    /// </summary>
    /// <remarks>
    /// A project that named an absolute path would build on the machine it was written on and
    /// nowhere else, so a walk out of the directory is kept in preference — it survives the whole
    /// tree being moved or cloned. Only a path with no relative form at all, which on Windows means
    /// another drive, stays as it came. Separators are written the way the format's own examples
    /// write them; Path.Combine reads those on Windows, where the reverse is not true.
    /// </remarks>
    private static string Carried(string path, string baseDirectory)
    {
        if (baseDirectory.Length == 0)
        {
            return path;
        }

        var relative = Path.GetRelativePath(baseDirectory, path);

        return Path.IsPathRooted(relative) ? path : relative.Replace('\\', '/');
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
    public async Task<bool> RemoveAsync(SvgcProjectNode node)
    {
        if (_workspace is not { } workspace || node.Parent is not { } parent)
        {
            return false;
        }

        // Only when it takes something with it. A row removed by mistake is one add away; a branch
        // is not, and there is no undo.
        if (node is SvgcProjectGroup { Children.Count: > 0 } group
            && !await ConfirmRemove(Removing(group)).ConfigureAwait(true))
        {
            return false;
        }

        foreach (var item in _tabs.Items.OfType<TabItem>()
                     .Where(item => item.Tag is SvgcProjectNode held && held.DescendsFrom(node))
                     .ToList())
        {
            if (!await CloseTabAsync(item).ConfigureAwait(true))
            {
                return false;
            }
        }

        parent.Remove(node);

        workspace.Save();
        BuildTree();

        return true;
    }

    /// <summary>
    /// The one drag this tree carries, so a file dropped on it is left to the viewer.
    /// </summary>
    /// <remarks>
    /// A bare name: Avalonia refuses an identifier with a separator in it, and built here rather
    /// than at the drag, where the refusal was an unhandled exception out of an async void handler
    /// and took the application with it. As a field it is a type initialiser instead, which is a
    /// failure every test that opens a window sees.
    /// </remarks>
    private static readonly DataFormat<string> RowFormat = DataFormat.CreateStringApplicationFormat("SvgcProjectNode");

    private SvgcProjectNode? _row;
    private PointerPressedEventArgs? _rowPressed;
    private Point _rowPressedAt;

    /// <summary>Whether the press that is finishing was a drag, so the tap it raises opens nothing.</summary>
    private bool _rowDragged;

    private SvgcProjectNode? _dropOn;
    private ProjectDrop _dropWhere;

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        _row = null;
        _rowPressed = null;
        _rowDragged = false;

        if (e.Source is not Visual source
            // The chevron folds the row; it does not pick it up.
            || source.FindAncestorOfType<ToggleButton>(true) is { }
            || source.FindAncestorOfType<TreeViewItem>(true)?.Tag is not SvgcProjectNode node
            // The project is the file. There is nowhere to put it.
            || node is SvgcProjectRoot
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

    private void OnRowDragOver(object? sender, DragEventArgs e)
    {
        if (_row is not { } dragged
            || e.Source is not Visual source
            || source.FindAncestorOfType<TreeViewItem>(true) is not { Tag: SvgcProjectNode node } item)
        {
            HideDrop();

            return;
        }

        e.DragEffects = DragDropEffects.Move;

        _dropOn = node;
        _dropWhere = Bands(e.GetPosition(item).Y, RowHeight(item), node);

        if (Landing(_dropOn, _dropWhere) is not { } landing || landing.Parent.DescendsFrom(dragged))
        {
            HideDrop();

            return;
        }

        ShowDrop(item);
    }

    private void OnRowDrop(object? sender, DragEventArgs e)
    {
        if (_row is { } dragged && _dropOn is { } target)
        {
            Move(dragged, target, _dropWhere);
        }

        HideDrop();
    }

    /// <summary>
    /// Moves a node to where a drop on <paramref name="target"/> puts it.
    /// </summary>
    /// <remarks>Public for the reason <see cref="ExportAsync"/> is: a way in without the pointer.</remarks>
    /// <returns>Whether it moved. A drop that would take a group into itself does not.</returns>
    public bool Move(SvgcProjectNode node, SvgcProjectNode target, ProjectDrop where)
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

        workspace.Save();
        BuildTree(node);

        return true;
    }

    /// <summary>Which group a drop lands in, and where among its children. Null when it lands nowhere.</summary>
    private static (SvgcProjectGroup Parent, int Index)? Landing(SvgcProjectNode target, ProjectDrop where)
    {
        if (where == ProjectDrop.Inside)
        {
            return target is SvgcProjectGroup group ? (group, group.Children.Count) : null;
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
    private static ProjectDrop Bands(double y, double height, SvgcProjectNode target)
    {
        if (target is not SvgcProjectGroup)
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

    private static string Removing(SvgcProjectGroup group)
    {
        var rows = group.Children.Count;

        return $"{ProjectWorkspace.Label(group)} holds {rows} {(rows == 1 ? "row" : "rows")}, "
               + "which will be removed with it. This cannot be undone.";
    }

    private async void OnProjectTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if ((_projectTree.SelectedItem as TreeViewItem)?.Tag is not SvgcProjectNode node)
        {
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
    public async Task ShowAsync(SvgcProjectNode node)
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

        if (node is SvgcProjectGroup group)
        {
            AddNodeTab(new GroupPanel(workspace, group), node, ProjectWorkspace.Label(node));
            return;
        }

        var drawing = (SvgcProjectDrawing)node;

        var viewer = AddTab();

        if (_tabs.SelectedItem is TabItem item)
        {
            item.Tag = node;

            // Beside the drawing's own parameters, in the pane a group keeps its settings in. The
            // class is the whole of it: a project usually builds one file several times, and what
            // tells those rows apart is settable nowhere else.
            var settings = new GroupPanel(workspace, drawing);

            settings.ModifiedChanged += (_, _) => Mark(item);

            viewer.SidePanelHeader = "Project";
            viewer.SidePanel = settings;
        }

        viewer.SizeRequest = ProjectWorkspace.SizeOf(drawing);
        Recipe(viewer, drawing);

        await viewer.LoadAsync(drawing.ResolvedInput).ConfigureAwait(true);
    }

    /// <summary>
    /// Puts the drawing's recipe on the viewer, so the preview is the document the project builds.
    /// </summary>
    /// <remarks>
    /// A recipe rewrites colours into expressions and declares the parameters that drive them, so a
    /// drawing under one looks nothing like its file — and the parameters the panel then offers are
    /// the recipe's, which is what makes them worth dragging. Without this the preview was the plain
    /// file while the build produced something else, and nothing said so.
    /// </remarks>
    private static void Recipe(SvgViewer viewer, SvgcProjectDrawing drawing)
    {
        viewer.Rewrite = null;
        viewer.Notice = null;

        if (drawing.EffectiveResolvedRecipe is not { } path)
        {
            return;
        }

        try
        {
            var recipe = SvgRecipe.Load(path);

            // Applied once here rather than taken on trust, because a recipe this drawing refuses —
            // one that already declares for itself, say — would otherwise fail inside the load, and
            // the tab would open empty instead of showing the drawing and the reason. It costs a
            // second read of a file the load is about to read anyway.
            SvgRecipeRewriter.Apply(File.ReadAllText(drawing.ResolvedInput), recipe);

            viewer.Rewrite = text => Rewritten(text, recipe);
        }
        catch (Exception failure)
            when (failure is SvgRecipeException or IOException or UnauthorizedAccessException)
        {
            viewer.Notice = $"{Path.GetFileName(path)} was not applied: {failure.Message}";
        }
    }

    /// <summary>The drawing as its recipe makes it, or as it is when the recipe will not have it.</summary>
    /// <remarks>
    /// Never throws: this runs on every keystroke in the source pane, and text edited into something
    /// the recipe refuses is shown as it is rather than freezing the picture where it was.
    /// </remarks>
    private static string Rewritten(string svgText, SvgRecipe recipe)
    {
        try
        {
            return SvgRecipeRewriter.Apply(svgText, recipe).Svg;
        }
        catch (SvgRecipeException)
        {
            return svgText;
        }
    }

    /// <summary>A tab for something that is not a drawing, which the viewer's own tab does not fit.</summary>
    private void AddNodeTab(Control content, SvgcProjectNode node, string name)
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
            Tag = node
        };

        close.Click += async (_, _) => await CloseTabAsync(item);

        if (content is GroupPanel panel)
        {
            panel.ModifiedChanged += (_, _) => Mark(item);
        }

        _tabs.Items.Add(item);
        _tabs.SelectedItem = item;

    }

    private TabItem? Tab(SvgcProjectNode node)
        => _tabs.Items.OfType<TabItem>().FirstOrDefault(item => ReferenceEquals(item.Tag, node));

    /// <summary>
    /// Reads the open drawings again as the project's settings now say to build them.
    /// </summary>
    /// <remarks>
    /// A drawing with edits of its own in the source pane is left alone: reloading it would throw
    /// them away, and following a setting is not worth that.
    /// </remarks>
    private void Rebuild()
    {
        foreach (var item in _tabs.Items.OfType<TabItem>())
        {
            if (item.Tag is not SvgcProjectDrawing drawing || item.Content is not SvgViewer viewer)
            {
                continue;
            }

            var request = ProjectWorkspace.SizeOf(drawing);

            // The input is editable too, so a tab can be left showing a file the row no longer names.
            var elsewhere = !string.Equals(viewer.DocumentPath, drawing.ResolvedInput, StringComparison.Ordinal);

            // A drawing under a recipe is read again whatever the settings say, rather than compared:
            // the recipe it names could have been changed, and so could the recipe file itself, and
            // no comparison here would see either.
            var derived = drawing.EffectiveResolvedRecipe is { } || viewer.Rewrite is { };

            if ((request.Equals(viewer.SizeRequest) && !elsewhere && !derived) || viewer.IsSourceModified)
            {
                continue;
            }

            viewer.SizeRequest = request;
            Recipe(viewer, drawing);

            if (ReferenceEquals(_tabs.SelectedItem, item))
            {
                _ = viewer.LoadAsync(drawing.ResolvedInput);
            }
            else
            {
                _stale.Add(item);
            }
        }
    }

    /// <summary>Reads the selected tab's drawing again, if it went out of date while out of sight.</summary>
    private void Refill()
    {
        if (_tabs.SelectedItem is not TabItem item
            || !_stale.Remove(item)
            || item.Content is not SvgViewer viewer
            || viewer.DocumentPath is not { } path)
        {
            return;
        }

        _ = viewer.LoadAsync(path);
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
        }
    }

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

        return Selected()?.Undo() ?? false;
    }

    /// <inheritdoc cref="Undo"/>
    public bool Redo()
    {
        if (Focused() is TextBox box)
        {
            box.Redo();

            return true;
        }

        return Selected()?.Redo() ?? false;
    }

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
            SuggestedFileName = viewer.DocumentPath is { } path
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
            when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
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
    /// How the window asks whether a branch of the project may go.
    /// </summary>
    /// <remarks>
    /// Its own rather than <see cref="ConfirmDiscard"/> widened: the two differ in their title and
    /// in both button labels, so one seam carrying the wording would take four arguments and every
    /// caller that only reads the message would have to pass values it ignores.
    /// </remarks>
    public Func<string, Task<bool>> ConfirmRemove { get; set; }

    /// <returns>Whether the tab closed, or false when its unsaved work was kept.</returns>
    private async Task<bool> CloseTabAsync(TabItem item)
    {
        // A close button is one click away from losing an edit, and nothing else would have said so.
        if (Unsaved(item) is { } name && !await ConfirmDiscard(Describe(new[] { name })))
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

        if (unsaved.Count == 0)
        {
            return;
        }

        e.Cancel = true;

        // Posted, so the close finishes being called off first: a prompt that answered immediately
        // would re-enter Close from inside OnClosing, as a test's stub does.
        Dispatcher.UIThread.Post(async () => await ConfirmThenClose(unsaved));
    }

    private async Task ConfirmThenClose(IReadOnlyList<string> unsaved)
    {
        if (!await ConfirmDiscard(Describe(unsaved)))
        {
            return;
        }

        _closeConfirmed = true;

        Close();
    }

    /// <summary>The viewer in the selected tab, or null while there is none.</summary>
    private SvgViewer? Selected() => (_tabs.SelectedItem as TabItem)?.Content as SvgViewer;

    /// <summary>Every open drawing with changes that are not on disk.</summary>
    private IReadOnlyList<string> Unsaved()
        => _tabs.Items.OfType<TabItem>()
            .Select(Unsaved)
            .Where(name => name is { })
            .Select(name => name!)
            .ToList();

    /// <summary>
    /// What a tab is holding that is not on disk, named, or null when it is holding nothing.
    /// </summary>
    /// <remarks>
    /// A drawing's tab answers for two things now: the drawing's own text, and the project settings
    /// riding in its right pane. Either one unsaved is the tab unsaved.
    /// </remarks>
    private static string? Unsaved(TabItem item) => item.Content switch
    {
        SvgViewer viewer when viewer.IsSourceModified || Settings(viewer) is { IsModified: true }
            => Named(viewer),
        GroupPanel panel when panel.IsModified => ProjectWorkspace.Label(panel.Node),
        _ => null
    };

    /// <summary>The project's say over the drawing a viewer is showing, when it came from a project.</summary>
    private static GroupPanel? Settings(SvgViewer viewer) => viewer.SidePanel as GroupPanel;

    private static TextBlock Marker(TabItem item) => (TextBlock)((StackPanel)item.Header!).Children[0];

    /// <summary>Puts the dot on the tab, or takes it off, according to what the tab is holding.</summary>
    private void Mark(TabItem item)
    {
        Marker(item).Classes.Set("unsaved", Unsaved(item) is { });

        UpdateTitle();
    }

    private static string Named(SvgViewer viewer)
        => viewer.DocumentPath is { } path ? Path.GetFileName(path) : "A drawing";



    private static string Describe(IReadOnlyList<string> unsaved)
        => unsaved.Count == 1
            ? $"{unsaved[0]} has changes that have not been saved."
            // Not "drawings": a group's settings are unsaved work too, and the window closes over
            // both.
            : $"{unsaved.Count} tabs have changes that have not been saved.";

    /// <summary>Asks whether edits that are not on disk may be thrown away.</summary>
    private Task<bool> AskDiscard(string message)
        => Ask("Unsaved changes", message, "Discard changes", "Keep editing");

    /// <summary>
    /// Puts a message up and waits for an answer.
    /// </summary>
    /// <remarks>
    /// One button when <paramref name="accept"/> is null: an export that failed is to be read, not
    /// answered, and a second button would offer a choice that is not there.
    /// </remarks>
    /// <returns>Whether <paramref name="accept"/> was the answer.</returns>
    private async Task<bool> Ask(string title, string message, string? accept, string dismiss)
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
        private static readonly FilePickerFileType ProjectFileType = new("Svgc Projects")
        {
            Patterns = new[] { "*.svgcproj" },
            AppleUniformTypeIdentifiers = new[] { "public.xml" },
            MimeTypes = new[] { "application/xml" }
        };

        internal static readonly FilePickerFileType Drawings = new("Svg Files")
        {
            Patterns = new[] { "*.svg", "*.svgz" },
            AppleUniformTypeIdentifiers = new[] { "public.svg-image" },
            MimeTypes = new[] { "image/svg+xml", "application/gzip" }
        };

        private static readonly FilePickerFileType AllFileType = new("All")
        {
            Patterns = new[] { "*.*" },
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
                FileTypeFilter = new List<FilePickerFileType> { Drawings, ProjectFileType, AllFileType }
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

        // Nothing else disposes the document a discarded viewer is holding.
        (item.Content as SvgViewer)?.Close();

        // The window keeps somewhere to open the next drawing rather than closing itself.
        if (_tabs.Items.Count == 0)
        {
            AddTab();
        }

        UpdateTitle();
    }

    /// <summary>Saves the drawing in the selected tab.</summary>
    /// <remarks>
    /// The modifier follows the platform rather than being spelled Control, so this is Cmd+S on
    /// macOS and Ctrl+S everywhere else, which is what each of them means by "save".
    /// </remarks>
    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        if (e.Key != Key.S || e.KeyModifiers != command)
        {
            return;
        }

        e.Handled = true;

        await SaveAsync();
    }

    /// <summary>
    /// Saves whatever the selected tab is holding.
    /// </summary>
    /// <remarks>Public for the reason <see cref="ExportAsync"/> is: a way in without the keyboard.</remarks>
    public async Task SaveAsync()
    {
        if ((_tabs.SelectedItem as TabItem)?.Content is GroupPanel panel)
        {
            try
            {
                panel.Save();
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                await Announce("The project couldn't be saved", failure.Message).ConfigureAwait(true);
            }

            return;
        }

        if (Selected() is not { } viewer)
        {
            return;
        }

        // Both halves, since both are the tab's: the drawing's text and the project's say over it.
        if (Settings(viewer) is { IsModified: true } settings)
        {
            try
            {
                settings.Save();
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                await Announce("The project couldn't be saved", failure.Message).ConfigureAwait(true);
            }
        }

        if (await viewer.SaveSourceAsync().ConfigureAwait(true))
        {
            Reread(viewer);
        }
    }

    /// <summary>
    /// Marks every other tab showing the same file as needing to be read again.
    /// </summary>
    /// <remarks>
    /// A project builds one drawing more than once, so the same file is often open in several tabs
    /// at once, each holding its own copy of it. A save in one left the others showing what the
    /// file used to say until they were closed and opened again. They are marked rather than read
    /// now because none of them is the tab on screen — only one can be — and reading a drawing into
    /// a viewer that is not presented is what left it blank.
    ///
    /// A tab holding edits of its own is left alone: reading the file again would throw them away,
    /// and two tabs disagreeing is the smaller harm.
    /// </remarks>
    private void Reread(SvgViewer saved)
    {
        if (saved.DocumentPath is not { } path)
        {
            return;
        }

        foreach (var item in _tabs.Items.OfType<TabItem>())
        {
            if (item.Content is SvgViewer viewer
                && !ReferenceEquals(viewer, saved)
                && !viewer.IsSourceModified
                && string.Equals(viewer.DocumentPath, path, StringComparison.Ordinal))
            {
                _stale.Add(item);
            }
        }
    }

    private void UpdateTitle()
    {
        // Before the name, as on the tab: the two say the same thing about the same file and
        // should be read the same way round.
        if ((_tabs.SelectedItem as TabItem)?.Content is GroupPanel group)
        {
            var edited = group.IsModified ? "• " : string.Empty;

            Title = $"{edited}{ProjectWorkspace.Label(group.Node)} — {group.Workspace.Name}";
            return;
        }

        var open = Selected();
        var path = open?.DocumentPath;
        var mark = _tabs.SelectedItem is TabItem tab && Unsaved(tab) is { } ? "• " : string.Empty;

        Title = path is { } ? $"{mark}{Path.GetFileName(path)} — SVG viewer" : "SVG viewer";
    }
}
