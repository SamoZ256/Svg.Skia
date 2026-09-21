// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Collections.Generic;
using SkiaSharp;
using Svg.SceneGraph;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>One element of one drawing on a canvas: which drawing it is in, and where in it.</summary>
/// <remarks>
/// <para>
/// Named by address rather than held as an element, because a drawing is rebuilt from its text on
/// every keystroke and shares no element with the one it replaced. A selection holding elements
/// would be stale a keystroke after it was made; one holding addresses is still about the same rows.
/// </para>
/// <para>
/// The placement is what says <em>which</em> drawing, and it costs nothing in the viewer: a single
/// drawing is already placed, at the origin, so one shape serves both a viewer showing one and a
/// board showing twenty.
/// </para>
/// </remarks>
/// <param name="Placement">The drawing, and where its own origin sits on the canvas.</param>
/// <param name="AddressKey">Which element of it, as the drawing's own document spells the address.</param>
public readonly record struct SvgViewerPick(SvgViewerPlacement Placement, string AddressKey)
{
    /// <summary>The element it names in the drawing as it now stands, or null where it has gone.</summary>
    public SvgElement? Element
        => Placement.Svg.SourceDocument is { } document && SvgElementAddress.Parse(AddressKey) is { } address
            ? address.Resolve(document)
            : null;

    /// <summary>What it covers, where its drawing sits on the canvas.</summary>
    public SKPath? Outline()
    {
        if (Element is not { } element || SvgViewerOutline.Of(Placement.Svg, element) is not { } traced)
        {
            return null;
        }

        // The viewer's one drawing sits at the origin, so this is the identity there and the
        // difference between the two hosts is a translation by nothing.
        if (Placement.At.X != 0f || Placement.At.Y != 0f)
        {
            traced.Transform(SKMatrix.CreateTranslation(Placement.At.X, Placement.At.Y));
        }

        return traced;
    }
}

/// <summary>What a selection of several covers.</summary>
public static class SvgViewerPicks
{
    /// <summary>
    /// One path holding every piece of every pick, or null where they cover nothing.
    /// </summary>
    /// <remarks>
    /// One path and not one per pick, because the canvas strokes what it is given whole: a ring is
    /// never filled, so the pieces need not be unioned and a selection spanning three drawings is
    /// still one thing to draw and one pulse.
    /// </remarks>
    public static SKPath? Outline(IReadOnlyList<SvgViewerPick> picks)
    {
        SKPath? traced = null;

        foreach (var pick in picks)
        {
            if (pick.Outline() is not { } one)
            {
                continue;
            }

            if (traced is null)
            {
                traced = one;

                continue;
            }

            traced.AddPath(one);
            one.Dispose();
        }

        return traced;
    }
}
