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
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Projects;
using Svg.Editor.Skia;
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

    /// <summary>What could not be read in the blocks the rows came from.</summary>
    private readonly TextBlock _parameterNote = new()
    {
        Margin = new Thickness(10, 0, 10, 10),
        Opacity = 0.6,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false
    };

    /// <summary>What declares each name on the panel, by name.</summary>
    /// <remarks>
    /// The rows come from the whole chain and from the drawing that is picked, so a row carries no
    /// idea which block it was read from and this is what says. By name because that is what a
    /// command is about and what the extension makes unique: a name declared twice down the chain
    /// is refused before it can be shown.
    /// </remarks>
    private readonly Dictionary<string, ProjectNode> _owners = new(StringComparer.Ordinal);

    /// <summary>One target per group the rows came from.</summary>
    private readonly Dictionary<ProjectGroup, GroupTarget> _targets = new();

    /// <summary>Where a declaration being added right now was sent, while it is being written.</summary>
    /// <remarks>
    /// Null at every other moment. The name is not declared anywhere yet, so there is nothing to
    /// look its holder up by until the write has landed and the rows have been read again.
    /// </remarks>
    private ProjectNode? _adding;

    /// <summary>The rows the panel last held, kept for <see cref="Carried"/> past it being emptied.</summary>
    /// <remarks>
    /// A rebuild lets go of the rows before it takes them again, and letting go empties the panel.
    /// Reading the values off the panel therefore read them after they had been thrown away, so any
    /// edit at all — a drawing dragged across the board — put every parameter back to what its
    /// group declares.
    /// </remarks>
    private IReadOnlyList<SvgViewerParameter>? _carried;

    private readonly SvgViewerDeclarationCommands? _commands;

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

    /// <summary>Dragging a drawing's own edges to change the size it is.</summary>
    private readonly SvgViewerPage _paging = new();

    /// <summary>The board's own snap toggle, which follows the setting rather than holding it.</summary>
    private ToggleButton? _snapping;

    /// <summary>The board's own captions toggle, which follows the setting the same way.</summary>
    private ToggleButton? _captions;

    /// <summary>Whether the board in front of us was laid out with names on its drawings.</summary>
    private bool _named = StudioSettings.DrawingCaptions;

    /// <summary>Whether the page of <see cref="_inspecting"/> is what is selected.</summary>
    /// <remarks>
    /// The third thing a board's one selection can be, beside a group and an element. It rides on
    /// <see cref="_inspecting"/> rather than naming a drawing of its own, because the drawing being
    /// looked at is what it is the page of — and the panes are already about that drawing.
    /// </remarks>
    private bool _page;

    /// <summary>The group that is selected, where one is rather than a drawing.</summary>
    /// <remarks>
    /// The two are the one selection and never both: a board has one thing being looked at, and
    /// <see cref="Subject"/> is what the panes are about.
    /// </remarks>
    private ProjectGroup? _selected;

    /// <summary>What is selected inside the drawing being looked at, by address.</summary>
    /// <remarks>
    /// Addresses and not elements: a drawing read again is a different graph, so the key is the only
    /// thing the two have in common. Which drawing they are in is <see cref="_inspecting"/> and is
    /// not held twice — a selection is of one drawing, which is why the tree showing one drawing's
    /// rows can show the whole of it.
    /// </remarks>
    private readonly List<string> _picked = new();

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

    /// <summary>Edits typed here and not yet put into the project, by setting name.</summary>
    /// <remarks>
    /// Held rather than applied, so a tab saves what was typed in it and nothing else. The cost is
    /// that the tree, the values other tabs inherit and the drawings already open all go on showing
    /// what the document says until this is committed — the document is the one thing they all read.
    /// </remarks>
    private readonly Dictionary<string, string?> _pending = new(StringComparer.Ordinal);

    /// <summary>Moving, turning and scaling the picked element by dragging it on the canvas.</summary>
    private readonly SvgViewerGizmos _gizmo = new();

    /// <summary>What a rectangle being swept has caught, and the ring showing it.</summary>
    private readonly SvgViewerSweep _sweep = new();

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
        _canvas.Moved += (_, move) => Placed(move);

        _canvas.Grip = Held;

        // Handles, then the line a drawing is carried by, then the gesture's own body — narrowest
        // first, the order the canvas resolves its own claims in. A shape that fills its page puts
        // all three over the same pixels, and without the middle rung the board could not be
        // rearranged at all while anything was selected.
        _canvas.IsEditTarget = at =>
            Arranged(at) is { } arranged
            && (_gizmo.Hits(arranged, (float)_canvas.Scale, handlesOnly: true)
                || _paging.Hits(new SKPoint(arranged.X, arranged.Y), (float)_canvas.Scale)
                || (!Grabbed(at) && _gizmo.Hits(arranged, (float)_canvas.Scale)));

        _canvas.Marqueed += (_, swept) => SelectEnclosed(swept);

        _canvas.Marqueeing += (_, swept) => ShowEnclosed(swept);

        // A left drag on a drawing's edges carries the drawing, because the grip answers first;
        // anywhere else it sweeps up a selection. What it never does is move the view, which has
        // the middle button, the wheel and two fingers of its own.
        _canvas.IsMarqueeEnabled = true;

        _canvas.EditBegun += (_, at) => BeginEdit(at);
        _canvas.EditMoved += (_, at) => DragEdit(at);
        _canvas.EditEnded += (_, _) => EndEdit();
        _canvas.EditCancelled += (_, _) => CancelEdit();

        _canvas.ViewChanged += (_, _) => ShowGizmo();

        // Built once, because what it writes into is decided per declaration rather than per panel:
        // the rows come from the group and every group above it, and Splice sends each edit to the
        // one that holds it.
        if (node is ProjectGroup)
        {
            _commands = new SvgViewerDeclarationCommands(
                Splice,
                () => _parameters.Parameters ?? Array.Empty<SvgViewerParameter>(),
                () => ParameterDialogService)
            {
                Holder = name => Holder(name),
                UsedElsewhere = name => TargetFor(Holder(name)).UsesElsewhere(name)
            };

            // What declares each row. Every row, including this group's own: the panel groups the
            // rows under it, and a run with no heading among runs that have one reads as belonging
            // to the one above it.
            _parameters.DeclaredBy = name =>
                _owners.TryGetValue(name, out var holder) ? ProjectWorkspace.Label(holder) : null;
        }

        _parameters.ValueChanged += (_, _) => Bind();
        _parameters.AddRequested += async (_, _) => await AddParameterAsync().ConfigureAwait(true);
        _parameters.CommitRequested += (_, _) => CommitDefaults();
        _parameters.EditRequested += async (_, row) =>
        {
            if (_commands is { } commands)
            {
                await commands.EditAsync(TopLevel.GetTopLevel(this), row).ConfigureAwait(true);
            }
        };
        _parameters.RemoveRequested += (_, row) => _commands?.Remove(row);
        _parameters.LetCommitted += async (_, let) => await CommitLetAsync(let).ConfigureAwait(true);
        _parameters.LetRemoveRequested += (_, let) => _commands?.RemoveLet(let);
        _parameters.LetMoveRequested = (let, to) => _commands?.MoveLet(let, to) == true;
        _parameters.ParameterMoveRequested = (row, to) => _commands?.MoveParameter(row, to) == true;

        // The other direction: a row picked in the tree rings the drawing it belongs to, which is
        // the one the tree is showing.
        _tree.Selected += (_, node) =>
        {
            _picked.Clear();
            _picked.AddRange(_tree.SelectedAddresses);

            Ring();

            TrackGizmo();
            ShowElement(node?.AddressKey);
        };

        Reread();

        // A group edited in another tab changes what this one inherits, so every tab follows the
        // one document rather than the copy it was opened with. Anything typed here and not
        // committed survives it.
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
    /// and wearing the viewer's tab style so the two strips cannot drift apart: the same three tabs
    /// in the same order, and what a person learns on one of them holds on the other.
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

        var showing = new TabItem { Header = "Variables", Content = parameters };

        tabs.Items.Add(showing);
        tabs.Items.Add(new TabItem { Header = "Element", Content = _elementHost });

        // Opened on, rather than the settings that come first in the strip: what a drawing declares
        // is what moves the board, and the settings are read when a row is set up and then left.
        // The same tab a drawing's own strip opens on, which is the point of the two being alike.
        tabs.SelectedItem = showing;

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
    /// knows about none of them. Null is answered with the project itself, which the panel holds the
    /// document for.
    /// </remarks>
    public Func<ProjectDrawing, ISvgViewerDeclarationTarget?>? TargetOf { get; set; }

    /// <summary>How the parameters tab asks what to declare. Replaceable, and faked in tests.</summary>
    public ISvgViewerParameterDialogService ParameterDialogService { get; set; } =
        new SvgViewerParameterDialogService();

    /// <summary>
    /// How the tab asks where a new declaration should go, or null for the menu it shows itself.
    /// </summary>
    /// <remarks>
    /// Answering null is cancelling, and nothing is written. Replaceable for the reason
    /// <see cref="ParameterDialogService"/> is: a menu is not something a test can click.
    /// </remarks>
    public Func<IReadOnlyList<ProjectNode>, Task<ProjectNode?>>? ChooseOwner { get; set; }

    /// <summary>Which drawing each placement on the board came from, in the board's order.</summary>
    /// <remarks>
    /// The canvas is handed placements and hands back nothing about where they came from, so this
    /// is the pairing — index for index with what it was shown. The caption used to be the answer, a
    /// caller reading the row's name off the writing under it. That is worse than it looks even now
    /// the writing is back: it can be switched off, and a picture was never a good place to keep an
    /// identity.
    /// </remarks>
    public IReadOnlyList<ProjectDrawing> Board => _shown.Select(shown => shown.Built.Drawing).ToList();

    /// <summary>Puts what was typed here into the project, which somebody else then saves.</summary>
    public void Commit()
    {
        // What is in the box being typed in, before deciding there is nothing to commit. Recording
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
        var writing = _pending.ToList();

        // Every setting typed since the last commit as one thing to take back, which is what was
        // handed over: the boxes are filled in together and committed together.
        Workspace.Do(
            $"change {ProjectWorkspace.Label(Node)}",
            () => ProjectSnapshot.Attributes(Node),
            () =>
            {
                foreach (var edit in writing)
                {
                    Write(Node, edit.Key, edit.Value);
                }
            });

        // Cleared before the project hears about it, which is honest now that this does not write:
        // the tab really is holding nothing, and what it handed over is the project's to report.
        _pending.Clear();

        // What is true, not the false this used to announce. Committing rebuilds the rows, which
        // detaches whichever box had focus, and a box losing focus records what is in it — so this
        // can end with something pending again, and saying "saved" there left the tab with no mark
        // and an unsaved warning waiting at the close button.
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
                // The rows are shown from in there, once the board has settled which drawing is
                // being looked at: the last section of the panel is that drawing's own.
                ShowDrawings();
            }
            else
            {
                _stale = true;

                ShowDeclarations();
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

    /// <summary>Somebody pressed one of this board's own toggles, and it wrote a setting.</summary>
    /// <remarks>
    /// The toggle writes the setting itself; this is for the window to tell every other tab, which
    /// is what makes a toolbar, the settings window and every other board one switch rather than
    /// several. One event for all of them because what a listener does about it is the same — ask
    /// the settings again — and which toggle it was is not something anybody has needed.
    /// </remarks>
    public event EventHandler? SettingChanged;

    /// <summary>Draws and drags at whatever the settings now say.</summary>
    /// <remarks>
    /// Asked again rather than told, for the reason the settings window answers nothing: the setting
    /// outlives every window that reads it, and a tab holding its own copy would be one more thing
    /// to keep in step.
    /// </remarks>
    public void Reread()
    {
        _canvas.CaptionSize = StudioSettings.CaptionSize;

        var grid = StudioSettings.SnapToGrid ? StudioSettings.Grid : SvgViewerGrid.None;

        // All three, because all three write a place: the canvas carries a drawing about, the gizmo
        // moves what is inside one, and the page is the drawing's own edges.
        _canvas.Grid = grid;
        _gizmo.Grid = grid;
        _paging.Grid = grid;

        if (_snapping is { })
        {
            _snapping.IsChecked = StudioSettings.SnapToGrid;
        }

        if (_captions is { })
        {
            _captions.IsChecked = StudioSettings.DrawingCaptions;
        }

        // The one that is not a render flag: a name is written into the placement when the board is
        // laid out, so this has to be laid out again. Only when it changed, or a theme picked in the
        // settings would re-lay every board open behind it.
        if (_named != StudioSettings.DrawingCaptions)
        {
            _named = StudioSettings.DrawingCaptions;

            // ShowDrawings rather than something narrower: it reuses the drawings it has built
            // instead of reading them again, keeps whatever is being inspected, and takes the
            // Rearrange branch, so the view neither jumps nor refits.
            if (_canvas.Placements.Count > 0)
            {
                ShowDrawings();
            }
        }
    }

    /// <summary>What a drawing is called on a board, or null where nothing is written under it.</summary>
    /// <remarks>
    /// The name and nothing else. It used to be two lines — the name, then the class and the size —
    /// because the columns were sized to hold whichever was wider. They are not any more: what is
    /// written is shrunk to the drawing, and a second line twice as wide as an icon would shrink to
    /// nothing anybody could read.
    /// </remarks>
    private static string? Caption(ProjectDrawing drawing)
        => StudioSettings.DrawingCaptions ? drawing.Name : null;

    /// <summary>
    /// Shows everything the drawings here are built with: the chain, then what the picked one
    /// declares for itself.
    /// </summary>
    /// <remarks>
    /// In the order a drawing is built — the project root's block, each group down to this one, and
    /// the drawing's own last. That is the order the generated arguments come out in, so a panel
    /// showing it any other way would be describing another document.
    ///
    /// Every row is editable where it is shown, and written back into whatever holds it: an
    /// inherited one changes the group and so every drawing under it, and one the drawing declares
    /// for itself changes that drawing alone.
    ///
    /// The selection contributes the last section and nothing else, so picking another drawing
    /// leaves the group's rows — and whatever somebody has dragged them to — exactly where they were.
    /// </remarks>
    private void ShowDeclarations()
    {
        if (Node is not ProjectGroup group)
        {
            return;
        }

        _owners.Clear();

        var subject = Subject();
        var reaches = Reaches();

        var parameters = new List<SvgExpressionParameter>();
        var lets = new List<SvgExpressionLet>();
        string? trouble = null;
        var hidden = 0;

        foreach (var holder in Holders())
        {
            var declared = SvgExpressionDeclarations.Parse(Declares(holder), out var diagnostics);

            // The first sentence and not all of them: this is one line under a panel, and what it is
            // for is saying that a block nobody can see is the reason a row is missing.
            trouble ??= diagnostics.Count > 0
                ? $"{ProjectWorkspace.Label(holder)} declares something that could not be read: {diagnostics[0].Message}"
                : null;

            // What the selection declares itself is shown whole, whether it uses it or not: it is
            // its own to add to and take away from. Everything above it — this tab's own group
            // included — is shown only where the selection reaches it. Clicking the board beside
            // the drawings is what puts the tab back to being about the group.
            var own = ReferenceEquals(holder, subject);

            foreach (var parameter in declared.Parameters)
            {
                if (!own && reaches is { } && !reaches.Contains(parameter.Name))
                {
                    hidden++;

                    continue;
                }

                _owners[parameter.Name] = holder;
                parameters.Add(parameter);
            }

            foreach (var let in declared.Lets)
            {
                if (!own && reaches is { } && !reaches.Contains(let.Name))
                {
                    hidden++;

                    continue;
                }

                _owners[let.Name] = holder;
                lets.Add(let);
            }
        }

        // Empty and not null. Null reads as "no document" and takes the Add button away with it,
        // which is the one button a group declaring nothing yet needs.
        _carried = Carried(SvgViewerParameterFactory.Create(parameters));

        _parameters.Parameters = _carried;
        _parameters.ShowLets(lets);

        Note(trouble ?? Left(hidden));
    }

    /// <summary>
    /// Which of the names declared above the selection it actually reaches, or null to show them all.
    /// </summary>
    /// <remarks>
    /// The same question the splice asks, answered off the drawings the board has already built:
    /// what a drawing was built with <em>is</em> what it reaches, narrowed on the way in, so there
    /// is nothing here to read or parse again.
    ///
    /// For a group that is the union over everything under <em>it</em> — one branch of the board,
    /// not the board. A tab lays out every drawing beneath it, so asking the whole board what it
    /// reaches answers for every branch at once, and the project's own tab then filtered nothing.
    ///
    /// Null where a drawing would not build or its block would not read: not knowing what is
    /// reached is not the same as reaching nothing, and hiding a row somebody is using is the worse
    /// of the two mistakes.
    /// </remarks>
    private IReadOnlyCollection<string>? Reaches()
    {
        // Nothing built at all is a board that has not been laid out yet, which is not the same as
        // a selection that reaches nothing.
        if (_built.Count == 0 && ((ProjectGroup)Node).Drawings.Any())
        {
            return null;
        }

        var subject = Subject();
        var names = new HashSet<string>(StringComparer.Ordinal);

        // A drawing descends from itself, so the picked-drawing case needs no rule of its own.
        foreach (var drawn in _built.Where(one => one.Drawing.DescendsFrom(subject)))
        {
            if (drawn.Document is not { DeclarationError: null } document)
            {
                return null;
            }

            foreach (var parameter in document.Declarations.Parameters)
            {
                names.Add(parameter.Name);
            }

            foreach (var let in document.Declarations.Lets)
            {
                names.Add(let.Name);
            }
        }

        return names;
    }

    /// <summary>What is declared above the selection and not shown, so it is not simply missing.</summary>
    /// <remarks>
    /// Without this a variable somebody wants to start using is invisible and unnamed, and the way
    /// to reach it — name it in the drawing, and it appears — is not one anybody would guess at.
    /// </remarks>
    private static string? Left(int hidden)
        => hidden switch
        {
            0 => null,
            1 => "One more is declared further up and not used here.",
            _ => $"{hidden} more are declared further up and not used here."
        };

    /// <summary>What declares for whatever is selected, outermost first and the selection last.</summary>
    /// <remarks>
    /// The selection's own ancestry rather than this tab's. A board shows the drawings of the groups
    /// nested under it as well as its own, so a drawing picked on the project's tab can sit two
    /// groups down, and walking up from the tab instead skipped every group in between — the panel
    /// then showed what the project declared and what the drawing declared, with the group that
    /// actually holds the family missing from the middle of it.
    /// </remarks>
    private IEnumerable<ProjectNode> Holders()
    {
        var subject = Subject();

        foreach (var holder in ProjectDeclarations.Chain(subject))
        {
            yield return holder;
        }

        yield return subject;
    }

    /// <summary>What the panel is about: whatever is selected, or this tab's own group.</summary>
    private ProjectNode Subject()
        => _inspecting is { } inspecting ? inspecting.Built.Drawing : _selected ?? Node;

    /// <summary>The text <paramref name="holder"/> keeps its declarations in.</summary>
    /// <remarks>
    /// A group keeps a block and nothing else. A drawing keeps them in the drawing, and in the
    /// buffer of a tab holding it where there is one — the same text the board is built from, so the
    /// rows and the picture cannot come to disagree about what the drawing declares.
    /// </remarks>
    private string Declares(ProjectNode holder)
        => holder switch
        {
            ProjectGroup group => group.CodeText,
            ProjectDrawing drawing => TargetOf?.Invoke(drawing)?.Text ?? drawing.Text,
            _ => string.Empty
        };

    /// <summary>Where a declaration is written: whatever holds it, or where a new one was sent.</summary>
    /// <remarks>
    /// A name nobody declares yet is a name being declared now, and <see cref="_adding"/> is where
    /// somebody has just said to put it. This group is the answer failing that — not one above it,
    /// and not the drawing that happens to be picked.
    /// </remarks>
    private ProjectNode Holder(string name)
        => _owners.TryGetValue(name, out var holder) ? holder : _adding ?? Node;

    /// <summary>Where <paramref name="holder"/> is written to.</summary>
    /// <remarks>
    /// A group's target is kept, because it is how an edit reaches the project and a fresh one per
    /// keystroke would be a fresh read of the block each time. A drawing's is not: it is the tab
    /// holding it where there is one, which is the host's to answer and can change under this.
    /// </remarks>
    private ISvgViewerDeclarationTarget TargetFor(ProjectNode holder)
    {
        if (holder is ProjectDrawing drawing)
        {
            return TargetOf?.Invoke(drawing) ?? new DrawingTarget(Workspace, drawing);
        }

        var group = (ProjectGroup)holder;

        if (!_targets.TryGetValue(group, out var target))
        {
            target = new GroupTarget(Workspace, group);
            _targets[group] = target;
        }

        return target;
    }

    /// <summary>
    /// Asks for a parameter and writes it into this group.
    /// </summary>
    /// <remarks>
    /// Public for the reason the viewer's is: it is the half of the button a test can drive, the
    /// other half being a modal.
    /// </remarks>
    /// <returns>Whether anything was written.</returns>
    public async Task<bool> AddParameterAsync()
    {
        if (_commands is not { } commands)
        {
            return false;
        }

        var candidates = Candidates();

        // Not asked where there is one answer, which is a group with nothing picked. A question
        // whose answer cannot differ is a click somebody has to make for no reason.
        var owner = candidates.Count == 1
            ? candidates[0]
            : await Where(candidates, _parameters.AddAnchor).ConfigureAwait(true);

        if (owner is null)
        {
            return false;
        }

        // Read by Holder for the duration of the write, since the name is not declared anywhere yet
        // and there is nothing else to look it up by.
        _adding = owner;

        try
        {
            return await commands.AddAsync(TopLevel.GetTopLevel(this)).ConfigureAwait(true);
        }
        finally
        {
            _adding = null;
        }
    }

    /// <summary>Where a new declaration could go.</summary>
    /// <remarks>
    /// Everything from this tab's own group down to whatever is selected. On a board that shows
    /// nested groups those are three or four things, and the one in the middle is usually the one
    /// meant: a drawing picked two groups down belongs to a family that neither the project nor the
    /// drawing itself speaks for.
    ///
    /// It stops at this tab's group rather than running on to the project root, so the rule is that
    /// a parameter can be declared anywhere between the tab somebody opened and the thing they
    /// clicked. The rows above that are still shown and still editable, which is how an existing one
    /// is changed; declaring a new one into a group whose tab this is not is a reach too far.
    ///
    /// The whole ancestry and not only the part that already declares, since a group holding no
    /// block yet is exactly the one somebody is about to give its first parameter.
    /// </remarks>
    private IReadOnlyList<ProjectNode> Candidates()
    {
        var candidates = new List<ProjectNode>();

        for (var node = Subject(); node is { }; node = node.Parent)
        {
            candidates.Add(node);

            if (ReferenceEquals(node, Node))
            {
                break;
            }
        }

        candidates.Reverse();

        return candidates;
    }

    /// <summary>Asks where a new declaration goes, however this panel has been told to ask.</summary>
    /// <remarks>
    /// One way in for both kinds, so a test standing in for the menu stands in for it wherever it
    /// would have been raised.
    /// </remarks>
    private Task<ProjectNode?> Where(IReadOnlyList<ProjectNode> candidates, Control anchor)
        => ChooseOwner is { } ask ? ask(candidates) : AskWhereAsync(candidates, anchor);

    /// <summary>Asks which of <paramref name="candidates"/> a new declaration should go to.</summary>
    /// <remarks>
    /// A menu on <paramref name="anchor"/>, because that is the button being answered for. Each item
    /// says what the choice does rather than only where it writes — declaring on the group is a
    /// change to every drawing under it, which is the whole difference between the two and not
    /// obvious from a name.
    ///
    /// The dismissal is posted rather than answered on the spot: closing is how a menu item's click
    /// arrives, so answering null there would race the click that chose something.
    /// </remarks>
    private Task<ProjectNode?> AskWhereAsync(IReadOnlyList<ProjectNode> candidates, Control anchor)
    {
        var answer = new TaskCompletionSource<ProjectNode?>();
        var menu = new MenuFlyout();

        foreach (var candidate in candidates)
        {
            var item = new MenuItem { Header = Declaring(candidate) };

            item.Click += (_, _) => answer.TrySetResult(candidate);

            menu.Items.Add(item);
        }

        menu.Closed += (_, _) => Dispatcher.UIThread.Post(
            () => answer.TrySetResult(null),
            DispatcherPriority.Background);

        menu.ShowAt(anchor);

        return answer.Task;
    }

    /// <summary>What declaring on <paramref name="candidate"/> would mean, as a menu item reads it.</summary>
    private static string Declaring(ProjectNode candidate)
        => candidate is ProjectDrawing
            ? $"{ProjectWorkspace.Label(candidate)} — this drawing alone"
            : $"{ProjectWorkspace.Label(candidate)} — every drawing in it";

    /// <summary>
    /// Writes what a let row says, asking where a new one goes.
    /// </summary>
    /// <remarks>
    /// The same question adding a parameter asks, for the same reason: a let is declared on a group
    /// or on a drawing, and which of the two is not something to guess at from the row it was typed
    /// into. A let that already exists has an answer already — it is written where it is written,
    /// and this is a change to it rather than a new declaration.
    /// </remarks>
    /// <returns>Whether anything was written.</returns>
    public async Task<bool> CommitLetAsync(SvgViewerLet let)
    {
        if (let is null)
        {
            throw new ArgumentNullException(nameof(let));
        }

        if (_commands is not { } commands)
        {
            return false;
        }

        if (let.Declaration is { })
        {
            return commands.CommitLet(let);
        }

        var candidates = Candidates();

        var owner = candidates.Count == 1
            ? candidates[0]
            : await Where(candidates, _parameters.AddAnchor).ConfigureAwait(true);

        if (owner is null)
        {
            return false;
        }

        _adding = owner;

        try
        {
            return commands.CommitLet(let);
        }
        finally
        {
            _adding = null;
        }
    }

    /// <summary>Writes every value somebody chose in as the declared default.</summary>
    /// <remarks>
    /// One write per document rather than one for the lot, which the commands work out: the rows
    /// here come from the chain and from the drawing that is picked.
    ///
    /// Public for the reason <see cref="AddParameterAsync"/> is — it is the button a test can drive.
    /// </remarks>
    /// <returns>Whether anything was written.</returns>
    public bool CommitDefaults() => _commands?.SetDefaults() == true;

    /// <summary>Keeps the value on any row still declared exactly as it was.</summary>
    /// <remarks>
    /// The rows are built again whenever anything in the project changes, and a slider somebody had
    /// dragged would otherwise snap back because a drawing was moved on the board.
    ///
    /// By name and by declaration: a parameter that has been edited since — a bound changed, a type
    /// changed — is a different slider, and carrying a value onto it would put it somewhere the new
    /// declaration does not say.
    /// </remarks>
    private IReadOnlyList<SvgViewerParameter> Carried(IReadOnlyList<SvgViewerParameter> rebuilt)
    {
        if (_carried is not { } was)
        {
            return rebuilt;
        }

        foreach (var row in rebuilt)
        {
            if (was.FirstOrDefault(had => had.IsModified && had.Declaration.Equals(row.Declaration)) is { } carried)
            {
                row.TrySet(carried.ToExprValue());
            }
        }

        return rebuilt;
    }

    /// <summary>Puts a declaration edit into the group that holds it, or says why it would not go.</summary>
    private bool Splice(string name, string label, Func<SvgSourceDocument, string?> edit)
    {
        var target = TargetFor(Holder(name));

        var was = target.Text;

        if (target.Commit(label, edit) is { } refusal)
        {
            Says(refusal);

            return false;
        }

        Says(null);

        // Nothing rebuilds the rows or the board here. A commit that changed the block edited the
        // project, and every tab open on it — this one included — has already been round through
        // Refresh by the time this returns. Doing it again drew an unwatched board twice over.
        return !string.Equals(target.Text, was, StringComparison.Ordinal);
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
            // The declarations are read from the drawing as built, which is where what its groups
            // declare has been written in.
            () => document.Built(target.Text),
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
            // What the drawing is actually rendering with: the group's values over the drawing's
            // own. The panel alone says nothing about a parameter the drawing declares for itself,
            // and a missing value refuses the whole evaluator rather than one readout.
            return ExprEvaluator.Create(document.Declarations, Bound(_inspecting!.Value.Built, Values()));
        }
        catch (Exception failure) when (failure is ExprException or ArgumentException)
        {
            // A readout nobody can work out is left blank; the row still says what the rule is.
            return null;
        }
    }

    /// <summary>
    /// Binds the values on the panel into every drawing on the board, and repaints.
    /// </summary>
    /// <remarks>
    /// Every one of them, with nothing to match: the rows are what the group declares, and a group's
    /// block is written into each of its drawings on the way to being drawn, so every drawing here
    /// declares every row. That is the whole of what replaced asking which drawings happened to
    /// declare the same thing — one declaration, in one place, and the family is whoever is under it.
    ///
    /// Cheap for a drag: the pictures are the ones already built, and <c>SetExpressionValues</c>
    /// re-evaluates a model each has cached rather than reading or compiling anything again.
    /// </remarks>
    private void Bind()
    {
        var values = Values();

        foreach (var shown in _shown)
        {
            if (shown.Built.Svg is not { } svg)
            {
                continue;
            }

            try
            {
                svg.SetExpressionValues(Bound(shown.Built, values));
            }
            catch (ExprException)
            {
                // A value a drawing will not take leaves its last rendering up, as in a viewer.
            }
        }

        _canvas.Publish();
    }

    /// <summary>What to bind into <paramref name="drawn"/> when the panel is showing <paramref name="values"/>.</summary>
    /// <remarks>
    /// The drawing's own values go in around what it takes from the panel, because the call replaces
    /// everything bound: sending part of the set would take the rest back to their defaults, and a
    /// drawing is entitled to declare one with no default at all — which would then refuse the lot.
    ///
    /// Two rows are not taken. One the drawing does not declare, which is a row whose group it does
    /// not sit under — a nested board on this canvas is entitled to be that. And one that belongs to
    /// another drawing: two drawings each declaring a <c>ring</c> of their own have two parameters
    /// that happen to be spelled alike, and moving both from one slider is the guess this panel was
    /// built to stop making.
    /// </remarks>
    private Dictionary<string, ExprValue> Bound(Drawn drawn, Dictionary<string, ExprValue> values)
    {
        if (drawn.Document is not { } document)
        {
            return values;
        }

        var bound = new Dictionary<string, ExprValue>(
            drawn.Svg?.ExpressionValues ?? Seeded(document),
            StringComparer.Ordinal);

        foreach (var pair in values)
        {
            if (!bound.ContainsKey(pair.Key) || Elsewhere(pair.Key, drawn.Drawing))
            {
                continue;
            }

            bound[pair.Key] = pair.Value;
        }

        return bound;
    }

    /// <summary>Whether <paramref name="name"/> is a row another drawing declares for itself.</summary>
    private bool Elsewhere(string name, ProjectDrawing drawing)
        => _owners.TryGetValue(name, out var holder)
           && holder is ProjectDrawing owned
           && !ReferenceEquals(owned, drawing);

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
        var chosen = _selected;

        // The addresses alone: which drawing they are in is `was`, which this already holds.
        var picked = _picked.ToList();
        var paged = _page;

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

            // And the elements inside it, which ring their drawings and fill the Element tab through
            // the same handler a click on a row does. A key the drawing no longer has selects
            // nothing, which is the honest answer: the element it named has been edited away.
            // Through the tree, which is what fills _picked again — and which drops any address
            // the rebuilt drawing no longer has, the element it named having been edited away.
            _tree.TrySelect(picked);

            // Put back after the tree, which clears it: selecting rows is what a drawing's own
            // selection is, and a page has none to select.
            _page = paged;

            Ring();
            TrackGizmo();
        }
        else if (chosen is { } group
                 && _framed.FirstOrDefault(framed => ReferenceEquals(framed.Group, group)) is { Group: { } } still)
        {
            // A group keeps its ring across a rebuild the way a drawing keeps its own. Its frame is
            // measured afresh, so the ring is taken from where the board has just put it rather
            // than from where it was.
            Choose(still.Group, still.Frame.Bounds);
        }
        else
        {
            // Inspect and Choose show the rows for what they take; nothing was taken, so the panel
            // is the chain alone and has to be told that the selection's section has gone.
            ShowDeclarations();
        }

        // The rows keep what somebody dragged them to, and a drawing read again comes back at what
        // it is seeded with — so without this the panel and the pictures beside it disagree about
        // every value, until the next drag happens to say one of them again. Outside the block
        // above, because the rows are the group's: they are not about whatever is picked, and a
        // rebuild with nothing picked leaves the same disagreement.
        if ((_parameters.Parameters ?? Array.Empty<SvgViewerParameter>()).Any(row => row.IsModified))
        {
            Bind();
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
                    Put(new SvgViewerPlacement(built.Svg!, to, Caption(drawing)), built);
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
                     .Of(items.Select(one => new SvgViewerSpread.Item(made[one].Svg!, made[one].Size)).ToList())
                     .Zip(items))
        {
            // The copy, and only the copy, goes into both lists: a click pairs a placement back to
            // its drawing by reference, and `with` makes a new record.
            Put(
                placement with
                {
                    At = new SKPoint(placement.At.X + block.X, placement.At.Y + block.Y),
                    Label = Caption(drawing)
                },
                made[drawing]);
        }
    }

    private void Put(SvgViewerPlacement placement, Drawn built) => _shown.Add((placement, built));

    /// <summary>
    /// What a press on the board takes hold of: a drawing, or the frame round a group.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A drawing before the frame it sits in, so pressing an icon moves the icon and pressing a
    /// frame's margin or its name moves the group — and the innermost frame first, for the same
    /// reason. Back to front among the drawings, which is the order a click resolves in.
    /// </para>
    /// <para>
    /// Both by their outlines and by nothing inside them. What is inside a drawing is the drawing,
    /// and it is reached by the same press: there is no mode to turn off any more, so answering for
    /// all of it would take every press that fell on a shape and leave nowhere to sweep from.
    /// </para>
    /// </remarks>
    private (object Item, SKRect Bounds)? Held(SKPoint at)
    {
        // Before the drawings, for the reason a click is answered that way: a frame's name is drawn
        // over them, so it has to be taken hold of over them too.
        if (Framed(at) is { } framed)
        {
            return (framed.Group, framed.Bounds);
        }

        for (var index = _shown.Count - 1; index >= 0; index--)
        {
            var area = Area(_shown[index].Placement);

            if (area.Width > 0f && _canvas.Grabs(area, at))
            {
                return (_shown[index].Built.Drawing, area);
            }
        }

        return null;
    }

    /// <summary>The group whose frame is taken hold of at <paramref name="at"/>, innermost first.</summary>
    /// <remarks>
    /// By the name written above it and by the line round it, rather than by anywhere inside it. A
    /// frame spans the room between the drawings it holds, so answering for that left nowhere on a
    /// full board to move the view from: a press in the gap between two icons carried the whole
    /// group instead.
    /// </remarks>
    private (ProjectGroup Group, SKRect Bounds)? Framed(SKPoint at)
    {
        foreach (var (frame, group) in _framed.OrderBy(framed => framed.Frame.Bounds.Width * framed.Frame.Bounds.Height))
        {
            if (_canvas.Grabs(frame, at))
            {
                return (group, frame.Bounds);
            }
        }

        return null;
    }

    /// <summary>Makes <paramref name="group"/> what the tab is about, and rings it.</summary>
    /// <remarks>
    /// The same ring a picked element wears, round the frame instead of round a silhouette: what it
    /// means is "this is the selection", and a board has one selection whether that is a shape, a
    /// drawing or a group.
    /// </remarks>
    private void Choose(ProjectGroup group, SKRect bounds)
    {
        Deselect();

        _selected = group;
        _showing.Text = ProjectWorkspace.Label(group);

        using var ring = new SKPathBuilder();

        ring.AddRect(bounds);

        _canvas.Highlight = ring.Detach();

        ShowDeclarations();
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
    /// Written straight into the project rather than held pending: what is pending is keyed by
    /// setting name on this tab's own node, and settling writes places on every node under it. So
    /// it is the project's edit and not the tab's, the same as a row dragged in the tree — the tab
    /// wears no mark for it, the window does, and any save writes it.
    /// </remarks>
    private void Placed(SvgViewerMove move)
    {
        if (move.Item is not ProjectNode node)
        {
            return;
        }

        // The whole tab and not the row that was dragged, because Settle is inside the gesture: the
        // first move on a board that has never been arranged writes a place for every row on it, so
        // one drag nobody meant to make turns a spread into an arrangement, and all of that is what
        // there is to take back.
        Workspace.Do(
            $"move {ProjectWorkspace.Label(node)}",
            () => Node is ProjectGroup board
                ? ProjectSnapshot.Places(board)
                : ProjectSnapshot.Attributes(Node),
            () =>
            {
                Settle();

                // The carry arrives on the grid already, put there by the canvas so that what was
                // drawn following the pointer is what gets written.
                node.X = ProjectNode.Rounded((node.X ?? 0f) + move.By.X);
                node.Y = ProjectNode.Rounded((node.Y ?? 0f) + move.By.Y);
            });
    }

    /// <summary>Gives every row of the tab the place it is already being drawn at.</summary>
    /// <remarks>
    /// Not put on the grid, even while one is on: what this writes is where things already are, and
    /// a row nobody has touched moving because somebody dragged another one is the one thing this
    /// exists to prevent.
    /// </remarks>
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

        return new SvgViewerFrame(SKRect.Inflate(bounds, label, label), name);
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
        // A frame's chrome before the drawings, because it is painted over them: what is clicked has
        // to be what is seen. Its name is no wider than the name, and its outline sits beyond the
        // drawings it holds, so what this takes from them is the little it covers.
        if (_canvas.TryGetDrawingPoint(at, out var board) && Framed(board) is { } framed)
        {
            Choose(framed.Group, framed.Bounds);

            return;
        }

        if (!_canvas.TryGetPlacementAt(at, out var placement, out var point) || placement is null)
        {
            // Beside every drawing rather than inside one, which is the board itself: that is a
            // click on nothing, and it puts the tab back to being about the group. Without it there
            // was no way back to what the group declares once a drawing had been picked.
            Deselect();
            ShowDeclarations();

            return;
        }

        var index = _shown.FindIndex(shown => ReferenceEquals(shown.Placement, placement));

        if (index < 0 || _shown[index].Built.Svg is not { } svg)
        {
            return;
        }

        if (svg.HitTestTopmostElement(new ShimSkiaSharp.SKPoint(point.X, point.Y)) is not { } element)
        {
            // On the drawing but on none of its ink, which is the page — a thing in its own right,
            // the way a group's frame is. This used to be a click that did nothing, kept that way so
            // that missing a shape by two pixels did not throw away what was being read; missing it
            // now lands on the drawing the shape is in, which is the next thing out.
            Inspect(_shown[index], placement, svg);

            _picked.Clear();
            _tree.TrySelect(Array.Empty<string>());

            _page = true;

            Ring();
            ShowElement(null);
            TrackGizmo();

            return;
        }

        Inspect(_shown[index], placement, svg);

        _page = false;
        _picked.Clear();
        _picked.Add(SvgElementAddress.Create(element).Key);

        _tree.TrySelect(_picked[0]);

        // Rung here rather than left to the row being selected. A group usually builds one file
        // several ways, so its drawings give their elements the same addresses; picking a shape in
        // one and the same shape in another asks the tree for a row it already has selected, it
        // raises nothing, and the ring stays on the drawing picked first.
        Ring(placement, svg, element);

        // And shown here for the same reason: the drawing changed even where the address did not.
        ShowElement(SvgElementAddress.Create(element).Key);

        // Tracked here for it too. The handles are measured from one drawing's scene, so picking the
        // same shape in a second copy has to move them even though the row did not change.
        TrackGizmo();
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

        // What the pane was holding is about to mean something else: a group builds one file
        // several ways, so the drawing arriving spells the same addresses for different shapes.
        _tree.Forget();
        _tree.Show(svg.SourceDocument);

        // The panel ends with what this drawing declares for itself, so it follows the selection.
        // Only this last section changes: the chain above it is the group's either way, and a value
        // somebody dragged there stays where they put it.
        ShowDeclarations();
    }

    /// <summary>Puts the ring round <paramref name="element"/>, where its drawing sits on the canvas.</summary>
    /// <remarks>
    /// The tracer answers in the drawing's own coordinates and the canvas rings in the space the
    /// drawings are arranged in, so the path is moved by the placement's offset on the way across.
    /// </remarks>
    private void Ring(SvgViewerPlacement placement, SKSvg svg, SvgElement element)
        => _canvas.Highlight = Outline(placement, svg, element);

    /// <summary>
    /// Selects what a swept rectangle caught, where it caught it all in one drawing.
    /// </summary>
    /// <remarks>
    /// A rectangle over two drawings has not said which was meant, and selects nothing — which is
    /// also what a rectangle over nothing does, so the two share the answer.
    /// </remarks>
    private void SelectEnclosed(SKRect swept)
    {
        _sweep.TryTrace(_canvas, swept, out _);

        var caught = _sweep.Caught;

        _sweep.Forget();

        // Nothing caught, or caught in more than one drawing, which is the same answer: a selection
        // is of one drawing, and a rectangle over two has not said which.
        if (caught.Count == 0 || Shown(caught[0].Placement) is not { } shown || shown.Built.Svg is not { } svg)
        {
            Deselect();
            ShowDeclarations();

            return;
        }

        Inspect(shown, caught[0].Placement, svg);

        // Through the tree, which is what tells everything else — including this panel's own
        // handler, which is where _picked is filled from.
        _tree.TrySelect(caught.Select(pick => pick.AddressKey).ToList());

        Ring();
        TrackGizmo();
    }

    /// <summary>Rings everything selected, wherever on the board it is.</summary>
    private void Ring() => _canvas.Highlight = Ringed();

    /// <summary>
    /// What the selection covers: the page, or the silhouette of every element picked in it.
    /// </summary>
    /// <remarks>
    /// A rectangle for a page, which is the same shape a selected group wears — what a ring means
    /// here is "this is the selection", whether that is a shape, a drawing or a group.
    /// </remarks>
    private SKPath? Ringed()
    {
        if (!_page)
        {
            return SvgViewerPicks.Outline(Picks());
        }

        if (_inspecting is not { } inspecting || Area(inspecting.Placement) is not { Width: > 0f } page)
        {
            return null;
        }

        using var ring = new SKPathBuilder();

        ring.AddRect(page);

        return ring.Detach();
    }

    /// <summary>What is selected, as the drawing it is in and the addresses inside it.</summary>
    private IReadOnlyList<SvgViewerPick> Picks()
        => _inspecting is { } inspecting
            ? _picked.Select(address => new SvgViewerPick(inspecting.Placement, address)).ToList()
            : Array.Empty<SvgViewerPick>();

    /// <summary>Rings what a sweep is over, while it is still being drawn.</summary>
    /// <remarks>
    /// The same question the release will ask, so what the rectangle has caught is visible before
    /// anybody commits to it. Retrace rather than the ring itself, which would restart its pulse on
    /// every frame; null is the sweep taken back, and the ring goes back to what is selected.
    /// </remarks>
    private void ShowEnclosed(SKRect? swept)
    {
        if (!_sweep.TryTrace(_canvas, swept, out var outline))
        {
            return;
        }

        // A sweep taken back leaves the ring on what is actually selected.
        _canvas.Retrace(swept is { } ? outline : SvgViewerPicks.Outline(Picks()));
    }

    /// <summary>
    /// Traces the ring again for an element that has moved under it.
    /// </summary>
    /// <remarks>
    /// The ring comes off the scene, so a drag that moves the shape leaves it behind on the
    /// silhouette the shape used to have. Retrace rather than <see cref="Ring"/>, which restarts the
    /// pulse announcing a new selection: at one frame per pointer move that is a ring flashing for
    /// as long as the drag lasts, about something nobody just picked.
    /// </remarks>
    private void Retrace() => _canvas.Retrace(SvgViewerPicks.Outline(Picks()));

    /// <summary>What the selection covers, where its drawings sit on the board.</summary>
    private static SKPath? Outline(SvgViewerPlacement placement, SKSvg svg, SvgElement element)
        => SvgViewerPicks.Outline(
            new[] { new SvgViewerPick(placement, SvgElementAddress.Create(element).Key) });

    // ---- editing on the canvas ---------------------------------------------------------------

    /// <summary>
    /// Where a control point falls in the space the drawings are arranged in.
    /// </summary>
    /// <remarks>
    /// Which is the space the gesture now works in: it is told where each of its members' drawings
    /// sits, so it can hold members of two drawings at once — and there is no single drawing's own
    /// space for a selection like that to be in.
    /// </remarks>
    private ShimSkiaSharp.SKPoint? Arranged(Point at)
        => _canvas.TryGetDrawingPoint(at, out var point) ? new ShimSkiaSharp.SKPoint(point.X, point.Y) : null;

    /// <summary>Whether a press would take hold of something rather than reaching into it.</summary>
    /// <remarks>
    /// The same question <see cref="Held"/> answers for the grip, asked before the gesture's body
    /// claim so the two cannot both take the press. Cheap enough to ask twice: it runs once per
    /// press, over the drawings on one tab.
    /// </remarks>
    private bool Grabbed(Point at)
        => _canvas.TryGetDrawingPoint(at, out var point) && Held(point) is { };

    /// <summary>Puts the handles on the picked element of the picked drawing, or takes them off.</summary>
    private void TrackGizmo()
    {
        _paging.Track(_page && _inspecting is { } paged ? Area(paged.Placement) : null);

        _gizmo.Track(
            _inspecting?.Built.Svg,
            _inspecting is { } inspecting
                ? new ShimSkiaSharp.SKPoint(inspecting.Placement.At.X, inspecting.Placement.At.Y)
                : default,
            Members());

        ShowGizmo();
    }

    /// <summary>
    /// The selection as the gesture knows it: which drawing each element is in, and where that
    /// drawing sits on the board.
    /// </summary>
    /// <remarks>
    /// Keyed by where the pick stands in the list, which is all the gesture does with a key — it
    /// hands it back with whatever was written, and <see cref="Writing"/> turns it into a file and
    /// an address there.
    /// </remarks>
    private IReadOnlyList<SvgViewerGizmoMember> Members()
        => Picks()
            .Where(pick => pick.Element is { })
            .Select(pick => new SvgViewerGizmoMember(pick.Element!, pick.AddressKey))
            .ToList();

    /// <summary>
    /// Hands the canvas the box as it now stands.
    /// </summary>
    /// <remarks>
    /// No longer moved to where the drawing sits: the gesture is told where each of its members is
    /// and answers in the space the board is arranged in, which is the only space a box round
    /// elements of two different drawings could be in.
    /// </remarks>
    /// <remarks>
    /// The page's box or the elements', never both, because a board has one selection. A page's
    /// carries no stalk: a drawing's edges are what its width and height say, and there is nowhere
    /// in a document for a turned one to be written.
    /// </remarks>
    private void ShowGizmo()
    {
        if (_page)
        {
            _canvas.GizmoTurns = false;
            _canvas.Gizmo = _paging.Box((float)_canvas.Scale);

            return;
        }

        _canvas.GizmoTurns = true;
        _canvas.Gizmo = _gizmo.Box((float)_canvas.Scale);
    }

    /// <summary>
    /// What a drag would be written into: the drawing's own file, at the address it spells there.
    /// </summary>
    /// <remarks>
    /// The translation <see cref="ShowElement"/> does, for the same reason — the tree is of the
    /// drawing as the project built it, and the file it came from spells the row differently. Null
    /// where the row is the recipe's own invention and the file has nowhere to put it.
    /// </remarks>
    private (ISvgViewerDeclarationTarget Target, IReadOnlyDictionary<string, string> Addresses)? Writing()
    {
        if (_inspecting is not { } inspecting
            || inspecting.Built.Document is not { } document
            || document.SourceText is null
            || _picked.Count == 0)
        {
            return null;
        }

        var target = TargetOf?.Invoke(inspecting.Built.Drawing)
                     ?? new DrawingTarget(Workspace, inspecting.Built.Drawing);

        // Once for the whole selection. The table is the file read and walked, and asking for it
        // per element made a drag of twenty elements twenty parses of the same text.
        var spelt = SvgSourceElements.Addresses(target.Text, document.Built(target.Text));
        var addresses = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var address in _picked)
        {
            if (spelt.TryGetValue(address, out var mine))
            {
                addresses[address] = mine;
            }
        }

        return addresses.Count == 0 ? null : (target, addresses);
    }

    /// <summary>The board's own record of a drawing that is on it, or null where it is not.</summary>
    private (SvgViewerPlacement Placement, Drawn Built)? Shown(SvgViewerPlacement placement)
    {
        foreach (var shown in _shown)
        {
            if (ReferenceEquals(shown.Placement, placement))
            {
                return shown;
            }
        }

        return null;
    }

    private void BeginEdit(Point at)
    {
        if (Arranged(at) is not { } arranged)
        {
            return;
        }

        if (_page)
        {
            _paging.Begin(new SKPoint(arranged.X, arranged.Y), (float)_canvas.Scale);

            return;
        }

        if (Writing() is not { } writing)
        {
            Says(Unwritten);

            return;
        }

        // The table once, rather than once per member: it is the file read and walked, and asking
        // the gesture to ask for it per element made a drag of twenty twenty parses of one text.
        Says(_gizmo.Begin(arranged, (float)_canvas.Scale, key => Driven(writing, key)));

        ShowGizmo();
    }

    private void DragEdit(Point at)
    {
        if (Arranged(at) is not { } arranged)
        {
            return;
        }

        if (_paging.IsDragging)
        {
            _paging.Drag(new SKPoint(arranged.X, arranged.Y));

            // The handles follow the pointer and the tile does not: a drawing built at a new size is
            // a re-parse, and one of those per frame is what this tab already refuses to do.
            ShowGizmo();

            return;
        }

        _gizmo.Drag(arranged);

        ShowGizmo();
        Retrace();
        _canvas.Publish();
    }

    /// <remarks>
    /// The commit rebuilds the one drawing whose text it changed, which is what replaces the live
    /// mutation with a drawing read from the file.
    ///
    /// Every exit that does not commit has to put the element back. The drag is applied to the built
    /// document as it is made, so a release the file will not take — an element whose transform is
    /// spelt in its style attribute, which is refused on the way in — would otherwise leave the
    /// drawing carrying a transform its own text does not have, for good: nothing rebuilds a drawing
    /// whose text did not change.
    /// </remarks>
    private void EndEdit()
    {
        if (_paging.IsDragging)
        {
            EndPage();

            return;
        }

        if (_gizmo.End() is not { } edit)
        {
            ShowGizmo();

            return;
        }

        if (Writing() is not { } writing)
        {
            Undo(Unwritten);

            return;
        }

        // One drawing, so one commit — and one commit is one entry in that drawing's history and
        // all or nothing besides: a refusal on the fourth element puts the first three back with it.
        var refusal = writing.Target.Commit(
            edit.Label,
            source => edit.Write(
                source,
                key => writing.Addresses.TryGetValue(key, out var at) ? at : null));

        if (refusal is { })
        {
            Undo(refusal);

            return;
        }

        Says(null);
        Written();
    }

    /// <summary>Puts the element back where the drag found it, and says why.</summary>
    private void Undo(string? note)
    {
        _gizmo.Revert();

        Says(note);
        ShowGizmo();
        Retrace();
        _canvas.Publish();
    }

    /// <summary>
    /// Writes the size the page was dragged to, into the drawing's own file.
    /// </summary>
    /// <remarks>
    /// The drawing's own width and height and not the size this project asks for it to be built at.
    /// Where the project does ask, the two disagree and the project wins on the next build — so the
    /// tile stays the size it was while the file underneath it says something else, and the note is
    /// the only place that says so.
    /// </remarks>
    private void EndPage()
    {
        var edges = _paging.End();

        if (edges is not { } moved || _inspecting is not { } inspecting || inspecting.Built.Document is not { } document)
        {
            ShowGizmo();

            return;
        }

        if (document.SourceText is null)
        {
            Says(Unwritten);
            ShowGizmo();

            return;
        }

        // The target alone, not Writing(): that answers for a selection of elements and builds the
        // table of where each is spelt in the file. A page is not one of them and has no address —
        // what a resize needs is only the file to write into.
        var target = TargetOf?.Invoke(inspecting.Built.Drawing)
                     ?? new DrawingTarget(Workspace, inspecting.Built.Drawing);

        var refusal = target.Commit(
            "resize the page",
            source => document.Reframe(source, moved.Left, moved.Top, moved.Right, moved.Bottom));

        if (refusal is null)
        {
            Written();
        }

        // After the rebuild, which lays the board out again and says what it has to say about that:
        // said before it, this is wiped by the very thing the drag asked for.
        Says(refusal ?? Pinned(inspecting.Built.Drawing));

        ShowGizmo();
    }

    /// <summary>
    /// What to say when the drawing has just been resized under a project that pins its size.
    /// </summary>
    /// <remarks>
    /// A width or a height and not a scale. A scale is a factor of whatever the drawing is, so a
    /// drawing made half as wide again is still drawn half as wide again and the tile follows the
    /// drag — there is nothing to say. A width is a number the build writes over the drawing's own,
    /// so the file says one thing and the tile goes on saying another, and a drag that left the tile
    /// where it was is otherwise silent. Silence there reads as a gesture that did not work.
    /// </remarks>
    private static string? Pinned(ProjectNode drawing)
        => drawing.EffectiveWidth is { } || drawing.EffectiveHeight is { }
            ? $"{ProjectWorkspace.Label(drawing)} is now that size, but this project builds it at the width its settings ask for."
            : null;

    private void CancelEdit()
    {
        // The page goes back to the size the file says, which is where it was: nothing is written
        // while the handles follow the pointer.
        _paging.Cancel();

        _gizmo.Cancel();

        ShowGizmo();
        Retrace();
        _canvas.Publish();
    }

    private const string Unwritten = "That row is not written in this drawing's file, so it cannot be dragged.";

    /// <summary>
    /// The element's transform as the file spells it, where an expression writes it, and null
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// Handed to the drag rather than used to refuse it: a gesture the element's own geometry can
    /// hold does not touch the transform at all, and one that cannot is composed onto this text
    /// instead of onto the number the expression came to.
    /// </remarks>
    private static string? Driven(
        (ISvgViewerDeclarationTarget Target, IReadOnlyDictionary<string, string> Addresses) writing,
        string key)
        => writing.Addresses.TryGetValue(key, out var address)
           && SvgSourceDocument.Read(writing.Target.Text, out _) is { } source
           && SvgAttributeEditor.Attribute(source, address, "transform") is { } written
           && written.Contains("{{", StringComparison.Ordinal)
            ? written
            : null;

    /// <summary>One drawing built the way the project builds it, or why it could not be.</summary>
    /// <summary>
    /// Builds a drawing, or hands back the build it already has where nothing it reads has changed.
    /// </summary>
    /// <remarks>
    /// The three things a build reads are the text, what the groups above it declare into that text,
    /// and the size it is asked for, so agreeing about all three is agreeing about the picture. Most
    /// of what refreshes this tab changes none of them: a drop
    /// writes x and y, a tree move writes an order, a root setting writes what the code generator
    /// does -- and re-parsing forty drawings to answer any of them cost the zoom, the ring and a
    /// tenth of a second each time.
    ///
    /// Comparing what was read beats classifying what happened: there are ten ways into
    /// <see cref="ProjectWorkspace.Edit"/> and the event they raise says nothing about which fired.
    /// </remarks>
    private Drawn Draw(ProjectDrawing drawing, Drawn? was)
    {
        // Through the host where a tab is holding this drawing, so the canvas shows what that tab
        // shows rather than what the project itself holds.
        var text = TargetOf?.Invoke(drawing)?.Text ?? drawing.Text;
        var declared = ProjectDeclarations.Declared(drawing);
        var sizing = Sizing(drawing);

        if (was is { } already
            && string.Equals(already.Text, text, StringComparison.Ordinal)
            && string.Equals(already.Declared, declared, StringComparison.Ordinal)
            && already.Sizing == sizing)
        {
            return already;
        }

        try
        {
            // Through the blocks the groups above it declare, which is what the build and the
            // drawing's own tab read it through too.
            var document = SvgViewerDocument.LoadFromSvg(
                text,
                null,
                ProjectWorkspace.SizeOf(drawing),
                own => ProjectDeclarations.Built(drawing, own));

            // What the panel would show for this drawing on opening it, which is what a viewer
            // binds and so what the drawing's own tab renders.
            try
            {
                document.Svg.SetExpressionValues(Seeded(document));
            }
            catch (ExprException)
            {
            }

            return new Drawn(drawing, text, declared, sizing, document, null);
        }
        catch (Exception failure)
        {
            // Anything: this is user data reaching a parser, and it arrives as an XmlException, a
            // FormatException, one of the IO exceptions or the loader's own refusal. A narrower set
            // would eventually let one through, and one bad drawing would cost the whole tab.
            return new Drawn(drawing, text, declared, sizing, null, failure.Message);
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
        // The tree holds elements of a document that may be about to be disposed, and so do the ring
        // and the handles. The rows are not let go with them: what is declared does not change
        // because a board was laid out again.
        Deselect();

        _shown.Clear();
        _framed.Clear();

        Says(null);
    }

    /// <summary>Lets go of the drawing being looked at, and of the element inside it.</summary>
    /// <remarks>
    /// Shows no rows of its own: a rebuild does this on its way past and takes the selection again
    /// at the end, so the panel would be built twice for one gesture. Whoever is finished with the
    /// selection rather than passing through it says so itself.
    /// </remarks>
    private void Deselect()
    {
        _canvas.Highlight = null;

        // With the ring, and for its reason: the handles are measured from a scene node of a
        // document a build may be about to dispose.
        _gizmo.Track(null, default, Array.Empty<SvgViewerGizmoMember>());
        _canvas.Gizmo = null;

        _inspecting = null;
        _selected = null;
        _page = false;
        _picked.Clear();
        _tree.Show(null);
        ShowElement(null);
        _showing.Text = "Click a drawing to see what it is made of.";
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

    /// <summary>The line above the canvas, or null where there is none.</summary>
    /// <remarks>For a host or a test to read what the board last had to say about a gesture.</remarks>
    public string? Notice => _notice.IsVisible ? _notice.Text : null;

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

        _canvas.ViewChanged += (_, _) =>
            _zoom.Text = (_canvas.Scale * 100d).ToString("0", CultureInfo.CurrentCulture) + "%";

        var ratio = new ToggleButton
        {
            Content = "Lock ratio",
            IsChecked = _gizmo.LocksAspect,
            [ToolTip.TipProperty] = "Keep the element's proportions while a handle is dragged"
        };

        ratio.IsCheckedChanged += (_, _) => _gizmo.LocksAspect = ratio.IsChecked == true;

        var snap = new ToggleButton
        {
            Content = "Snap",
            IsChecked = StudioSettings.SnapToGrid,
            [ToolTip.TipProperty] = "Land what is dragged on the grid rather than where the pointer stopped"
        };

        // Straight into the setting, unlike the lock beside it: where a gesture lands is remembered
        // between sessions, and a board that asked only itself would disagree with the next tab.
        snap.IsCheckedChanged += (_, _) =>
        {
            if (StudioSettings.SnapToGrid == (snap.IsChecked == true))
            {
                return;
            }

            StudioSettings.SnapToGrid = snap.IsChecked == true;

            Reread();

            SettingChanged?.Invoke(this, EventArgs.Empty);
        };

        _snapping = snap;

        bar.Children.Add(Tool("Fit", "Fit to window", () => _canvas.Fit()));
        bar.Children.Add(Tool("1:1", "Actual size", () => _canvas.ActualSize()));
        bar.Children.Add(Tool("−", "Zoom out, or scroll down", () => _canvas.ZoomOut()));
        bar.Children.Add(_zoom);
        bar.Children.Add(Tool("+", "Zoom in, or scroll up", () => _canvas.ZoomIn()));
        var captions = new ToggleButton
        {
            Content = "Captions",
            IsChecked = StudioSettings.DrawingCaptions,
            [ToolTip.TipProperty] = "Write each drawing's name inside its own top corner"
        };

        captions.IsCheckedChanged += (_, _) =>
        {
            if (StudioSettings.DrawingCaptions == (captions.IsChecked == true))
            {
                return;
            }

            StudioSettings.DrawingCaptions = captions.IsChecked == true;

            Reread();

            SettingChanged?.Invoke(this, EventArgs.Empty);
        };

        _captions = captions;

        bar.Children.Add(ratio);
        bar.Children.Add(snap);
        bar.Children.Add(captions);

        return bar;
    }

    private static Button Tool(string content, string tip, Action click)
    {
        var button = new Button { Content = content, [ToolTip.TipProperty] = tip };

        button.Click += (_, _) => click();

        return button;
    }

    /// <summary>Where this tab's own assets are, for an icon a row is drawn with.</summary>
    private static readonly Uri Home = new("avares://Svg.Studio/");

    /// <remarks>
    /// The document and not just its picture: the declarations shown on the panel and the text the
    /// colours are surveyed from are both read off it.
    /// </remarks>
    private sealed record Drawn(
        ProjectDrawing Drawing,
        string Text,
        string Declared,
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

        if (node is not ProjectRoot)
        {
            _properties.Children.Add(new Separator { Margin = new Thickness(0, 6) });
        }

        Add("namespace");
        Add("class");

        if (node is ProjectRoot)
        {
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
                // Written as typed, grid or no grid: a number somebody has entered is the number
                // they meant, and rounding it would be the box arguing with them.
                //
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
        "namespace" => node.Namespace,
        "class" => node.Class,
        "padding" => node.Padding,
        "x" => node.X is { } x ? Number(x) : null,
        "y" => node.Y is { } y ? Number(y) : null,
        "width" => node.Width is { } width ? Number(width) : null,
        "height" => node.Height is { } height ? Number(height) : null,
        "scale" => node.Scale is { } scale ? Number(scale) : null,
        // The project's own three. Left out, they showed empty however the file was written, and an
        // edit to one could never be recognised as typed back to what the file says — so it stayed
        // pending for ever.
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
