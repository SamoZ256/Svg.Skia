// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Collections.Generic;

namespace Svg.Expressions;

/// <summary>
/// A checked expression: every node knows its type, every name is resolved, and every call names a
/// function of the language rather than a string. This is what a back end consumes — emitting C#,
/// or evaluating against values — so neither has to type check anything.
/// </summary>
/// <remarks>
/// <see cref="Position"/> travels with each node so a diagnostic still points at the authored text.
/// Nothing is folded: a back end emitting source has to reproduce what was written, and a number
/// keeps its <see langword="double"/> so narrowing happens once, where the target decides it.
/// </remarks>
public abstract record TypedExpr(ExprType Type, int Position);

public sealed record TypedNumber(int Position, double Value)
    : TypedExpr(ExprType.Number, Position);

public sealed record TypedColor(int Position, byte R, byte G, byte B, byte A)
    : TypedExpr(ExprType.Color, Position);

public sealed record TypedBoolean(int Position, bool Value)
    : TypedExpr(ExprType.Boolean, Position);

/// <summary>A parameter or a let, as declared by the document.</summary>
/// <remarks>
/// Distinct from <see cref="TypedConstant"/>: a symbol table entry shadows a built-in, so a back end
/// handed only a name would emit the constant for a document that declared <c>pi</c> itself.
/// </remarks>
public sealed record TypedSymbol(ExprType Type, int Position, string Name)
    : TypedExpr(Type, Position);

public sealed record TypedConstant(ExprType Type, int Position, ExprConstant Constant)
    : TypedExpr(Type, Position);

public sealed record TypedUnary(ExprType Type, int Position, ExprUnaryOp Op, TypedExpr Operand)
    : TypedExpr(Type, Position);

public sealed record TypedBinary(ExprType Type, int Position, ExprBinaryOp Op, TypedExpr Left, TypedExpr Right)
    : TypedExpr(Type, Position);

public sealed record TypedConditional(ExprType Type, int Position, TypedExpr Condition, TypedExpr WhenTrue, TypedExpr WhenFalse)
    : TypedExpr(Type, Position);

public sealed record TypedCall(ExprType Type, int Position, ExprFunction Function, IReadOnlyList<TypedExpr> Arguments)
    : TypedExpr(Type, Position);
