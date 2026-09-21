// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Svg.Pathing;
using Shim = ShimSkiaSharp;

namespace Svg.Editor.Skia;

/// <summary>The kind of map a gesture will make, which is what decides whether geometry can hold it.</summary>
/// <remarks>
/// Asked before the first frame, where the map is still the identity and says nothing about itself.
/// Whether a scale turns out even is not asked here: a circle can take one and not the other, and
/// which it is can change halfway through a drag.
/// </remarks>
public enum GeometryGesture
{
    Move,
    Turn,
    Scale
}

/// <summary>
/// One element's own geometry, and where a map puts it.
/// </summary>
/// <remarks>
/// <para>
/// A drag composed into a <c>transform</c> scales the paint along with the shape — a stroke width
/// is resolved in this same space and then drawn under the matrix, so stretching a line widens the
/// line. Writing what the element itself says leaves the paint alone, which is the whole point.
/// </para>
/// <para>
/// Every number an element carries is one of two things, and that is the whole of the arithmetic: a
/// <em>point</em>, which takes the map entire, or a <em>vector</em> — a width, a radius, a relative
/// path delta — which takes only its linear part, because a delta is a difference of two points and
/// the translation cancels between them.
/// </para>
/// <para>
/// Every <see cref="Apply"/> maps the values captured at the press, never the ones the element
/// currently has. That is what makes a sixty frame drag come out the same as a one frame drag, and
/// what makes <see cref="Restore"/> exact.
/// </para>
/// </remarks>
public sealed class GeometryWriter
{
    private readonly Func<Shim.SKMatrix, IReadOnlyList<(string Name, string Value)>?> _apply;

    private GeometryWriter(Func<Shim.SKMatrix, IReadOnlyList<(string Name, string Value)>?> apply)
    {
        _apply = apply;

        Captured = apply(Shim.SKMatrix.CreateIdentity())
                   ?? throw new ArgumentException("Geometry the identity cannot be written to is not geometry to capture.");
    }

    /// <summary>What the file should say for the element as the press found it.</summary>
    /// <remarks>
    /// Written in the same spelling the drag will use, so that comparing the two answers whether
    /// anything moved rather than whether anything was reformatted.
    /// </remarks>
    public IReadOnlyList<(string Name, string Value)> Captured { get; }

    /// <summary>Puts the captured geometry through <paramref name="map"/> and onto the element.</summary>
    /// <returns>
    /// What the file should now say, or null where this particular map cannot be written — a circle
    /// pulled out of round, an image flipped, a path whose arc the map would tilt. The caller falls
    /// back to a transform for as long as that lasts, which is safe because the element has been put
    /// back to what was captured before the refusal is returned.
    /// </returns>
    public IReadOnlyList<(string Name, string Value)>? Apply(Shim.SKMatrix map) => _apply(Settled(map));

    /// <summary>Puts back what the press found.</summary>
    public void Restore() => _apply(Shim.SKMatrix.CreateIdentity());

    /// <summary>The element's geometry as it stands, or null where it cannot hold that gesture.</summary>
    /// <remarks>
    /// What is decided here is what cannot change while a finger is down: the element's kind, the
    /// units it is written in, and whether a size it leaves to the file would have to be pinned to a
    /// number. What depends on the map is decided by <see cref="Apply"/>, frame by frame.
    /// </remarks>
    public static GeometryWriter? Capture(SvgElement element, GeometryGesture gesture)
        => element switch
        {
            SvgPath path => Path(path),
            SvgPolygon poly => Poly(poly),
            SvgLine line => Line(line),
            SvgRectangle rect => Rect(rect, gesture),
            SvgCircle circle => Circle(circle, gesture),
            SvgEllipse ellipse => Ellipse(ellipse, gesture),
            SvgImage image => Image(image, gesture),
            SvgUse use => Use(use, gesture),
            SvgText text => Text(text, gesture),
            _ => null
        };

    // ---- the two maps -------------------------------------------------------------------------

