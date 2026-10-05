// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Svg.Pathing;

namespace Svg.Editor.Skia;

/// <summary>Whether a point lies on the shape or is one of a curve's handles.</summary>
public enum GeometryPointKind
{
    Anchor,
    Handle
}

/// <summary>One point of a shape that can be taken hold of by itself, in the element's own coordinates.</summary>
public sealed class GeometryPoint
{
    internal GeometryPoint(GeometryPointKind kind, PointF at, int segment, int which, int anchor, int other)
    {
        Kind = kind;
        At = at;
        Segment = segment;
        Which = which;
        Anchor = anchor;
        Other = other;
    }

    public GeometryPointKind Kind { get; }

    public PointF At { get; }

    /// <summary>The path segment, the polygon or polyline point, or the line end it belongs to.</summary>
    public int Segment { get; }

    /// <summary>0 for an anchor, 1 for its segment's first control, 2 for its second.</summary>
    public int Which { get; }

    /// <summary>The point a handle hangs from; an anchor's own index.</summary>
    public int Anchor { get; }

    /// <summary>The second anchor a quadratic's one control also hangs from, or -1.</summary>
    public int Other { get; }
}

/// <summary>
/// The points of a path, polygon, polyline or line, and what moving, adding or removing one writes.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GeometryWriter"/> puts a whole element through one map; this takes one point at a
/// time. A path is read into absolute positions, edited there, and written back segment by segment
/// in each segment's own letter and case, so a relative path stays relative and an <c>h</c> stays
/// an <c>h</c> for as long as it can. A segment an edit did not reach is written as it was read.
/// <c>PathService</c> could not be widened for this: it makes every segment absolute in place.
/// </para>
/// <para>
/// Every edit starts from what <see cref="Capture"/> found, as <see cref="GeometryWriter.Apply"/>
/// does, and numbers are written to as many decimals as the shape already uses, at least three.
/// </para>
/// </remarks>
public sealed class GeometryPoints
{
    /// <summary>How close the last point before a <c>Z</c> has to be to the first to be the same point.</summary>
    private const float Joined = 1e-4f;

    private const string TwoEnds = "A line has just its two ends.";

    private readonly SvgPath? _path;
    private readonly SvgPolygon? _poly;
    private readonly SvgLine? _line;
    private readonly SvgPathSegmentList? _segments;
    private readonly Piece[] _pieces = Array.Empty<Piece>();
    private readonly float[] _coordinates = Array.Empty<float>();
    private readonly SvgUnit[] _ends = Array.Empty<SvgUnit>();
    private readonly int _decimals;
    private readonly List<GeometryPoint> _points = new();

    /// <summary>Each segment's anchor, by point; a <c>Z</c> and a closing point that is the first are the subpath's.</summary>
    private readonly int[] _anchors = Array.Empty<int>();

    private GeometryPoints(SvgPath path, SvgPathSegmentList segments, Piece[] pieces)
    {
        _path = path;
        _segments = segments;
        _pieces = pieces;
        _anchors = new int[pieces.Length];
        _decimals = Decimals(segments.SelectMany(Numbers));

        var subpath = -1;
        var opened = PointF.Empty;

        for (var i = 0; i < pieces.Length; i++)
        {
            var piece = pieces[i];
            var from = i == 0 ? -1 : _anchors[i - 1];

            if (piece.Kind == 'Z')
            {
                _anchors[i] = subpath;

                continue;
            }

            if (piece.Kind == 'M')
            {
                subpath = Add(GeometryPointKind.Anchor, piece.End, i, 0, -1, -1);
                opened = piece.End;
                _anchors[i] = subpath;

                continue;
            }

            _anchors[i] = Closes(i) && Near(piece.End, opened, Joined)
                ? subpath
                : Add(GeometryPointKind.Anchor, piece.End, i, 0, -1, -1);

            switch (piece.Kind)
            {
                case 'C' or 'S':
                    Add(GeometryPointKind.Handle, piece.First, i, 1, from, -1);
                    Add(GeometryPointKind.Handle, piece.Second, i, 2, _anchors[i], -1);
                    break;
                case 'Q':
                    Add(GeometryPointKind.Handle, piece.First, i, 1, _anchors[i], from);
                    break;
            }
        }
    }

