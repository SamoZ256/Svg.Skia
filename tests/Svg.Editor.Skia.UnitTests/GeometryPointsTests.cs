using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Svg.Pathing;
using Xunit;

namespace Svg.Editor.Skia.UnitTests;

/// <summary>
/// Taking one point of a shape at a time: moving it, adding one, removing one.
/// </summary>
/// <remarks>
/// Against the model directly, with exact strings: what these pin is that a segment keeps its
/// letter and case, that what an edit does not reach is written as it was, and the refusals.
/// </remarks>
public class GeometryPointsTests
{
    private static string Says(IReadOnlyList<(string Name, string Value)>? written)
        => written is null
            ? "refused"
            : string.Join(" ", written.Select(pair => $"{pair.Name}={pair.Value}"));

    private static SvgPath Drawn(string data) => new() { PathData = SvgPathBuilder.Parse(data) };

    private static SvgPolygon Cornered(params float[] coordinates) => Pointed(new SvgPolygon(), coordinates);

    private static SvgPolyline Bent(params float[] coordinates) => Pointed(new SvgPolyline(), coordinates);

    private static T Pointed<T>(T poly, float[] coordinates) where T : SvgPolygon
    {
        var points = new SvgPointCollection();

        points.AddRange(coordinates.Select(value => new SvgUnit(SvgUnitType.User, value)));
        poly.Points = points;

        return poly;
    }

    private static GeometryPoints Captured(SvgElement element)
    {
        var points = GeometryPoints.Capture(element);

        Assert.NotNull(points);

        return points!;
    }

    private static int Anchor(GeometryPoints points, float x, float y) => Find(points, GeometryPointKind.Anchor, x, y);

    private static int Handle(GeometryPoints points, float x, float y) => Find(points, GeometryPointKind.Handle, x, y);

    private static int Find(GeometryPoints points, GeometryPointKind kind, float x, float y)
    {
        for (var i = 0; i < points.Points.Count; i++)
        {
            if (points.Points[i].Kind == kind && points.Points[i].At == new PointF(x, y))
            {
                return i;
            }
        }

        throw new InvalidOperationException($"No {kind} at {x},{y}.");
    }

    private static string Moved(string data, GeometryPointKind kind, PointF from, PointF to, bool smooth = true)
    {
        var points = Captured(Drawn(data));

        return Says(points.Move(Find(points, kind, from.X, from.Y), to, smooth));
    }

    private static string Removed(SvgElement element, float x, float y)
    {
        var points = Captured(element);
        var refusal = points.Remove(Anchor(points, x, y), out var written);

        return refusal ?? Says(written);
    }

    private static string Inserted(SvgElement element, int segment, float t)
    {
        var points = Captured(element);
        var refusal = points.Insert(segment, t, out var written, out _);

        return refusal ?? Says(written);
    }

    // ---- what there is to take hold of --------------------------------------------------------

    [Fact]
    public void A_Path_Lists_Its_Anchors_And_The_Handles_Of_Its_Curves()
    {
        var points = Captured(Drawn("M0,0 C0,10 10,10 10,0 L20,0"));

        Assert.Equal(
            new[] { "Anchor 0,0", "Anchor 10,0", "Handle 0,10", "Handle 10,10", "Anchor 20,0" },
            points.Points.Select(point => $"{point.Kind} {point.At.X},{point.At.Y}"));
        Assert.Equal(0, points.Points[Handle(points, 0f, 10f)].Anchor);
        Assert.Equal(1, points.Points[Handle(points, 10f, 10f)].Anchor);
    }

    /// <summary>
    /// The last point of a closed path written on top of the first is one point, not two.
    /// </summary>
    /// <remarks>
    /// That is how most icons close: back to the start explicitly, then <c>Z</c>. Two points in one
    /// place would be one to take hold of and one left behind to tear the outline.
    /// </remarks>
    [Fact]
    public void A_Closing_Point_On_The_First_Is_The_First()
    {
        var points = Captured(Drawn("M10,10 L30,10 L30,30 L10,10 Z"));

        Assert.Equal(3, points.Points.Count);
        Assert.Equal("d=M5 5 L30 10 L30 30 L5 5 Z", Says(points.Move(Anchor(points, 10f, 10f), new PointF(5f, 5f))));
    }

