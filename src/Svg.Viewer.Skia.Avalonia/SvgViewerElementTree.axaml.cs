// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Svg;
using Svg.SourceEditing;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// Every element of the open drawing, as a tree.
/// </summary>
/// <remarks>
/// The whole document and not only what is drawn: <c>&lt;defs&gt;</c> and its contents, the
/// <c>&lt;e:code&gt;</c> block, a <c>&lt;title&gt;</c>. What is in a file is the question this pane
/// answers, and half of it is the half that never appears on the canvas.
///
/// Rebuilt outright rather than patched. <see cref="SvgElement"/> raises nothing when a child is
/// removed — <c>OnElementRemoved</c> calls a protected hook and no event — so there is no edit this
/// could follow, and the drawing is reloaded from its text on every keystroke anyway.
/// </remarks>
public partial class SvgViewerElementTree : UserControl
{
    private readonly TreeView _tree;
    private readonly Grid _dropHost;
    private readonly Border _dropLine;
    private readonly TextBlock _dropWord;
    private readonly TextBlock _empty;
    private readonly TextBox _filter;

    /// <summary>Which rows are open, by address, so a rebuild does not fold the tree up.</summary>
    /// <summary>How far the pointer travels before a press becomes a drag.</summary>
    private const double DragThreshold = 4d;

    /// <summary>What a dragged row carries: its address, then each property it could be applied as, spaced.</summary>
    private static readonly DataFormat<string> RowFormat = DataFormat.CreateStringApplicationFormat("SvgViewerElementRow");

    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    /// <summary>
    /// What is open while a filter is on, kept apart from <see cref="_expanded"/>.
    /// </summary>
    /// <remarks>
    /// A filter has to open everything it keeps, or a match three levels down is behind a closed
    /// row and the box appears to do nothing. Written here rather than into the remembered set, so
    /// clearing the box leaves the tree folded the way the reader left it.
    /// </remarks>
    private readonly HashSet<string> _matched = new(StringComparer.Ordinal);

    private readonly Dictionary<string, SvgViewerElementNode> _byAddress = new(StringComparer.Ordinal);

    private SvgDocument? _document;
    private string _query = string.Empty;
    private SvgViewerElementNode? _root;
    private SvgViewerElementNode? _row;
    private PointerPressedEventArgs? _rowPressed;
    private Point _rowPressedAt;

    /// <summary>What was selected before the press that picked a row up.</summary>
    /// <remarks>
    /// Pressing a row selects it, which turns the element panel to it. A row that could be dropped
    /// on that panel puts this back as it starts moving, so the panel shows what it would land on.
    /// </remarks>
    private List<string> _before = new();

    /// <summary>The rows selected, the first of them being the one the followers are anchored to.</summary>
    /// <remarks>
    /// A list rather than a set: the order is what says which row is the anchor, and the restore
    /// has to put that one back as the tree's own selected item.
    /// </remarks>
    private readonly List<string> _selectedAddresses = new();

    private bool _restoring;

    /// <summary>Whether a set of rows is being handed to the control a row at a time.</summary>
    private bool _choosing;

    public SvgViewerElementTree()
    {
        AvaloniaXamlLoader.Load(this);

        _tree = this.FindControl<TreeView>("Tree")!;
        _dropHost = this.FindControl<Grid>("DropHost")!;
        _dropLine = this.FindControl<Border>("DropLine")!;
        _dropWord = this.FindControl<TextBlock>("DropWord")!;

        _tree.AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Tunnel);
        _tree.PointerMoved += OnRowMoved;
        // Entered as well as moved over: coming back from another control raises no move in between.
        _tree.AddHandler(DragDrop.DragEnterEvent, OnRowDragOver);
        _tree.AddHandler(DragDrop.DragOverEvent, OnRowDragOver);
        _tree.AddHandler(DragDrop.DragLeaveEvent, (_, _) => HideDrop());
        _tree.AddHandler(DragDrop.DropEvent, OnRowDrop);

        DragDrop.SetAllowDrop(_tree, true);

        _tree.ContextMenu = Menu();
        _tree.KeyDown += OnTreeKeyDown;

        _empty = this.FindControl<TextBlock>("EmptyLabel")!;
        _filter = this.FindControl<TextBox>("FilterBox")!;

        _filter.TextChanged += (_, _) =>
        {
            _query = _filter.Text ?? string.Empty;

            Show(_document);
        };

