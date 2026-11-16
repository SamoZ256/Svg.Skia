// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;

namespace ShimSkiaSharp;

public class PathClip : ICloneable, IDeepCloneable<PathClip>
{
    public SKPath? Path { get; set; }

    public SKMatrix? Transform { get; set; }

    public ClipPath? Clip { get; set; }

    /// <summary>What <see cref="Path"/> was drawn from, where its numbers are expressions.</summary>
    /// <remarks>Beside the path rather than instead of it: the path is what it draws with nothing bound.</remarks>
    public SymPath? SymbolicPath { get; set; }

    /// <summary>The whole of <see cref="Transform"/> as the functions that built it, where one is driven.</summary>
    public SymMatrix? SymbolicTransform { get; set; }

    /// <summary>Whether anything this part clips by, nested clips included, is driven.</summary>
    public bool IsSymbolic
        => SymbolicPath?.IsSymbolic == true || SymbolicTransform?.IsSymbolic == true || Clip?.IsSymbolic == true;

    public PathClip Clone() => DeepClone(new CloneContext());

    public PathClip DeepClone() => Clone();

    object ICloneable.Clone() => Clone();

    internal PathClip DeepClone(CloneContext context)
    {
        if (context.TryGet(this, out PathClip existing))
        {
            return existing;
        }

        var clone = new PathClip();
        context.Add(this, clone);

        clone.Path = Path?.DeepClone(context);
        clone.Transform = Transform;
        clone.Clip = Clip?.DeepClone(context);
        clone.SymbolicPath = SymbolicPath;
        clone.SymbolicTransform = SymbolicTransform;

        return clone;
    }
}