    [Fact]
    public void A_Chosen_Anchor_Shows_The_Handles_Of_Both_Its_Segments()
    {
        var points = Captured(Drawn("M0,0 C0,10 10,10 10,0 C10,-10 20,-10 20,0"));

        Assert.Equal(4, points.Around(Anchor(points, 10f, 0f)).Count);
        Assert.Equal(
            new[] { Handle(points, 0f, 10f), Handle(points, 10f, 10f) },
            points.Around(Anchor(points, 0f, 0f)));
    }

    [Fact]
    public void Shapes_Without_Points_Of_Their_Own_Have_None_To_Capture()
    {
        Assert.Null(GeometryPoints.Capture(new SvgRectangle { Width = 10f, Height = 10f }));
        Assert.Null(GeometryPoints.Capture(new SvgLine { EndX = new SvgUnit(SvgUnitType.Percentage, 50f) }));
        Assert.Null(GeometryPoints.Capture(Bent(0f, 0f, 10f)));
    }

    // ---- moving -------------------------------------------------------------------------------

    [Fact]
    public void An_Anchor_Moves_And_Nothing_Else_Does()
    {
        Assert.Equal(
            "d=M10 10 L40 12 L30 30",
            Moved("M10,10 L30,10 L30,30", GeometryPointKind.Anchor, new PointF(30f, 10f), new PointF(40f, 12f)));
    }

    /// <summary>
    /// A relative path stays relative, and what comes after the moved point stays where it was.
    /// </summary>
    /// <remarks>
    /// The segment after a moved anchor is measured from it, so its delta is rewritten to land where
    /// it landed before.
    /// </remarks>
    [Fact]
    public void A_Relative_Path_Keeps_Its_Later_Points_Where_They_Were()
    {
        Assert.Equal(
            "d=m10 10 l30 0 l-10 20",
            Moved("m10,10 l20,0 l0,20", GeometryPointKind.Anchor, new PointF(30f, 10f), new PointF(40f, 10f)));
    }

    /// <summary>
    /// An <c>h</c> stays an <c>h</c> while its point stays level, and is spelt out once it does not.
    /// </summary>
    [Fact]
    public void Shorthand_Survives_A_Move_Along_Its_Own_Axis()
    {
        Assert.Equal(
            "d=M10 10 H40 L30 30",
            Moved("M10,10 H30 V30", GeometryPointKind.Anchor, new PointF(30f, 10f), new PointF(40f, 10f)));
        Assert.Equal(
            "d=M10 10 L30 15 V30",
            Moved("M10,10 H30 V30", GeometryPointKind.Anchor, new PointF(30f, 10f), new PointF(30f, 15f)));
        Assert.Equal(
            "d=m10 10 h25 l-5 20",
            Moved("m10,10 h20 v20", GeometryPointKind.Anchor, new PointF(30f, 10f), new PointF(35f, 10f)));
    }

    [Fact]
    public void An_Anchor_Carries_Its_Handles()
    {
        Assert.Equal(
            "d=M0 0 C0 10 12 12 12 2 C12 -8 20 -10 20 0",
            Moved("M0,0 C0,10 10,10 10,0 C10,-10 20,-10 20,0", GeometryPointKind.Anchor, new PointF(10f, 0f), new PointF(12f, 2f)));
    }

    [Fact]
    public void A_Handle_Moves_Its_Own_Control()
    {
        Assert.Equal(
            "d=M0 0 C0 10 25 10 20 0",
            Moved("M0,0 C0,10 20,10 20,0", GeometryPointKind.Handle, new PointF(20f, 10f), new PointF(25f, 10f)));
    }

    /// <summary>
    /// An S reflects the handle before it, so moving that handle swings both, and the S is written as it was.
    /// </summary>
    [Fact]
    public void A_Handle_Before_An_S_Is_Mirrored_Through_It()
    {
        Assert.Equal(
            "d=M0 0 C0 10 12 10 10 0 S20 -10 20 0",
            Moved("M0,0 C0,10 10,10 10,0 S20,-10 20,0", GeometryPointKind.Handle, new PointF(10f, 10f), new PointF(12f, 10f)));
    }

