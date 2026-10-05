// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Svg;
using Svg.Skia;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// The outline of what an element covers on a drawing.
/// </summary>
/// <remarks>
/// The element's own silhouette, not a box around it. A bounding box says where a shape roughly is;
/// on anything that is not a rectangle — a path, a circle, a group of scattered children — it also
/// covers a great deal the shape is not, and two overlapping shapes are ringed identically.
///
/// Here rather than in <see cref="SvgViewer"/> because a viewer is not the only thing that shows a
/// drawing: a host laying several out — a project group's preview — rings an element the same way,
/// and one of these was already enough work to be worth not having twice.
/// </remarks>
public static class SvgViewerOutline
{
    /// <summary>
    /// What <paramref name="element"/> covers on <paramref name="svg"/>, or null where it covers nothing.
    /// </summary>
    /// <remarks>
    /// Every scene node the element has, not the first: one reached through <c>&lt;use&gt;</c> is
    /// drawn once per use, and ringing one of them points at a copy nobody picked.
    ///
    /// In the drawing's own coordinates. A host arranging several offsets the result by wherever it
    /// put this one.
    ///
    /// Usually free: the scene graph a load compiled is the one this reads, so nothing is built for
    /// it. Only after something has thrown that away — binding a value, which rewrites the recorded
    /// drawing rather than compiling one — does the next call pay for a compile.
    /// </remarks>
    public static SkiaSharp.SKPath? Of(SKSvg svg, SvgElement element)
    {
        if (svg is null || element is null)
        {
            return null;
        }

        var outline = new SkiaSharp.SKPath();

        // A mask's content is indexed only once something has been rebuilt, and then under the mask's
        // own transform rather than where it lands, so it is ringed from where it was measured instead.
        if (svg.TryGetRetainedSceneNodes(element, out var scene))
        {
            foreach (var placed in scene.Where(node => !InMask(node)))
            {
                Trace(placed, svg.SkiaModel, outline, under: null);
            }
        }

        // As well as any copy a <use> drew of it, never instead of one: it stands wherever it masks or
        // clips, and that is where it was clicked.
        if (Owner(element) is { })
        {
            using var content = new SkiaSharp.SKPathBuilder(outline);

            foreach (var use in UsesOf(svg, element))
            {
                content.AddPath(use.Outline);
            }

            outline.Dispose();
            outline = content.Detach();
        }

        return outline.IsEmpty ? null : outline;
    }

    /// <summary>The <c>&lt;mask&gt;</c> or <c>&lt;clipPath&gt;</c> <paramref name="element"/> is written inside, if any.</summary>
    /// <remarks>
    /// Such content stands wherever an element using it does, once for each, so where it is taken hold
    /// of is one of those uses rather than any place of its own.
    /// </remarks>
    internal static SvgElement? Owner(SvgElement element)
        => element.Parents.FirstOrDefault(parent => parent is SvgMask or SvgClipPath);

    /// <summary>Where mask or clip content stands, once for each element using it, in the order they are drawn.</summary>
    /// <remarks>The one list a use is counted in, so a click and the handles it gets agree on which use it was.</remarks>
    internal static IEnumerable<SvgViewerUnseen> UsesOf(SKSvg svg, SvgElement element)
        => Unseen(svg).Where(unseen =>
            unseen.Kind is SvgViewerUnseenKind.Mask or SvgViewerUnseenKind.Clip && ReferenceEquals(unseen.Element, element));

    /// <summary>
    /// What the handles of <paramref name="element"/> are drawn round and dragged through: its total
    /// transform, its own included, and its geometry before it. Null where it can have none.
    /// </summary>
    /// <param name="use">
    /// For mask or clip content, which of the elements using it to stand in; the first where there is
    /// no such use.
    /// </param>
    /// <remarks>
    /// Never from mask content's own scene node, whose transform the compile leaves in the masked
    /// element's space and which is indexed a rebuild behind.
    /// </remarks>
    internal static (ShimSkiaSharp.SKMatrix Total, ShimSkiaSharp.SKRect Geometry)? Placement(SKSvg svg, SvgElement element, int use)
    {
        if (Owner(element) is null)
        {
            return svg.TryGetRetainedSceneNodes(element, out var nodes) && nodes.Count > 0
                ? (nodes[0].TotalTransform, nodes[0].GeometryBounds)
                : null;
        }

        var uses = UsesOf(svg, element).ToList();

        return uses.Count == 0 ? null : uses[use >= 0 && use < uses.Count ? use : 0].Placed;
    }

