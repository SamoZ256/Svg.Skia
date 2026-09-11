// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>
/// One recipe: the drawings it paints, what it declares, and what it replaces.
/// </summary>
/// <remarks>
/// <para>
/// The shape of a drawing's tab, because a recipe is read the same way one is: the thing itself in
/// the room, and what it is made of in the strip beside it. The canvas holds the drawings under this
/// recipe rather than a drawing of its own — a recipe has no ink, and the only way to see what a
/// rule does is to see it done — and the strip splits into the declarations above and the rules
/// below, where a drawing's tab puts the parameters above and the elements below. Both answer "what
/// is this file made of", which is why they take the same slot.
/// </para>
/// <para>
/// It was a text editor. That made sense while the text was the truth; once the tree became it, the
/// editor was a read-only transcript of what the tree would write — a second account of the file
/// that could not be edited and had to be re-coloured whenever anything changed. What a recipe is
/// made of is exactly two things, and both already had panels.
/// </para>
/// <para>
/// A view onto a <see cref="RecipeWorkspace"/> and not the owner of its tree: the same recipe is
/// edited from the colours and parameters of the drawings under it, and all of it is one history.
/// </para>
/// </remarks>
public sealed class RecipePanel : UserControl
{
    /// <summary>What a relative include is read against, which nothing here writes one of.</summary>
    private static readonly Uri Home = new("avares://Svg.Studio/");

    private readonly SvgViewerCanvas _canvas = new();

    private readonly SvgViewerDeclarationPanel _parameters = new();

    private readonly ContentControl _rulesHost = new();

