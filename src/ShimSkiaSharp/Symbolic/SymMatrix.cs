// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;

namespace ShimSkiaSharp;

// One transform function, spelled as the model folds it rather than as SVG writes it: the optional
// argument forms are filled out here, so nothing downstream has to know that rotate takes one
// argument or three.
public enum SymTransformOp
{
    // (a, b, c, d, e, f)
    Matrix,

    // (x, y)
    Translate,

    // (x, y)
    Scale,

    // (degrees, centreX, centreY)
    Rotate,

    // (degreesX, degreesY) — one call covers skewX and skewY, as SvgSkew does.
    Skew
}

// A transform whose arguments may be expressions rather than numbers.
//
// Deliberately not a SymNode: a node describes how a *value* was derived, and both back ends walk
// one carrying the ExprType the position expects. A matrix is not a value of the language, and a
// matrix-shaped node could reach a slot expecting a colour. The arguments below are ordinary
// SymNodes, so everything that already evaluates or emits one works on them unchanged.
public sealed record SymTransform(SymTransformOp Op, IReadOnlyList<SymNode> Arguments) : IDeepCloneable<SymTransform>
{
    // Immutable all the way down, so sharing the instance is a correct deep clone.
    public SymTransform DeepClone() => this;

    public bool IsLiteral
    {
        get
        {
            foreach (var argument in Arguments)
            {
                if (argument is not SymLit)
                {
                    return false;
                }
            }

            return true;
        }
    }
}

/// <summary>An element's transform, kept as the functions that built it.</summary>
public sealed record SymMatrix(IReadOnlyList<SymTransform> Transforms) : IDeepCloneable<SymMatrix>
{
    public SymMatrix DeepClone() => this;

    /// <summary>Whether any argument is something other than a number already known.</summary>
    public bool IsSymbolic
    {
        get
        {
            foreach (var transform in Transforms)
            {
                if (!transform.IsLiteral)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>A matrix already folded, as the one function that reproduces it.</summary>
    public static SymMatrix Of(SKMatrix matrix)
        => new(new[]
        {
            new SymTransform(
                SymTransformOp.Matrix,
                new[]
                {
                    SymNode.Literal(matrix.ScaleX),
                    SymNode.Literal(matrix.SkewY),
                    SymNode.Literal(matrix.SkewX),
                    SymNode.Literal(matrix.ScaleY),
                    SymNode.Literal(matrix.TransX),
                    SymNode.Literal(matrix.TransY)
                })
        });

    /// <summary>
    /// <paramref name="left"/> then <paramref name="right"/>, as <c>SKMatrix.PreConcat</c> folds them.
    /// </summary>
    /// <remarks>
    /// Null where neither side is symbolic, so a document that drives no transform records exactly
    /// the commands it always did. The baked matrices stand in for the side that carries no
    /// functions, which is what lets a driven transform sit under an ordinary one.
    /// </remarks>
    public static SymMatrix? PreConcat(SymMatrix? left, SKMatrix leftBaked, SymMatrix? right, SKMatrix rightBaked)
    {
        if (left is null && right is null)
        {
            return null;
        }

        var folded = new List<SymTransform>();

        folded.AddRange(Side(left, leftBaked));

        foreach (var transform in Side(right, rightBaked))
        {
            // Two folded matrices in a row multiply out, so a chain grows with the number of driven
            // functions rather than with the depth of the tree.
            if (folded.Count > 0 &&
                folded[folded.Count - 1] is { Op: SymTransformOp.Matrix, IsLiteral: true } already &&
                transform is { Op: SymTransformOp.Matrix, IsLiteral: true })
            {
                folded[folded.Count - 1] = Of(Bake(already).PreConcat(Bake(transform))).Transforms[0];

                continue;
            }

            folded.Add(transform);
        }

        return new SymMatrix(folded);
    }

    /// <summary>
    /// One side of a concatenation: its own functions, or the matrix it baked to.
    /// </summary>
    /// <remarks>
    /// An identity contributes nothing, so a driven transform under an untransformed ancestor — the
    /// common case — carries the one function the author wrote and no scaffolding around it.
    /// </remarks>
    private static IReadOnlyList<SymTransform> Side(SymMatrix? symbolic, SKMatrix baked)
        => symbolic?.Transforms
           ?? (baked.IsIdentity ? Array.Empty<SymTransform>() : Of(baked).Transforms);

    /// <summary>
    /// The matrix one function makes of the arguments it resolved to.
    /// </summary>
    /// <remarks>
    /// The one place a function becomes a matrix, so the recorder, the evaluator and
    /// <c>TransformsService</c> cannot disagree about what a skew angle means.
    /// </remarks>
    public static SKMatrix Apply(SymTransformOp op, IReadOnlyList<float> arguments) => op switch
    {
        // SVG writes a matrix down the columns, so b and c are the skews the other way round.
        SymTransformOp.Matrix => new SKMatrix(
            arguments[0], arguments[2], arguments[4],
            arguments[1], arguments[3], arguments[5],
            0f, 0f, 1f),
        SymTransformOp.Translate => SKMatrix.CreateTranslation(arguments[0], arguments[1]),
        SymTransformOp.Scale => SKMatrix.CreateScale(arguments[0], arguments[1]),
        SymTransformOp.Rotate => SKMatrix.CreateRotationDegrees(arguments[0], arguments[1], arguments[2]),
        SymTransformOp.Skew => SKMatrix.CreateSkew(Tangent(arguments[0]), Tangent(arguments[1])),
        _ => SKMatrix.CreateIdentity()
    };

    /// <summary>What a skew angle in degrees means as a matrix coefficient.</summary>
    public static float Tangent(float degrees) => (float)Math.Tan(Math.PI * degrees / 180d);

    private static SKMatrix Bake(SymTransform transform)
    {
        var arguments = new float[transform.Arguments.Count];

        for (var index = 0; index < arguments.Length; index++)
        {
            arguments[index] = (float)((SymLit)transform.Arguments[index]).Value;
        }

        return Apply(transform.Op, arguments);
    }
}
