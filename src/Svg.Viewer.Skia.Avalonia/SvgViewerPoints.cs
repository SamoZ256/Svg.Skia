// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Svg.Editor.Skia;
using Svg.Skia;
using Shim = ShimSkiaSharp;
using SK = SkiaSharp;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>The points of the shape being reshaped, as the canvas draws them, in the space the drawings are arranged in.</summary>
/// <param name="Chosen">Where the chosen point is, drawn filled; null while none is.</param>
/// <param name="ChosenIsAnchor">Whether the chosen point is an anchor, drawn square, rather than a handle, drawn round.</param>
public sealed record SvgViewerPointMarks(
    IReadOnlyList<SK.SKPoint> Anchors,
    IReadOnlyList<SK.SKPoint> Handles,
    IReadOnlyList<(SK.SKPoint From, SK.SKPoint To)> Stalks,
    SK.SKPoint? Chosen,
    bool ChosenIsAnchor);

/// <summary>
/// Reshaping one path, polygon, polyline or line a point at a time: the gesture, and what it writes.
/// </summary>
/// <remarks>
/// In the role of <see cref="SvgViewerGizmo"/>: it moves the built element as the hand moves so the
/// drawing follows, and hands the host one <see cref="SvgViewerEdits"/> to commit when it is let go
/// of. What a point is and what moving one writes is <see cref="GeometryPoints"/>'s. Everything a
/// host says and hears is in the space the drawings are arranged in, as with
/// <see cref="SvgViewerGizmos"/>, and the placement is taken off inside.
/// </remarks>
public sealed class SvgViewerPoints
{
    /// <summary>How near a click has to come to the outline to mean it, on screen.</summary>
    private const float NearPixels = 6f;

    private SKSvg? _svg;
    private SvgViewerGizmoMember? _member;
    private Shim.SKPoint _at;
    private GeometryPoints? _points;
    private Shim.SKMatrix _from = Shim.SKMatrix.CreateIdentity();
    private Shim.SKMatrix _to = Shim.SKMatrix.CreateIdentity();
    private int _chosen = -1;
    private bool _dragging;
    private Shim.SKPoint _pressed;
    private IReadOnlyList<(string Name, string Value)>? _writes;
    private SvgViewerGrid _grid = SvgViewerGrid.None;
    private SvgViewerGrid _inside = SvgViewerGrid.None;

    /// <summary>The segment the last click landed on, so the second click of a double-click adds a point to it and to nothing else.</summary>
    private int? _clicked;

    /// <summary>Whether the shape's points are written as an expression, asked by key and attribute.</summary>
    /// <remarks>
    /// The built document holds the number the expression came to, and writing that back would
    /// replace the expression with it. Geometry does not take expressions today; this is what
    /// stops one being flattened the day it does.
    /// </remarks>
    public Func<string, string, bool>? Driven { get; set; }

    /// <inheritdoc cref="SvgViewerGizmos.Grid"/>
    public SvgViewerGrid Grid
    {
        get => _grid;
        set
        {
            _grid = value;
            _inside = value.From(_at.X, _at.Y);
        }
    }

    /// <summary>Whether there is a shape whose points are showing.</summary>
    public bool IsShowing => _points is { };

    /// <summary>Whether a point is being dragged.</summary>
    public bool IsDragging => _dragging;

    /// <summary>The chosen point, by index, or -1.</summary>
    /// <remarks>Settable so a host that rebuilds its selection from scratch can put it back.</remarks>
    public int Chosen
    {
        get => _chosen;
        set => _chosen = _points is { } points && value >= 0 && value < points.Points.Count ? value : -1;
    }

    /// <summary>Whether <paramref name="element"/> has points this can take hold of.</summary>
    public static bool Reshapes(SvgElement? element) => element is { } && GeometryPoints.Capture(element) is { };

    /// <summary>Follows the selection, and has to be called again after every rebuild.</summary>
    /// <remarks>The chosen point is kept while the element is the same one, by key, so it outlives the rebuild a commit makes.</remarks>
    public void Track(SKSvg? svg, Shim.SKPoint at, SvgViewerGizmoMember? member)
    {
        Cancel();

        var same = member is { } now && _member is { } was && now.Key == was.Key;

        _svg = svg;
        _at = at;
        _member = member;
        _points = null;
        _inside = _grid.From(at.X, at.Y);

        if (svg is { } && member is { } held
            && SvgViewerOutline.Placement(svg, held.Element, held.Use) is { } placed
            && placed.Total.TryInvert(out var to))
        {
            _points = GeometryPoints.Capture(held.Element);
            _from = placed.Total;
            _to = to;
        }

        Chosen = same ? _chosen : -1;
        _clicked = same ? _clicked : null;
    }

