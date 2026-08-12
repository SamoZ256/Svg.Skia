// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
namespace ShimSkiaSharp;

// Operations the model itself applies to a symbolic value while building a picture. These are
// distinct from anything the author writes: authored expressions stay whole inside a SymSource
// node and are compiled by the back end, so no authoring construct needs an operator here.
public enum SymOp
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Negate,

    // (color, factor) => the color with its alpha channel multiplied by factor.
    ScaleAlpha,

    // (color) => the color with its RGB channels converted from sRGB to linear RGB.
    // Applied when an element resolves color-interpolation to linearRGB.
    ToLinearRgb
}
