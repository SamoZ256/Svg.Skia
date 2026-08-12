using ShimSkiaSharp;
using Xunit;

namespace ShimSkiaSharp.UnitTests;

public class SymNodeTests
{
    [Fact]
    public void Source_Keeps_Author_Text()
    {
        var node = SymNode.Source("MathF.Sin(t * MathF.Tau)");
        Assert.Equal("MathF.Sin(t * MathF.Tau)", Assert.IsType<SymSource>(node).Text);
    }

    [Fact]
    public void Records_Compare_Structurally()
    {
        var left = SymNode.Add(SymNode.Source("t"), SymNode.Literal(2));
        var right = SymNode.Add(SymNode.Source("t"), SymNode.Literal(2));

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotSame(left, right);
    }

    [Fact]
    public void Different_Raw_Text_Is_Not_Equal()
    {
        Assert.NotEqual(SymNode.Source("t"), SymNode.Source("u"));
    }

    [Fact]
    public void Multiply_By_One_Folds_Away()
    {
        var raw = SymNode.Source("t");

        Assert.Same(raw, SymNode.Multiply(raw, SymNode.One));
        Assert.Same(raw, SymNode.Multiply(SymNode.One, raw));
    }

    [Fact]
    public void Add_Zero_And_Subtract_Zero_Fold_Away()
    {
        var raw = SymNode.Source("t");

        Assert.Same(raw, SymNode.Add(raw, SymNode.Zero));
        Assert.Same(raw, SymNode.Add(SymNode.Zero, raw));
        Assert.Same(raw, SymNode.Subtract(raw, SymNode.Zero));
    }

    [Fact]
    public void Divide_By_One_Folds_Away()
    {
        var raw = SymNode.Source("t");

        Assert.Same(raw, SymNode.Divide(raw, SymNode.One));
    }

    [Fact]
    public void Multiply_By_Zero_Does_Not_Fold()
    {
        // NaN * 0 is NaN, so collapsing to Zero would change the meaning of an opaque operand.
        var raw = SymNode.Source("t");
        var node = SymNode.Multiply(raw, SymNode.Zero);

        var binary = Assert.IsType<SymBinary>(node);
        Assert.Equal(SymOp.Multiply, binary.Op);
        Assert.Same(raw, binary.Left);
    }

    [Fact]
    public void Non_Identity_Operands_Build_A_Binary_Node()
    {
        var node = SymNode.Multiply(SymNode.Source("t"), SymNode.Literal(2));

        var binary = Assert.IsType<SymBinary>(node);
        Assert.Equal(SymOp.Multiply, binary.Op);
        Assert.Equal(SymNode.Source("t"), binary.Left);
        Assert.Equal(SymNode.Literal(2), binary.Right);
    }

    [Fact]
    public void ScaleAlpha_By_One_Returns_The_Color_Unchanged()
    {
        var color = SymNode.Source("SKColors.Red");

        Assert.Same(color, SymNode.ScaleAlpha(color, SymNode.One));
    }

    [Fact]
    public void ScaleAlpha_Wraps_When_Factor_Is_Not_One()
    {
        var color = SymNode.Source("SKColors.Red");
        var factor = SymNode.Source("opacity");
        var node = SymNode.ScaleAlpha(color, factor);

        var binary = Assert.IsType<SymBinary>(node);
        Assert.Equal(SymOp.ScaleAlpha, binary.Op);
        Assert.Same(color, binary.Left);
        Assert.Same(factor, binary.Right);
    }

    [Fact]
    public void Negate_Builds_A_Unary_Node()
    {
        var unary = Assert.IsType<SymUnary>(SymNode.Negate(SymNode.Source("t")));

        Assert.Equal(SymOp.Negate, unary.Op);
        Assert.Equal(SymNode.Source("t"), unary.Operand);
    }

    [Fact]
    public void IsLiteral_Matches_Only_Literal_Nodes()
    {
        Assert.True(SymNode.IsLiteral(SymNode.One, 1));
        Assert.True(SymNode.IsLiteral(SymNode.Literal(2.5), 2.5));
        Assert.False(SymNode.IsLiteral(SymNode.Source("1"), 1));
        Assert.False(SymNode.IsLiteral(null, 1));
    }
}
