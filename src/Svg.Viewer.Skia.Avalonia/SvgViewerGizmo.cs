// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Svg.Editor.Skia;
using Svg.SceneGraph;
using Svg.Skia;
using Svg.Transforms;
using Shim = ShimSkiaSharp;
using SK = SkiaSharp;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>What a finished gesture wants written, and what to call the undo step.</summary>
public readonly record struct SvgViewerEdit(string Label, string Transform);

/// <summary>
/// Moving, rotating and scaling one element by dragging it.
/// </summary>
/// <remarks>
/// The gesture, not the pane: <see cref="SvgViewerCanvas"/> owns the pointer and the view, the host
/// owns the file, and this owns the arithmetic between them. It draws nothing and writes nothing —
/// <see cref="End"/> hands back the attribute value for the host to commit through whatever is
/// holding the drawing.
///
/// Every gesture is composed in the element's own <em>geometry</em> space — what
/// <see cref="SvgSceneNode.GeometryBounds"/> is measured in, before the element's own transform and
/// before every ancestor's. That is the one space in which the answer is a plain
/// <c>translate</c>/<c>rotate</c>/<c>scale</c> rather than a matrix, and it is why the written
/// transform goes on the <em>end</em> of the list: the last entry is the innermost, the one applied
/// to the geometry first. A pointer position is brought into that space by inverting
/// <see cref="SvgSceneNode.TotalTransform"/>, which is what makes a shape inside a scaled
/// <c>&lt;g&gt;</c> follow the pointer one for one instead of by the ancestor's factor.
///
/// While the drag runs, the element is mutated in place and the scene re-rendered through
/// <see cref="SKSvg.TryApplyRetainedSceneMutationAndRender"/> — one element recompiled, not the
/// document reparsed, which is what makes it keep up with a pointer. Nothing is committed until the
/// button comes up, so a drag is one undo step rather than one per frame.
/// </remarks>
public sealed class SvgViewerGizmo
{
    /// <summary>Below this a scale factor is a collapse, and the shape could never be grabbed again.</summary>
    private const float MinimumFactor = 0.01f;

    /// <summary>How close two pivots must be to count as the same one, in geometry units.</summary>
    private const float PivotSlack = 0.001f;

    private readonly SelectionService _selection = new();

    private SKSvg? _svg;
    private SvgVisualElement? _element;
    private SvgSceneNode? _node;

    // Captured at press and held for the length of the drag. The scene node is re-resolved after
    // every mutation, so nothing here may be read back off it.
    private int _handle = -1;
    private bool _dragging;
    private Shim.SKPoint _pressed;
    private Shim.SKRect _geometry;

    /// <summary>
    /// The way back from the drawing to the element's own geometry, as it stood at the press.
    /// </summary>
    /// <remarks>
    /// Held rather than asked of the scene node each frame, and this is the whole of why a drag
    /// tracks the pointer. Every frame mutates the element, so the node's transform by the second
    /// frame already carries the first frame's move — measuring against it would subtract the
    /// gesture from itself, and the shape would crawl along at a fraction of the pointer's speed.
    /// </remarks>
    private Shim.SKMatrix _toGeometry = Shim.SKMatrix.CreateIdentity();

    private SvgTransformCollection _restore = new();
    private IReadOnlyList<SvgTransform> _head = Array.Empty<SvgTransform>();
    private float _startX, _startY;
    private float _startAngle;
    private float _startScaleX = 1f, _startScaleY = 1f;
    private Shim.SKPoint _pivot;

    /// <summary>The element being edited, or null when nothing is.</summary>
    public SvgElement? Element => _element;

    /// <summary>Whether a drag is in flight.</summary>
    public bool IsDragging => _dragging;

