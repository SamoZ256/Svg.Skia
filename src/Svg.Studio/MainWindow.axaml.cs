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
using Svg.CodeGen.Skia.Projects;
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

    /// <summary>The open project, or null. The window works on one at a time, as a workspace is.</summary>
    private ProjectWorkspace? _workspace;

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

        _tabs = this.FindControl<TabControl>("Tabs")!;
        _tabs.SelectionChanged += (_, _) => UpdateTitle();

        _projectTree = this.FindControl<TreeView>("ProjectTree")!;
        _projectColumn = this.FindControl<Grid>("Shell")!.ColumnDefinitions[0];
        _projectPaneHost = this.FindControl<Border>("ProjectPaneHost")!;
        _projectSplitter = this.FindControl<GridSplitter>("ProjectSplitter")!;
        _projectName = this.FindControl<TextBlock>("ProjectName")!;
        _projectTree.KeyDown += OnProjectTreeKeyDown;
        _tabs.TemplateApplied += OnTabsTemplateApplied;

        // On the strip, and tunnelling, because TabItem handles a press itself to become selected
        // and a bubbling handler would never see it.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        _tabs.AddHandler(PointerPressedEvent, OnTabPointerPressed, RoutingStrategies.Tunnel);
        _tabs.AddHandler(PointerMovedEvent, OnTabPointerMoved, RoutingStrategies.Tunnel);
        _tabs.AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Tunnel);
        _tabs.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(null));

        ShowMenuGestures();

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
                Children = { title, close }
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
            UpdateTitle();
        };

        // A dot rather than an asterisk: the tab already ends in a close button, and two marks
        // competing for the same corner read as one smudge.
        viewer.SourceModifiedChanged += (_, modified) =>
        {
            title.Text = modified ? name + " •" : name;
            UpdateTitle();
        };

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
            await Ask("The project couldn't be opened", failure.Message, null, "Close").ConfigureAwait(true);
            return;
        }

        var workspace = new ProjectWorkspace(document);

        _workspace = workspace;

        workspace.ModifiedChanged += (_, _) => UpdateTitle();

        // One setting decides what everything under it inherits, so the tree's names and the
        // drawings already open both have to follow it.
        workspace.Edited += (_, _) =>
        {
            BuildTree();
            Resize();
        };

        ShowProjectPane(true);
        BuildTree();
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

        if (workspace.IsModified && !await ConfirmDiscard(Describe(new[] { workspace.Name })).ConfigureAwait(true))
        {
            return false;
        }

        // The tabs are the project's, so they go with it rather than being left pointing at nothing.
        foreach (var item in _tabs.Items.OfType<TabItem>().Where(item => item.Tag is SvgcProjectNode).ToList())
        {
            if (item.Content is SvgViewer { IsSourceModified: true } editing
                && !await ConfirmDiscard(Describe(new[] { Named(editing) })).ConfigureAwait(true))
            {
                return false;
            }

            CloseTab(item);
        }

        _workspace = null;

        _projectTree.Items.Clear();
        ShowProjectPane(false);
        UpdateTitle();

        return true;
    }

    private async void OnCloseProject(object? sender, EventArgs e) => await CloseProjectAsync();

    private void ShowProjectPane(bool show)
    {
        _projectPaneHost.IsVisible = show;
        _projectSplitter.IsVisible = show;
        _projectColumn.Width = show ? new GridLength(260) : new GridLength(0);
        _projectColumn.MinWidth = show ? 180 : 0;
    }

    private void BuildTree()
    {
        if (_workspace is not { } workspace)
        {
            return;
        }

        _projectName.Text = workspace.Name;

        var selected = (_projectTree.SelectedItem as TreeViewItem)?.Tag;

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

            // The chevron folds; it does not open.
            if (e.Source is Visual source && source.FindAncestorOfType<ToggleButton>(true) is { })
            {
                return;
            }

            await ShowAsync(node);
        };

        if (node is SvgcProjectGroup group)
        {
            foreach (var child in group.Children)
            {
                item.Items.Add(Branch(child, selected));
            }
        }

        return item;
    }

    private async void OnProjectTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)
            || (_projectTree.SelectedItem as TreeViewItem)?.Tag is not SvgcProjectNode node)
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

        if (node is not SvgcProjectDrawing drawing)
        {
            AddNodeTab(new GroupPanel(workspace, node), node, ProjectWorkspace.Label(node));
            return;
        }

        var viewer = AddTab();

        if (_tabs.SelectedItem is TabItem item)
        {
            item.Tag = node;
        }

        viewer.SizeRequest = ProjectWorkspace.SizeOf(drawing);

        await viewer.LoadAsync(drawing.ResolvedInput).ConfigureAwait(true);
    }

    /// <summary>A tab for something that is not a drawing, which the viewer's own tab does not fit.</summary>
    private void AddNodeTab(Control content, SvgcProjectNode node, string name)
    {
        var title = new TextBlock { Classes = { "title" }, Text = name };
        var close = new Button { Classes = { "close" }, Content = "✕" };

        var item = new TabItem
        {
            Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { title, close }
            },
            Content = content,
            Tag = node
        };

        close.Click += async (_, _) => await CloseTabAsync(item);

        _tabs.Items.Add(item);
        _tabs.SelectedItem = item;

    }

    private TabItem? Tab(SvgcProjectNode node)
        => _tabs.Items.OfType<TabItem>().FirstOrDefault(item => ReferenceEquals(item.Tag, node));

    /// <summary>
    /// Rebuilds the open drawings at the size the project's settings now ask for.
    /// </summary>
    /// <remarks>
    /// A drawing with edits of its own in the source pane is left alone: reloading it would throw
    /// them away, and a resize is not worth that.
    /// </remarks>
    private void Resize()
    {
        foreach (var item in _tabs.Items.OfType<TabItem>())
        {
            if (item.Tag is not SvgcProjectDrawing drawing || item.Content is not SvgViewer viewer)
            {
                continue;
            }

            var request = ProjectWorkspace.SizeOf(drawing);

            if (request.Equals(viewer.SizeRequest) || viewer.IsSourceModified)
            {
                continue;
            }

            viewer.SizeRequest = request;

            _ = viewer.LoadAsync(drawing.ResolvedInput);
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
            SvgExport.Write(document, viewer.Source, target);
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

    private async Task CloseTabAsync(TabItem item)
    {
        // A close button is one click away from losing an edit, and nothing else would have said so.
        if (item.Content is SvgViewer { IsSourceModified: true } editing
            && !await ConfirmDiscard(Describe(new[] { Named(editing) })))
        {
            return;
        }

        CloseTab(item);
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

        var unsaved = UnsavedAll();

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
            .Select(item => item.Content switch
            {
                SvgViewer { IsSourceModified: true } viewer => Named(viewer),
                _ => null
            })
            .Where(name => name is { })
            .Select(name => name!)
            .ToList();

    private static string Named(SvgViewer viewer)
        => viewer.DocumentPath is { } path ? Path.GetFileName(path) : "A drawing";

    /// <summary>Every open drawing with changes that are not on disk, and the project if it has any.</summary>
    private IReadOnlyList<string> UnsavedAll()
    {
        var unsaved = Unsaved().ToList();

        if (_workspace is { IsModified: true } workspace)
        {
            unsaved.Add(workspace.Name);
        }

        return unsaved;
    }

    private static string Describe(IReadOnlyList<string> unsaved)
        => unsaved.Count == 1
            ? $"{unsaved[0]} has changes that have not been saved."
            : $"{unsaved.Count} drawings have changes that have not been saved.";

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

        private static readonly FilePickerFileType DrawingFileType = new("Svg Files")
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
                FileTypeFilter = new List<FilePickerFileType> { DrawingFileType, ProjectFileType, AllFileType }
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

        if ((_tabs.SelectedItem as TabItem)?.Content is GroupPanel { Workspace: { } project })
        {
            try
            {
                project.Save();
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                await Ask("The project couldn't be saved", failure.Message, null, "Close");
            }

            return;
        }

        if (Selected() is { } viewer)
        {
            await viewer.SaveSourceAsync();
        }
    }

    private void UpdateTitle()
    {
        if ((_tabs.SelectedItem as TabItem)?.Content is GroupPanel { Workspace: { } project } group)
        {
            var edited = project.IsModified ? " •" : string.Empty;

            Title = $"{ProjectWorkspace.Label(group.Node)} — {project.Name}{edited}";
            return;
        }

        var open = Selected();
        var path = open?.DocumentPath;
        var mark = open is { IsSourceModified: true } ? " •" : string.Empty;

        Title = path is { } ? $"{Path.GetFileName(path)}{mark} — SVG viewer" : "SVG viewer";
    }
}
