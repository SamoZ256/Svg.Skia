// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Svg.PaintCode.UnitTests;

public class PaintCodeSvgWriterTests
{
    /// <summary>
    /// The fixture's canvas sits at (54, 136) on its desk, which is what makes this worth asserting:
    /// a canvas's bounds place it on the desk, and its contents are measured from its own corner, so
    /// the drawing starts at zero however far across the desk it was put.
    /// </summary>
    [Fact]
    public void A_Canvas_Becomes_A_Drawing_Of_Its_Own_Size_Measured_From_Its_Own_Corner()
    {
        var root = Written().Root!;

        Assert.Equal("30", root.Attribute("width")!.Value);
        Assert.Equal("30", root.Attribute("height")!.Value);
        Assert.Equal("0 0 30 30", root.Attribute("viewBox")!.Value);

        // Nor is the origin carried in as a shift of everything inside it.
        Assert.Null(root.Attribute("transform"));
    }

    [Fact]
    public void A_Bezier_Is_Written_In_Its_Own_Space_With_Its_Anchor_As_A_Translate()
    {
        var path = Element("path");

        Assert.Equal(
            "M9.8047,1.5499C10.3257,0.5945 11.327,0 12.4152,0C13.5034,0 7.1184,6.4764 9.8047,1.5499Z",
            path.Attribute("d")!.Value);
        Assert.Equal("translate(3.0844,3.6379)", path.Attribute("transform")!.Value);
    }

    [Fact]
    public void A_Fill_An_Expression_Drives_Is_Written_In_Braces()
        => Assert.Equal("{{ state ? colorPurple : colorPurple }}", Element("path").Attribute("fill")!.Value);

    [Fact]
    public void A_Fill_Nothing_Drives_Is_The_Library_Colour_Where_The_Library_Names_One()
        => Assert.Equal("{{ colorPurple }}", Elements("path").Last().Attribute("fill")!.Value);

    [Fact]
    public void The_Block_Declares_What_The_Drawing_Reaches_And_Nothing_Else()
    {
        var code = Written().Descendants(PaintCodeCode.Namespace + "param");

        Assert.Equal(new[] { "colorPurple", "state" }, code.Select(element => element.Attribute("name")!.Value).OrderBy(name => name));
    }

    [Fact]
    public void A_Bound_Drawing_Declares_The_Expression_Namespace()
        => Assert.Equal(PaintCodeCode.Namespace.NamespaceName, Written().Root!.Attribute(XNamespace.Xmlns + "e")!.Value);

    [Fact]
    public void A_Shape_With_No_Stroke_Says_So_By_Saying_Nothing()
        => Assert.Null(Element("path").Attribute("stroke"));

    [Fact]
    public void An_Even_Odd_Winding_Rule_Reaches_The_Element()
        => Assert.Equal("evenodd", Element("path").Attribute("fill-rule")!.Value);

    [Fact]
    public void A_Straight_Segment_Is_Written_As_A_Line_Rather_Than_A_Curve()
    {
        var shape = Shape(
            new PaintCodePathPoint(new PaintCodePoint(0, 0), default, default),
            new PaintCodePathPoint(new PaintCodePoint(10, -10), default, default));

        Assert.Equal("M0,0L10,10", PaintCodePathData.For(shape));
    }

    [Fact]
    public void A_Control_Offset_Is_Added_To_The_Point_It_Belongs_To()
    {
        var shape = Shape(
            new PaintCodePathPoint(new PaintCodePoint(0, 0), default, new PaintCodePoint(1, 0)),
            new PaintCodePathPoint(new PaintCodePoint(10, 0), new PaintCodePoint(-1, 0), default));

        Assert.Equal("M0,0C1,0 9,0 10,0", PaintCodePathData.For(shape));
    }

    [Fact]
    public void A_Rectangle_Keeps_Svgs_Own_Element()
    {
        var element = Rectangle(0, PaintCodeShapeKind.Rectangle);

        Assert.Equal("rect", element.Name.LocalName);
        Assert.Equal("0", element.Attribute("y")!.Value);
        Assert.Equal("14", element.Attribute("height")!.Value);
        Assert.Null(element.Attribute("rx"));
    }

    [Fact]
    public void A_Rectangle_Rounded_On_Every_Corner_Says_So_With_One_Radius()
        => Assert.Equal("2", Rectangle(2, PaintCodeShapeKind.RoundedRectangle).Attribute("rx")!.Value);

    [Fact]
    public void A_Rectangle_Rounded_On_Two_Corners_Becomes_A_Path()
    {
        var metrics = new PaintCodeShapeMetrics(2, true, true, false, false, 0, 360, true, 0, 0);
        var shape = Box(PaintCodeShapeKind.RoundedRectangle, metrics);

        Assert.Equal("M2,0L7,0A2 2 0 0 1 9,2L9,14L0,14L0,2A2 2 0 0 1 2,0Z", PaintCodePathData.For(shape));
    }

    [Theory]
    // A whole oval is written with no sweep at all rather than with a full turn, and reading the
    // first of those as an arc draws nothing: 670 of the sample's 718 ovals say it that way.
    [InlineData(0, 0)]
    // Ends before it starts, so PaintCode adds nothing and the sweep is a whole turn as it stands.
    [InlineData(90, -270)]
    public void A_Whole_Oval_Keeps_Svgs_Own_Element(double start, double end)
    {
        var shape = Box(PaintCodeShapeKind.Oval, new PaintCodeShapeMetrics(0, true, true, true, true, start, end, true, 0, 0));

        Assert.True(PaintCodePathData.IsWholeEllipse(shape));
    }

