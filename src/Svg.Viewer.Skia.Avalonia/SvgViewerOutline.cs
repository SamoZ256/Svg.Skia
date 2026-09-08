// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using Svg;
using Svg.Expressions;
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
        if (svg is null || element is null || !svg.TryGetRetainedSceneNodes(element, out var scene))
        {
            return null;
        }

        var outline = new SkiaSharp.SKPath();
        var bound = Bound(svg);

        foreach (var placed in scene)
        {
            Trace(placed, svg.SkiaModel, bound, outline);
        }

        return outline.IsEmpty ? null : outline;
    }

    /// <summary>What the drawing is currently bound to, or null while it draws its placeholders.</summary>
    /// <remarks>
    /// The scene holds the matrix the drawing was compiled with, which is the stand-in while a
    /// transform is driven — so a ring around a rotated element would sit where it was written
    /// rather than where it is.
    /// </remarks>
    private static ExprEvaluator? Bound(SKSvg svg)
    {
        if (svg.ExpressionValues is not { } values)
        {
            return null;
        }

        try
        {
            return ExprEvaluator.Create(svg.ExpressionDeclarations, values);
        }
        catch (Exception failure) when (failure is ExprException or ArgumentException)
        {
            return null;
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
    private static bool Trace(SvgSceneNode node, SkiaModel model, ExprEvaluator? bound, SkiaSharp.SKPath outline)
    {
        if (node.HitTestPath is { } geometry)
        {
            using var traced = model.ToSKPath(geometry);
            using var drawn = Drawn(node, traced, model);

            var placement = model.ToSKMatrix(node.TotalTransformWith(bound));

            outline.AddPath(drawn ?? traced, ref placement);

            return true;
        }

        var tracedAny = false;

        foreach (var child in node.Children)
        {
            tracedAny |= Trace(child, model, bound, outline);
        }

        if (tracedAny)
        {
            return true;
        }

        // Mapped rather than read off the node where a value moved it, since TransformedBounds is
        // where the drawing was compiled.
        var covered = bound is { } && node.SymbolicTotalTransform is { }
            ? node.TotalTransformWith(bound).MapRect(node.GeometryBounds)
            : node.TransformedBounds;

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
