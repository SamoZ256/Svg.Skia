// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// Keeps the rows a tree is scrolled inside of at its top, one under another, the way an editor
/// pins the scopes it is scrolled into.
/// </summary>
/// <remarks>
/// The pinned rows are the rows themselves, drawn further down, rather than copies: a render
/// transform is hit-tested, so a pinned row is clicked, chosen, opened, folded and dragged by
/// everything that already does so to a row. Only a press on one adds anything, which is to scroll
/// the row to where it is drawn — so it is the same row under the pointer either way.
/// </remarks>
public static class SvgViewerStickyRows
{
    /// <summary>Pins the rows <paramref name="tree"/> is scrolled inside of, for as long as it lives.</summary>
    /// <remarks>Before any pointer handler of the tree's own, so the press it scrolls reaches those afterwards.</remarks>
    public static void Attach(TreeView tree)
    {
        if (tree is null)
        {
            throw new ArgumentNullException(nameof(tree));
        }

        _ = new Pins(tree);
    }

    private sealed class Pins
    {
        private readonly TreeView _tree;
        private List<(TreeViewItem Item, Border Row, double By)> _pinned = new();

        public Pins(TreeView tree)
        {
            _tree = tree;

            // Every layout pass: a scroll arranges the rows again, and so do a branch opened or
            // folded and the rows being built afresh, which is everything that moves a row.
            tree.LayoutUpdated += (_, _) => Pin();
            tree.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
            tree.AddHandler(Control.RequestBringIntoViewEvent, OnBroughtIntoView, handledEventsToo: true);
        }

        private ScrollViewer? Viewer() => _tree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

        /// <summary>The row of <paramref name="item"/> apart from the rows under it.</summary>
        private static Border? Row(TreeViewItem item)
            => item.GetVisualChildren().FirstOrDefault()?.GetVisualChildren().OfType<Border>()
                .FirstOrDefault(border => border.Name == "PART_LayoutRoot");

        private void Pin()
        {
            var pinned = new List<(TreeViewItem Item, Border Row, double By)>();

            if (Viewer() is { } viewer)
            {
                var slot = 0d;
                IEnumerable<Control> level = _tree.GetRealizedContainers();

                while (Next(level, viewer, slot) is { } next)
                {
                    pinned.Add(next);
                    slot += next.Row.Bounds.Height;
                    level = next.Item.GetRealizedContainers();
                }
            }

            foreach (var (item, row, _) in _pinned)
            {
                if (!pinned.Exists(one => ReferenceEquals(one.Item, item)))
                {
                    item.PropertyChanged -= OnStateChanged;
                    row.ClearValue(Visual.RenderTransformProperty);
                    row.ClearValue(Visual.ZIndexProperty);
                    row.ClearValue(Border.BackgroundProperty);
                }
            }

            foreach (var (item, row, by) in pinned)
            {
                if (!_pinned.Exists(one => ReferenceEquals(one.Item, item)))
                {
                    item.PropertyChanged += OnStateChanged;
                }

                if (row.RenderTransform is not TranslateTransform { X: 0d } moved || moved.Y != by)
                {
                    row.RenderTransform = new TranslateTransform(0d, by);
                }

                // Over the rows under it, which are its own: they are in the presenter beside it.
                row.ZIndex = 1;

                Paint(item, row);
            }

            _pinned = pinned;
        }

        /// <summary>
        /// The row among <paramref name="level"/> scrolled past <paramref name="slot"/> with rows of
        /// its own still below it, and how far down it is drawn — pushed up as its last rows leave.
        /// </summary>
        private static (TreeViewItem Item, Border Row, double By)? Next(IEnumerable<Control> level, ScrollViewer viewer, double slot)
        {
            foreach (var item in level.OfType<TreeViewItem>())
            {
                if (!item.IsVisible || !item.IsExpanded || item.ItemCount == 0
                    || Row(item) is not { } row
                    || item.TranslatePoint(default, viewer) is not { } at)
                {
                    continue;
                }

                var height = row.Bounds.Height;
                var bottom = at.Y + item.Bounds.Height;

                // Half a pixel short, so the row a press has just scrolled into place stays let go of.
                if (at.Y < slot - 0.5d && bottom > slot)
                {
                    return (item, row, Math.Min(slot, bottom - height) - at.Y);
                }
            }

            return null;
        }

