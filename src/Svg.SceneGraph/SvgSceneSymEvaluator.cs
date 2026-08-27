// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using ShimSkiaSharp;
using Svg.Expressions;

namespace Svg.SceneGraph;

/// <summary>
/// Computes a <see cref="SymNode"/> into a value. The runtime counterpart of the code generator's
/// <c>SymCSharpEmitter</c>, and the same walk with the same type threading.
/// </summary>
/// <remarks>
/// The expected type travels down the tree rather than being read off each node, because position
/// decides it: the factor of an alpha scale is a number inside a colour. Getting it wrong reports a
/// type error on a document that is fine. A <see cref="SymSource"/> can hold a literal the model
/// wrote itself, so source handling cannot be shortcut for nodes the author did not write.
/// </remarks>
internal static class SvgSceneSymEvaluator
{
    public static ExprValue Evaluate(SymNode node, ExprType expected, ExprEvaluator evaluator)
    {
        switch (node)
        {
            case SymSource source:
                {
                    // Named by the language rather than here, so a bad document reports identically
                    // whichever back end reads it, and there is one label to add a case to.
                    var what = ExprFunctions.DescribeUse(expected);

                    return evaluator.EvaluateTo(source.Text, expected, what);
                }

            case SymLit lit:
                // Narrowed here for the same reason the emitter round-trips the literal through
                // float: an opacity arrives as a float widened to double, and the value the two back
                // ends compute with has to be the same one.
                return ExprValue.Number((float)lit.Value);

            case SymUnary { Op: SymOp.Negate } unary:
                return ExprValue.Number(-Evaluate(unary.Operand, ExprType.Number, evaluator).AsNumber);

            case SymUnary { Op: SymOp.ToLinearRgb } unary:
                return ExprColor.ToLinearRgb(Evaluate(unary.Operand, ExprType.Color, evaluator));

            case SymBinary { Op: SymOp.ScaleAlpha } binary:
                // The one node whose operands differ in type, which is what the threading is for.
                return ExprColor.ScaleAlpha(
                    Evaluate(binary.Left, ExprType.Color, evaluator),
                    Evaluate(binary.Right, ExprType.Number, evaluator).AsNumber);

            case SymBinary binary:
                {
                    var left = Evaluate(binary.Left, ExprType.Number, evaluator).AsNumber;
                    var right = Evaluate(binary.Right, ExprType.Number, evaluator).AsNumber;

                    return ExprValue.Number(Arithmetic(binary.Op, left, right));
                }

            default:
                throw new NotSupportedException($"Unsupported {nameof(SymNode)}: {node.GetType().Name}.");
        }
    }

    private static float Arithmetic(SymOp op, float left, float right)
        => op switch
        {
            SymOp.Add => left + right,
            SymOp.Subtract => left - right,
            SymOp.Multiply => left * right,
            SymOp.Divide => left / right,
            _ => throw new NotSupportedException($"Unsupported binary {nameof(SymOp)}: {op}.")
        };
}
