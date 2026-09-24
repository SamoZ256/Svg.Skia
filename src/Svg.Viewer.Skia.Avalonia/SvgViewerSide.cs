// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// The strip down the right of a viewer: the host's own panes, the variables, the attributes of
/// whatever is picked, and the tree it is picked from.
/// </summary>
/// <remarks>
/// Regions stacked rather than tabs, because a variable is dragged onto an attribute and neither end
/// of that gesture can be behind the other. It is the argument the element tree was already given in
/// the markup this replaced — behind a tab, each hides the other — applied to the two panes it was
/// not applied to.
///
/// One class rather than a copy per host. A group's board in <c>Svg.Studio</c> is not a viewer and
/// built this column by hand, and the two had already drifted: a 220 tall tree against a 200 tall
/// one, and an opening tab chosen two different ways. Splitting the strip would have been the same
/// change made twice.
///
/// The host's panes keep a tab strip of their own, since a host may supply several and they are read
/// one at a time. A host that supplies none has no strip at all rather than an empty one.
/// </remarks>
public sealed class SvgViewerSide
{
    /// <summary>What the tree is worth when nobody has dragged its splitter.</summary>
    private const double TreeHeight = 200d;

    private static readonly IBrush Divider = new SolidColorBrush(Color.Parse("#20808080"));

    private readonly Grid _grid;
    private readonly Border _stripHost = new();
    private readonly GridSplitter _stripSplitter = Splitter();

    /// <summary>Kept so its line can go with the strip: topmost, it would double the toolbar's.</summary>
    private readonly Border _variablesHost;

    private readonly Border _treeHost;
    private readonly GridSplitter _treeSplitter = Splitter();

    /// <summary>What the tree's row was before it was hidden, so dragging it survives a round trip.</summary>
    private GridLength _treeHeight = new(TreeHeight, GridUnitType.Pixel);

    private IReadOnlyList<SvgViewerPane> _panes = Array.Empty<SvgViewerPane>();

    public SvgViewerSide(Control variables, Control element, Control tree)
    {
        if (variables is null)
        {
            throw new ArgumentNullException(nameof(variables));
        }

        if (element is null)
        {
            throw new ArgumentNullException(nameof(element));
        }

        if (tree is null)
        {
            throw new ArgumentNullException(nameof(tree));
        }

        _grid = new Grid { RowDefinitions = new RowDefinitions("*,6,*,6,*,6,200") };

        _variablesHost = Region(variables);
        _treeHost = Region(tree);

        Place(_stripHost, 0);
        Place(_stripSplitter, 1);
        Place(_variablesHost, 2);
        Place(Splitter(), 3);
        Place(Region(element), 4);
        Place(_treeSplitter, 5);
        Place(_treeHost, 6);

        // Nothing from a host yet, so no strip and no room taken for one.
        HideStrip();
    }

    /// <summary>The column itself, to be put wherever the host keeps it.</summary>
    public Control Root => _grid;

    /// <summary>Whether the tree at the foot of the column is shown.</summary>
    public bool ShowsTree
    {
        get => _treeHost.IsVisible;
        set
        {
            if (_treeHost.IsVisible == value)
            {
                return;
            }

            // The row carries the height: hiding the border alone would leave everything above it
            // paying for a strip of nothing.
            if (value)
            {
                _grid.RowDefinitions[6].Height = _treeHeight;
            }
            else
            {
                _treeHeight = _grid.RowDefinitions[6].Height;
                _grid.RowDefinitions[6].Height = new GridLength(0d);
            }

            _treeHost.IsVisible = value;
            _treeSplitter.IsVisible = value;
        }
    }

    /// <summary>The host's own panes, in the order they are read.</summary>
    /// <remarks>
    /// The same panes said again change nothing, and rebuilding the strip over somebody working in it
    /// is not nothing: a host that recomposes this whenever its own settings are saved took the
    /// reader back to the first tab every time.
    /// </remarks>
    public IReadOnlyList<SvgViewerPane> Panes
    {
        get => _panes;
        set
        {
            var panes = value ?? Array.Empty<SvgViewerPane>();

            if (Same(_panes, panes))
            {
                return;
            }

            _panes = panes;

            FillStrip();
        }
    }

    /// <summary>Which of the host's panes is being read, or null while it has none.</summary>
    private string? Showing
        => _stripHost.Child is TabControl strip && strip.SelectedItem is TabItem selected
            ? selected.Header as string
            : null;

    private static bool Same(IReadOnlyList<SvgViewerPane> panes, IReadOnlyList<SvgViewerPane> others)
        => panes.Count == others.Count
           && panes.Zip(others).All(
               pair => ReferenceEquals(pair.First.Content, pair.Second.Content)
                       && string.Equals(pair.First.Header, pair.Second.Header, StringComparison.Ordinal));

    private void FillStrip()
    {
        // What was being looked at, so a strip that gains or loses a pane does not also change the
        // subject. Only by name: the pane it was is not always one of the panes it now is.
        var looking = Showing;

        // Emptied first, and the tabs with it: a control cannot be added to a second parent, and a
        // pane the host handed in twice is moving between them.
        if (_stripHost.Child is TabControl open)
        {
            foreach (var item in open.Items.OfType<TabItem>())
            {
                item.Content = null;
            }
        }

        _stripHost.Child = null;

        if (_panes.Count == 0)
        {
            HideStrip();

            return;
        }

        var tabs = new TabControl { Classes = { "panes" }, Padding = new Thickness(0) };

        foreach (var pane in _panes)
        {
            tabs.Items.Add(new TabItem { Header = pane.Header, Content = pane.Content });
        }

        if (looking is { }
            && tabs.Items.OfType<TabItem>().FirstOrDefault(item => Equals(item.Header, looking)) is { } again)
        {
            tabs.SelectedItem = again;
        }

        _stripHost.Child = tabs;

        _grid.RowDefinitions[0].Height = new GridLength(1d, GridUnitType.Star);
        _stripHost.IsVisible = true;
        _stripSplitter.IsVisible = true;
        _variablesHost.BorderThickness = new Thickness(0, 1, 0, 0);
    }

    private void HideStrip()
    {
        _grid.RowDefinitions[0].Height = new GridLength(0d);
        _stripHost.IsVisible = false;
        _stripSplitter.IsVisible = false;
        _variablesHost.BorderThickness = new Thickness(0);
    }

    private void Place(Control control, int row)
    {
        Grid.SetRow(control, row);
        _grid.Children.Add(control);
    }

    /// <summary>One region, under the line that separates it from the one above.</summary>
    private static Border Region(Control content)
        => new()
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = Divider,
            Child = content
        };

    private static GridSplitter Splitter()
        => new() { ResizeDirection = GridResizeDirection.Rows, Background = Brushes.Transparent };
}
