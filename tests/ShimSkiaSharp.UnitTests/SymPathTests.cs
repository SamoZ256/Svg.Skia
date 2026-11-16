// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ShimSkiaSharp.UnitTests;

/// <summary>
/// What a driven clip shape draws once its numbers are known. Each rule is the one the converter
/// applies to the same numbers written as literals, which is what lets a bound clip be the clip
/// the literal document records.
/// </summary>
public class SymPathTests
{
    private static SymPathCommand Command(SymPathOp op, params SymNode[] arguments) => new(op, arguments);

    private static SymNode N(double value) => SymNode.Literal(value);

    private static readonly SymNode s_x = SymNode.Source("x");

    private static SymPath Path(params SymPathCommand[] commands) => new(SKPathFillType.Winding, commands);

    private static PathCommand[] Applied(SymPath path, params float[] arguments) => path.Apply(arguments).Commands!.ToArray();

    private static readonly SymPath s_arc = Path(
        Command(SymPathOp.MoveTo, N(0), N(0)),
        Command(SymPathOp.ArcTo, s_x, s_x, N(0), s_x, s_x, s_x, s_x));

    [Fact]
    public void An_Arc_With_No_Radius_Is_A_Line()
    {
        Assert.Equal(new LineToPathCommand(4, 0), Applied(s_arc, 0, 0, 0, 5, 0, 0, 0, 4, 0)[1]);
        Assert.Equal(new LineToPathCommand(4, 0), Applied(s_arc, 0, 0, -float.Epsilon, 5, 0, 0, 0, 4, 0)[1]);
    }

    [Fact]
    public void An_Arc_Ending_Where_It_Starts_Draws_Nothing()
    {
        Assert.Single(Applied(s_arc, 0, 0, 5, 5, 0, 1, 1, 0, 0));
    }

    [Fact]
    public void An_Arc_Takes_Its_Radii_Unsigned_And_A_Flag_Wherever_It_Is_Not_Nought()
    {
        Assert.Equal(
            new ArcToPathCommand(5, 3, 0, SKPathArcSize.Large, SKPathDirection.CounterClockwise, 8, 0),
            Applied(s_arc, 0, 0, -5, -3, 0, -2, 0, 8, 0)[1]);
        Assert.Equal(
            new ArcToPathCommand(5, 3, 0, SKPathArcSize.Small, SKPathDirection.Clockwise, 8, 0),
            Applied(s_arc, 0, 0, 5, 3, 0, 0, 0.5f, 8, 0)[1]);
    }

    /// <summary>A close puts the point an arc is measured from back where the figure began.</summary>
    [Fact]
    public void A_Close_Returns_To_Where_The_Figure_Began()
    {
        var path = Path(
            Command(SymPathOp.MoveTo, N(2), N(2)),
            Command(SymPathOp.LineTo, N(8), N(2)),
            Command(SymPathOp.Close),
            Command(SymPathOp.ArcTo, N(4), N(4), N(0), N(0), N(1), s_x, N(2)));

        Assert.Equal(3, Applied(path, 2, 2, 8, 2, 4, 4, 0, 0, 1, 2, 2).Length);
        Assert.Equal(4, Applied(path, 2, 2, 8, 2, 4, 4, 0, 0, 1, 6, 2).Length);
    }

    private static readonly SymPath s_rect = Path(Command(SymPathOp.Rect, s_x, s_x, s_x, s_x, s_x, s_x, s_x, s_x));

    [Theory]
    [InlineData(0f, 10f)]
    [InlineData(10f, -1f)]
    public void A_Rect_With_No_Size_Is_Empty_And_Not_Null(float width, float height)
    {
        var path = s_rect.Apply(new[] { 1f, 1f, width, height, 2f, 2f, 2f, 2f });

        Assert.NotNull(path);
        Assert.True(path.IsEmpty);
    }

    [Theory]
    [InlineData(0f, 3f, 3f, 3f)]
    [InlineData(3f, 0f, 3f, 3f)]
    [InlineData(-3f, 2f, 3f, 2f)]
    [InlineData(9f, 2f, 5f, 2f)]
    [InlineData(2f, 9f, 2f, 4f)]
    public void A_Rect_Takes_Its_Radii_As_SvgRectangle_And_The_Converter_Do(float rx, float ry, float expectedX, float expectedY)
    {
        Assert.Equal(
            new AddRoundRectPathCommand(SKRect.Create(1, 1, 10, 8), expectedX, expectedY),
            Assert.Single(s_rect.Apply(new[] { 1f, 1f, 10f, 8f, rx, ry, rx, ry }).Commands!));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(-3f, -2f)]
    public void A_Rect_With_No_Radius_Is_A_Rect(float rx, float ry)
    {
        Assert.Equal(new AddRectPathCommand(SKRect.Create(1, 1, 10, 8)), Assert.Single(s_rect.Apply(new[] { 1f, 1f, 10f, 8f, rx, ry, rx, ry }).Commands!));
    }