    /// <summary>
    /// An arc that ends more than a turn past where it starts comes round by as many turns as it
    /// takes, not by one.
    /// </summary>
    /// <remarks>
    /// PaintCode's own sum is <c>(start - end) + (end > start ? 360 * ceil((end - start) / 360) : 0)</c>.
    /// Adding a single turn is right inside one and wrong beyond it, and taking the size of the
    /// sweep instead -- anything past a turn is whole -- closed the gap in powerButton-state's ring
    /// altogether. start -290 end 110 is PaintCode's own AddArc(rect, 290, 320).
    ///
    /// A sweep that comes round to nothing draws nothing, which is not the same as drawing all of
    /// it: thermostat-temperature-level asks for start -450 end 270 and PaintCode's sum brings that
    /// to nought. No oval in the sample is start 0 end 360, but the same sum would make one of those
    /// empty too, which is why it is not in the theory above.
    /// </remarks>
    [Theory]
    [InlineData(-290, 110, false, false)]
    [InlineData(-450, 270, false, true)]
    [InlineData(0, 360, false, true)]
    [InlineData(90, -270, true, false)]
    public void An_Arc_Past_A_Turn_Comes_Round_As_Far_As_It_Has_To(double start, double end, bool whole, bool empty)
    {
        var shape = Box(PaintCodeShapeKind.Oval, new PaintCodeShapeMetrics(0, true, true, true, true, start, end, false, 0, 0));

        Assert.Equal(whole, PaintCodePathData.IsWholeEllipse(shape));
        Assert.Equal(empty, PaintCodePathData.For(shape)!.Length == 0);
    }

    [Fact]
    public void A_Gradient_Fill_Becomes_A_Gradient_Laid_Along_The_Angle_It_Was_Given()
    {
        var shape = Filled(Gradient(), -90, frame: At(20, 10), kind: PaintCodeShapeKind.Rectangle);
        var element = WriteTree(Only(shape), new List<PaintCodeImportNote>());
        var gradient = element.Descendants().First(one => one.Name.LocalName == "linearGradient");

        // Down the shape, which for a plain rectangle is down its box: top edge to bottom edge.
        Assert.Equal(new[] { "10", "0", "10", "10" }, new[] { "x1", "y1", "x2", "y2" }.Select(name => gradient.Attribute(name)!.Value));
        Assert.Equal(new[] { "#ff0000", "#0000ff" }, gradient.Elements().Select(stop => stop.Attribute("stop-color")!.Value));
        Assert.Equal($"url(#{gradient.Attribute("id")!.Value})", element.Descendants().First(one => one.Name.LocalName == "rect").Attribute("fill")!.Value);
    }

