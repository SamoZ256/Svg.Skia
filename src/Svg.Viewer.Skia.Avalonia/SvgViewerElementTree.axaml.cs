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
    private readonly TextBlock _empty;
    private readonly TextBox _filter;

    /// <summary>Which rows are open, by address, so a rebuild does not fold the tree up.</summary>
    /// <summary>How far the pointer travels before a press becomes a drag.</summary>
    private const double DragThreshold = 4d;

    /// <summary>What a dragged row carries. Nothing reads it; a drag needs some format to be.</summary>
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
    private Func<string, SvgElementDrop, bool>? _newGroupRequested;
    private SvgViewerElementNode? _row;
    private PointerPressedEventArgs? _rowPressed;
    private Point _rowPressedAt;
    private SvgViewerElementNode? _dropOn;
    private SvgElementDrop _dropWhere;
    private string? _selectedAddress;
    private bool _restoring;

    public SvgViewerElementTree()
    {
        AvaloniaXamlLoader.Load(this);

        _tree = this.FindControl<TreeView>("Tree")!;
        _dropHost = this.FindControl<Grid>("DropHost")!;
        _dropLine = this.FindControl<Border>("DropLine")!;

        _tree.AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Tunnel);
        _tree.PointerMoved += OnRowMoved;
        _tree.AddHandler(DragDrop.DragOverEvent, OnRowDragOver);
        _tree.AddHandler(DragDrop.DropEvent, OnRowDrop);

        DragDrop.SetAllowDrop(_tree, true);
        _empty = this.FindControl<TextBlock>("EmptyLabel")!;
        _filter = this.FindControl<TextBox>("FilterBox")!;

        _filter.TextChanged += (_, _) =>
        {
            _query = _filter.Text ?? string.Empty;

            Show(_document);
        };

        _tree.SelectionChanged += (_, _) =>
        {
            if (_restoring)
            {
                return;
            }

            _selectedAddress = SelectedNode?.AddressKey;

            Selected?.Invoke(this, SelectedNode);
        };
    }

    /// <summary>Raised when a row is selected, or with null when the selection is dropped.</summary>
    public event EventHandler<SvgViewerElementNode?>? Selected;

    /// <summary>
    /// Moves a row to where it was dropped, for a host that has somewhere to write it.
    /// </summary>
    /// <remarks>
    /// Wired rather than built in, and the rows are draggable only where it is: this control is also
    /// the tree of a project group's tab, which shows a drawing it has no text to edit.
    /// </remarks>
    public Func<string, string, SvgElementDrop, bool>? MoveRequested { get; set; }

    /// <summary>Writes an empty group where a row was picked, for the same kind of host.</summary>
    public Func<string, SvgElementDrop, bool>? NewGroupRequested
    {
        get => _newGroupRequested;
        set
        {
            _newGroupRequested = value;

            _tree.ContextMenu = value is null ? null : Menu();
        }
    }

    private ContextMenu Menu()
    {
        var group = new MenuItem { Header = "New group" };

        group.Click += (_, _) =>
        {
            if (_newGroupRequested is { } write && SelectedNode is { } row)
            {
                write(row.AddressKey, SvgElementDrop.After);
            }
        };

        var menu = new ContextMenu { ItemsSource = new[] { group } };

        menu.Opening += (_, _) => group.IsEnabled = SelectedNode is { };

        return menu;
    }

    // ---- dragging a row ---------------------------------------------------------------------

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        _row = null;
        _rowPressed = null;

        if (MoveRequested is null
            || e.Source is not Visual source
            // The chevron folds the row; it does not pick it up.
            || source.FindAncestorOfType<ToggleButton>(true) is { }
            || source.FindAncestorOfType<TreeViewItem>(true)?.DataContext is not SvgViewerElementNode node
            // The drawing itself is the file. There is nowhere to put it.
            || node.AddressKey.Length == 0
            || !e.GetCurrentPoint(_tree).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _row = node;
        _rowPressed = e;
        _rowPressedAt = e.GetPosition(_tree);
    }

    private async void OnRowMoved(object? sender, PointerEventArgs e)
    {
        if (_row is null || _rowPressed is not { } pressed)
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

        var data = new DataTransfer();

        data.Add(DataTransferItem.Create(RowFormat, string.Empty));

        _rowPressed = null;

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
            || (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(true) is not { DataContext: SvgViewerElementNode over })
        {
            HideDrop();

            return;
        }

        e.DragEffects = DragDropEffects.Move;

        // Taken, or the viewer's own handler answers for it: that one is about files dropped on the
        // drawing and turns away a drag carrying none — which is every drag of a row, so not one of
        // them could be started at all.
        e.Handled = true;

        // A row cannot land in its own branch, and the addresses say so: everything under a row
        // spells its address and then some.
        if (over.AddressKey.StartsWith(dragged.AddressKey, StringComparison.Ordinal))
        {
            HideDrop();

            return;
        }

        var item = (e.Source as Visual)!.FindAncestorOfType<TreeViewItem>(true)!;

        _dropOn = over;
        _dropWhere = Bands(e.GetPosition(item).Y, RowHeight(item), over);

        ShowDrop(item);
    }

    private void OnRowDrop(object? sender, DragEventArgs e)
    {
        var target = _dropOn;
        var where = _dropWhere;
        var dragged = _row;

        // Before anything else: the landing is what the pointer said last, and HideDrop forgets it.
        HideDrop();

        if (dragged is { } && target is { } && MoveRequested is { } move)
        {
            e.Handled = true;

            move(dragged.AddressKey, target.AddressKey, where);
        }
    }

    /// <summary>
    /// Which band of a row the pointer is in, and so what a drop there means.
    /// </summary>
    /// <remarks>
    /// A row that can hold children has three: a quarter at each end to go beside it, and the middle
    /// to go in it. One that cannot has two, so there is nowhere to aim that would mean nothing.
    /// </remarks>
    private static SvgElementDrop Bands(double y, double height, SvgViewerElementNode target)
    {
        if (!Holds(target))
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
    /// a subtree apart.
    /// </remarks>
    private static double RowHeight(TreeViewItem item)
        => item.GetVisualChildren().FirstOrDefault()?.Bounds.Height is { } own && own > 0d
            ? own
            : item.Bounds.Height;

    private void ShowDrop(TreeViewItem item)
    {
        if (item.TranslatePoint(new Point(0, 0), _dropHost) is not { } at)
        {
            return;
        }

        var height = RowHeight(item);
        var inside = _dropWhere == SvgElementDrop.Inside;

        _dropLine.Width = Math.Max(item.Bounds.Width, 1);
        _dropLine.Height = inside ? height : 2d;
        _dropLine.Background = new SolidColorBrush(Color.Parse(inside ? "#334C9BE8" : "#4C9BE8"));
        _dropLine.BorderThickness = new Thickness(inside ? 1d : 0d);
        _dropLine.Margin = new Thickness(at.X, at.Y + (_dropWhere == SvgElementDrop.After ? height - 2d : 0d), 0, 0);
        _dropLine.IsVisible = true;
    }

    private void HideDrop()
    {
        _dropLine.IsVisible = false;
        _dropOn = null;
    }

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

            if (_selectedAddress is { } address && _byAddress.TryGetValue(address, out var again))
            {
                _tree.SelectedItem = again;
            }
            else
            {
                _tree.SelectedItem = null;

                // A row the filter is hiding is still the selected row; only one that has gone from
                // the document stops being it.
                if (!filtering)
                {
                    _selectedAddress = null;
                }
            }
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

    /// <summary>Selects the row at <paramref name="addressKey"/>, opening everything above it.</summary>
    /// <returns>Whether there is a row there.</returns>
    public bool TrySelect(string? addressKey)
    {
        if (addressKey is null || !_byAddress.TryGetValue(addressKey, out var node))
        {
            return false;
        }

        Reveal(addressKey);

        _tree.SelectedItem = node;

        // Posted, because a row inside a branch that was closed a line ago has no container to
        // scroll to until the tree has laid out again.
        Dispatcher.UIThread.Post(() => _tree.ScrollIntoView(node), DispatcherPriority.Background);

        return true;
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
