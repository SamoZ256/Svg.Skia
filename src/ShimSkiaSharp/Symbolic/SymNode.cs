// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;

namespace ShimSkiaSharp;

// A language-neutral description of how a value was derived. The model builds these while
// flattening an SVG; back ends decide what syntax they render as. Nothing here emits C#.
public abstract record SymNode : IDeepCloneable<SymNode>
{
    // Nodes are immutable all the way down, so sharing the instance is a correct deep clone.
    public SymNode DeepClone() => this;

    public static readonly SymNode Zero = new SymLit(0);

    public static readonly SymNode One = new SymLit(1);

    public static SymNode Source(string text)
        => new SymSource(text ?? throw new ArgumentNullException(nameof(text)));

    public static SymNode Literal(double value) => new SymLit(value);

    public static bool IsLiteral(SymNode? node, double value)
        => node is SymLit lit && lit.Value == value;

    // Folding is restricted to identities that hold for every double, NaN and infinities
    // included. x * 0 is not one of them (NaN * 0 is NaN), so Multiply never folds to Zero.
    public static SymNode Add(SymNode left, SymNode right)
    {
        if (IsLiteral(right, 0))
        {
            return left;
        }

        if (IsLiteral(left, 0))
        {
            return right;
        }

        return new SymBinary(SymOp.Add, left, right);
    }

    public static SymNode Subtract(SymNode left, SymNode right)
        => IsLiteral(right, 0) ? left : new SymBinary(SymOp.Subtract, left, right);

    public static SymNode Multiply(SymNode left, SymNode right)
    {
        if (IsLiteral(right, 1))
        {
            return left;
        }

        if (IsLiteral(left, 1))
        {
            return right;
        }

        return new SymBinary(SymOp.Multiply, left, right);
    }

    public static SymNode Divide(SymNode left, SymNode right)
        => IsLiteral(right, 1) ? left : new SymBinary(SymOp.Divide, left, right);

    public static SymNode Negate(SymNode operand) => new SymUnary(SymOp.Negate, operand);

    public static SymNode ScaleAlpha(SymNode color, SymNode factor)
        => IsLiteral(factor, 1) ? color : new SymBinary(SymOp.ScaleAlpha, color, factor);

    public static SymNode ToLinearRgb(SymNode color) => new SymUnary(SymOp.ToLinearRgb, color);
}

// Author-supplied expression source, carried through the pipeline uninterpreted. Parsing and
// type checking belong to the back end, which is the only layer that knows the symbol table.
public sealed record SymSource(string Text) : SymNode;

public sealed record SymLit(double Value) : SymNode;

public sealed record SymUnary(SymOp Op, SymNode Operand) : SymNode;

public sealed record SymBinary(SymOp Op, SymNode Left, SymNode Right) : SymNode;
