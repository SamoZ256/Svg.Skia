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
using Svg.Expressions;
using Svg.Highlighting;
using Svg.Skia;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// A drop-in SVG viewer: open a drawing, zoom and pan it, and drive the parameters it declares.
/// </summary>
/// <remarks>
/// <para>
/// Loading is the only thing that leaves the UI thread, because parsing a document and compiling its
/// scene is the expensive half. Binding a value is not: <see cref="SKSvg.SetExpressionValues"/>
/// evaluates a model that is already there, and doing it on the UI thread is what keeps two changes
/// in the order the user made them.
/// </para>
/// <para>
/// Nothing here blanks the drawing on an error. A failed load leaves the previous document up, a
/// malformed declaration block still renders its placeholders, and a rejected value leaves the last
/// good rendering exactly where it was.
/// </para>
/// </remarks>
public partial class SvgViewer : UserControl
{
    private readonly SvgViewerCanvas _canvas;
    private readonly SvgViewerParameterPanel _parameters;
    private readonly Border _toolBar;
    private readonly Border _statusPanel;
    private readonly Border _parameterHost;
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

    /// <summary>What the source pane's row was last set to, so hiding it can be undone.</summary>
    private GridLength _sourceHeight;

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
    /// False until the pane has been opened for this document, and false again the moment another is
    /// loaded: a document that arrives while the pane is closed leaves the editor holding the last
    /// one's text, and asking that what is wrong with the drawing would answer about the wrong file.
    /// </remarks>
    private bool _sourceShown;

    /// <summary>What the modified flag last was, so the change can be raised rather than polled.</summary>
    private bool _sourceModified;

    /// <summary>
    /// Waits for typing to stop before rebuilding the drawing.
    /// </summary>
    /// <remarks>
    /// A timer rather than <see cref="RequestApply"/>'s coalescing: that runs once a frame, which is
    /// what a slider drag wants and the opposite of what this does. Rebuilding is whole-document —
    /// 18ms to parse a 132KB drawing, 13ms to split it and 12ms to check it — so waiting for a pause
    /// is what makes it free, and nothing incremental is needed.
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
        _parameters = this.FindControl<SvgViewerParameterPanel>("PART_Parameters")!;
        _toolBar = this.FindControl<Border>("ToolBarPanel")!;
        _statusPanel = this.FindControl<Border>("StatusPanel")!;
        _parameterHost = this.FindControl<Border>("ParameterPanelHost")!;
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

        _sourceHeight = _body.RowDefinitions[2].Height;

        this.FindControl<Button>("OpenButton")!.Click += async (_, _) => await OpenAsync();
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
        _parameters.ValueChanged += (_, _) => RequestApply();

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
    /// The viewer holds one document, so opening replaces what is up. A host that shows several at
    /// once — the shell, whose tabs are one viewer each — marks the request handled and opens the
    /// paths its own way; unhandled, the viewer loads them itself as before.
    /// </remarks>
    public event EventHandler<SvgViewerOpenRequestedEventArgs>? OpenRequested;

    /// <summary>How the viewer asks for a file. Replaceable, and faked in tests.</summary>
    public ISvgViewerFileDialogService FileDialogService { get; set; } = new SvgViewerFileDialogService();

    public SvgViewerDocument? Document => _document;

    public SKSvg? Svg => _document?.Svg;

    public string? DocumentPath => _document?.Path;

    public IReadOnlyList<SvgViewerParameter> Parameters => _rows;

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

    public bool ShowParameterPanel
    {
        get => _parameterHost.IsVisible;
        set
        {
            _parameterHost.IsVisible = value;
            _splitter.IsVisible = value;
        }
    }

