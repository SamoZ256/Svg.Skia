// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Svg;

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
    private readonly TextBlock _empty;
    private readonly TextBox _filter;

    /// <summary>Which rows are open, by address, so a rebuild does not fold the tree up.</summary>
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

    /// <summary>What is picked, by address, for the reason <c>_expanded</c> is.</summary>
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);

    private readonly Dictionary<string, SvgViewerElementNode> _byAddress = new(StringComparer.Ordinal);

    private SvgDocument? _document;
    private string _query = string.Empty;
    private SvgViewerElementNode? _root;
    private Func<IReadOnlyList<SvgViewerElementNode>, bool>? _wrapRequested;
    private string? _announced;

    public SvgViewerElementTree()
    {
        AvaloniaXamlLoader.Load(this);

        _tree = this.FindControl<TreeView>("Tree")!;
        _empty = this.FindControl<TextBlock>("EmptyLabel")!;
        _filter = this.FindControl<TextBox>("FilterBox")!;

        _filter.TextChanged += (_, _) =>
        {
            _query = _filter.Text ?? string.Empty;

            Show(_document);
        };

        _tree.SelectionChanged += (_, _) => Announce();
    }

    /// <summary>Raised when a row is selected, or with null when the selection is dropped.</summary>
    public event EventHandler<SvgViewerElementNode?>? Selected;

    /// <summary>
    /// Puts the picked rows in a group, for a host that has somewhere to write one.
    /// </summary>
    /// <remarks>
    /// Wired rather than built in, and the menu appears only where it is: this control is also the
    /// tree of a project group's tab, which shows a drawing it has no text to edit.
    /// </remarks>
    public Func<IReadOnlyList<SvgViewerElementNode>, bool>? WrapRequested
    {
        get => _wrapRequested;
        set
        {
            _wrapRequested = value;

            _tree.ContextMenu = value is null ? null : Menu();
        }
    }

    private ContextMenu Menu()
    {
        var wrap = new MenuItem
        {
            Header = "Group into <g>",
            InputGesture = new KeyGesture(Key.G, Command)
        };

        wrap.Click += (_, _) => Wrap();

        var menu = new ContextMenu { ItemsSource = new[] { wrap } };

        menu.Opening += (_, _) => wrap.IsEnabled = SelectedNodes.Count > 1;

        return menu;
    }

    /// <summary>What this platform spells a command with — Ctrl here, Cmd on a Mac.</summary>
    /// <remarks>
    /// Asked of the platform rather than of the operating system, because the headless one used by
    /// the tests names Control on every machine, and a gesture spelled from OperatingSystem could
    /// not be pressed in a test.
    /// </remarks>
    private KeyModifiers Command
        => this.GetPlatformSettings()?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    /// <summary>Puts the picked rows in a group, where the host has said how.</summary>
    /// <returns>Whether anything was written.</returns>
    public bool Wrap()
    {
        var picked = SelectedNodes;

        return _wrapRequested is { } wrap && picked.Count > 1 && wrap(picked);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.G && e.KeyModifiers == Command && _wrapRequested is { })
        {
            _ = Wrap();

            e.Handled = true;

            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// Tells anyone listening what is picked, where that is not what they were told last.
    /// </summary>
    /// <remarks>
    /// Compared rather than guarded by a flag. The rows carry their own state back onto the
    /// containers a rebuild makes, and that happens during layout rather than inside the rebuild —
    /// so a guard around the rebuild misses it, and the pane would be told a selection was picked
    /// on every keystroke and scroll the text out from under whoever was typing.
    /// </remarks>
    private void Announce()
    {
        var picked = SelectedNodes;
        // Counted as well as named, because the root's address is the empty string and would
        // otherwise read as nothing being picked at all.
        var announcing = picked.Count + ":" + string.Join("|", picked.Select(node => node.AddressKey));

        if (string.Equals(announcing, _announced, StringComparison.Ordinal))
        {
            return;
        }

        _announced = announcing;

        Selected?.Invoke(this, picked.Count > 0 ? picked[0] : null);
    }

    /// <summary>Every picked row, in document order.</summary>
    /// <remarks>
    /// Read off the tree rather than off the control's own selection, which answers only for rows
    /// whose container has been realised — and this tree is thrown away and built again on every
    /// keystroke, so a row scrolled out of sight would drop out of a grouping.
    /// </remarks>
    public IReadOnlyList<SvgViewerElementNode> SelectedNodes
        => _root is null
            ? Array.Empty<SvgViewerElementNode>()
            : _root.Flatten().Where(node => node.IsSelected).ToList();

    public SvgViewerElementNode? SelectedNode => SelectedNodes.Count > 0 ? SelectedNodes[0] : null;

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

        // A row the filter is hiding is still picked; only one that has gone from the document stops
        // being. The rows themselves carry the state back onto whatever containers the rebuilt tree
        // makes, exactly as the open branches do.
        if (!filtering)
        {
            _selected.IntersectWith(_byAddress.Keys);
        }

        _tree.ItemsSource = _root is null ? null : new[] { _root };

        Announce();
    }

    /// <summary>What is typed in the filter box.</summary>
    public string Filter
    {
        get => _query;
        set => _filter.Text = value ?? string.Empty;
    }

    /// <summary>Selects the rows at <paramref name="addressKeys"/>, opening everything above them.</summary>
    /// <returns>Whether every one of them is a row.</returns>
    public bool TrySelect(IReadOnlyList<string> addressKeys)
    {
        if (addressKeys is null)
        {
            throw new ArgumentNullException(nameof(addressKeys));
        }

        var every = true;

        foreach (var addressKey in addressKeys)
        {
            every &= TrySelect(addressKey, keep: true);
        }

        return every;
    }

    /// <summary>Selects the row at <paramref name="addressKey"/>, opening everything above it.</summary>
    /// <returns>Whether there is a row there.</returns>
    public bool TrySelect(string? addressKey) => TrySelect(addressKey, keep: false);

    private bool TrySelect(string? addressKey, bool keep)
    {
        if (addressKey is null || !_byAddress.TryGetValue(addressKey, out var node))
        {
            return false;
        }

        if (!keep)
        {
            _selected.Clear();

            foreach (var row in _byAddress.Values)
            {
                row.IsSelected = row.AddressKey == addressKey;
            }
        }

        Reveal(addressKey);

        node.IsSelected = true;

        Announce();

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
            filtering ? _matched : _expanded,
            _selected);

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
