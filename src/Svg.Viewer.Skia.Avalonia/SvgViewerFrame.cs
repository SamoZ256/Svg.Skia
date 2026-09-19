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
/// <param name="Label">What to write inside its top corner, or null to write nothing.</param>
/// <remarks>
/// Where the name is written, and so where the frame is taken hold of, is
/// <see cref="SvgViewerCanvas.TitleOf"/>: a name is drawn at a fixed size on the control, so only
/// something that knows how far in the view is zoomed can say where it lands. It sits inside the
/// frame, so an arrangement leaves no room for it and there is no size to be told here — a frame
/// is exactly what its bounds say.
/// </remarks>
public sealed record SvgViewerFrame(SKRect Bounds, string? Label = null);