    /// <summary>
    /// Whether the drawing's text is shown under it.
    /// </summary>
    /// <remarks>
    /// A pane rather than a window of its own, because this control is dropped into applications
    /// that own their windows: one opening unbidden is not something an embedder can place, own or
    /// suppress. A host that wants a window can put <see cref="SvgViewerDocument.SourceText"/> in one.
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
    /// Analysed on first ask and kept until the document changes, rather than only when the pane is
    /// opened: the error panel needs to know whether a failed binding is the drawing's fault before
    /// anyone has asked to read it. A host that wants a problems list of its own reads these; the
    /// pane marks them in place.
    /// </remarks>
    public IReadOnlyList<SvgSourceDiagnostic> SourceDiagnostics => Diagnostics();

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
    /// <see cref="OpenRequested"/> is about — a host loading a file itself is not opening anything.
    /// It is also how a test drives a drop without building a drag payload.
    /// <para>
    /// Returns whether a drawing is open as a result, and true for a handled request — the host
    /// placed the paths, and only it knows what became of each. The task waits for whatever the host
    /// handed back.
    /// </para>
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
                // Nothing was open for the failure to leave alone. Reporting on the document here
                // would say `No drawing open.` over a card naming the line that stopped it -- true
                // of the viewer, and no answer to someone who just handed it a file. The panel is
                // told there is no drawing rather than an empty one, so it does not claim the file
                // declares no parameters when nothing has read it.
                _statusText.Text = name is { } ? $"{name} couldn't be opened" : "The drawing couldn't be opened.";
                _parameters.Parameters = null;
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
        _parameters.Parameters = null;

