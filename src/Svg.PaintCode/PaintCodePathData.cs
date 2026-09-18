// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Svg.PaintCode;

/// <summary>
/// Turns a shape into SVG path data, in the shape's own space: the y-flip is applied here, and the
/// anchor is not, because the element carries it as a <c>translate</c>.
/// </summary>
/// <remarks>
/// Everything PaintCode stores is y-up, so a point at <c>(x, y)</c> is written at <c>(x, -y)</c>.
/// Verified point by point, control points included, against the C# PaintCode itself generates.
/// </remarks>
internal static class PaintCodePathData
{
    internal static string? For(PaintCodeShape shape)
        => shape.Kind switch
        {
            PaintCodeShapeKind.Bezier => Bezier(shape),
            PaintCodeShapeKind.Rectangle or PaintCodeShapeKind.RoundedRectangle => Rectangle(shape),
            PaintCodeShapeKind.Oval => Oval(shape),
            PaintCodeShapeKind.Star or PaintCodeShapeKind.Polygon => Outline(Corners(shape)),
            _ => null
        };

    /// <summary>The shape's box in SVG coordinates, relative to the shape's own origin.</summary>
    internal static PaintCodeRect Box(PaintCodeShape shape)
    {
        var frame = shape.Frame;

        return new PaintCodeRect(frame.X, -(frame.Y + frame.Height), frame.Width, frame.Height);
    }

    /// <summary>
    /// How far the shape reaches along <paramref name="direction"/>, as the two ends of that reach.
    /// </summary>
    /// <remarks>
    /// What PaintCode lays a gradient between when it was given an angle rather than two handles:
    /// the two lines across the direction that touch the outline. Measured against its own generated
    /// code -- a star at -45 degrees is drawn between the two points its outline touches, 8.59 from
    /// the middle, where its box's corners are 12.93.
    ///
    /// The outline and not the box, which are the same thing only for a plain rectangle. Null where
    /// the outline is one this does not measure -- an arc of an oval -- and the caller falls back.
    /// </remarks>
    internal static (double Low, double High)? Span(PaintCodeShape shape, PaintCodePoint direction)
        => shape.Kind switch
        {
            PaintCodeShapeKind.Bezier => BezierSpan(shape, direction),
            PaintCodeShapeKind.Rectangle or PaintCodeShapeKind.RoundedRectangle => RectangleSpan(shape, direction),
            PaintCodeShapeKind.Oval when IsWholeEllipse(shape) => EllipseSpan(shape, direction),
            PaintCodeShapeKind.Star or PaintCodeShapeKind.Polygon => CornerSpan(shape, direction),
            _ => null
        };

    private static (double, double)? BezierSpan(PaintCodeShape shape, PaintCodePoint direction)
    {
        if (shape.Path is not { } path)
        {
            return null;
        }

        var reach = default((double Low, double High)?);

        foreach (var contour in path.Contours)
        {
            var points = contour.Points;

            if (points.Count == 0)
            {
                continue;
            }

            Reach(ref reach, At(Flip(points[0].Position), direction));

            for (var index = 1; index < points.Count; index++)
            {
                SegmentSpan(ref reach, points[index - 1], points[index], direction);
            }

            if (contour.IsClosed)
            {
                SegmentSpan(ref reach, points[points.Count - 1], points[0], direction);
            }
        }

        return reach;
    }

    /// <summary>Where one segment reaches, which for a curve is not where either of its ends is.</summary>
    /// <remarks>
    /// Projected first and solved in one dimension: a cubic's own turning points are where its
    /// derivative -- a quadratic -- is nought, and the same roots answer for the curve's reach along
    /// any direction once the four points are projected onto it.
    /// </remarks>
    private static void SegmentSpan(
        ref (double Low, double High)? reach,
        PaintCodePathPoint from,
        PaintCodePathPoint to,
        PaintCodePoint direction)
    {
        Reach(ref reach, At(Flip(to.Position), direction));

        if (IsZero(from.Exiting) && IsZero(to.Entering))
        {
            return;
        }

        var start = At(Flip(from.Position), direction);
        var first = At(Flip(Add(from.Position, from.Exiting)), direction);
        var second = At(Flip(Add(to.Position, to.Entering)), direction);
        var end = At(Flip(to.Position), direction);

        var a = end - (3 * second) + (3 * first) - start;
        var b = 2 * (second - (2 * first) + start);
        var c = first - start;

        foreach (var t in Roots(a, b, c))
        {
            var rest = 1 - t;

            Reach(
                ref reach,
                (start * rest * rest * rest) + (3 * first * t * rest * rest) + (3 * second * t * t * rest) + (end * t * t * t));
        }
    }