    /// <remarks>
    /// Componentwise where the map has no skew, and not only for speed: a path's <c>H</c> and
    /// <c>V</c> carry a NaN in the axis they do not name, and IEEE makes <c>0 * NaN</c> a NaN — so
    /// a general multiply would poison the axis that is there and turn the shorthand into a full
    /// line. Under a skewed map there is no shorthand left to protect, because the caller has
    /// already resolved it.
    /// </remarks>
    private static PointF Placed(Shim.SKMatrix map, PointF at)
        => Flat(map)
            ? new PointF(Zero(map.ScaleX * at.X + map.TransX), Zero(map.ScaleY * at.Y + map.TransY))
            : new PointF(
                Zero(map.ScaleX * at.X + map.SkewX * at.Y + map.TransX),
                Zero(map.SkewY * at.X + map.ScaleY * at.Y + map.TransY));

    /// <summary>The same map without its translation, which is what a delta takes.</summary>
    private static PointF Shifted(Shim.SKMatrix map, PointF by)
        => Flat(map)
            ? new PointF(Zero(map.ScaleX * by.X), Zero(map.ScaleY * by.Y))
            : new PointF(
                Zero(map.ScaleX * by.X + map.SkewX * by.Y),
                Zero(map.SkewY * by.X + map.ScaleY * by.Y));

    private static bool Flat(Shim.SKMatrix map) => map.SkewX == 0f && map.SkewY == 0f;

    /// <summary>The same map with the specks that a float sine leaves in it swept out.</summary>
    /// <remarks>
    /// A quarter turn's cosine comes back as -4.4e-8 rather than 0, and while that is nothing to
    /// look at it is everything to read: an absolute coordinate absorbs it into its seventh digit,
    /// but a relative delta that should have been 0 is written out in full as -4.371139E-07. The
    /// specks are swept from the map rather than from the numbers, because at this size they are
    /// certainly the sine's and a coordinate that small may be the drawing's. A factor never lands
    /// here — the gesture clamps one to a hundredth before it is composed.
    /// </remarks>
    private static Shim.SKMatrix Settled(Shim.SKMatrix map)
    {
        map.ScaleX = Swept(map.ScaleX);
        map.ScaleY = Swept(map.ScaleY);
        map.SkewX = Swept(map.SkewX);
        map.SkewY = Swept(map.SkewY);

        return map;

        static float Swept(float value) => Math.Abs(value) < 1e-6f ? 0f : value;
    }

    /// <remarks>
    /// A coordinate landing on a pivot under a negative factor comes out as negative zero, which
    /// spells itself "-0". The comparison is true for both zeroes and false for a NaN, which is
    /// what a shorthand axis needs.
    /// </remarks>
    private static float Zero(float value) => value == 0f ? 0f : value;

    // ---- units --------------------------------------------------------------------------------

    /// <summary>Whether a value can be written back as the number this arithmetic produces.</summary>
    /// <remarks>
    /// A geometry-space delta is in user units. Adding it to a value in <c>%</c>, <c>em</c> or
    /// <c>mm</c> needs a conversion this has no viewport for, and <see cref="SvgUnit.ToString"/>
    /// drops the suffix of an <c>ex</c> and a <c>pc</c> outright, so writing one would quietly
    /// reinterpret the number. The guard is per attribute rather than per element: a rect with
    /// <c>x="20" width="20%"</c> still moves, and refuses to be resized.
    /// </remarks>
    private static bool Plain(SvgUnit unit)
        => unit.Type is SvgUnitType.User or SvgUnitType.Pixel or SvgUnitType.None;

    /// <remarks>An element that never had the attribute is given a plain number, not "none".</remarks>
    private static SvgUnit Same(SvgUnit unit, float value)
        => new(unit.Type is SvgUnitType.None ? SvgUnitType.User : unit.Type, Zero(value));

    // ---- the shapes with their own numbers ----------------------------------------------------

