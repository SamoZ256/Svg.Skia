// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Svg.Expressions;
using Svg.Highlighting;
using Svg.Skia;
using Svg.SourceEditing;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// A drop-in SVG viewer: open a drawing, zoom and pan it, and drive the parameters it declares.
/// </summary>
/// <remarks>
/// Loading is the only thing that leaves the UI thread; binding a value evaluates a model that is
/// already there, and staying on the thread keeps two changes in the order they were made. Nothing
/// here blanks the drawing on an error — a failed load, a malformed block and a rejected value all
/// leave what is up where it was.
/// </remarks>
public partial class SvgViewer : UserControl, ISvgViewerDeclarationTarget
{
    private readonly SvgViewerCanvas _canvas;
    private readonly SvgViewerDeclarationPanel _panel;

    private readonly SvgViewerElementPanel _element;

    /// <summary>How many panes the host had when the strip was last filled.</summary>
    private int _hosted;
    private readonly Border _toolBar;
    private readonly Border _statusPanel;
    private readonly Border _panelHost;
    private readonly GridSplitter _splitter;
    private readonly TextBlock _statusText;
    private readonly TextBlock _zoomText;
    private readonly Grid _errorPanel;
    private readonly SelectableTextBlock _errorText;
    private readonly TextBlock _noteText;
    private readonly Border _sourceHost;
    private readonly GridSplitter _sourceSplitter;
    private readonly TextEditor _sourceEditor;
    private readonly SvgViewerSourceColorizer _sourceColorizer;
    private readonly SvgViewerSourceMarkers _sourceMarkers;
    private readonly ToggleButton _sourceButton;
    private readonly ToggleButton _boundsButton;
    private readonly Grid _body;
    private readonly Grid _drawing;
    private readonly Grid _side;
    private readonly Border _treeHost;
    private readonly GridSplitter _treeSplitter;
    private readonly SvgViewerElementTree _elementTree;

    /// <summary>What the panel's buttons do. Shared with any other host that shows one.</summary>
    private readonly SvgViewerDeclarationCommands _commands;
    private readonly ToggleButton _elementsButton;

    /// <summary>What the source pane's row was last set to, so hiding it can be undone.</summary>
    private GridLength _sourceHeight;

    /// <summary>What the panel's column was last set to, for the same reason.</summary>
    private GridLength _panelWidth;

    /// <summary>What the element tree's row was last set to, for the same reason.</summary>
    private GridLength _treeHeight;

    /// <summary>Whether the pane's text is stale — a document arrived, or the theme changed.</summary>
    private bool _sourceStale = true;

    /// <summary>What is wrong with the drawing, for whatever a pointer comes to rest on.</summary>
    private IReadOnlyList<SvgSourceDiagnostic> _sourceDiagnostics = Array.Empty<SvgSourceDiagnostic>();

    /// <summary>Whether the drawing has been analysed, which is not the same as being shown.</summary>
    private bool _sourceAnalysed;

    /// <summary>Where each element is written, for the text the pane is holding.</summary>
    private IReadOnlyDictionary<string, SvgSourceElement> _sourceElements =
        new Dictionary<string, SvgSourceElement>(StringComparer.Ordinal);

    /// <summary>Whether that has been worked out, which costs a second read of the whole file.</summary>
    private bool _sourceMapped;

    /// <summary>Whether the pane is showing less than the whole drawing, which it may not edit.</summary>
    private bool _sourceTruncated;

    /// <summary>Whether the text is being replaced rather than typed, so an edit is not a change.</summary>
    private bool _sourceLoading;

    /// <summary>
    /// Whether the editor is holding the open drawing, and is therefore the truth about it.
    /// </summary>
    /// <remarks>
    /// Not the same as being on screen: a parameter added from the panel fills the buffer without
    /// opening the pane, and from that moment the document is modified and can be saved.
    /// </remarks>
    private bool _sourceBuffered;

    /// <summary>What the modified flag last was, so the change can be raised rather than polled.</summary>
    private bool _sourceModified;

    /// <summary>Waits for typing to stop before rebuilding the drawing.</summary>
    /// <remarks>
    /// A timer rather than <see cref="RequestApply"/>'s per-frame coalescing, because rebuilding is
    /// whole-document: 18ms to parse a 132KB drawing, 13ms to split it and 12ms to check it.
    /// </remarks>
    private readonly DispatcherTimer _rebuild = new() { Interval = TimeSpan.FromMilliseconds(200d) };

    private SvgViewerDocument? _document;
    private IReadOnlyList<SvgViewerParameter> _rows = Array.Empty<SvgViewerParameter>();
    private int _loadVersion;
    private bool _applyQueued;
    private string? _notice;
    private IReadOnlyList<SvgViewerPane> _sidePanels = Array.Empty<SvgViewerPane>();

    public SvgViewer()
    {
        AvaloniaXamlLoader.Load(this);

        _canvas = this.FindControl<SvgViewerCanvas>("PART_Canvas")!;
        _panel = this.FindControl<SvgViewerDeclarationPanel>("PART_Declarations")!;
        _toolBar = this.FindControl<Border>("ToolBarPanel")!;
        _statusPanel = this.FindControl<Border>("StatusPanel")!;
        _panelHost = this.FindControl<Border>("DeclarationPanelHost")!;
        _splitter = this.FindControl<GridSplitter>("Splitter")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _zoomText = this.FindControl<TextBlock>("ZoomText")!;
        _errorPanel = this.FindControl<Grid>("ErrorPanel")!;
        _errorText = this.FindControl<SelectableTextBlock>("ErrorText")!;
        _noteText = this.FindControl<TextBlock>("NoteText")!;
        _sourceHost = this.FindControl<Border>("SourcePanelHost")!;
        _sourceSplitter = this.FindControl<GridSplitter>("SourceSplitter")!;
        _sourceEditor = this.FindControl<TextEditor>("SourceEditor")!;
        _sourceButton = this.FindControl<ToggleButton>("SourceButton")!;
        _boundsButton = this.FindControl<ToggleButton>("BoundsButton")!;
        _body = this.FindControl<Grid>("Body")!;
        _drawing = this.FindControl<Grid>("Drawing")!;
        _side = this.FindControl<Grid>("Side")!;
        _treeHost = this.FindControl<Border>("ElementTreeHost")!;
        _treeSplitter = this.FindControl<GridSplitter>("TreeSplitter")!;
        _elementTree = this.FindControl<SvgViewerElementTree>("PART_Elements")!;
        _elementsButton = this.FindControl<ToggleButton>("ElementsButton")!;

        _sourceHeight = _drawing.RowDefinitions[2].Height;
        _panelWidth = _body.ColumnDefinitions[2].Width;
        _treeHeight = _side.RowDefinitions[2].Height;

        this.FindControl<Button>("FitButton")!.Click += (_, _) => _canvas.Fit();
        this.FindControl<Button>("ActualSizeButton")!.Click += (_, _) => _canvas.ActualSize();
        this.FindControl<Button>("ResetButton")!.Click += (_, _) => _canvas.ResetView();
        this.FindControl<Button>("ZoomInButton")!.Click += (_, _) => _canvas.ZoomIn();
        this.FindControl<Button>("ZoomOutButton")!.Click += (_, _) => _canvas.ZoomOut();
        this.FindControl<Button>("ResetParametersButton")!.Click += (_, _) => ResetParameters();

        _sourceButton.IsCheckedChanged += (_, _) => ShowSource = _sourceButton.IsChecked == true;

        _elementsButton.IsChecked = true;
        _elementsButton.IsCheckedChanged += (_, _) => ShowElementTree = _elementsButton.IsChecked == true;

        // The drawing's own text, and its own buffer: an element's attribute belongs to the drawing.
        // Not through Write, which hands an edit to DeclarationTarget — under a recipe that is the
        // recipe, and a fill on a rect has no business being written there.
        _element = new SvgViewerElementPanel(PaneSource, Declarations, Splice, Values);

        _elementTree.MoveRequested = MoveElement;
        _elementTree.NewGroupRequested = NewGroup;

        _elementTree.Selected += (_, node) =>
        {
            OutlineElement(node);

            _element.Show(SourceAddress(node?.AddressKey));

            // Only where the text is already being read. Picking a row is about the drawing, and a
            // pane that threw itself open over it every time would be answering a question nobody
            // asked. A host that does mean to show the text calls RevealInSource itself.
            if (ShowSource)
            {
                RevealInSource(node);
            }

            ElementSelected?.Invoke(this, node?.Element);
        };

        _boundsButton.IsChecked = ShowBounds;
        _boundsButton.IsCheckedChanged += (_, _) => ShowBounds = _boundsButton.IsChecked == true;

        // The splitter colours, the marker draws, and both ask for a brush when they need one rather
        // than being handed a palette, so a theme change is a repaint and not a rebuild.
        _sourceColorizer = new SvgViewerSourceColorizer(SourceBrush);
        _sourceMarkers = new SvgViewerSourceMarkers(ErrorBrush, WarningBrush);

        _sourceEditor.TextArea.TextView.LineTransformers.Add(_sourceColorizer);
        _sourceEditor.TextArea.TextView.BackgroundRenderers.Add(_sourceMarkers);
        _sourceEditor.TextArea.TextView.PointerHover += OnSourceHover;
        _sourceEditor.TextArea.TextView.PointerHoverStopped += (_, _) => HideSourceTip();
        _sourceEditor.TextChanged += (_, _) => OnSourceEdited();

        _rebuild.Tick += (_, _) =>
        {
            _rebuild.Stop();
            RebuildFromSource();
        };

        _canvas.ViewChanged += (_, _) => UpdateZoomText();
        _canvas.Picked += (_, at) => PickElement(at);
        _panel.ValueChanged += (_, _) => RequestApply();

        // Fired and forgotten: a click is not something to await, and the two report what they did
        // through the note and the drawing like every other edit.
        _commands = new SvgViewerDeclarationCommands(
            Declarations,
            Write,
            () => _rows,
            () => ParameterDialogService);

        _panel.AddRequested += async (_, _) => await AddParameterAsync().ConfigureAwait(true);
        _panel.CommitRequested += (_, _) => CommitParameterDefaults();
        _panel.EditRequested += async (_, row) => await EditParameterAsync(row).ConfigureAwait(true);
        _panel.RemoveRequested += (_, row) => RemoveParameter(row);
        _panel.LetCommitted += (_, let) => CommitLet(let);
        _panel.LetMoveRequested = MoveLet;
        _panel.ParameterMoveRequested = MoveParameter;
        _panel.LetRemoveRequested += (_, let) => RemoveLet(let);

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // A repaint rather than a reload: rebuilding the document would send the reader back to the
        // top of the file because the theme changed under them.
        ActualThemeVariantChanged += (_, _) => PaintSource();

        UpdateZoomText();
        UpdateStatus();
        ShowSource = false;

        // The strip exists from the start, not from a host setting a pane: the Element tab is the
        // viewer's own, and a viewer nobody has given panes to still has elements to pick.
        FillPanelHost();
    }

