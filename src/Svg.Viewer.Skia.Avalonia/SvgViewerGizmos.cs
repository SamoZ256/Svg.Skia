// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Svg.Editor.Skia;
using Svg.SceneGraph;
using Svg.Skia;
using Svg.SourceEditing;
using Svg.Transforms;
using Shim = ShimSkiaSharp;
using SK = SkiaSharp;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>One element a gesture is to move, as the host that owns the selection knows it.</summary>
/// <param name="Svg">The drawing it is drawn in.</param>
/// <param name="Element">The element itself, in that drawing's live document.</param>
/// <param name="Key">What the host calls it; handed back unread with whatever was written.</param>
/// <param name="At">Where that drawing's own origin sits in the space the gesture is made in.</param>
public readonly record struct SvgViewerGizmoMember(SKSvg Svg, SvgElement Element, string Key, Shim.SKPoint At);

/// <summary>What a finished gesture wants written, for however many elements it moved.</summary>
public readonly record struct SvgViewerEdits(
    string Label,
    IReadOnlyList<(string Key, IReadOnlyList<(string Name, string Value)> Writes)> Members)
{
    /// <summary>Writes every member into one document, stopping at the first refusal.</summary>
    /// <remarks>
    /// One document and one commit, however many elements it carries: a commit hands the edit the
    /// whole document and pushes one state, so a drag of six shapes is one thing to take back and a
    /// refusal on the fourth puts the first three back with it.
    /// </remarks>
    /// <param name="address">What the host calls each member, turned into an address in the file.</param>
    /// <returns>The sentence refusing the edit, or null where it was made.</returns>
    public string? Write(SvgSourceDocument source, Func<string, string?> address)
    {
        foreach (var (key, writes) in Members)
        {
            if (address(key) is not { } at)
            {
                return Unwritten;
            }

            foreach (var (name, value) in writes)
            {
                if (SvgAttributeEditor.SetAttribute(source, at, name, value) is { } refusal)
                {
                    return refusal;
                }
            }
        }

        return null;
    }

    /// <summary>The members whose keys <paramref name="mine"/> claims, or null where none are.</summary>
    /// <remarks>
    /// For a board, where a selection may span several drawings and each is written on its own.
    /// </remarks>
    public SvgViewerEdits? For(Func<string, bool> mine)
    {
        var claimed = Members.Where(member => mine(member.Key)).ToList();

        return claimed.Count == 0 ? null : new SvgViewerEdits(Label, claimed);
    }

    private const string Unwritten = "That element is not written in this drawing's file, so it cannot be dragged.";
}

/// <summary>
/// Moving, turning and scaling a selection of elements by dragging one box round them all.
/// </summary>
/// <remarks>
/// <para>
/// A selection of one is <see cref="SvgViewerGizmo"/> and nothing else: this holds one and hands
/// every call straight to it. That is what keeps a single drag writing exactly what it always wrote,
/// down to the spelling, and it is why the gesture's own arithmetic was not grown a list instead —
/// the pivot means the element's own space there and the shared space here, and one field cannot
/// honestly be both.
/// </para>
/// <para>
/// With several, the gesture is composed once in the space the pointer is in and then carried into
/// each member's own geometry: with <c>F</c> the matrix from a member's geometry to that shared
/// space, the member's own map is <c>F⁻¹ M F</c>. Where that conjugation stays something the file
/// can spell — a translate always, a turn where F is a similarity, a scale where F has no rotation
/// in it — it is written as one, so the file keeps <c>translate(20, 0)</c> rather than a matrix
/// saying the same thing less clearly.
/// </para>
/// </remarks>
public sealed class SvgViewerGizmos
{
    /// <summary>Below this a scale factor is a collapse, and the shape could never be grabbed again.</summary>
    private const float MinimumFactor = 0.01f;

    private readonly SelectionService _selection = new();
    private readonly SvgViewerGizmo _one = new();
    private readonly List<Held> _held = new();

    /// <summary>The one member, where there is one: the inner gizmo does not carry its key.</summary>
    private SvgViewerGizmoMember? _only;