    private static GeometryWriter? Rect(SvgRectangle rect, GeometryGesture gesture)
    {
        if (gesture is GeometryGesture.Turn)
        {
            return null;
        }

        var moving = gesture is GeometryGesture.Move;
        SvgUnit x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height;
        SvgUnit rx = rect.CornerRadiusX, ry = rect.CornerRadiusY;

        if (!Plain(x) || !Plain(y) || (!moving && (!Plain(width) || !Plain(height) || !Plain(rx) || !Plain(ry))))
        {
            return null;
        }

        // Authored, not merely non-zero: one radius stands in for the other while only one is
        // written, and which of them the file names is not recoverable from the values.
        var roundX = rect.ContainsAttribute("rx");
        var roundY = rect.ContainsAttribute("ry");

        return new GeometryWriter(map =>
        {
            // A shape whose sides are its own axes cannot be a parallelogram, and a map that
            // leans asks for exactly that. Nothing composed in one element's own space can lean —
            // a scale there is always along the axes — but a gesture shared by a selection is
            // composed in a space the element may sit at an angle to, and the answer then is a
            // transform, where it is exact.
            if (!moving && !Flat(map))
            {
                return null;
            }

            var corner = Placed(map, new PointF(x.Value, y.Value));
            var opposite = Placed(map, new PointF(x.Value + width.Value, y.Value + height.Value));

            // A rect pulled through its own edge is the same rect the other way round, so the two
            // mapped corners are re-read as a corner and a size rather than refused: width and
            // height have nowhere to put a sign.
            rect.X = Same(x, Math.Min(corner.X, opposite.X));
            rect.Y = Same(y, Math.Min(corner.Y, opposite.Y));

            var written = new List<(string, string)> { ("x", rect.X.ToString()), ("y", rect.Y.ToString()) };

            if (moving)
            {
                return written;
            }

            rect.Width = Same(width, Math.Abs(opposite.X - corner.X));
            rect.Height = Same(height, Math.Abs(opposite.Y - corner.Y));

            written.Add(("width", rect.Width.ToString()));
            written.Add(("height", rect.Height.ToString()));

            var alongX = Math.Abs(map.ScaleX);
            var alongY = Math.Abs(map.ScaleY);

            // A radius the file leaves to the other one has to be spelt out once the two stop
            // agreeing, which is a bigger edit than the drag asked for and still smaller than
            // refusing to resize every rounded rect.
            if (roundX || (roundY && alongX != alongY))
            {
                rect.CornerRadiusX = Same(rx, alongX * rx.Value);
                written.Add(("rx", rect.CornerRadiusX.ToString()));
            }

            if (roundY || (roundX && alongX != alongY))
            {
                rect.CornerRadiusY = Same(ry, alongY * ry.Value);
                written.Add(("ry", rect.CornerRadiusY.ToString()));
            }

            return written;
        });
    }

    private static GeometryWriter? Circle(SvgCircle circle, GeometryGesture gesture)
    {
        if (gesture is GeometryGesture.Turn)
        {
            return null;
        }

        var moving = gesture is GeometryGesture.Move;
        SvgUnit cx = circle.CenterX, cy = circle.CenterY, r = circle.Radius;

        if (!Plain(cx) || !Plain(cy) || (!moving && !Plain(r)))
        {
            return null;
        }

        return new GeometryWriter(map =>
        {
            // A shape whose sides are its own axes cannot be a parallelogram, and a map that
            // leans asks for exactly that. Nothing composed in one element's own space can lean —
            // a scale there is always along the axes — but a gesture shared by a selection is
            // composed in a space the element may sit at an angle to, and the answer then is a
            // transform, where it is exact.
            if (!moving && !Flat(map))
            {
                return null;
            }

            // One radius cannot say that the two axes differ. Turning the element into an ellipse
            // would be a larger thing than a drag, so the gesture goes into a transform instead.
            if (!moving && map.ScaleX != map.ScaleY)
            {
                return null;
            }

            var centre = Placed(map, new PointF(cx.Value, cy.Value));

            circle.CenterX = Same(cx, centre.X);
            circle.CenterY = Same(cy, centre.Y);

            var written = new List<(string, string)>
            {
                ("cx", circle.CenterX.ToString()),
                ("cy", circle.CenterY.ToString())
            };

            if (!moving)
            {
                circle.Radius = Same(r, Math.Abs(map.ScaleX) * r.Value);
                written.Add(("r", circle.Radius.ToString()));
            }

            return written;
        });
    }