    /// <summary>
    /// A gradient given an angle runs from one side of the shape to the other, not across its box.
    /// </summary>
    /// <remarks>
    /// The numbers are PaintCode's own, read out of the C# it generates for this document:
    /// tv-state's rounded rectangle is 18.85 by 9.85 with a corner radius of 3, and at -45 degrees
    /// PaintCode draws it between (8.7, 6.59) and (21.3, 19.19) — 17.81 long. Laid across the box
    /// instead it came out 20.29, the diagonal of a box whose corners this shape does not have.
    ///
    /// Measured as a length and a middle rather than as four numbers: where the line sits across the
    /// direction paints identically, so pinning it would be pinning something nobody can see.
    /// </remarks>
    [Fact]
    public void A_Gradient_Runs_Across_The_Shape_Rather_Than_Across_Its_Box()
    {
        var notes = new List<PaintCodeImportNote>();
        var rounded = new PaintCodeShapeMetrics(3, true, true, true, true, 0, 0, true, 0, 50);
        var shape = Filled(Gradient(), -45, frame: At(18.85, 9.85), kind: PaintCodeShapeKind.RoundedRectangle, metrics: rounded);

        var (x1, y1, x2, y2) = Laid(shape, notes);

        Assert.Equal(17.81, Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2)), 2);

        // At the angle it was given, and through the middle of the shape.
        Assert.Equal(x2 - x1, y2 - y1, 4);
        Assert.Equal(18.85 / 2, (x1 + x2) / 2, 4);
        Assert.Equal(9.85 / 2, (y1 + y2) / 2, 4);

        // Nothing was approximated, so there is nothing to say about it.
        Assert.Empty(notes);
    }

    /// <summary>
    /// A circle's gradient is its diameter whichever way it runs, where its box's is the diagonal.
    /// </summary>
    [Fact]
    public void A_Round_Shapes_Gradient_Is_Measured_Round()
    {
        var (x1, y1, x2, y2) = Laid(Filled(Gradient(), 45, frame: At(28, 28), kind: PaintCodeShapeKind.Oval));

        Assert.Equal(28d, Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2)), 3);
        Assert.Equal(new[] { 14d, 14d }, new[] { (x1 + x2) / 2, (y1 + y2) / 2 });
    }

    /// <summary>
    /// A star is the ten points PaintCode puts round its box, and a gradient reaches only as far.
    /// </summary>
    /// <remarks>
    /// Both are PaintCode's own numbers for symbol-overlay-new, read out of the C# it generates: the
    /// ten points to the two decimals it writes them at, and a gradient at -45 degrees drawn 17.18
    /// long. Laid across the box that would have been 25.87, the distance between two corners this
    /// shape has none of.
    /// </remarks>
    [Fact]
    public void A_Star_Reaches_Only_As_Far_As_Its_Points()
    {
        var frame = new PaintCodeFrame(12.1, -31.4299, 18.2917, 18.2917, default, 0, 1, 1, 1, false, true);
        var points = new PaintCodeShapeMetrics(0, true, true, true, true, 0, 0, true, 5, 44.5556);
        var shape = Filled(Gradient(), -45, frame: frame, kind: PaintCodeShapeKind.Star, metrics: points);

        Assert.Equal(
            "M21.2458,13.1382L23.6411,18.9873L29.9441,19.4578L25.1214,23.5433L26.6216,29.6832"
            + "L21.2458,26.359L15.8701,29.6832L17.3703,23.5433L12.5476,19.4578L18.8506,18.9873Z",
            PaintCodePathData.For(shape));

        var (x1, y1, x2, y2) = Laid(shape);

        Assert.Equal(17.18, Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2)), 2);
    }

    /// <summary>
    /// An outline this cannot measure keeps the box it always had, and says so.
    /// </summary>
    /// <remarks>
    /// An arc of an oval is the one fill here whose reach is not worked out, and a shape with no
    /// outline at all — a path of one point — is the other: a gradient of no length paints in its
    /// last stop's colour alone, which is not a drawing anybody meant.
    /// </remarks>
    [Fact]
    public void An_Outline_That_Cannot_Be_Measured_Keeps_The_Box()
    {
        var notes = new List<PaintCodeImportNote>();
        var arc = new PaintCodeShapeMetrics(0, true, true, true, true, 90, 0, false, 0, 50);
        var shape = Filled(Gradient(), -45, frame: At(20, 10), kind: PaintCodeShapeKind.Oval, metrics: arc);
        var gradient = WriteTree(Only(shape), notes)
            .Descendants().First(one => one.Name.LocalName == "linearGradient");

        Assert.Equal("objectBoundingBox", gradient.Attribute("gradientUnits")!.Value);
        Assert.Contains(notes, note => note.Severity is PaintCodeImportSeverity.Approximated && note.Property == "fill");

        // And the point-sized one, which is measured and comes to nothing.
        Assert.Equal(
            "objectBoundingBox",
            WriteTree(Only(Filled(Gradient(), -45)), new List<PaintCodeImportNote>())
                .Descendants().First(one => one.Name.LocalName == "linearGradient")
                .Attribute("gradientUnits")!.Value);
    }

    /// <summary>
    /// A gradient laid by dragging its ends runs between them, not down the shape.
    /// </summary>
    /// <remarks>
    /// PaintCode records that as type 2 and draws it from the two handles, leaving the angle beside
    /// them at the default nobody turned. Reading the angle instead laid 27 of the sample's gradients
    /// top to bottom, and the note about laying one across the box was really describing this.
    ///
    /// The handles are offsets from the shape's own middle in PaintCode's y-up space. A box 20 wide
    /// and 10 tall has its middle at (10, 5) once the drawing is turned over, so a handle 6 to the
    /// left and 4 above it lands at (4, 1).
    /// </remarks>
    [Fact]
    public void A_Gradient_Laid_By_Its_Ends_Runs_Between_Them()
    {
        var box = new PaintCodeFrame(0, -10, 20, 10, default, 0, 1, 1, 1, false, true);
        var shape = Filled(Gradient(), -90, ends: new PaintCodeGradientEnds(new PaintCodePoint(-6, 4), new PaintCodePoint(6, -4), 0, 0), frame: box);
        var gradient = WriteTree(Only(shape), new List<PaintCodeImportNote>())
            .Descendants().First(one => one.Name.LocalName == "linearGradient");

        Assert.Equal("userSpaceOnUse", gradient.Attribute("gradientUnits")!.Value);
        Assert.Equal(new[] { "4", "1", "16", "9" }, new[] { "x1", "y1", "x2", "y2" }.Select(name => gradient.Attribute(name)!.Value));
    }

    /// <summary>
    /// A radial gradient runs between its own two circles, not out from the middle of the box.
    /// </summary>
    /// <remarks>
    /// PaintCode lays one between an inner circle and an outer, and SVG says exactly that: cx/cy/r
    /// is where the last stop lands and fx/fy/fr where the first does. Laying it over the box instead
    /// put all 31 of the sample's radials in the middle at half the width, whatever PaintCode had
    /// been told, and the note beside them was really describing this.
    /// </remarks>
    [Fact]
    public void A_Radial_Gradient_Runs_Between_Its_Own_Two_Circles()
    {
        var box = new PaintCodeFrame(0, -10, 20, 10, default, 0, 1, 1, 1, false, true);
        var shape = Filled(
            Gradient(),
            -90,
            ends: new PaintCodeGradientEnds(new PaintCodePoint(-6, 4), new PaintCodePoint(6, -4), 1, 8),
            frame: box,
            radial: true);

        var gradient = WriteTree(Only(shape), new List<PaintCodeImportNote>())
            .Descendants().First(one => one.Name.LocalName == "radialGradient");

        Assert.Equal("userSpaceOnUse", gradient.Attribute("gradientUnits")!.Value);
        Assert.Equal(
            new[] { "16", "9", "8", "4", "1", "1" },
            new[] { "cx", "cy", "r", "fx", "fy", "fr" }.Select(name => gradient.Attribute(name)!.Value));
    }

    [Fact]
    public void A_Gradient_An_Expression_Chooses_Drives_Each_Of_Its_Stops()
    {
        var shape = Filled(Gradient(), -90, "state ? warm : warm");
        var gradient = WriteTree(Only(shape), new List<PaintCodeImportNote>())
            .Descendants().First(one => one.Name.LocalName == "linearGradient");

        Assert.Equal(
            new[] { "{{ state ? #ff0000ff : #ff0000ff }}", "{{ state ? #0000ffff : #0000ffff }}" },
            gradient.Elements().Select(stop => stop.Attribute("stop-color")!.Value));
    }

    /// <summary>
    /// A gradient an expression names the way PaintCode's own code does is still that gradient.
    /// </summary>
    /// <remarks>
    /// The library holds the name as it was typed and PaintCode's generator emits it with the first
    /// letter lowered, which is how its expressions spell it. Keyed by what was typed, the lookup
    /// missed, the chooser could not be read, and the note blamed the format for having no gradient
    /// type -- six of them in the sample, on every icon that shades by temperature.
    /// </remarks>
    [Fact]
    public void A_Gradient_Named_The_Way_PaintCodes_Own_Code_Names_It_Is_Still_Found()
    {
        var notes = new List<PaintCodeImportNote>();
        var shape = Filled(Gradient(), -90, "state ? cool : warm");
        var gradient = WriteTree(Only(shape), notes)
            .Descendants().First(one => one.Name.LocalName == "linearGradient");

        Assert.Equal(
            new[] { "{{ state ? #ff0000ff : #ff0000ff }}", "{{ state ? #0000ffff : #0000ffff }}" },
            gradient.Elements().Select(stop => stop.Attribute("stop-color")!.Value));

        Assert.Empty(notes);
    }

    /// <summary>
    /// A blended shape carries the mode PaintCode gave it, which SVG has its own name for.
    /// </summary>
    /// <remarks>
    /// The number is Core Graphics' CGBlendMode, and 1 is multiply -- PaintCode's own generated code
    /// draws the sample's one blended shape with SKBlendMode.Multiply. Everything after 15 is a
    /// compositing operation with no CSS keyword, which is still reported.
    /// </remarks>
    [Theory]
    [InlineData(1, "multiply")]
    [InlineData(5, "lighten")]
    [InlineData(8, "soft-light")]
    [InlineData(9, "hard-light")]
    [InlineData(15, "luminosity")]
    public void A_Blended_Shape_Carries_The_Mode_It_Was_Given(int mode, string keyword)
    {
        var notes = new List<PaintCodeImportNote>();
        var written = WriteTree(Only(Blended(mode)), notes).Descendants().First(one => one.Name.LocalName == "rect");

        Assert.Equal($"mix-blend-mode:{keyword}", written.Attribute("style")!.Value);
        Assert.Empty(notes);
    }

    [Fact]
    public void A_Compositing_Operation_Is_Reported_Rather_Than_Guessed_At()
    {
        var notes = new List<PaintCodeImportNote>();
        var written = WriteTree(Only(Blended(16)), notes).Descendants().First(one => one.Name.LocalName == "rect");

        Assert.Null(written.Attribute("style"));

        var note = Assert.Single(notes);

        Assert.Equal("blendMode", note.Property);
        Assert.Contains("a compositing operation rather than a blend", note.Message, StringComparison.Ordinal);
    }

    private static PaintCodeShape Blended(int mode)
        => new(
            "Rectangle",
            PaintCodeShapeKind.Rectangle,
            new PaintCodeFrame(0, -10, 10, 10, default, 0, 1, 1, 1, false, true),
            new Dictionary<string, PaintCodeBinding>(),
            null,
            new PaintCodePaint(PaintCodePaintKind.Color, new PaintCodeColor(string.Empty, 255, 0, 0, 1), null),
            PaintCodePaint.None,
            PaintCodeStroke.None,
            false,
            null,
            PaintCodeShapeMetrics.Default,
            false,
            -90,
            null,
            mode);

    [Fact]
    public void A_Shape_Carrying_Words_Is_Written_As_A_Run_Placed_In_Its_Box()
    {
        var text = new PaintCodeText("3", "Inter", "Bold", 7, null, 2, 0, 0, 0);
        var shape = new PaintCodeShape(
            "Text",
            PaintCodeShapeKind.Rectangle,
            new PaintCodeFrame(0, -9, 9, 9, default, 0, 1, 1, 1, false, true),
            new Dictionary<string, PaintCodeBinding>(),
            null,
            PaintCodePaint.None,
            PaintCodePaint.None,
            PaintCodeStroke.None,
            false,
            text,
            PaintCodeShapeMetrics.Default);

        var run = WriteTree(Only(shape), new List<PaintCodeImportNote>()).Descendants().First(one => one.Name.LocalName == "text");

        Assert.Equal("9", run.Attribute("x")!.Value);
        Assert.Equal("4.5", run.Attribute("y")!.Value);
        Assert.Equal("end", run.Attribute("text-anchor")!.Value);
        Assert.Equal("central", run.Attribute("dominant-baseline")!.Value);
        Assert.Equal("bold", run.Attribute("font-weight")!.Value);
        Assert.Equal("3", run.Value);
    }

    /// <summary>An oval whose sweep an expression drives, for the tests below to vary.</summary>
    private static PaintCodeShape Arc(
        string property,
        string expression,
        double value,
        double start,
        double end,
        bool closed,
        double size = 28,
        PaintCodePaint? fill = null,
        PaintCodePaint? stroke = null)
        => new(
            "Selected",
            PaintCodeShapeKind.Oval,
            new PaintCodeFrame(0, -size, size, size, default, 0, 1, 1, 1, false, true),
            new Dictionary<string, PaintCodeBinding>
            {
                [property] = new(expression, PaintCodeValueKind.Number, value, null, null, null, null, null)
            },
            null,
            fill ?? PaintCodePaint.None,
            stroke ?? PaintCodePaint.None,
            stroke is { } ? new PaintCodeStroke(1.5, 0, 0, 10, false, 0, 0, 0) : PaintCodeStroke.None,
            false,
            null,
            new PaintCodeShapeMetrics(0, true, true, true, true, start, end, closed, 0, 0));

    private static XElement Written(PaintCodeShape shape, List<PaintCodeImportNote> notes)
        => WriteTree(Only(shape), notes).Descendants().First(one => one.Name.LocalName == "path");

    /// <summary>
    /// A driven arc is the whole circle, cut to length by a dash.
    /// </summary>
    /// <remarks>
    /// SVG bakes an arc into path data, where no expression can reach, so the sweep was written at
    /// whatever angle the document was saved with and a level indicator never moved. What a dash
    /// leaves showing is a length along the path, and on a circle a length is an angle.
    ///
    /// The numbers are PaintCode's own for analog-level: a 28-unit circle, the arc starting at 270
    /// and running clockwise, so the path starts at six o'clock and the ring is 87.9646 round.
    /// </remarks>
    [Fact]
    public void A_Driven_Arc_Is_A_Circle_A_Dash_Cuts_To_Length()
    {
        var notes = new List<PaintCodeImportNote>();
        var stroke = new PaintCodePaint(PaintCodePaintKind.Color, new PaintCodeColor(string.Empty, 0, 0, 0, 1), null);
        var written = Written(Arc("endAngle", "x", 1, 270, 270, closed: false, stroke: stroke), notes);

        Assert.Equal("M14,28A14 14 0 1 1 14,0A14 14 0 1 1 14,28", written.Attribute("d")!.Value);
        Assert.Equal("87.9646 87.9646", written.Attribute("stroke-dasharray")!.Value);

        // The part of the circle the arc does not cover, in PaintCode's own sum: the sweep from the
        // angle that stays put to the driven one, a whole turn for each one it has gone past, and
        // the offset back to the angle the drawing was saved at.
        Assert.Equal(
            "{{ clamp(360 - (270 - ((x) + 269) + 360 * max(0, ceil((((x) + 269) - 270) / 360))), 0, 360) * 0.2443 }}",
            written.Attribute("stroke-dashoffset")!.Value);

        Assert.Empty(notes);
    }

    /// <summary>
    /// A wedge is a circle of half the radius stroked its whole width, which is exactly a pie.
    /// </summary>
    /// <remarks>
    /// The stroke covers every radius from the middle to the rim, and a butt cap on a circle is
    /// perpendicular to the tangent, which is the radial direction — so the caps are the wedge's two
    /// straight edges rather than an approximation of them. The fill moves to the stroke because the
    /// stroke is what draws it now.
    /// </remarks>
    [Fact]
    public void A_Driven_Wedge_Is_A_Half_Size_Circle_Stroked_Its_Own_Width()
    {
        var notes = new List<PaintCodeImportNote>();
        var fill = new PaintCodePaint(PaintCodePaintKind.Color, new PaintCodeColor(string.Empty, 255, 0, 0, 1), null);
        var written = Written(Arc("endAngle", "x", 1, 90, 90, closed: true, size: 16, fill: fill), notes);

        Assert.Equal("M8,4A4 4 0 1 1 8,12A4 4 0 1 1 8,4", written.Attribute("d")!.Value);
        Assert.Equal("none", written.Attribute("fill")!.Value);
        Assert.Equal("#ff0000", written.Attribute("stroke")!.Value);
        Assert.Equal("8", written.Attribute("stroke-width")!.Value);
        Assert.Equal("25.1327 25.1327", written.Attribute("stroke-dasharray")!.Value);
        Assert.Empty(notes);
    }

    /// <summary>A driven start runs from the end that stays put, which is the other way round.</summary>
    [Fact]
    public void A_Driven_Start_Turns_From_The_End_That_Stays_Put()
    {
        var notes = new List<PaintCodeImportNote>();
        var stroke = new PaintCodePaint(PaintCodePaintKind.Color, new PaintCodeColor(string.Empty, 0, 0, 0, 1), null);
        var written = Written(Arc("startAngle", "x", 1, 270, -90, closed: false, stroke: stroke), notes);

        // Anticlockwise, from the fixed end at -90.
        Assert.Equal("M14,28A14 14 0 1 0 14,0A14 14 0 1 0 14,28", written.Attribute("d")!.Value);
        Assert.StartsWith("{{ clamp(360 - (((x) + 269) - -90", written.Attribute("stroke-dashoffset")!.Value, StringComparison.Ordinal);
        Assert.Empty(notes);
    }

    /// <summary>A group clipped by a driven wedge is masked by it, since a clip takes no stroke.</summary>
    [Fact]
    public void A_Driven_Wedge_Clipping_A_Group_Becomes_A_Mask()
    {
        var notes = new List<PaintCodeImportNote>();
        var clip = Arc("endAngle", "x", 1, 90, 90, closed: true, size: 16);
        var shape = new PaintCodeShape(
            "Body",
            PaintCodeShapeKind.Rectangle,
            new PaintCodeFrame(0, -10, 10, 10, default, 0, 1, 1, 1, false, true),
            new Dictionary<string, PaintCodeBinding>(),
            null,
            new PaintCodePaint(PaintCodePaintKind.Color, new PaintCodeColor(string.Empty, 0, 0, 255, 1), null),
            PaintCodePaint.None,
            PaintCodeStroke.None,
            false,
            null,
            PaintCodeShapeMetrics.Default);

        var group = new PaintCodeGroup("Group", Identity(), new Dictionary<string, PaintCodeBinding>(), new[] { (PaintCodeItem)shape }, clip);
        var document = WriteTree(new PaintCodeGroup("Root", Identity(), new Dictionary<string, PaintCodeBinding>(), new[] { (PaintCodeItem)group }, null), notes);

        var mask = Assert.Single(document.Descendants().Where(one => one.Name.LocalName == "mask"));
        var path = mask.Elements().Single();

        Assert.Empty(document.Descendants().Where(one => one.Name.LocalName == "clipPath"));
        Assert.Equal("userSpaceOnUse", mask.Attribute("maskUnits")!.Value);
        Assert.Equal("#ffffff", path.Attribute("stroke")!.Value);
        Assert.Equal("8", path.Attribute("stroke-width")!.Value);

        var masked = document.Descendants().Single(one => one.Name.LocalName == "g" && one.Attribute("mask") is { });

        Assert.Equal($"url(#{mask.Attribute("id")!.Value})", masked.Attribute("mask")!.Value);
        Assert.Null(masked.Attribute("clip-path"));
        Assert.Empty(notes);
    }

    /// <summary>
    /// A sweep a dash cannot draw keeps the angle it was saved with, and says which one it was.
    /// </summary>
    [Theory]
    [InlineData(true, false, 28, 20, "an ellipse's arc is not proportional")]
    [InlineData(true, true, 28, 28, "a closed arc is outlined round its two straight edges")]
    [InlineData(false, false, 28, 28, "an open arc closes across its chord")]
    public void A_Sweep_A_Dash_Cannot_Draw_Keeps_The_Angle_It_Was_Saved_With(
        bool stroked,
        bool closed,
        double width,
        double height,
        string because)
    {
        var notes = new List<PaintCodeImportNote>();
        var paint = new PaintCodePaint(PaintCodePaintKind.Color, new PaintCodeColor(string.Empty, 0, 0, 0, 1), null);
        var shape = new PaintCodeShape(
            "Selected",
            PaintCodeShapeKind.Oval,
            new PaintCodeFrame(0, -height, width, height, default, 0, 1, 1, 1, false, true),
            new Dictionary<string, PaintCodeBinding>
            {
                ["endAngle"] = new("x", PaintCodeValueKind.Number, 1, null, null, null, null, null)
            },
            null,
            stroked ? PaintCodePaint.None : paint,
            stroked ? paint : PaintCodePaint.None,
            stroked ? new PaintCodeStroke(1.5, 0, 0, 10, false, 0, 0, 0) : PaintCodeStroke.None,
            false,
            null,
            new PaintCodeShapeMetrics(0, true, true, true, true, 270, 250, closed, 0, 0));

        var written = WriteTree(Only(shape), notes).Descendants().First(one => one.Name.LocalName is "path" or "ellipse");

        Assert.Null(written.Attribute("stroke-dashoffset"));

        var note = Assert.Single(notes, one => one.Property == "endAngle");

        Assert.Contains(because, note.Message, StringComparison.Ordinal);
        Assert.Contains("the drawing's own angle is written", note.Message, StringComparison.Ordinal);
    }

    // The one arc the conversion was checked against: PaintCode's own C# turns these two angles into
    // AddArc(rect, -117, 139), which is the same arc said the other way round.
    [Fact]
    public void An_Oval_Arc_Runs_From_Minus_Start_To_Minus_End_Turning_Clockwise()
    {
        var metrics = new PaintCodeShapeMetrics(0, true, true, true, true, 117, -22, false, 0, 0);
        var shape = new PaintCodeShape(
            "Oval",
            PaintCodeShapeKind.Oval,
            new PaintCodeFrame(0, -15, 15, 15, new PaintCodePoint(9.25, -5.25), 0, 1, 1, 1, false, true),
            new Dictionary<string, PaintCodeBinding>(),
            null,
            PaintCodePaint.None,
            PaintCodePaint.None,
            PaintCodeStroke.None,
            false,
            null,
            metrics);

        Assert.Equal("M4.0951,0.8175A7.5 7.5 0 0 1 14.4539,10.3095", PaintCodePathData.For(shape));
    }

    /// <summary>
    /// An arc that runs more than half a turn still runs PaintCode's way round.
    /// </summary>
    /// <remarks>
    /// PaintCode turns one way and never takes the short route — its own code writes
    /// <c>AddArc(rect, -start, (360 * ceil(end / 360)) - end)</c>, a sweep that is always positive.
    /// Taking the sign of <c>start - end</c> as the direction instead drew the complement of every
    /// arc past half a turn: the same two ends, the other side of the ellipse. It cost 36 of the
    /// sample's canvases and no test saw it, because the only arc pinned here happened to have a
    /// positive sweep already.
    /// </remarks>
    [Theory]
    // start - end is positive: unchanged, and the arc PaintCode's AddArc(rect, -117, 139) draws.
    [InlineData(117, -22, false, "M4.0951,0.8175A7.5 7.5 0 0 1 14.4539,10.3095")]
    // Negative, so the turn wraps to 260 and the arc becomes the long way round. PaintCode writes
    // AddArc(rect, 10, (360 * ceil(100 / 360)) - 100).
    [InlineData(-10, 90, false, "M14.8861,8.8024A7.5 7.5 0 1 1 7.5,0")]
    // Exactly half a turn, where the two halves are told apart by the direction alone.
    [InlineData(0, 180, false, "M15,7.5A7.5 7.5 0 0 1 0,7.5")]
    // Closed through the middle, which is the wedge PaintCode closes with LineTo(MidX, MidY).
    [InlineData(0, 180, true, "M15,7.5A7.5 7.5 0 0 1 0,7.5L7.5,7.5Z")]
    public void An_Oval_Arc_Turns_The_Way_PaintCode_Turns(double start, double end, bool closed, string expected)
    {
        var shape = new PaintCodeShape(
            "Oval",
            PaintCodeShapeKind.Oval,
            new PaintCodeFrame(0, -15, 15, 15, new PaintCodePoint(9.25, -5.25), 0, 1, 1, 1, false, true),
            new Dictionary<string, PaintCodeBinding>(),
            null,
            PaintCodePaint.None,
            PaintCodePaint.None,
            PaintCodeStroke.None,
            false,
            null,
            new PaintCodeShapeMetrics(0, true, true, true, true, start, end, closed, 0, 0));

        Assert.Equal(expected, PaintCodePathData.For(shape));
    }

    [Fact]
    public void A_Turned_Shape_Turns_The_Other_Way_Because_The_Drawing_Is_Flipped()
    {
        var shape = new PaintCodeShape(
            "Tilted",
            PaintCodeShapeKind.Rectangle,
            new PaintCodeFrame(0, -14, 9, 14, new PaintCodePoint(12.08, -9.9848), -16.9437, 1, 1, 1, false, true),
            new Dictionary<string, PaintCodeBinding>(),
            null,
            PaintCodePaint.None,
            PaintCodePaint.None,
            PaintCodeStroke.None,
            false,
            null,
            PaintCodeShapeMetrics.Default);

        var element = Write(shape);

        Assert.Equal("translate(12.08,9.9848) rotate(16.9437)", element.Attribute("transform")!.Value);
    }

    private static XDocument Written()
    {
        var document = PaintCodeDocument.Parse(SampleDocument.Bytes());
        var canvas = document.Canvases.Single();

        return PaintCodeSvgWriter.Write(canvas, PaintCodeDeclarations.Of(document), PaintCodeSymbols.Of(document), new List<PaintCodeImportNote>());
    }

    private static XElement Element(string name) => Elements(name).First();

    private static System.Collections.Generic.IEnumerable<XElement> Elements(string name)
        => Written().Descendants().Where(element => element.Name.LocalName == name);

    /// <summary>
    /// The constant PaintCode writes beside a driven transform, which is the difference between the
    /// number the drawing had and the number the expression comes to with the document's own values.
    /// </summary>
    /// <remarks>
    /// x defaults to 1 in the scope these are written against, so an item sitting at 7 is driven by
    /// "x + 6" — which is the shape of the constant in PaintCode's own generated code.
    /// </remarks>
    [Fact]
    public void A_Transform_An_Expression_Drives_Adds_Back_What_The_Item_Sits_In()
    {
        var element = Bound("displayAnchorY", "x", 7, anchorY: -7);

        Assert.Equal("translate(0,{{ x + 6 }})", element.Attribute("transform")!.Value);
    }

    [Fact]
    public void A_Driven_Turn_Turns_The_Other_Way_Like_A_Written_One()
    {
        // A turn of one degree driven by an expression that comes to one: nothing to add back.
        var element = Bound("displayRotation", "x", 1, rotation: 1);

        Assert.Equal("translate(0,0) rotate({{ -(x) }})", element.Attribute("transform")!.Value);
    }

    [Fact]
    public void A_Group_Places_Its_Children_Whether_Or_Not_It_Turns_Them()
    {
        // The rule this replaced dropped an untransformed group's anchor entirely, which moved every
        // shape under 322 of the sample's 375 groups.
        var shape = Box(PaintCodeShapeKind.Rectangle, PaintCodeShapeMetrics.Default);
        var group = new PaintCodeGroup(
            "Inner",
            new PaintCodeFrame(0, 0, 0, 0, new PaintCodePoint(3, -4), 0, 1, 1, 1, false, true),
            new Dictionary<string, PaintCodeBinding>(),
            new[] { (PaintCodeItem)shape },
            null);

        var element = WriteTree(Only(group), new List<PaintCodeImportNote>())
            .Descendants().Single(one => one.Name.LocalName == "g" && one.Attribute("id")?.Value == "inner");

        Assert.Equal("translate(3,4)", element.Attribute("transform")!.Value);
    }

    [Fact]
    public void A_Clip_Shape_Is_Placed_Where_The_Shape_It_Was_Drawn_From_Is()
    {
        var clip = new PaintCodeShape(
            "Window",
            PaintCodeShapeKind.Rectangle,
            new PaintCodeFrame(0, -20, 20, 20, new PaintCodePoint(-9.95, 9.54), 0, 1, 1, 1, false, true),
            new Dictionary<string, PaintCodeBinding>(),
            null,
            PaintCodePaint.None,
            PaintCodePaint.None,
            PaintCodeStroke.None,
            false,
            null,
            PaintCodeShapeMetrics.Default);

        var group = new PaintCodeGroup(
            "Clipped",
            Identity(),
            new Dictionary<string, PaintCodeBinding>(),
            new[] { (PaintCodeItem)Box(PaintCodeShapeKind.Rectangle, PaintCodeShapeMetrics.Default) },
            clip);

        var path = WriteTree(Only(group), new List<PaintCodeImportNote>())
            .Descendants().First(one => one.Name.LocalName == "clipPath").Elements().Single();

        Assert.Equal("translate(-9.95,-9.54)", path.Attribute("transform")!.Value);
    }

    [Fact]
    public void A_Driven_Transform_Inside_A_Layer_Is_Written_As_The_Number_It_Had()
    {
        var notes = new List<PaintCodeImportNote>();
        var shape = Driven("displayAnchorY", "x", 1, anchorY: -7);
        var group = new PaintCodeGroup(
            "Faded",
            new PaintCodeFrame(0, 0, 0, 0, default, 0, 1, 1, 0.5, false, true),
            new Dictionary<string, PaintCodeBinding>(),
            new[] { (PaintCodeItem)shape },
            null);

        var element = WriteTree(group, notes).Descendants().First(item => item.Name.LocalName == "path");

        Assert.Equal("translate(0,7)", element.Attribute("transform")!.Value);
        Assert.Contains(notes, note => note.Property == "displayAnchorY" && note.Message.Contains("layer"));
    }

    private static XElement Write(PaintCodeShape shape)
    {
        var canvas = new PaintCodeCanvas(
            "canvas",
            "canvas",
            new PaintCodeRect(0, 0, 30, 30),
            true,
            false,
            new PaintCodeGroup("Root", Identity(), new Dictionary<string, PaintCodeBinding>(), new[] { (PaintCodeItem)shape }, null));

        return PaintCodeSvgWriter.Write(canvas, PaintCodeDeclarations.Of(Scope()), PaintCodeSymbols.Of(Scope()), new List<PaintCodeImportNote>()).Root!.Elements().Last();
    }

    private static XElement Rectangle(double radius, PaintCodeShapeKind kind)
        => Write(Box(kind, new PaintCodeShapeMetrics(radius, true, true, true, true, 0, 360, true, 0, 0)));

    private static PaintCodeShape Box(PaintCodeShapeKind kind, PaintCodeShapeMetrics metrics)
        => new(
            "Box",
            kind,
            new PaintCodeFrame(0, -14, 9, 14, default, 0, 1, 1, 1, false, true),
            new Dictionary<string, PaintCodeBinding>(),
            null,
            PaintCodePaint.None,
            PaintCodePaint.None,
            PaintCodeStroke.None,
            false,
            null,
            metrics);

    private static PaintCodeShape Shape(params PaintCodePathPoint[] points)
        => new(
            "Bezier",
            PaintCodeShapeKind.Bezier,
            Identity(),
            new Dictionary<string, PaintCodeBinding>(),
            new PaintCodePath(new[] { new PaintCodeContour(points, false) }),
            PaintCodePaint.None,
            PaintCodePaint.None,
            PaintCodeStroke.None,
            false,
            null,
            PaintCodeShapeMetrics.Default);

    private static PaintCodeDocument Scope() => PaintCodeDocument.Parse(ScopeDocument.Bytes());

    private static PaintCodeGradient Gradient()
        => new(
            "warm",
            new[]
            {
                new PaintCodeGradientStop(new PaintCodeColor(string.Empty, 255, 0, 0, 1), 0, 0.5, false),
                new PaintCodeGradientStop(new PaintCodeColor(string.Empty, 0, 0, 255, 1), 1, 0.5, false)
            });

    private static PaintCodeShape Filled(
        PaintCodeGradient gradient,
        double angle,
        string? expression = null,
        PaintCodeGradientEnds? ends = null,
        PaintCodeFrame? frame = null,
        bool radial = false,
        PaintCodeShapeKind kind = PaintCodeShapeKind.Bezier,
        PaintCodeShapeMetrics? metrics = null)
        => new(
            "Filled",
            kind,
            frame ?? Identity(),
            expression is { }
                ? new Dictionary<string, PaintCodeBinding> { ["fill"] = new(expression, PaintCodeValueKind.Gradient, null, null, null, null, gradient, null) }
                : new Dictionary<string, PaintCodeBinding>(),
            kind is PaintCodeShapeKind.Bezier
                ? new PaintCodePath(new[] { new PaintCodeContour(new[] { new PaintCodePathPoint(default, default, default) }, false) })
                : null,
            new PaintCodePaint(PaintCodePaintKind.Gradient, null, gradient),
            PaintCodePaint.None,
            PaintCodeStroke.None,
            false,
            null,
            metrics ?? PaintCodeShapeMetrics.Default,
            radial,
            angle,
            ends);

    /// <summary>A box <paramref name="width"/> by <paramref name="height"/> with its top left at the origin.</summary>
    private static PaintCodeFrame At(double width, double height)
        => new(0, -height, width, height, default, 0, 1, 1, 1, false, true);

    /// <summary>The two ends of the one gradient <paramref name="shape"/> is filled with.</summary>
    private static (double X1, double Y1, double X2, double Y2) Laid(PaintCodeShape shape, List<PaintCodeImportNote>? notes = null)
    {
        var gradient = WriteTree(Only(shape), notes ?? new List<PaintCodeImportNote>())
            .Descendants().First(one => one.Name.LocalName == "linearGradient");

        Assert.Equal("userSpaceOnUse", gradient.Attribute("gradientUnits")!.Value);

        double Of(string name) => double.Parse(gradient.Attribute(name)!.Value, CultureInfo.InvariantCulture);

        return (Of("x1"), Of("y1"), Of("x2"), Of("y2"));
    }

    private static PaintCodeGroup Only(PaintCodeItem item)
        => new("Root", Identity(), new Dictionary<string, PaintCodeBinding>(), new[] { item }, null);

    private static PaintCodeFrame Identity() => new(0, 0, 0, 0, default, 0, 1, 1, 1, false, true);

    private static PaintCodeShape Driven(string property, string expression, double value, double anchorY = 0, double rotation = 0)
        => new(
            "Driven",
            PaintCodeShapeKind.Bezier,
            new PaintCodeFrame(0, 0, 0, 0, new PaintCodePoint(0, anchorY), rotation, 1, 1, 1, false, true),
            new Dictionary<string, PaintCodeBinding>
            {
                [property] = new(expression, PaintCodeValueKind.Number, value, null, null, null, null, null)
            },
            new PaintCodePath(new[] { new PaintCodeContour(new[] { new PaintCodePathPoint(default, default, default) }, false) }),
            PaintCodePaint.None,
            PaintCodePaint.None,
            PaintCodeStroke.None,
            false,
            null,
            PaintCodeShapeMetrics.Default);

    private static XElement Bound(string property, string expression, double value, double anchorY = 0, double rotation = 0)
        => WriteTree(
                new PaintCodeGroup("Root", Identity(), new Dictionary<string, PaintCodeBinding>(), new[] { (PaintCodeItem)Driven(property, expression, value, anchorY, rotation) }, null),
                new List<PaintCodeImportNote>())
            .Descendants().First(item => item.Name.LocalName == "path");

    private static XDocument WriteTree(PaintCodeGroup root, List<PaintCodeImportNote> notes)
    {
        var canvas = new PaintCodeCanvas("canvas", "canvas", new PaintCodeRect(0, 0, 30, 30), true, false, new PaintCodeGroup("Canvas", Identity(), new Dictionary<string, PaintCodeBinding>(), new[] { (PaintCodeItem)root }, null));

        return PaintCodeSvgWriter.Write(canvas, PaintCodeDeclarations.Of(Scope()), PaintCodeSymbols.Of(Scope()), notes);
    }
}
