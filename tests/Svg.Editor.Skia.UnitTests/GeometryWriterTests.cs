using System;
using System.Collections.Generic;
using System.Linq;
using Svg.Pathing;
using Xunit;
using Shim = ShimSkiaSharp;

namespace Svg.Editor.Skia.UnitTests;

/// <summary>
/// Putting a gesture into an element's own numbers rather than into a transform.
/// </summary>
/// <remarks>
/// Against the writer directly, where a map is a map and no pointer is involved: what these pin is
/// the arithmetic and the refusals, and the gizmo's own tests pin that the right map arrives.
///
/// The numbers are exact. Every shape here spans 20..40, so the same three maps serve them all: a
/// move of (20, 10), a doubling about (20, 20), and a quarter turn about the middle at (30, 30).
/// </remarks>
public class GeometryWriterTests
{
    private static Shim.SKMatrix Moved => Shim.SKMatrix.CreateTranslation(20f, 10f);

    private static Shim.SKMatrix Doubled => Shim.SKMatrix.CreateScale(2f, 2f, 20f, 20f);

    private static Shim.SKMatrix Widened => Shim.SKMatrix.CreateScale(2f, 1f, 20f, 20f);

    private static Shim.SKMatrix Turned => Shim.SKMatrix.CreateRotationDegrees(90f, 30f, 30f);

    /// <summary>What the file would say, as one string, so a test is one assertion.</summary>
    private static string Says(IReadOnlyList<(string Name, string Value)>? written)
        => written is null
            ? "refused"
            : string.Join(" ", written.Select(pair => $"{pair.Name}={pair.Value}"));

    private static SvgRectangle Box() => new() { X = 20f, Y = 20f, Width = 20f, Height = 20f };

    private static SvgPath Drawn(string data) => new() { PathData = SvgPathBuilder.Parse(data) };

    // ---- the shapes with their own numbers ----------------------------------------------------

    [Fact]
    public void A_Rect_Moves_By_Its_Own_Corner()
    {
        var writer = GeometryWriter.Capture(Box(), GeometryGesture.Move);

        Assert.NotNull(writer);
        Assert.Equal("x=40 y=30", Says(writer!.Apply(Moved)));
    }

    [Fact]
    public void A_Rect_Scales_By_Its_Own_Size()
    {
        var writer = GeometryWriter.Capture(Box(), GeometryGesture.AxisScale);

        Assert.Equal("x=20 y=20 width=40 height=40", Says(writer!.Apply(Doubled)));
    }

    /// <summary>
    /// A rect pulled through its own edge is the same rect the other way round.
    /// </summary>
    /// <remarks>
    /// Width has nowhere to put a sign, so the two mapped corners are read back as a corner and a
    /// size rather than refused — which is exact, unlike the same trick on an image.
    /// </remarks>
    [Fact]
    public void A_Rect_Pulled_Through_Itself_Is_Read_Back_The_Other_Way()
    {
        var writer = GeometryWriter.Capture(Box(), GeometryGesture.AxisScale);

        Assert.Equal("x=0 y=20 width=20 height=20", Says(writer!.Apply(Shim.SKMatrix.CreateScale(-1f, 1f, 20f, 20f))));
    }

    /// <summary>
    /// A radius left to the other one has to be spelt out once the two stop agreeing.
    /// </summary>
    [Fact]
    public void A_Rounded_Rect_Spells_Out_The_Radius_It_Left_Implied()
    {
        var rect = Box();
        rect.CornerRadiusX = 4f;

        var writer = GeometryWriter.Capture(rect, GeometryGesture.AxisScale);

        Assert.Equal("x=20 y=20 width=40 height=20 rx=8 ry=4", Says(writer!.Apply(Widened)));
    }

    /// <summary>The guard is per attribute, so what a unit blocks is only what it is written on.</summary>
    [Fact]
    public void A_Rect_Measured_In_Percent_Still_Moves_And_Will_Not_Resize()
    {
        var rect = Box();
        rect.Width = new SvgUnit(SvgUnitType.Percentage, 20f);

        Assert.Null(GeometryWriter.Capture(rect, GeometryGesture.AxisScale));

        var writer = GeometryWriter.Capture(rect, GeometryGesture.Move);

        Assert.Equal("x=40 y=30", Says(writer!.Apply(Moved)));
    }