    /// <summary>Lets go of the chosen point.</summary>
    public void Deselect()
    {
        _chosen = -1;
        _clicked = null;
    }

    /// <summary>The marks to draw, or null where there is no shape.</summary>
    public SvgViewerPointMarks? Marks()
    {
        if (_points is not { } points)
        {
            return null;
        }

        var anchors = new List<SK.SKPoint>();
        var handles = new List<SK.SKPoint>();
        var stalks = new List<(SK.SKPoint, SK.SKPoint)>();

        for (var i = 0; i < points.Points.Count; i++)
        {
            if (points.Points[i].Kind == GeometryPointKind.Anchor)
            {
                anchors.Add(Arranged(points.Points[i].At));
            }
        }

        foreach (var i in Shown())
        {
            var handle = points.Points[i];

            handles.Add(Arranged(handle.At));
            stalks.Add((Arranged(points.Points[handle.Anchor].At), Arranged(handle.At)));

            if (handle.Other >= 0)
            {
                stalks.Add((Arranged(points.Points[handle.Other].At), Arranged(handle.At)));
            }
        }

        return new SvgViewerPointMarks(
            anchors,
            handles,
            stalks,
            _chosen >= 0 ? Arranged(points.Points[_chosen].At) : null,
            _chosen >= 0 && points.Points[_chosen].Kind == GeometryPointKind.Anchor);
    }

    /// <summary>Whether a press at <paramref name="at"/> takes hold of a point.</summary>
    public bool Hits(Shim.SKPoint at, float scale) => Hit(at, scale) >= 0;

    /// <summary>Chooses the point under the press and takes hold of it, or says why it will not.</summary>
    /// <returns>The sentence refusing the gesture, or null where it began or there was nothing to begin.</returns>
    public string? Begin(Shim.SKPoint at, float scale)
    {
        Cancel();

        var hit = Hit(at, scale);

        if (hit < 0)
        {
            return null;
        }

        if (Unwritable() is { } refusal)
        {
            return refusal;
        }

        _chosen = hit;
        _clicked = null;
        _pressed = Inside(at);
        _writes = null;
        _dragging = true;

        return null;
    }

    /// <summary>Puts the chosen point under the pointer, on the grid, and along one of eight directions while <paramref name="square"/>.</summary>
    public void Drag(Shim.SKPoint at, bool square)
    {
        if (!_dragging || _points is not { } points)
        {
            return;
        }

        var start = Drawn(points.Points[_chosen].At);
        var now = Inside(at);
        var moved = new Shim.SKPoint(start.X + (now.X - _pressed.X), start.Y + (now.Y - _pressed.Y));

        if (square)
        {
            var (x, y) = SvgViewerDraw.Eighth(start, moved);

            moved = new Shim.SKPoint(x, y);
        }

        // The point is snapped, not the pointer: the hand took hold of it a little way off its centre.
        moved = new Shim.SKPoint(_inside.PullX(moved.X), _inside.PullY(moved.Y));

        var local = _to.MapPoint(moved);

        // Back where it started is no edit at all, rather than the same numbers written in a new spelling.
        if (new PointF(local.X, local.Y) == points.Points[_chosen].At)
        {
            Revert();

            return;
        }

        _writes = points.Move(_chosen, new PointF(local.X, local.Y));

        Redraw();
    }

    /// <summary>Lets go, and says what to write, or null where the point is where it was.</summary>
    public SvgViewerEdits? End()
    {
        if (!_dragging || _points is not { } points)
        {
            return null;
        }

        _dragging = false;

        if (_writes is not { } writes)
        {
            return null;
        }

        return Edit(points.Points[_chosen].Kind == GeometryPointKind.Anchor ? "move a point" : "move a handle", writes);
    }

    /// <summary>Drops a drag in flight, putting the shape back; the chosen point stays chosen.</summary>
    public void Cancel()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;

