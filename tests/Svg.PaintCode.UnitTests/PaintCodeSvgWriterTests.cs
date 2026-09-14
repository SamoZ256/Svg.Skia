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
    [InlineData(0, 360)]
    [InlineData(90, -270)]
    public void A_Whole_Oval_Keeps_Svgs_Own_Element(double start, double end)
    {
        var shape = Box(PaintCodeShapeKind.Oval, new PaintCodeShapeMetrics(0, true, true, true, true, start, end, true, 0, 0));

        Assert.True(PaintCodePathData.IsWholeEllipse(shape));
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

    [Fact]
    public void A_Transform_An_Expression_Drives_Adds_Back_What_The_Item_Sits_In()
    {
        var element = Bound("displayAnchorY", "x", 1, anchorY: -7);

        Assert.Equal("translate(0,{{ x + 6 }})", element.Attribute("transform")!.Value);
    }

    [Fact]
    public void A_Driven_Turn_Turns_The_Other_Way_Like_A_Written_One()
    {
        var element = Bound("displayRotation", "x", -1080, rotation: -1080);

        Assert.Equal("translate(0,0) rotate({{ -(x) }})", element.Attribute("transform")!.Value);
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

    private static PaintCodeShape Filled(PaintCodeGradient gradient, double angle, string? expression = null)
        => new(
            "Filled",
            PaintCodeShapeKind.Bezier,
            Identity(),
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
            false,
            angle);

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