    /// <summary>The roots of <c>at² + bt + c</c> that fall inside the segment.</summary>
    private static IEnumerable<double> Roots(double a, double b, double c)
    {
        if (Math.Abs(a) < 1e-12)
        {
            if (Math.Abs(b) > 1e-12 && Inside(-c / b, out var straight))
            {
                yield return straight;
            }

            yield break;
        }

        var square = (b * b) - (4 * a * c);

        if (square < 0)
        {
            yield break;
        }

        var root = Math.Sqrt(square);

        if (Inside((-b + root) / (2 * a), out var first))
        {
            yield return first;
        }

        if (Inside((-b - root) / (2 * a), out var second))
        {
            yield return second;
        }
    }

    private static bool Inside(double t, out double inside)
    {
        inside = t;

        return t > 0 && t < 1;
    }

    /// <summary>
    /// A rectangle's reach, which at a rounded corner is the arc rather than the corner itself.
    /// </summary>
    /// <remarks>
    /// Corner by corner: a rounded one reaches its own circle's centre plus the radius, in whatever
    /// direction is asked for, and the straight sides between them end on those same arcs.
    /// </remarks>
    private static (double, double)? RectangleSpan(PaintCodeShape shape, PaintCodePoint direction)
    {
        var box = Box(shape);
        var radius = Radius(shape, box);
        var metrics = shape.Metrics;
        var reach = default((double Low, double High)?);

        // The flip turns the box upside down and the corner flags with it, as the path data says.
        var corners = new[]
        {
            (X: box.X, Y: box.Y, Rounded: metrics.TopLeftRounded, Towards: new PaintCodePoint(1, 1)),
            (X: box.X + box.Width, Y: box.Y, Rounded: metrics.TopRightRounded, Towards: new PaintCodePoint(-1, 1)),
            (X: box.X + box.Width, Y: box.Y + box.Height, Rounded: metrics.BottomRightRounded, Towards: new PaintCodePoint(-1, -1)),
            (X: box.X, Y: box.Y + box.Height, Rounded: metrics.BottomLeftRounded, Towards: new PaintCodePoint(1, -1))
        };

        foreach (var corner in corners)
        {
            if (corner.Rounded && radius > 0)
            {
                var middle = At(
                    new PaintCodePoint(corner.X + (corner.Towards.X * radius), corner.Y + (corner.Towards.Y * radius)),
                    direction);

                Reach(ref reach, middle + radius);
                Reach(ref reach, middle - radius);
            }
            else
            {
                Reach(ref reach, At(new PaintCodePoint(corner.X, corner.Y), direction));
            }
        }

        return reach;
    }

    /// <summary>An ellipse's reach, which is the one shape with a closed form for it.</summary>
    private static (double, double)? EllipseSpan(PaintCodeShape shape, PaintCodePoint direction)
    {
        var box = Box(shape);
        var radiusX = box.Width / 2;
        var radiusY = box.Height / 2;

        if (radiusX <= 0 || radiusY <= 0)
        {
            return null;
        }

        var middle = At(new PaintCodePoint(box.X + radiusX, box.Y + radiusY), direction);
        var reach = Math.Sqrt(Square(radiusX * direction.X) + Square(radiusY * direction.Y));

        return (middle - reach, middle + reach);
    }

    /// <summary>A star's or a polygon's reach: its corners, which are all it is made of.</summary>
    private static (double, double)? CornerSpan(PaintCodeShape shape, PaintCodePoint direction)
    {
        var reach = default((double Low, double High)?);

        foreach (var corner in Corners(shape))
        {
            Reach(ref reach, At(corner, direction));
        }

        return reach;
    }

