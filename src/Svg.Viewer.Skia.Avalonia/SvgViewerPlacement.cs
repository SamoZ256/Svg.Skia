// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using SkiaSharp;
using Svg.Skia;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>One drawing on a canvas: where it sits, and what it is called there.</summary>
/// <remarks>
/// Everything about the placement is the caller's: a canvas showing several drawings is showing
/// somebody's arrangement of them — a project's group in the order it builds them — and the rules
/// behind that arrangement are no business of the surface drawing it.
/// </remarks>
/// <param name="Svg">The drawing.</param>
/// <param name="At">Where its own origin goes, in drawing units.</param>
/// <param name="Label">What to write under it, or null to write nothing.</param>
/// <param name="LabelSize">
/// How tall that writing is, in drawing units — so it is scaled by the canvas along with everything
/// else. Zero writes nothing.
/// </param>
public sealed record SvgViewerPlacement(SKSvg Svg, SKPoint At, string? Label = null, float LabelSize = 0f);
