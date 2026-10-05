// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Linq;
using ShimSkiaSharp;
using Svg;
using Svg.Expressions;
using Svg.SceneGraph;

namespace Svg.Skia;

/// <summary>A box a drawing reserves for its host, as the generated class will report it.</summary>
/// <param name="Node">
/// What draws it where it stands, whose bounds follow a drag or a bound value; null where
/// <paramref name="Rect"/> is.
/// </param>
/// <param name="Rect">
/// Where the declared defaults put it, in the picture's coordinates; null when the element has no place
/// of its own in the drawing, such as inside a <c>&lt;clipPath&gt;</c>.
/// </param>
/// <param name="Driven">Whether a parameter moves it, so that it stands elsewhere once one is bound.</param>
public sealed record SvgSceneBox(SvgElement Element, string Name, SvgSceneNode? Node, SKRect? Rect, bool Driven);

/// <summary>The <c>e:bounds</c> boxes of a drawing, measured the one way a build and an editor both use.</summary>
public static class SvgSceneBoxes
{
    /// <summary>The marked elements, leaving out those under <c>&lt;defs&gt;</c>.</summary>
    /// <remarks>
    /// A PaintCode symbol arrives there as a copy of another canvas, marks included, and those boxes
    /// belong to that canvas's class.
    /// </remarks>
    public static IEnumerable<(SvgElement Element, string Name)> Marked(SvgDocument document)
    {
        var key = SvgExpressionAttributes.KeyFor(SvgExpressionAttributes.Bounds);

        foreach (var element in document.Descendants())
        {
            if (element.CustomAttributes.TryGetValue(key, out var name) && !element.Parents.OfType<SvgDefinitionList>().Any())
            {
                yield return (element, name);
            }
        }
    }

    /// <summary>Every box of <paramref name="document"/>, measured in <paramref name="scene"/>, which is left as it is.</summary>
    /// <remarks>
    /// Measured at the declared defaults whatever values the scene is bound to, since those are what a
    /// build has. Where a parameter has no default there are none, and a driven box is measured where
    /// the drawing was compiled.
    /// </remarks>
    public static IReadOnlyList<SvgSceneBox> Measure(SvgDocument document, SvgSceneDocument scene)
    {
        var marked = Marked(document).ToList();

        if (marked.Count == 0)
        {
            return Array.Empty<SvgSceneBox>();
        }

        var defaults = Defaults(document);
        var boxes = new List<SvgSceneBox>(marked.Count);

        foreach (var (element, name) in marked)
        {
            if (Placed(scene, element) is not { } node)
            {
                boxes.Add(new SvgSceneBox(element, name, null, null, false));
                continue;
            }

            boxes.Add(new SvgSceneBox(element, name, node, At(node, defaults), Driven(node)));
        }

        return boxes;
    }

    /// <summary>The declared defaults, or null where a parameter has none or a declaration does not evaluate.</summary>
    public static ExprEvaluator? Defaults(SvgDocument document)
    {
        try
        {
            return ExprEvaluator.Create(document.ExpressionDeclarations, null);
        }
        catch (Exception failure) when (failure is ExprException or ArgumentException)
        {
            return null;
        }
    }

    private static SKRect At(SvgSceneNode node, ExprEvaluator? defaults)
    {
        if (node.SymbolicTotalTransform is not { } symbolic)
        {
            return node.TransformedBounds;
        }

        try
        {
            var total = defaults is { } ? SvgSceneSymEvaluator.EvaluateMatrix(symbolic, defaults) : symbolic.Placeholder;

            return total.MapRect(node.GeometryBounds);
        }
        catch (Exception failure) when (failure is ExprException or ArgumentException)
        {
            return node.TransformedBounds;
        }
    }

    /// <summary>The node drawing <paramref name="element"/> where it stands, rather than a copy a <c>&lt;use&gt;</c> placed.</summary>
    /// <remarks>
    /// A <c>&lt;use&gt;</c> compiles its target again under the target's own address, so the element's
    /// address alone can name a copy standing somewhere else. Content only ever drawn as copies, such
    /// as a <c>&lt;symbol&gt;</c>'s, has no such node.
    /// </remarks>
    private static SvgSceneNode? Placed(SvgSceneDocument scene, SvgElement element)
    {
        if (!scene.TryGetNode(element, out var first) || first?.ElementAddressKey is not { } address ||
            !scene.TryGetNodes(address, out var nodes))
        {
            return null;
        }

        return nodes.FirstOrDefault(node => !Ancestors(node).Any(ancestor => ancestor.Kind == SvgSceneNodeKind.Use));
    }

    /// <summary>Whether a parameter moves <paramref name="node"/>, or anything inside it and so its bounds.</summary>
    private static bool Driven(SvgSceneNode node)
        => node.SymbolicTotalTransform is { } || node.Children.Any(child => child.SymbolicTransform is { } || Driven(child));

    private static IEnumerable<SvgSceneNode> Ancestors(SvgSceneNode node)
    {
        for (var ancestor = node.Parent; ancestor is { }; ancestor = ancestor.Parent)
        {
            yield return ancestor;
        }
    }
}