    private bool _dragging;
    private int _handle = -1;
    private Shim.SKPoint _pressed;
    private SK.SKRect _union;
    private Shim.SKPoint _pivot;
    private Shim.SKPoint _centre;

    /// <inheritdoc cref="SvgViewerGizmo.LocksAspect"/>
    public bool LocksAspect
    {
        get => _one.LocksAspect;
        set => _one.LocksAspect = value;
    }

    /// <summary>Whether a drag is in flight.</summary>
    public bool IsDragging => _held.Count > 1 ? _dragging : _one.IsDragging;

    /// <summary>The elements the box is round.</summary>
    public IReadOnlyList<SvgElement> Elements
        => _held.Count > 1
            ? _held.Select(held => (SvgElement)held.Element).ToList()
            : _one.Element is { } only ? new[] { only } : Array.Empty<SvgElement>();

    /// <summary>Follows the selection, and has to be called again after every rebuild.</summary>
    public void Track(IReadOnlyList<SvgViewerGizmoMember> members)
    {
        Cancel();

        _held.Clear();
        _only = null;

        if (members.Count == 1)
        {
            _only = members[0];

            _one.Track(members[0].Svg, members[0].Element);

            return;
        }

        _one.Track(null, null);

        foreach (var member in members)
        {
            if (member.Element is not SvgVisualElement drawn)
            {
                continue;
            }

            var held = new Held(member, drawn);

            held.Resolve();

            if (held.Node is { })
            {
                _held.Add(held);
            }
        }
    }

    /// <summary>The box round the whole selection, or null where there is nothing to draw one on.</summary>
    /// <remarks>
    /// Upright, and measured in the space the drawings are arranged in. Two elements have no frame
    /// in common — a box that leaned would be one member's frame imposed on the rest, and it would
    /// swing the moment the selection changed.
    /// </remarks>
    public BoundsInfo? Box(float scale)
    {
        if (_held.Count <= 1)
        {
            return _one.Box(scale);
        }

        if (Spanned() is not { } union || scale <= 0f)
        {
            return null;
        }

        return _selection.GetBoundsInfo(
            new Shim.SKRect(union.Left, union.Top, union.Right, union.Bottom),
            Shim.SKMatrix.CreateIdentity(),
            () => scale);
    }

    /// <summary>Whether a press belongs to this rather than to a pan or a sweep.</summary>
    /// <remarks>
    /// A handle, or a member's own ink. Not the inside of the box: the one round two shapes at
    /// opposite corners of a drawing covers a great deal of canvas that belongs to nobody.
    /// </remarks>
    public bool Hits(Shim.SKPoint at, float scale)
    {
        if (_held.Count <= 1)
        {
            return _one.Hits(at, scale);
        }

        return (Box(scale) is { } box && _selection.HitHandle(box, new SK.SKPoint(at.X, at.Y), scale, out _) >= 0)
               || _held.Any(held => held.Covers(at));
    }

    /// <summary>Starts a drag, or says why it will not.</summary>
    /// <param name="driven">
    /// What a member's transform says in the file, where an expression writes it, asked by key.
    /// </param>
    public string? Begin(Shim.SKPoint at, float scale, Func<string, string?>? driven = null)
    {
        if (_held.Count <= 1)
        {
            return _one.Begin(at, scale, _only is { } only ? driven?.Invoke(only.Key) : null);
        }

        Cancel();

        if (Box(scale) is not { } box || Spanned() is not { } union)
        {
            return null;
        }

        _handle = _selection.HitHandle(box, new SK.SKPoint(at.X, at.Y), scale, out _);

        if (_handle < 0 && !_held.Any(held => held.Covers(at)))
        {
            return null;
        }

        if (union.Width <= 0f && union.Height <= 0f)
        {
            return Sizeless;
        }

        _union = union;
        _pressed = at;
        _pivot = Pivot();
        _centre = new Shim.SKPoint(union.MidX, union.MidY);

        foreach (var held in _held)
        {
            if (held.Begin(_handle, driven) is { } refusal)
            {
                // Every member or none: a drag that quietly leaves one behind is worse than one
                // that says why nothing moved.
                Revert();

                return refusal;
            }
        }

        _dragging = true;

        return null;
    }

