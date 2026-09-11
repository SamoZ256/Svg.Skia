// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using Svg.Skia;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>Lays several drawings out on one canvas, captioned, in a grid that nearly squares.</summary>
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
    /// <summary>One drawing to place: the picture, how big it is, and what to write under it.</summary>
    public readonly record struct Item(SKSvg Svg, SKSize Size, string Caption);

    /// <summary>
    /// Places <paramref name="items"/> in reading order, left to right and top to bottom.
    /// </summary>
    /// <remarks>
    /// The result lines up index for index with what was handed in, which is what lets a caller pair
    /// a placement back with the drawing it came from — neither half knows the other otherwise.
    ///
    /// Sized in drawing units and not in pixels, so the caption keeps its proportion to the ink at
    /// every zoom. A row's captions sit on one line however differently sized the drawings above
    /// them are, because a row of labels stepping up and down reads as noise.
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

        var largest = items.Max(one => Math.Max(one.Size.Width, one.Size.Height));
        var label = Math.Max(largest * 0.05f, 1f);
        var gap = label * 2f;

        using var font = new SKFont(SKTypeface.Default, label);

        var widths = new float[columns];
        var heights = new float[rows];

        for (var index = 0; index < items.Count; index++)
        {
            var wanted = Math.Max(items[index].Size.Width, Widest(font, items[index].Caption));

            widths[index % columns] = Math.Max(widths[index % columns], wanted);
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
                    // Centred across its column and standing on the row's floor, so the captions of
                    // a row line up however differently sized the drawings above them are.
                    placed.Add(new SvgViewerPlacement(
                        items[index].Svg,
                        new SKPoint(
                            x + (widths[column] - items[index].Size.Width) / 2f,
                            y + heights[row] - items[index].Size.Height),
                        items[index].Caption,
                        label));
                }

                x += widths[column] + gap;
            }

            // Two lines of caption under the row, and a gap before the next.
            y += heights[row] + label * 3.4f + gap;
        }

        return placed;
    }

    private static float Widest(SKFont font, string caption)
        => caption.Split('\n').Max(line => font.MeasureText(line));
}