    /// <summary>
    /// Every element whose move moves <paramref name="element"/> as well: its ancestors and, for mask
    /// or clip content held through <paramref name="use"/>, the element using it there and that one's.
    /// </summary>
    internal static IEnumerable<SvgElement> Under(SKSvg svg, SvgElement element, int use)
    {
        var above = element.Parents;

        if (Owner(element) is null)
        {
            return above;
        }

        var uses = UsesOf(svg, element).ToList();

        return uses.Count > 0 && uses[use >= 0 && use < uses.Count ? use : 0].User is { } user
            ? above.Concat(user.Parents).Append(user)
            : above;
    }

    /// <summary>The mask or clip path <paramref name="element"/>'s <paramref name="property"/> applies, if it resolves to one with an id.</summary>
    /// <remarks>
    /// By id within the document only. The <see cref="System.Uri"/> lookup would follow a reference out
    /// to a file or a URL, which a row of the tree has no business opening.
    ///
    /// Only the kind the property takes: a mask named by <c>clip-path</c> is applied by nothing, and a
    /// row saying it was would point at what the renderer ignores.
    /// </remarks>
    internal static SvgElement? Applied(SvgElement element, string property)
        => element.TryGetAttribute(property, out var value) &&
           value.TrimStart().StartsWith("url(", System.StringComparison.OrdinalIgnoreCase) &&
           element.OwnerDocument?.GetElementById(value) is { ID: { Length: > 0 } } applied &&
           (property == "mask" ? applied is SvgMask : applied is SvgClipPath)
            ? applied
            : null;

    /// <summary>Whether <paramref name="node"/> is the content of a mask, compiled for one element it masks.</summary>
    internal static bool InMask(SvgSceneNode node)
    {
        for (var ancestor = node.Parent; ancestor is { }; ancestor = ancestor.Parent)
        {
            if (ancestor.Kind == SvgSceneNodeKind.Mask)
            {
                return true;
            }
        }

        return false;
    }

    // UI thread. Read once per SKSvg, since a commit builds a new one; measured once per scene revision,
    // which a drag and a bound value both move on.
    private static readonly ConditionalWeakTable<SKSvg, StrongBox<bool>> s_unseeable = new();
    private static readonly ConditionalWeakTable<SvgSceneDocument, Measured> s_measured = new();

    private sealed record Measured(long Revision, IReadOnlyList<SvgViewerUnseen> Unseen);

    /// <summary>Everything <paramref name="svg"/> has but does not paint, where each stands in its own units.</summary>
    /// <remarks>
    /// Boxes first, then the rest in the order they are drawn. A mask or a clip is outlined once per
    /// element that uses it, since that is where its content stands, and one nothing uses not at all.
    ///
    /// Asked of the DOM first, so a drawing with nothing of the kind never compiles a scene for this:
    /// binding a value throws the scene away, and a board of drawings would otherwise compile each one
    /// again on the next frame.
    /// </remarks>
    internal static IReadOnlyList<SvgViewerUnseen> Unseen(SKSvg svg)
    {
        var unseeable = s_unseeable.GetValue(svg, drawing => new StrongBox<bool>(
            drawing.SourceDocument is { } document && HasUnseen(document))).Value;

        if (!unseeable || svg.SourceDocument is not { } source || !svg.TryEnsureRetainedSceneGraph(out var scene) || scene is null)
        {
            return System.Array.Empty<SvgViewerUnseen>();
        }

        if (s_measured.TryGetValue(scene, out var measured) && measured.Revision == scene.Revision)
        {
            return measured.Unseen;
        }

        var found = new List<SvgViewerUnseen>();
        var boxed = new HashSet<SvgSceneNode>();

        foreach (var box in SvgSceneBoxes.Measure(source, scene))
        {
            if (box.Node is { } node)
            {
                found.Add(new SvgViewerUnseen(box.Element, SvgViewerUnseenKind.Box, new SkiaSharp.SKPath(), box));
                boxed.Add(node);
            }
        }

        Walk(scene.Root, scene, svg.SkiaModel, boxed, hiddenAbove: false, found, under: null, maskDepth: -1, user: null);

        s_measured.Remove(scene);
        s_measured.Add(scene, new Measured(scene.Revision, found));

        return found;
    }

