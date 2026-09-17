// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using SkiaSharp;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions;
using Svg.Highlighting;
using Svg.Skia;
using Svg.SourceEditing;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>
/// One node of a project: its settings, and — for a group — what it builds.
/// </summary>
/// <remarks>
/// Built in code rather than declared, for the reason the tabs are: the rows depend on what kind of
/// node this is, so a template would have to be chosen at runtime anyway. A group fills a tab, since
/// the list of what it builds wants the room; a drawing is its settings alone, and fits the pane
/// beside the tree — its own tab is the viewer, which belongs to another package and has a right
/// pane of its own about the drawing rather than about the project.
/// </remarks>
public sealed class GroupPanel : UserControl
{
    // Named because the canvas beside it has buttons of its own, and a test asking what the settings
    // offer has to be able to say which half it means.
    private readonly StackPanel _properties = new() { Name = "Settings", Spacing = 8, Margin = new Thickness(10) };
    private readonly SvgViewerCanvas _canvas = new();
    private readonly TextBlock _heading = new() { FontWeight = FontWeight.SemiBold, Margin = new Thickness(10, 10, 10, 0) };

    private readonly TextBlock _zoom = new()
    {
        Width = 64,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        FontFamily = new FontFamily("Menlo, Consolas, monospace"),
        Text = "100%"
    };

    private readonly TextBlock _notice = new()
    {
        IsVisible = false,
        Margin = new Thickness(10, 0, 10, 6),
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.65
    };

    /// <summary>What each drawing of the group was last built into, in the group's own order.</summary>
    /// <remarks>
    /// Held because a picture belongs to the document that built it: the canvas only borrows one, so
    /// both have to be let go together, in that order. And held against the next build, which reuses
    /// whatever it would only have built again.
    /// </remarks>
    private IReadOnlyList<Drawn> _built = Array.Empty<Drawn>();

    /// <summary>Each drawing on the canvas, paired with where the spread put it.</summary>
    /// <remarks>
    /// The pair is the whole of what a click needs and neither half has it: a placement carries no
    /// idea which project node it came from, and the built record carries no idea where on the
    /// canvas it ended up. Both were thrown away as locals before anything asked.
    /// </remarks>
    private readonly List<(SvgViewerPlacement Placement, Drawn Built)> _shown = new();

    /// <summary>Each group under this one that has a place, paired with the frame drawn round it.</summary>
    /// <remarks>
    /// A frame is not a placement, so the canvas answers nothing about one: this is what says which
    /// group a press on a frame took hold of.
    /// </remarks>
    private readonly List<(SvgViewerFrame Frame, ProjectGroup Group)> _framed = new();

    private readonly SvgViewerElementTree _tree = new();

    private readonly SvgViewerDeclarationPanel _parameters = new();

    /// <summary>What the parameters do not reach, when the group's drawings declare different things.</summary>
    private readonly TextBlock _parameterNote = new()
    {
        Margin = new Thickness(10, 0, 10, 10),
        Opacity = 0.6,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false
    };

    /// <summary>Where the selected drawing keeps its declarations, or null when nothing is selected.</summary>
    private ISvgViewerDeclarationTarget? _target;


    private SvgViewerDeclarationCommands? _commands;

    /// <summary>The Element tab's content: a panel for the picked element, or a line saying why not.</summary>
    private readonly ContentControl _elementHost = new();

    private readonly TextBlock _elementNote = new()
    {
        Margin = new Thickness(10),
        Opacity = 0.6,
        TextWrapping = TextWrapping.Wrap
    };

    /// <summary>Which of the group's drawings the tree is showing, since it can only show one.</summary>
    private readonly TextBlock _showing = new()
    {
        Margin = new Thickness(10, 8, 10, 0),
        Opacity = 0.55,
        FontSize = 11,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Text = "Click a drawing to see what it is made of."
    };

    /// <summary>The drawing the tree is showing, and where it sits on the canvas.</summary>
    private (SvgViewerPlacement Placement, Drawn Built)? _inspecting;

    /// <summary>The element being looked at inside it, by the address that survives a rebuild.</summary>
    /// <remarks>
    /// The address and not the element: a drawing read again is a different graph, so the key is the
    /// only thing the two have in common. It is what <see cref="Pick"/> already selects rows by.
    /// </remarks>
    private string? _picked;

    /// <summary>Whether this is the tab being looked at.</summary>
    /// <remarks>
    /// A tab's content leaves the visual tree when another tab is picked, so a board is laid out
    /// only while it is on screen — and every save refreshes every open panel.
    /// </remarks>
    private bool _watched;

    /// <summary>Whether an edit arrived while this tab was not the one being looked at.</summary>
    /// <remarks>
    /// True to begin with, since a tab that has never been attached has never built anything.
    /// </remarks>
    private bool _stale = true;

    /// <summary>Edits typed here and not yet written to the project, by setting name.</summary>
    /// <remarks>
    /// Held rather than applied, so a tab saves what was typed in it and nothing else. The cost is
    /// that the tree, the values other tabs inherit and the drawings already open all go on showing
    /// what is in the file until this is saved — the document is the one thing they all read.
    /// </remarks>
    private readonly Dictionary<string, string?> _pending = new(StringComparer.Ordinal);

    public GroupPanel(ProjectWorkspace workspace, ProjectNode node)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Node = node ?? throw new ArgumentNullException(nameof(node));

        // The viewer's own strip wears these, and a second strip that was meant to look the same
        // would not stay that way if it wore a copy.
        Styles.Add(new StyleInclude(Home)
        {
            Source = new Uri("avares://Svg.Viewer.Skia.Avalonia/SvgViewerPaneTabs.axaml")
        });

        Content = node is ProjectGroup ? Built() : Alone();

        // Harmless on a drawing's settings pane, which has no canvas and so no placements to fall on.
        _canvas.Picked += (_, at) => Pick(at);
        _canvas.Grip = Held;
        _canvas.Moved += (_, move) => Placed(move);

        _parameters.ValueChanged += (_, _) => Bind();
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

        // The other direction: a row picked in the tree rings the drawing it belongs to, which is
        // the one the tree is showing.
        _tree.Selected += (_, node) =>
        {
            if (node is { } picked && _inspecting is { } inspecting && inspecting.Built.Svg is { } svg)
            {
                Ring(inspecting.Placement, svg, picked.Element);
            }

            _picked = node?.AddressKey;

            ShowElement(node?.AddressKey);
        };

        // A group saved in another tab changes what this one inherits, so every tab follows the
        // one document rather than the copy it was opened with. Anything typed here and not saved
        // survives it.
        workspace.Edited += (_, _) => Refresh();

