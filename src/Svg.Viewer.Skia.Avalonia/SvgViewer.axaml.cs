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
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
    private readonly Border _errorPanel;
    private readonly SelectableTextBlock _errorText;
    private readonly Border _sourceHost;
    private readonly GridSplitter _sourceSplitter;
    private readonly ItemsControl _sourceLines;
    private readonly ToggleButton _sourceButton;
    private readonly Grid _body;

    /// <summary>What the source pane's row was last set to, so hiding it can be undone.</summary>
    private GridLength _sourceHeight;

    /// <summary>Whether the pane's text is stale — a document arrived, or the theme changed.</summary>
    private bool _sourceStale = true;

    /// <summary>What is wrong with the drawing, by the line it is wrong on.</summary>
    private IReadOnlyDictionary<int, List<SvgSourceDiagnostic>> _sourceMarks =
        new Dictionary<int, List<SvgSourceDiagnostic>>();

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
        _errorPanel = this.FindControl<Border>("ErrorPanel")!;
        _errorText = this.FindControl<SelectableTextBlock>("ErrorText")!;
        _sourceHost = this.FindControl<Border>("SourcePanelHost")!;
        _sourceSplitter = this.FindControl<GridSplitter>("SourceSplitter")!;
        _sourceLines = this.FindControl<ItemsControl>("SourceLines")!;
        _sourceLines.ItemTemplate = new FuncDataTemplate<SvgSourceLine>((_, _) => BuildLine(), supportsRecycling: true);
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

        _canvas.ViewChanged += (_, _) => UpdateZoomText();
        _parameters.ValueChanged += (_, _) => RequestApply();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // The palette is resolved once per pass rather than bound per run: a document is thousands
        // of runs, and that many dynamic resource subscriptions costs more than redoing the pass.
        ActualThemeVariantChanged += (_, _) =>
        {
            _sourceStale = true;
            RenderSource();
        };

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
            // The current document is untouched, so whatever was on screen stays there.
            ShowError(failure.Message);
            UpdateStatus();
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

        RebuildParameters(document);

        // A document that declares parameters renders its placeholders until values are bound, which
        // is never what someone opening a file wants to look at.
        Apply();

        previous?.Dispose();

        ShowError(document.DeclarationError);
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
        _parameters.Parameters = _rows;

        ShowError(null);
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
        if (_rows.Count == declarations.Count
            && _rows.Select(r => r.Name).SequenceEqual(declarations.Select(d => d.Name), StringComparer.Ordinal)
            && _rows.Select(r => r.Type).SequenceEqual(declarations.Select(d => d.Type)))
        {
            return;
        }

        _rows = SvgViewerParameterFactory.Create(declarations);
        _parameters.Parameters = _rows;
    }

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
            ShowError(document.DeclarationError);
        }
        catch (ExprException failure)
        {
            // Binding is all or nothing, so the previous rendering is still up. The control's value
            // is deliberately left alone: it is what the user has to see in order to correct it.
            ShowError(failure.ToDiagnostic());
        }
        catch (Exception failure)
        {
            ShowError(failure.Message);
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
        _sourceStale = true;
        RenderSource();
    }

    /// <summary>
    /// Fills the pane, coloured where that is affordable.
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

        var source = _document?.SourceText;

        var text = source is { Length: > SourceLimit }
            ? source[..SourceLimit]
              + $"{Environment.NewLine}{Environment.NewLine}… {source.Length - SourceLimit:N0} more characters not shown."
            : source ?? string.Empty;

        var lines = SvgSourceHighlighter.Lines(text);

        // Splitting a document is context-free, checking one is not — it reads every declaration in
        // the file — so this is a second pass, and one that costs nothing on a drawing with no
        // expressions in it.
        _sourceMarks = Mark(lines, SvgSourceDiagnostics.Analyse(text));

        _sourceLines.ItemsSource = lines;
    }

    /// <summary>Files each diagnostic under the line it starts on.</summary>
    /// <remarks>
    /// By line rather than by position, because a row is realised on its own and has no way to search
    /// a document it never sees the whole of.
    /// </remarks>
    private static Dictionary<int, List<SvgSourceDiagnostic>> Mark(
        IReadOnlyList<SvgSourceLine> lines,
        IReadOnlyList<SvgSourceDiagnostic> diagnostics)
    {
        var marks = new Dictionary<int, List<SvgSourceDiagnostic>>();

        foreach (var diagnostic in diagnostics)
        {
            var low = 0;
            var high = lines.Count - 1;
            var found = -1;

            while (low <= high)
            {
                var middle = (low + high) / 2;

                if (lines[middle].Start <= diagnostic.Start)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (found < 0)
            {
                continue;
            }

            var number = lines[found].Number;

            if (!marks.TryGetValue(number, out var on))
            {
                marks[number] = on = new List<SvgSourceDiagnostic>();
            }

            on.Add(diagnostic);
        }

        return marks;
    }

    /// <summary>Builds one row: its number, and its text coloured a piece at a time.</summary>
    /// <remarks>
    /// The runs are made here, as a row is realised, rather than held with the tokens — which is
    /// what keeps a drawing of a hundred thousand lines costing what the forty on screen cost.
    /// </remarks>
    private Control BuildLine()
    {
        var number = new TextBlock
        {
            Width = 44d,
            Margin = new Thickness(0d, 0d, 8d, 0d),
            TextAlignment = TextAlignment.Right,
            Foreground = SourceBrush(null),
        };

        var text = new SelectableTextBlock();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { number, text } };

        row.DataContextChanged += (_, _) =>
        {
            if (row.DataContext is not SvgSourceLine line)
            {
                return;
            }

            number.Text = line.Number.ToString(CultureInfo.CurrentCulture);

            var marks = _sourceMarks.TryGetValue(line.Number, out var on) ? on : null;

            number.Foreground = marks is null ? SourceBrush(null) : ErrorBrush();
            ToolTip.SetTip(text, marks is null ? null : string.Join("\n", marks.Select(m => m.Message)));

            var underline = marks is null ? null : Underline();

            var inlines = new InlineCollection();
            var coloured = Math.Min(line.Tokens.Count, SvgSourceHighlighter.RowTokenLimit);

            for (var index = 0; index < coloured; index++)
            {
                var token = line.Tokens[index];

                var run = new Run(token.Text) { Foreground = SourceBrush(token.Kind) };

                // Under the piece that is wrong rather than the line it is on: a line of markup is
                // long, and "something here" is most of what a reader already knows.
                if (marks is { } && marks.Any(m => m.Start < token.Start + token.Length && token.Start < m.Start + m.Length))
                {
                    run.TextDecorations = underline;
                }

                inlines.Add(run);
            }

            if (line.Tokens.Count > coloured)
            {
                inlines.Add(new Run(line.Rest(coloured)) { Foreground = SourceBrush(SvgSourceTokenKind.Text) });
            }

            text.Inlines = inlines;
        };

        return row;
    }

    private IBrush? ErrorBrush()
        => this.TryFindResource("SvgViewerSourceErrorBrush", ActualThemeVariant, out var brush) ? brush as IBrush : null;

    /// <summary>
    /// A red underline, built per pass because the brush it draws with follows the theme.
    /// </summary>
    /// <remarks>
    /// Solid and 2px: a dashed one under 12pt type reads as a smudge, and an editor's wavy line is
    /// not something the text stack draws — <see cref="TextDecoration"/> offers a stroke, a
    /// thickness and a dash array, so weight is the only dial that makes a mark obvious.
    /// </remarks>
    private TextDecorationCollection Underline() => new()
    {
        new TextDecoration
        {
            Location = TextDecorationLocation.Underline,
            Stroke = ErrorBrush(),
            StrokeThickness = 2d,
        },
    };

    /// <summary>The brush for a kind of token, or for a line number when given none.</summary>
    private IBrush? SourceBrush(SvgSourceTokenKind? kind)
    {
        var key = kind switch
        {
            null => "SvgViewerSourceLineNumberBrush",
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

        return this.TryFindResource(key, ActualThemeVariant, out var brush) ? brush as IBrush : null;
    }

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

    private void ShowError(string? message)
    {
        _errorText.Text = message ?? string.Empty;
        _errorPanel.IsVisible = !string.IsNullOrEmpty(message);

        if (!string.IsNullOrEmpty(message))
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
