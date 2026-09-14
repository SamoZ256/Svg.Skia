// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
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
            PaintCodeShapeKind.Star => Star(shape),
            PaintCodeShapeKind.Polygon => Polygon(shape),
            _ => null
        };

    /// <summary>The shape's box in SVG coordinates, relative to the shape's own origin.</summary>
    internal static PaintCodeRect Box(PaintCodeShape shape)
    {
        var frame = shape.Frame;

        return new PaintCodeRect(frame.X, -(frame.Y + frame.Height), frame.Width, frame.Height);
    }

    /// <summary>Whether the shape is a plain rectangle, which SVG has an element for.</summary>
    internal static bool IsPlainRectangle(PaintCodeShape shape)
    {
        var metrics = shape.Metrics;

        return shape.Kind is PaintCodeShapeKind.Rectangle or PaintCodeShapeKind.RoundedRectangle &&
               (metrics.CornerRadius <= 0 ||
                (metrics.TopLeftRounded && metrics.TopRightRounded && metrics.BottomLeftRounded && metrics.BottomRightRounded));
    }

    /// <summary>Whether the shape is a whole ellipse rather than an arc of one.</summary>
    internal static bool IsWholeEllipse(PaintCodeShape shape)
        => shape.Kind is PaintCodeShapeKind.Oval && Math.Abs(shape.Metrics.StartAngle - shape.Metrics.EndAngle) >= 360;

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
        var radius = Math.Max(0, Math.Min(metrics.CornerRadius, Math.Min(box.Width, box.Height) / 2));
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
        var sweep = metrics.StartAngle - metrics.EndAngle;

        if (radiusX <= 0 || radiusY <= 0)
        {
            return string.Empty;
        }

        if (Math.Abs(sweep) >= 360)
        {
            return Ellipse(centerX, centerY, radiusX, radiusY);
        }

        var start = OnEllipse(centerX, centerY, radiusX, radiusY, -metrics.StartAngle);
        var end = OnEllipse(centerX, centerY, radiusX, radiusY, -metrics.EndAngle);
        var data = new StringBuilder();

        data.Append('M').Append(Pair(start))
            .Append('A').Append(Number(radiusX)).Append(' ').Append(Number(radiusY)).Append(" 0 ")
            .Append(Math.Abs(sweep) > 180 ? '1' : '0').Append(' ')
            .Append(sweep > 0 ? '1' : '0').Append(' ')
            .Append(Pair(end));

        if (metrics.IsClosed)
        {
            data.Append('Z');
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

    private static string Star(PaintCodeShape shape)
    {
        var box = Box(shape);
        var metrics = shape.Metrics;
        var points = Math.Max(3, metrics.Sides);
        var data = new StringBuilder();

        for (var index = 0; index < points * 2; index++)
        {
            var scale = index % 2 == 0 ? 1d : Math.Max(0, metrics.InnerRadiusPercentage);

            data.Append(index == 0 ? 'M' : 'L').Append(Pair(OnEllipse(
                box.X + box.Width / 2,
                box.Y + box.Height / 2,
                box.Width / 2 * scale,
                box.Height / 2 * scale,
                -90 + index * 180d / points)));
        }

        return data.Append('Z').ToString();
    }

    private static string Polygon(PaintCodeShape shape)
    {
        var box = Box(shape);
        var sides = Math.Max(3, shape.Metrics.Sides);
        var data = new StringBuilder();

        for (var index = 0; index < sides; index++)
        {
            data.Append(index == 0 ? 'M' : 'L').Append(Pair(OnEllipse(
                box.X + box.Width / 2,
                box.Y + box.Height / 2,
                box.Width / 2,
                box.Height / 2,
                -90 + index * 360d / sides)));
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
