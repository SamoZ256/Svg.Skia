// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;

namespace ShimSkiaSharp;

public abstract record SKPathEffect : IDeepCloneable<SKPathEffect>
{
    public static SKPathEffect CreateDash(float[] intervals, float phase)
        => new DashPathEffect(intervals, phase);

    /// <summary>A dash whose phase an expression drives.</summary>
    /// <remarks>
    /// The expression rides beside the number rather than instead of it, the way
    /// <see cref="SKColor.Expression"/> sits beside a colour's bytes: what was recorded still draws
    /// on its own, and binding replaces it.
    /// </remarks>
    public static SKPathEffect CreateDash(float[] intervals, float phase, SymNode? phaseExpression)
        => new DashPathEffect(intervals, phase) { PhaseExpression = phaseExpression };

    public SKPathEffect DeepClone() => DeepClone(new CloneContext());

    internal SKPathEffect DeepClone(CloneContext context)
    {
        if (context.TryGet(this, out SKPathEffect existing))
        {
            return existing;
        }

        context.Enter(this);
        try
        {
            var clone = this switch
            {
                // The expression clones with it: a node is immutable, so carrying the reference is
                // the whole of the copy, and dropping it here would lose the binding wherever a
                // paint is cloned.
                DashPathEffect dashPathEffect => new DashPathEffect(CloneHelpers.CloneArray(dashPathEffect.Intervals, context), dashPathEffect.Phase)
                {
                    PhaseExpression = dashPathEffect.PhaseExpression
                },
                _ => throw new NotSupportedException($"Unsupported {nameof(SKPathEffect)} type: {GetType().Name}.")
            };

            context.Add(this, clone);
            return clone;
        }
        finally
        {
            context.Exit(this);
        }
    }
}

public record DashPathEffect(float[]? Intervals, float Phase) : SKPathEffect
{
    /// <summary>What drives the phase, where anything does.</summary>
    /// <remarks>
    /// Beside <see cref="Phase"/> rather than instead of it, so a reader that knows nothing about
    /// expressions still draws the dash the document was written with.
    /// </remarks>
    public SymNode? PhaseExpression { get; init; }
}
