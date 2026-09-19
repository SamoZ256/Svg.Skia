// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using Svg.Skia;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>Lays several drawings out on one canvas, in a grid that nearly squares.</summary>
/// <remarks>
/// Beside <see cref="SvgViewerPlacement"/> because it is the one arrangement more than one host
/// wants: a project group showing what it builds, and a recipe showing the drawings it paints.
/// Both had the same grid and only one of them had the code for it.
///
/// Nothing here decides what a drawing is called or which drawings there are. It is handed what to
/// place and answers where each one goes, so a caller with an order of its own keeps it.
/// </remarks>
public static class SvgViewerSpread
{
    /// <summary>One drawing to place: the picture and how big it is.</summary>
    public readonly record struct Item(SKSvg Svg, SKSize Size);

    /// <summary>
    /// Places <paramref name="items"/> in reading order, left to right and top to bottom.
    /// </summary>
    /// <remarks>
    /// The result lines up index for index with what was handed in, which is what lets a caller pair
    /// a placement back with the drawing it came from — neither half knows the other otherwise.
    ///
    /// A row's drawings stand on one floor however differently sized they are, because a row of
    /// pictures stepping up and down reads as noise.
    /// </remarks>
    public static IReadOnlyList<SvgViewerPlacement> Of(IReadOnlyList<Item> items)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        if (items.Count == 0)
        {
            return Array.Empty<SvgViewerPlacement>();
        }

        var columns = (int)Math.Ceiling(Math.Sqrt(items.Count));
        var rows = (int)Math.Ceiling(items.Count / (double)columns);

        // The unit every gap on a board is counted in, taken from the largest drawing so a spread
        // of icons and a spread of pages are spaced alike in proportion to what they hold.
        var largest = items.Max(one => Math.Max(one.Size.Width, one.Size.Height));
        var gap = Math.Max(largest * 0.05f, 1f) * 2f;

        var widths = new float[columns];
        var heights = new float[rows];

        for (var index = 0; index < items.Count; index++)
        {
            widths[index % columns] = Math.Max(widths[index % columns], items[index].Size.Width);
            heights[index / columns] = Math.Max(heights[index / columns], items[index].Size.Height);
        }

        var placed = new List<SvgViewerPlacement>(items.Count);
        var y = 0f;

        for (var row = 0; row < rows; row++)
        {
            var x = 0f;

            for (var column = 0; column < columns; column++)
            {
                var index = row * columns + column;

                if (index < items.Count)
                {
                    // Centred across its column and standing on the row's floor.
                    placed.Add(new SvgViewerPlacement(
                        items[index].Svg,
                        new SKPoint(
                            x + (widths[column] - items[index].Size.Width) / 2f,
                            y + heights[row] - items[index].Size.Height)));
                }

                x += widths[column] + gap;
            }

            y += heights[row] + gap;
        }

        return placed;
    }
}