    /// <summary>Raised once a document has loaded and its parameters are built.</summary>
    public event EventHandler<SvgViewerDocument>? DocumentOpened;

    /// <summary>Raised for anything the user should see, with the message already formatted.</summary>
    public event EventHandler<string>? ErrorRaised;

    /// <summary>Raised after a value change has been bound to the drawing.</summary>
    public event EventHandler<SvgViewerParameter>? ParameterValueChanged;

    /// <summary>
    /// Raised when the user asks for files — picked or dropped — before any of them is read.
    /// </summary>
    /// <remarks>
    /// The viewer holds one document, so opening replaces what is up. A host showing several marks
    /// the request handled and places the paths itself.
    /// </remarks>
    public event EventHandler<SvgViewerOpenRequestedEventArgs>? OpenRequested;

    /// <summary>How the viewer asks for a file. Replaceable, and faked in tests.</summary>
    public ISvgViewerFileDialogService FileDialogService { get; set; } = new SvgViewerFileDialogService();

    /// <summary>
    /// The size to build the drawing at, or none to take the size it was written with.
    /// </summary>
    /// <remarks>
    /// For a host that decides a drawing's size elsewhere — an svgc project, whose group says what
    /// its drawings are built at. Applied to the parsed document on every build, so it survives an
    /// edit in the source pane; the file itself is never resized, which is what separates this from
    /// <c>Edit → Resize…</c>.
    /// </remarks>
    public SvgSizeRequest SizeRequest { get; set; } = SvgSizeRequest.None;

    /// <summary>
    /// What a drawing's text goes through before it is drawn, or null to draw it as written.
    /// </summary>
    /// <remarks>
    /// For a host whose drawing is derived from its file — an svgc project applying a recipe, where
    /// what is built is not what the file says. The pane still shows and saves the file itself, so
    /// a document set up this way declares things its own text does not; where the panel's commands
    /// should write those is <see cref="DeclarationTarget"/>.
    ///
    /// Applies to a drawing loaded from a path, and to every rebuild of it. It takes effect on the
    /// next load, which is the caller's to make.
    /// </remarks>
    public Func<string, string>? Rewrite { get; set; }

    /// <summary>How the viewer asks what parameter to declare. Replaceable, and faked in tests.</summary>
    public ISvgViewerParameterDialogService ParameterDialogService { get; set; } = new SvgViewerParameterDialogService();

    public ISvgViewerResizeDialogService ResizeDialogService { get; set; } = new SvgViewerResizeDialogService();

    public SvgViewerDocument? Document => _document;

    public SKSvg? Svg => _document?.Svg;

    public string? DocumentPath => _document?.Path;

    public IReadOnlyList<SvgViewerParameter> Parameters => _rows;

    /// <summary>The let rows, including any row still being filled in.</summary>
    public IReadOnlyList<SvgViewerLet> Lets => _panel.Lets;

    public SvgViewerCanvas Canvas => _canvas;

    public bool ShowToolBar
    {
        get => _toolBar.IsVisible;
        set => _toolBar.IsVisible = value;
    }

    /// <inheritdoc cref="SvgViewerCanvas.ShowBounds"/>
    public bool ShowBounds
    {
        get => _canvas.ShowBounds;
        set
        {
            _canvas.ShowBounds = value;
            _boundsButton.IsChecked = value;
        }
    }

    public bool ShowStatusBar
    {
        get => _statusPanel.IsVisible;
        set => _statusPanel.IsVisible = value;
    }

    /// <summary>
    /// Panels of the host's own, shown in the right pane beside the parameters.
    /// </summary>
    /// <remarks>
    /// The pane becomes a strip of tabs while there are any and holds the parameters alone again
    /// when there are none, so a host that sets nothing sees what it always saw. The host's come
    /// first, in the order given, and so the pane opens on the first of them: a host sets a panel
    /// because it has something to say about the drawing, and one filed behind a tab nobody clicks
    /// may as well not be there. See <see cref="SvgViewerPane"/> for what belongs in one.
    /// </remarks>
    public IReadOnlyList<SvgViewerPane> SidePanels
    {
        get => _sidePanels;
        set
        {
            var panes = value ?? Array.Empty<SvgViewerPane>();

            // The same panes said again change nothing, and rebuilding the strip over somebody
            // working in it is not nothing: a host that recomposes this whenever its own settings
            // are saved took the reader back to the first tab every time.
            if (Same(_sidePanels, panes))
            {
                return;
            }

            _sidePanels = panes;

            FillPanelHost();
        }
    }

    private static bool Same(IReadOnlyList<SvgViewerPane> panes, IReadOnlyList<SvgViewerPane> others)
        => panes.Count == others.Count
           && panes.Zip(others).All(
               pair => ReferenceEquals(pair.First.Content, pair.Second.Content)
                       && string.Equals(pair.First.Header, pair.Second.Header, StringComparison.Ordinal));

    private void FillPanelHost()
    {
        // What was being looked at, so a strip that gains or loses a pane does not also change the
        // subject. Only by name: the pane it was is not always one of the panes it now is.
        //
        // Except where the host had none and now has some. The viewer's own tabs are what a strip
        // falls back to rather than what somebody chose, and keeping one selected would file a
        // host's first pane behind them — a host sets one because it has something to say.
        var looking = _hosted > 0 && _panelHost.Child is TabControl showing && showing.SelectedItem is TabItem selected
            ? selected.Header as string
            : null;

        _hosted = _sidePanels.Count;

        // Emptied first, and the tabs with it: a control cannot be added to a second parent, and
        // the parameters panel is moving between the host and a tab inside it.
        if (_panelHost.Child is TabControl open)
        {
            foreach (var item in open.Items.OfType<TabItem>())
            {
                item.Content = null;
            }
        }

        _panelHost.Child = null;

        // A strip even with no panes from the host: what the drawing declares and what the picked
        // element is written with are two things, and the second has to be reachable. An embedder
        // that had a bare parameters panel gains a strip of two.
        var tabs = new TabControl { Classes = { "panes" }, Padding = new Thickness(0) };

        foreach (var pane in _sidePanels)
        {
            tabs.Items.Add(new TabItem { Header = pane.Header, Content = pane.Content });
        }

        tabs.Items.Add(new TabItem { Header = "Parameters", Content = _panel });
        tabs.Items.Add(new TabItem { Header = "Element", Content = _element });

        if (looking is { }
            && tabs.Items.OfType<TabItem>().FirstOrDefault(item => Equals(item.Header, looking)) is { } again)
        {
            tabs.SelectedItem = again;
        }

        _panelHost.Child = tabs;
    }

    public bool ShowDeclarationPanel
    {
        get => _panelHost.IsVisible;
        set
        {
            if (_panelHost.IsVisible == value)
            {
                return;
            }

            // The column carries the width, so hiding the panel has to zero it — and its minimum
            // with it — or the drawing keeps paying for a strip it cannot see. What the splitter was
            // dragged to comes back. The element tree is in the same column and goes with it: what
            // this hides is the whole right-hand strip, not one pane of it. The strip is the full
            // height of the viewer, so this is the width of everything but the drawing and its text.
            if (value)
            {
                _body.ColumnDefinitions[2].MinWidth = PanelMinimum;
                _body.ColumnDefinitions[2].Width = _panelWidth;
            }
            else
            {
                _panelWidth = _body.ColumnDefinitions[2].Width;
                _body.ColumnDefinitions[2].MinWidth = 0d;
                _body.ColumnDefinitions[2].Width = new GridLength(0d);
            }

            _panelHost.IsVisible = value;
            _splitter.IsVisible = value;
        }
    }

    /// <summary>The narrowest the panel is worth being, matching what the markup declares.</summary>
    private const double PanelMinimum = 260d;

