// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
namespace ShimSkiaSharp;

// A gradient's stops as lists the host passes: a colour[] for Colors, a number[] for Offsets or
// null to space them evenly, and the paint's opacity scaling each colour's alpha. Beside the stops
// it recorded rather than instead of them, as an expression sits beside a colour, so the gradient
// still draws as written until it is bound.
public sealed record SymGradientStops(SymNode Colors, SymNode? Offsets, SymNode Opacity, bool LinearRgb) : IDeepCloneable<SymGradientStops>
{
    // Immutable all the way down, so sharing the instance is a correct deep clone.
    public SymGradientStops DeepClone() => this;
}