        /// <summary>Scrolls a pinned row to where it is drawn, so the press lands on it in its own place.</summary>
        private void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is not Visual source
                || Viewer() is not { } viewer
                || _pinned.FirstOrDefault(one => one.Row == source || one.Row.IsVisualAncestorOf(source)) is not { Row: { } } pressed)
            {
                return;
            }

            viewer.Offset = viewer.Offset.WithY(viewer.Offset.Y - pressed.By);
            _tree.UpdateLayout();
        }

        /// <summary>
        /// Scrolls a row brought into view out from under the rows pinned above it, after the
        /// viewer has put it at the top.
        /// </summary>
        private void OnBroughtIntoView(object? sender, RequestBringIntoViewEventArgs e)
        {
            if ((e.TargetObject as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true) is not { } item
                || Viewer() is not { } viewer
                || _tree.ItemsPanelRoot is not { } panel
                || item.TranslatePoint(default, panel) is not { } at)
            {
                return;
            }

            // Measured against the rows rather than the viewer, which has not arranged the new
            // offset yet: what is above the row is the same wherever it is scrolled to.
            var above = item.GetVisualAncestors().OfType<TreeViewItem>().Sum(owner => Row(owner)?.Bounds.Height ?? 0d);
            var highest = Math.Max(0d, at.Y - above);

            if (viewer.Offset.Y > highest)
            {
                viewer.Offset = viewer.Offset.WithY(highest);
            }
        }

        private void OnStateChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if ((e.Property == TreeViewItem.IsSelectedProperty || e.Property == InputElement.IsPointerOverProperty)
                && sender is TreeViewItem item
                && Row(item) is { } row)
            {
                Paint(item, row);
            }
        }

        /// <summary>
        /// Fills a pinned row, which would otherwise show the rows scrolled under it through its own.
        /// </summary>
        /// <remarks>
        /// The theme's brush for the row's state laid over what the pane is painted with, so it reads as
        /// the row it is. Set on the row and not on the header inside it, which starts at the indent.
        /// </remarks>
        private void Paint(TreeViewItem item, Border row)
        {
            var under = Behind(_tree);
            var key = (item.IsSelected, item.IsPointerOver) switch
            {
                (true, true) => "TreeViewItemBackgroundSelectedPointerOver",
                (true, false) => "TreeViewItemBackgroundSelected",
                (false, true) => "TreeViewItemBackgroundPointerOver",
                _ => null
            };

            var over = key is { } && item.TryFindResource(key, item.ActualThemeVariant, out var found) && found is ISolidColorBrush brush
                ? brush
                : null;

            var fill = over is { } top ? Blend(top.Color, top.Opacity, under) : under;

            // Asked on every layout pass, and a new brush each time would draw the tree again for nothing.
            if (row.Background is not ISolidColorBrush { Color: var was } || was != fill)
            {
                row.Background = new SolidColorBrush(fill);
            }
        }

        /// <summary>The first solid colour behind <paramref name="tree"/>, which is what the pane shows through it.</summary>
        private static Color Behind(Visual tree)
        {
            foreach (var visual in tree.GetSelfAndVisualAncestors())
            {
                var background = visual switch
                {
                    TemplatedControl control => control.Background,
                    Panel panel => panel.Background,
                    Border border => border.Background,
                    _ => null
                };

                if (background is ISolidColorBrush { Color.A: 255 } solid && solid.Opacity >= 1d)
                {
                    return solid.Color;
                }
            }

            return tree is StyledElement { ActualThemeVariant: var variant } && variant == global::Avalonia.Styling.ThemeVariant.Dark
                ? Colors.Black
                : Colors.White;
        }

        private static Color Blend(Color top, double opacity, Color under)
        {
            var alpha = top.A / 255d * opacity;

            byte Mix(byte over, byte below) => (byte)Math.Round(over * alpha + below * (1d - alpha));

            return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
        }
    }
}