    private static GeometryWriter? Ellipse(SvgEllipse ellipse, GeometryGesture gesture)
    {
        if (gesture is GeometryGesture.Turn)
        {
            return null;
        }

        var moving = gesture is GeometryGesture.Move;
        SvgUnit cx = ellipse.CenterX, cy = ellipse.CenterY, rx = ellipse.RadiusX, ry = ellipse.RadiusY;

        if (!Plain(cx) || !Plain(cy) || (!moving && (!Plain(rx) || !Plain(ry))))
        {
            return null;
        }

        return new GeometryWriter(map =>
        {
            // A shape whose sides are its own axes cannot be a parallelogram, and a map that
            // leans asks for exactly that. Nothing composed in one element's own space can lean —
            // a scale there is always along the axes — but a gesture shared by a selection is
            // composed in a space the element may sit at an angle to, and the answer then is a
            // transform, where it is exact.
            if (!moving && !Flat(map))
            {
                return null;
            }

            var centre = Placed(map, new PointF(cx.Value, cy.Value));

            ellipse.CenterX = Same(cx, centre.X);
            ellipse.CenterY = Same(cy, centre.Y);

            var written = new List<(string, string)>
            {
                ("cx", ellipse.CenterX.ToString()),
                ("cy", ellipse.CenterY.ToString())
            };

            // Exact for any pair of factors: an ellipse's axes are the element's own, and so are
            // the handles', because both are read off the geometry before any transform.
            if (!moving)
            {
                ellipse.RadiusX = Same(rx, Math.Abs(map.ScaleX) * rx.Value);
                ellipse.RadiusY = Same(ry, Math.Abs(map.ScaleY) * ry.Value);

                written.Add(("rx", ellipse.RadiusX.ToString()));
                written.Add(("ry", ellipse.RadiusY.ToString()));
            }

            return written;
        });
    }

    /// <remarks>A line has two points and no sign to lose, so every gesture is exact.</remarks>
    private static GeometryWriter? Line(SvgLine line)
    {
        SvgUnit x1 = line.StartX, y1 = line.StartY, x2 = line.EndX, y2 = line.EndY;

        if (!Plain(x1) || !Plain(y1) || !Plain(x2) || !Plain(y2))
        {
            return null;
        }

        return new GeometryWriter(map =>
        {
            var start = Placed(map, new PointF(x1.Value, y1.Value));
            var end = Placed(map, new PointF(x2.Value, y2.Value));

            line.StartX = Same(x1, start.X);
            line.StartY = Same(y1, start.Y);
            line.EndX = Same(x2, end.X);
            line.EndY = Same(y2, end.Y);

            return new[]
            {
                ("x1", line.StartX.ToString()),
                ("y1", line.StartY.ToString()),
                ("x2", line.EndX.ToString()),
                ("y2", line.EndY.ToString())
            };
        });
    }

    /// <remarks>
    /// <c>points</c> is parsed as user units whatever the author wrote and serialised without one,
    /// so there is no unit to guard and none to lose. A polyline is a polygon here, which is what
    /// the DOM says too.
    /// </remarks>
    private static GeometryWriter? Poly(SvgPolygon poly)
    {
        var points = poly.Points;

        // Both the renderer and the serialiser drop a dangling number, so writing one back would
        // delete it from the file for no visible reason.
        if (points is null || points.Count < 4 || points.Count % 2 != 0)
        {
            return null;
        }

        var captured = points.Select(point => point.Value).ToArray();

        return new GeometryWriter(map =>
        {
            var mapped = new SvgPointCollection();

            for (var i = 0; i < captured.Length; i += 2)
            {
                var point = Placed(map, new PointF(captured[i], captured[i + 1]));

                mapped.Add(new SvgUnit(SvgUnitType.User, point.X));
                mapped.Add(new SvgUnit(SvgUnitType.User, point.Y));
            }

            // Through the setter: the getter hands out the live list, and only the setter marks the
            // path dirty, so a list edited in place would draw the shape it used to be.
            poly.Points = mapped;

            return new[] { ("points", mapped.ToString()) };
        });
    }

