// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Svg;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// One element of the open drawing, as a row of the tree.
/// </summary>
/// <remarks>
/// Addressed rather than referenced. Rebuilding a drawing from edited text produces a new
/// <see cref="SvgDocument"/> and new elements every keystroke, so <see cref="Element"/> is stale a
/// moment after it is read and <see cref="AddressKey"/> is what the pane remembers: what was
/// selected, and what was expanded, in whatever document is current.
/// </remarks>
public sealed class SvgViewerElementNode : INotifyPropertyChanged
{
    private readonly HashSet<string> _expanded;
    private readonly HashSet<string> _selected;

    internal SvgViewerElementNode(
        SvgElement element,
        string addressKey,
        string label,
        string? id,
        IReadOnlyList<SvgViewerElementNode> children,
        HashSet<string> expanded,
        HashSet<string> selected)
    {
        Element = element;
        AddressKey = addressKey;
        Label = label;
        Id = id;
        Children = children;
        _expanded = expanded;
        _selected = selected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SvgElement Element { get; }

    /// <inheritdoc cref="SvgElementAddress.Key"/>
    public string AddressKey { get; }

    /// <summary>What the element is called: <c>g</c>, <c>rect</c>, <c>e:code</c>'s own name.</summary>
    public string Label { get; }

    /// <summary>The element's id with a hash in front, or null where it has none.</summary>
    public string? Id { get; }

    public bool HasId => Id is { };

    public IReadOnlyList<SvgViewerElementNode> Children { get; }

    /// <summary>
    /// Whether the row is open, held by address so a rebuild does not fold the tree up.
    /// </summary>
    /// <remarks>
    /// The set is the pane's and is shared rather than copied: the rows are thrown away and made
    /// again on every keystroke, and a row that reported its own state would have nowhere to report
    /// it to by the time anything listened.
    /// </remarks>
    public bool IsExpanded
    {
        get => _expanded.Contains(AddressKey);
        set
        {
            if (value == _expanded.Contains(AddressKey))
            {
                return;
            }

            if (value)
            {
                _expanded.Add(AddressKey);
            }
            else
            {
                _expanded.Remove(AddressKey);
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    /// <summary>Whether the row is picked, held by address for the reason the open state is.</summary>
    public bool IsSelected
    {
        get => _selected.Contains(AddressKey);
        set
        {
            if (value == _selected.Contains(AddressKey))
            {
                return;
            }

            if (value)
            {
                _selected.Add(AddressKey);
            }
            else
            {
                _selected.Remove(AddressKey);
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    /// <summary>This row and every row under it, in document order.</summary>
    public IEnumerable<SvgViewerElementNode> Flatten()
    {
        yield return this;

        foreach (var child in Children)
        {
            foreach (var node in child.Flatten())
            {
                yield return node;
            }
        }
    }

    public override string ToString() => Id is { } id ? $"{Label} {id}" : Label;
}