    private readonly TextBlock _status = new()
    {
        Opacity = 0.65,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly TextBlock _fault = new()
    {
        IsVisible = false,
        Margin = new Thickness(12, 0, 0, 0),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly TextBlock _zoom = new()
    {
        Width = 64,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        FontFamily = new FontFamily("Menlo, Consolas, monospace"),
        Text = "100%"
    };

    /// <summary>The documents on the canvas.</summary>
    /// <remarks>
    /// Held because a picture belongs to the document that built it: the canvas only borrows one, so
    /// both have to be let go together, in that order.
    /// </remarks>
    private readonly List<SvgViewerDocument> _loaded = new();

    /// <summary>Which drawings this recipe paints, and how to build one the way the project does.</summary>
    private readonly Func<IReadOnlyList<SvgcProjectDrawing>> _drawings;

    private readonly Func<SvgcProjectDrawing, string, string> _build;

    private SvgViewerDeclarationCommands? _commands;

    /// <param name="drawings">
    /// The project's drawings that are built through this recipe. Asked again on every gesture, so
    /// a recipe taken off a group stops painting it without anything here being told.
    /// </param>
    /// <param name="build">How the host turns one drawing's file into what the project builds.</param>
    public RecipePanel(
        RecipeWorkspace workspace,
        Func<IReadOnlyList<SvgcProjectDrawing>> drawings,
        Func<SvgcProjectDrawing, string, string> build)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _drawings = drawings ?? throw new ArgumentNullException(nameof(drawings));
        _build = build ?? throw new ArgumentNullException(nameof(build));

        // The viewer's own strip wears these, and a second strip meant to look the same would not
        // stay that way if it wore a copy.
        Styles.Add(new StyleInclude(Home)
        {
            Source = new Uri("avares://Svg.Viewer.Skia.Avalonia/SvgViewerPaneTabs.axaml")
        });

        Resources.MergedDictionaries.Add(new ResourceInclude(Home)
        {
            Source = new Uri("avares://Svg.Viewer.Skia.Avalonia/SvgViewerSourceBrushes.axaml")
        });

        _parameters.ValueChanged += (_, _) =>
        {
            Bind();

            // The readouts beside each rule are what the expressions come to, so they follow.
            (_rulesHost.Content as ReplacementsPanel)?.Readouts();
        };

        _parameters.AddRequested += async (_, _) => await AddParameterAsync().ConfigureAwait(true);
        _parameters.CommitRequested += (_, _) => _commands?.SetDefaults();
        _parameters.EditRequested += async (_, row) =>
        {
            if (_commands is { } commands)
            {
                await commands.EditAsync(TopLevel.GetTopLevel(this), row).ConfigureAwait(true);
            }
        };
        _parameters.RemoveRequested += (_, row) => _commands?.Remove(row);
        _parameters.LetCommitted += (_, let) => _commands?.CommitLet(let);
        _parameters.LetRemoveRequested += (_, let) => _commands?.RemoveLet(let);
        _parameters.LetMoveRequested = (let, to) => _commands?.MoveLet(let, to) == true;
        _parameters.ParameterMoveRequested = (row, to) => _commands?.MoveParameter(row, to) == true;

        _canvas.ViewChanged += (_, _) =>
            _zoom.Text = (_canvas.Scale * 100d).ToString("0", CultureInfo.CurrentCulture) + "%";

        Content = Body();

        // One rebuild per gesture, whichever panel made it — including this panel's own writes.
        workspace.Edited += (_, _) => Show();
        workspace.ModifiedChanged += (_, modified) => ModifiedChanged?.Invoke(this, modified);

        _rulesHost.Content = new ReplacementsPanel(workspace, null, Values);

        Show();
    }

    /// <summary>The recipe this is a view of.</summary>
    public RecipeWorkspace Workspace { get; }

    /// <summary>The file this is showing.</summary>
    public string Path => Workspace.Path;

    /// <summary>The recipe as text: what the tree writes, which is what a save would put down.</summary>
    public string Text => Workspace.Text;

    /// <summary>Whether the recipe has edits that are not on disk.</summary>
    public bool IsModified => Workspace.IsModified;

    /// <summary>Raised when <see cref="IsModified"/> changes, for a host that marks its tab.</summary>
    public event EventHandler<bool>? ModifiedChanged;

    /// <inheritdoc cref="RecipeWorkspace.Fault"/>
    public string? Fault => Workspace.Fault;

    /// <summary>The rules on show, for a host or a test that drives them.</summary>
    public ReplacementsPanel Rules => (ReplacementsPanel)_rulesHost.Content!;

    /// <summary>What the parameters currently declare, for the same.</summary>
    public SvgViewerDeclarationPanel Parameters => _parameters;

    /// <summary>The drawings this recipe paints, as they are being shown.</summary>
    public IReadOnlyList<SvgViewerDocument> Shown => _loaded;

    /// <summary>Replaces the whole recipe, as one thing to take back.</summary>
    /// <remarks>
    /// How text arrives from outside now that nothing here is typed into: a host reverting a file,
    /// or a test standing in for somebody who edited it elsewhere. Refused where it would not read.
    /// </remarks>
    public bool SetText(string recipeText)
        => Workspace.Commit("edit the recipe", (string _) => recipeText ?? string.Empty) is null;

    /// <summary>Takes back the last edit, or puts it back.</summary>
    public bool Undo() => Workspace.Undo();

    /// <inheritdoc cref="Undo"/>
    public bool Redo() => Workspace.Redo();

    /// <inheritdoc cref="RecipeWorkspace.Save"/>
    public void Save() => Workspace.Save();

    /// <summary>
    /// Asks for a parameter and writes it into the recipe.
    /// </summary>
    /// <remarks>
    /// Public for the reason the viewer's is: it is the half of the button a test can drive, the
    /// other half being a modal.
    /// </remarks>
    public async Task<bool> AddParameterAsync()
        => _commands is { } commands
           && await commands.AddAsync(TopLevel.GetTopLevel(this)).ConfigureAwait(true);

    /// <summary>How the panel asks what parameter to declare. Replaceable, and faked in tests.</summary>
    public ISvgViewerParameterDialogService ParameterDialogService { get; set; } =
        new SvgViewerParameterDialogService();

    /// <summary>Reads the recipe again and shows what it now says.</summary>
    private void Show()
    {
        ShowParameters();
        ShowDrawings();
        ShowStatus();
    }

    private Control Body()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,340") };

        var centre = new DockPanel();
        var tools = Tools();

        centre.Children.Add(tools);
        DockPanel.SetDock(tools, Dock.Top);
        centre.Children.Add(_canvas);

        grid.Children.Add(centre);

        var splitter = new GridSplitter { Background = Brushes.Transparent };

        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);

        // The divider is on the strip rather than on each panel in it, or the splitter between them
        // leaves a six pixel hole in a line that runs the whole height of the tab.
        var right = new Border
        {
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#20808080")),
            Child = Side()
        };

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var whole = new DockPanel();
        var status = Status();

