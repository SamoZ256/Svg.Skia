// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using SkiaSharp;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>A named rectangle drawn round part of an arrangement, holding no drawing of its own.</summary>
/// <remarks>
/// What it is round is the caller's business, exactly as <see cref="SvgViewerPlacement"/>'s
/// arrangement is: furniture the host asked for, at coordinates the host worked out. A Svg.Studio
/// project draws one round each group a board holds, so what belongs to what can be seen and the
/// whole of it taken hold of at once.
/// </remarks>
/// <param name="Bounds">Where it is, in the space the drawings are arranged in.</param>
/// <param name="Label">What to write above it, or null to write nothing.</param>
/// <param name="LabelSize">
/// How tall that writing is, in drawing units, so it is scaled along with everything else. Zero
/// writes nothing.
/// </param>
public sealed record SvgViewerFrame(SKRect Bounds, string? Label = null, float LabelSize = 0f)
{
    /// <summary>The strip the name is written on, which is empty where there is no name.</summary>
    /// <remarks>
    /// A frame is taken hold of by its name rather than by anywhere inside it. What is inside it is
    /// mostly the room between the drawings it holds, and a host that grabbed that had no room left
    /// to pan in — a press nearly anywhere carried a whole group instead of moving the view.
    ///
    /// A quarter of the writing's height below the top edge as well as the room above it, so the
    /// frame's own line is part of the target and the band is not a hairline at a low zoom.
    /// </remarks>
    public SKRect Title
        => this is { Label.Length: > 0, LabelSize: > 0f }
            ? new SKRect(
                Bounds.Left,
                Bounds.Top - (LabelSize * 1.5f),
                Bounds.Right,
                Bounds.Top + (LabelSize * 0.25f))
            : SKRect.Empty;
}
