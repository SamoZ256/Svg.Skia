// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
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

            case SymUnary { Op: SymOp.Last } unary:
                return Last(unary.Operand, null, evaluator);

            case SymBinary { Op: SymOp.Last } binary:
                return Last(binary.Left, binary.Right, evaluator);

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

    /// <summary>The matrix a recorded transform makes of the values its arguments bind to.</summary>
    /// <remarks>
    /// Folded left to right by <c>PreConcat</c>, as <c>TransformsService.ToMatrix</c> folds an
    /// <c>SvgTransformCollection</c>: the recorded functions are that same list in that same order,
    /// so a bound picture agrees with the one the compiler folded from the stand-in values.
    /// </remarks>
    public static SKMatrix EvaluateMatrix(SymMatrix matrix, ExprEvaluator evaluator)
    {
        var total = SKMatrix.CreateIdentity();

        foreach (var transform in matrix.Transforms)
        {
            var arguments = new float[transform.Arguments.Count];

            for (var index = 0; index < arguments.Length; index++)
            {
                arguments[index] = Evaluate(transform.Arguments[index], ExprType.Number, evaluator).AsNumber;
            }

            total = total.PreConcat(SymMatrix.Apply(transform.Op, arguments));
        }

        return total;
    }

    /// <summary>The path a driven clip shape draws with the values its numbers bind to.</summary>
    public static SKPath EvaluatePath(SymPath path, ExprEvaluator evaluator)
    {
        var arguments = new float[path.ArgumentCount];
        var index = 0;

        foreach (var command in path.Commands)
        {
            foreach (var argument in command.Arguments)
            {
                arguments[index++] = Evaluate(argument, ExprType.Number, evaluator).AsNumber;
            }
        }

        return path.Apply(arguments);
    }

    /// <summary>
    /// <paramref name="clipPath"/> drawn with the values its expressions bind to, or the same instance
    /// where nothing in it is driven.
    /// </summary>
    /// <remarks>
    /// Built afresh rather than changed in place: the compiled clip is shared by the node, the clip
    /// cache and the picture still holding its expressions. The copy drops them, for the reason a
    /// resolved colour drops its own. <paramref name="rewritten"/> keeps a clip that several places
    /// share one instance once rewritten.
    /// </remarks>
    public static ClipPath EvaluateClipPath(ClipPath clipPath, ExprEvaluator evaluator, IDictionary<object, object>? rewritten = null)
    {
        if (!clipPath.IsSymbolic)
        {
            return clipPath;
        }

        if (rewritten is { } && rewritten.TryGetValue(clipPath, out var existing))
        {
            return (ClipPath)existing;
        }

        List<PathClip>? clips = null;

        if (clipPath.Clips is { } parts)
        {
            clips = new List<PathClip>(parts.Count);

            foreach (var part in parts)
            {
                clips.Add(EvaluatePathClip(part, evaluator, rewritten));
            }
        }

        var result = new ClipPath
        {
            Clips = clips,
            Transform = clipPath.SymbolicTransform is { IsSymbolic: true } transform ? EvaluateMatrix(transform, evaluator) : clipPath.Transform,
            Clip = clipPath.Clip is { } nested ? EvaluateClipPath(nested, evaluator, rewritten) : null
        };

        if (rewritten is { })
        {
            rewritten[clipPath] = result;
        }

        return result;
    }

    private static PathClip EvaluatePathClip(PathClip clip, ExprEvaluator evaluator, IDictionary<object, object>? rewritten)
    {
        if (!clip.IsSymbolic)
        {
            return clip;
        }

        if (rewritten is { } && rewritten.TryGetValue(clip, out var existing))
        {
            return (PathClip)existing;
        }

        var result = new PathClip
        {
            Path = clip.SymbolicPath is { IsSymbolic: true } path ? EvaluatePath(path, evaluator) : clip.Path,
            Transform = clip.SymbolicTransform is { IsSymbolic: true } transform ? EvaluateMatrix(transform, evaluator) : clip.Transform,
            Clip = clip.Clip is { } nested ? EvaluateClipPath(nested, evaluator, rewritten) : null
        };

        if (rewritten is { })
        {
            rewritten[clip] = result;
        }

        return result;
    }

    private static ExprValue Last(SymNode colors, SymNode? offsets, ExprEvaluator evaluator)
    {
        var list = Evaluate(colors, ExprType.ColorList, evaluator).AsColorList;
        var count = offsets is null ? list.Count : Math.Min(list.Count, Evaluate(offsets, ExprType.NumberList, evaluator).AsNumberList.Count);

        return count == 0 ? ExprValue.Color(0, 0, 0, 0) : list[count - 1];
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
