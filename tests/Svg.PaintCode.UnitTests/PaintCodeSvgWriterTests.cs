// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Svg.PaintCode.UnitTests;

public class PaintCodeSvgWriterTests
{
    [Fact]
    public void A_Canvas_Becomes_A_Drawing_Of_Its_Own_Size_Measured_From_Its_Own_Corner()
    {
        var root = Written().Root!;

        Assert.Equal("30", root.Attribute("width")!.Value);
        Assert.Equal("30", root.Attribute("height")!.Value);
        Assert.Equal("0 0 30 30", root.Attribute("viewBox")!.Value);
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
        var document = PaintCodeDocument.Parse(ScopeDocument.Bytes());
        var element = WriteTree(Only(Filled(Gradient(), -90)), new List<PaintCodeImportNote>());
        var gradient = element.Descendants().First(one => one.Name.LocalName == "linearGradient");

        Assert.Equal("0.5", gradient.Attribute("x1")!.Value);
        Assert.Equal("0", gradient.Attribute("y1")!.Value);
        Assert.Equal("0.5", gradient.Attribute("x2")!.Value);
        Assert.Equal("1", gradient.Attribute("y2")!.Value);
        Assert.Equal(new[] { "#ff0000", "#0000ff" }, gradient.Elements().Select(stop => stop.Attribute("stop-color")!.Value));
        Assert.Equal($"url(#{gradient.Attribute("id")!.Value})", element.Descendants().First(one => one.Name.LocalName == "path").Attribute("fill")!.Value);
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

    private static PaintCodeShape Filled(PaintCodeGradient gradient, double angle, string? expression = null, PaintCodeGradientEnds? ends = null, PaintCodeFrame? frame = null, bool radial = false)
        => new(
            "Filled",
            PaintCodeShapeKind.Bezier,
            frame ?? Identity(),
            expression is { }
                ? new Dictionary<string, PaintCodeBinding> { ["fill"] = new(expression, PaintCodeValueKind.Gradient, null, null, null, null, gradient, null) }
                : new Dictionary<string, PaintCodeBinding>(),
            new PaintCodePath(new[] { new PaintCodeContour(new[] { new PaintCodePathPoint(default, default, default) }, false) }),
            new PaintCodePaint(PaintCodePaintKind.Gradient, null, gradient),
            PaintCodePaint.None,
            PaintCodeStroke.None,
            false,
            null,
            PaintCodeShapeMetrics.Default,
            radial,
            angle,
            ends);

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
