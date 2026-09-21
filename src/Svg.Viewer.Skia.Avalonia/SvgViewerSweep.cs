// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// What a rectangle being swept has caught, and the ring showing it.
/// </summary>
/// <remarks>
/// <para>
/// One place for the rule and for the ring that shows it, so a host cannot state the rule twice and
/// have the rectangle promise what the drop will discard. Both hosts hold one of these for the
/// length of a sweep.
/// </para>
/// <para>
/// It answers "nothing changed" rather than tracing the same thing again. A sweep asks on every
/// pointer move, and most of those moves grow the rectangle across empty canvas without catching
/// anything new — tracing there would build a path per frame, sixty a second, each of them left for
/// the finalizer because the canvas deliberately does not free a ring the render thread may still
/// be drawing. Tracing only when the catch changes puts that back to a handful per sweep, which is
/// the number the canvas's own remark about it assumes.
/// </para>
/// </remarks>
public sealed class SvgViewerSweep
{
    private SKRect? _at;
    private SvgViewerPlacement? _in;
    private IReadOnlyList<SvgElement> _caught = Array.Empty<SvgElement>();

    /// <summary>What the sweep has caught, as picks, for a host about to select them.</summary>
    public IReadOnlyList<SvgViewerPick> Caught
        => _in is { } placement
            ? SvgViewerPicks.Of(new[] { (placement, _caught) })
            : Array.Empty<SvgViewerPick>();

    /// <summary>Asks what a rectangle is over, and whether that is news.</summary>
    /// <param name="swept">The rectangle as it now stands, or null where the sweep is over.</param>
    /// <param name="outline">The ring to show, which is null for a sweep that will select nothing.</param>
    /// <returns>Whether anything changed, and so whether the ring is worth swapping.</returns>
    public bool TryTrace(SvgViewerCanvas canvas, SKRect? swept, out SKPath? outline)
    {
        outline = null;

        if (swept is not { } rectangle)
        {
            var had = _in is { };

            Forget();

            // A sweep taken back is news exactly when it was showing something.
            return had;
        }

        // The same rectangle twice is the same answer twice: a move that stayed inside the slack,
        // or a pointer reporting where it already was.
        if (_at == rectangle)
        {
            return false;
        }

        _at = rectangle;

        var only = SvgViewerPicks.Only(canvas.Sweeping(rectangle));
        var caught = only?.Elements ?? Array.Empty<SvgElement>();

        // By the elements themselves rather than by their addresses: nothing rebuilds a drawing
        // while a sweep runs, so the references hold for the length of it, and naming an element
        // costs a walk up its parents that the answer does not need.
        if (ReferenceEquals(only?.Placement, _in) && Same(caught, _caught))
        {
            return false;
        }

        _in = only?.Placement;
        _caught = caught;

        outline = SvgViewerPicks.Outline(Caught);

        return true;
    }

    /// <summary>Lets go of what was being shown, for a sweep that is over.</summary>
    public void Forget()
    {
        _at = null;
        _in = null;
        _caught = Array.Empty<SvgElement>();
    }

    private static bool Same(IReadOnlyList<SvgElement> now, IReadOnlyList<SvgElement> before)
    {
        if (now.Count != before.Count)
        {
            return false;
        }

        for (var i = 0; i < now.Count; i++)
        {
            if (!ReferenceEquals(now[i], before[i]))
            {
                return false;
            }
        }

        return true;
    }
}
