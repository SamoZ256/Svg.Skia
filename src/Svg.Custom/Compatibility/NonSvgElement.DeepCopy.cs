// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable

namespace Svg;

public partial class NonSvgElement
{
    /// <summary>
    /// Carries the element's namespace across a copy.
    /// </summary>
    /// <remarks>
    /// <see cref="SvgElement.DeepCopy{T}"/> clones through the parameterless constructor, leaving
    /// <c>ElementNamespace</c> at the SVG default while the name survives — so a foreign element
    /// comes back looking like SVG with an unrecognised name, and anything matching on both silently
    /// stops matching. Found through a cloned <c>&lt;e:code&gt;</c> block whose declarations came
    /// back empty.
    /// </remarks>
    public override SvgElement DeepCopy<T>()
    {
        var clone = base.DeepCopy<T>();

        if (clone is NonSvgElement nonSvgElement)
        {
            nonSvgElement.ElementNamespace = ElementNamespace;
        }

        return clone;
    }
}