    private GeometryPoints(SvgPolygon poly, float[] coordinates)
    {
        _poly = poly;
        _coordinates = coordinates;
        _decimals = Decimals(coordinates);

        for (var i = 0; i < coordinates.Length / 2; i++)
        {
            Add(GeometryPointKind.Anchor, new PointF(coordinates[2 * i], coordinates[2 * i + 1]), i, 0, -1, -1);
        }
    }

    private GeometryPoints(SvgLine line, SvgUnit[] ends)
    {
        _line = line;
        _ends = ends;
        _decimals = Decimals(ends.Select(unit => unit.Value));

        Add(GeometryPointKind.Anchor, new PointF(ends[0].Value, ends[1].Value), 0, 0, -1, -1);
        Add(GeometryPointKind.Anchor, new PointF(ends[2].Value, ends[3].Value), 1, 0, -1, -1);
    }

    /// <summary>Every point, anchors and handles, as <see cref="Capture"/> found them.</summary>
    public IReadOnlyList<GeometryPoint> Points => _points;

    /// <summary>The attributes an edit writes.</summary>
    public IReadOnlyList<string> Names
        => _path is { } ? new[] { "d" }
            : _poly is { } ? new[] { "points" }
            : new[] { "x1", "y1", "x2", "y2" };

    /// <summary>The element's points, or null where it is not a shape with any, or one that cannot be written back.</summary>
    /// <remarks>
    /// The parser swallows its own errors and hands back what it managed, so a path that does not
    /// open with a moveto is one it choked on, and writing it back would delete the rest.
    /// </remarks>
    public static GeometryPoints? Capture(SvgElement element)
    {
        switch (element)
        {
            case SvgPath path:
                return path.PathData is { Count: > 0 } segments
                       && segments[0] is SvgMoveToSegment
                       && Read(segments) is { } pieces
                    ? new GeometryPoints(path, (SvgPathSegmentList)segments.Clone(), pieces)
                    : null;
            case SvgPolygon poly:
                // Both the renderer and the serialiser drop a dangling number.
                return poly.Points is { Count: >= 4 } points && points.Count % 2 == 0
                    ? new GeometryPoints(poly, points.Select(point => point.Value).ToArray())
                    : null;
            case SvgLine line:
                var ends = new[] { line.StartX, line.StartY, line.EndX, line.EndY };

                return ends.All(GeometryWriter.Plain) ? new GeometryPoints(line, ends) : null;
            default:
                return null;
        }
    }

    /// <summary>The handles worth showing while <paramref name="point"/> is chosen: those on the segments either side of its anchor.</summary>
    public IReadOnlyList<int> Around(int point)
    {
        if (_path is null || point < 0 || point >= _points.Count)
        {
            return Array.Empty<int>();
        }

        var anchor = _points[point].Anchor;
        var segments = new HashSet<int>();

        for (var i = 0; i < _pieces.Length; i++)
        {
            if (_anchors[i] == anchor && _pieces[i].Kind != 'Z')
            {
                segments.Add(i);
                segments.Add(i + 1);
            }
        }

        return Enumerable.Range(0, _points.Count)
            .Where(i => _points[i].Kind == GeometryPointKind.Handle && segments.Contains(_points[i].Segment))
            .ToArray();
    }