        Refresh();
    }

    /// <summary>A group's tab: what it builds beside the settings that decide it.</summary>
    private Control Built()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,6,340")
        };

        var centre = new DockPanel();
        var tools = Tools();

        centre.Children.Add(_heading);
        DockPanel.SetDock(_heading, Dock.Top);

        centre.Children.Add(tools);
        DockPanel.SetDock(tools, Dock.Top);

        centre.Children.Add(_notice);
        DockPanel.SetDock(_notice, Dock.Top);

        centre.Children.Add(_canvas);

        grid.Children.Add(centre);

        var splitter = new GridSplitter { Background = Brushes.Transparent };

        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);

        var right = new Border
        {
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#20808080")),
            Child = Side()
        };

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        return grid;
    }

    /// <summary>
    /// The strip beside a group's drawings: what the settings say, and what the drawings are made of.
    /// </summary>
    /// <remarks>
    /// The same two halves the viewer's own side pane has, and deliberately the same shape — a strip
    /// of tabs over a tree, split by a splitter. Built in code because everything in this panel is,
    /// and wearing the viewer's tab style so the two strips cannot drift apart; a tab of one alone
    /// is a strip waiting for the colours and the parameters to join it.
    /// </remarks>
    private Control Side()
    {
        var side = new Grid { RowDefinitions = new RowDefinitions("*,6,220") };

        var tabs = new TabControl { Classes = { "panes" }, Padding = new Thickness(0) };

        tabs.Items.Add(new TabItem
        {
            Header = "Project",
            Content = new ScrollViewer { Content = _properties }
        });

        var parameters = new DockPanel();

        DockPanel.SetDock(_parameterNote, Dock.Bottom);
        parameters.Children.Add(_parameterNote);
        parameters.Children.Add(_parameters);

        tabs.Items.Add(new TabItem { Header = "Parameters", Content = parameters });
        tabs.Items.Add(new TabItem { Header = "Element", Content = _elementHost });

        side.Children.Add(tabs);

        var splitter = new GridSplitter { Background = Brushes.Transparent };

        Grid.SetRow(splitter, 1);
        side.Children.Add(splitter);

        var below = new DockPanel();

        DockPanel.SetDock(_showing, Dock.Top);
        below.Children.Add(_showing);
        below.Children.Add(_tree);

        var host = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#20808080")),
            Child = below
        };

        Grid.SetRow(host, 2);
        side.Children.Add(host);

        return side;
    }

    /// <summary>A drawing's settings, with nothing beside them, for the pane.</summary>
    private Control Alone()
    {
        var panel = new DockPanel();

        panel.Children.Add(_heading);
        DockPanel.SetDock(_heading, Dock.Top);
        panel.Children.Add(new ScrollViewer { Content = _properties });

        return panel;
    }

    public ProjectWorkspace Workspace { get; }

    /// <summary>The node this is about.</summary>
    public ProjectNode Node { get; }

    /// <summary>Whether anything typed here has not been written to the project.</summary>
    public bool IsModified => _pending.Count > 0;

    /// <summary>Raised when <see cref="IsModified"/> changes, for a host that marks its tab.</summary>
    public event EventHandler<bool>? ModifiedChanged;

    /// <summary>Where a drawing's text is written: the tab it is open in, or nothing.</summary>
    /// <remarks>
    /// Answered by the host, because deciding it means knowing which tabs are open and this panel
    /// knows about none of them. Null is answered with the file, which the panel holds the document
    /// for.
    /// </remarks>
    public Func<ProjectDrawing, ISvgViewerDeclarationTarget?>? TargetOf { get; set; }

    /// <summary>How the parameters tab asks what to declare. Replaceable, and faked in tests.</summary>
    public ISvgViewerParameterDialogService ParameterDialogService { get; set; } =
        new SvgViewerParameterDialogService();

    /// <summary>Writes what was typed here into the project, and the project to its file.</summary>
    public void Save()
    {
        // What is in the box being typed in, before deciding there is nothing to save. Recording
        // an edit when the box loses focus is what lets a half-typed value be rejected while it is
        // still on screen, but it also meant Ctrl+S did nothing at all until the caret left.
        var typing = Typing();

        if (typing is { } box)
        {
            var typed = (string)box.Tag!;

            // Checked now, since typing records without checking. Put back rather than written,
            // the same as the caret leaving would.
            if (!Edit(typed, box.Text))
            {
                box.Text = Value(Node, typed);
            }
        }

        if (_pending.Count == 0)
        {
            return;
        }

        var caret = typing?.CaretIndex ?? 0;
        var setting = typing?.Tag as string;

        var was = IsModified;

        foreach (var edit in _pending)
        {
            Write(Node, edit.Key, edit.Value);
        }

        _pending.Clear();

        // The file first: a tab that reports itself saved when the write threw would be lying, and
        // the edits are already in the document either way.
        Workspace.Save();

        // What is true, not the false this used to announce. Saving rebuilds the rows, which
        // detaches whichever box had focus, and a box losing focus records what is in it — so a
        // save can end with something pending again, and saying "saved" there left the tab with
        // no mark and an unsaved warning waiting at the close button.
        Announce(was);

        // The rows were rebuilt under whoever was typing, so put them back where they were rather
        // than making a save cost the caret.
        Resume(setting, caret);
    }

    /// <summary>The box being typed in, if the caret is in one of this panel's.</summary>
    private TextBox? Typing()
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox box
           && box.Tag is string
           && ReferenceEquals(box.FindAncestorOfType<GroupPanel>(), this)
            ? box
            : null;

    /// <summary>Puts the caret back in the box for <paramref name="setting"/>, where it was.</summary>
    private void Resume(string? setting, int caret)
    {
        if (setting is null)
        {
            return;
        }

        var box = _properties.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(candidate => Equals(candidate.Tag, setting));

        if (box is null)
        {
            return;
        }

        box.Focus();
        box.CaretIndex = Math.Min(caret, box.Text?.Length ?? 0);
    }

    /// <summary>Says so if the tab's unsaved state has changed since <paramref name="was"/>.</summary>
    private void Announce(bool was)
    {
        if (IsModified != was)
        {
            ModifiedChanged?.Invoke(this, IsModified);
        }
    }

    /// <summary>Puts the group's settings on the right, and the drawings under it in the centre.</summary>
    public void Refresh()
    {
        _heading.Text = ProjectWorkspace.Label(Node);

        if (Node is ProjectGroup)
        {
            if (_watched)
            {
                ShowDrawings();
            }
            else
            {
                _stale = true;
            }
        }

        ShowProperties(Node);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _watched = true;

        if (Node is ProjectGroup && _stale)
        {
            ShowDrawings();
        }
    }

    /// <summary>
    /// Stops laying the board out, and keeps it.
    /// </summary>
    /// <remarks>
    /// The drawings used to be disposed here and read again on the way back, so picking another tab
    /// and returning re-parsed the whole group and fitted the board afresh — and so did dragging
    /// this tab along the strip, which removes the item and puts it back. A tab nobody edited while
    /// it was away has nothing to do on its return.
    ///
    /// The price is that every open group tab holds its pictures rather than only the one on screen.
    /// <see cref="Close"/> is what lets them go, from the one place a tab is discarded.
    /// </remarks>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _watched = false;
    }

    /// <summary>Lets go of the drawings this tab built. The tab is finished with after it.</summary>
    public void Close() => Release();

    /// <summary>
    /// Shows the parameters of the drawing that is selected, and points the commands at wherever
    /// that drawing keeps its declarations.
    /// </summary>
    /// <remarks>
    /// The selection is the subject, so this behaves as the drawing's own tab would: the rows come
    /// from the drawing <em>as built</em>.
    ///
    /// Rebuilt on every selection, because the target changes with it.
    /// </remarks>
    private void ShowParameters()
    {
        if (_inspecting is not { } inspecting || inspecting.Built.Document is not { } document)
        {
            _target = null;
            _commands = null;
            _parameters.Parameters = null;
            _parameters.ShowLets(null);

            Note("Pick a drawing to see what it declares.");

            return;
        }

        // The host answers for a tab, which is what it knows about. What is left is the file, and
        // the document that was read from it is here rather than there.
        _target = TargetOf?.Invoke(inspecting.Built.Drawing)
                  ?? (document.SourceText is { } text
                      ? new DrawingTarget(Workspace, inspecting.Built.Drawing)
                      : null);

        if (_target is null)
        {
            _commands = null;
            _parameters.Parameters = null;
            _parameters.ShowLets(null);

            // Only when the file could not be read; everything else has somewhere to go.
            Note("This drawing could not be read, so there is nowhere to write its declarations.");

            return;
        }

        _commands = new SvgViewerDeclarationCommands(
            () => _target?.Text ?? string.Empty,
            Splice,
            () => _parameters.Parameters ?? Array.Empty<SvgViewerParameter>(),
            () => ParameterDialogService);

        // Empty and not null. Null reads as "no document" and takes the Add button away with it,
        // which is the one button a drawing declaring nothing yet needs.
        _parameters.Parameters = Carried(SvgViewerParameterFactory.Create(document.Declarations.Parameters));

        _parameters.ShowLets(document.Declarations.Lets);

        Note(null);
    }

    /// <summary>
    /// Asks for a parameter and writes it where the selected drawing keeps its declarations.
    /// </summary>
    /// <remarks>
    /// Public for the reason the viewer's is: it is the half of the button a test can drive, the
    /// other half being a modal.
    /// </remarks>
    /// <returns>Whether anything was written.</returns>
    public async Task<bool> AddParameterAsync()
        => _commands is { } commands
           && await commands.AddAsync(TopLevel.GetTopLevel(this)).ConfigureAwait(true);

    /// <summary>
    /// Keeps the value on any row still declared the same way, by this drawing or one sharing it.
    /// </summary>
    /// <remarks>
    /// The rows are built again whenever the declarations change, and adding a parameter is a
    /// change; a slider somebody had dragged would otherwise snap back because they pressed a button
    /// about a different parameter. Carried across a change of drawing only where the two declare
    /// the same parameters, since that is exactly when the value was bound into both.
    /// </remarks>
    /// <remarks>
    /// Asked of the two row lists rather than of the two drawings, which is the same question one
    /// step nearer: a row carries the declaration it was built from, and these are the rows whose
    /// values are actually in play.
    /// </remarks>
    private IReadOnlyList<SvgViewerParameter> Carried(IReadOnlyList<SvgViewerParameter> rebuilt)
    {
        var was = _parameters.Parameters;

        if (was is null || !Same(was.Select(row => row.Declaration).ToList(), rebuilt.Select(row => row.Declaration).ToList()))
        {
            return rebuilt;
        }

        // Known one for one by the check above, so the rows pair by position.
        for (var index = 0; index < rebuilt.Count; index++)
        {
            if (was[index] is { IsModified: true } had)
            {
                rebuilt[index].TrySet(had.ToExprValue());
            }
        }

        return rebuilt;
    }

    /// <summary>Puts a declaration edit where the drawing keeps them, or says why it would not go.</summary>
    private bool Splice(string label, Func<SvgSourceDocument, string?> edit)
    {
        if (_target is not { } target)
        {
            Says("There is nowhere to write this drawing's declarations.");

            return false;
        }

        var was = target.Text;

        if (target.Commit(label, edit) is { } refusal)
        {
            Says(refusal);

            return false;
        }

        Says(null);

        if (string.Equals(target.Text, was, StringComparison.Ordinal))
        {
            return false;
        }

        // The drawing is built from what was just written, so the canvas is out of date; and the
        // rows are read from the drawing, so they are too. Straight away rather than on the buffer's
        // Edited, which is debounced by a fifth of a second -- the right delay for somebody typing
        // and the wrong one for a button they just pressed.
        ShowDrawings();

        return true;
    }

    private void Note(string? said)
    {
        _parameterNote.Text = said ?? string.Empty;
        _parameterNote.IsVisible = said is { Length: > 0 };
    }

    /// <summary>
    /// What a drawing renders with before anybody touches it: the values its panel would show.
    /// </summary>
    /// <remarks>
    /// Not the declared defaults. Binding those refuses the whole set the moment one parameter has
    /// no default — which a drawing is entitled to do, since a host is expected to supply it — so
    /// a single <c>&lt;param name="whiteColor" type="color" /&gt;</c> left every drawing in the
    /// group on its placeholders, which render grey. The seed is what
    /// <see cref="SvgViewerParameterFactory"/> puts in a row for a declaration that gives it
    /// nothing, and it is what a viewer binds on opening the same drawing: the group's canvas and
    /// the drawing's own tab then show the same picture, which is the whole point of the tab.
    /// </remarks>
    private static Dictionary<string, ExprValue> Seeded(SvgViewerDocument document)
    {
        var values = new Dictionary<string, ExprValue>(StringComparer.Ordinal);

        foreach (var row in SvgViewerParameterFactory.Create(document.Declarations.Parameters))
        {
            values[row.Name] = row.ToExprValue();
        }

        return values;
    }

    /// <summary>The values the panel is showing, as the drawing takes them.</summary>
    private Dictionary<string, ExprValue> Values()
    {
        var values = new Dictionary<string, ExprValue>(StringComparer.Ordinal);

        foreach (var row in _parameters.Parameters ?? Array.Empty<SvgViewerParameter>())
        {
            values[row.Name] = row.ToExprValue();
        }

        return values;
    }

    /// <summary>What the colour readouts are worked out with: this drawing's names and its values.</summary>
    /// <summary>
    /// Shows what the picked element is written with, in the drawing's own file.
    /// </summary>
    /// <remarks>
    /// The address comes from a tree of the drawing that was <em>built</em>, and what is edited is
    /// the text it was made from.
    ///
    /// Rebuilt with the selection rather than kept: it is one element of one drawing, and both
    /// change together.
    /// </remarks>
    private void ShowElement(string? addressKey)
    {
        if (_inspecting is not { } inspecting
            || inspecting.Built.Document is not { } document
            || document.SourceText is not { } source)
        {
            _elementNote.Text = _inspecting is null
                ? "Pick an element to see what it is written with."
                : "This drawing could not be read, so there is nothing to show.";

            _elementHost.Content = _elementNote;

            return;
        }

        if (addressKey is null)
        {
            _elementNote.Text = "Pick an element to see what it is written with.";
            _elementHost.Content = _elementNote;

            return;
        }

        var drawing = inspecting.Built.Drawing;

        var target = TargetOf?.Invoke(drawing) ?? new DrawingTarget(Workspace, drawing);

        var panel = new SvgViewerElementPanel(
            () => target.Text,
            () => target.Text,
            (label, edit) =>
            {
                var refusal = target.Commit(label, edit);

                if (refusal is null)
                {
                    Written();
                }

                return refusal;
            },
            () => Evaluator(document));

        panel.Show(SvgSourceElements.Addresses(target.Text, document.Built(target.Text))
            .TryGetValue(addressKey, out var mine)
            ? mine
            : null);

        _elementHost.Content = panel;
    }

    /// <summary>Reads the drawings again after an element was written, and answers that it went.</summary>
    private bool Written()
    {
        ShowDrawings();

        return true;
    }

    private ExprEvaluator? Evaluator(SvgViewerDocument document)
    {
        try
        {
            return ExprEvaluator.Create(document.Declarations, Values());
        }
        catch (Exception failure) when (failure is ExprException or ArgumentException)
        {
            // A readout nobody can work out is left blank; the row still says what the rule is.
            return null;
        }
    }

    /// <summary>
    /// Binds the values on the panel into every drawing that shares the declarations, and repaints.
    /// </summary>
    /// <remarks>
    /// A value belongs to the declaration it is for, and every drawing declaring that takes it. So
    /// moving one slider moves the family, which is the whole reason to look at them side by side.
    ///
    /// Cheap for a drag: the pictures are the ones already built, and <c>SetExpressionValues</c>
    /// re-evaluates a model each has cached rather than reading or compiling anything again.
    /// </remarks>
    private void Bind()
    {
        if (_inspecting is not { } inspecting)
        {
            return;
        }

        var picked = inspecting.Built;
        var values = Values();

        foreach (var shown in _shown)
        {
            if (shown.Built.Svg is not { } svg || !Shares(picked, shown.Built))
            {
                continue;
            }

            try
            {
                svg.SetExpressionValues(values);
            }
            catch (ExprException)
            {
                // A value a drawing will not take leaves its last rendering up, as in a viewer.
            }
        }

        _canvas.Publish();
    }

    /// <summary>Whether two drawings declare the same parameters, and so take the same values.</summary>
    /// <remarks>
    /// What a drawing declares rather than where it declared it, read off the drawing as built. So
    /// two drawings that each wrote the same block by hand are a family, which is what a set of
    /// icons written from one template is.
    ///
    /// The whole parameter list, in order, and not the names: a bound is as much of what a parameter
    /// is as its type, so two drawings agreeing on all of them have agreed about something, where two
    /// that merely both say <c>hue</c> would be a coincidence acted on.
    ///
    /// The default is the one thing left out, because it is where a drawing starts rather than what
    /// it takes. A set of icons seeded at different colours is the case this is for, and counting the
    /// seed would have been exactly the rule that broke it.
    ///
    /// A drawing declaring nothing shares with nothing but itself -- it has no value anyone could be
    /// moving, and an empty set means "every default", which would put a neighbour that has values
    /// back to its placeholders.
    /// </remarks>
    private static bool Shares(Drawn picked, Drawn other)
        => ReferenceEquals(picked.Drawing, other.Drawing)
           || (picked.Document is { } mine
               && other.Document is { } theirs
               && mine.Declarations.Parameters.Count > 0
               && Same(mine.Declarations.Parameters, theirs.Declarations.Parameters));

    /// <summary>Whether two parameter lists take the same values, in the same order.</summary>
    private static bool Same(
        IReadOnlyList<SvgExpressionParameter> mine,
        IReadOnlyList<SvgExpressionParameter> theirs)
        => mine.Count == theirs.Count
           && !mine.Where((parameter, index) => !parameter.SharesValuesWith(theirs[index])).Any();

    /// <summary>What the group builds, drawn on one canvas.</summary>
    /// <remarks>
    /// Laid out at the sizes the project builds them at, with nothing scaled to fit: the canvas has
    /// a zoom of its own, so a spread that is true to itself can be looked at whole or up close, and
    /// a drawing built at ×4 stands four times one built at ×1 wherever the zoom is. That difference
    /// is what a group's settings do, and the question a group tab exists to answer.
    /// </remarks>
    private void ShowDrawings()
    {
        _stale = false;

        // What was being looked at, since Forget is about to let go of it. Every rebuild of this tab
        // used to empty the Parameters and Element tabs — a settings edit did it, and a drawing
        // moved on the board would do it on every drop.
        var was = _inspecting?.Built.Drawing;
        var picked = _picked;

        Forget();

        var had = _built;
        var reusing = had.ToDictionary(one => one.Drawing);

        var built = ((ProjectGroup)Node).Drawings
            .Select(drawing => Draw(drawing, reusing.GetValueOrDefault(drawing)))
            .ToList();

        _built = built;

        Says(Trouble(built));

        // Only the ones that built, since a drawing that would not read has nothing to place. None
        // of them is a board: the notice says what is wrong with each, and the spread below reads
        // the largest of no drawings at all.
        var drawn = built.Where(drawn => drawn.Svg is { }).ToList();

        if (drawn.Count == 0)
        {
            _canvas.Show(Array.Empty<SvgViewerPlacement>());

            Discard(had, built);

            if (built.Count == 0)
            {
                Says("This group holds no drawings.");
            }

            return;
        }

        // Once for the whole tab rather than per spread, and to the number a spread of all of them
        // would have arrived at: a board that is settled into places must not resize a caption.
        var label = Math.Max(drawn.Max(one => Math.Max(one.Size.Width, one.Size.Height)) * 0.05f, 1f);

        Lay((ProjectGroup)Node, SKPoint.Empty, label, drawn.ToDictionary(one => one.Drawing));

        var placed = _shown.Select(shown => shown.Placement).ToList();
        var frames = _framed.Select(framed => framed.Frame).ToList();

        // Fitted the first time the tab has a board and never again on its own, because everything
        // else is this board being laid out afresh -- a drawing dropped somewhere, a setting typed,
        // a parameter added -- and a fit there would take away whatever was being looked at and move
        // the thing that was just let go.
        if (_canvas.Placements.Count == 0)
        {
            _canvas.Show(placed, frames);
        }
        else
        {
            _canvas.Rearrange(placed, frames);
        }

        // Only now: a picture belongs to its document, and until the line above the canvas was still
        // holding the board it had. The render thread reads a snapshot of its own, so a document
        // disposed while one naming its picture is published is a surface drawing freed memory.
        Discard(had, built);

        // The same row as before, over whatever it is built from now: what is shown is looked up
        // again rather than put back, and that is the point — the tabs beside it are about the
        // drawing as it now is.
        if (was is { } drawing
            && _shown.FirstOrDefault(shown => ReferenceEquals(shown.Built.Drawing, drawing)) is { Built.Svg: { } } again)
        {
            Inspect(again, again.Placement, again.Built.Svg!);

            // And the element inside it, which rings the drawing and fills the Element tab through
            // the same handler a click on a row does. A key the drawing no longer has selects
            // nothing, which is the honest answer: the element it named has been edited away.
            _tree.TrySelect(picked);
        }
    }

    /// <summary>
    /// Where everything one group holds goes, in the space the tab arranges in.
    /// </summary>
    /// <remarks>
    /// One rule at every depth: the children that name a place go where they say, relative to the
    /// board they sit on, and the rest are laid out together by the spread and put beside them. A
    /// board where nothing names a place is the spread and nothing else, which is what a group that
    /// has never been arranged still looks like.
    ///
    /// Recursive because a place is relative: a group's children are written against it, so the
    /// offset is carried down and a frame is drawn round what comes back.
    /// </remarks>
    private void Lay(ProjectGroup group, SKPoint at, float label, IReadOnlyDictionary<ProjectDrawing, Drawn> made)
    {
        // Where this board's own items begin, so the ones with no place go beside what this board
        // holds rather than beside everything the tab has placed so far.
        var from = _shown.Count;
        var framedFrom = _framed.Count;

        var loose = new List<ProjectDrawing>();

        foreach (var child in group.Children)
        {
            if (!child.HasPosition)
            {
                // A group with no place of its own is not a unit on this board: what it holds joins
                // the queue, exactly as it did before any of this.
                loose.AddRange(child is ProjectGroup unplaced ? unplaced.Drawings : new[] { (ProjectDrawing)child });

                continue;
            }

            var to = new SKPoint(at.X + child.X!.Value, at.Y + child.Y!.Value);

            switch (child)
            {
                case ProjectDrawing drawing when made.TryGetValue(drawing, out var built):
                    Put(new SvgViewerPlacement(built.Svg!, to, Caption(drawing), label), built);
                    break;

                case ProjectGroup inner:
                    var shown = _shown.Count;
                    var framed = _framed.Count;

                    // Held, so the frames come out outermost first and a click on two of them at
                    // once is the inner one's.
                    _framed.Add((new SvgViewerFrame(default), inner));

                    Lay(inner, to, label, made);

                    _framed[framed] = (Around(shown, framed + 1, to, inner.Name, label), inner);
                    break;
            }
        }

        if (loose.Count == 0)
        {
            return;
        }

        var items = loose.Where(made.ContainsKey).ToList();
        var block = Beside(at, from, framedFrom, label);

        foreach (var (placement, drawing) in SvgViewerSpread
                     .Of(items.Select(one => new SvgViewerSpread.Item(made[one].Svg!, made[one].Size, Caption(one))).ToList())
                     .Zip(items))
        {
            // The copy, and only the copy, goes into both lists: a click pairs a placement back to
            // its drawing by reference, and `with` makes a new record.
            Put(placement with { At = new SKPoint(placement.At.X + block.X, placement.At.Y + block.Y) }, made[drawing]);
        }
    }

    private void Put(SvgViewerPlacement placement, Drawn built) => _shown.Add((placement, built));

    /// <summary>
    /// What a press on the board takes hold of: a drawing, or the frame round a group.
    /// </summary>
    /// <remarks>
    /// A drawing before the frame it sits in, so pressing an icon moves the icon and pressing a
    /// frame's margin or its name moves the group — and the innermost frame first, for the same
    /// reason. Back to front among the drawings, which is the order a click resolves in.
    /// </remarks>
    private (object Item, SKRect Bounds)? Held(SKPoint at)
    {
        for (var index = _shown.Count - 1; index >= 0; index--)
        {
            var area = Area(_shown[index].Placement);

            if (area.Width > 0f && area.Contains(at.X, at.Y))
            {
                return (_shown[index].Built.Drawing, area);
            }
        }

        foreach (var (frame, group) in _framed.OrderBy(framed => framed.Frame.Bounds.Width * framed.Frame.Bounds.Height))
        {
            if (frame.Bounds.Contains(at.X, at.Y))
            {
                return (group, frame.Bounds);
            }
        }

        return null;
    }

    /// <summary>
    /// Writes where something was let go, and the places of everything else while it is at it.
    /// </summary>
    /// <remarks>
    /// The first move on a board that has never been arranged settles the whole tab: every row is
    /// written at the place the spread had just given it, so the arrangement somebody was looking
    /// at is the one that is saved and nothing jumps. What appears is a frame round each group,
    /// which is the point of moving anything at all.
    ///
    /// Written through and saved, as every other arrangement edit is — a row added, removed or
    /// dragged in the tree writes the file as it is made. It cannot be held pending: what is
    /// pending is keyed by setting name on this tab's own node, and settling writes places on every
    /// node under it.
    /// </remarks>
    private void Placed(SvgViewerMove move)
    {
        if (move.Item is not ProjectNode node)
        {
            return;
        }

        Settle();

        node.X = ProjectNode.Rounded((node.X ?? 0f) + move.By.X);
        node.Y = ProjectNode.Rounded((node.Y ?? 0f) + move.By.Y);

        Workspace.Save();
    }

    /// <summary>Gives every row of the tab the place it is already being drawn at.</summary>
    private void Settle()
    {
        var at = new Dictionary<ProjectNode, SKPoint>();

        foreach (var (placement, built) in _shown)
        {
            at[built.Drawing] = placement.At;
        }

        // A group is where the nearest corner of what it holds is — not its frame, which is
        // inflated and has a name above it, and not the ink, which would tie the file to how the
        // canvas happens to paint. Bottom up, so a group's own corner is known before its parent's.
        Corner((ProjectGroup)Node);

        Rebase((ProjectGroup)Node, SKPoint.Empty);

        SKPoint? Corner(ProjectNode node)
        {
            if (node is not ProjectGroup group)
            {
                return at.TryGetValue(node, out var placed) ? placed : null;
            }

            SKPoint? corner = null;

            foreach (var child in group.Children)
            {
                if (Corner(child) is not { } held)
                {
                    continue;
                }

                corner = corner is { } known
                    ? new SKPoint(MathF.Min(known.X, held.X), MathF.Min(known.Y, held.Y))
                    : held;
            }

            if (corner is { } found)
            {
                at[group] = found;
            }

            return corner;
        }

        void Rebase(ProjectGroup group, SKPoint origin)
        {
            foreach (var child in group.Children)
            {
                if (!at.TryGetValue(child, out var held))
                {
                    continue;
                }

                child.X = ProjectNode.Rounded(held.X - origin.X);
                child.Y = ProjectNode.Rounded(held.Y - origin.Y);

                if (child is ProjectGroup inner)
                {
                    Rebase(inner, held);
                }
            }
        }
    }

    /// <summary>Where the ones with no place of their own go: right of everything on this board that has one.</summary>
    private SKPoint Beside(SKPoint at, int from, int framedFrom, float label)
    {
        var placed = _shown.Skip(from).Select(shown => Area(shown.Placement))
            .Concat(_framed.Skip(framedFrom).Select(framed => framed.Frame.Bounds))
            .Where(area => area.Width > 0f)
            .ToList();

        // The room the spread leaves between two of its own, so a queue beside an arrangement is
        // as far off it as the queue's own columns are from each other.
        return placed.Count == 0
            ? at
            : new SKPoint(placed.Max(area => area.Right) + label * 2f, at.Y);
    }

    /// <summary>The frame round what one group holds: everything placed since the mark.</summary>
    private SvgViewerFrame Around(int shown, int framed, SKPoint at, string name, float label)
    {
        var held = _shown.Skip(shown).Select(one => Area(one.Placement))
            .Concat(_framed.Skip(framed).Select(one => one.Frame.Bounds))
            .Where(area => area.Width > 0f)
            .ToList();

        // A group holding nothing is still something to see and to take hold of, at the place it
        // says it is.
        var bounds = held.Count == 0
            ? new SKRect(at.X, at.Y, at.X + label * 4f, at.Y + label * 4f)
            : held.Aggregate(SKRect.Union);

        return new SvgViewerFrame(SKRect.Inflate(bounds, label, label), name, label);
    }

    private static SKRect Area(SvgViewerPlacement placement)
    {
        if (placement.Svg.Picture is not { CullRect: { Width: > 0f, Height: > 0f } cull })
        {
            return default;
        }

        cull.Offset(placement.At);

        return cull;
    }

    /// <summary>Rings whatever was clicked, on the drawing it was clicked in.</summary>
    /// <remarks>
    /// Two questions, and the canvas answers only the first: which of the drawings the point fell
    /// on, and then — of that drawing alone — which element is under it. Hit testing the wrong
    /// picture would answer confidently about a shape somebody was not pointing at.
    /// </remarks>
    private void Pick(Point at)
    {
        if (!_canvas.TryGetPlacementAt(at, out var placement, out var point) || placement is null)
        {
            return;
        }

        var index = _shown.FindIndex(shown => ReferenceEquals(shown.Placement, placement));

        if (index < 0 || _shown[index].Built.Svg is not { } svg)
        {
            return;
        }

        if (svg.HitTestTopmostElement(new ShimSkiaSharp.SKPoint(point.X, point.Y)) is not { } element)
        {
            // A click on the drawing but not on any of its ink. Leaving the ring where it is beats
            // clearing it: the pane is read alongside the picture, and a click that missed by two
            // pixels should not throw away what was being looked at.
            return;
        }

        Inspect(_shown[index], placement, svg);

        _picked = SvgElementAddress.Create(element).Key;

        _tree.TrySelect(_picked);

        // Rung here rather than left to the row being selected. A group usually builds one file
        // several ways, so its drawings give their elements the same addresses; picking a shape in
        // one and the same shape in another asks the tree for a row it already has selected, it
        // raises nothing, and the ring stays on the drawing picked first.
        Ring(placement, svg, element);

        // And shown here for the same reason: the drawing changed even where the address did not.
        ShowElement(SvgElementAddress.Create(element).Key);
    }

    /// <summary>Puts <paramref name="svg"/> in the tree, if it is not the one already there.</summary>
    /// <remarks>
    /// One drawing at a time. A group builds several and the tree keys its rows by a path that is
    /// unique only inside one document, so showing them all at once would need a different tree; the
    /// label above it is what says which of them this is.
    /// </remarks>
    private void Inspect((SvgViewerPlacement Placement, Drawn Built) shown, SvgViewerPlacement placement, SKSvg svg)
    {
        _showing.Text = ProjectWorkspace.Label(shown.Built.Drawing);

        if (_inspecting is { } inspecting && ReferenceEquals(inspecting.Placement, placement))
        {
            return;
        }

        _inspecting = (placement, shown.Built);

        _tree.Show(svg.SourceDocument);

        // The tabs are about whatever is selected, so they follow it.
        ShowParameters();
    }

    /// <summary>Puts the ring round <paramref name="element"/>, where its drawing sits on the canvas.</summary>
    /// <remarks>
    /// The tracer answers in the drawing's own coordinates and the canvas rings in the space the
    /// drawings are arranged in, so the path is moved by the placement's offset on the way across.
    /// </remarks>
    private void Ring(SvgViewerPlacement placement, SKSvg svg, SvgElement element)
    {
        var outline = SvgViewerOutline.Of(svg, element);

        outline?.Transform(SKMatrix.CreateTranslation(placement.At.X, placement.At.Y));

        _canvas.Highlight = outline;
    }

    /// <summary>One drawing built the way the project builds it, or why it could not be.</summary>
    /// <summary>
    /// Builds a drawing, or hands back the build it already has where nothing it reads has changed.
    /// </summary>
    /// <remarks>
    /// The two things a build reads are the text and the size it is asked for, so agreeing about
    /// both is agreeing about the picture. Most of what refreshes this tab changes neither: a drop
    /// writes x and y, a tree move writes an order, a root setting writes what the code generator
    /// does -- and re-parsing forty drawings to answer any of them cost the zoom, the ring and a
    /// tenth of a second each time.
    ///
    /// Comparing what was read beats classifying what happened: there are eleven ways into
    /// <see cref="ProjectWorkspace.Save"/> and the event they raise says nothing about which fired.
    /// </remarks>
    private Drawn Draw(ProjectDrawing drawing, Drawn? was)
    {
        // Through the host where a tab is holding this drawing, so the canvas shows what that tab
        // shows rather than what the project was last saved with.
        var text = TargetOf?.Invoke(drawing)?.Text ?? drawing.Text;
        var sizing = Sizing(drawing);

        if (was is { } already
            && string.Equals(already.Text, text, StringComparison.Ordinal)
            && already.Sizing == sizing)
        {
            return already;
        }

        try
        {
            var document = SvgViewerDocument.LoadFromSvg(text, null, ProjectWorkspace.SizeOf(drawing));

            // What the panel would show for this drawing on opening it, which is what a viewer
            // binds and so what the drawing's own tab renders.
            try
            {
                document.Svg.SetExpressionValues(Seeded(document));
            }
            catch (ExprException)
            {
            }

            return new Drawn(drawing, text, sizing, document, null);
        }
        catch (Exception failure)
        {
            // Anything: this is user data reaching a parser, and it arrives as an XmlException, a
            // FormatException, one of the IO exceptions or the loader's own refusal. A narrower set
            // would eventually let one through, and one bad drawing would cost the whole tab.
            return new Drawn(drawing, text, sizing, null, failure.Message);
        }
    }

    /// <summary>Everything the size of a build depends on, as the four settings that decide it.</summary>
    /// <remarks>
    /// The settings rather than the <see cref="SvgSizeRequest"/> they are folded into: that is a
    /// plain struct with no equality of its own, and these are what it is made of anyway.
    /// </remarks>
    private static (float?, float?, float?, string?) Sizing(ProjectNode node)
        => (node.EffectiveWidth, node.EffectiveHeight, node.EffectiveScale, node.EffectivePadding);

    /// <summary>Lets go of the drawings, and of the documents that own them.</summary>
    /// <remarks>
    /// The canvas first: a picture belongs to its document, so one still placed after the document
    /// is disposed is a surface drawing freed memory.
    /// </remarks>
    private void Release()
    {
        Forget();

        _canvas.Show(Array.Empty<SvgViewerPlacement>());

        Discard(_built, Array.Empty<Drawn>());

        _built = Array.Empty<Drawn>();
        _stale = true;
    }

    /// <summary>Lets go of everything a build is about to say again.</summary>
    /// <remarks>
    /// Not the canvas: blanking it is what told it a new board had arrived, and a new board is
    /// fitted. What is placed is replaced by the build a moment later, so there is nothing for it
    /// to be holding in between.
    /// </remarks>
    private void Forget()
    {
        _canvas.Highlight = null;

        _shown.Clear();
        _framed.Clear();

        // The tree holds elements of a document that may be about to be disposed, and the
        // parameters belong to the drawing it was showing.
        _inspecting = null;
        _picked = null;
        _tree.Show(null);
        ShowParameters();
        ShowElement(null);
        _showing.Text = "Click a drawing to see what it is made of.";

        Says(null);
    }

    /// <summary>
    /// Disposes every document of <paramref name="previous"/> that <paramref name="kept"/> did not
    /// take over.
    /// </summary>
    /// <remarks>Always after the canvas has been given what replaces them. See the call site.</remarks>
    private static void Discard(IReadOnlyList<Drawn> previous, IReadOnlyList<Drawn> kept)
    {
        var keeping = kept.Where(one => one.Document is { }).Select(one => one.Document!).ToHashSet();

        foreach (var built in previous)
        {
            if (built.Document is { } document && !keeping.Contains(document))
            {
                document.Dispose();
            }
        }
    }

    /// <summary>Puts a line above the canvas, or takes it away.</summary>
    private void Says(string? said)
    {
        _notice.Text = said ?? string.Empty;
        _notice.IsVisible = said is { Length: > 0 };
    }

    /// <summary>What could not be read, in one line however many of them there were.</summary>
    private static string? Trouble(IReadOnlyList<Drawn> built)
    {
        var faults = built.Where(one => one.Fault is { }).ToList();

        if (faults.Count == 0)
        {
            return null;
        }

        var first = $"{faults[0].Drawing.Name} could not be read: {faults[0].Fault}";

        return faults.Count == 1 ? first : $"{first} And {faults.Count - 1} more could not be read.";
    }

    /// <summary>The canvas's own controls, which are the viewer's in the order the viewer has them.</summary>
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

        _canvas.ViewChanged += (_, _) =>
            _zoom.Text = (_canvas.Scale * 100d).ToString("0", CultureInfo.CurrentCulture) + "%";

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

    /// <summary>What a drawing is called and what it is built at, over two lines.</summary>
    /// <remarks>
    /// Two, because one is about twice as wide as the drawings it sits under and the columns are
    /// sized to hold it.
    /// </remarks>
    private static string Caption(ProjectDrawing drawing)
    {
        var name = drawing.EffectiveNamespace is { } space && drawing.EffectiveClass is { } className
            ? $"{space}.{className}"
            : drawing.EffectiveClass ?? drawing.EffectiveNamespace ?? "(unnamed)";

        return $"{drawing.Name}\n{name}   {Size(drawing)}";
    }

    private static readonly Uri Home = new("avares://Svg.Studio/");

    /// <remarks>
    /// The document and not just its picture: the declarations shown on the panel and the text the
    /// colours are surveyed from are both read off it.
    /// </remarks>
    private sealed record Drawn(
        ProjectDrawing Drawing,
        string Text,
        (float?, float?, float?, string?) Sizing,
        SvgViewerDocument? Document,
        string? Fault)
    {
        public SKSvg? Svg => Document?.Svg;

        /// <summary>
        /// How big the picture is now.
        /// </summary>
        /// <remarks>
        /// Read rather than held: binding a parameter rewrites the recorded picture in place, extent
        /// and all, so a size taken at build time describes a drawing that has since changed shape.
        /// It was right only because every lay used to follow a build.
        /// </remarks>
        public SKSize Size => Document?.Svg.Picture?.CullRect.Size ?? default;
    }

    /// <summary>What the sizing comes to, said the way the project says it.</summary>
    private static string Size(ProjectNode node)
    {
        var parts = new List<string>();

        if (node.EffectiveWidth is { } width)
        {
            parts.Add($"w{Number(width)}");
        }

        if (node.EffectiveHeight is { } height)
        {
            parts.Add($"h{Number(height)}");
        }

        if (node.EffectiveScale is { } scale)
        {
            parts.Add($"×{Number(scale)}");
        }

        if (node.EffectivePadding is { } padding)
        {
            parts.Add($"pad {padding}");
        }

        return parts.Count == 0 ? "as written" : string.Join(" ", parts);
    }

    private void ShowProperties(ProjectNode node)
    {
        _properties.Children.Clear();

        if (node is not ProjectRoot)
        {
            Add("name");
        }

        if (node is ProjectDrawing)
        {
            Add("output");
        }

        if (node is not ProjectRoot)
        {
            _properties.Children.Add(new Separator { Margin = new Thickness(0, 6) });
        }

        Add("namespace");
        Add("class");

        if (node is ProjectRoot)
        {
            Add("singleFile");
            Add("cache");
            Add("helperScope");
            Add("skiaSharp");
        }

        _properties.Children.Add(new Separator { Margin = new Thickness(0, 6) });

        Add("width");
        Add("height");
        Add("scale");
        Add("padding");

        // Where it sits on the board of the group holding it — which the project has none of, and
        // which a node's own tab therefore shows without showing any change.
        if (node is not ProjectRoot)
        {
            _properties.Children.Add(new Separator { Margin = new Thickness(0, 6) });

            Add("x");
            Add("y");
        }

        void Add(string name) => _properties.Children.Add(Row(node, name));
    }

    /// <summary>What the box shows: what was typed here if anything, and what the file says if not.</summary>
    public string? Shown(string name) => Shown(Node, name);

    private string? Shown(ProjectNode node, string name)
        => _pending.TryGetValue(name, out var pending) ? pending : Value(node, name);

    /// <summary>Writes one setting onto <paramref name="node"/>, or throws if the value is not one.</summary>
    private static void Write(ProjectNode node, string name, string? value)
    {
        switch (name)
        {
            case "name":
                node.Name = value!;
                break;
            case "output":
                ((ProjectDrawing)node).Output = value;
                break;
            case "namespace":
                node.Namespace = value;
                break;
            case "class":
                node.Class = value;
                break;
            case "padding":
                node.Padding = value;
                break;
            case "x":
                node.X = SvgcProject.ParseLength(value, "position");
                // Both or neither, as the format asks. Typing one of them places the node at the
                // board's origin on the other axis; clearing either takes the place away.
                node.Y = node.X is { } ? node.Y ?? 0f : null;
                break;
            case "y":
                node.Y = SvgcProject.ParseLength(value, "position");
                node.X = node.Y is { } ? node.X ?? 0f : null;
                break;
            case "width":
                node.Width = SvgcProject.ParseLength(value, "width");
                break;
            case "height":
                node.Height = SvgcProject.ParseLength(value, "height");
                break;
            case "scale":
                node.Scale = SvgcProject.ParseScale(value);
                break;
            case "singleFile":
                ((ProjectRoot)node).SingleFile = value;
                break;
            case "cache":
                ((ProjectRoot)node).Cache = SvgcProject.ParseCache(value);
                break;
            case "helperScope":
                ((ProjectRoot)node).HelperScope = SvgcProject.ParseHelperScope(value);
                break;
            case "skiaSharp":
                ((ProjectRoot)node).SkiaSharp = SvgcProject.ParseSkiaSharpTarget(value);
                break;
        }
    }

    private static Button Command(string content, string tip, Action run)
    {
        var button = new Button
        {
            Content = content,
            FontSize = 11,
            Padding = new Thickness(8, 2),
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center
        };

        ToolTip.SetTip(button, tip);

        button.Click += (_, _) => run();

        return button;
    }

    private static Control Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        foreach (var button in buttons)
        {
            panel.Children.Add(button);
        }

        Grid.SetColumn(panel, 1);

        return panel;
    }

    /// <summary>
    /// One setting: what this node says, with what it would inherit shown behind it.
    /// </summary>
    /// <remarks>
    /// An empty box means "inherited", so clearing one is how an override is taken back — which is
    /// why the watermark carries the inherited value rather than a hint.
    /// </remarks>
    private Control Row(ProjectNode node, string name)
    {
        var value = Shown(node, name);

        var box = new TextBox
        {
            Text = value,
            PlaceholderText = Inherited(node, name),
            FontSize = 12,
            Tag = name
        };

        box.TextChanged += (_, _) => Track(name, box.Text);

        box.LostFocus += (_, _) =>
        {
            var text = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text!.Trim();

            if (text == value)
            {
                return;
            }

            if (!Edit(name, text))
            {
                // Put back, and said, rather than left looking accepted.
                box.Text = value;
                return;
            }

            value = text;
        };

        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = name, Opacity = 0.65, FontSize = 11 },
                box
            }
        };
    }

    /// <summary>
    /// Records a setting as typed here, without writing it to the project.
    /// </summary>
    /// <remarks>
    /// The edit is kept until <see cref="Save"/>, so a value typed back to what the file already
    /// says leaves nothing to save rather than a tab marked for no change.
    /// </remarks>
    /// <returns>Whether the value was one the setting can hold.</returns>
    public bool Edit(string name, string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        // Checked as it is typed rather than at save, so a value the project cannot hold is
        // rejected while the box that holds it is still on screen.
        try
        {
            Validate(name, text);
        }
        catch (SvgcProjectException failure)
        {
            Fault = failure.Message;
            return false;
        }

        Fault = null;

        Track(name, text);

        return true;
    }

    /// <summary>
    /// Records what a box holds as it is typed, without asking whether the project can hold it.
    /// </summary>
    /// <remarks>
    /// Unchecked on purpose: a number is typed through "1." and "-", and snatching those back
    /// between keystrokes is worse than letting the box keep them until the caret leaves or a save
    /// asks. What it buys is the mark appearing at the first keystroke rather than when the caret
    /// leaves, which is the only thing saying there is anything to save.
    /// </remarks>
    private void Track(string name, string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var was = IsModified;

        if (text == Value(Node, name))
        {
            _pending.Remove(name);
        }
        else
        {
            _pending[name] = text;
        }

        Announce(was);
    }

    /// <summary>Throws unless <paramref name="value"/> is one this setting can hold.</summary>
    /// <remarks>
    /// The same parsers the setters use, called without them: a setting checked here is not written
    /// anywhere yet, and the setters write to the document an unsaved edit must not touch.
    /// </remarks>
    private static void Validate(string name, string? value)
    {
        switch (name)
        {
            // The one setting with no empty form: a row that says nothing is a row nobody can
            // tell from the one beside it.
            case "name" when string.IsNullOrWhiteSpace(value):
                throw new SvgcProjectException("A drawing or group needs a name.");
            case "width":
                SvgcProject.ParseLength(value, "width");
                break;
            case "height":
                SvgcProject.ParseLength(value, "height");
                break;
            case "scale":
                SvgcProject.ParseScale(value);
                break;
            case "x":
            case "y":
                SvgcProject.ParseLength(value, "position");
                break;
            case "cache":
                SvgcProject.ParseCache(value);
                break;
            case "helperScope":
                SvgcProject.ParseHelperScope(value);
                break;
            case "skiaSharp":
                SvgcProject.ParseSkiaSharpTarget(value);
                break;
        }
    }

    /// <summary>What the node would take for <paramref name="name"/> if it said nothing itself.</summary>
    private static string? Inherited(ProjectNode node, string name)
    {
        if (node.Parent is not { } parent)
        {
            return null;
        }

        var owner = parent.OwnerOf(name);

        return owner is null ? null : $"{Value(owner, name)} — from {ProjectWorkspace.Label(owner)}";
    }

    private static string? Value(ProjectNode node, string name) => name switch
    {
        "name" => node.Name,
        "output" => (node as ProjectDrawing)?.Output,
        "namespace" => node.Namespace,
        "class" => node.Class,
        "padding" => node.Padding,
        "x" => node.X is { } x ? Number(x) : null,
        "y" => node.Y is { } y ? Number(y) : null,
        "width" => node.Width is { } width ? Number(width) : null,
        "height" => node.Height is { } height ? Number(height) : null,
        "scale" => node.Scale is { } scale ? Number(scale) : null,
        // The project's own five. Left out, they showed empty however the file was written, and an
        // edit to one could never be recognised as typed back to what the file says — so it stayed
        // pending for ever.
        "singleFile" => (node as ProjectRoot)?.SingleFile,
        "cache" => Text((node as ProjectRoot)?.Cache),
        "helperScope" => Text((node as ProjectRoot)?.HelperScope),
        "skiaSharp" => (node as ProjectRoot)?.SkiaSharp is { } target
            ? (target == SkiaSharpTarget.V3 ? "3" : "4")
            : null,
        _ => null
    };

    /// <summary>Why the last edit was refused, or null.</summary>
    public string? Fault { get; private set; }

    private static string Number(float value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Text<T>(T? value) where T : struct
        => value is { } set ? set.ToString()!.ToLowerInvariant() : null;
}
