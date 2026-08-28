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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using System.Windows.Input;
using Avalonia.VisualTree;
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
public partial class SvgViewer : UserControl
{
    private readonly SvgViewerCanvas _canvas;
    private readonly SvgViewerDeclarationPanel _panel;
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
    private readonly Grid _body;
    private readonly Grid _drawing;

    /// <summary>What the source pane's row was last set to, so hiding it can be undone.</summary>
    private GridLength _sourceHeight;

    /// <summary>What the panel's column was last set to, for the same reason.</summary>
    private GridLength _panelWidth;

    /// <summary>Whether the pane's text is stale — a document arrived, or the theme changed.</summary>
    private bool _sourceStale = true;

    /// <summary>What is wrong with the drawing, for whatever a pointer comes to rest on.</summary>
    private IReadOnlyList<SvgSourceDiagnostic> _sourceDiagnostics = Array.Empty<SvgSourceDiagnostic>();

    /// <summary>Whether the drawing has been analysed, which is not the same as being shown.</summary>
    private bool _sourceAnalysed;

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
        _body = this.FindControl<Grid>("Body")!;
        _drawing = this.FindControl<Grid>("Drawing")!;

        _sourceHeight = _body.RowDefinitions[2].Height;
        _panelWidth = _drawing.ColumnDefinitions[2].Width;

        this.FindControl<Button>("FitButton")!.Click += (_, _) => _canvas.Fit();
        this.FindControl<Button>("ActualSizeButton")!.Click += (_, _) => _canvas.ActualSize();
        this.FindControl<Button>("ResetButton")!.Click += (_, _) => _canvas.ResetView();
        this.FindControl<Button>("ZoomInButton")!.Click += (_, _) => _canvas.ZoomIn();
        this.FindControl<Button>("ZoomOutButton")!.Click += (_, _) => _canvas.ZoomOut();
        this.FindControl<Button>("ResetParametersButton")!.Click += (_, _) => ResetParameters();

        _sourceButton.IsCheckedChanged += (_, _) => ShowSource = _sourceButton.IsChecked == true;

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
        _panel.ValueChanged += (_, _) => RequestApply();

        // Fired and forgotten: a click is not something to await, and the two report what they did
        // through the note and the drawing like every other edit.
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