    /// <summary>Puts <paramref name="point"/> at <paramref name="to"/> and onto the element.</summary>
    /// <param name="smooth">
    /// Whether a handle in line with the one across its anchor keeps it in line, which is what
    /// keeps a smooth curve smooth.
    /// </param>
    /// <returns>What the file should now say.</returns>
    public IReadOnlyList<(string Name, string Value)> Move(int point, PointF to, bool smooth = true)
    {
        var moved = _points[point];

        if (_poly is { })
        {
            var coordinates = (float[])_coordinates.Clone();

            coordinates[2 * moved.Segment] = Round(to.X);
            coordinates[2 * moved.Segment + 1] = Round(to.Y);

            return Poly(coordinates);
        }

        if (_line is { } line)
        {
            Restore();

            var x = GeometryWriter.Same(_ends[2 * moved.Segment], Round(to.X));
            var y = GeometryWriter.Same(_ends[2 * moved.Segment + 1], Round(to.Y));

            if (moved.Segment == 0)
            {
                line.StartX = x;
                line.StartY = y;

                return new[] { ("x1", x.ToString()), ("y1", y.ToString()) };
            }

            line.EndX = x;
            line.EndY = y;

            return new[] { ("x2", x.ToString()), ("y2", y.ToString()) };
        }

        var pieces = Copies();
        var by = new PointF(to.X - moved.At.X, to.Y - moved.At.Y);

        if (moved.Kind == GeometryPointKind.Anchor)
        {
            // An anchor carries its handles: the one coming in on its own segment, and the one
            // going out on the next. An S's going-out handle is a reflection and follows by itself.
            for (var i = 0; i < pieces.Length; i++)
            {
                if (_anchors[i] != point || pieces[i].Kind == 'Z')
                {
                    continue;
                }

                pieces[i].End = Plus(pieces[i].End, by);

                if (pieces[i].Kind is 'C' or 'S')
                {
                    pieces[i].Second = Plus(pieces[i].Second, by);
                }

                if (i + 1 < pieces.Length && pieces[i + 1].Kind == 'C')
                {
                    pieces[i + 1].First = Plus(pieces[i + 1].First, by);
                }
            }

            return Path(pieces);
        }

        var piece = pieces[moved.Segment];

        if (moved.Which == 2)
        {
            piece.Second = to;
        }
        else
        {
            // An S's first control is the last one's reflection; to put it anywhere else is a C.
            piece.First = to;
            piece.Kind = piece.Kind == 'S' ? 'C' : piece.Kind;
        }

        if (smooth && Opposite(moved) is { } opposite)
        {
            var anchor = _points[moved.Anchor].At;
            var across = opposite.Which == 2 ? _pieces[opposite.Segment].Second : _pieces[opposite.Segment].First;

            if (InLine(moved.At, anchor, across) && Length(Minus(to, anchor)) > Joined)
            {
                var along = Minus(anchor, to);
                var scale = Length(Minus(across, anchor)) / Length(along);
                var turned = Plus(anchor, new PointF(along.X * scale, along.Y * scale));

                if (opposite.Which == 2)
                {
                    pieces[opposite.Segment].Second = turned;
                }
                else
                {
                    pieces[opposite.Segment].First = turned;
                }
            }
        }

        return Path(pieces);
    }

    /// <summary>Puts back what <see cref="Capture"/> found.</summary>
    public void Restore()
    {
        if (_path is { } path)
        {
            path.PathData = (SvgPathSegmentList)_segments!.Clone();
        }
        else if (_poly is { } poly)
        {
            poly.Points = Collection(_coordinates);
        }
        else if (_line is { } line)
        {
            line.StartX = _ends[0];
            line.StartY = _ends[1];
            line.EndX = _ends[2];
            line.EndY = _ends[3];
        }
    }

