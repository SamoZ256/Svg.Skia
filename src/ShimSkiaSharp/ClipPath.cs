// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
namespace ShimSkiaSharp;

public class ClipPath : ICloneable, IDeepCloneable<ClipPath>
{
    public IList<PathClip>? Clips { get; set; }

    public SKMatrix? Transform { get; set; }

    public ClipPath? Clip { get; set; }

    /// <summary>The whole of <see cref="Transform"/> as the functions that built it, where one is driven.</summary>
    public SymMatrix? SymbolicTransform { get; set; }

    public bool IsEmpty => Clips is null || Clips.Count == 0;

    /// <summary>Whether anything in this clip, nested clips included, is driven.</summary>
    public bool IsSymbolic
    {
        get
        {
            if (SymbolicTransform?.IsSymbolic == true || Clip?.IsSymbolic == true)
            {
                return true;
            }

            if (Clips is { } clips)
            {
                foreach (var clip in clips)
                {
                    if (clip.IsSymbolic)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    public ClipPath()
    {
        Clips = new List<PathClip>();
    }

    public ClipPath Clone() => DeepClone(new CloneContext());

    public ClipPath DeepClone() => Clone();

    object ICloneable.Clone() => Clone();

    internal ClipPath DeepClone(CloneContext context)
    {
        if (context.TryGet(this, out ClipPath existing))
        {
            return existing;
        }

        var clone = new ClipPath();
        context.Add(this, clone);

        clone.Clips = CloneHelpers.CloneList(Clips, context, clip => clip.DeepClone(context));
        clone.Transform = Transform;
        clone.Clip = Clip?.DeepClone(context);
        clone.SymbolicTransform = SymbolicTransform;

        return clone;
    }
}
