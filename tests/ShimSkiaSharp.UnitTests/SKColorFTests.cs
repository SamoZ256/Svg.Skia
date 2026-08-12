using ShimSkiaSharp;
using Xunit;

namespace ShimSkiaSharp.UnitTests;

public class SKColorFTests
{
    [Fact]
    public void Implicit_To_SKColor_Works()
    {
        var colorF = new SKColorF(0.5f, 0.25f, 0.75f, 0.4f);
        SKColor color = colorF;
        Assert.Equal((byte)(0.5f * 255f), color.Red);
        Assert.Equal((byte)(0.25f * 255f), color.Green);
        Assert.Equal((byte)(0.75f * 255f), color.Blue);
        Assert.Equal((byte)(0.4f * 255f), color.Alpha);
    }

    [Fact]
    public void ToString_Returns_CommaSeparatedValues()
    {
        var colorF = new SKColorF(1f, 0.5f, 0.25f, 0f);
        Assert.Equal("1, 0.5, 0.25, 0", colorF.ToString());
    }

    [Fact]
    public void Expression_Defaults_To_Null()
    {
        Assert.Null(new SKColorF(0.5f, 0.25f, 0.75f, 0.4f).Expression);
    }

    [Fact]
    public void Implicit_To_SKColor_Carries_The_Expression()
    {
        var colorF = new SKColorF(0.5f, 0.25f, 0.75f, 0.4f).WithExpression(SymNode.Source("Fill(t)"));
        SKColor color = colorF;

        Assert.Equal(SymNode.Source("Fill(t)"), color.Expression);
    }

    [Fact]
    public void Round_Trip_Through_SKColor_Preserves_The_Expression()
    {
        SKColor color = new SKColor(255, 128, 64, 32).WithExpression(SymNode.Source("Fill(t)"));
        SKColorF colorF = color;
        SKColor roundTripped = colorF;

        Assert.Equal(SymNode.Source("Fill(t)"), roundTripped.Expression);
    }
}