        ShowNote(null);
        ShowFault(null);
        UpdateStatus();
        UpdateZoomText();
        UpdateSource();
    }

    // ---- parameters ---------------------------------------------------------------------------

    private void RebuildParameters(SvgViewerDocument document)
    {
        var declarations = document.Declarations;

        // Values survive a reload whose parameters are unchanged. Opening the same file again, or
        // re-reading one that was edited elsewhere, must not silently discard what was set.
        if (_rows.Count != declarations.Count || !_rows.Zip(declarations).All(pair => Same(pair.First, pair.Second)))
        {
            // Row by row where the whole list does not match, because the text is being edited:
            // adding one <e:param> used to discard every value bound to the others, which was a
            // rare annoyance when a reload meant reopening a file and is a constant one while
            // someone is typing.
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
        _parameters.Parameters = _rows;
    }

    /// <summary>Whether a row already standing was built from this declaration.</summary>
    /// <remarks>
    /// Everything a row is made of, not the name and the type alone. Those two were enough when a
    /// reload meant reopening a file, where the declarations came back identical — with the source
    /// editable, changing a <c>step</c> or a bound leaves both untouched, and the panel went on
    /// showing what the file said before the edit.
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
        _parameters.ResetToDefaults();
        RequestApply();
    }

    public bool TrySetParameterValue(string name, ExprValue value)
    {
        var row = _rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));

        switch (row)
        {
            case SvgViewerNumberParameter number when value.Type == ExprType.Number:
                number.Value = value.AsNumber;
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
    /// <remarks>
    /// Dragging a slider raises a change per tick, and each binding evaluates the model and rebuilds
    /// a picture. One per frame is the difference between a smooth drag and a queue of stale ones.
    /// </remarks>
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

        if (document.Declarations.Count == 0)
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
            // Binding is all or nothing, so the previous rendering is still up. The control's value
            // is deliberately left alone: it is what the user has to see in order to correct it.
            ShowNote(Note());
            ShowFault(IsMarked(failure) ? null : failure.ToDiagnostic());
        }
        catch (Exception failure)
        {
            ShowNote(Note());
            ShowFault(failure.Message);
        }

        // The picture is swapped in place, and nothing about the control changed, so the repaint has
        // to be asked for.
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
    /// Not a layout limit any more — the rows virtualise, so length costs nothing to display. It is
    /// a backstop on what is held: the tokens for a drawing this size are a few tens of megabytes,
    /// and something has to say no to a file that is pathological rather than large.
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
        _sourceShown = false;
        _sourceAnalysed = false;
        _sourceDiagnostics = Array.Empty<SvgSourceDiagnostic>();

        _rebuild.Stop();
    }

    /// <summary>The text everything else works from: the editor's once it is holding the drawing.</summary>
    private string PaneSource() => _sourceShown ? _sourceEditor.Text : SourceText();

    /// <summary>The drawing's text, cut to what the pane will hold.</summary>
    /// <remarks>
    /// A cut is recorded because it makes the pane read-only. Editing a truncated drawing and saving
    /// it would behead the file and write the sentence below into it, which is a way to lose work
    /// that no warning makes acceptable.
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
    /// Splitting a document is context-free, checking one is not — it reads every declaration in the
    /// file — so this is a second pass, and one that costs nothing on a drawing with no expressions
    /// in it.
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
    /// Fills the pane.
    /// </summary>
    /// <remarks>
    /// Only when the pane is up, because the toggle starts off and laying out a document nobody
    /// asked to see is the whole cost of the feature paid for nothing.
    /// </remarks>
    private void RenderSource()
    {
        if (!_sourceStale || !_sourceHost.IsVisible)
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
        _sourceShown = true;

        RaiseModified();
        RefreshSource();
    }

    /// <summary>Re-colours and re-marks what the pane is showing, without disturbing it.</summary>
    private void RefreshSource()
    {
        _sourceColorizer.Show(SvgSourceHighlighter.Lines(_sourceEditor.Text));
        _sourceMarkers.Show(Diagnostics());

        PaintSource();
    }

    /// <summary>Taken as a keystroke: the analysis is stale, and the drawing follows in a moment.</summary>
    private void OnSourceEdited()
    {
        if (_sourceLoading || !_sourceShown)
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
    /// Half-typed markup does not parse, so a refusal is the ordinary case and must cost nothing: the
    /// picture that is up stays up and only the marks move. The colours and diagnostics are refreshed
    /// either way, because they are what the reader is steering by while the drawing cannot follow.
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
        _canvas.Svg = rebuilt.Svg;

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
        => _sourceShown && _sourceEditor.Document is { } document && !document.UndoStack.IsOriginalFile;

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
    /// Writes the pane's text back to a file.
    /// </summary>
    /// <remarks>
    /// Asks for somewhere to put it when the drawing has no file of its own — one loaded from text or
    /// a stream — through the same service the open button uses, so a host that supplied one for
    /// opening has already supplied one for saving.
    /// </remarks>
    /// <returns>Whether anything was written.</returns>
    public async Task<bool> SaveSourceAsync(string? path = null)
    {
        if (_document is not { } document || !_sourceShown)
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
    private IBrush? SourceBrush(SvgSourceTokenKind kind) => Resource(kind switch
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
    });

    /// <summary>
    /// Every brush the pane paints with, by the one route.
    /// </summary>
    /// <remarks>
    /// A key is a string, and a rename that catches one paints nothing and says nothing: the line
    /// numbers went unpainted for two commits exactly that way. One lookup is what a test can check
    /// every key against.
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
        var count = document.Declarations.Count;

        _statusText.Text = count == 0
            ? $"{name} — no parameters"
            : $"{name} — {count} parameter{(count == 1 ? string.Empty : "s")}";
    }

    /// <summary>
    /// What is wrong with the open drawing, said once and for as long as it is true.
    /// </summary>
    /// <remarks>
    /// A standing statement rather than a reaction: a drawing with mistakes in it has them from the
    /// moment it opens, and finding that out by moving an unrelated slider is the wrong way round.
    /// The count and the pointer are all it gives, because the pane marks each one on the line that
    /// carries it, and repeating the compiler's words down here says the same thing twice in the
    /// place least able to point at it.
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
    /// Far enough that shapes become colour rather than edges, which with the wash over it reads as
    /// glass in front of the drawing rather than a drawing someone forgot to focus. A small radius
    /// looks like a mistake; this one looks deliberate. Not so far that the drawing stops being
    /// recognisable — a reader still wants to see which one they are being told about.
    /// </remarks>
    private const double FaultBlur = 28d;

    /// <summary>
    /// Says what is wrong with the drawing, in the status bar, beside what is already there.
    /// </summary>
    /// <remarks>
    /// A note rather than a panel: it is about things the pane marks on the line that carries them,
    /// so its whole job is to say how many and where to look. On the row that already exists,
    /// because a note that came and went with every edit would shove the viewer up and down while
    /// someone typed.
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
    /// Over rather than under. What reaches here is what the pane cannot mark — a value of the wrong
    /// type for the attribute holding it, a document that would not load, a parameter the host left
    /// unbound — and in every one of those the drawing on screen is not what the file says. Blurring
    /// it says that before the sentence is read, and takes no room from anything to say it.
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
    /// The event is synchronous, so a host that opens asynchronously — anything reading a file —
    /// has otherwise no way to say it has not finished, and its caller would be told the drawing is
    /// open before it had been read.
    /// </remarks>
    public Task? Completion { get; set; }
}