    /// <summary>Where a star's or a polygon's points sit, in the order the path data writes them.</summary>
    private static IEnumerable<PaintCodePoint> Corners(PaintCodeShape shape)
    {
        var box = Box(shape);
        var metrics = shape.Metrics;
        var star = shape.Kind is PaintCodeShapeKind.Star;
        var sides = Math.Max(3, metrics.Sides);
        var count = star ? sides * 2 : sides;

        for (var index = 0; index < count; index++)
        {
            // A percentage, not a fraction: the sample's stars carry 37 to 52, and reading one as a
            // multiplier puts the inner vertices forty times beyond the tips.
            var scale = star && index % 2 == 1 ? Math.Max(0, metrics.InnerRadiusPercentage / 100) : 1d;

            yield return OnEllipse(
                box.X + (box.Width / 2),
                box.Y + (box.Height / 2),
                box.Width / 2 * scale,
                box.Height / 2 * scale,
                -90 + (index * (star ? 180d : 360d) / sides));
        }
    }

    private static void Reach(ref (double Low, double High)? reach, double at)
        => reach = reach is { } held ? (Math.Min(held.Low, at), Math.Max(held.High, at)) : (at, at);

    /// <summary>Where a point falls along a direction, which is all a linear gradient reads of it.</summary>
    private static double At(PaintCodePoint point, PaintCodePoint direction)
        => (point.X * direction.X) + (point.Y * direction.Y);

    private static double Square(double value) => value * value;

    /// <summary>Whether the shape is a plain rectangle, which SVG has an element for.</summary>
    internal static bool IsPlainRectangle(PaintCodeShape shape)
    {
        var metrics = shape.Metrics;

        return shape.Kind is PaintCodeShapeKind.Rectangle or PaintCodeShapeKind.RoundedRectangle &&
               (metrics.CornerRadius <= 0 ||
                (metrics.TopLeftRounded && metrics.TopRightRounded && metrics.BottomLeftRounded && metrics.BottomRightRounded));
    }

