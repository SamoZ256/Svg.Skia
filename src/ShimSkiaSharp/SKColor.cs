// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;

namespace ShimSkiaSharp;

public readonly struct SKColor : IEquatable<SKColor>
{
    public byte Red { get; }

    public byte Green { get; }

    public byte Blue { get; }

    public byte Alpha { get; }

    // Null for a plain color. When set, the channels above stay authoritative for rendering
    // and describe the design-time value; code generators emit this instead of the literal.
    public SymNode? Expression { get; }

    public static readonly SKColor Empty = default;

    public SKColor(byte red, byte green, byte blue, byte alpha)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
        Expression = null;
    }

    public SKColor(byte red, byte green, byte blue, byte alpha, SymNode? expression)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
        Expression = expression;
    }

    public SKColor WithExpression(SymNode? expression)
        => new(Red, Green, Blue, Alpha, expression);

    public static implicit operator SKColorF(SKColor color)
    {
        return new(
            color.Red * (1 / 255.0f),
            color.Green * (1 / 255.0f),
            color.Blue * (1 / 255.0f),
            color.Alpha * (1 / 255.0f),
            color.Expression);
    }

    public bool Equals(SKColor other)
    {
        return Red == other.Red &&
               Green == other.Green &&
               Blue == other.Blue &&
               Alpha == other.Alpha &&
               Equals(Expression, other.Expression);
    }

    public override bool Equals(object? obj)
        => obj is SKColor other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Red.GetHashCode();
            hash = (hash * 397) ^ Green.GetHashCode();
            hash = (hash * 397) ^ Blue.GetHashCode();
            hash = (hash * 397) ^ Alpha.GetHashCode();
            hash = (hash * 397) ^ (Expression?.GetHashCode() ?? 0);
            return hash;
        }
    }

    public override string ToString()
        => Expression is null
            ? FormattableString.Invariant($"{Red}, {Green}, {Blue}, {Alpha}")
            : FormattableString.Invariant($"{Red}, {Green}, {Blue}, {Alpha} [{Expression}]");
}
