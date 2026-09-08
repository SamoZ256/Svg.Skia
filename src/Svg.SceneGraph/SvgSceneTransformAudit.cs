// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace Svg.Skia;

/// <summary>
/// Whether a driven transform can be bound at all, or was measured against while it was baked.
/// </summary>
/// <remarks>
/// Asked of the compiled scene rather than the document, because every question here is one
/// compilation already answered: which nodes open a layer, which carry a filter, and where a
/// <c>&lt;use&gt;</c> put them. Which cases are sound is documented in svg-expressions.md.
/// </remarks>
public static class SvgSceneTransformAudit
{
    /// <summary>Why a driven transform here cannot be bound, or null when every one of them can.</summary>
    public static string? WhyUnsound(SvgSceneDocument? sceneDocument)
    {
        if (sceneDocument is null)
        {
            return null;
        }

        // A <clipPath>'s contents compile to geometry and are never recorded as commands, so an
        // expression written on one reaches no node at all -- which is why this half is asked of the
        // document and the rest of the scene.
        var driven = false;

        if (sceneDocument.SourceDocument is { } document)
        {
            foreach (var element in document.Descendants())
            {
                if (SvgExpressionAttributes.Lifted(element.CustomAttributes, SvgTransformExpression.Name) is null)
                {
                    continue;
                }

                driven = true;

                if (element.Parents.OfType<SvgClipPath>().Any())
                {
                    return Refusal(
                        element,
                        "is inside a <clipPath>, whose contents are compiled to geometry rather than recorded, so a bound value has nothing left to move");
                }

                // Either a driven transform moves, or it is named here. Text, an element that also
                // declares transform-origin, and a function or argument count the model has no case
                // for all compile to a node still holding the stand-in, and would otherwise bind to
                // nothing at all with nothing said.
                if (sceneDocument.TryGetNode(element, out var compiled) &&
                    compiled is { SymbolicTransform: null })
                {
                    return Refusal(element, "was compiled without it -- <text>, an element that also declares transform-origin, and a function or argument count SVG does not allow all keep the value they were compiled with");
                }
            }
        }

        if (!driven)
        {
            return null;
        }

        foreach (var node in sceneDocument.Traverse())
        {
            if (node.SymbolicTransform is null || node.Element is not { } element)
            {
                continue;
            }

            // The filter region, the blur radius it decomposes, and the inverse the renderer records
            // as a delta are all taken from the total transform as it was compiled.
            if (node.Filter is { })
            {
                return Refusal(element, "is measured against a filter on the same element");
            }

            for (var ancestor = node.Parent; ancestor is { }; ancestor = ancestor.Parent)
            {
                if (ancestor.Filter is { })
                {
                    return Refusal(element, $"is measured against a filter on {Describe(ancestor)}");
                }

                // A layer's bounds are unioned from its children through their own transforms, and
                // SaveLayer clips to them, so a child that moves is cut at the edge it had when the
                // drawing was compiled.
                if (Layers(ancestor))
                {
                    return Refusal(element, $"sits inside the layer opened by {Describe(ancestor)}, whose bounds were measured from where it was compiled");
                }
            }

            if (Filtered(node) is { } filtered)
            {
                return Refusal(element, $"is measured against a filter on {Describe(filtered)}, below it");
            }
        }

        return null;
    }

    private static bool Layers(SvgSceneNode node)
        => node.Opacity is { } || node.MaskNode is { } || node.BlendModePaint is { } || node.IsIsolationGroup;

    /// <summary>The first node below <paramref name="node"/> carrying a filter, or null.</summary>
    private static SvgSceneNode? Filtered(SvgSceneNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.Filter is { })
            {
                return child;
            }

            if (Filtered(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string Refusal(SvgElement element, string because)
        => $"the transform on {Describe(element)} {because}, so a bound value cannot move it. Write the transform as a literal, or move what measures it off the path between them.";

    private static string Describe(SvgSceneNode node)
        => node.Element is { } element ? Describe(element) : "an unnamed group";

    private static string Describe(SvgElement element)
        => string.IsNullOrEmpty(element.ID)
            ? $"<{element.ElementName}>"
            : $"<{element.ElementName} id='{element.ID}'>";
}