    /// <summary>A unit the element was written in is the unit it is written back in.</summary>
    [Fact]
    public void A_Rect_In_Pixels_Is_Written_Back_In_Pixels()
    {
        var rect = new SvgRectangle
        {
            X = new SvgUnit(SvgUnitType.Pixel, 20f),
            Y = new SvgUnit(SvgUnitType.Pixel, 20f),
            Width = new SvgUnit(SvgUnitType.Pixel, 20f),
            Height = new SvgUnit(SvgUnitType.Pixel, 20f)
        };

        var writer = GeometryWriter.Capture(rect, GeometryGesture.Move);

        Assert.Equal("x=40px y=30px", Says(writer!.Apply(Moved)));
    }

    /// <summary>There is no attribute on a rect that can hold an angle.</summary>
    [Fact]
    public void A_Rect_Cannot_Be_Turned()
    {
        Assert.Null(GeometryWriter.Capture(Box(), GeometryGesture.Turn));
    }

    [Fact]
    public void A_Circle_Takes_An_Even_Scale()
    {
        var circle = new SvgCircle { CenterX = 30f, CenterY = 30f, Radius = 10f };
        var writer = GeometryWriter.Capture(circle, GeometryGesture.EvenScale);

        Assert.Equal("cx=40 cy=40 r=20", Says(writer!.Apply(Doubled)));
    }

    /// <summary>One radius cannot say that the two axes differ, so the drag falls back instead.</summary>
    [Fact]
    public void A_Circle_Refuses_An_Uneven_Scale()
    {
        var circle = new SvgCircle { CenterX = 30f, CenterY = 30f, Radius = 10f };
        var writer = GeometryWriter.Capture(circle, GeometryGesture.AxisScale);

        Assert.Equal("refused", Says(writer!.Apply(Widened)));
    }

    /// <summary>An ellipse's axes are the element's own, and so are the handles'.</summary>
    [Fact]
    public void An_Ellipse_Takes_Each_Axis_On_Its_Own()
    {
        var ellipse = new SvgEllipse { CenterX = 30f, CenterY = 30f, RadiusX = 10f, RadiusY = 10f };
        var writer = GeometryWriter.Capture(ellipse, GeometryGesture.AxisScale);

        Assert.Equal("cx=40 cy=30 rx=20 ry=10", Says(writer!.Apply(Widened)));
    }

    [Fact]
    public void A_Line_Turns_Into_Its_Own_Endpoints()
    {
        var line = new SvgLine { StartX = 20f, StartY = 30f, EndX = 40f, EndY = 30f };
        var writer = GeometryWriter.Capture(line, GeometryGesture.Turn);

        Assert.Equal("x1=30 y1=20 x2=30 y2=40", Says(writer!.Apply(Turned)));
    }

    [Fact]
    public void A_Polygon_Scales_Into_Its_Own_Points()
    {
        var poly = new SvgPolygon { Points = new SvgPointCollection { 20f, 40f, 40f, 40f, 40f, 20f } };
        var writer = GeometryWriter.Capture(poly, GeometryGesture.AxisScale);

        Assert.Equal("points=20,60 60,60 60,20", Says(writer!.Apply(Doubled)));
    }

    /// <summary>
    /// A dangling number is not written back.
    /// </summary>
    /// <remarks>
    /// Both the renderer and the serialiser drop it, so re-writing the attribute would delete it
    /// from the file for no visible reason.
    /// </remarks>
    [Fact]
    public void A_Polygon_With_A_Number_Left_Over_Is_Refused()
    {
        var poly = new SvgPolygon { Points = new SvgPointCollection { 20f, 40f, 40f, 40f, 40f } };

        Assert.Null(GeometryWriter.Capture(poly, GeometryGesture.Move));
    }

    /// <summary>Every entry, because an x of several numbers places the glyphs one by one.</summary>
    [Fact]
    public void A_Text_Run_Moves_Every_Coordinate_It_Lists()
    {
        var text = new SvgText
        {
            X = new SvgUnitCollection { 20f, 30f },
            Y = new SvgUnitCollection { 70f }
        };

        var writer = GeometryWriter.Capture(text, GeometryGesture.Move);

        Assert.Equal("x=40 50 y=80", Says(writer!.Apply(Moved)));
    }

    /// <summary>There is no attribute for a run's size.</summary>
    [Fact]
    public void A_Text_Run_Cannot_Be_Scaled()
    {
        var text = new SvgText { X = new SvgUnitCollection { 20f }, Y = new SvgUnitCollection { 70f } };

        Assert.Null(GeometryWriter.Capture(text, GeometryGesture.EvenScale));
    }

    /// <summary>
    /// A use's width and height are a viewport for a symbol and ignored for anything else.
    /// </summary>
    [Fact]
    public void A_Use_Moves_And_Will_Not_Scale()
    {
        var use = new SvgUse { X = 20f, Y = 20f };
        var writer = GeometryWriter.Capture(use, GeometryGesture.Move);

        Assert.Equal("x=40 y=30", Says(writer!.Apply(Moved)));
        Assert.Null(GeometryWriter.Capture(use, GeometryGesture.EvenScale));
    }