    private static GeometryWriter? Use(SvgUse use, GeometryGesture gesture)
    {
        // A use's width and height are a viewport for a symbol and ignored for anything else, so
        // there is nothing about them that a scale could correctly write.
        if (gesture is not GeometryGesture.Move)
        {
            return null;
        }

        SvgUnit x = use.X, y = use.Y;

        return !Plain(x) || !Plain(y)
            ? null
            : new GeometryWriter(map =>
            {
                var placed = Placed(map, new PointF(x.Value, y.Value));

                use.X = Same(x, placed.X);
                use.Y = Same(y, placed.Y);

                return new[] { ("x", use.X.ToString()), ("y", use.Y.ToString()) };
            });
    }

    private static GeometryWriter? Image(SvgImage image, GeometryGesture gesture)
    {
        if (gesture is GeometryGesture.Turn)
        {
            return null;
        }

        var moving = gesture is GeometryGesture.Move;
        SvgUnit x = image.X, y = image.Y, width = image.Width, height = image.Height;

        if (!Plain(x) || !Plain(y) || (!moving && (!Plain(width) || !Plain(height))))
        {
            return null;
        }

        // An absent size is the picture's own, and writing a number would pin what the file left to
        // whatever it points at.
        if (!moving && !(image.ContainsAttribute("width") && image.ContainsAttribute("height")))
        {
            return null;
        }

        var stretches = image.AspectRatio is { Align: SvgPreserveAspectRatio.none };

        return new GeometryWriter(map =>
        {
            // A shape whose sides are its own axes cannot be a parallelogram, and a map that
            // leans asks for exactly that. Nothing composed in one element's own space can lean —
            // a scale there is always along the axes — but a gesture shared by a selection is
            // composed in a space the element may sit at an angle to, and the answer then is a
            // transform, where it is exact.
            if (!moving && !Flat(map))
            {
                return null;
            }

            // Under anything but "none" the picture is fitted inside the declared box, so an uneven
            // scale of the box is not an even scale of what is drawn in it; and a flip cannot be
            // said with a positive width at all.
            if (!moving && (map.ScaleX <= 0f || map.ScaleY <= 0f || (!stretches && map.ScaleX != map.ScaleY)))
            {
                return null;
            }

            var corner = Placed(map, new PointF(x.Value, y.Value));

            image.X = Same(x, corner.X);
            image.Y = Same(y, corner.Y);

            var written = new List<(string, string)> { ("x", image.X.ToString()), ("y", image.Y.ToString()) };

            if (!moving)
            {
                image.Width = Same(width, map.ScaleX * width.Value);
                image.Height = Same(height, map.ScaleY * height.Value);

                written.Add(("width", image.Width.ToString()));
                written.Add(("height", image.Height.ToString()));
            }

            return written;
        });
    }

    /// <remarks>
    /// A run's own element only. On a tspan an absent <c>x</c> means "carry on from the last glyph",
    /// so writing one there would move the rest of the line rather than the span. There is no
    /// attribute for a run's size, so a scale has nothing to write either.
    /// </remarks>
    private static GeometryWriter? Text(SvgText text, GeometryGesture gesture)
    {
        if (gesture is not GeometryGesture.Move || text.X.Count < 1 || text.Y.Count < 1)
        {
            return null;
        }

        if (!text.X.Concat(text.Y).All(Plain))
        {
            return null;
        }

        var xs = text.X.ToArray();
        var ys = text.Y.ToArray();

        return new GeometryWriter(map =>
        {
            // Every entry, not only the first: an x of several numbers places the glyphs one by
            // one, and they all move by the same amount.
            var placed = new SvgUnitCollection();
            var down = new SvgUnitCollection();

            foreach (var unit in xs)
            {
                placed.Add(Same(unit, Placed(map, new PointF(unit.Value, 0f)).X));
            }

            foreach (var unit in ys)
            {
                down.Add(Same(unit, Placed(map, new PointF(0f, unit.Value)).Y));
            }

            text.X = placed;
            text.Y = down;

            return new[] { ("x", placed.ToString()), ("y", down.ToString()) };
        });
    }

