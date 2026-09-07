// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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

    /// <summary>Which rows are open, by address, so a rebuild does not fold the tree up.</summary>
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    private readonly Dictionary<string, SvgViewerElementNode> _byAddress = new(StringComparer.Ordinal);

    private SvgViewerElementNode? _root;
    private string? _selectedAddress;
    private bool _restoring;

    public SvgViewerElementTree()
    {
        AvaloniaXamlLoader.Load(this);

        _tree = this.FindControl<TreeView>("Tree")!;
        _empty = this.FindControl<TextBlock>("EmptyLabel")!;

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

    public SvgViewerElementNode? SelectedNode => _tree.SelectedItem as SvgViewerElementNode;

    /// <summary>The document's root row, or null when nothing is open.</summary>
    public SvgViewerElementNode? Root => _root;

    /// <summary>
    /// Shows <paramref name="document"/>, keeping what was open and what was selected.
    /// </summary>
    /// <remarks>
    /// Both are kept by address rather than by element, because a drawing rebuilt from edited text
    /// shares no element with the one it replaced. A selection whose element has since been deleted
    /// is dropped, which is the only honest answer.
    /// </remarks>
    public void Show(SvgDocument? document)
    {
        _byAddress.Clear();
        _root = document is null ? null : Build(document, string.Empty);

        _empty.IsVisible = _root is null;

        // The root and its children, so a drawing opens showing what it is made of rather than one
        // closed row. Anything deeper is the reader's to open: a file of any size has more rows
        // than a pane this tall, and the editor's expand-everything scrolls the top off screen.
        if (_root is { } root && _expanded.Count == 0)
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
            _tree.SelectedItem = _selectedAddress is { } address && _byAddress.TryGetValue(address, out var again)
                ? again
                : null;

            _selectedAddress = SelectedNode?.AddressKey;
        }
        finally
        {
            _restoring = false;
        }
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

    private SvgViewerElementNode Build(SvgElement element, string addressKey)
    {
        var children = new List<SvgViewerElementNode>(element.Children.Count);

        for (var index = 0; index < element.Children.Count; index++)
        {
            children.Add(Build(
                element.Children[index],
                addressKey.Length == 0
                    ? index.ToString(CultureInfo.InvariantCulture)
                    : addressKey + "/" + index.ToString(CultureInfo.InvariantCulture)));
        }

        var node = new SvgViewerElementNode(
            element,
            addressKey,
            SvgElementNames.NameOf(element),
            string.IsNullOrEmpty(element.ID) ? null : "#" + element.ID,
            children,
            _expanded);

        // Last, so a duplicate address cannot exist: the key is the path, and a path names one row.
        _byAddress[addressKey] = node;

        return node;
    }
}
