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
    /// <para>
    /// <see cref="SvgElement.DeepCopy{T}"/> constructs the clone through the parameterless
    /// constructor, which leaves <c>ElementNamespace</c> at its default of the SVG namespace. The
    /// element name does survive, so a copied foreign element comes back looking like an SVG element
    /// that happens to have an unrecognised name — which is worse than losing it, because nothing
    /// reports an error and any code matching on name plus namespace silently stops matching.
    /// </para>
    /// <para>
    /// Found through <c>SvgDocument.ExpressionDeclarations</c>: a cloned document's
    /// <c>&lt;e:code&gt;</c> block kept its name and its attributes but no longer claimed to be in
    /// the expression namespace, so the declarations came back empty. Anything else that copies a
    /// document containing foreign content has the same problem.
    /// </para>
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