    /// <summary>Moves the gesture on, and redraws every member where it now is.</summary>
    public void Drag(Shim.SKPoint at)
    {
        if (_held.Count <= 1)
        {
            _one.Drag(at);

            return;
        }

        if (!_dragging)
        {
            return;
        }

        var map = Shared(at);

        foreach (var held in _held)
        {
            held.Drag(map, _handle, _pivot, _centre);
        }

        Redraw();
    }

    /// <summary>Ends the drag and hands back what the file should say, or null where nothing moved.</summary>
    public SvgViewerEdits? End()
    {
        if (_held.Count <= 1)
        {
            return _one.End() is { } one && _only is { } only
                ? new SvgViewerEdits(one.Label, new[] { (only.Key, one.Writes) })
                : null;
        }

        if (!_dragging)
        {
            return null;
        }

        _dragging = false;

        var moved = _held
            .Where(held => held.Moved)
            .Select(held => (held.Member.Key, held.Writes!))
            .ToList();

        return moved.Count == 0
            ? null
            : new SvgViewerEdits(
                _handle switch
                {
                    8 => $"rotate {_held.Count} elements",
                    >= 0 => $"scale {_held.Count} elements",
                    _ => $"move {_held.Count} elements"
                },
                moved);
    }

    /// <summary>Puts every member back where the drag found it.</summary>
    public void Cancel()
    {
        if (_held.Count <= 1)
        {
            _one.Cancel();

            return;
        }

        if (!_dragging)
        {
            return;
        }

        _dragging = false;

        Revert();
    }

    /// <inheritdoc cref="SvgViewerGizmo.Revert"/>
    public void Revert()
    {
        if (_held.Count <= 1)
        {
            _one.Revert();

            return;
        }

        foreach (var held in _held)
        {
            held.Revert();
        }

        Redraw();
    }

    /// <summary>The upright rectangle every member's own box falls inside.</summary>
    private SK.SKRect? Spanned()
    {
        SK.SKRect? union = null;

        foreach (var held in _held)
        {
            if (held.Node is not { } node)
            {
                continue;
            }

            var box = SelectionService.GetBoundsRect(_selection.GetBoundsInfo(node, One));

            box.Offset(held.Member.At.X, held.Member.At.Y);

            union = union is { } spanned ? SK.SKRect.Union(spanned, box) : box;
        }

        return union;
    }

    /// <summary>The corner or edge the drag turns about: the one opposite the handle being pulled.</summary>
    private Shim.SKPoint Pivot()
        => new(
            _handle switch { 0 or 6 or 7 => _union.Right, 2 or 3 or 4 => _union.Left, _ => _union.MidX },
            _handle switch { 0 or 1 or 2 => _union.Bottom, 4 or 5 or 6 => _union.Top, _ => _union.MidY });

    /// <summary>The gesture as one map, in the space the pointer is in.</summary>
    private Shim.SKMatrix Shared(Shim.SKPoint now)
    {
        if (_handle == 8)
        {
            return Shim.SKMatrix.CreateRotationDegrees(
                SvgViewerGizmo.Degrees(_centre, now) - SvgViewerGizmo.Degrees(_centre, _pressed),
                _centre.X,
                _centre.Y);
        }

        if (_handle < 0)
        {
            return Shim.SKMatrix.CreateTranslation(now.X - _pressed.X, now.Y - _pressed.Y);
        }

        var wide = _handle is 0 or 2 or 3 or 4 or 6 or 7;
        var tall = _handle is 0 or 1 or 2 or 4 or 5 or 6;

        var x = wide ? SvgViewerGizmo.Factor(now.X - _pivot.X, _pressed.X - _pivot.X) : 1f;
        var y = tall ? SvgViewerGizmo.Factor(now.Y - _pivot.Y, _pressed.Y - _pivot.Y) : 1f;

        if (LocksAspect)
        {
            x = y = wide && tall && Math.Abs(y) > Math.Abs(x) ? y : wide ? x : y;
        }

        return Shim.SKMatrix.CreateScale(x, y, _pivot.X, _pivot.Y);
    }