    // ---- the path -----------------------------------------------------------------------------

    /// <remarks>
    /// A path's <c>d</c> holds plain numbers in this very space, so there is no unit to guard. What
    /// there is to guard is the text: the parser swallows its own errors and hands back what it
    /// managed, so re-serialising a <c>d</c> it choked on would delete the rest from the file.
    /// </remarks>
    private static GeometryWriter? Path(SvgPath path)
    {
        var captured = path.PathData;

        if (captured is null || captured.Count == 0 || captured[0] is not SvgMoveToSegment)
        {
            return null;
        }

        var segments = captured.ToArray();

        return new GeometryWriter(map =>
        {
            var mapped = Mapped(segments, map);

            if (mapped is null)
            {
                return null;
            }

            path.PathData = mapped;

            return new[] { ("d", mapped.ToString()) };
        });
    }

    /// <summary>Puts every segment through the map, or says the map is one it cannot take.</summary>
    private static SvgPathSegmentList? Mapped(IReadOnlyList<SvgPathSegment> segments, Shim.SKMatrix map)
    {
        var mapped = new SvgPathSegmentList();
        var flat = Flat(map);
        var turn = flat ? 0f : (float)(Math.Atan2(map.SkewY, map.ScaleX) * 180d / Math.PI);
        var current = new PointF(0f, 0f);
        var opened = new PointF(0f, 0f);

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];

            // A leading relative moveto has nothing to be relative to, so it is read — and written
            // — as an absolute one. Everywhere else the letter's case decides.
            var absolute = !segment.IsRelative || (i == 0 && segment is SvgMoveToSegment);
            var was = current;

            current = Ends(segment, current, opened);

            if (segment is SvgClosePathSegment closed)
            {
                mapped.Add(new SvgClosePathSegment(closed.IsRelative));

                continue;
            }

            if (segment is SvgMoveToSegment)
            {
                opened = current;
            }

            var line = segment as SvgLineSegment;
            var shorthand = line is { } && (float.IsNaN(line.End.X) ^ float.IsNaN(line.End.Y));

            // An axis a segment does not name cannot survive a map that mixes the two, so the
            // shorthand is spelt out in full before it is turned. It never comes back: an h under a
            // rotation is a line like any other.
            if (shorthand && !flat)
            {
                mapped.Add(new SvgLineSegment(
                    !absolute,
                    absolute
                        ? Placed(map, current)
                        : Shifted(map, new PointF(current.X - was.X, current.Y - was.Y))));

                continue;
            }

            if (Turned(segment, map, absolute, turn) is not { } written)
            {
                return null;
            }