    public bool ShowStatusBar
    {
        get => _statusPanel.IsVisible;
        set => _statusPanel.IsVisible = value;
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
            // dragged to comes back.
            if (value)
            {
                _drawing.ColumnDefinitions[2].MinWidth = PanelMinimum;
                _drawing.ColumnDefinitions[2].Width = _panelWidth;
            }
            else
            {
                _panelWidth = _drawing.ColumnDefinitions[2].Width;
                _drawing.ColumnDefinitions[2].MinWidth = 0d;
                _drawing.ColumnDefinitions[2].Width = new GridLength(0d);
            }

            _panelHost.IsVisible = value;
            _splitter.IsVisible = value;
        }
    }

    /// <summary>The narrowest the panel is worth being, matching what the markup declares.</summary>
    private const double PanelMinimum = 260d;

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
            // paying for a strip it cannot see. What the splitter was dragged to comes back.
            if (value)
            {
                _body.RowDefinitions[2].Height = _sourceHeight;
            }
            else
            {
                _sourceHeight = _body.RowDefinitions[2].Height;
                _body.RowDefinitions[2].Height = new GridLength(0d);
            }

            _sourceHost.IsVisible = value;
            _sourceSplitter.IsVisible = value;
            _sourceButton.IsChecked = value;

            RenderSource();
        }
    }

    /// <summary>
    /// What is wrong with the open drawing, as ranges into <see cref="SvgViewerDocument.SourceText"/>.
    /// </summary>
    /// <remarks>
    /// Analysed on first ask, not only when the pane opens: the error panel needs to know whether a
    /// failed binding is the drawing's fault before anyone asks to read it.
    /// </remarks>
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
        => LoadCoreAsync(() => SvgViewerDocument.Load(path), Path.GetFileName(path));

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

        _document = document;
        _canvas.Svg = document.Svg;

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

        // Swapped in place, so nothing about the control changed and the repaint must be asked for.
        _canvas.Publish();

        foreach (var row in _rows)
        {
            ParameterValueChanged?.Invoke(this, row);
        }
    }

    // ---- drag and drop ------------------------------------------------------------------------

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects &= DragDropEffects.Copy | DragDropEffects.Link;

        if (e.DataTransfer?.TryGetFiles() is not { Length: > 0 })
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer?.TryGetFiles()
            ?.Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToList();

        if (paths is { Count: > 0 })
        {
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

    /// <summary>Drops what was known about the drawing that was open.</summary>
    private void ForgetSource()
    {
        _sourceStale = true;
        _sourceBuffered = false;
        _sourceAnalysed = false;
        _sourceDiagnostics = Array.Empty<SvgSourceDiagnostic>();

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

        RaiseModified();

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

        if (_document is not { } open)
        {
            return;
        }

        SvgViewerDocument rebuilt;

        try
        {
            rebuilt = open.Reload(_sourceEditor.Text);
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
    /// Asks for a parameter and writes it into the drawing's own text.
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

        EnsureSourceBuffer();

        if (_sourceTruncated)
        {
            ShowNote("This drawing is too large to edit here.");

            return false;
        }

        var taken = _rows.Select(row => row.Name).ToList();

        var parameter = await ParameterDialogService
            .AskAsync(TopLevel.GetTopLevel(this), taken)
            .ConfigureAwait(true);

        return parameter is { } declared && Splice(SvgDeclarationEditor.Add(PaneSource(), declared));
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

        EnsureSourceBuffer();

        if (_sourceTruncated)
        {
            ShowNote("This drawing is too large to edit here.");

            return false;
        }

        // Its own name is not one it clashes with.
        var taken = _rows
            .Where(row => !ReferenceEquals(row, parameter))
            .Select(row => row.Name)
            .ToList();

        var replacement = await ParameterDialogService
            .EditAsync(TopLevel.GetTopLevel(this), taken, parameter.Declaration)
            .ConfigureAwait(true);

        return replacement is { } wanted
            && Splice(SvgDeclarationEditor.Update(PaneSource(), parameter.Name, wanted));
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

        EnsureSourceBuffer();

        if (_sourceTruncated)
        {
            ShowNote("This drawing is too large to edit here.");

            return false;
        }

        return Splice(SvgDeclarationEditor.Remove(PaneSource(), parameter.Name));
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

        EnsureSourceBuffer();

        if (_sourceTruncated)
        {
            ShowNote("This drawing is too large to edit here.");

            return false;
        }

        var changed = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in _rows.Where(row => row.IsModified))
        {
            changed[row.Name] = row.ToExpression();
        }

        return changed.Count > 0 && Splice(SvgDeclarationEditor.SetDefaults(PaneSource(), changed));
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

        EnsureSourceBuffer();

        if (_sourceTruncated)
        {
            ShowNote("This drawing is too large to edit here.");

            return false;
        }

        var name = let.Name.Trim();
        var expression = let.Expression.Trim();

        return Splice(
            let.Declaration is { } declared
                ? SvgDeclarationEditor.UpdateLet(PaneSource(), declared.Name, name, expression)
                : SvgDeclarationEditor.AddLet(PaneSource(), name, expression));
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

        if (_document is null || let.Declaration is not { } declared)
        {
            return false;
        }

        EnsureSourceBuffer();

        if (_sourceTruncated)
        {
            ShowNote("This drawing is too large to edit here.");

            return false;
        }

        return Splice(SvgDeclarationEditor.MoveLet(PaneSource(), declared.Name, to));
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

        if (_document is null || let.Declaration is not { } declared)
        {
            return false;
        }

        EnsureSourceBuffer();

        if (_sourceTruncated)
        {
            ShowNote("This drawing is too large to edit here.");

            return false;
        }

        return Splice(SvgDeclarationEditor.RemoveLet(PaneSource(), declared.Name));
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

        EnsureSourceBuffer();

        if (_sourceTruncated)
        {
            ShowNote("This drawing is too large to edit here.");

            return false;
        }

        return Splice(SvgDeclarationEditor.MoveParameter(PaneSource(), parameter.Name, to));
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

        if (result.Edits.Count == 0 || _sourceEditor.Document is not { } document)
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
    internal static string SourceResourceKey(SvgSourceTokenKind kind) => kind switch
    {
        SvgSourceTokenKind.Punctuation => "SvgViewerSourcePunctuationBrush",
        SvgSourceTokenKind.Element => "SvgViewerSourceElementBrush",
        SvgSourceTokenKind.Attribute => "SvgViewerSourceAttributeBrush",
        SvgSourceTokenKind.Value => "SvgViewerSourceValueBrush",
        SvgSourceTokenKind.Comment => "SvgViewerSourceCommentBrush",
        SvgSourceTokenKind.Expression => "SvgViewerSourceExpressionBrush",
        SvgSourceTokenKind.ExpressionNumber => "SvgViewerSourceExpressionNumberBrush",
        SvgSourceTokenKind.ExpressionColor => "SvgViewerSourceExpressionColorBrush",
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
            return null;
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
            return null;
        }

        var said = errors == 0
            ? Count(warnings, "warning")
            : warnings == 0
                ? Count(errors, "error")
                : $"{Count(errors, "error")} and {Count(warnings, "warning")}";

        return $"{said}, marked in the Source pane";
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
