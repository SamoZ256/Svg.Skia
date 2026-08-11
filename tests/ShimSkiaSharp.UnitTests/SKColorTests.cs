using ShimSkiaSharp;
using Xunit;

namespace ShimSkiaSharp.UnitTests;

public class SKColorTests
{
    [Fact]
    public void Implicit_To_SKColorF_Works()
    {
        var color = new SKColor(255, 128, 64, 32);
        SKColorF colorF = color;
        Assert.Equal(1f, colorF.Red, 6);
        Assert.Equal(128f / 255f, colorF.Green, 6);
        Assert.Equal(64f / 255f, colorF.Blue, 6);
        Assert.Equal(32f / 255f, colorF.Alpha, 6);
    }

    [Fact]
    public void ToString_Returns_CommaSeparatedValues()
    {
        var color = new SKColor(1, 2, 3, 4);
        Assert.Equal("1, 2, 3, 4", color.ToString());
    }

    [Fact]
    public void Expression_Defaults_To_Null()
    {
        Assert.Null(new SKColor(1, 2, 3, 4).Expression);
        Assert.Null(SKColor.Empty.Expression);
    }

    [Fact]
    public void WithExpression_Keeps_The_Channels()
    {
        var color = new SKColor(1, 2, 3, 4).WithExpression(SymNode.Source("Fill(t)"));

        Assert.Equal(1, color.Red);
        Assert.Equal(2, color.Green);
        Assert.Equal(3, color.Blue);
        Assert.Equal(4, color.Alpha);
        Assert.Equal(SymNode.Source("Fill(t)"), color.Expression);
    }

    [Fact]
    public void Same_Channels_With_Different_Expressions_Are_Not_Equal()
    {
        var left = new SKColor(1, 2, 3, 4).WithExpression(SymNode.Source("Fill(t)"));
        var right = new SKColor(1, 2, 3, 4).WithExpression(SymNode.Source("Fill(u)"));

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void Symbolic_Color_Is_Not_Equal_To_Its_Plain_Fallback()
    {
        var plain = new SKColor(1, 2, 3, 4);
        var symbolic = plain.WithExpression(SymNode.Source("Fill(t)"));

        Assert.NotEqual(plain, symbolic);
        Assert.NotEqual(plain.GetHashCode(), symbolic.GetHashCode());
    }

    [Fact]
    public void Equal_Expressions_Compare_Structurally()
    {
        var left = new SKColor(1, 2, 3, 4).WithExpression(SymNode.Source("Fill(t)"));
        var right = new SKColor(1, 2, 3, 4).WithExpression(SymNode.Source("Fill(t)"));

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Implicit_To_SKColorF_Carries_The_Expression()
    {
        SKColorF colorF = new SKColor(255, 128, 64, 32).WithExpression(SymNode.Source("Fill(t)"));

        Assert.Equal(SymNode.Source("Fill(t)"), colorF.Expression);
    }

    [Fact]
    public void ToString_Appends_The_Expression_When_Symbolic()
    {
        var color = new SKColor(1, 2, 3, 4).WithExpression(SymNode.Source("Fill(t)"));

        Assert.Equal("1, 2, 3, 4 [SymSource { Text = Fill(t) }]", color.ToString());
    }
}