    /// <summary>Takes the anchor <paramref name="point"/> out of the shape, joining what was either side of it.</summary>
    /// <returns>The sentence refusing it, or null where <paramref name="written"/> says what the file should now say.</returns>
    public string? Remove(int point, out IReadOnlyList<(string Name, string Value)>? written)
    {
        written = null;

        if (_line is { })
        {
            return TwoEnds;
        }

        var removing = _points[point];

        if (removing.Kind == GeometryPointKind.Handle)
        {
            return "A handle is not a point on the shape, so there is nothing to remove.";
        }

        if (_poly is { } poly)
        {
            var open = poly is SvgPolyline;

            if (_coordinates.Length / 2 <= (open ? 2 : 3))
            {
                return open
                    ? "A polyline needs two points; delete the element instead."
                    : "A polygon needs three corners; delete the element instead.";
            }

            var left = _coordinates.ToList();

            left.RemoveRange(2 * removing.Segment, 2);
            written = Poly(left.ToArray());

            return null;
        }

        var pieces = Copies().ToList();
        var at = Enumerable.Range(0, pieces.Count).Where(i => _anchors[i] == point && pieces[i].Kind != 'Z').ToArray();
        var first = at[0];
        var opens = first;

        while (pieces[opens].Kind != 'M')
        {
            opens--;
        }

        var ends = opens + 1;

        while (ends < pieces.Count && pieces[ends].Kind != 'M')
        {
            ends++;
        }

        // A Z that the subpath carries on past draws from where it opened without a moveto, which
        // the cases below would have to unpick; nothing writes that, so it is refused rather than guessed.
        if (Enumerable.Range(opens, ends - opens - 1).Any(i => pieces[i].Kind == 'Z'))
        {
            return "That path carries on past where it closes, so its points cannot be taken apart here.";
        }

        var closed = pieces[ends - 1].Kind == 'Z';
        var anchors = Enumerable.Range(opens, ends - opens).Where(i => pieces[i].Kind != 'Z').Select(i => _anchors[i]).Distinct().Count();

        if (anchors < 3)
        {
            return "A path needs two points here; delete the element instead.";
        }

        if (pieces[first].Kind == 'M')
        {
            var next = first + 1;
            var opening = pieces[first];

            if (closed && at.Length > 1)
            {
                // The first point is also the last: the segment into it and the one out of it
                // become one, which ends where the path now opens.
                pieces[at[1]] = Merge(pieces[at[1]], pieces[next]);
            }
            else if (closed)
            {
                var closing = new Piece('L', pieces[ends - 1].Relative) { Start = pieces[ends - 2].End, End = opening.End };
                var merged = Merge(closing, pieces[next]);

                if (merged.Kind != 'L')
                {
                    pieces.Insert(ends - 1, merged);
                }
            }

            opening.End = pieces[next].End;
            pieces.RemoveAt(next);
        }
        else if (first + 1 == pieces.Count || pieces[first + 1].Kind == 'M')
        {
            pieces.RemoveAt(first);
        }
        else if (pieces[first + 1].Kind == 'Z')
        {
            var into = pieces[first];

            if (Straight(into) || into.Kind == 'A')
            {
                pieces.RemoveAt(first);
            }
            else
            {
                pieces[first] = Merge(into, new Piece('L', into.Relative) { Start = into.End, End = pieces[opens].End });
            }
        }
        else
        {
            pieces[first + 1] = Merge(pieces[first], pieces[first + 1]);
            pieces.RemoveAt(first);
        }

        written = Path(Pinned(pieces));

        return null;
    }

    /// <summary>Puts a new anchor on <paramref name="segment"/> at <paramref name="t"/>, leaving the shape as it was.</summary>
    /// <param name="anchor">The new anchor's index among the points the element now has.</param>
    /// <returns>The sentence refusing it, or null where <paramref name="written"/> says what the file should now say.</returns>
    public string? Insert(int segment, float t, out IReadOnlyList<(string Name, string Value)>? written, out int anchor)
    {
        written = null;
        anchor = -1;

        if (_line is { })
        {
            return TwoEnds;
        }

        if (_poly is { })
        {
            var count = _coordinates.Length / 2;
            var from = new PointF(_coordinates[2 * segment], _coordinates[2 * segment + 1]);
            var to = new PointF(_coordinates[2 * ((segment + 1) % count)], _coordinates[2 * ((segment + 1) % count) + 1]);
            var put = Lerp(from, to, t);
            var coordinates = _coordinates.ToList();

            coordinates.InsertRange(2 * segment + 2, new[] { Round(put.X), Round(put.Y) });
            written = Poly(coordinates.ToArray());
            anchor = segment + 1;

            return null;
        }

        var pieces = Copies().ToList();
        var piece = pieces[segment];
        var start = piece.Start;

        switch (piece.Kind)
        {
            case 'A':
                return "A point cannot be added to an arc.";
            case 'M':
                return "A moveto draws nothing to add a point to.";
            case 'Z':
                pieces.Insert(segment, new Piece('L', piece.Relative) { End = Lerp(start, piece.End, t) });
                break;
            case 'L' or 'H' or 'V':
                pieces.Insert(segment, new Piece(piece.Kind, piece.Relative) { End = Lerp(start, piece.End, t) });
                break;
            case 'C' or 'S':
            {
                var q0 = Lerp(start, piece.First, t);
                var q1 = Lerp(piece.First, piece.Second, t);
                var q2 = Lerp(piece.Second, piece.End, t);
                var r0 = Lerp(q0, q1, t);
                var r1 = Lerp(q1, q2, t);

                pieces.Insert(segment, new Piece('C', piece.Relative) { First = q0, Second = r0, End = Lerp(r0, r1, t) });
                piece.Kind = 'C';
                piece.First = r1;
                piece.Second = q2;
                break;
            }
            default:
            {
                var q0 = Lerp(start, piece.First, t);
                var q1 = Lerp(piece.First, piece.End, t);

                pieces.Insert(segment, new Piece('Q', piece.Relative) { First = q0, End = Lerp(q0, q1, t) });
                piece.Kind = 'Q';
                piece.First = q1;
                break;
            }
        }

        written = Path(Pinned(pieces));

        var points = Capture(_path!)?.Points ?? Array.Empty<GeometryPoint>();

        for (var i = 0; i < points.Count && anchor < 0; i++)
        {
            anchor = points[i].Kind == GeometryPointKind.Anchor && points[i].Segment == segment ? i : -1;
        }

        return null;
    }