    /// <summary>A radius borrowed is the other one measured on the borrowing axis, which a percentage makes differ.</summary>
    [Fact]
    public void A_Rect_Borrows_The_Other_Radius_As_Measured_On_Its_Own_Axis()
    {
        Assert.Equal(
            new AddRoundRectPathCommand(SKRect.Create(1, 1, 10, 8), 3, 1.5f),
            Assert.Single(s_rect.Apply(new[] { 1f, 1f, 10f, 8f, 3f, 0f, 1.5f, 0f }).Commands!));
        Assert.Equal(
            new AddRoundRectPathCommand(SKRect.Create(1, 1, 10, 8), 2.5f, 2),
            Assert.Single(s_rect.Apply(new[] { 1f, 1f, 10f, 8f, 0f, 2f, 0f, 2.5f }).Commands!));
    }

    [Fact]
    public void An_Ellipse_And_A_Circle_Draw_As_Their_Converters_Do()
    {
        var ellipse = Path(Command(SymPathOp.Ellipse, s_x, s_x, s_x, s_x));
        var circle = Path(Command(SymPathOp.Circle, s_x, s_x, s_x));

        Assert.Equal(new AddOvalPathCommand(SKRect.Create(1, 2, 8, 6)), Assert.Single(ellipse.Apply(new[] { 5f, 5f, -4f, 3f }).Commands!));
        Assert.True(ellipse.Apply(new[] { 5f, 5f, -4f, -3f }).IsEmpty);
        Assert.True(ellipse.Apply(new[] { 5f, 5f, 4f, 0f }).IsEmpty);
        Assert.Equal(new AddCirclePathCommand(5, 5, 2), Assert.Single(circle.Apply(new[] { 5f, 5f, 2f }).Commands!));
        Assert.True(circle.Apply(new[] { 5f, 5f, 0f }).IsEmpty);
    }

    [Fact]
    public void A_Number_That_Is_Not_Finite_Draws_Nothing_And_Keeps_The_Fill()
    {
        var path = new SymPath(SKPathFillType.EvenOdd, new[] { Command(SymPathOp.Circle, s_x, s_x, s_x) });

        foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var applied = path.Apply(new[] { 5f, value, 2f });
            Assert.True(applied.IsEmpty);
            Assert.Equal(SKPathFillType.EvenOdd, applied.FillType);
        }
    }

    /// <summary>A relative coordinate after a driven one is their sum, so nought has to be folded through it.</summary>
    [Fact]
    public void The_Stand_In_Folds_What_Each_Number_Was_Built_From()
    {
        var path = Path(
            Command(SymPathOp.MoveTo, SymNode.Add(N(10), s_x), SymNode.Subtract(N(4), SymNode.Negate(s_x))),
            Command(SymPathOp.LineTo, SymNode.Multiply(N(2), N(3)), SymNode.Divide(N(9), N(3))));

        Assert.Equal(new PathCommand[] { new MoveToPathCommand(10, 4), new LineToPathCommand(6, 3) }, path.ToPlaceholder().Commands!.ToArray());
    }

    [Fact]
    public void Each_Command_Reads_As_Many_Numbers_As_It_Takes()
    {
        Assert.Equal(
            new[] { 2, 2, 4, 6, 0, 7, 8, 4, 3 },
            new[] { SymPathOp.MoveTo, SymPathOp.LineTo, SymPathOp.QuadTo, SymPathOp.CubicTo, SymPathOp.Close, SymPathOp.ArcTo, SymPathOp.Rect, SymPathOp.Ellipse, SymPathOp.Circle }
                .Select(SymPath.Arity));
        Assert.Equal(9, s_arc.ArgumentCount);
    }

    [Fact]
    public void A_Clip_Is_Driven_Wherever_Anything_In_It_Is()
    {
        var literal = Path(Command(SymPathOp.Circle, N(1), N(1), N(1)));
        var driven = new SymMatrix(new[] { new SymTransform(SymTransformOp.Translate, new[] { s_x, SymNode.Zero }) });

        Assert.False(literal.IsSymbolic);
        Assert.True(s_rect.IsSymbolic);
        Assert.False(new PathClip { SymbolicPath = literal }.IsSymbolic);
        Assert.True(new PathClip { SymbolicPath = s_rect }.IsSymbolic);
        Assert.True(new PathClip { SymbolicTransform = driven }.IsSymbolic);

        var nested = new ClipPath { Clip = new ClipPath() };
        nested.Clip.Clips!.Add(new PathClip { Clip = new ClipPath { SymbolicTransform = driven } });
        Assert.True(nested.IsSymbolic);
        Assert.False(new ClipPath { Clip = new ClipPath() }.IsSymbolic);
    }

    [Fact]
    public void A_Clone_Shares_What_Drives_The_Clip()
    {
        var driven = new SymMatrix(new[] { new SymTransform(SymTransformOp.Translate, new[] { s_x, SymNode.Zero }) });
        var clip = new ClipPath { SymbolicTransform = driven };
        clip.Clips!.Add(new PathClip { Path = s_rect.ToPlaceholder(), SymbolicPath = s_rect, SymbolicTransform = driven });

        var clone = clip.DeepClone();

        Assert.Same(driven, clone.SymbolicTransform);
        Assert.Same(s_rect, clone.Clips![0].SymbolicPath);
        Assert.Same(driven, clone.Clips[0].SymbolicTransform);
        Assert.Same(s_rect, s_rect.DeepClone());
    }
}