    /// <summary>Renders every drawing a member moved, once each.</summary>
    private void Redraw()
    {
        foreach (var drawing in _held.Select(held => held.Member.Svg).Distinct())
        {
            var last = _held.Last(held => ReferenceEquals(held.Member.Svg, drawing));

            if (!drawing.TryApplyRetainedSceneMutationAndRender(last.Element, null, out var applied) || applied is null)
            {
                drawing.FromSvgDocument(drawing.SourceDocument);
            }
        }

        foreach (var held in _held)
        {
            held.Resolve();
        }
    }

    private static float One() => 1f;

    private const string Sizeless = "That selection covers nothing, so there is nothing to take hold of.";

    /// <summary>One member of the selection, and everything its own drag needs.</summary>
    private sealed class Held
    {
        public Held(SvgViewerGizmoMember member, SvgVisualElement element)
        {
            Member = member;
            Element = element;
        }

        public SvgViewerGizmoMember Member { get; }

        public SvgVisualElement Element { get; }

        public SvgSceneNode? Node { get; private set; }

        public IReadOnlyList<(string Name, string Value)>? Writes { get; private set; }

        public bool Moved => Writes is { } written && !written.SequenceEqual(_before);

        private Shim.SKMatrix _toGeometry = Shim.SKMatrix.CreateIdentity();
        private Shim.SKMatrix _from = Shim.SKMatrix.CreateIdentity();
        private SvgTransformCollection _restore = new();
        private IReadOnlyList<SvgTransform> _head = Array.Empty<SvgTransform>();
        private GeometryWriter? _writer;
        private string? _written;
        private IReadOnlyList<(string Name, string Value)> _before = Array.Empty<(string, string)>();

        public void Resolve()
            => Node = Member.Svg.TryGetRetainedSceneNodes(Element, out var nodes) && nodes.Count > 0 ? nodes[0] : null;

        /// <summary>Takes hold of this member, or says why the whole gesture will not run.</summary>
        public string? Begin(int handle, Func<string, string?>? driven)
        {
            if (Node is not { } node)
            {
                return Flattened;
            }

            // Where the member's own geometry sits in the space the gesture is made in: its own
            // transform and every ancestor's, and then where its drawing was put on the canvas.
            _from = Shim.SKMatrix.CreateTranslation(Member.At.X, Member.At.Y).PreConcat(node.TotalTransform);

            if (!_from.TryInvert(out var toGeometry))
            {
                return Flattened;
            }

            _toGeometry = toGeometry;
            _restore = SvgViewerGizmo.Clone(Element.Transforms);
            _head = _restore.ToList();
            _written = driven?.Invoke(Member.Key);
            Writes = null;

            _writer = GeometryWriter.Capture(
                Element,
                handle switch
                {
                    8 => GeometryGesture.Turn,
                    >= 0 => GeometryGesture.Scale,
                    _ => GeometryGesture.Move
                });

            _before = _writer is { } writer
                ? writer.Captured
                : new[] { ("transform", _written ?? _restore.ToString()) };

            return null;
        }

        /// <summary>Puts this member through the shared map, in its own geometry.</summary>
        public void Drag(Shim.SKMatrix shared, int handle, Shim.SKPoint pivot, Shim.SKPoint centre)
        {
            var mine = _toGeometry.PreConcat(shared).PreConcat(_from);

            if (_writer is { } writer && writer.Apply(mine) is { } written)
            {
                Element.Transforms = SvgViewerGizmo.Clone(_restore);
                Writes = written;

                return;
            }

            _writer?.Restore();

            var tail = Tail(shared, mine, handle, pivot, centre);
            var composed = new SvgTransformCollection();

            foreach (var transform in _head)
            {
                composed.Add(transform);
            }

            foreach (var transform in tail)
            {
                composed.Add(transform);
            }

            Element.Transforms = composed;

            Writes = new[]
            {
                ("transform", _written is { } spelt
                    ? (spelt + " " + string.Join(" ", tail)).Trim()
                    : composed.ToString())
            };
        }