    /// <summary>
    /// Whether the drawing's elements are listed under the parameters.
    /// </summary>
    /// <remarks>
    /// On, unlike <see cref="ShowSource"/>: what a drawing is made of is the question a viewer is
    /// opened to answer, and a pane nobody finds answers nothing. A host that wants the height back
    /// turns it off. Hidden with the whole column by <see cref="ShowDeclarationPanel"/>.
    /// </remarks>
    public bool ShowElementTree
    {
        get => _treeHost.IsVisible;
        set
        {
            if (_treeHost.IsVisible == value)
            {
                return;
            }

            // The row carries the height, the way the source pane's does: hiding the border alone
            // would leave the parameters paying for a strip of nothing.
            if (value)
            {
                _side.RowDefinitions[2].Height = _treeHeight;
            }
            else
            {
                _treeHeight = _side.RowDefinitions[2].Height;
                _side.RowDefinitions[2].Height = new GridLength(0d);
            }

            _treeHost.IsVisible = value;
            _treeSplitter.IsVisible = value;
            _elementsButton.IsChecked = value;

            // Filled on the way up and emptied on the way down, which is what makes turning it off
            // worth anything.
            UpdateElementTree();
        }
    }

    /// <summary>The tree of the open drawing's elements.</summary>
    public SvgViewerElementTree Elements => _elementTree;

    /// <summary>The element picked in the tree, or null when none is.</summary>
    public SvgElement? SelectedElement => _elementTree.SelectedNode?.Element;

    /// <summary>Raised when the picked element changes, with null when the pick is dropped.</summary>
    public event EventHandler<SvgElement?>? ElementSelected;

    /// <summary>
    /// Selects the row for whatever was clicked at <paramref name="at"/>.
    /// </summary>
    /// <remarks>
    /// A click that lands on nothing changes nothing. Clearing the selection is the design tool's
    /// convention and it is the wrong one here: the pane exists to be read alongside the drawing,
    /// and a click that missed by two pixels would throw away the row and the place in the text
    /// somebody was looking at.
    ///
    /// What is picked is the element that was drawn, so clicking a shape placed by <c>&lt;use&gt;</c>
    /// selects the definition it was drawn from — which is where it is written, and the only row
    /// there is for it.
    /// </remarks>
    private void PickElement(Point at)
    {
        if (_document is not { } open
            || !_canvas.TryGetDrawingPoint(at, out var point)
            || open.Svg.HitTestTopmostElement(new ShimSkiaSharp.SKPoint(point.X, point.Y)) is not { } element)
        {
            return;
        }

        _elementTree.TrySelect(SvgElementAddress.Create(element).Key);
    }

    /// <summary>
    /// Rings <paramref name="node"/> on the drawing, or clears the ring when there is nothing to ring.
    /// </summary>
    /// <remarks>
    /// Anything that never reaches the drawing — everything under <c>&lt;defs&gt;</c>, the
    /// <c>&lt;e:code&gt;</c> block, a <c>&lt;title&gt;</c> — rings nothing while its row still
    /// selects and is still shown in the text.
    ///
    /// The drawing sits at the origin here, so what <see cref="SvgViewerOutline"/> traces needs no
    /// offsetting before the canvas is given it.
    /// </remarks>
    /// <summary>Moves a row to where it was dropped, and follows it.</summary>
    /// <remarks>
    /// The rebuild is asked for rather than waited for. It is debounced by 200ms so that typing does
    /// not recompile per keystroke, and a drop that let it run late would leave the row under the
    /// pointer where it was until the timer caught up.
    /// </remarks>
    private bool MoveElement(string addressKey, string targetKey, SvgElementDrop where)
        => Rewritten(SvgElementEditor.Move(PaneSource(), addressKey, targetKey, where), targetKey);

    private bool NewGroup(string targetKey, SvgElementDrop where)
        => Rewritten(SvgElementEditor.NewGroup(PaneSource(), targetKey, where), targetKey);

    private bool Rewritten(SvgSourceEditResult result, string follow)
    {
        if (!Writable() || !Splice(result))
        {
            return false;
        }

        _rebuild.Stop();
        RebuildFromSource();

        // Where the row landed is not where it was, and the addresses after it have all shifted, so
        // the row it went beside is what can still be pointed at.
        _elementTree.TrySelect(follow);

        return true;
    }

    /// <summary>Whether an edit can be written into the pane, saying why not where it cannot.</summary>
    /// <remarks>
    /// The buffer first: Splice writes into the editor's document, and until it has been filled that
    /// is the empty one AvaloniaEdit starts with — the pane need never have been opened. A drawing
    /// past the pane's limit is shown cut, and writing a span measured against the whole of it would
    /// behead the file.
    /// </remarks>
    private bool Writable()
    {
        EnsureSourceBuffer();

        if (!_sourceTruncated)
        {
            return true;
        }

        ShowNote("This drawing is too large to edit here.");

        return false;
    }

    private void OutlineElement(SvgViewerElementNode? node)
        => _canvas.Highlight = Outline(node);

    /// <summary>Traces the selected element again, for a value that moved where it is drawn.</summary>
    private void RetraceOutline() => _canvas.Retrace(Outline(_elementTree.SelectedNode));

    private SkiaSharp.SKPath? Outline(SvgViewerElementNode? node)
        => node is null || _document is not { } open
            ? null
            : SvgViewerOutline.Of(open.Svg, node.Element);

    /// <summary>
    /// Shows where <paramref name="node"/> is written, opening the source pane to do it.
    /// </summary>
    /// <remarks>
    /// Opens the pane, because calling this is asking to be shown the text. Picking a row in the
    /// tree does not call it unless the pane is already open.
    ///
    /// The name is checked before the caret moves. The text and the document are read separately and
    /// nothing correlates them, so the one failure worth engineering against is scrolling somebody
    /// confidently to the wrong line; not moving at all is a fine second best. It is also what
    /// happens for a drawing too large for the pane to hold, which is shown cut and will not parse.
    /// </remarks>
    /// <returns>Whether the element could be placed in the text.</returns>
    public bool RevealInSource(SvgViewerElementNode? node)
    {
        if (node is null || _document is null)
        {
            return false;
        }

        ShowSource = true;
        EnsureSourceBuffer();

        if (!SourceElements().TryGetValue(node.AddressKey, out var placed)
            || !string.Equals(placed.Name, node.Label, StringComparison.Ordinal)
            || _sourceEditor.Document is not { } text
            || placed.Start + placed.Length > text.TextLength)
        {
            return false;
        }

        _sourceEditor.Select(placed.Start, placed.Length);

        var at = text.GetLocation(placed.Start);

        _sourceEditor.ScrollTo(at.Line, at.Column);

        return true;
    }

    /// <summary>
    /// Whether the drawing's text is shown under it.
    /// </summary>
    /// <remarks>
    /// A pane rather than a window, because an embedder owns its windows and cannot place or
    /// suppress one that opens unbidden.
    /// </remarks>
    public bool ShowSource
    {
        get => _sourceHost.IsVisible;
        set
        {
            if (_sourceHost.IsVisible == value)
            {
                return;
            }

            // The row carries the height, so hiding the pane has to zero it or the drawing keeps
            // paying for a strip it cannot see. What the splitter was dragged to comes back. The row
            // is the drawing's column alone, so showing the text costs the canvas its height and
            // costs the side panes nothing.
            if (value)
            {
                _drawing.RowDefinitions[2].Height = _sourceHeight;
            }
            else
            {
                _sourceHeight = _drawing.RowDefinitions[2].Height;
                _drawing.RowDefinitions[2].Height = new GridLength(0d);
            }

            _sourceHost.IsVisible = value;
            _sourceSplitter.IsVisible = value;
            _sourceButton.IsChecked = value;

            RenderSource();
        }
    }

    /// <summary>
    /// Opens the source pane's own find box, over the text it is showing.
    /// </summary>
    /// <remarks>
    /// AvaloniaEdit installs the box with the editor's template and styles it out of the theme the
    /// pane already includes, so all there is to do is show it. The pane comes up first because the
    /// buffer is only filled while it is visible — searching a closed pane would search nothing.
    /// </remarks>
    public void FindInSource()
    {
        ShowSource = true;

        // The box comes with the editor's template, and a control in a pane that has never been
        // open has never been measured and so has no template yet: without this the first search
        // after opening the pane found nothing to open, and only a second one worked.
        _sourceEditor.ApplyTemplate();

        if (_sourceEditor.SearchPanel is { } panel)
        {
            panel.Open();

            // The keystroke goes to the box rather than to the text under it, which is the whole
            // point of asking for it.
            panel.Reactivate();
        }
    }

    /// <summary>
    /// What is wrong with the open drawing, as ranges into <see cref="SvgViewerDocument.SourceText"/>.
    /// </summary>
    /// <remarks>
    /// Analysed on first ask, not only when the pane opens: the error panel needs to know whether a
    /// failed binding is the drawing's fault before anyone asks to read it.
    /// </remarks>
    /// <summary>
    /// A standing sentence from the host about the open drawing, said with the viewer's own.
    /// </summary>
    /// <remarks>
    /// For trouble only the host can see — an svgc project whose recipe will not apply to this
    /// drawing, so what is on screen is not what the project builds. The status line under the
    /// drawing is the only place already saying that kind of thing about it.
    /// </remarks>
    public string? Notice
    {
        get => _notice;
        set
        {
            _notice = string.IsNullOrEmpty(value) ? null : value;
            ShowTrouble();
        }
    }

    public IReadOnlyList<SvgSourceDiagnostic> SourceDiagnostics => Diagnostics();

    /// <summary>The whole drawing as text, including the edits the pane is holding.</summary>
    /// <remarks>
    /// Not what the pane shows: a drawing past <see cref="SourceLimit"/> is shown cut and cannot be
    /// edited, and handing out the cut would behead whatever it was written to.
    /// </remarks>
    public string Source
        => _sourceBuffered && !_sourceTruncated ? _sourceEditor.Text : _document?.SourceText ?? string.Empty;

    /// <summary>The values currently bound, keyed by parameter name.</summary>
    public IReadOnlyDictionary<string, ExprValue> ParameterValues => BuildValues();

    // ---- loading ------------------------------------------------------------------------------

