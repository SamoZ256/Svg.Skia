// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Globalization;
using SkiaSharp;
using Shim = ShimSkiaSharp;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>A line dragged across a canvas, read as a distance and an angle in a drawing's own units.</summary>
/// <remarks>
/// Upstream's <c>SvgEditorMeasurement</c> reads the same four numbers, but only its own editor can make
/// one. The ends are in the space the canvas arranges drawings in, which is where a host's grid and
/// guides pull them.
/// </remarks>
public sealed class SvgViewerMeasure
{
    private Shim.SKPoint _from;
    private Shim.SKPoint _to;

    /// <summary>Whether there is a line to show, from a drag in progress or the last one let go.</summary>
    public bool IsShowing { get; private set; }

    public void Begin(Shim.SKPoint at)
    {
        _from = _to = at;
        IsShowing = true;
    }

    /// <param name="level">Shift: the nearest of the eight directions, as the Line tool takes.</param>
    public void Drag(Shim.SKPoint to, bool level)
    {
        var (x, y) = level ? SvgViewerDraw.Eighth(_from, to) : (to.X, to.Y);

        _to = new Shim.SKPoint(x, y);
    }

    public void Clear() => IsShowing = false;

    /// <summary>The line, to ring on the canvas, or null with nothing measured.</summary>
    public SKPath? Line()
    {
        if (!IsShowing)
        {
            return null;
        }

        using var line = new SKPathBuilder();

        line.MoveTo(_from.X, _from.Y);
        line.LineTo(_to.X, _to.Y);

        return line.Detach();
    }

    /// <summary>The reading, with <paramref name="units"/> mapping the drawing's user units to the canvas's space.</summary>
    public string? Reading(Shim.SKMatrix units)
    {
        if (!IsShowing || !units.TryInvert(out var back))
        {
            return null;
        }

        var from = back.MapPoint(_from);
        var to = back.MapPoint(_to);
        var dx = (double)to.X - from.X;
        var dy = (double)to.Y - from.Y;

        // Clockwise from the x axis, since y runs down: upstream's convention.
        return string.Format(
            CultureInfo.InvariantCulture,
            "Distance {0:0.##}, angle {1:0.##}°, Δx {2:0.##}, Δy {3:0.##}",
            Math.Sqrt(dx * dx + dy * dy),
            Math.Atan2(dy, dx) * 180d / Math.PI,
            dx,
            dy);
    }
}