    /// <summary>
    /// An S's own first handle is a reflection; dragging it makes the S a C, and swings the handle
    /// across the anchor round with it so the curve stays smooth.
    /// </summary>
    [Fact]
    public void Dragging_An_S_Reflection_Spells_It_Out_And_Keeps_It_Smooth()
    {
        Assert.Equal(
            "d=M0 0 C0 10 11.961 9.806 10 0 C8 -10 20 -10 20 0",
            Moved("M0,0 C0,10 10,10 10,0 S20,-10 20,0", GeometryPointKind.Handle, new PointF(10f, -10f), new PointF(8f, -10f)));
        Assert.Equal(
            "d=M0 0 C0 10 10 10 10 0 C8 -10 20 -10 20 0",
            Moved("M0,0 C0,10 10,10 10,0 S20,-10 20,0", GeometryPointKind.Handle, new PointF(10f, -10f), new PointF(8f, -10f), smooth: false));
    }

    /// <summary>
    /// A corner's handles do not line up, and a handle sitting on its anchor points nowhere, so
    /// neither is turned.
    /// </summary>
    [Fact]
    public void A_Handle_Across_A_Corner_Stays_Put()
    {
        var points = Captured(Drawn("M0,0 C0,10 10,0 10,0 C10,0 20,10 20,0"));
        var handle = points.Points
            .Select((point, index) => (point, index))
            .Single(pair => pair.point.Kind == GeometryPointKind.Handle && pair.point.Segment == 2 && pair.point.Which == 1)
            .index;

        Assert.Equal("d=M0 0 C0 10 10 0 10 0 C12 5 20 10 20 0", Says(points.Move(handle, new PointF(12f, 5f))));
    }

    [Fact]
    public void A_Quadratic_Control_Moves_And_A_T_After_It_Follows()
    {
        Assert.Equal(
            "d=M0 0 Q10 20 20 0 T40 0",
            Moved("M0,0 Q10,10 20,0 T40,0", GeometryPointKind.Handle, new PointF(10f, 10f), new PointF(10f, 20f)));
    }

    [Fact]
    public void An_Arc_Keeps_Its_Radii_And_Flags_When_Its_End_Moves()
    {
        Assert.Equal(
            "d=M0 0 A10 10 0 0 1 20 5",
            Moved("M0,0 A10,10 0 0 1 20,0", GeometryPointKind.Anchor, new PointF(20f, 0f), new PointF(20f, 5f)));
    }

    /// <summary>A path written to four decimals is moved to four, so its own numbers are not rounded away.</summary>
    [Fact]
    public void Numbers_Are_Written_To_The_Decimals_The_Path_Uses()
    {
        Assert.Equal(
            "d=M0.1234 0 L10.1235 0",
            Moved("M0.1234,0 L10,0", GeometryPointKind.Anchor, new PointF(10f, 0f), new PointF(10.123456f, 0f)));
    }

    [Fact]
    public void A_Polygon_Moves_One_Corner()
    {
        var points = Captured(Cornered(0f, 0f, 10f, 0f, 10f, 10f));

        Assert.Equal("points=0,0 20,0 10,10", Says(points.Move(1, new PointF(20f, 0f))));
    }

    [Fact]
    public void A_Line_Moves_One_End_In_Its_Own_Unit()
    {
        var line = new SvgLine
        {
            EndX = new SvgUnit(SvgUnitType.Pixel, 10f),
            EndY = new SvgUnit(SvgUnitType.Pixel, 10f)
        };
        var points = Captured(line);

        Assert.Equal("x2=20px y2=5px", Says(points.Move(1, new PointF(20f, 5f))));
    }

    [Fact]
    public void Restore_Puts_Back_What_Was_Captured()
    {
        var path = Drawn("m10,10 h20 v20");
        var before = path.PathData.ToString();
        var points = Captured(path);

        points.Move(Anchor(points, 30f, 10f), new PointF(35f, 12f));
        points.Restore();

        Assert.Equal(before, path.PathData.ToString());
    }

    // ---- removing -----------------------------------------------------------------------------

    [Fact]
    public void Removing_A_Point_Between_Two_Lines_Leaves_One_Line()
    {
        Assert.Equal("d=M0 0 L20 10", Removed(Drawn("M0,0 L10,0 L20,10"), 10f, 0f));
        Assert.Equal("d=m0 0 l20 10", Removed(Drawn("m0,0 l10,0 l10,10"), 10f, 0f));
    }

