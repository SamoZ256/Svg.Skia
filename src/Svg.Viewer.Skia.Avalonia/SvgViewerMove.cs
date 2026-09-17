// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using SkiaSharp;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>Something taken hold of on a canvas and let go somewhere else.</summary>
/// <param name="Item">Whatever <see cref="SvgViewerCanvas.Grip"/> answered at the press.</param>
/// <param name="By">
/// How far it was carried, in the space the drawings are arranged in rather than in control pixels:
/// a place a host saves has to mean the same at every zoom.
/// </param>
public readonly record struct SvgViewerMove(object Item, SKPoint By);