    /// <summary>The segment nearest <paramref name="at"/>, and how far along it, where one is within <paramref name="within"/>.</summary>
    /// <param name="toScreen">Where a point of the element's lands, which is where the distance is measured.</param>
    /// <remarks>An arc is left out: nothing is added to one, and it is the one segment that cannot be traced without solving for its centre.</remarks>
    public (int Segment, float T)? Nearest(PointF at, Func<PointF, PointF> toScreen, float within)
    {
        const int Steps = 32;

        var best = (Segment: -1, T: 0f, Distance: within);
        Func<float, PointF>? nearest = null;

        foreach (var (segment, curve) in Curves())
        {
            var previous = toScreen(curve(0f));

            for (var step = 1; step <= Steps; step++)
            {
                var here = toScreen(curve(step / (float)Steps));
                var (distance, along) = Beside(at, previous, here);

                if (distance <= best.Distance)
                {
                    best = (segment, (step - 1 + along) / Steps, distance);
                    nearest = curve;
                }

                previous = here;
            }
        }

        if (nearest is null)
        {
            return null;
        }

        // The chords miss a curve by a little between samples; a few rounds of narrowing find the curve itself.
        var low = Math.Max(0f, best.T - 1f / Steps);
        var high = Math.Min(1f, best.T + 1f / Steps);

        for (var round = 0; round < 24; round++)
        {
            var a = low + (high - low) / 3f;
            var b = high - (high - low) / 3f;

            if (Length(Minus(toScreen(nearest(a)), at)) < Length(Minus(toScreen(nearest(b)), at)))
            {
                high = b;
            }
            else
            {
                low = a;
            }
        }

        return (best.Segment, (low + high) / 2f);
    }

    // ---- reading and writing a path -------------------------------------------------------------

    /// <summary>One segment, every position absolute.</summary>
    private sealed class Piece
    {
        public Piece(char kind, bool relative)
        {
            Kind = kind;
            Relative = relative;
        }

        public char Kind;
        public bool Relative;
        public PointF Start;
        public PointF End;

        /// <summary>A cubic's first control, an S's reflected one, a quadratic's control, or a T's reflected one.</summary>
        public PointF First;

        public PointF Second;
        public SvgArcSegment? Arc;

        /// <summary>The segment as read, and the piece it was read into; null for a piece an edit made.</summary>
        public SvgPathSegment? Original;

        public Piece? Was;

        public Piece Copy() => (Piece)MemberwiseClone();
    }

    private static Piece[]? Read(SvgPathSegmentList segments)
    {
        var pieces = new Piece[segments.Count];
        var current = PointF.Empty;
        var opened = PointF.Empty;
        Piece? previous = null;

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var start = current;
            var piece = new Piece(Letter(segment), segment.IsRelative) { Start = start, Original = segment };

            PointF Absolute(PointF point) => segment.IsRelative ? Plus(start, point) : point;

            piece.End = GeometryWriter.Ends(segment, current, opened);

            switch (segment)
            {
                case SvgCubicCurveSegment cubic:
                    piece.First = piece.Kind == 'S' ? Implied(previous, start, true) : Absolute(cubic.FirstControlPoint);
                    piece.Second = Absolute(cubic.SecondControlPoint);
                    break;
                case SvgQuadraticCurveSegment quadratic:
                    piece.First = piece.Kind == 'T' ? Implied(previous, start, false) : Absolute(quadratic.ControlPoint);
                    break;
                case SvgArcSegment arc:
                    piece.Arc = arc;
                    break;
            }

            if (!Finite(piece.End) || !Finite(piece.First) || !Finite(piece.Second))
            {
                return null;
            }

            if (piece.Kind == 'M')
            {
                opened = piece.End;
            }

            piece.Was = piece;
            pieces[i] = piece;
            previous = piece;
            current = piece.End;
        }