    /// <summary>A flip cannot be said with a positive width, and a mirrored picture is not the same picture.</summary>
    [Fact]
    public void An_Image_Refuses_A_Flip()
    {
        var image = new SvgImage { X = 20f, Y = 20f, Width = 20f, Height = 20f };
        var writer = GeometryWriter.Capture(image, GeometryGesture.EvenScale);

        Assert.Equal("refused", Says(writer!.Apply(Shim.SKMatrix.CreateScale(-1f, 1f, 20f, 20f))));
    }

    /// <summary>A size the file leaves to the picture is not one a drag may pin to a number.</summary>
    [Fact]
    public void An_Image_With_No_Size_Of_Its_Own_Will_Not_Scale()
    {
        Assert.Null(GeometryWriter.Capture(new SvgImage { X = 20f, Y = 20f }, GeometryGesture.EvenScale));
    }

    // ---- the path -----------------------------------------------------------------------------

    /// <summary>
    /// A move leaves every shorthand where it was.
    /// </summary>
    /// <remarks>
    /// H and V carry a NaN in the axis they do not name. The map is applied componentwise for
    /// exactly this reason: a general multiply makes 0 * NaN a NaN and poisons the axis that is
    /// there, which would turn every h into an l.
    /// </remarks>
    [Fact]
    public void A_Path_Keeps_Its_Shorthand_Through_A_Move()
    {
        var writer = GeometryWriter.Capture(Drawn("M20,20 H40 V40 H20 Z"), GeometryGesture.Move);

        Assert.Equal("d=M40 30 H60 V50 H40 Z", Says(writer!.Apply(Moved)));
    }

    /// <summary>
    /// A relative path is untouched by a move apart from where it opens.
    /// </summary>
    /// <remarks>
    /// Every later number is a delta, and a delta is a difference of two points, so the translation
    /// cancels out of it. The leading moveto is the exception both ways: it has nothing to be
    /// relative to, so it is read and written as an absolute one.
    /// </remarks>
    [Fact]
    public void A_Relative_Path_Only_Moves_Where_It_Opens()
    {
        var writer = GeometryWriter.Capture(Drawn("m20,20 h20 v20 h-20 z"), GeometryGesture.Move);

        Assert.Equal("d=m40 30 h20 v20 h-20 z", Says(writer!.Apply(Moved)));
    }

    [Fact]
    public void A_Relative_Path_Scales_Its_Deltas()
    {
        var writer = GeometryWriter.Capture(Drawn("m20,20 h20 v20 h-20 z"), GeometryGesture.EvenScale);

        Assert.Equal("d=m20 20 h40 v40 h-40 z", Says(writer!.Apply(Doubled)));
    }

    [Fact]
    public void A_Path_Scales_Every_Number_It_Has()
    {
        var writer = GeometryWriter.Capture(Drawn("M20,20 H40 V40 H20 Z"), GeometryGesture.EvenScale);

        Assert.Equal("d=M20 20 H60 V60 H20 Z", Says(writer!.Apply(Doubled)));
    }

    /// <summary>
    /// A turn spells out the shorthand it cannot keep.
    /// </summary>
    /// <remarks>
    /// An axis a segment does not name cannot survive a map that mixes the two, so an h becomes an
    /// l. It never comes back, which is the price of holding a rotation in the geometry.
    /// </remarks>
    [Fact]
    public void A_Turned_Path_Spells_Out_Its_Shorthand()
    {
        var writer = GeometryWriter.Capture(Drawn("M20,20 H40 V40 H20 Z"), GeometryGesture.Turn);

        Assert.Equal("d=M40 20 L40 40 L20 40 L20 20 Z", Says(writer!.Apply(Turned)));
    }

    /// <summary>A reflected control point commutes with the map, so the shorthand survives.</summary>
    [Fact]
    public void A_Shorthand_Curve_Stays_Shorthand()
    {
        var writer = GeometryWriter.Capture(Drawn("M20,20 C25,25 35,25 35,30 S30,35 25,30"), GeometryGesture.EvenScale);

        Assert.Equal("d=M20 20 C30 30 50 30 50 40 S40 50 30 40", Says(writer!.Apply(Doubled)));
    }