    /// <summary>Follows the selection, and has to be called again after every rebuild.</summary>
    /// <remarks>
    /// A rebuild compiles a new <see cref="SKSvg"/>, and the scene nodes the old one handed out
    /// describe a document that is no longer on screen. Nothing here survives one.
    /// </remarks>
    public void Track(SKSvg? svg, SvgElement? element)
    {
        Cancel();

        _svg = svg;
        _element = element as SvgVisualElement;
        _node = null;

        if (_svg is null || _element is null)
        {
            return;
        }

        _node = Resolve();
    }

    /// <summary>The selection box and its handles, or null when there is nothing to draw.</summary>
    /// <param name="scale">The view's scale, which the handles are kept a constant size against.</param>
    public BoundsInfo? Box(float scale)
        => _node is { } node && scale > 0f ? _selection.GetBoundsInfo(node, () => scale) : null;

    /// <summary>Whether a press at <paramref name="at"/> is this gizmo's to answer rather than a pan.</summary>
    public bool Hits(Shim.SKPoint at, float scale)
        => Box(scale) is { } box
           && (_selection.HitHandle(box, new SK.SKPoint(at.X, at.Y), scale, out _) >= 0 || Covers(at));

    /// <summary>
    /// Starts a drag, or says why it will not.
    /// </summary>
    /// <returns>The sentence refusing the gesture, or null where it began.</returns>
    public string? Begin(Shim.SKPoint at, float scale)
    {
        Cancel();

        if (_element is null || _node is not { } node || Box(scale) is not { } box)
        {
            return null;
        }

        // A transform an expression writes is not a number this can add to. Rewriting it as one
        // would draw the right picture once and throw away the thing that made it move.
        if (node.SymbolicTransform is { })
        {
            return Driven;
        }

        if (!node.TotalTransform.TryInvert(out var toGeometry))
        {
            return Flattened;
        }

        _handle = _selection.HitHandle(box, new SK.SKPoint(at.X, at.Y), scale, out _);

        if (_handle < 0 && !Covers(at))
        {
            return null;
        }

        _geometry = node.GeometryBounds;

        if (_geometry.Width <= 0f || _geometry.Height <= 0f)
        {
            return Sizeless;
        }

        _toGeometry = toGeometry;
        _pressed = toGeometry.MapPoint(at);
        _restore = Clone(_element.Transforms);
        _dragging = true;

        Fold();

        return null;
    }

    /// <summary>Moves the gesture on, and redraws the element where it now is.</summary>
    public void Drag(Shim.SKPoint at)
    {
        if (!_dragging || _element is null)
        {
            return;
        }

        var now = _toGeometry.MapPoint(at);

        _element.Transforms = Compose(
            _handle switch
            {
                8 => Rotated(now),
                >= 0 => Scaled(now),
                _ => Moved(now)
            });

        Redraw();
    }

    /// <summary>Ends the drag and hands back what the file should say, or null where nothing moved.</summary>
    public SvgViewerEdit? End()
    {
        if (!_dragging || _element is null)
        {
            return null;
        }

        var written = _element.Transforms?.ToString() ?? string.Empty;
        var before = _restore.ToString();

        _dragging = false;
        _restore = new SvgTransformCollection();

        return string.Equals(written, before, StringComparison.Ordinal)
            ? null
            : new SvgViewerEdit(
                _handle switch { 8 => "rotate an element", >= 0 => "scale an element", _ => "move an element" },
                written);
    }

    /// <summary>Puts the element back where the drag found it.</summary>
    public void Cancel()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;

        if (_element is { })
        {
            _element.Transforms = _restore;
            Redraw();
        }