    /// <summary>Whether the shape is an oval whose two radii are the same.</summary>
    /// <remarks>
    /// Compared as the numbers are written, which is the rule the rest of this file compares by: a
    /// hundredth of a millionth of a unit apart is the same circle to everything downstream.
    /// </remarks>
    internal static bool IsRound(PaintCodeShape shape)
    {
        var box = Box(shape);

        return shape.Kind is PaintCodeShapeKind.Oval &&
               box.Width > 0 &&
               string.Equals(Number(box.Width), Number(box.Height), StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole circle, starting where an arc at <paramref name="degrees"/> would and running the
    /// way PaintCode turns.
    /// </summary>
    /// <remarks>
    /// Two half arcs for the reason <see cref="Ellipse"/> is written that way: one arc of 360
    /// degrees starts and ends at the same point and draws nothing. Left open rather than closed,
    /// because what this is for is a dash measured from the start point, and a Z would add a
    /// zero-length segment for the dash to count.
    /// </remarks>
    internal static string Ring(double centerX, double centerY, double radius, double degrees, bool clockwise)
    {
        var start = OnEllipse(centerX, centerY, radius, radius, -degrees);
        var across = OnEllipse(centerX, centerY, radius, radius, -degrees + 180);
        var sweep = clockwise ? '1' : '0';

        return new StringBuilder()
            .Append('M').Append(Pair(start))
            .Append('A').Append(Number(radius)).Append(' ').Append(Number(radius)).Append(" 0 1 ").Append(sweep).Append(' ').Append(Pair(across))
            .Append('A').Append(Number(radius)).Append(' ').Append(Number(radius)).Append(" 0 1 ").Append(sweep).Append(' ').Append(Pair(start))
            .ToString();
    }

    /// <summary>
    /// Whether the shape is a whole ellipse rather than an arc of one.
    /// </summary>
    /// <remarks>
    /// A whole one is written with no sweep at all rather than with a full turn: 670 of the sample's
    /// 718 ovals are start 0, end 0, and reading that as an arc draws nothing.
    /// </remarks>
    internal static bool IsWholeEllipse(PaintCodeShape shape)
        => shape.Kind is PaintCodeShapeKind.Oval &&
           (shape.Metrics.StartAngle == shape.Metrics.EndAngle || Turn(shape.Metrics) >= 360);

    /// <summary>
    /// How far round the arc goes, the way PaintCode works it out.
    /// </summary>
    /// <remarks>
    /// Its own generated code writes the sweep as
    /// <c>(start - end) + (end > start ? 360 * ceil((end - start) / 360) : 0)</c>, so an end past the
    /// start is brought round by as many whole turns as it takes rather than by exactly one. Adding
    /// a single turn is right for every sweep inside one and wrong beyond it: powerButton-state is
    /// start -290 end 110, which is -400, and one turn leaves -40 where PaintCode has 320. Reading
    /// the size of it instead -- anything past a turn is whole -- drew that one as a closed ring
    /// with the gap at the top filled in, and large-switch with it.
    /// </remarks>
    private static double Turn(PaintCodeShapeMetrics metrics)
    {
        var sweep = metrics.StartAngle - metrics.EndAngle;

        return metrics.EndAngle > metrics.StartAngle
            ? sweep + (360 * Math.Ceiling((metrics.EndAngle - metrics.StartAngle) / 360))
            : sweep;
    }

    private static string? Bezier(PaintCodeShape shape)
    {
        if (shape.Path is not { } path)
        {
            return null;
        }

        var data = new StringBuilder();

        foreach (var contour in path.Contours)
        {
            if (contour.Points.Count == 0)
            {
                continue;
            }

            var points = contour.Points;
            data.Append('M').Append(Pair(Flip(points[0].Position)));

            for (var index = 1; index < points.Count; index++)
            {
                Segment(data, points[index - 1], points[index]);
            }

            // A closed contour still needs its last segment drawn where that segment curves; where it
            // is straight, Z is the line back and writing one as well would draw it twice.
            if (contour.IsClosed)
            {
                var last = points[points.Count - 1];

                if (!IsZero(last.Exiting) || !IsZero(points[0].Entering))
                {
                    Segment(data, last, points[0]);
                }

                data.Append('Z');
            }
        }

        return data.Length == 0 ? null : data.ToString();
    }

    // A control point is stored as an offset from the point it belongs to. Where both offsets are
    // zero the segment is a straight line, which SVG says in a third of the characters.
    private static void Segment(StringBuilder data, PaintCodePathPoint from, PaintCodePathPoint to)
    {
        if (IsZero(from.Exiting) && IsZero(to.Entering))
        {
            data.Append('L').Append(Pair(Flip(to.Position)));

            return;
        }

        data.Append('C')
            .Append(Pair(Flip(Add(from.Position, from.Exiting)))).Append(' ')
            .Append(Pair(Flip(Add(to.Position, to.Entering)))).Append(' ')
            .Append(Pair(Flip(to.Position)));
    }

    private static string Rectangle(PaintCodeShape shape)
    {
        var box = Box(shape);
        var metrics = shape.Metrics;
        var radius = Radius(shape, box);
        var left = box.X;
        var top = box.Y;
        var right = box.X + box.Width;
        var bottom = box.Y + box.Height;

        // The flip turns PaintCode's box upside down, and its corner flags with it: what it calls the
        // top corners are the ones at the larger y, which after the flip are this box's top corners.
        var topLeft = metrics.TopLeftRounded ? radius : 0;
        var topRight = metrics.TopRightRounded ? radius : 0;
        var bottomRight = metrics.BottomRightRounded ? radius : 0;
        var bottomLeft = metrics.BottomLeftRounded ? radius : 0;
        var data = new StringBuilder();

        data.Append('M').Append(Pair(new PaintCodePoint(left + topLeft, top)));
        Side(data, right - topRight, top, topRight, right, top + topRight);
        Side(data, right, bottom - bottomRight, bottomRight, right - bottomRight, bottom);
        Side(data, left + bottomLeft, bottom, bottomLeft, left, bottom - bottomLeft);
        Side(data, left, top + topLeft, topLeft, left + topLeft, top);

        return data.Append('Z').ToString();
    }

    /// <summary>The corner radius the shape is drawn with, which its own box can be too small for.</summary>
    private static double Radius(PaintCodeShape shape, PaintCodeRect box)
        => Math.Max(0, Math.Min(shape.Metrics.CornerRadius, Math.Min(box.Width, box.Height) / 2));

    private static void Side(StringBuilder data, double x, double y, double radius, double cornerX, double cornerY)
    {
        data.Append('L').Append(Pair(new PaintCodePoint(x, y)));

        if (radius > 0)
        {
            data.Append('A').Append(Number(radius)).Append(' ').Append(Number(radius))
                .Append(" 0 0 1 ").Append(Pair(new PaintCodePoint(cornerX, cornerY)));
        }
    }

    // PaintCode measures an oval's sweep anticlockwise from the positive x axis in its own y-up
    // space, so in SVG's y-down space the same arc runs from -start to -end, turning clockwise.
    private static string Oval(PaintCodeShape shape)
    {
        var box = Box(shape);
        var metrics = shape.Metrics;
        var radiusX = box.Width / 2;
        var radiusY = box.Height / 2;
        var centerX = box.X + radiusX;
        var centerY = box.Y + radiusY;
        var turn = Turn(metrics);

        if (radiusX <= 0 || radiusY <= 0)
        {
            return string.Empty;
        }

        if (metrics.StartAngle == metrics.EndAngle || turn >= 360)
        {
            return Ellipse(centerX, centerY, radiusX, radiusY);
        }

        // A sweep that comes round to nothing draws nothing, which is not the same as drawing the
        // whole of it: thermostat-temperature-level asks for start -450 end 270, and PaintCode's own
        // sum brings that to nought.
        if (turn <= 0)
        {
            return string.Empty;
        }

        // PaintCode turns one way and never takes the short route, so the sweep is always positive
        // and always clockwise once the drawing is turned over. Reading the sign of start - end as
        // the direction instead drew the complement of every arc that ran more than half a turn --
        // the same two ends, the other way round, and 36 of the sample's canvases wrong by it.
        var start = OnEllipse(centerX, centerY, radiusX, radiusY, -metrics.StartAngle);
        var end = OnEllipse(centerX, centerY, radiusX, radiusY, -metrics.EndAngle);
        var data = new StringBuilder();

        data.Append('M').Append(Pair(start))
            .Append('A').Append(Number(radiusX)).Append(' ').Append(Number(radiusY)).Append(" 0 ")
            .Append(turn > 180 ? '1' : '0').Append(" 1 ")
            .Append(Pair(end));

        // Through the middle, not straight back: PaintCode closes an arc with LineTo(MidX, MidY),
        // which is a wedge rather than the segment a bare Z cuts off. The two enclose the same area
        // at exactly half a turn, where the chord is a diameter, and nowhere else.
        if (metrics.IsClosed)
        {
            data.Append('L').Append(Pair(new PaintCodePoint(centerX, centerY))).Append('Z');
        }

        return data.ToString();
    }

    // Two arcs, because one of 360 degrees starts and ends at the same point and draws nothing.
    private static string Ellipse(double centerX, double centerY, double radiusX, double radiusY)
        => new StringBuilder()
            .Append('M').Append(Pair(new PaintCodePoint(centerX - radiusX, centerY)))
            .Append('A').Append(Number(radiusX)).Append(' ').Append(Number(radiusY)).Append(" 0 1 0 ")
            .Append(Pair(new PaintCodePoint(centerX + radiusX, centerY)))
            .Append('A').Append(Number(radiusX)).Append(' ').Append(Number(radiusY)).Append(" 0 1 0 ")
            .Append(Pair(new PaintCodePoint(centerX - radiusX, centerY)))
            .Append('Z')
            .ToString();

    /// <summary>A shape that is only its corners: the line round them, closed.</summary>
    private static string Outline(IEnumerable<PaintCodePoint> corners)
    {
        var data = new StringBuilder();

        foreach (var corner in corners)
        {
            data.Append(data.Length == 0 ? 'M' : 'L').Append(Pair(corner));
        }

        return data.Append('Z').ToString();
    }

    private static PaintCodePoint OnEllipse(double centerX, double centerY, double radiusX, double radiusY, double degrees)
    {
        var radians = degrees * Math.PI / 180;

        return new PaintCodePoint(centerX + radiusX * Math.Cos(radians), centerY + radiusY * Math.Sin(radians));
    }

    private static PaintCodePoint Flip(PaintCodePoint point) => new(point.X, -point.Y);

    private static PaintCodePoint Add(PaintCodePoint point, PaintCodePoint offset)
        => new(point.X + offset.X, point.Y + offset.Y);

    private static bool IsZero(PaintCodePoint point) => point.X == 0 && point.Y == 0;

    private static string Pair(PaintCodePoint point) => Number(point.X) + "," + Number(point.Y);

    internal static string Number(double value)
    {
        var rounded = Math.Round(value, 4, MidpointRounding.AwayFromZero);

        // Negative zero prints as "-0", a needless difference between two identical drawings.
        return (rounded == 0 ? 0 : rounded).ToString("0.####", CultureInfo.InvariantCulture);
    }
}