        whole.Children.Add(status);
        DockPanel.SetDock(status, Dock.Bottom);
        whole.Children.Add(grid);

        return whole;
    }

    /// <summary>
    /// The strip beside the drawings: what the recipe declares, and what it replaces.
    /// </summary>
    /// <remarks>
    /// Two rows rather than two tabs, for the reason the viewer gives for its own: the two are read
    /// together. A rule's expression is written out of the names above it, and a parameter is
    /// declared because a rule below wants it. Behind a tab, each hides the other.
    /// </remarks>
    private Control Side()
    {
        var side = new Grid { RowDefinitions = new RowDefinitions("*,6,220") };

        side.Children.Add(_parameters);

        var splitter = new GridSplitter { Background = Brushes.Transparent };

        Grid.SetRow(splitter, 1);
        side.Children.Add(splitter);

        var rules = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#20808080")),
            Child = _rulesHost
        };

        Grid.SetRow(rules, 2);
        side.Children.Add(rules);

        return side;
    }

    private Control Tools()
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(10, 8, 10, 6)
        };

        var bounds = new ToggleButton
        {
            Content = "Bounds",
            IsChecked = _canvas.ShowBounds,
            [ToolTip.TipProperty] = "Outline each drawing's own edges"
        };

        bounds.IsCheckedChanged += (_, _) => _canvas.ShowBounds = bounds.IsChecked == true;

        bar.Children.Add(Tool("Fit", "Fit to window", () => _canvas.Fit()));
        bar.Children.Add(Tool("1:1", "Actual size", () => _canvas.ActualSize()));
        bar.Children.Add(Tool("−", "Zoom out, or scroll down", () => _canvas.ZoomOut()));
        bar.Children.Add(_zoom);
        bar.Children.Add(Tool("+", "Zoom in, or scroll up", () => _canvas.ZoomIn()));
        bar.Children.Add(bounds);

        return bar;
    }

    private static Button Tool(string content, string tip, Action click)
    {
        var button = new Button { Content = content, [ToolTip.TipProperty] = tip };

        button.Click += (_, _) => click();

        return button;
    }

    /// <summary>
    /// The line under the tab: what this recipe is and what it comes to.
    /// </summary>
    /// <remarks>
    /// Two columns rather than two rows, as the viewer's is: the fault arrives and goes as the
    /// recipe is edited, and a row that appeared with it would shove the canvas up and down.
    /// </remarks>
    private Control Status()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        grid.Children.Add(_status);

        Grid.SetColumn(_fault, 1);
        grid.Children.Add(_fault);

        _fault[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SvgViewerSourceErrorBrush");

        return new Border
        {
            Padding = new Thickness(8, 4),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#20808080")),
            Child = grid
        };
    }

    private void ShowStatus()
    {
        var rules = Workspace.Recipe?.Rules.Count ?? 0;

        _status.Text = string.Join(
            " · ",
            System.IO.Path.GetFileName(Path),
            Count(_loaded.Count, "drawing"),
            Count(rules, "rule"));

        _fault.Text = Fault;
        _fault.IsVisible = Fault is { };
    }

    private static string Count(int many, string what) => many == 1 ? $"1 {what}" : $"{many} {what}s";

    /// <summary>Fills the declaration panel from the recipe's own <c>code</c> block.</summary>
    /// <remarks>
    /// Read from the recipe text rather than from a drawing built through it: a recipe being given
    /// its first parameter has no drawing that can be built yet, and the panel has to show the row
    /// that was just added either way.
    /// </remarks>
    private void ShowParameters()
    {
        _commands ??= new SvgViewerDeclarationCommands(
            () => Workspace.Text,
            Write,
            () => _parameters.Parameters ?? Array.Empty<SvgViewerParameter>(),
            () => ParameterDialogService);

        var declarations = SvgExpressionDeclarations.Parse(Workspace.Text, out var diagnostics);

        // Empty and not null. Null reads as "no file" and takes the Add button away with it, which
        // is the one button a recipe declaring nothing yet needs.
        _parameters.Parameters = SvgViewerParameterFactory.Create(declarations.Parameters);
        _parameters.ShowLets(declarations.Lets);
        _parameters.Trouble = diagnostics.Count > 0 ? diagnostics[0].Message : null;
    }

    /// <summary>Writes one edit into the recipe, and says what it refused.</summary>
    private bool Write(string label, Func<Svg.SourceEditing.SvgSourceDocument, string?> edit)
    {
        if (Workspace.Commit(label, edit) is { } refusal)
        {
            _fault.Text = refusal;
            _fault.IsVisible = true;

            return false;
        }

        return true;
    }

    /// <summary>
    /// Draws the drawings this recipe paints, as the project builds them.
    /// </summary>
    /// <remarks>
    /// At the sizes the project builds them at, with nothing scaled to fit, for the reason a group's
    /// canvas gives: the canvas has a zoom of its own, so a spread true to itself can be looked at
    /// whole or up close.
    /// </remarks>
    private void ShowDrawings()
    {
        Release();

        var drawings = _drawings();

        if (drawings.Count == 0)
        {
            _canvas.Show(Array.Empty<SvgViewerPlacement>());

            return;
        }

        var items = new List<SvgViewerSpread.Item>(drawings.Count);

        foreach (var drawing in drawings)
        {
            if (Draw(drawing) is not { } document)
            {
                continue;
            }

            _loaded.Add(document);

            items.Add(new SvgViewerSpread.Item(
                document.Svg,
                document.Svg.Picture?.CullRect.Size ?? default,
                System.IO.Path.GetFileName(drawing.ResolvedInput)));
        }

        _canvas.Show(SvgViewerSpread.Of(items));
    }

    /// <summary>One drawing built through this recipe, or null where it would not build.</summary>
    private SvgViewerDocument? Draw(SvgcProjectDrawing drawing)
    {
        try
        {
            var document = SvgViewerDocument.Load(
                drawing.ResolvedInput,
                ProjectWorkspace.SizeOf(drawing),
                text => _build(drawing, text));

            try
            {
                document.Svg.SetExpressionValues(Values() is { } evaluator ? Bound(evaluator) : Seeded(document));
            }
            catch (ExprException)
            {
            }

            return document;
        }
        catch (Exception)
        {
            // Anything: this is user data reaching a parser, and it arrives as an XmlException, a
            // FormatException, one of the IO exceptions or the loader's own refusal. One drawing
            // that will not build should cost its own square and not the tab.
            return null;
        }
    }

    /// <summary>What the panel's rows are set to, which is what the drawings are painted with.</summary>
    private Dictionary<string, ExprValue> Bound(ExprEvaluator evaluator)
    {
        _ = evaluator;

        var values = new Dictionary<string, ExprValue>(StringComparer.Ordinal);

        foreach (var row in _parameters.Parameters ?? Array.Empty<SvgViewerParameter>())
        {
            values[row.Name] = row.ToExprValue();
        }

        return values;
    }

    /// <summary>The declared defaults, for a drawing shown before any row has been touched.</summary>
    private static Dictionary<string, ExprValue> Seeded(SvgViewerDocument document)
    {
        var values = new Dictionary<string, ExprValue>(StringComparer.Ordinal);

        foreach (var row in SvgViewerParameterFactory.Create(document.Declarations.Parameters))
        {
            values[row.Name] = row.ToExprValue();
        }

        return values;
    }

    /// <summary>What the rules' expressions come to right now, or null while nothing can be worked out.</summary>
    private ExprEvaluator? Values()
    {
        try
        {
            return ExprEvaluator.Create(
                SvgExpressionDeclarations.Parse(Workspace.Text, out _),
                Bound(null!));
        }
        catch (Exception failure) when (failure is ExprException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Paints the drawings again for a value that moved, without rebuilding them.</summary>
    private void Bind()
    {
        var values = Bound(null!);

        foreach (var document in _loaded)
        {
            try
            {
                document.Svg.SetExpressionValues(values);
            }
            catch (ExprException)
            {
                // A value a drawing will not take leaves its last rendering up, as in a viewer.
            }
        }

        _canvas.Publish();
    }

    /// <summary>Lets go of the drawings, and of the documents that own them.</summary>
    /// <remarks>
    /// The canvas first: a picture belongs to its document, so one still placed after the document
    /// is disposed is a surface drawing freed memory.
    /// </remarks>
    private void Release()
    {
        _canvas.Show(Array.Empty<SvgViewerPlacement>());

        foreach (var document in _loaded)
        {
            document.Dispose();
        }

        _loaded.Clear();
    }
}