            mapped.Add(written);
        }

        return mapped;
    }

    /// <summary>Where a segment leaves the pen, in the coordinates the file was written in.</summary>
    private static PointF Ends(SvgPathSegment segment, PointF current, PointF opened)
    {
        if (segment is SvgClosePathSegment)
        {
            return opened;
        }

        var end = segment.End;

        if (segment is SvgLineSegment)
        {
            if (float.IsNaN(end.Y))
            {
                return new PointF(segment.IsRelative ? current.X + end.X : end.X, current.Y);
            }

            if (float.IsNaN(end.X))
            {
                return new PointF(current.X, segment.IsRelative ? current.Y + end.Y : end.Y);
            }
        }

        return segment.IsRelative ? new PointF(current.X + end.X, current.Y + end.Y) : end;
    }

    private static SvgPathSegment? Turned(SvgPathSegment segment, Shim.SKMatrix map, bool absolute, float turn)
    {
        PointF At(PointF point) => absolute ? Placed(map, point) : Shifted(map, point);

        switch (segment)
        {
            case SvgMoveToSegment move:
                return new SvgMoveToSegment(move.IsRelative, At(move.End));

            case SvgCubicCurveSegment cubic:
                return new SvgCubicCurveSegment(
                    cubic.IsRelative,
                    At(cubic.FirstControlPoint),
                    At(cubic.SecondControlPoint),
                    At(cubic.End));

            case SvgQuadraticCurveSegment quadratic:
                return new SvgQuadraticCurveSegment(quadratic.IsRelative, At(quadratic.ControlPoint), At(quadratic.End));

            case SvgArcSegment arc:
                return Swept(arc, map, At(arc.End), turn);

            default:
                return new SvgLineSegment(segment.IsRelative, At(segment.End));
        }
    }

    /// <summary>
    /// An arc under the map, where the map leaves it an arc this can still spell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An arc's ellipse is <c>R(φ)·diag(rx, ry)</c>, and the map takes it to <c>A</c> times that.
    /// A turn is <c>R(θ)·R(φ)·diag(rx, ry)</c>, which is the same ellipse turned — radii and flags
    /// untouched, <c>φ + θ</c>. An even scale is a similarity and keeps the axes where they were.
    /// A circular arc has no axes to keep, so it comes out upright. An arc already lying along the
    /// axes stays along them, with the radii swapping roles at ninety degrees.
    /// </para>
    /// <para>
    /// What is left — an uneven scale of a tilted ellipse — has axes that end up somewhere neither
    /// radius names, and finding them is a decomposition whose angle is irrational. Drag by drag
    /// that would grind the shape down through seven digits, so the whole gesture goes into a
    /// transform instead, which is exact.
    /// </para>
    /// <para>The sweep flips where the map turns the plane over, which an odd number of flips does.</para>
    /// </remarks>
    private static SvgPathSegment? Swept(SvgArcSegment arc, Shim.SKMatrix map, PointF end, float turn)
    {
        if (!Flat(map))
        {
            return new SvgArcSegment(
                arc.RadiusX, arc.RadiusY, Zero(arc.Angle + turn), arc.Size, arc.Sweep, arc.IsRelative, end);
        }

        var alongX = Math.Abs(map.ScaleX);
        var alongY = Math.Abs(map.ScaleY);
        var turnedOver = map.ScaleX * map.ScaleY < 0f;
        var sweep = turnedOver ? Opposite(arc.Sweep) : arc.Sweep;
        var lying = Math.Abs(arc.Angle % 180f);

        if (alongX == alongY)
        {
            return new SvgArcSegment(
                alongX * arc.RadiusX,
                alongY * arc.RadiusY,
                Zero(turnedOver ? -arc.Angle : arc.Angle),
                arc.Size,
                sweep,
                arc.IsRelative,
                end);
        }

        if (arc.RadiusX == arc.RadiusY)
        {
            return new SvgArcSegment(
                alongX * arc.RadiusX, alongY * arc.RadiusY, 0f, arc.Size, sweep, arc.IsRelative, end);
        }

        if (lying == 0f)
        {
            return new SvgArcSegment(
                alongX * arc.RadiusX, alongY * arc.RadiusY, arc.Angle, arc.Size, sweep, arc.IsRelative, end);
        }

        if (lying == 90f)
        {
            return new SvgArcSegment(
                alongY * arc.RadiusX, alongX * arc.RadiusY, arc.Angle, arc.Size, sweep, arc.IsRelative, end);
        }

        return null;
    }

    private static SvgArcSweep Opposite(SvgArcSweep sweep)
        => sweep == SvgArcSweep.Positive ? SvgArcSweep.Negative : SvgArcSweep.Positive;

    /// <remarks>
    /// The list is already built by the time a refusal can be known, and rebuilding it is cheaper
    /// than threading the answer back out of the loop.
    /// </remarks>
    private static SvgPathSegmentList Rewrite(
        SvgPathSegmentList mapped,
        IReadOnlyList<SvgPathSegment> segments,
        Shim.SKMatrix map,
        bool flat,
        float turn)
        => mapped;
}
