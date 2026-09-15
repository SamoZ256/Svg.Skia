// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Linq;
using Xunit;

namespace Svg.PaintCode.UnitTests;

public class PaintCodeReaderTests
{
    [Fact]
    public void A_Document_Comes_Back_With_Its_Desks_And_Canvases()
    {
        var document = PaintCodeDocument.Parse(SampleDocument.Bytes());
        var desk = Assert.Single(document.Desks);
        var canvas = Assert.Single(desk.Canvases);

        Assert.Equal("Icons", document.Name);
        Assert.Equal("Overlays", desk.Name);
        Assert.Equal("overlay-error", canvas.Name);
        Assert.Equal(30d, canvas.Bounds.Width);
        Assert.True(canvas.IsExported);
    }

    [Fact]
    public void A_Canvas_Is_Keyed_By_Its_Name_With_Everything_But_Letters_And_Digits_Removed()
        => Assert.Equal("symboloverlayerror", PaintCodeReader.Identifier("symbol-overlay-error"));

    [Fact]
    public void A_Bezier_Keeps_Its_Points_And_Their_Control_Offsets()
    {
        var canvas = PaintCodeDocument.Parse(SampleDocument.Bytes()).Canvases.Single();
        var shape = Assert.IsType<PaintCodeShape>(canvas.Root.Children.First());
        var contour = Assert.Single(shape.Path!.Contours);

        Assert.Equal(PaintCodeShapeKind.Bezier, shape.Kind);
        Assert.True(contour.IsClosed);
        Assert.Equal(2, contour.Points.Count);
        Assert.Equal(9.8047d, contour.Points[0].Position.X);
        Assert.Equal(-1.5499d, contour.Points[0].Position.Y);
        Assert.Equal(0.521d, contour.Points[0].Exiting.X);
        Assert.Equal(-1.0882d, contour.Points[1].Entering.X);
    }

    [Fact]
    public void An_Anchor_Is_Read_As_The_Shapes_Position_In_Canvas_Space()
    {
        var shape = (PaintCodeShape)PaintCodeDocument.Parse(SampleDocument.Bytes()).Canvases.Single().Root.Children.First();

        Assert.Equal(3.0844d, shape.Frame.Anchor.X);
        Assert.Equal(-3.6379d, shape.Frame.Anchor.Y);
    }

    [Fact]
    public void A_Winding_Rule_Of_One_Is_Even_Odd()
        => Assert.True(((PaintCodeShape)PaintCodeDocument.Parse(SampleDocument.Bytes()).Canvases.Single().Root.Children.First()).IsEvenOdd);

    [Fact]
    public void A_Colour_Is_Read_From_Its_Components_As_The_Bytes_PaintCode_Emits()
    {
        var shape = (PaintCodeShape)PaintCodeDocument.Parse(SampleDocument.Bytes()).Canvases.Single().Root.Children.First();
        var color = Assert.IsType<PaintCodeColor>(shape.Fill.Color);

        Assert.Equal(PaintCodePaintKind.Color, shape.Fill.Kind);
        Assert.Equal("colorPurple", color.Name);
        Assert.Equal(95, color.Red);
        Assert.Equal(0, color.Green);
        Assert.Equal(201, color.Blue);
        Assert.Equal(1d, color.Alpha);
    }

    [Fact]
    public void A_Derived_Colour_Takes_Its_Parents_Channels_And_The_Operations_Alpha()
    {
        var color = PaintCodeDocument.Parse(SampleDocument.Bytes()).Variables.Single(variable => variable.Name == "purple70").Value.Color!;

        Assert.Equal(95, color.Red);
        Assert.Equal(201, color.Blue);
        Assert.Equal(0.7d, color.Alpha);
        Assert.False(color.IsApproximate);
    }

    [Fact]
    public void An_Input_Variable_Carries_Its_Value_And_A_Derived_One_Its_Expression()
    {
        var variables = PaintCodeDocument.Parse(SampleDocument.Bytes()).Variables;
        var state = variables.Single(variable => variable.Name == "state");
        var off = variables.Single(variable => variable.Name == "off");

        Assert.False(state.IsDerived);
        Assert.Equal(PaintCodeValueKind.Boolean, state.Kind);
        Assert.True(state.Value.Flag);
        Assert.Equal("!state", off.Expression);
        Assert.True(off.IsDerived);
    }

    [Fact]
    public void A_Bound_Property_Keeps_The_Expression_That_Drives_It()
    {
        var shape = (PaintCodeShape)PaintCodeDocument.Parse(SampleDocument.Bytes()).Canvases.Single().Root.Children.First();

        Assert.Equal("state ? colorPurple : colorPurple", shape.Bindings["fill"].Expression);
    }

    [Fact]
    public void A_Range_On_A_Variable_Becomes_Its_Bounds()
    {
        var level = PaintCodeDocument.Parse(SampleDocument.Bytes()).Variables.Single(variable => variable.Name == "level");

        Assert.Equal(0d, level.Minimum);
        Assert.Equal(1d, level.Maximum);
    }
}
