// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Collections.Generic;

namespace Svg.Expressions;

// The untyped tree the parser produces. Deliberately internal: nothing outside this assembly
// should have to know the shape of an unchecked expression, and the checked one is what a back end
// consumes. ExprChecker's entry points take text for the same reason.
//
// A call carries its name as written rather than a resolved function, so an unterminated call
// still reports "Expected ')' to close the call to 'wobble'" from the parser instead of the
// checker's unknown-function complaint.

// The operators are public, unlike the tree they appear in: a checked expression carries them, and
// a back end in another assembly has to switch on them.
public enum ExprUnaryOp
{
    Negate,
    Not
}

public enum ExprBinaryOp
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Equal,
    NotEqual,
    And,
    Or
}

internal abstract record ExprNode(int Position);

internal sealed record NumberExpr(int Position, double Value) : ExprNode(Position);

internal sealed record ColorExpr(int Position, byte R, byte G, byte B, byte A) : ExprNode(Position);

internal sealed record BooleanExpr(int Position, bool Value) : ExprNode(Position);

internal sealed record IdentifierExpr(int Position, string Name) : ExprNode(Position);

internal sealed record UnaryExpr(int Position, ExprUnaryOp Op, ExprNode Operand) : ExprNode(Position);

internal sealed record BinaryExpr(int Position, ExprBinaryOp Op, ExprNode Left, ExprNode Right) : ExprNode(Position);

internal sealed record ConditionalExpr(int Position, ExprNode Condition, ExprNode WhenTrue, ExprNode WhenFalse) : ExprNode(Position);

internal sealed record CallExpr(int Position, string Name, IReadOnlyList<ExprNode> Arguments) : ExprNode(Position);