        _restore = new SvgTransformCollection();
    }

    // ---- the three gestures, all in the element's own geometry space --------------------------

    /// <remarks>
    /// The difference of two mapped points rather than a mapped difference, which comes to the same
    /// thing and needs no vector mapping: the translation in the inverse cancels between them.
    /// </remarks>
    private IReadOnlyList<SvgTransform> Moved(Shim.SKPoint now)
        => new SvgTransform[]
        {
            new SvgTranslate(
                _selection.Snap(_startX + (now.X - _pressed.X)),
                _selection.Snap(_startY + (now.Y - _pressed.Y)))
        };

    /// <remarks>
    /// About the middle of the element's own bounds, and measured in the same space, so what is
    /// written is the angle the file will read rather than the one the screen happened to show. The
    /// two differ only where an ancestor skews or scales the element unevenly, and there no single
    /// <c>rotate()</c> is the answer anyway.
    /// </remarks>
    private IReadOnlyList<SvgTransform> Rotated(Shim.SKPoint now)
    {
        var centre = new Shim.SKPoint(MidX(_geometry), MidY(_geometry));
        var turned = Degrees(centre, now) - Degrees(centre, _pressed);

        return new SvgTransform[] { new SvgRotate(_startAngle + turned, centre.X, centre.Y) };
    }

    /// <remarks>
    /// The handle opposite the one being dragged stays where it is, which is what a scale handle
    /// means. An edge handle moves one axis and leaves the other at its factor, so dragging the side
    /// of a shape does not also stretch it vertically.
    ///
    /// Written as a translate and a scale rather than the usual three, because
    /// <c>translate(p) scale(s) translate(-p)</c> folds exactly to
    /// <c>translate(p(1-s)) scale(s)</c> and a shorter transform is a shorter attribute.
    /// </remarks>
    private IReadOnlyList<SvgTransform> Scaled(Shim.SKPoint now)
    {
        var wide = _handle is 0 or 2 or 3 or 4 or 6 or 7;
        var tall = _handle is 0 or 1 or 2 or 4 or 5 or 6;

        var x = wide ? Factor(now.X - _pivot.X, _pressed.X - _pivot.X) : 1f;
        var y = tall ? Factor(now.Y - _pivot.Y, _pressed.Y - _pivot.Y) : 1f;

        var scaleX = _startScaleX * x;
        var scaleY = _startScaleY * y;

        return new SvgTransform[]
        {
            new SvgTranslate(_pivot.X * (1f - scaleX), _pivot.Y * (1f - scaleY)),
            new SvgScale(scaleX, scaleY)
        };
    }

    /// <remarks><c>ShimSkiaSharp.SKRect</c> carries the four edges and nothing derived from them.</remarks>
    private static float MidX(Shim.SKRect rect) => (rect.Left + rect.Right) / 2f;

    private static float MidY(Shim.SKRect rect) => (rect.Top + rect.Bottom) / 2f;

    private static float Factor(float now, float pressed)
        => Math.Abs(pressed) < MinimumFactor ? 1f : now / pressed;

    private static float Degrees(Shim.SKPoint from, Shim.SKPoint to)
        => (float)(Math.Atan2(to.Y - from.Y, to.X - from.X) * 180d / Math.PI);

    // ---- what the gesture is written onto -----------------------------------------------------

    /// <summary>
    /// Takes the trailing transform this gesture can carry on from, leaving the rest as the head.
    /// </summary>
    /// <remarks>
    /// Without this a second drag would write a second <c>translate</c> beside the first, and a
    /// tenth would write a tenth. The last entry in the list is the one applied to the geometry
    /// first, so it is the only one a gesture composed in geometry space can fold into — and folding
    /// a translate the author wrote is not a liberty, it is the same transform with different
    /// numbers.
    ///
    /// A scale is only carried on from when it turns about the same corner. Re-emitting somebody
    /// else's pivot as this one's would move the shape without anybody dragging it.
    /// </remarks>
    private void Fold()
    {
        var written = _element?.Transforms?.ToList() ?? new List<SvgTransform>();

        _startX = _startY = 0f;
        _startAngle = 0f;
        _startScaleX = _startScaleY = 1f;
        _pivot = Pivot();

        switch (_handle)
        {
            case 8 when Last(written) is SvgRotate turned
                        && Near(turned.CenterX, MidX(_geometry)) && Near(turned.CenterY, MidY(_geometry)):
                _startAngle = turned.Angle;
                written.RemoveAt(written.Count - 1);
                break;

            case >= 0 when written.Count >= 2
                           && Last(written) is SvgScale scaled
                           && written[written.Count - 2] is SvgTranslate about
                           && Near(about.X, _pivot.X * (1f - scaled.X))
                           && Near(about.Y, _pivot.Y * (1f - scaled.Y)):
                _startScaleX = scaled.X;
                _startScaleY = scaled.Y;
                written.RemoveRange(written.Count - 2, 2);
                break;

            case < 0 when Last(written) is SvgTranslate moved:
                _startX = moved.X;
                _startY = moved.Y;
                written.RemoveAt(written.Count - 1);
                break;
        }

        _head = written;
    }

    /// <summary>The corner or edge the drag turns about: the one opposite the handle being pulled.</summary>
    private Shim.SKPoint Pivot()
        => new(
            _handle switch { 0 or 6 or 7 => _geometry.Right, 2 or 3 or 4 => _geometry.Left, _ => MidX(_geometry) },
            _handle switch { 0 or 1 or 2 => _geometry.Bottom, 4 or 5 or 6 => _geometry.Top, _ => MidY(_geometry) });

    private SvgTransformCollection Compose(IReadOnlyList<SvgTransform> tail)
    {
        var composed = new SvgTransformCollection();

        foreach (var transform in _head)
        {
            composed.Add(transform);
        }

        foreach (var transform in tail)
        {
            composed.Add(transform);
        }

        return composed;
    }

    private static SvgTransform? Last(List<SvgTransform> written)
        => written.Count > 0 ? written[written.Count - 1] : null;

    private static bool Near(float a, float b) => Math.Abs(a - b) <= PivotSlack;

    /// <remarks>
    /// Empty rather than null for an element carrying no transform. <c>SvgElement.Transforms</c>'s
    /// setter subscribes to what it is handed without looking, so putting a null back is a crash;
    /// an empty list writes the same identity and says the same nothing.
    /// </remarks>
    private static SvgTransformCollection Clone(SvgTransformCollection? transforms)
    {
        var copy = new SvgTransformCollection();

        if (transforms is null)
        {
            return copy;
        }

        foreach (var transform in transforms)
        {
            copy.Add((SvgTransform)transform.Clone());
        }

        return copy;
    }

    // ---- the drawing -------------------------------------------------------------------------

    /// <summary>Whether the element itself, rather than a handle, is under the point.</summary>
    private bool Covers(Shim.SKPoint at)
        => _svg is { } svg && _element is { } element
           && ReferenceEquals(svg.HitTestTopmostElement(at), element);

    /// <summary>
    /// Redraws the one element that moved.
    /// </summary>
    /// <remarks>
    /// The whole document is recompiled only where the incremental path refuses — a transform that
    /// needs the root rebuilt, which the scene graph decides and this cannot second-guess. Without
    /// the fallback such an element would sit still under the pointer with no word about why.
    /// </remarks>
    private void Redraw()
    {
        if (_svg is not { } svg || _element is null)
        {
            return;
        }

        if (!svg.TryApplyRetainedSceneMutationAndRender(_element, Transform, out var applied) || applied is null)
        {
            svg.FromSvgDocument(svg.SourceDocument);
        }

        _node = Resolve();
    }

    private static readonly string[] Transform = { "transform" };

    /// <remarks>
    /// After a mutation as well as after a load: a recompile replaces the nodes under the root it
    /// rebuilt, so the one held from a moment ago describes a shape that is no longer drawn.
    /// </remarks>
    private SvgSceneNode? Resolve()
        => _svg is { } svg && _element is { } element
           && svg.TryGetRetainedSceneNodes(element, out var nodes) && nodes.Count > 0
            ? nodes[0]
            : null;

    private const string Driven =
        "That element's transform is written by an expression, so dragging it would overwrite what moves it.";

    private const string Flattened =
        "That element is drawn flat, so there is no way back from the pointer to where it is written.";

    private const string Sizeless = "That element covers nothing, so there is nothing to take hold of.";
}