    /// <summary>A point between a line and a curve leaves a curve that keeps the curve's far handle.</summary>
    [Fact]
    public void Removing_A_Point_Before_A_Curve_Keeps_Its_Far_Handle()
    {
        Assert.Equal("d=M0 0 C0 0 20 10 20 0", Removed(Drawn("M0,0 L10,0 C10,10 20,10 20,0"), 10f, 0f));
    }

    [Fact]
    public void Removing_A_Point_Keeps_An_S_After_It_Where_It_Was()
    {
        Assert.Equal(
            "d=M0 0 C0 0 20 10 20 0 S30 -10 30 0",
            Removed(Drawn("M0,0 L10,0 C10,10 20,10 20,0 S30,-10 30,0"), 10f, 0f));
    }

    [Fact]
    public void Removing_The_First_Or_Last_Point_Of_An_Open_Path_Shortens_It()
    {
        Assert.Equal("d=M10 0 L20 0", Removed(Drawn("M0,0 L10,0 L20,0"), 0f, 0f));
        Assert.Equal("d=M0 0 L10 0", Removed(Drawn("M0,0 L10,0 L20,0"), 20f, 0f));
    }

    /// <summary>
    /// A closed path has no first point to speak of: removing where it opens joins the segments
    /// either side, and it opens at the next one.
    /// </summary>
    [Fact]
    public void Removing_Where_A_Closed_Path_Opens_Joins_Round_The_Loop()
    {
        Assert.Equal("d=M30 10 L30 30 L30 10 Z", Removed(Drawn("M10,10 L30,10 L30,30 L10,10 Z"), 10f, 10f));
        Assert.Equal("d=M30 10 L30 30 Z", Removed(Drawn("M10,10 L30,10 L30,30 Z"), 10f, 10f));
        Assert.Equal(
            "d=M30 10 L30 30 C30 30 40 10 30 10 Z",
            Removed(Drawn("M10,10 C20,0 40,10 30,10 L30,30 Z"), 10f, 10f));
    }

    [Fact]
    public void Removing_The_Last_Curve_Before_A_Close_Keeps_It_A_Curve()
    {
        Assert.Equal("d=M0 0 L10 0 C10 10 0 0 0 0 Z", Removed(Drawn("M0,0 L10,0 C10,10 0,10 0,5 Z"), 0f, 5f));
    }

    [Fact]
    public void Removing_A_Point_Before_An_Arc_Starts_The_Arc_Sooner()
    {
        Assert.Equal("d=M0 0 A5 5 0 0 1 20 0", Removed(Drawn("M0,0 L10,0 A5,5 0 0 1 20,0"), 10f, 0f));
    }

    [Fact]
    public void Too_Few_Points_Are_Not_Removed()
    {
        Assert.Equal("A path needs two points here; delete the element instead.", Removed(Drawn("M0,0 L10,0"), 10f, 0f));
        Assert.Equal("A polygon needs three corners; delete the element instead.", Removed(Cornered(0f, 0f, 10f, 0f, 10f, 10f), 10f, 0f));
        Assert.Equal("A polyline needs two points; delete the element instead.", Removed(Bent(0f, 0f, 10f, 0f), 10f, 0f));
        Assert.Equal("A line has just its two ends.", Removed(new SvgLine { EndX = 10f }, 10f, 0f));
    }

    [Fact]
    public void A_Polygon_Loses_One_Corner()
    {
        Assert.Equal("points=0,0 10,10 0,10", Removed(Cornered(0f, 0f, 10f, 0f, 10f, 10f, 0f, 10f), 10f, 0f));
    }

    // ---- inserting ----------------------------------------------------------------------------

    [Fact]
    public void A_Point_Splits_A_Line_In_Its_Own_Letter()
    {
        Assert.Equal("d=M0 0 L5 0 L10 0", Inserted(Drawn("M0,0 L10,0"), 1, 0.5f));
        Assert.Equal("d=M0 0 H2.5 H10", Inserted(Drawn("M0,0 H10"), 1, 0.25f));
        Assert.Equal("d=m0 0 h2.5 h7.5", Inserted(Drawn("m0,0 h10"), 1, 0.25f));
    }

