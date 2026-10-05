// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Svg.Expressions;

/// <summary>
/// Replaces whatever a checked expression already knows with its value, so a back end emitting
/// source writes <c>false</c> where the author wrote <c>!true</c>.
/// </summary>
/// <remarks>
/// Every value comes from <see cref="ExprValueBackend"/>, which is pinned bit for bit against the C#
/// back end, so folding cannot change an answer — only where it is computed.
/// </remarks>
public static class ExprFolder
{
    private static readonly IReadOnlyDictionary<string, ExprValue> s_none =
        new Dictionary<string, ExprValue>(StringComparer.Ordinal);

    /// <param name="constants">
    /// Symbols whose value is already known — a let that folded to one. Never a parameter.
    /// </param>
    public static TypedExpr Fold(TypedExpr node, IReadOnlyDictionary<string, ExprValue> constants)
    {
        switch (node)
        {
            case TypedSymbol symbol when constants.TryGetValue(symbol.Name, out var value):
                return Literal(value, symbol.Position);

            case TypedUnary unary:
                return Evaluated(unary with { Operand = Fold(unary.Operand, constants) });

            // Only the left operand decides, so the right is still evaluated wherever it was.
            case TypedBinary { Op: ExprBinaryOp.And or ExprBinaryOp.Or } logical:
                {
                    var left = Fold(logical.Left, constants);

                    if (left is TypedBoolean decided)
                    {
                        return decided.Value == (logical.Op == ExprBinaryOp.Or)
                            ? new TypedBoolean(logical.Position, decided.Value)
                            : Fold(logical.Right, constants);
                    }

                    return logical with { Left = left, Right = Fold(logical.Right, constants) };
                }

            case TypedBinary binary:
                return Evaluated(binary with
                {
                    Left = Fold(binary.Left, constants),
                    Right = Fold(binary.Right, constants)
                });

            case TypedConditional conditional:
                {
                    var condition = Fold(conditional.Condition, constants);

                    if (condition is TypedBoolean decided)
                    {
                        return Fold(decided.Value ? conditional.WhenTrue : conditional.WhenFalse, constants);
                    }

                    return conditional with
                    {
                        Condition = condition,
                        WhenTrue = Fold(conditional.WhenTrue, constants),
                        WhenFalse = Fold(conditional.WhenFalse, constants)
                    };
                }

            // Answers that depend on the runtime computing them, which folding would make the
            // generator's host rather than the drawing's: the netstandard2.0 fallback is an ulp off
            // MathF for the transcendentals, .NET Framework prints a float to 7 digits rather than
            // round-trip, and case mapping follows the ICU tables or invariant mode.
            case TypedCall call:
                {
                    var folded = call with { Arguments = call.Arguments.Select(argument => Fold(argument, constants)).ToList() };

                    return call.Function is ExprFunction.Sin or ExprFunction.Cos or ExprFunction.Tan or ExprFunction.Pow
                                            or ExprFunction.Upper or ExprFunction.Lower
                           || (call.Function == ExprFunction.Str && call.Arguments[0].Type == ExprType.Number)
                        ? folded
                        : Evaluated(folded);
                }

            default:
                return node;
        }
    }

    /// <summary>The value of a node that needs nothing bound to compute.</summary>
    public static bool TryValue(TypedExpr node, out ExprValue value)
    {
        if (IsConstant(node))
        {
            value = ExprValueBackend.Evaluate(node, s_none);
            return true;
        }

        value = default;
        return false;
    }

    public static TypedExpr Literal(ExprValue value, int position)
        => value.Type switch
        {
            ExprType.Number => new TypedNumber(position, value.AsNumber),
            ExprType.Integer => new TypedInteger(position, value.AsInteger),
            ExprType.Color => new TypedColor(position, value.Red, value.Green, value.Blue, value.Alpha),
            ExprType.Boolean => new TypedBoolean(position, value.AsBoolean),
            ExprType.String => new TypedString(position, value.AsString),
            _ => throw new NotSupportedException($"Unsupported {nameof(ExprType)}: {value.Type}.")
        };

    // A bare pi stays MathF.PI, which reads better than its digits; it only folds inside something.
    private static bool IsConstant(TypedExpr node)
        => node is TypedNumber or TypedInteger or TypedColor or TypedBoolean or TypedString or TypedConstant;

    private static TypedExpr Evaluated(TypedExpr node)
    {
        IReadOnlyList<TypedExpr> operands = node switch
        {
            TypedUnary unary => new[] { unary.Operand },
            TypedBinary binary => new[] { binary.Left, binary.Right },
            TypedCall call => call.Arguments,
            _ => Array.Empty<TypedExpr>()
        };

        if (!operands.All(IsConstant))
        {
            return node;
        }

        try
        {
            return Literal(ExprValueBackend.Evaluate(node, s_none), node.Position);
        }
        catch (Exception)
        {
            // clamp(5, 3, 1) throws from Math.Clamp. The generated code has to go on throwing where
            // it runs rather than the generator failing on a document that compiles today.
            return node;
        }
    }
}
