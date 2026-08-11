// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;

namespace ShimSkiaSharp;

public readonly struct SKColorF
{
    public float Red { get; }
    public float Green { get; }
    public float Blue { get; }
    public float Alpha { get; }

    // See SKColor.Expression. Carried here so the conversions between the two do not drop it.
    public SymNode? Expression { get; }

    public static readonly SKColorF Empty = default;

    public SKColorF(float red, float green, float blue, float alpha)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
        Expression = null;
    }

    public SKColorF(float red, float green, float blue, float alpha, SymNode? expression)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
        Expression = expression;
    }

    public SKColorF WithExpression(SymNode? expression)
        => new(Red, Green, Blue, Alpha, expression);

    public static implicit operator SKColor(SKColorF color)
    {
        return new(
            (byte)(color.Red * 255.0f),
            (byte)(color.Green * 255.0f),
            (byte)(color.Blue * 255.0f),
            (byte)(color.Alpha * 255.0f),
            color.Expression);
    }

    public override string ToString()
        => Expression is null
            ? FormattableString.Invariant($"{Red}, {Green}, {Blue}, {Alpha}")
            : FormattableString.Invariant($"{Red}, {Green}, {Blue}, {Alpha} [{Expression}]");
}