    [Fact]
    public void A_Point_Splits_A_Quadratic_Into_Two()
    {
        Assert.Equal("d=M0 0 Q5 5 10 5 Q15 5 20 0", Inserted(Drawn("M0,0 Q10,10 20,0"), 1, 0.5f));
    }

    [Fact]
    public void A_Point_On_The_Closing_Edge_Goes_Before_The_Close()
    {
        Assert.Equal("d=M0 0 L10 0 L10 10 L5 5 Z", Inserted(Drawn("M0,0 L10,0 L10,10 Z"), 3, 0.5f));
    }

    /// <summary>
    /// Splitting a curve leaves the curve where it was: both halves trace it.
    /// </summary>
    /// <remarks>
    /// The S after it reflected the curve's last handle, which the split shortens, so the S is
    /// spelt out as the C it was.
    /// </remarks>
    [Fact]
    public void A_Point_Splits_A_Curve_Without_Moving_It()
    {
        Assert.Equal(
            "d=M0 0 C0 5 2.5 7.5 5 7.5 C7.5 7.5 10 5 10 0 C10 -10 20 -10 20 0",
            Inserted(Drawn("M0,0 C0,10 10,10 10,0 S20,-10 20,0"), 1, 0.5f));

        var path = Drawn("M0,0 C0,30 40,-10 40,20");
        var points = Captured(path);

        Assert.Null(points.Insert(1, 0.3f, out _, out var anchor));
        Assert.Equal(1, anchor);

        var halves = path.PathData.OfType<SvgCubicCurveSegment>().ToArray();

        for (var u = 0f; u <= 1f; u += 0.05f)
        {
            var whole = Cubic(new PointF(0f, 0f), new PointF(0f, 30f), new PointF(40f, -10f), new PointF(40f, 20f), u);
            var half = u <= 0.3f
                ? Cubic(new PointF(0f, 0f), halves[0].FirstControlPoint, halves[0].SecondControlPoint, halves[0].End, u / 0.3f)
                : Cubic(halves[0].End, halves[1].FirstControlPoint, halves[1].SecondControlPoint, halves[1].End, (u - 0.3f) / 0.7f);

            Assert.True(Math.Abs(whole.X - half.X) < 2e-3f && Math.Abs(whole.Y - half.Y) < 2e-3f, $"{u}: {whole} against {half}");
        }
    }

    [Fact]
    public void A_Point_Cannot_Be_Added_To_An_Arc_Or_A_Line()
    {
        Assert.Equal("A point cannot be added to an arc.", Inserted(Drawn("M0,0 A5,5 0 0 1 10,0"), 1, 0.5f));
        Assert.Equal("A line has just its two ends.", Inserted(new SvgLine { EndX = 10f }, 0, 0.5f));
    }

    [Fact]
    public void A_Polygon_Takes_A_Point_On_Its_Closing_Edge()
    {
        var points = Captured(Cornered(0f, 0f, 10f, 0f, 10f, 10f));

        Assert.Null(points.Insert(2, 0.5f, out var written, out var anchor));
        Assert.Equal("points=0,0 10,0 10,10 5,5", Says(written));
        Assert.Equal(3, anchor);
    }

    // ---- finding a segment --------------------------------------------------------------------

    [Fact]
    public void The_Nearest_Segment_Is_Found_Within_Reach()
    {
        var points = Captured(Drawn("M0,0 L10,0 C10,10 20,10 20,0"));

        var near = points.Nearest(new PointF(5f, 1f), at => at, 2f);

        Assert.NotNull(near);
        Assert.Equal(1, near!.Value.Segment);
        Assert.Equal(0.5f, near.Value.T, 3);

        var curve = points.Nearest(new PointF(15f, 8f), at => at, 2f);

        Assert.Equal(2, curve?.Segment);
        Assert.Equal(0.5f, curve!.Value.T, 2);
        Assert.Null(points.Nearest(new PointF(5f, 4f), at => at, 2f));
    }

    private static PointF Cubic(PointF p0, PointF p1, PointF p2, PointF p3, float t)
    {
        var u = 1f - t;

        return new PointF(
            u * u * u * p0.X + 3f * u * u * t * p1.X + 3f * u * t * t * p2.X + t * t * t * p3.X,
            u * u * u * p0.Y + 3f * u * u * t * p1.Y + 3f * u * t * t * p2.Y + t * t * t * p3.Y);
    }
}