    public async Task<bool> OpenAsync()
    {
        var path = await FileDialogService.OpenSvgAsync(TopLevel.GetTopLevel(this)).ConfigureAwait(true);

        return path is { } && await OpenAsync(new[] { path }).ConfigureAwait(true);
    }

    /// <summary>
    /// Opens the first path that loads, unless a host takes the request.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="LoadAsync(string)"/> because this is the user asking, which is what
    /// <see cref="OpenRequested"/> is about. A handled request returns true: only the host knows what
    /// became of each path.
    /// </remarks>
    public async Task<bool> OpenAsync(IReadOnlyList<string> paths)
    {
        var request = new SvgViewerOpenRequestedEventArgs(paths);

        OpenRequested?.Invoke(this, request);

        if (request.Handled)
        {
            // A host opens on its own schedule, and this method is how a caller waits for it: a task
            // that completed while the files were still being read would be a lie, and a failure
            // raised inside one nobody awaits is a failure nobody sees.
            if (request.Completion is { } opening)
            {
                await opening.ConfigureAwait(true);
            }

            return true;
        }

        foreach (var path in paths)
        {
            if (await LoadAsync(path).ConfigureAwait(true))
            {
                return true;
            }
        }

        return false;
    }

    public Task<bool> LoadAsync(string path)
        => LoadCoreAsync(() => SvgViewerDocument.Load(path, SizeRequest, Rewrite), Path.GetFileName(path));

    public Task<bool> LoadTextAsync(string svgText)
        => LoadCoreAsync(() => SvgViewerDocument.LoadFromSvg(svgText), null);

    public Task<bool> LoadAsync(Stream stream)
        => LoadCoreAsync(() => SvgViewerDocument.Load(stream), null);

    private async Task<bool> LoadCoreAsync(Func<SvgViewerDocument> load, string? name)
    {
        var version = Interlocked.Increment(ref _loadVersion);

        _statusText.Text = name is { } ? $"opening {name}…" : "opening…";

        SvgViewerDocument document;
        try
        {
            // The expensive half, and the only thing that leaves the UI thread.
            document = await Task.Run(load).ConfigureAwait(true);
        }
        catch (Exception failure)
        {
            ShowNote(null);
            ShowFault(failure.Message);

            if (_document is null)
            {
                // Told there is no drawing rather than an empty one, so the panel does not claim
                // the file declares no parameters when nothing has read it.
                _statusText.Text = name is { } ? $"{name} couldn't be opened" : "The drawing couldn't be opened.";
                _panel.Parameters = null;
                _panel.ShowLets(null);
            }
            else
            {
                // The current document is untouched, so whatever was on screen stays there, and its
                // name is still the true answer to what is open.
                UpdateStatus();
            }

            return false;
        }

        if (Volatile.Read(ref _loadVersion) != version)
        {
            // A newer load already won; this one must not overwrite it.
            document.Dispose();
            return false;
        }

        SetDocument(document);
        return true;
    }

    private void SetDocument(SvgViewerDocument document)
    {
        var previous = _document;

        // The same drawing built again — a project resizing it, or a reopen — keeps the view it was
        // being looked at through. Assigning Svg starts over as if a file had been opened, which
        // threw away a zoom someone had set to look at the thing they were changing. Replace leaves
        // a view that was adjusted by hand alone and refits one that was not, which is the same
        // rule the source pane rebuilds under.
        _document = document;

        if (previous is { Path: { } was } && was == document.Path)
        {
            _canvas.Replace(document.Svg);
        }
        else
        {
            _canvas.Svg = document.Svg;
        }

        // Before Apply below, which asks what is wrong with the drawing: leaving the previous
        // analysis in place would answer for the file that was open a moment ago.
        ForgetSource();

        RebuildParameters(document);

        // What the document says about itself, before binding gets the last word on it. The other
        // way round, Apply reported a fault with nowhere to point at and this wiped it a line later,
        // so it appeared only when a parameter was next touched.
        ShowTrouble();

        // A document that declares parameters renders its placeholders until values are bound, which
        // is never what someone opening a file wants to look at.
        Apply();

        previous?.Dispose();

        UpdateStatus();
        UpdateZoomText();
        UpdateSource();
        UpdateElementTree();

        DocumentOpened?.Invoke(this, document);
    }

    /// <summary>Releases the open document and leaves the viewer empty.</summary>
    /// <remarks>
    /// A host that discards a viewer — closing a tab — has to call this: a document is disposed only
    /// when the next one replaces it, so the last one loaded would otherwise outlive the control.
    /// </remarks>
    public void Close()
    {
        // A load still in flight must not put a document back into a viewer that has been closed.
        Interlocked.Increment(ref _loadVersion);

        _canvas.Svg = null;

        _document?.Dispose();
        _document = null;

        _rows = Array.Empty<SvgViewerParameter>();
        _panel.Parameters = null;
        _panel.ShowLets(null);

        ShowNote(null);
        ShowFault(null);
        UpdateStatus();
        UpdateZoomText();
        UpdateSource();
        UpdateElementTree();
    }

    // ---- parameters ---------------------------------------------------------------------------

    private void RebuildParameters(SvgViewerDocument document)
    {
        var declarations = document.Declarations.Parameters;

        // Values survive a reload whose parameters are unchanged. Opening the same file again, or
        // re-reading one that was edited elsewhere, must not silently discard what was set.
        if (_rows.Count != declarations.Count || !_rows.Zip(declarations).All(pair => Same(pair.First, pair.Second)))
        {
            // Row by row, because adding one <e:param> used to discard every value bound to the
            // others — rare when a reload meant reopening a file, constant while someone types.
            var kept = _rows.ToDictionary(row => row.Name, StringComparer.Ordinal);

            _rows = SvgViewerParameterFactory.Create(declarations);

            foreach (var row in _rows)
            {
                // Only a value somebody chose. One still sitting where its default put it should
                // follow that default when the text changes it, or editing default="180" to "90"
                // would rebuild the row and then put 180 straight back.
                if (kept.TryGetValue(row.Name, out var previous) && previous.Type == row.Type && previous.IsModified)
                {
                    TrySetParameterValue(row.Name, previous.ToExprValue());
                }
            }
        }

        // Told even where no row moved, because this is not only a list: it is the panel finding
        // out that a drawing was read at all, which is what separates `declares no parameters` from
        // having nothing to say. The panel leaves identical rows alone, so this costs a comparison.
        _panel.Parameters = _rows;
        _panel.ShowLets(document.Declarations.Lets);
    }

    /// <summary>Whether a row already standing was built from this declaration.</summary>
    /// <remarks>
    /// All four expressions, not the name and type alone: with the source editable, changing a
    /// <c>step</c> or a bound leaves those two untouched and the panel showed the pre-edit range.
    /// </remarks>
    private static bool Same(SvgViewerParameter row, SvgExpressionParameter declared)
        => row.Type == declared.Type
           && string.Equals(row.Name, declared.Name, StringComparison.Ordinal)
           && string.Equals(row.Declaration.DefaultExpression, declared.DefaultExpression, StringComparison.Ordinal)
           && string.Equals(row.Declaration.MinExpression, declared.MinExpression, StringComparison.Ordinal)
           && string.Equals(row.Declaration.MaxExpression, declared.MaxExpression, StringComparison.Ordinal)
           && string.Equals(row.Declaration.StepExpression, declared.StepExpression, StringComparison.Ordinal);

    public void ResetParameters()
    {
        _panel.ResetToDefaults();
        RequestApply();
    }