        public void Revert()
        {
            _writer?.Restore();
            Element.Transforms = _restore;
        }

        /// <summary>
        /// The shared gesture in this member's own geometry, spelt the way the file reads best.
        /// </summary>
        /// <remarks>
        /// A translate survives any conjugation. A turn survives one by a similarity, which is what
        /// an ancestor that only moves, turns and scales evenly leaves. A scale survives one by a
        /// matrix with no rotation in it, because diagonals commute. Anything else is a matrix,
        /// swept of the specks a float sine leaves so a quarter turn does not spell -4.4e-8.
        /// </remarks>
        private IReadOnlyList<SvgTransform> Tail(
            Shim.SKMatrix shared,
            Shim.SKMatrix mine,
            int handle,
            Shim.SKPoint pivot,
            Shim.SKPoint centre)
        {
            if (handle < 0)
            {
                var moved = Vector(shared.TransX, shared.TransY);

                return new SvgTransform[] { new SvgTranslate(Zero(moved.X), Zero(moved.Y)) };
            }

            if (handle == 8 && Similar(_from, out var mirrored))
            {
                var turned = (float)(Math.Atan2(shared.SkewY, shared.ScaleX) * 180d / Math.PI);
                var about = _toGeometry.MapPoint(centre);

                return new SvgTransform[]
                {
                    new SvgRotate(Zero(mirrored ? -turned : turned), Zero(about.X), Zero(about.Y))
                };
            }

            if (handle >= 0 && handle != 8 && _from.SkewX == 0f && _from.SkewY == 0f)
            {
                var about = _toGeometry.MapPoint(pivot);
                var sx = shared.ScaleX;
                var sy = shared.ScaleY;

                return new SvgTransform[]
                {
                    new SvgTranslate(Zero(about.X * (1f - sx)), Zero(about.Y * (1f - sy))),
                    new SvgScale(Zero(sx), Zero(sy))
                };
            }

            var swept = GeometryWriter.Settled(mine);

            return new SvgTransform[]
            {
                new SvgMatrix(new List<float>
                {
                    Zero(swept.ScaleX), Zero(swept.SkewY), Zero(swept.SkewX),
                    Zero(swept.ScaleY), Zero(swept.TransX), Zero(swept.TransY)
                })
            };
        }

        private Shim.SKPoint Vector(float x, float y)
        {
            var origin = _toGeometry.MapPoint(new Shim.SKPoint(0f, 0f));
            var moved = _toGeometry.MapPoint(new Shim.SKPoint(x, y));

            return new Shim.SKPoint(moved.X - origin.X, moved.Y - origin.Y);
        }

        /// <summary>Whether a matrix only turns and scales evenly, and whether it turns the plane over.</summary>
        private static bool Similar(Shim.SKMatrix map, out bool mirrored)
        {
            mirrored = map.ScaleX * map.ScaleY + map.SkewX * map.SkewY < 0f;

            var upright = SvgViewerGizmo.Near(map.ScaleX, map.ScaleY) && SvgViewerGizmo.Near(map.SkewX, -map.SkewY);
            var flipped = SvgViewerGizmo.Near(map.ScaleX, -map.ScaleY) && SvgViewerGizmo.Near(map.SkewX, map.SkewY);

            return upright || flipped;
        }

        /// <summary>Whether this member's own ink is under the pointer.</summary>
        public bool Covers(Shim.SKPoint at)
        {
            var local = new Shim.SKPoint(at.X - Member.At.X, at.Y - Member.At.Y);

            for (var hit = Member.Svg.HitTestTopmostElement(local); hit is { }; hit = hit.Parent)
            {
                if (ReferenceEquals(hit, Element))
                {
                    return true;
                }
            }

            return false;
        }

        private static float Zero(float value) => value == 0f ? 0f : value;

        private const string Flattened =
            "One of those elements is drawn flat, so there is no way back from the pointer to where it is written.";
    }
}
