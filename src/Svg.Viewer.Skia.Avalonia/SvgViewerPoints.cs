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
/// <param name="Chosen">The chosen points, drawn filled: square where one is an anchor, round where it is a handle.</param>
public sealed record SvgViewerPointMarks(
    IReadOnlyList<SK.SKPoint> Anchors,
    IReadOnlyList<SK.SKPoint> Handles,
    IReadOnlyList<(SK.SKPoint From, SK.SKPoint To)> Stalks,
    IReadOnlyList<(SK.SKPoint At, bool Anchor)> Chosen);

/// <summary>
/// Reshaping one path, polygon, polyline or line by its points: the gesture, and what it writes.
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

    /// <summary>The points as the drag in flight has them, read off the element after every frame.</summary>
    /// <remarks>
    /// Beside the ones taken at the press rather than over them: every frame is moved from where the
    /// press found the shape, and these are only what is drawn and hit until the commit reads it again.
    /// </remarks>
    private GeometryPoints? _moving;
    private Shim.SKMatrix _from = Shim.SKMatrix.CreateIdentity();
    private Shim.SKMatrix _to = Shim.SKMatrix.CreateIdentity();
    private List<int> _chosen = new();

    /// <summary>What a sweep being drawn would choose, drawn in place of what is chosen until it is let go.</summary>
    private List<int>? _sweeping;

    /// <summary>The point the press took hold of, which the snap and Shift's eight directions are for.</summary>
    private int _grabbed = -1;

    /// <summary>Whether the press added its point with Shift, so the click that may follow does not take it straight back out.</summary>
    private bool _added;
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

    /// <summary>The chosen points, by index.</summary>
    /// <remarks>Settable so a host that rebuilds its selection from scratch can put them back.</remarks>
    public IReadOnlyList<int> Chosen
    {
        get => _chosen.ToArray();
        set => _chosen = _points is { } points ? value.Where(i => i >= 0 && i < points.Points.Count).Distinct().ToList() : new List<int>();
    }

    /// <summary>Follows the selection, and has to be called again after every rebuild.</summary>
    /// <remarks>
    /// A <c>&lt;use&gt;</c> is reshaped by the shape it draws, where it draws it, so what is written is
    /// that shape, under its own address rather than the key the host handed in. The chosen point is
    /// kept while that is the same one, so it outlives the rebuild a commit makes.
    /// </remarks>
    public void Track(SKSvg? svg, Shim.SKPoint at, SvgViewerGizmoMember? member)
    {
        Cancel();

        var shape = svg is { } && member is { } held ? SvgViewerOutline.Shape(svg, held.Element, held.Use) : null;

        if (shape is { } drawn && member is { } picked && !ReferenceEquals(drawn.Shape, picked.Element))
        {
            member = new SvgViewerGizmoMember(drawn.Shape, SvgElementAddress.Create(drawn.Shape).Key, picked.Use);
        }

        var same = member is { } now && _member is { } was && now.Key == was.Key;

        _svg = svg;
        _at = at;
        _member = member;
        _points = null;
        _moving = null;
        _inside = _grid.From(at.X, at.Y);

        if (shape is { } found && found.Placed.Total.TryInvert(out var to))
        {
            _points = GeometryPoints.Capture(found.Shape);
            _from = found.Placed.Total;
            _to = to;
        }

        Chosen = same ? _chosen : Array.Empty<int>();
        _sweeping = null;
        _clicked = same ? _clicked : null;
    }

    /// <summary>Lets go of the chosen points.</summary>
    public void Deselect()
    {
        _chosen.Clear();
        _sweeping = null;
        _clicked = null;
    }

    /// <summary>Chooses every anchor of the shape.</summary>
    public void ChooseAll()
    {
        if (_points is { } points)
        {
            _chosen = Enumerable.Range(0, points.Points.Count).Where(i => points.Points[i].Kind == GeometryPointKind.Anchor).ToList();
        }
    }

    /// <summary>Chooses the anchors a swept rectangle lies round, or adds them to what is chosen while <paramref name="adding"/>.</summary>
    /// <param name="swept">The rectangle, in the space the drawings are arranged in; null for a sweep taken back.</param>
    /// <param name="final">Whether the sweep has been let go of, rather than still being drawn.</param>
    public void Sweep(SK.SKRect? swept, bool adding, bool final)
    {
        _sweeping = null;

        if (swept is not { } rectangle || _points is not { } points)
        {
            return;
        }

        var caught = Enumerable.Range(0, points.Points.Count)
            .Where(i => points.Points[i].Kind == GeometryPointKind.Anchor && rectangle.Contains(Arranged(points.Points[i].At)));
        var chosen = (adding ? _chosen.Union(caught) : caught).ToList();

        if (final)
        {
            _chosen = chosen;
            _clicked = null;
        }
        else
        {
            _sweeping = chosen;
        }
    }

    /// <summary>The marks to draw, or null where there is no shape.</summary>
    public SvgViewerPointMarks? Marks()
    {
        if (Showing is not { } points)
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

        var chosen = _sweeping ?? _chosen;

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
            chosen.Select(i => (Arranged(points.Points[i].At), points.Points[i].Kind == GeometryPointKind.Anchor)).ToList());
    }

    /// <summary>Whether a press at <paramref name="at"/> takes hold of a point.</summary>
    public bool Hits(Shim.SKPoint at, float scale) => Hit(at, scale) >= 0;

    /// <summary>Whether <paramref name="at"/> is on a point of the shape or on its outline between them.</summary>
    public bool Near(Shim.SKPoint at, float scale) => Hit(at, scale) >= 0 || Segment(at, scale) is { };

    /// <summary>Chooses the point under the press and takes hold of what is chosen, or says why it will not.</summary>
    /// <param name="adding">
    /// Shift: a point not chosen is added to the rest. A chosen one stays chosen here and is let go
    /// of by <see cref="Click"/>, only if the press turns out to be a click — Shift held from the
    /// start of a drag is what holds it to eight directions. Without Shift a press on a chosen point
    /// drags them all, and on any other point chooses that one alone.
    /// </param>
    /// <returns>The sentence refusing the gesture, or null where it began or there was nothing to begin.</returns>
    public string? Begin(Shim.SKPoint at, float scale, bool adding = false)
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

        _clicked = null;
        _added = adding && !_chosen.Contains(hit);

        if (!_chosen.Contains(hit))
        {
            if (!adding)
            {
                _chosen.Clear();
            }

            _chosen.Add(hit);
        }

        _grabbed = hit;
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

        var start = Drawn(points.Points[_grabbed].At);
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
        if (new PointF(local.X, local.Y) == points.Points[_grabbed].At)
        {
            Revert();

            return;
        }

        _writes = points.Move(_chosen, _grabbed, new PointF(local.X, local.Y));

        // Read again only where it lists the same points, which a drag that joins the last point to
        // the first does not; the marks then stay where the press left them until the commit.
        _moving = GeometryPoints.Capture(_member!.Value.Element) is { } read && read.Points.Count == points.Points.Count
            ? read
            : null;

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

        var label = _chosen.Count > 1
            ? $"move {_chosen.Count} points"
            : points.Points[_grabbed].Kind == GeometryPointKind.Anchor ? "move a point" : "move a handle";

        return Edit(label, writes);
    }

    /// <summary>Drops a drag in flight, putting the shape back; the chosen points stay chosen.</summary>
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
        _moving = null;

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
    /// <param name="adding">Shift, which keeps what is chosen where a click would otherwise let go of it.</param>
    public bool Click(Shim.SKPoint at, float scale, int clicks, bool adding, out SvgViewerEdits? edit, out string? refusal)
    {
        edit = null;
        refusal = null;

        if (_points is not { } points)
        {
            return false;
        }

        // A click on a point of several chosen is choosing that one: the press kept them all in case
        // it was the start of a drag, and it was not.
        if (Hit(at, scale) is var hit and >= 0)
        {
            if (!adding)
            {
                _chosen = new List<int> { hit };
            }
            else if (!_added)
            {
                _chosen.Remove(hit);
            }

            _added = false;

            return true;
        }

        if (Segment(at, scale) is not { } segment)
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
            _chosen = new List<int> { anchor };
            _writes = written;
            edit = Edit("add a point", written!);

            return true;
        }

        if (!adding)
        {
            _chosen.Clear();
        }

        _clicked = segment.Segment;

        return true;
    }

    /// <summary>
    /// What deleting the chosen points writes: the anchors among them taken out, or, where only handles
    /// are chosen, those handles drawn back into their anchors.
    /// </summary>
    /// <returns>Whether there was anything chosen to delete; the edit or the refusal is in the out parameters.</returns>
    public bool Remove(out SvgViewerEdits? edit, out string? refusal)
    {
        edit = null;
        refusal = null;

        if (_points is not { } points || _chosen.Count == 0)
        {
            return false;
        }

        if ((refusal = Unwritable()) is { })
        {
            return true;
        }

        var anchors = _chosen.Count(i => points.Points[i].Kind == GeometryPointKind.Anchor);

        if (anchors == 0)
        {
            _writes = points.Retract(_chosen);
            edit = Edit(_chosen.Count == 1 ? "retract a handle" : $"retract {_chosen.Count} handles", _writes);
            _chosen = _chosen.Select(i => points.Points[i].Anchor).Distinct().ToList();

            return true;
        }

        if ((refusal = points.Remove(_chosen, out var written)) is null)
        {
            _writes = written;
            edit = Edit(anchors == 1 ? "delete a point" : $"delete {anchors} points", written!);
            _chosen.Clear();
        }

        return true;
    }

    // ---- inside -------------------------------------------------------------------------------

    /// <summary>The handles worth drawing: those either side of every chosen point that stand off their anchor.</summary>
    private IEnumerable<int> Shown()
    {
        if (Showing is not { } points)
        {
            return Array.Empty<int>();
        }

        return (_sweeping ?? _chosen).SelectMany(points.Around).Distinct().Where(i =>
        {
            var handle = points.Points[i];

            return handle.At != points.Points[handle.Anchor].At
                   && (handle.Other < 0 || handle.At != points.Points[handle.Other].At);
        });
    }

    /// <summary>The point a press takes hold of: an anchor before a handle, so a handle lying on its anchor never hides it.</summary>
    private int Hit(Shim.SKPoint at, float scale)
    {
        if (Showing is not { } points || scale <= 0f)
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

    /// <summary>The segment of the outline <paramref name="at"/> is on, and how far along it.</summary>
    private (int Segment, float T)? Segment(Shim.SKPoint at, float scale)
    {
        if (_points is not { } points)
        {
            return null;
        }

        var inside = Inside(at);

        return points.Nearest(new PointF(inside.X * scale, inside.Y * scale), point => Screen(point, scale), NearPixels);
    }

    private GeometryPoints? Showing => _moving ?? _points;

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