    /// <summary>An even scale is a similarity, so the ellipse keeps its axes and its tilt.</summary>
    [Fact]
    public void An_Arc_Under_An_Even_Scale_Keeps_Its_Tilt()
    {
        var writer = GeometryWriter.Capture(Drawn("M25,30 A5,8 30 0 1 35,30"), GeometryGesture.EvenScale);

        Assert.Equal("d=M30 40 A10 16 30 0 1 50 40", Says(writer!.Apply(Doubled)));
    }

    /// <summary>A circular arc has no axes to keep, so an uneven scale leaves it upright.</summary>
    [Fact]
    public void A_Circular_Arc_Comes_Out_Upright()
    {
        var writer = GeometryWriter.Capture(Drawn("M25,30 A5,5 0 1 1 35,30"), GeometryGesture.AxisScale);

        Assert.Equal("d=M30 30 A10 5 0 1 1 50 30", Says(writer!.Apply(Widened)));
    }

    /// <summary>
    /// A tilted ellipse under an uneven scale is not an arc this can spell.
    /// </summary>
    /// <remarks>
    /// Its axes end up somewhere neither radius names, and finding them is a decomposition whose
    /// angle is irrational — drag by drag that would grind the shape down through seven digits.
    /// </remarks>
    [Fact]
    public void A_Tilted_Arc_Refuses_An_Uneven_Scale()
    {
        var writer = GeometryWriter.Capture(Drawn("M25,30 A5,8 30 0 1 35,30"), GeometryGesture.AxisScale);

        Assert.Equal("refused", Says(writer!.Apply(Widened)));
    }

    /// <summary>A turn is exact for every arc: the ellipse is the same one, turned.</summary>
    [Fact]
    public void An_Arc_Turns_By_What_The_Drag_Turned()
    {
        var writer = GeometryWriter.Capture(Drawn("M25,30 A5,8 30 0 1 35,30"), GeometryGesture.Turn);
        var written = writer!.Apply(Turned);

        Assert.Contains("A5 8 120 0 1", Says(written), StringComparison.Ordinal);
    }

    /// <summary>The map turns the plane over, and an arc has to be told which way round it is.</summary>
    [Fact]
    public void A_Mirrored_Arc_Sweeps_The_Other_Way()
    {
        var writer = GeometryWriter.Capture(Drawn("M25,30 A5,5 0 1 1 35,30"), GeometryGesture.EvenScale);

        Assert.Equal("d=M15 30 A5 5 0 1 0 5 30", Says(writer!.Apply(Shim.SKMatrix.CreateScale(-1f, 1f, 20f, 20f))));
    }

    /// <summary>
    /// A path whose text the parser could not read is left alone.
    /// </summary>
    /// <remarks>
    /// The parser swallows its own errors and hands back what it managed, so re-serialising what it
    /// returned would delete the rest of the author's text.
    /// </remarks>
    [Fact]
    public void A_Path_That_Does_Not_Open_With_A_Moveto_Is_Refused()
    {
        Assert.Null(GeometryWriter.Capture(Drawn("L10,10"), GeometryGesture.Move));
        Assert.Null(GeometryWriter.Capture(new SvgPath(), GeometryGesture.Move));
    }

    /// <summary>A relative segment after a close is relative to where the subpath opened.</summary>
    [Fact]
    public void A_Close_Puts_The_Pen_Back_Where_The_Subpath_Opened()
    {
        var writer = GeometryWriter.Capture(Drawn("M10,10 h10 v10 z h5"), GeometryGesture.Turn);

        // The trailing h starts from 10,10 rather than from 20,20, and turning it about that shows
        // which of the two the walk was holding.
        Assert.Equal("d=M40 10 l0 10 l-10 0 z l0 5", Says(writer!.Apply(Shim.SKMatrix.CreateRotationDegrees(90f, 25f, 25f))));
    }

    // ---- putting it back ----------------------------------------------------------------------

    [Fact]
    public void Restore_Puts_Back_What_The_Press_Found()
    {
        var rect = Box();
        var writer = GeometryWriter.Capture(rect, GeometryGesture.AxisScale);

        Assert.Equal("x=20 y=20 width=20 height=20", Says(writer!.Captured));

        writer.Apply(Doubled);

        Assert.Equal(40f, rect.Width.Value);

        writer.Restore();

        Assert.Equal(20f, rect.Width.Value);
        Assert.Equal(20f, rect.X.Value);
    }

    /// <summary>A map applied twice is the map, not the map twice: every frame reads the press.</summary>
    [Fact]
    public void Applying_A_Map_Again_Does_Not_Compose_It()
    {
        var writer = GeometryWriter.Capture(Box(), GeometryGesture.AxisScale);

        writer!.Apply(Doubled);

        Assert.Equal("x=20 y=20 width=40 height=40", Says(writer.Apply(Doubled)));
    }
}