        return pieces;
    }

    private static char Letter(SvgPathSegment segment)
        => segment switch
        {
            SvgMoveToSegment => 'M',
            SvgLineSegment line => float.IsNaN(line.End.Y) ? 'H' : float.IsNaN(line.End.X) ? 'V' : 'L',
            SvgCubicCurveSegment cubic => float.IsNaN(cubic.FirstControlPoint.X) ? 'S' : 'C',
            SvgQuadraticCurveSegment quadratic => float.IsNaN(quadratic.ControlPoint.X) ? 'T' : 'Q',
            SvgArcSegment => 'A',
            _ => 'Z'
        };

    /// <summary>The control an S or a T does not write: the last one reflected, where the last segment was the same kind of curve.</summary>
    private static PointF Implied(Piece? previous, PointF start, bool cubic)
        => cubic
            ? previous?.Kind is 'C' or 'S' ? Minus(Plus(start, start), previous.Second) : start
            : previous?.Kind is 'Q' or 'T' ? Minus(Plus(start, start), previous.First) : start;

    private Piece[] Copies() => _pieces.Select(piece => piece.Copy()).ToArray();

    private IReadOnlyList<(string Name, string Value)> Path(IList<Piece> pieces)
    {
        var list = new SvgPathSegmentList();
        var current = PointF.Empty;
        var opened = PointF.Empty;

        // Each segment is written from where the ones before it, as written, left the pen, so a
        // relative segment after a rounded one still ends where it did.
        foreach (var piece in pieces)
        {
            if (piece.Kind == 'Z')
            {
                piece.End = opened;
            }

            var segment = piece.Was is { } was && Same(piece, current, was)
                ? piece.Original!.Clone()
                : Written(piece, current);

            current = GeometryWriter.Ends(segment, current, opened);
            opened = segment is SvgMoveToSegment ? current : opened;
            list.Add(segment);
        }

        _path!.PathData = list;

        return new[] { ("d", list.ToString()) };
    }

    private static bool Same(Piece piece, PointF start, Piece was)
        => piece.Kind == was.Kind
           && start == was.Start
           && piece.End == was.End
           && (piece.Kind is not ('C' or 'Q') || piece.First == was.First)
           && (piece.Kind is not ('C' or 'S') || piece.Second == was.Second);

    private SvgPathSegment Written(Piece piece, PointF start)
    {
        var relative = piece.Relative;

        PointF Out(PointF at)
            => relative
                ? new PointF(Round(at.X - (double)start.X), Round(at.Y - (double)start.Y))
                : new PointF(Round(at.X), Round(at.Y));

        var end = Out(piece.End);

        return piece.Kind switch
        {
            'M' => new SvgMoveToSegment(relative, end),
            'H' when Level(piece.End.Y, start.Y) => new SvgLineSegment(relative, new PointF(end.X, float.NaN)),
            'V' when Level(piece.End.X, start.X) => new SvgLineSegment(relative, new PointF(float.NaN, end.Y)),
            'L' or 'H' or 'V' => new SvgLineSegment(relative, end),
            'C' => new SvgCubicCurveSegment(relative, Out(piece.First), Out(piece.Second), end),
            'S' => new SvgCubicCurveSegment(relative, Out(piece.Second), end),
            'Q' => new SvgQuadraticCurveSegment(relative, Out(piece.First), end),
            'T' => new SvgQuadraticCurveSegment(relative, end),
            'A' => new SvgArcSegment(piece.Arc!.RadiusX, piece.Arc.RadiusY, piece.Arc.Angle, piece.Arc.Size, piece.Arc.Sweep, relative, end),
            _ => new SvgClosePathSegment(relative)
        };
    }

    /// <summary>Spells out an S or a T whose reflection an edit has changed, so the curve stays where it was.</summary>
    /// <remarks>Only after a segment is added or removed; a moved handle is meant to swing the reflection with it.</remarks>
    private static List<Piece> Pinned(List<Piece> pieces)
    {
        Piece? previous = null;

        foreach (var piece in pieces)
        {
            var start = previous?.End ?? PointF.Empty;

            if ((piece.Kind == 'S' && !Near(Implied(previous, start, true), piece.First, Joined))
                || (piece.Kind == 'T' && !Near(Implied(previous, start, false), piece.First, Joined)))
            {
                piece.Kind = piece.Kind == 'S' ? 'C' : 'Q';
            }

            previous = piece;
        }

        return pieces;
    }

    /// <summary>One segment from where <paramref name="into"/> starts to where <paramref name="outOf"/> ends, keeping the outer handles.</summary>
    private static Piece Merge(Piece into, Piece outOf)
    {
        if (into.Kind == 'A' || outOf.Kind == 'A')
        {
            return outOf;
        }

        if (Straight(into) && Straight(outOf))
        {
            return new Piece('L', outOf.Relative) { End = outOf.End };
        }

        return new Piece('C', outOf.Relative)
        {
            First = into.Kind switch
            {
                'C' or 'S' => into.First,
                'Q' or 'T' => Lerp(into.Start, into.First, 2f / 3f),
                _ => into.Start
            },
            Second = outOf.Kind switch
            {
                'C' or 'S' => outOf.Second,
                'Q' or 'T' => Lerp(outOf.End, outOf.First, 2f / 3f),
                _ => outOf.End
            },
            End = outOf.End
        };
    }

    private static bool Straight(Piece piece) => piece.Kind is 'L' or 'H' or 'V';

    private bool Closes(int i) => i + 1 < _pieces.Length && _pieces[i + 1].Kind == 'Z';

    /// <summary>The handle across <paramref name="handle"/>'s anchor, where it is one the file writes.</summary>
    private (int Segment, int Which)? Opposite(GeometryPoint handle)
    {
        var segment = handle.Segment;

        if (_pieces[segment].Kind == 'Q')
        {
            return null;
        }

        if (handle.Which == 2)
        {
            var next = segment + 1 < _pieces.Length && _pieces[segment + 1].Kind != 'Z'
                ? segment + 1
                : Array.IndexOf(_anchors, handle.Anchor) + 1;

            return next < _pieces.Length && next != segment && _pieces[next].Kind == 'C' ? (next, 1) : null;
        }

        var previous = segment - 1;

        if (_pieces[previous].Kind == 'M')
        {
            previous = Array.LastIndexOf(_anchors, handle.Anchor);
            previous = previous > segment && _pieces[previous].Kind == 'Z' ? previous - 1 : previous;
        }

        return previous != segment && _pieces[previous].Kind is 'C' or 'S' && _anchors[previous] == handle.Anchor
            ? (previous, 2)
            : null;
    }

    // ---- polygons -----------------------------------------------------------------------------

    private IReadOnlyList<(string Name, string Value)> Poly(float[] coordinates)
    {
        var points = Collection(coordinates);

        // Through the setter: only the setter marks the shape dirty.
        _poly!.Points = points;

        return new[] { ("points", points.ToString()) };
    }

    private static SvgPointCollection Collection(IEnumerable<float> coordinates)
    {
        var points = new SvgPointCollection();

        points.AddRange(coordinates.Select(value => new SvgUnit(SvgUnitType.User, value)));

        return points;
    }

    // ---- tracing ------------------------------------------------------------------------------

    private IEnumerable<(int Segment, Func<float, PointF> Curve)> Curves()
    {
        if (_line is { })
        {
            yield return (0, t => Lerp(_points[0].At, _points[1].At, t));

            yield break;
        }

        if (_poly is { } poly)
        {
            var count = _points.Count;

            for (var i = 0; i < (poly is SvgPolyline ? count - 1 : count); i++)
            {
                var from = _points[i].At;
                var to = _points[(i + 1) % count].At;

                yield return (i, t => Lerp(from, to, t));
            }

            yield break;
        }

        for (var i = 0; i < _pieces.Length; i++)
        {
            var piece = _pieces[i];
            var start = piece.Start;

            switch (piece.Kind)
            {
                case 'M' or 'A':
                    break;
                case 'C' or 'S':
                    yield return (i, t => Cubic(start, piece.First, piece.Second, piece.End, t));
                    break;
                case 'Q' or 'T':
                    yield return (i, t => Lerp(Lerp(start, piece.First, t), Lerp(piece.First, piece.End, t), t));
                    break;
                default:
                    if (!Near(start, piece.End, Joined))
                    {
                        yield return (i, t => Lerp(start, piece.End, t));
                    }

                    break;
            }
        }
    }

    private static PointF Cubic(PointF p0, PointF p1, PointF p2, PointF p3, float t)
    {
        var a = Lerp(Lerp(p0, p1, t), Lerp(p1, p2, t), t);
        var b = Lerp(Lerp(p1, p2, t), Lerp(p2, p3, t), t);

        return Lerp(a, b, t);
    }

    /// <summary>How far <paramref name="at"/> is from the chord, and where along it the nearest point is.</summary>
    private static (float Distance, float Along) Beside(PointF at, PointF from, PointF to)
    {
        var chord = Minus(to, from);
        var squared = chord.X * chord.X + chord.Y * chord.Y;
        var along = squared <= 0f
            ? 0f
            : Math.Max(0f, Math.Min(1f, ((at.X - from.X) * chord.X + (at.Y - from.Y) * chord.Y) / squared));

        return (Length(Minus(at, Lerp(from, to, along))), along);
    }

    // ---- numbers ------------------------------------------------------------------------------

    private static IEnumerable<float> Numbers(SvgPathSegment segment)
    {
        yield return segment.End.X;
        yield return segment.End.Y;

        switch (segment)
        {
            case SvgCubicCurveSegment cubic:
                yield return cubic.FirstControlPoint.X;
                yield return cubic.FirstControlPoint.Y;
                yield return cubic.SecondControlPoint.X;
                yield return cubic.SecondControlPoint.Y;
                break;
            case SvgQuadraticCurveSegment quadratic:
                yield return quadratic.ControlPoint.X;
                yield return quadratic.ControlPoint.Y;
                break;
        }
    }

    /// <summary>The most decimals any of <paramref name="values"/> needs, at least three and at most six.</summary>
    private static int Decimals(IEnumerable<float> values)
    {
        var most = 3;

        foreach (var value in values)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                continue;
            }

            var places = 0;

            while (places < 6 && (float)Math.Round(value, places) != value)
            {
                places++;
            }

            most = Math.Max(most, places);
        }

        return most;
    }

    private float Round(double value) => GeometryWriter.Zero((float)Math.Round(value, _decimals, MidpointRounding.AwayFromZero));

    /// <summary>Whether two coordinates write as the same number.</summary>
    private bool Level(float a, float b) => Math.Abs(a - b) < 0.5d * Math.Pow(10d, -_decimals);

    private static bool Finite(PointF point)
        => !float.IsNaN(point.X) && !float.IsNaN(point.Y) && !float.IsInfinity(point.X) && !float.IsInfinity(point.Y);

    private static bool Near(PointF a, PointF b, float within) => Math.Abs(a.X - b.X) <= within && Math.Abs(a.Y - b.Y) <= within;

    /// <summary>Whether a handle and the one across its anchor point exactly away from each other, to half a degree.</summary>
    private static bool InLine(PointF handle, PointF anchor, PointF across)
    {
        var out1 = Minus(handle, anchor);
        var out2 = Minus(anchor, across);

        if (Length(out1) <= Joined || Length(out2) <= Joined)
        {
            return false;
        }

        var angle = Math.Atan2(out1.X * out2.Y - out1.Y * out2.X, out1.X * out2.X + out1.Y * out2.Y);

        return Math.Abs(angle) <= 0.5d * Math.PI / 180d;
    }

    private static PointF Plus(PointF a, PointF b) => new(a.X + b.X, a.Y + b.Y);

    private static PointF Minus(PointF a, PointF b) => new(a.X - b.X, a.Y - b.Y);

    private static float Length(PointF vector) => (float)Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y);

    private static PointF Lerp(PointF a, PointF b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private int Add(GeometryPointKind kind, PointF at, int segment, int which, int anchor, int other)
    {
        var index = _points.Count;

        _points.Add(new GeometryPoint(kind, at, segment, which, kind == GeometryPointKind.Anchor ? index : anchor, other));

        return index;
    }
}