        _tree.SelectionChanged += (_, _) =>
        {
            if (_restoring || _choosing)
            {
                return;
            }

            _selectedAddresses.Clear();
            _selectedAddresses.AddRange(SelectedNodes.Select(node => node.AddressKey));

            Selected?.Invoke(this, SelectedNode);
        };
    }

    /// <summary>Raised when a row is selected, or with null when the selection is dropped.</summary>
    /// <remarks>The row can be one the filter hides, which is then not in the tree.</remarks>
    public event EventHandler<SvgViewerElementNode?>? Selected;

    /// <summary>
    /// Moves a row to where it was dropped, for a host that has somewhere to write it.
    /// </summary>
    /// <remarks>
    /// Wired rather than built in, and the rows are draggable only where it or
    /// <see cref="ClipRequested"/> is: this control is also the tree of a project group's tab, which
    /// moves nothing, and there only a row that can be applied is picked up.
    /// </remarks>
    public Func<string, string, SvgElementDrop, bool>? MoveRequested { get; set; }

    /// <summary>Writes an empty group where a row was picked, for the same kind of host.</summary>
    public Func<string, SvgElementDrop, bool>? NewGroupRequested { get; set; }

    /// <summary>Takes the rows named out of the drawing, for the same kind of host.</summary>
    public Func<IReadOnlyList<string>, bool>? DeleteRequested { get; set; }

    /// <summary>Writes a copy of each row named after it, for the same kind of host.</summary>
    public Func<IReadOnlyList<string>, bool>? DuplicateRequested { get; set; }

    /// <summary>
    /// Clips or masks a row with another, for any host that writes: the content's row, the
    /// target's, then <c>clip-path</c> or <c>mask</c>. Asked by the menu and by a drop.
    /// </summary>
    public Func<string, string, string, bool>? ClipRequested { get; set; }

    /// <summary>Clips or masks a row with a new clip path or mask covering it, for the same kind of host.</summary>
    public Func<string, string, bool>? NewClipRequested { get; set; }

    /// <summary>The rows' menu, showing whatever a host has wired and nothing where it wired nothing.</summary>
    private ContextMenu Menu()
    {
        var group = Item("New group", () => NewGroupRequested is { } write && SelectedNode is { } row && write(row.AddressKey, SvgElementDrop.After));
        var clip = Item("New clip path", () => New("clip-path"));
        var mask = Item("New mask", () => New("mask"));
        var clipWith = Item(string.Empty, () => With("clip-path"));
        var maskWith = Item(string.Empty, () => With("mask"));
        var duplicate = Item("Duplicate", () => DuplicateRequested is { } write && write(_selectedAddresses.ToList()));
        var delete = Item("Delete", () => DeleteRequested is { } write && write(_selectedAddresses.ToList()));
        var menu = new ContextMenu { ItemsSource = new[] { group, clip, mask, clipWith, maskWith, duplicate, delete } };

        menu.Opening += (_, e) =>
        {
            var command = Command();
            var pair = ClipRequested is { } ? Paired() : null;

            group.IsVisible = NewGroupRequested is { };
            clip.IsVisible = mask.IsVisible = NewClipRequested is { };
            clipWith.IsVisible = pair is { } && Offers(pair.Value, "clip-path");
            maskWith.IsVisible = pair is { } && Offers(pair.Value, "mask");
            duplicate.IsVisible = DuplicateRequested is { };
            delete.IsVisible = DeleteRequested is { };

            group.IsEnabled = SelectedNode is { };
            clip.IsEnabled = One("clip-path");
            mask.IsEnabled = One("mask");
            duplicate.IsEnabled = delete.IsEnabled = _selectedAddresses.Count > 0;

            // A TextBlock, since the ids are what the drawing says and the first underscore of a
            // header string is an access key.
            if (pair is (var target, var shape))
            {
                clipWith.Header = new TextBlock { Text = $"Clip {Named(target)} with {Named(shape)}" };
                maskWith.Header = new TextBlock { Text = $"Mask {Named(target)} with {Named(shape)}" };
            }

            // Shown rather than bound, which is what a MenuItem's gesture is: the keys are answered
            // by the tree and the canvas, so they work while this menu is closed.
            duplicate.InputGesture = new KeyGesture(Key.D, command);
            delete.InputGesture = new KeyGesture(Key.Delete);

            e.Cancel = !(group.IsVisible || clip.IsVisible || clipWith.IsVisible || maskWith.IsVisible || duplicate.IsVisible || delete.IsVisible);
        };

        return menu;

        static MenuItem Item(string header, Func<bool> run)
        {
            var item = new MenuItem { Header = header };

            item.Click += (_, _) => run();

            return item;
        }

        bool New(string property) => NewClipRequested is { } make && _selectedAddresses is [var row] && make(row, property);

        bool One(string property) => _selectedAddresses is [var key] && _byAddress.TryGetValue(key, out var row) && Takes(row, property);

        bool With(string property)
            => ClipRequested is { } apply && Paired() is (var target, var shape) && apply(shape.AddressKey, target.AddressKey, property);

        // A clip path offers only Clip and a mask only Mask; anything else offers both, and the file refuses what it must.
        static bool Offers((SvgViewerElementNode Target, SvgViewerElementNode Shape) pair, string property)
            => Takes(pair.Target, property) && Applies(pair.Shape) is var applies && (applies.Length == 0 || applies.Contains(property));

        static string Named(SvgViewerElementNode node)
            => string.IsNullOrEmpty(node.Element.ID) ? node.Label : $"{node.Label} #{node.Element.ID}";
    }

    /// <summary>The two rows selected, as what would be clipped and what it would be clipped with; null unless there are two.</summary>
    /// <remarks>
    /// What it is clipped with is the row kept off the canvas — a clip path, a mask, a shape in
    /// &lt;defs&gt; — where only one is. Otherwise it is the one painted later, the way a shape is drawn
    /// over what it is to cut: by address, index by index, and a child after the group it is in.
    /// </remarks>
    private (SvgViewerElementNode Target, SvgViewerElementNode Shape)? Paired()
    {
        if (_selectedAddresses is not [var one, var other]
            || !_byAddress.TryGetValue(one, out var first)
            || !_byAddress.TryGetValue(other, out var second)
            || SvgElementAddress.Parse(one) is not { } a
            || SvgElementAddress.Parse(other) is not { } b)
        {
            return null;
        }

        return (Applies(first).Length > 0, Applies(second).Length > 0) switch
        {
            (true, false) => (second, first),
            (false, true) => (first, second),
            _ => a.ChildIndexes.AsSpan().SequenceCompareTo(b.ChildIndexes) < 0 ? (first, second) : (second, first)
        };
    }

    /// <summary>Whether <paramref name="row"/> can be given <paramref name="property"/>, as the file would let it.</summary>
    /// <remarks>Never the drawing itself, which has no place of its own to cover or carry a shape into.</remarks>
    private static bool Takes(SvgViewerElementNode row, string property)
        => row.AddressKey.Length > 0 && SvgElementEditor.Clippable(row.Label, property);

    /// <summary>The platform's command key, so the gesture is ⌘D where the menu says it is.</summary>
    private KeyModifiers Command()
        => this.GetPlatformSettings()?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (_selectedAddresses.Count == 0)
        {
            return;
        }

        // Back as well as Delete: the two are one key on a Mac keyboard. A copy of the selection,
        // because writing it changes it under the host's feet.
        if (e.Key is Key.Delete or Key.Back && e.KeyModifiers == KeyModifiers.None && DeleteRequested is { } delete)
        {
            e.Handled = true;

            delete(_selectedAddresses.ToList());
        }
        else if (e.Key == Key.D && e.KeyModifiers == Command() && DuplicateRequested is { } duplicate)
        {
            e.Handled = true;

            duplicate(_selectedAddresses.ToList());
        }
    }

    // ---- dragging a row ---------------------------------------------------------------------

    /// <summary>A drag of the row at <paramref name="addressKey"/>, which could be applied as each of <paramref name="applies"/>.</summary>
    /// <remarks>
    /// Everything is in the payload rather than in a field, so the element panel, which is another
    /// control, can read it, and a drag can be made without a pointer.
    /// </remarks>
    public static DataTransfer Carrying(string addressKey, params string[] applies)
    {
        var data = new DataTransfer();

        data.Add(DataTransferItem.Create(RowFormat, string.Join(' ', applies.Prepend(addressKey))));

        return data;
    }

    /// <summary>The row a drag is carrying, and what it could be applied as, or null where it carries no row.</summary>
    public static (string Key, IReadOnlyList<string> Applies)? Carried(DragEventArgs e)
        => e?.DataTransfer is { } carried && carried.Contains(RowFormat) && carried.TryGetValue(RowFormat) is { } written
            ? (written.Split(' ')[0], written.Split(' ')[1..])
            : null;

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        _row = null;
        _rowPressed = null;

        if ((MoveRequested is null && ClipRequested is null)
            || e.Source is not Visual source
            // The chevron folds the row; it does not pick it up.
            || source.FindAncestorOfType<ToggleButton>(true) is { }
            || source.FindAncestorOfType<TreeViewItem>(true)?.DataContext is not SvgViewerElementNode node
            // The drawing itself is the file. There is nowhere to put it.
            || node.AddressKey.Length == 0
            || (MoveRequested is null && Applies(node).Length == 0)
            || !e.GetCurrentPoint(_tree).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _row = node;
        _rowPressed = e;
        _rowPressedAt = e.GetPosition(_tree);
        _before = _selectedAddresses.ToList();
    }

    private async void OnRowMoved(object? sender, PointerEventArgs e)
    {
        if (_row is not { } row || _rowPressed is not { } pressed)
        {
            return;
        }

        if (!e.GetCurrentPoint(_tree).Properties.IsLeftButtonPressed)
        {
            _row = null;
            _rowPressed = null;

            return;
        }

        var travelled = e.GetPosition(_tree) - _rowPressedAt;

        if (Math.Abs(travelled.X) < DragThreshold && Math.Abs(travelled.Y) < DragThreshold)
        {
            return;
        }

        var applies = Applies(row);

        _rowPressed = null;

        if (applies.Length > 0 && !_before.SequenceEqual(_selectedAddresses))
        {
            Select(_before, reveal: false);
        }

        try
        {
            // Link and Copy as well, since an apply answers one of them, and the drag source keeps
            // only the answers it was started with.
            await DragDrop.DoDragDropAsync(
                pressed,
                Carrying(row.AddressKey, applies),
                DragDropEffects.Move | DragDropEffects.Link | DragDropEffects.Copy);
        }
        finally
        {
            _row = null;

            HideDrop();
        }
    }

    private void OnRowDragOver(object? sender, DragEventArgs e)
    {
        if (Carried(e) is null)
        {
            HideDrop();

            return;
        }

        // Taken, or the viewer's own handler answers for it: that one is about files dropped on the
        // drawing and turns away a drag carrying none — which is every drag of a row, so not one of
        // them could be started at all.
        e.Handled = true;

        if (Landing(e) is not { } landing)
        {
            e.DragEffects = DragDropEffects.None;

            HideDrop();

            return;
        }

        e.DragEffects = landing.Applied is { } ? Applying(e) : DragDropEffects.Move;

        ShowDrop(landing.Item, landing.Where, landing.Applied);
    }

    private void OnRowDrop(object? sender, DragEventArgs e)
    {
        HideDrop();

        // Worked out again rather than kept from the last move, so ⌥ and the row under the pointer are read as it is let go.
        if (Landing(e) is not { } landing)
        {
            return;
        }

        e.Handled = true;

        if (landing.Applied is { } property)
        {
            e.DragEffects = Applying(e);

            ClipRequested?.Invoke(landing.Dragged, landing.Over.AddressKey, property);
        }
        else
        {
            e.DragEffects = DragDropEffects.Move;

            MoveRequested?.Invoke(landing.Dragged, landing.Over.AddressKey, landing.Where);
        }
    }

    /// <summary>
    /// Where a dragged row would land, and as what: moved beside or into the row under the pointer,
    /// or applied to it as a clip path or a mask. Null where it would land nowhere.
    /// </summary>
    /// <remarks>
    /// A row kept off the canvas applies over the whole of a drawn row, since moving it there is
    /// refused anyway. One that is not — a clip path written beside the shapes — still moves on the
    /// row's outer quarters, and applies in the middle, where a group would have taken it inside.
    /// </remarks>
    private (TreeViewItem Item, string Dragged, SvgViewerElementNode Over, SvgElementDrop Where, string? Applied)? Landing(DragEventArgs e)
    {
        if (Carried(e) is not { Key: var dragged }
            || !_byAddress.TryGetValue(dragged, out var row)
            || (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(true) is not { DataContext: SvgViewerElementNode over } item
            // Everything under a row spells its address and a slash, and nothing lands in its own branch.
            || over.AddressKey == dragged
            || over.AddressKey.StartsWith(dragged + "/", StringComparison.Ordinal))
        {
            return null;
        }

        var applied = ClipRequested is null || Shelter(over.Element) is { }
            ? null
            : Applies(row) switch
            {
                [] => null,
                [var only] => only,
                _ => e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? "mask" : "clip-path"
            };

        applied = applied is { } property && Takes(over, property) ? property : null;

        var where = applied is { } && Shelter(row.Element) is { }
            ? SvgElementDrop.Inside
            : Bands(e.GetPosition(item).Y, RowHeight(item), Holds(over) || applied is { });

        if (where != SvgElementDrop.Inside)
        {
            applied = null;
        }

        return applied is null && MoveRequested is null ? null : (item, dragged, over, where, applied);
    }

    /// <summary>What a row could be clipped or masked with where it is dropped on one that is drawn.</summary>
    /// <remarks>
    /// A shape kept in &lt;defs&gt; could be either, and is the clip path unless ⌥ is held; a
    /// <c>&lt;use&gt;</c> there is only a mask where what it draws is no shape a clip path takes.
    /// </remarks>
    private static string[] Applies(SvgViewerElementNode row)
        => row.Label switch
        {
            "clipPath" => ["clip-path"],
            "mask" => ["mask"],
            var name when SvgElementEditor.Clips(name) && Shelter(row.Element) == "defs" => Outlined(row.Element) ? ["clip-path", "mask"] : ["mask"],
            _ => []
        };

    /// <summary>Whether what <paramref name="element"/> draws, through any <c>&lt;use&gt;</c> of a use, is a shape or text.</summary>
    private static bool Outlined(SvgElement element)
    {
        var seen = new HashSet<SvgElement>();

        while (element is SvgUse use && seen.Add(use)
               && use.TryGetEffectiveHrefString(out var href) && use.OwnerDocument?.GetElementById(href) is { } drawn)
        {
            element = drawn;
        }

        return element is not SvgUse && SvgElementEditor.Clips(SvgElementNames.NameOf(element));
    }

    /// <summary>What a drop that applies a row answers: a link, or a copy where the drag offers no link.</summary>
    /// <remarks>macOS narrows what a drag offers to a copy while ⌥ is held, and turns down every other answer.</remarks>
    internal static DragDropEffects Applying(DragEventArgs e)
        => (e.DragEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link : DragDropEffects.Copy;

    /// <summary>The nearest element above this one that keeps what it holds off the canvas, or null where it is drawn.</summary>
    private static string? Shelter(SvgElement element)
        => element.Parents.Select(SvgElementNames.NameOf).FirstOrDefault(SvgElementEditor.Keeps);

    /// <summary>
    /// Which band of a row the pointer is in, and so what a drop there means.
    /// </summary>
    /// <remarks>
    /// A row a drop can go into has three: a quarter at each end to go beside it, and the middle
    /// to go in it. One that cannot has two, so there is nowhere to aim that would mean nothing.
    /// </remarks>
    private static SvgElementDrop Bands(double y, double height, bool middle)
    {
        if (!middle)
        {
            return y < height / 2 ? SvgElementDrop.Before : SvgElementDrop.After;
        }

        return y < height * 0.25 ? SvgElementDrop.Before
            : y > height * 0.75 ? SvgElementDrop.After
            : SvgElementDrop.Inside;
    }

    /// <summary>Whether a drop can go inside this row.</summary>
    private static bool Holds(SvgViewerElementNode node)
        => node.Children.Count > 0 || string.Equals(node.Label, "g", StringComparison.Ordinal);

    /// <summary>
    /// How tall the row itself is, rather than the row and everything under it.
    /// </summary>
    /// <remarks>
    /// A TreeViewItem's bounds cover its whole branch, and taking those would put the quarter marks
    /// a subtree apart. So does its first visual child, a panel holding the header and the branch.
    /// </remarks>
    private static double RowHeight(TreeViewItem item)
        => item.GetTemplateDescendants().OfType<Control>().FirstOrDefault(part => part.Name == "PART_Header")?.Bounds.Height is { } own && own > 0d
            ? own
            : item.Bounds.Height;

    private void ShowDrop(TreeViewItem item, SvgElementDrop where, string? applied)
    {
        if (item.TranslatePoint(new Point(0, 0), _dropHost) is not { } at)
        {
            return;
        }

        var height = RowHeight(item);
        var inside = where == SvgElementDrop.Inside;
        var (_, word, colour) = s_applies.FirstOrDefault(apply => apply.Property == applied);
        var ink = Color.Parse(colour ?? "#4C9BE8");

        _dropLine.Width = Math.Max(item.Bounds.Width, 1);
        _dropLine.Height = inside ? height : 2d;
        _dropLine.BorderBrush = new SolidColorBrush(ink);
        _dropLine.Background = new SolidColorBrush(inside ? Color.FromArgb(0x33, ink.R, ink.G, ink.B) : ink);
        _dropLine.BorderThickness = new Thickness(inside ? 1d : 0d);

        // After an open group is after all of it.
        _dropLine.Margin = new Thickness(at.X, at.Y + (where == SvgElementDrop.After ? item.Bounds.Height - 2d : 0d), 0, 0);
        _dropWord.Text = word;
        _dropWord.Foreground = _dropLine.BorderBrush;
        _dropWord.IsVisible = word is { };
        _dropLine.IsVisible = true;
    }

    private void HideDrop() => _dropLine.IsVisible = false;

    public SvgViewerElementNode? SelectedNode => _tree.SelectedItem as SvgViewerElementNode;

    /// <summary>The document's root row, or null when nothing is open.</summary>
    public SvgViewerElementNode? Root => _root;

    /// <summary>
    /// Shows <paramref name="document"/>, keeping what was open and what was selected.
    /// </summary>
    /// <remarks>
    /// Both are kept by address rather than by element, because a drawing rebuilt from edited text
    /// shares no element with the one it replaced. A selection whose element has since been deleted
    /// is dropped; one the filter is merely hiding is remembered, and comes back with the box empty.
    /// </remarks>
    public void Show(SvgDocument? document)
    {
        _document = document;

        _byAddress.Clear();
        _matched.Clear();

        var filtering = _query.Length > 0;

        _root = document is null ? null : Build(document, string.Empty, filtering);

        _empty.Text = document is null ? "No drawing is open." : "Nothing here matches.";
        _empty.IsVisible = _root is null;

        // The root and its children, so a drawing opens showing what it is made of rather than one
        // closed row. Anything deeper is the reader's to open: a file of any size has more rows
        // than a pane this tall, and the editor's expand-everything scrolls the top off screen.
        if (!filtering && _root is { } root && _expanded.Count == 0)
        {
            _expanded.Add(root.AddressKey);

            foreach (var child in root.Children)
            {
                _expanded.Add(child.AddressKey);
            }
        }

        _restoring = true;

        try
        {
            _tree.ItemsSource = _root is null ? null : new[] { _root };

            var again = _selectedAddresses
                .Where(address => _byAddress.ContainsKey(address))
                .Select(address => _byAddress[address])
                .ToList();

            if (again.Count > 0)
            {
                Choose(again);
            }
            else
            {
                _tree.SelectedItems?.Clear();
                _tree.SelectedItem = null;
            }

            // A row the filter is hiding is still a selected row; one whose element has gone from
            // the document is not. Asked of the document rather than of the rows on screen, because
            // those two differ by exactly what is typed in the filter box — and gating the question
            // on that left a drawing's addresses alive after it was put away, to be restored onto
            // the next drawing of a group, which spells the same addresses for a different shape.
            _selectedAddresses.RemoveAll(
                address => document is null || SvgElementAddress.Parse(address)?.Resolve(document) is null);
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>What is typed in the filter box.</summary>
    public string Filter
    {
        get => _query;
        set => _filter.Text = value ?? string.Empty;
    }

    /// <summary>Every selected address, including any whose row the filter is hiding.</summary>
    /// <remarks>
    /// What a host should read to learn what is selected. <see cref="SelectedNodes"/> is what is on
    /// screen, which is a different question the moment anything is typed in the filter box — and
    /// answering the first with the second quietly drops elements from a selection because of it.
    /// </remarks>
    public IReadOnlyList<string> SelectedAddresses => _selectedAddresses;

    /// <summary>Every selected row, the first being the one <see cref="SelectedNode"/> answers.</summary>
    public IReadOnlyList<SvgViewerElementNode> SelectedNodes
        => _tree.SelectedItems is { } chosen
            ? chosen.OfType<SvgViewerElementNode>().ToList()
            : Array.Empty<SvgViewerElementNode>();

    /// <summary>Selects the row at <paramref name="addressKey"/>, opening everything above it.</summary>
    /// <returns>Whether there is a row there.</returns>
    public bool TrySelect(string? addressKey)
        => addressKey is { } one && TrySelect(new[] { one });

    /// <summary>Selects those rows and no others, opening everything above each of them.</summary>
    /// <remarks>
    /// The whole set is handed over in one assignment rather than a row at a time, so a selection
    /// of six is one thing for the tree to lay out and one event for whoever is listening.
    /// </remarks>
    /// <returns>Whether there is a row for every one of them.</returns>
    public bool TrySelect(IReadOnlyCollection<string> addressKeys) => Select(addressKeys, reveal: true);

    /// <inheritdoc cref="TrySelect(IReadOnlyCollection{string})"/>
    /// <param name="reveal">Whether to open what is above the rows and scroll to them, which a drag putting back what it found does not.</param>
    private bool Select(IReadOnlyCollection<string> addressKeys, bool reveal)
    {
        var rows = new List<SvgViewerElementNode>();

        foreach (var addressKey in addressKeys)
        {
            if (_byAddress.TryGetValue(addressKey, out var node))
            {
                if (reveal)
                {
                    Reveal(addressKey);
                }

                rows.Add(node);
            }
        }

        if (rows.Count == 0)
        {
            // Asking for nothing is asking for nothing to be selected, which is what a sweep that
            // caught nothing means. Asking for rows that have all gone leaves what was there.
            if (addressKeys.Count == 0)
            {
                _tree.SelectedItems?.Clear();
                _tree.SelectedItem = null;

                // Emptied, the control does not always say that it changed — and everything that
                // follows the selection is told by that one event, so the ring and the handles
                // would be left standing round a selection that is no longer there.
                if (_selectedAddresses.Count > 0)
                {
                    _selectedAddresses.Clear();

                    Selected?.Invoke(this, null);
                }
            }
            else if (!reveal && _document is { } document && addressKeys.First() is var anchor
                     && SvgElementAddress.Parse(anchor)?.Resolve(document) is { } element)
            {
                // A drag putting back what the filter hides, which has no row to hand the control:
                // the anchor is said without one, so the host turns back to it.
                Forget();
                _selectedAddresses.AddRange(addressKeys);

                Selected?.Invoke(this, new SvgViewerElementNode(
                    element, anchor, SvgElementNames.NameOf(element), null, Array.Empty<SvgViewerElementNode>(), _expanded));

                return true;
            }

            return addressKeys.Count == 0;
        }

        // The tree scrolls to a selected row by itself, and as it is handed it, which would take the
        // row being dragged out from under the pointer.
        var scrolls = _tree.AutoScrollToSelectedItem;

        _tree.AutoScrollToSelectedItem = reveal && scrolls;

        try
        {
            // The whole request and not only the rows that can be shown: a row the filter is hiding
            // is still a selected row, and a host reading back what it just asked for would
            // otherwise find its selection cut down to whatever is typed in a box.
            Choose(rows, addressKeys);
        }
        finally
        {
            _tree.AutoScrollToSelectedItem = scrolls;
        }

        // Posted, because a row inside a branch that was closed a line ago has no container to
        // scroll to until the tree has laid out again.
        if (reveal)
        {
            Dispatcher.UIThread.Post(() => _tree.ScrollIntoView(rows[0]), DispatcherPriority.Background);
        }

        return rows.Count == addressKeys.Count;
    }

    /// <summary>Puts those rows, and only those, in the tree's own selection.</summary>
    /// <remarks>
    /// The control says it changed once per row it is handed, so six rows told whoever is listening
    /// seven times — once of them with nothing selected at all, since the set is emptied before it
    /// is filled. Every follower re-read the selection, re-drew the ring and rebuilt the panel on
    /// each of those, which is the flicker, the pane that blinks empty, and six re-reads of a file
    /// nobody changed. It is said once here instead, deliberately, with the set complete.
    /// </remarks>
    private void Choose(IReadOnlyList<SvgViewerElementNode> rows, IReadOnlyCollection<string>? asked = null)
    {
        _choosing = true;

        try
        {
            if (rows.Count == 1)
            {
                // Through the single property, which is what a tree in single-selection mode has,
                // and what every host that never selects two sees.
                _tree.SelectedItem = rows[0];
            }
            else
            {
                _tree.SelectedItems?.Clear();
                _tree.SelectedItem = rows[0];

                if (_tree.SelectedItems is { } chosen)
                {
                    for (var i = 1; i < rows.Count; i++)
                    {
                        chosen.Add(rows[i]);
                    }
                }
            }
        }
        finally
        {
            _choosing = false;
        }

        // A rebuild putting back what was already selected is not somebody selecting it, which is
        // what the other guard has always meant.
        if (_restoring)
        {
            return;
        }

        _selectedAddresses.Clear();
        _selectedAddresses.AddRange(asked ?? rows.Select(row => row.AddressKey).ToList());

        Selected?.Invoke(this, rows[0]);
    }

    /// <summary>Lets go of what is selected, saying nothing: the rows belong to a drawing being left.</summary>
    /// <remarks>
    /// A host changing which drawing the pane is showing is the only thing that can know that the
    /// addresses it is holding are about to mean something else — a group builds one file several
    /// ways, so its drawings spell the same addresses for different shapes, and a selection carried
    /// across would land on rows nobody picked. Silent because the host is mid-change and about to
    /// say what it has selected instead.
    /// </remarks>
    public void Forget()
    {
        _selectedAddresses.Clear();

        _restoring = true;

        try
        {
            _tree.SelectedItems?.Clear();
            _tree.SelectedItem = null;
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>Opens every row above <paramref name="addressKey"/>.</summary>
    /// <remarks>
    /// The addresses above a row are the prefixes of its own — <c>0/2/1</c> is under <c>0/2</c>,
    /// which is under <c>0</c>, which is under the root's empty one — so this walks the path back
    /// down rather than searching for a parent.
    /// </remarks>
    private void Reveal(string addressKey)
    {
        if (_byAddress.TryGetValue(string.Empty, out var root))
        {
            root.IsExpanded = true;
        }

        for (var cut = addressKey.IndexOf('/'); cut >= 0; cut = addressKey.IndexOf('/', cut + 1))
        {
            if (_byAddress.TryGetValue(addressKey.Substring(0, cut), out var above))
            {
                above.IsExpanded = true;
            }
        }
    }

    /// <summary>What a clip path or a mask is called beside a row, and the colour a drop of one is drawn in: the canvas's outlines of their content.</summary>
    private static readonly (string Property, string Word, string Colour)[] s_applies = { ("clip-path", "clip", "#3FB950"), ("mask", "mask", "#9B6CFF") };

    /// <summary>
    /// One row and everything under it, or null where the filter keeps none of it.
    /// </summary>
    /// <remarks>
    /// A row survives if it matches or anything under it does, so filtering for <c>circle</c> leaves
    /// the groups it took to get there — a match with its ancestors cut off says where it is not.
    /// </remarks>
    private SvgViewerElementNode? Build(SvgElement element, string addressKey, bool filtering)
    {
        var children = new List<SvgViewerElementNode>(element.Children.Count);

        for (var index = 0; index < element.Children.Count; index++)
        {
            var childAddress = addressKey.Length == 0
                ? index.ToString(CultureInfo.InvariantCulture)
                : addressKey + "/" + index.ToString(CultureInfo.InvariantCulture);

            if (Build(element.Children[index], childAddress, filtering) is { } child)
            {
                children.Add(child);
            }
        }

        var label = SvgElementNames.NameOf(element);
        var id = string.IsNullOrEmpty(element.ID) ? null : "#" + element.ID;

        // The constant it is reported as, in the dim text beside it, where the filter already looks.
        if (element.CustomAttributes.TryGetValue(SvgExpressionAttributes.KeyFor(SvgExpressionAttributes.Bounds), out var box))
        {
            id = id is { } ? id + " ▭ " + box : "▭ " + box;
        }

        // And what clips or masks it, which nothing on the canvas says of the element itself.
        foreach (var (property, word, _) in s_applies)
        {
            if (SvgViewerOutline.Applied(element, property) is { } applied)
            {
                id = (id is { } ? id + " " : string.Empty) + word + " #" + applied.ID;
            }
        }

        if (filtering && children.Count == 0 && !Matches(label, id))
        {
            return null;
        }

        var node = new SvgViewerElementNode(
            element,
            addressKey,
            label,
            id,
            children,
            filtering ? _matched : _expanded);

        _byAddress[addressKey] = node;

        if (filtering)
        {
            _matched.Add(addressKey);
        }

        return node;
    }

    private bool Matches(string label, string? id)
        => label.Contains(_query, StringComparison.OrdinalIgnoreCase)
           || (id is { } written && written.Contains(_query, StringComparison.OrdinalIgnoreCase));
}