        Revert();
    }

    /// <summary>Puts the shape back as it was taken hold of, for an edit the file would not take.</summary>
    /// <remarks>Whatever the edit was: a point added or removed is applied to the element too, ahead of its commit.</remarks>
    public void Revert()
    {
        if (_points is null || _writes is null)
        {
            return;
        }

        _points.Restore();
        _writes = null;

        Redraw();
    }

    /// <summary>
    /// Answers a click that a press on a point or on the outline did not turn into a drag.
    /// </summary>
    /// <param name="clicks">Which click of a run this is; the second on a segment the first landed on adds a point there.</param>
    /// <param name="edit">The point to add, for the host to write.</param>
    /// <returns>
    /// Whether the click was the shape's — on a point, or on its outline — and so not one for picking
    /// whatever is drawn under it. The sentence refusing an edit is in <paramref name="refusal"/>.
    /// </returns>
    public bool Click(Shim.SKPoint at, float scale, int clicks, out SvgViewerEdits? edit, out string? refusal)
    {
        edit = null;
        refusal = null;

        if (_points is not { } points)
        {
            return false;
        }

        if (Hit(at, scale) >= 0)
        {
            return true;
        }

        var inside = Inside(at);
        var near = points.Nearest(
            new PointF(inside.X * scale, inside.Y * scale),
            point => Screen(point, scale),
            NearPixels);

        if (near is not { } segment)
        {
            _clicked = null;

            return false;
        }

        if (clicks >= 2 && _clicked == segment.Segment)
        {
            _clicked = null;

            if ((refusal = Unwritable()) is { }
                || (refusal = points.Insert(segment.Segment, segment.T, out var written, out var anchor)) is { })
            {
                return true;
            }

            // Chosen before the commit, which rebuilds the drawing and keeps the index.
            _chosen = anchor;
            _writes = written;
            edit = Edit("add a point", written!);

            return true;
        }

        _chosen = -1;
        _clicked = segment.Segment;

        return true;
    }

    /// <summary>What deleting the chosen point writes: the point taken out, or a handle drawn back into its anchor.</summary>
    /// <returns>Whether there was a chosen point to delete; the edit or the refusal is in the out parameters.</returns>
    public bool Remove(out SvgViewerEdits? edit, out string? refusal)
    {
        edit = null;
        refusal = null;

        if (_points is not { } points || _chosen < 0)
        {
            return false;
        }

        if ((refusal = Unwritable()) is { })
        {
            return true;
        }

        var chosen = points.Points[_chosen];

        if (chosen.Kind == GeometryPointKind.Handle)
        {
            _writes = points.Move(_chosen, points.Points[chosen.Anchor].At, smooth: false);
            edit = Edit("retract a handle", _writes);
            _chosen = chosen.Anchor;

            return true;
        }

        if ((refusal = points.Remove(_chosen, out var written)) is null)
        {
            _writes = written;
            edit = Edit("delete a point", written!);
            _chosen = -1;
        }

        return true;
    }

    // ---- inside -------------------------------------------------------------------------------

    /// <summary>The handles worth drawing: those either side of the chosen point that stand off their anchor.</summary>
    private IEnumerable<int> Shown()
    {
        if (_points is not { } points || _chosen < 0)
        {
            return Array.Empty<int>();
        }

        return points.Around(_chosen).Where(i =>
        {
            var handle = points.Points[i];

            return handle.At != points.Points[handle.Anchor].At
                   && (handle.Other < 0 || handle.At != points.Points[handle.Other].At);
        });
    }

    /// <summary>The point a press takes hold of: an anchor before a handle, so a handle lying on its anchor never hides it.</summary>
    private int Hit(Shim.SKPoint at, float scale)
    {
        if (_points is not { } points || scale <= 0f)
        {
            return -1;
        }

        var inside = Inside(at);
        var reach = SelectionService.HandleSize / 2f / scale;
        var candidates = Enumerable.Range(0, points.Points.Count)
            .Where(i => points.Points[i].Kind == GeometryPointKind.Anchor)
            .Concat(Shown());

        foreach (var i in candidates)
        {
            var point = Drawn(points.Points[i].At);

            if (Math.Abs(point.X - inside.X) <= reach && Math.Abs(point.Y - inside.Y) <= reach)
            {
                return i;
            }
        }

        return -1;
    }

    private string? Unwritable()
        => _member is { } member && _points is { } points && Driven is { } driven && points.Names.Any(name => driven(member.Key, name))
            ? "That shape's points are written as an expression, so they cannot be edited here."
            : null;

    private SvgViewerEdits Edit(string label, IReadOnlyList<(string Name, string Value)> writes)
        => new(label, new[] { (_member!.Value.Key, writes) });

    /// <summary>Recompiles the one element the points belong to, and draws the frame.</summary>
    private void Redraw()
    {
        if (_svg is not { } svg || _member is not { } member || _points is not { } points)
        {
            return;
        }

        if (!svg.TryApplyRetainedSceneMutationAndRender(member.Element, points.Names.ToArray(), out var applied) || applied is null)
        {
            svg.FromSvgDocument(svg.SourceDocument);
        }
    }

    private Shim.SKPoint Inside(Shim.SKPoint at) => new(at.X - _at.X, at.Y - _at.Y);

    private Shim.SKPoint Drawn(PointF local) => _from.MapPoint(new Shim.SKPoint(local.X, local.Y));

    private SK.SKPoint Arranged(PointF local)
    {
        var drawn = Drawn(local);

        return new SK.SKPoint(drawn.X + _at.X, drawn.Y + _at.Y);
    }

    private PointF Screen(PointF local, float scale)
    {
        var drawn = Drawn(local);

        return new PointF(drawn.X * scale, drawn.Y * scale);
    }
}