    public bool TrySetParameterValue(string name, ExprValue value)
    {
        var row = _rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));

        switch (row)
        {
            case SvgViewerNumberParameter number when value.Type == ExprType.Number:
                // The same widening the seed took: compared plainly, the float's binary tail would
                // leave the row modified for ever over a difference nobody made.
                number.Value = SvgViewerParameterFactory.Widen(value.AsNumber);
                return true;

            case SvgViewerBooleanParameter boolean when value.Type == ExprType.Boolean:
                boolean.Value = value.AsBoolean;
                return true;

            case SvgViewerStringParameter text when value.Type == ExprType.String:
                text.Value = value.AsString;
                return true;

            case SvgViewerColorParameter colour when value.Type == ExprType.Color:
                colour.Color = global::Avalonia.Media.Color.FromArgb(value.Alpha, value.Red, value.Green, value.Blue);
                return true;

            default:
                return false;
        }
    }

    private Dictionary<string, ExprValue> BuildValues()
    {
        var values = new Dictionary<string, ExprValue>(_rows.Count, StringComparer.Ordinal);

        foreach (var row in _rows)
        {
            values[row.Name] = row.ToExprValue();
        }

        return values;
    }

    /// <summary>
    /// Coalesces a burst of changes into one binding per frame.
    /// </summary>
    /// <remarks>One per frame: a drag raises a change per tick, and each rebuilds a picture.</remarks>
    private void RequestApply()
    {
        if (_applyQueued)
        {
            return;
        }

        _applyQueued = true;

        Dispatcher.UIThread.Post(
            () =>
            {
                _applyQueued = false;
                Apply();
            },
            DispatcherPriority.Render);
    }

    private void Apply()
    {
        if (_document is not { } document)
        {
            return;
        }

        ShowLetValues(document);

        if (document.Declarations.Parameters.Count == 0)
        {
            return;
        }

        try
        {
            document.Svg.SetExpressionValues(BuildValues());
            ShowTrouble();
        }
        catch (ExprException failure)
        {
            // All or nothing, so the previous rendering is still up. The control keeps its value:
            // it is what the user has to see to correct it.
            ShowNote(Note());
            ShowFault(IsMarked(failure) ? null : failure.ToDiagnostic());
        }
        catch (Exception failure)
        {
            ShowNote(Note());
            ShowFault(failure.Message);
        }

        // A bound transform moves the scene the ring is traced from, so one left from before the
        // value changed would sit where the element used to be.
        RetraceOutline();

        // Swapped in place, so nothing about the control changed and the repaint must be asked for.
        _canvas.Publish();

        foreach (var row in _rows)
        {
            ParameterValueChanged?.Invoke(this, row);
        }
    }

    // ---- drag and drop ------------------------------------------------------------------------

    /// <remarks>
    /// Marked handled where it is taken, so that a host with a drop target of its own behind this
    /// one — a window that opens a file dropped anywhere on it — does not open the same files a
    /// second time as the event carries on past. A drag this cannot take is left unhandled for that
    /// same host to answer.
    /// </remarks>
    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects &= DragDropEffects.Copy | DragDropEffects.Link;

        if (e.DataTransfer?.TryGetFiles() is not { Length: > 0 })
        {
            e.DragEffects = DragDropEffects.None;

            return;
        }

        e.Handled = true;
    }

    /// <inheritdoc cref="OnDragOver"/>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer?.TryGetFiles()
            ?.Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToList();

        if (paths is { Count: > 0 })
        {
            e.Handled = true;

            await OpenAsync(paths).ConfigureAwait(true);
        }
    }

    // ---- chrome -------------------------------------------------------------------------------

    private void UpdateZoomText()
        => _zoomText.Text = (_canvas.Scale * 100d).ToString("0", CultureInfo.CurrentCulture) + "%";

    /// <summary>How much of a drawing's text the pane will show.</summary>
    /// <remarks>
    /// A backstop on what is held, not on layout: the tokens for a drawing this size are tens of
    /// megabytes.
    /// </remarks>
    internal const int SourceLimit = 2_000_000;

    private void UpdateSource()
    {
        ForgetSource();
        RenderSource();
    }

    /// <summary>Shows what the open drawing is made of, or empties the pane when nothing is open.</summary>
    /// <remarks>
    /// Nothing is built while the pane is closed, the way the source pane colours nothing while it
    /// is: this runs on every rebuild, which is every time typing pauses, and it is 27ms at 4,000
    /// elements on the UI thread. A host that turned the pane off should not be paying that. The
    /// tree therefore holds nothing while it is hidden, and is filled again when it is shown.
    ///
    /// The ring is drawn again rather than left: a rebuild restores the selected row without raising
    /// anything, and the rectangles it was ringing belong to the scene the last document compiled.
    /// Keeping them would leave a ring where the shape used to be, which is worse than none.
    /// </remarks>
    private void UpdateElementTree()
    {
        _elementTree.Show(_treeHost.IsVisible ? _document?.Svg.SourceDocument : null);

        OutlineElement(_elementTree.SelectedNode);

        // The tree raises nothing while it restores a selection, so the panel would go on showing
        // the text as it was before the keystroke that rebuilt it.
        _element.Show(SourceAddress(_elementTree.SelectedNode?.AddressKey));
    }

    /// <summary>Drops what was known about the drawing that was open.</summary>
    private void ForgetSource()
    {
        _sourceStale = true;
        _sourceBuffered = false;
        _sourceAnalysed = false;
        _sourceMapped = false;
        _sourceDiagnostics = Array.Empty<SvgSourceDiagnostic>();
        _sourceElements = new Dictionary<string, SvgSourceElement>(StringComparer.Ordinal);

        _rebuild.Stop();
    }

    /// <summary>The text everything else works from: the editor's once it is holding the drawing.</summary>
    private string PaneSource() => _sourceBuffered ? _sourceEditor.Text : SourceText();

    /// <summary>The drawing's text, cut to what the pane will hold.</summary>
    /// <remarks>
    /// A cut is recorded because it makes the pane read-only: saving a truncated drawing would
    /// behead the file and write the sentence below into it.
    /// </remarks>
    private string SourceText()
    {
        var source = _document?.SourceText;

        _sourceTruncated = source is { Length: > SourceLimit };

        return _sourceTruncated
            ? source![..SourceLimit]
              + $"{Environment.NewLine}{Environment.NewLine}… {source.Length - SourceLimit:N0} more characters not shown."
              + $"{Environment.NewLine}This drawing is too large to edit here."
            : source ?? string.Empty;
    }

    /// <summary>
    /// What is wrong with the drawing, analysed at most once per document.
    /// </summary>
    /// <remarks>
    /// Splitting is context-free and checking is not, so this is a second pass — and a free one on a
    /// drawing with no expressions.
    /// </remarks>
    private IReadOnlyList<SvgSourceDiagnostic> Diagnostics()
    {
        if (_sourceAnalysed)
        {
            return _sourceDiagnostics;
        }

        _sourceAnalysed = true;
        _sourceDiagnostics = SvgSourceDiagnostics.Analyse(PaneSource());

        return _sourceDiagnostics;
    }

    /// <summary>
    /// Where each element of the drawing is written, worked out at most once per edit.
    /// </summary>
    /// <remarks>
    /// A second read of the whole file, so it is put off until somebody asks to be shown an
    /// element. A reader who never picks a row never pays for it.
    ///
    /// Keyed as the drawing that was built holds them, since that is where the rows come from, but
    /// placed in the file, since that is what the pane shows. The two are the same text unless a
    /// <see cref="Rewrite"/> is in play; a recipe's is what made them differ.
    /// </remarks>
    /// <summary>What the tree calls an element, as the file it is written in calls it.</summary>
    /// <remarks>
    /// The tree is of the drawing that was built; the pane and the element panel are the file. A
    /// rewrite that injects a block shifts every address after it, so the two disagree wherever one
    /// is in play — and an edit aimed at the wrong address writes somebody else's attribute.
    /// </remarks>
    private string? SourceAddress(string? addressKey)
    {
        if (addressKey is null)
        {
            return null;
        }

        var source = PaneSource();

        return SvgSourceElements.Addresses(source, _document?.Built(source)).TryGetValue(addressKey, out var mine)
            ? mine
            : null;
    }

    /// <summary>What the drawing's expressions come to now, for a readout, or null.</summary>
    private ExprEvaluator? Values()
    {
        if (_document is not { } document)
        {
            return null;
        }

        try
        {
            return ExprEvaluator.Create(document.Declarations, BuildValues());
        }
        catch (Exception failure) when (failure is ExprException or ArgumentException)
        {
            return null;
        }
    }

    private IReadOnlyDictionary<string, SvgSourceElement> SourceElements()
    {
        if (_sourceMapped)
        {
            return _sourceElements;
        }

        var source = PaneSource();

        _sourceMapped = true;
        _sourceElements = SvgSourceElements.Map(source, _document?.Built(source));

        return _sourceElements;
    }

    /// <summary>
    /// Gives the editor the open drawing, if it does not have it already.
    /// </summary>
    /// <remarks>
    /// Separate from painting the pane: showing it needs both, an edit from the panel needs only
    /// this, and a drawing nobody opens or edits costs nothing.
    /// </remarks>
    private void EnsureSourceBuffer()
    {
        if (!_sourceStale)
        {
            return;
        }

        _sourceStale = false;

        var text = SourceText();

        HideSourceTip();

        // Replacing the document resets the caret, the scroll and the undo stack, so it happens on a
        // load and never on an edit. TextChanged fires for this too, and the flag is what tells the
        // two apart.
        _sourceLoading = true;

        try
        {
            _sourceEditor.Document = new TextDocument(text);
            _sourceEditor.Document.UndoStack.MarkAsOriginalFile();
        }
        finally
        {
            _sourceLoading = false;
        }

        _sourceEditor.IsReadOnly = _document is null || _sourceTruncated;
        _sourceBuffered = true;

        RaiseModified();
    }

    /// <summary>
    /// Fills the pane.
    /// </summary>
    /// <remarks>
    /// Only when the pane is up, since the toggle starts off. It re-colours on every show, not only
    /// the first, because the panel can have edited the buffer while the pane was closed.
    /// </remarks>
    private void RenderSource()
    {
        if (!_sourceHost.IsVisible)
        {
            return;
        }

        EnsureSourceBuffer();
        RefreshSource();
    }

    /// <summary>Re-colours and re-marks what the pane is showing, without disturbing it.</summary>
    /// <remarks>
    /// Nothing to do while the pane is closed: an edit still rebuilds and still reports, and only
    /// the colouring is worth putting off.
    /// </remarks>
    private void RefreshSource()
    {
        if (!_sourceHost.IsVisible)
        {
            return;
        }

        _sourceColorizer.Show(SvgSourceHighlighter.Lines(_sourceEditor.Text));
        _sourceMarkers.Show(Diagnostics());

        PaintSource();
    }

    /// <summary>Taken as a keystroke: the analysis is stale, and the drawing follows in a moment.</summary>
    private void OnSourceEdited()
    {
        if (_sourceLoading || !_sourceBuffered)
        {
            return;
        }

        _sourceAnalysed = false;
        _sourceMapped = false;

        // Posted rather than called: AvaloniaEdit raises TextChanged before its undo stack has
        // taken the edit, so IsOriginalFile is still true at this point and the drawing reads as
        // saved. Measured — the flag is true inside the handler and false by the time a posted
        // call runs, so calling it here raised nothing and a host could never mark its tab.
        Dispatcher.UIThread.Post(RaiseModified);

        _rebuild.Stop();
        _rebuild.Start();
    }

    /// <summary>
    /// Builds the drawing again from the text in the pane.
    /// </summary>
    /// <remarks>
    /// Half-typed markup does not parse, so a refusal is the ordinary case: the picture stays up and
    /// only the marks move, which is what the reader steers by until the drawing can follow.
    /// </remarks>
    private void RebuildFromSource()
    {
        RefreshSource();

        RebuildFrom(_sourceEditor.Text);
    }

    /// <summary>
    /// Builds the drawing again from the text it is already holding.
    /// </summary>
    /// <remarks>
    /// For a host that has changed what the same text comes to rather than the text: a
    /// <see cref="Rewrite"/> whose recipe has been edited, or a new <see cref="SizeRequest"/>.
    /// Quieter than opening the file again, which is what it replaced — the source pane keeps its
    /// buffer, its caret and anything typed into it, the status line does not flash a load, and
    /// nothing is read off the disk.
    /// </remarks>
    /// <returns>Whether there was a drawing to build.</returns>
    public bool Rebuild()
    {
        if (_document is null)
        {
            return false;
        }

        // Source and not the editor's own text, which is the truncated stand-in for a drawing too
        // large to hold — building from that would behead the picture.
        RebuildFrom(Source);

        return true;
    }

    private void RebuildFrom(string svgText)
    {
        if (_document is not { } open)
        {
            return;
        }

        SvgViewerDocument rebuilt;

        try
        {
            // This viewer's rewrite and not the document's, which is the one it was loaded with: a
            // host that has edited its recipe since is asking for exactly that difference.
            rebuilt = open.Reload(svgText, SizeRequest, Rewrite);
        }
        catch (Exception)
        {
            // Not readable as SVG, which is what a document looks like in the middle of being typed.
            ShowTrouble();
            return;
        }

        _document = rebuilt;
        _canvas.Replace(rebuilt.Svg);

        RebuildParameters(rebuilt);

        ShowTrouble();

        // A fresh picture starts unbound, so the values on the panel have to be put back on it or
        // every parameter snaps to its default as the text is typed. It reports last for the same
        // reason as on a load: what it finds has nowhere else to be said.
        Apply();

        open.Dispose();

        UpdateStatus();

        // Here as well as in SetDocument, and this is the easy one to miss: a rebuild raises no
        // DocumentOpened, so a tree that followed the event alone would be showing the document as
        // it was before the last keystroke.
        UpdateElementTree();
    }

    /// <summary>Whether the pane holds edits that are not on disk.</summary>
    public bool IsSourceModified
        => _sourceBuffered && _sourceEditor.Document is { } document && !document.UndoStack.IsOriginalFile;

    /// <summary>Raised when <see cref="IsSourceModified"/> changes, for a host that marks its chrome.</summary>
    public event EventHandler<bool>? SourceModifiedChanged;

    private void RaiseModified()
    {
        var modified = IsSourceModified;

        if (modified == _sourceModified)
        {
            return;
        }

        _sourceModified = modified;
        SourceModifiedChanged?.Invoke(this, modified);
    }

    /// <summary>
    /// Where the declaration commands write, when that is not the drawing itself.
    /// </summary>
    /// <remarks>
    /// For a host whose drawing declares things its own text does not: an svgc project applying a
    /// recipe puts the parameters in the recipe file, and every command below would otherwise write
    /// them into the drawing — which would also give it a declaration block of its own, and a recipe
    /// refuses a document that already has one.
    ///
    /// Set alongside <see cref="Rewrite"/>, which is what put the declarations there to begin with.
    /// </remarks>
    public ISvgViewerDeclarationTarget? DeclarationTarget { get; set; }

    /// <summary>The drawing as it stands, for a host writing declarations into it.</summary>
    /// <remarks>
    /// A viewer is a target as well as a consumer of one. A host that keeps a drawing's declarations
    /// somewhere else sets <see cref="DeclarationTarget"/>; a host editing a drawing that is open
    /// here writes through this instead, so the edit lands in the buffer somebody is looking at
    /// rather than in the file underneath it — with undo, the unsaved mark, and a save that waits
    /// to be asked for.
    ///
    /// Explicit, because <c>Text</c> and <c>Apply</c> are poor names on a viewer and good ones on a
    /// target: a caller that wants these has the interface in its hand already.
    /// </remarks>
    string ISvgViewerDeclarationTarget.Text => Source;

    /// <inheritdoc />
    bool ISvgViewerDeclarationTarget.Apply(IReadOnlyList<SvgTextEdit> edits)
    {
        if (edits is null || edits.Count == 0 || _document is null)
        {
            return false;
        }

        // Spelled out rather than left to Editable(), which answers yes at once when this viewer has
        // a DeclarationTarget of its own and never reaches either of these.
        //
        // The buffer first: Splice writes into the editor's document, and until it has been filled
        // that is the empty one AvaloniaEdit starts with — the pane need never have been opened.
        EnsureSourceBuffer();

        if (_sourceTruncated)
        {
            ShowNote("This drawing is too large to edit here.");

            return false;
        }

        return Splice(SvgSourceEditResult.From(edits));
    }

    /// <summary>The text the declaration commands read.</summary>
    private string Declarations() => DeclarationTarget?.Text ?? PaneSource();

    /// <summary>Whether there is anywhere to write a declaration, saying so when there is not.</summary>
    private bool Editable()
    {
        if (DeclarationTarget is { })
        {
            return true;
        }

        EnsureSourceBuffer();

        if (_sourceTruncated)
        {
            ShowNote("This drawing is too large to edit here.");

            return false;
        }

        return true;
    }

    /// <summary>Puts a declaration edit wherever the declarations live.</summary>
    /// <remarks>
    /// The refusal is reported here either way, so a host supplying a target has one thing to do
    /// with the edits and nothing to say about them.
    /// </remarks>
    private bool Write(SvgSourceEditResult result)
    {
        if (DeclarationTarget is not { } target)
        {
            return Splice(result);
        }

        if (!result.Succeeded)
        {
            ShowNote(result.Refusal);

            return false;
        }

        return result.Edits.Count > 0 && target.Apply(result.Edits);
    }

    /// <summary>
    /// Asks for a parameter and writes it where the declarations live.
    /// </summary>
    /// <remarks>
    /// A splice, not a rewrite: the rest of the file is left as it was, comments included, and the
    /// buffer changing is a keystroke as far as everything downstream is concerned. It does not need
    /// the pane open — which is why the buffer and the pane are separate things.
    /// </remarks>
    /// <returns>Whether the drawing was changed.</returns>
    public async Task<bool> AddParameterAsync()
    {
        if (_document is null)
        {
            return false;
        }

        if (!Editable())
        {
            return false;
        }

        return await _commands.AddAsync(TopLevel.GetTopLevel(this)).ConfigureAwait(true);
    }

    /// <summary>
    /// Asks what one parameter should declare, and writes the answer into the drawing.
    /// </summary>
    /// <remarks>
    /// A rename is an edit everywhere the drawing names it, and the whole of it is one thing to take
    /// back. The type is not offered: every expression using it was checked against the type it has.
    /// </remarks>
    /// <returns>Whether the drawing was changed.</returns>
    public async Task<bool> EditParameterAsync(SvgViewerParameter parameter)
    {
        if (parameter is null)
        {
            throw new ArgumentNullException(nameof(parameter));
        }

        if (_document is null)
        {
            return false;
        }

        if (!Editable())
        {
            return false;
        }

        return await _commands.EditAsync(TopLevel.GetTopLevel(this), parameter).ConfigureAwait(true);
    }

    /// <summary>
    /// Takes one parameter out of the drawing.
    /// </summary>
    /// <remarks>
    /// Refused while anything still names it, since removing it would leave a drawing that parses
    /// and draws nothing. The refusal says how many uses there are, which is what tells somebody
    /// whether the button did nothing or whether they meant something else.
    /// </remarks>
    /// <returns>Whether the drawing changed.</returns>
    public bool RemoveParameter(SvgViewerParameter parameter)
    {
        if (parameter is null)
        {
            throw new ArgumentNullException(nameof(parameter));
        }

        if (_document is null)
        {
            return false;
        }

        if (!Editable())
        {
            return false;
        }

        return _commands.Remove(parameter);
    }

    /// <summary>
    /// Writes every value somebody chose into the drawing as the declared default.
    /// </summary>
    /// <remarks>
    /// One call for the lot, so a session of moving sliders is one thing to take back. Only rows that
    /// differ are written, so committing twice does nothing the second time.
    /// </remarks>
    /// <returns>Whether the drawing was changed.</returns>
    public bool CommitParameterDefaults()
    {
        if (_document is null)
        {
            return false;
        }

        if (!Editable())
        {
            return false;
        }

        return _commands.SetDefaults();
    }

    /// <summary>
    /// Writes what a let row says into the drawing, declaring it if it is not there yet.
    /// </summary>
    /// <returns>Whether the drawing changed.</returns>
    public bool CommitLet(SvgViewerLet let)
    {
        if (let is null)
        {
            throw new ArgumentNullException(nameof(let));
        }

        if (_document is null)
        {
            return false;
        }

        if (!Editable())
        {
            return false;
        }

        return _commands.CommitLet(let);
    }

    /// <summary>
    /// Moves a let to <paramref name="to"/> among the drawing's lets.
    /// </summary>
    /// <remarks>
    /// Where a let sits is what it can name, so this is refused rather than applied when it would
    /// leave something unresolved. The panel keeps a drag inside the positions that check, so the
    /// refusal is a backstop and not the usual answer.
    /// </remarks>
    /// <returns>Whether the drawing changed.</returns>
    public bool MoveLet(SvgViewerLet let, int to)
    {
        if (let is null)
        {
            throw new ArgumentNullException(nameof(let));
        }

        if (_document is null || let.Declaration is null)
        {
            return false;
        }

        if (!Editable())
        {
            return false;
        }

        return _commands.MoveLet(let, to);
    }

    /// <summary>
    /// Takes one let out of the drawing.
    /// </summary>
    /// <remarks>
    /// Refused while anything still names it, as a parameter is. A row nobody has written yet never
    /// reaches this: the panel throws that one away itself, since there is nothing in the document
    /// to take out.
    /// </remarks>
    /// <returns>Whether the drawing changed.</returns>
    public bool RemoveLet(SvgViewerLet let)
    {
        if (let is null)
        {
            throw new ArgumentNullException(nameof(let));
        }

        if (_document is null || let.Declaration is null)
        {
            return false;
        }

        if (!Editable())
        {
            return false;
        }

        return _commands.RemoveLet(let);
    }

    /// <summary>
    /// Moves a parameter to <paramref name="to"/> among the drawing's parameters.
    /// </summary>
    /// <remarks>
    /// Presentational to this drawing — nothing reads parameters in order — but not to the code
    /// generated from it, whose signature is written in that order. So a move is refused when it
    /// would put a parameter with no default after one that has a default, which is C#'s rule about
    /// optional arguments and the generator's own refusal asked earlier.
    /// </remarks>
    /// <returns>Whether the drawing changed.</returns>
    public bool MoveParameter(SvgViewerParameter parameter, int to)
    {
        if (parameter is null)
        {
            throw new ArgumentNullException(nameof(parameter));
        }

        if (_document is null)
        {
            return false;
        }

        if (!Editable())
        {
            return false;
        }

        return _commands.MoveParameter(parameter, to);
    }

    /// <summary>Shows what each let currently evaluates to, beside it.</summary>
    /// <remarks>
    /// A second fold of the same declarations rather than a reading of the picture: what the render
    /// evaluates is kept per drawing command, not per name. The expressions are tiny and an apply is
    /// already coalesced per frame.
    /// </remarks>
    private void ShowLetValues(SvgViewerDocument document)
    {
        if (_panel.Lets.Count == 0)
        {
            return;
        }

        ExprEvaluator? evaluator = null;

        try
        {
            evaluator = ExprEvaluator.Create(document.Declarations, BuildValues());
        }
        catch (Exception failure) when (failure is ExprException or ArgumentException)
        {
            // Nothing resolves, which the rows and the pane already say between them. A stale
            // readout would be a second, quieter account of the same trouble.
        }

        foreach (var row in _panel.Lets)
        {
            row.Readout = Readout(evaluator, row);
        }
    }

    /// <summary>What one let evaluates to, or nothing where that cannot be said.</summary>
    private static string Readout(ExprEvaluator? evaluator, SvgViewerLet row)
    {
        // Evaluating the name alone reads it out of the map Create has already filled, so the lets
        // are folded once rather than once per row.
        if (evaluator is null || row.Declaration is not { } declared)
        {
            return string.Empty;
        }

        try
        {
            var value = evaluator.Evaluate(declared.Name);

            return $"{ExprFunctions.Describe(value.Type)}  {SvgViewerParameterFactory.Describe(value)}";
        }
        catch (Exception failure) when (failure is ExprException or ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>Puts an edit through the text buffer, as one thing that can be taken back.</summary>
    /// <remarks>
    /// Through the buffer rather than around it, so the undo stack is the one history of the
    /// document. Grouped, because an insertion that declared a namespace and opened a block is three
    /// spans and one decision.
    /// </remarks>
    private bool Splice(SvgSourceEditResult result)
    {
        if (!result.Succeeded)
        {
            ShowNote(result.Refusal);

            return false;
        }

        if (result.Edits.Count == 0)
        {
            return false;
        }

        // Filled first, because the edits were measured against PaneSource(), which reads the
        // drawing's own text until the pane holds it. Splicing them into the pane's empty document
        // threw ArgumentOutOfRangeException for every edit made before the source pane was opened.
        EnsureSourceBuffer();

        if (_sourceEditor.Document is not { } document)
        {
            return false;
        }

        document.BeginUpdate();

        try
        {
            // Back to front, so an earlier edit does not move the ones after it.
            for (var index = result.Edits.Count - 1; index >= 0; index--)
            {
                var edit = result.Edits[index];

                document.Replace(edit.Position, edit.Length, edit.Text);
            }
        }
        finally
        {
            document.EndUpdate();
        }

        return true;
    }

    /// <summary>Asks what size the drawing should be, and resizes it to the answer.</summary>
    /// <returns>Whether the drawing was resized.</returns>
    public async Task<bool> ResizeAsync()
    {
        if (_document is not { } document)
        {
            return false;
        }

        var natural = document.Svg.Picture?.CullRect;

        if (natural is not { Width: > 0f, Height: > 0f })
        {
            ShowNote("This drawing has no size to resize from.");

            return false;
        }

        var request = await ResizeDialogService
            .AskAsync(TopLevel.GetTopLevel(this), new SvgViewerResize(natural.Value.Width, natural.Value.Height))
            .ConfigureAwait(true);

        return request is { } size && Resize(size);
    }

    /// <summary>
    /// Resizes the drawing, by rewriting the frame its root element declares.
    /// </summary>
    /// <remarks>
    /// An edit to the pane rather than to the picture, so it is the drawing that is a different size
    /// and not the view of it: the text says so, saving writes it, and taking it back is an undo.
    /// </remarks>
    /// <returns>Whether anything was rewritten.</returns>
    public bool Resize(SvgSizeRequest request)
    {
        if (_document is not { } document)
        {
            return false;
        }

        EnsureSourceBuffer();

        if (_sourceTruncated)
        {
            ShowNote("This drawing is too large to edit here.");

            return false;
        }

        return Splice(document.Resize(PaneSource(), request));
    }

    /// <summary>
    /// Writes the pane's text back to a file.
    /// </summary>
    /// <remarks>
    /// A drawing loaded from text or a stream has no file, so it asks through the same service the
    /// open button uses.
    /// </remarks>
    /// <returns>Whether anything was written.</returns>
    public async Task<bool> SaveSourceAsync(string? path = null)
    {
        if (_document is not { } document || !_sourceBuffered)
        {
            return false;
        }

        var target = path ?? document.Path
            ?? await FileDialogService.SaveSvgAsync(TopLevel.GetTopLevel(this), null).ConfigureAwait(true);

        if (string.IsNullOrEmpty(target))
        {
            return false;
        }

        try
        {
            document.Write(_sourceEditor.Text, target!);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            ShowFault(failure.Message);
            return false;
        }

        _sourceEditor.Document.UndoStack.MarkAsOriginalFile();
        RaiseModified();

        return true;
    }

    /// <summary>Takes back the last edit to the drawing's text.</summary>
    /// <remarks>
    /// For a host with a menu: the pane binds the platform's gestures itself, and a menu item wants
    /// the same thing without one. The stack is the pane's, so this takes back typing, a committed
    /// declaration and a resize alike — and never a parameter value, which is bound rather than
    /// written.
    /// </remarks>
    /// <returns>Whether there was anything to take back.</returns>
    public bool Undo() => _sourceBuffered && _sourceEditor.Undo();

    /// <inheritdoc cref="Undo"/>
    public bool Redo() => _sourceBuffered && _sourceEditor.Redo();

    /// <summary>
    /// The platform is only there to ask once the control is in a window, so the pane's gestures
    /// are bound on the way in rather than in the constructor.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        BindSourceHotkeys();
    }

    /// <summary>
    /// Gives the pane the undo and redo gestures the platform uses.
    /// </summary>
    /// <remarks>
    /// AvaloniaEdit binds the two commands and no keys to them — it registers CommandBindings for
    /// ApplicationCommands.Undo and Redo and never asks the keymap for a gesture, which every other
    /// command of its own does — so a pane in a plain host has an undo stack nothing can reach.
    /// Taken from the platform rather than written down, so this is Cmd+Z, Cmd+Shift+Z and Cmd+Y on
    /// macOS and the Control forms elsewhere, whatever the platform says those are.
    /// </remarks>
    private void BindSourceHotkeys()
    {
        if (_sourceEditor.KeyBindings.Count > 0 || this.GetPlatformSettings()?.HotkeyConfiguration is not { } hotkeys)
        {
            return;
        }

        Bind(hotkeys.Undo, () => _sourceEditor.Undo());
        Bind(hotkeys.Redo, () => _sourceEditor.Redo());

        void Bind(IEnumerable<KeyGesture> gestures, Action run)
        {
            foreach (var gesture in gestures)
            {
                // On the editor rather than on the viewer, so a gesture reaches the pane only while
                // somebody is in it: a parameter box keeps its own.
                _sourceEditor.KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = new Run(run) });
            }
        }
    }

    /// <summary>An ICommand around a delegate, since neither Avalonia nor this package has one.</summary>
    private sealed class Run : ICommand
    {
        private readonly Action _run;

        public Run(Action run) => _run = run;

        // Nothing turns these off: an undo with nothing to undo is a no-op inside AvaloniaEdit, and
        // a binding that came and went would be a second thing to keep in step with the stack.
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _run();
    }

    /// <summary>Repaints the pane in the current theme, without disturbing what is on screen.</summary>
    private void PaintSource()
    {
        if (!_sourceHost.IsVisible)
        {
            return;
        }

        _sourceEditor.Foreground = SourceBrush(SvgSourceTokenKind.Text);
        _sourceEditor.LineNumbersForeground = Resource("SvgViewerSourceLineNumberBrush");

        _sourceEditor.TextArea.TextView.Redraw();
    }

    /// <summary>Shows what is wrong with whatever the pointer came to rest on.</summary>
    /// <remarks>
    /// The token rather than the line it sits on. A line of markup is long, and a message about a
    /// name is worth much less at the other end of one.
    /// </remarks>
    private void OnSourceHover(object? sender, PointerEventArgs e)
    {
        var view = _sourceEditor.TextArea.TextView;

        if (_sourceDiagnostics.Count == 0 || _sourceEditor.Document is not { } document)
        {
            return;
        }

        if (view.GetPositionFloor(e.GetPosition(view) + view.ScrollOffset) is not { } position)
        {
            HideSourceTip();
            return;
        }

        var offset = document.GetOffset(position.Location);

        var messages = _sourceDiagnostics
            .Where(d => d.Start <= offset && offset < d.Start + d.Length)
            .Select(d => d.Message)
            .ToList();

        if (messages.Count == 0)
        {
            HideSourceTip();
            return;
        }

        ToolTip.SetTip(_sourceEditor, string.Join("\n", messages));
        ToolTip.SetIsOpen(_sourceEditor, true);

        e.Handled = true;
    }

    private void HideSourceTip()
    {
        ToolTip.SetIsOpen(_sourceEditor, false);
        ToolTip.SetTip(_sourceEditor, null);
    }

    private IBrush? ErrorBrush() => Resource("SvgViewerSourceErrorBrush");

    private IBrush? WarningBrush() => Resource("SvgViewerSourceWarningBrush");

    /// <summary>The brush for a kind of token.</summary>
    private IBrush? SourceBrush(SvgSourceTokenKind kind) => Resource(SourceResourceKey(kind));

    /// <summary>What a piece of a document is painted with, by name.</summary>
    /// <remarks>
    /// Internal because <see cref="SvgExpressionPresenter"/> paints the same kinds in an editable box
    /// beside the pane. One table, so a `tau` cannot be one colour in the source and another in the
    /// row above it.
    /// </remarks>
    /// <summary>The brush key a token kind is painted from.</summary>
    /// <remarks>
    /// Public alongside <see cref="SvgViewerSourceColorizer"/>, and for the same reason: a host
    /// colouring its own source view has to reach the same brush for the same kind, or two panes in
    /// one window paint the same text differently.
    /// </remarks>
    public static string SourceResourceKey(SvgSourceTokenKind kind) => kind switch
    {
        SvgSourceTokenKind.Punctuation => "SvgViewerSourcePunctuationBrush",
        SvgSourceTokenKind.Element => "SvgViewerSourceElementBrush",
        SvgSourceTokenKind.Attribute => "SvgViewerSourceAttributeBrush",
        SvgSourceTokenKind.Value => "SvgViewerSourceValueBrush",
        SvgSourceTokenKind.Comment => "SvgViewerSourceCommentBrush",
        SvgSourceTokenKind.Expression => "SvgViewerSourceExpressionBrush",
        SvgSourceTokenKind.ExpressionNumber => "SvgViewerSourceExpressionNumberBrush",
        SvgSourceTokenKind.ExpressionColor => "SvgViewerSourceExpressionColorBrush",
        SvgSourceTokenKind.ExpressionString => "SvgViewerSourceExpressionStringBrush",
        SvgSourceTokenKind.ExpressionFunction => "SvgViewerSourceExpressionFunctionBrush",
        SvgSourceTokenKind.ExpressionConstant => "SvgViewerSourceExpressionConstantBrush",
        SvgSourceTokenKind.ExpressionKeyword => "SvgViewerSourceExpressionKeywordBrush",
        SvgSourceTokenKind.ExpressionOperator => "SvgViewerSourceExpressionOperatorBrush",
        SvgSourceTokenKind.ExpressionPunctuation => "SvgViewerSourceExpressionPunctuationBrush",
        SvgSourceTokenKind.ExpressionIdentifier => "SvgViewerSourceExpressionIdentifierBrush",
        _ => "SvgViewerSourceTextBrush",
    };

    /// <summary>
    /// Every brush the pane paints with, by the one route.
    /// </summary>
    /// <remarks>
    /// A key is a string, so a rename that misses one paints nothing and says nothing — the line
    /// numbers went unpainted for two commits that way. One lookup is what a test can check.
    /// </remarks>
    internal IBrush? Resource(string key)
        => this.TryFindResource(key, ActualThemeVariant, out var brush) ? brush as IBrush : null;

    private void UpdateStatus()
    {
        if (_document is not { } document)
        {
            _statusText.Text = "No drawing open.";
            return;
        }

        var name = document.Path is { } path ? Path.GetFileName(path) : "drawing";
        var count = document.Declarations.Parameters.Count;

        _statusText.Text = count == 0
            ? $"{name} — no parameters"
            : $"{name} — {count} parameter{(count == 1 ? string.Empty : "s")}";
    }

    /// <summary>
    /// What is wrong with the open drawing, said once and for as long as it is true.
    /// </summary>
    /// <remarks>
    /// A standing statement, not a reaction: a drawing has its mistakes from the moment it opens.
    /// A count and a pointer only, because the pane marks each one on the line that carries it.
    /// </remarks>
    private string? Note()
    {
        if (_document is null)
        {
            return _notice;
        }

        var found = Diagnostics();

        var errors = 0;
        var warnings = 0;

        foreach (var diagnostic in found)
        {
            if (diagnostic.Severity == SvgSourceSeverity.Warning)
            {
                warnings++;
            }
            else
            {
                errors++;
            }
        }

        // Counted apart because they do not mean the same thing. A warning is something the drawing
        // opened in spite of -- an element this renderer does not know, say -- and calling six of
        // those six errors would be the status bar saying a working file is broken.
        if (errors == 0 && warnings == 0)
        {
            return _notice;
        }

        var said = errors == 0
            ? Count(warnings, "warning")
            : warnings == 0
                ? Count(errors, "error")
                : $"{Count(errors, "error")} and {Count(warnings, "warning")}";

        var counted = $"{said}, marked in the Source pane";

        // Both, on the one line there is. The host's comes first: it is about the drawing as a
        // whole, and the count points at marks the reader can find without being told twice.
        return _notice is { } notice ? $"{notice} · {counted}" : counted;
    }

    private static string Count(int many, string what) => many == 1 ? $"1 {what}" : $"{many} {what}s";

    /// <summary>
    /// What is wrong with nowhere in the file to say it, or null.
    /// </summary>
    /// <remarks>
    /// The one case the drawing itself can carry: a document whose declarations would not read and
    /// whose text could not be kept, so there is no pane to mark and nothing to point at.
    /// </remarks>
    private string? Fault()
        => Diagnostics().Count == 0 ? _document?.DeclarationError : null;

    /// <summary>Says everything that is standing about the open drawing.</summary>
    private void ShowTrouble()
    {
        ShowNote(Note());
        ShowFault(Fault());
    }

    /// <summary>Whether the pane already marks what this failure is about.</summary>
    private bool IsMarked(ExprException failure)
        => Diagnostics().Any(d => string.Equals(d.Message, failure.Message, StringComparison.Ordinal));

    /// <summary>
    /// How far the drawing is pushed out of focus while something is being said over it.
    /// </summary>
    /// <remarks>
    /// Far enough that shapes become colour rather than edges, so it reads as glass over the drawing
    /// rather than one somebody forgot to focus — and near enough to stay recognisable.
    /// </remarks>
    private const double FaultBlur = 28d;

    /// <summary>
    /// Says what is wrong with the drawing, in the status bar, beside what is already there.
    /// </summary>
    /// <remarks>
    /// On the row that already exists: a note appearing and vanishing with every edit would shove
    /// the viewer up and down while someone typed.
    /// </remarks>
    private void ShowNote(string? message)
    {
        _noteText.Text = message ?? string.Empty;
        _noteText.IsVisible = !string.IsNullOrEmpty(message);

        if (!string.IsNullOrEmpty(message))
        {
            // Through the resource rather than a resolved brush, so the note follows a theme change
            // like everything else does. A note that is only warnings is not painted as an error.
            var key = _sourceDiagnostics.Any(d => d.Severity == SvgSourceSeverity.Error)
                      || _sourceDiagnostics.Count == 0
                ? "SvgViewerSourceErrorBrush"
                : "SvgViewerSourceWarningBrush";

            _noteText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(key);
        }

        if (!string.IsNullOrEmpty(message))
        {
            ErrorRaised?.Invoke(this, message!);
        }
    }

    /// <summary>
    /// Says what has no line to be said on, over the drawing it is about.
    /// </summary>
    /// <remarks>
    /// What reaches here is what the pane cannot mark, and in every such case the drawing on screen
    /// is not what the file says. Blurring it says so before the sentence is read.
    /// </remarks>
    private void ShowFault(string? message)
    {
        var shown = !string.IsNullOrEmpty(message);

        _errorText.Text = message ?? string.Empty;
        _errorPanel.IsVisible = shown;

        _canvas.Effect = shown ? new BlurEffect { Radius = FaultBlur } : null;

        if (shown)
        {
            ErrorRaised?.Invoke(this, message!);
        }
    }
}

/// <summary>
/// The files a user has asked to open, and whether the host has taken them.
/// </summary>
public sealed class SvgViewerOpenRequestedEventArgs : EventArgs
{
    public SvgViewerOpenRequestedEventArgs(IReadOnlyList<string> paths) => Paths = paths;

    /// <summary>What was picked or dropped, in the order it arrived.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>Set by a host that has opened the paths itself, which stops the viewer loading them.</summary>
    public bool Handled { get; set; }

    /// <summary>
    /// What the host started, for <see cref="SvgViewer.OpenAsync(IReadOnlyList{string})"/> to wait on.
    /// </summary>
    /// <remarks>
    /// The event is synchronous, so a host opening asynchronously has no other way to say it has not
    /// finished.
    /// </remarks>
    public Task? Completion { get; set; }
}