    // Generous on purpose: a false yes costs one compile, a false no hides something.
    private static bool HasUnseen(SvgDocument document)
        => SvgSceneBoxes.Marked(document).Any() || document.Descendants().Any(element =>
            element is SvgMask or SvgClipPath ||
            element is SvgVisualElement visual &&
            // A line's Fill is null rather than None, and a url() fill may resolve to nothing.
            (visual.Display == "none" || !visual.Visible || visual.Fill is not SvgColourServer || visual.Fill == SvgPaintServer.None));

    /// <param name="under">
    /// Where the node's parent stands, for mask content, whose own TotalTransform the compile leaves in
    /// the masked node's space and a bound value refreshes into the picture's; null to read the node's.
    /// </param>
    /// <param name="maskDepth">How deep inside mask content, from 0 for a mask's own child; -1 outside it.</param>
    /// <param name="user">For mask content, the element the mask is drawn for.</param>
    private static void Walk(
        SvgSceneNode node,
        SvgSceneDocument scene,
        SkiaModel model,
        HashSet<SvgSceneNode> boxed,
        bool hiddenAbove,
        List<SvgViewerUnseen> found,
        ShimSkiaSharp.SKMatrix? under,
        int maskDepth,
        SvgElement? user)
    {
        var total = under is { } parent ? parent.PreConcat(node.Transform) : node.TotalTransform;

        // What an ink click there would pick: the <use> for a copy, so its outline does the same.
        var element = node.HitTestTargetElement ?? node.Element;

        if (maskDepth >= 0)
        {
            // Only a mask's own children are drawn and picked, so their outlines do not stack; one nested
            // deeper is kept for the ring it gets when picked from the tree.
            if (element is { })
            {
                Add(found, element, SvgViewerUnseenKind.Mask, node, model, under, drawn: maskDepth == 0, hidden: false, user);
            }
        }
        else if (node.IsVisible)
        {
            // Visible again under a hidden ancestor, so anything hidden below starts an outline of its own.
            hiddenAbove = false;
        }

        if (maskDepth < 0 && element is { } && !boxed.Contains(node))
        {
            // Not inherited, so the subtree is the node's to outline: every child under it is hidden
            // too, and outlining each again would only stack lines.
            if (node.IsDisplayNone)
            {
                Add(found, element, SvgViewerUnseenKind.Hidden, node, model, under: null, drawn: true, hidden: false);
                return;
            }

            // Inherited, so outlined where it starts, round only what stays hidden under it.
            if (!node.IsVisible)
            {
                if (!hiddenAbove)
                {
                    Add(found, element, SvgViewerUnseenKind.Hidden, node, model, under: null, drawn: true, hidden: true);
                    hiddenAbove = true;
                }
            }
            else if (!node.SupportsFillHitTest && !node.SupportsStrokeHitTest &&
                     (node.HitTestPath is { } || node.Kind == SvgSceneNodeKind.Text && !node.HasLocalVisuals))
            {
                Add(found, element, SvgViewerUnseenKind.Unpainted, node, model, under: null, drawn: true, hidden: false);
            }
        }

        // Drawn inside the masked node's own space.
        if (node.MaskNode is { } mask)
        {
            var masked = total.PreConcat(mask.Transform);

            foreach (var content in mask.Children)
            {
                Walk(content, scene, model, boxed, hiddenAbove: false, found, masked, maskDepth: 0, user: element);
            }
        }

        if (node.ClipPath is { } &&
            node.ClipResourceKey is { } key &&
            scene.TryGetResource(key, out var resource) &&
            resource is { })
        {
            var placement = model.ToSKMatrix(total);

            foreach (var (contentElement, clip) in resource.ClipContent(scene, node))
            {
                if (model.ToSKPath(clip) is not { IsEmpty: false } content)
                {
                    continue;
                }

                content.Transform(placement);

                // A <use> folds its own x, y and transform into the path, so nothing of its own says where
                // the path stands, and it gets no handles.
                (ShimSkiaSharp.SKMatrix, ShimSkiaSharp.SKRect)? placed =
                    contentElement is not SvgUse && clip.Clips is { Count: 1 } clips && clips[0].Path is { } path
                        ? (total.PreConcat(clip.Transform ?? ShimSkiaSharp.SKMatrix.Identity).PreConcat(clips[0].Transform ?? ShimSkiaSharp.SKMatrix.Identity), path.Bounds)
                        : null;

                found.Add(new SvgViewerUnseen(contentElement, SvgViewerUnseenKind.Clip, content, null, Placed: placed, User: element));
            }
        }

        foreach (var child in node.Children)
        {
            Walk(child, scene, model, boxed, hiddenAbove, found, under is null ? null : total, maskDepth < 0 ? -1 : maskDepth + 1, user);
        }
    }

