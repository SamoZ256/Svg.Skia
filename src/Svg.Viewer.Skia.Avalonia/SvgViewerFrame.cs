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
public sealed record SvgViewerFrame(SKRect Bounds, string? Label = null, float LabelSize = 0f);
