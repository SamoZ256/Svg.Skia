// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;
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
    public void A_Fill_Is_Written_As_The_Bytes_PaintCode_Emits()
        => Assert.Equal("#5f00c9", Element("path").Attribute("fill")!.Value);

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

    [Fact]
    public void A_Whole_Oval_Keeps_Svgs_Own_Element()
    {
        var shape = Box(PaintCodeShapeKind.Oval, new PaintCodeShapeMetrics(0, true, true, true, true, 0, 360, true, 0, 0));

        Assert.True(PaintCodePathData.IsWholeEllipse(shape));
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
        var canvas = PaintCodeDocument.Parse(SampleDocument.Bytes()).Canvases.Single();

        return PaintCodeSvgWriter.Write(canvas, new List<PaintCodeImportNote>());
    }

    private static XElement Element(string name)
        => Written().Descendants().First(element => element.Name.LocalName == name);

    private static XElement Write(PaintCodeShape shape)
    {
        var canvas = new PaintCodeCanvas(
            "canvas",
            "canvas",
            new PaintCodeRect(0, 0, 30, 30),
            true,
            false,
            new PaintCodeGroup("Root", Identity(), new Dictionary<string, PaintCodeBinding>(), new[] { (PaintCodeItem)shape }, null));

        return PaintCodeSvgWriter.Write(canvas, new List<PaintCodeImportNote>()).Root!.Elements().First();
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

    private static PaintCodeFrame Identity() => new(0, 0, 0, 0, default, 0, 1, 1, 1, false, true);
}