    /// <param name="hidden">Whether to trace only what is hidden, leaving out descendants made visible again.</param>
    private static void Add(
        List<SvgViewerUnseen> found,
        SvgElement element,
        SvgViewerUnseenKind kind,
        SvgSceneNode node,
        SkiaModel model,
        ShimSkiaSharp.SKMatrix? under,
        bool drawn,
        bool hidden,
        SvgElement? user = null)
    {
        var outline = new SkiaSharp.SKPath();

        if (Trace(node, model, outline, under, hidden ? static traced => !traced.IsVisible : null))
        {
            var total = under is { } parent ? parent.PreConcat(node.Transform) : node.TotalTransform;

            found.Add(new SvgViewerUnseen(element, kind, outline, null, drawn, (total, node.GeometryBounds), user));
        }
        else
        {
            outline.Dispose();
        }
    }

    /// <summary>
    /// Adds what <paramref name="node"/> covers to <paramref name="outline"/>, in the drawing's space.
    /// </summary>
    /// <remarks>
    /// Only the seven basic shapes carry geometry — <c>SvgSceneCompiler.TryGetDirectVisualPath</c>
    /// builds one for a path, rect, circle, ellipse, line, polyline and polygon, and for nothing
    /// else. So a container is its children traced one by one, which is what makes a group's ring
    /// its parts rather than the box around them, and a <c>&lt;use&gt;</c> ring the real shape it
    /// was drawn from.
    ///
    /// What has neither geometry nor children — text, an image — falls back to its own bounds. Those
    /// are tight rather than nominal (a text node's are the measured run), so the box is the answer
    /// rather than an approximation of one.
    /// </remarks>
    /// <returns>Whether anything was added.</returns>
    /// <param name="under">
    /// Where the node's parent stands, for content whose <c>TotalTransform</c> cannot be trusted to
    /// say it; null to read the node's own.
    /// </param>
    /// <param name="only">Which nodes to trace, a subtree left out where it says no; null for all of them.</param>
    private static bool Trace(
        SvgSceneNode node,
        SkiaModel model,
        SkiaSharp.SKPath outline,
        ShimSkiaSharp.SKMatrix? under,
        System.Func<SvgSceneNode, bool>? only = null)
    {
        if (only is { } && !only(node))
        {
            return false;
        }

        var total = under is { } parent ? parent.PreConcat(node.Transform) : node.TotalTransform;

        if (node.HitTestPath is { } geometry)
        {
            using var traced = model.ToSKPath(geometry);
            using var drawn = Drawn(node, traced, model);

            var placement = model.ToSKMatrix(total);

            outline.AddPath(drawn ?? traced, ref placement);

            return true;
        }

        var tracedAny = false;

        foreach (var child in node.Children)
        {
            tracedAny |= Trace(child, model, outline, under is null ? null : total, only);
        }

        if (tracedAny)
        {
            return true;
        }

        var covered = under is null ? node.TransformedBounds : total.MapRect(node.GeometryBounds);

        if (covered is { Width: > 0f, Height: > 0f })
        {
            outline.AddRect(model.ToSKRect(covered));

            return true;
        }

        return false;
    }

    /// <summary>
    /// The edges of the band <paramref name="node"/>'s stroke paints, or null when it paints none.
    /// </summary>
    /// <remarks>
    /// A shape's geometry is the line a stroke is drawn <em>along</em>, not what gets drawn. On an
    /// icon made of stroked paths — most of them are — ringing the geometry runs the ring down the
    /// middle of the stroke, which reads as the shape being recoloured rather than outlined, and at
    /// any real stroke width it hides the thing it is pointing at.
    ///
    /// So the stroke is widened into the region it covers, the same way
    /// <c>Svg.Editor.Skia.PathService.OffsetPath</c> does it. A stroke-only shape needs no more: the
    /// two edges of the band and its caps are exactly its outline. One that is filled as well is
    /// unioned with its own geometry, because there the inner edge of the band falls inside the
    /// shape and ringing it would draw a second line through the middle of a filled area.
    ///
    /// <c>node.Stroke</c> can be left unresolved in general, but not here: the payload is
    /// resolved for any node with a <c>HitTestPath</c> (<c>SvgSceneDocument.HasOwnPaintPayload</c>),
    /// and that is the only kind this is called for.
    ///
    /// Not right for <c>vector-effect="non-scaling-stroke"</c>, whose width is in device space while
    /// this widens in the shape's own. The ring is then too thin or too fat by the zoom factor.
    ///
    /// Measured over a group of 500 stroked paths: 7ms to ring them untouched, 16ms widened and
    /// unioned, 42ms widened with round caps. The union is not what costs — the caps are — so it
    /// stays; and the whole of it is inside the 200ms a rebuild is debounced by, which is the only
    /// place this runs other than a click.
    /// </remarks>
    private static SkiaSharp.SKPath? Drawn(SvgSceneNode node, SkiaSharp.SKPath geometry, SkiaModel model)
    {
        if (node.Stroke is not { StrokeWidth: > 0f } stroke)
        {
            return null;
        }

        using var pen = new SkiaSharp.SKPaint
        {
            Style = SkiaSharp.SKPaintStyle.Stroke,
            StrokeWidth = stroke.StrokeWidth,
            StrokeCap = model.ToSKStrokeCap(stroke.StrokeCap),
            StrokeJoin = model.ToSKStrokeJoin(stroke.StrokeJoin),
            StrokeMiter = stroke.StrokeMiter
        };

        using var widened = new SkiaSharp.SKPathBuilder();

        if (!pen.GetFillPath(geometry, widened))
        {
            return null;
        }

        var band = widened.Detach();

        if (!node.SupportsFillHitTest)
        {
            return band;
        }

        using (band)
        {
            return geometry.Op(band, SkiaSharp.SKPathOp.Union);
        }
    }
}

/// <summary>Why something is outlined although it paints nothing.</summary>
internal enum SvgViewerUnseenKind
{
    /// <summary>An <c>e:bounds</c> box the drawing reserves for the code that draws it.</summary>
    Box,

    /// <summary>The content of a <c>&lt;mask&gt;</c>, where it masks one element.</summary>
    Mask,

    /// <summary>The content of a <c>&lt;clipPath&gt;</c>, where it clips one element.</summary>
    Clip,

    /// <summary>A shape with neither a fill nor a stroke.</summary>
    Unpainted,

    /// <summary><c>display="none"</c>, or where <c>visibility</c> turns hidden.</summary>
    Hidden
}

/// <param name="Outline">Where it stands, in the drawing's own units; empty for a box, whose place is read live.</param>
/// <param name="Box">The box, for <see cref="SvgViewerUnseenKind.Box"/>.</param>
/// <param name="Drawn">Whether it is drawn and picked, rather than kept only to ring it when picked elsewhere.</param>
/// <param name="Placed">
/// Its total transform, its own included, and its geometry before it, for handles; null where it can
/// have none.
/// </param>
/// <param name="User">For mask or clip content, the element it masks or clips there.</param>
internal sealed record SvgViewerUnseen(
    SvgElement Element,
    SvgViewerUnseenKind Kind,
    SkiaSharp.SKPath Outline,
    SvgSceneBox? Box,
    bool Drawn = true,
    (ShimSkiaSharp.SKMatrix Total, ShimSkiaSharp.SKRect Geometry)? Placed = null,
    SvgElement? User = null);
