using System;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// Reading a padding, and refusing one that would not be padding.
/// </summary>
public class SvgPaddingTests
{
    private static (float Top, float Right, float Bottom, float Left) Of(string text)
    {
        var padding = SvgPadding.Parse(text);

        return (padding.Top, padding.Right, padding.Bottom, padding.Left);
    }

    [Fact]
    public void Nothing_Is_No_Padding()
    {
        Assert.True(SvgPadding.Parse(null).IsEmpty);
        Assert.True(SvgPadding.Parse("   ").IsEmpty);
        Assert.True(SvgPadding.None.IsEmpty);
    }

    [Fact]
    public void One_Value_Is_Every_Side()
        => Assert.Equal((0.1f, 0.1f, 0.1f, 0.1f), Of("10%"));

    [Fact]
    public void Two_Are_Down_And_Across()
        => Assert.Equal((0.05f, 0.1f, 0.05f, 0.1f), Of("5% 10%"));

    [Fact]
    public void Three_Are_Top_Across_And_Bottom()
        => Assert.Equal((0.05f, 0.1f, 0.2f, 0.1f), Of("5% 10% 20%"));

    [Fact]
    public void Four_Go_Clockwise_From_The_Top()
        => Assert.Equal((0.05f, 0.1f, 0.2f, 0.3f), Of("5% 10% 20% 30%"));

    [Fact]
    public void A_Fraction_Says_The_Same_Thing_As_A_Percentage()
        => Assert.Equal(Of("10%"), Of("0.1"));

    [Fact]
    public void A_Bare_Number_Is_The_Fraction_And_Not_The_Percentage()
    {
        // So `10` asks for ten times the canvas and is refused, rather than quietly meaning a tenth
        // of it -- which is the reading that would silently produce the wrong drawing.
        Assert.Throws<ArgumentException>(() => SvgPadding.Parse("10"));
    }

    [Fact]
    public void Padding_That_Would_Crop_Is_Refused()
    {
        // Adding space is the whole of what this does, so a negative side is a different feature
        // asked for by the wrong name.
        Assert.Throws<ArgumentException>(() => new SvgPadding(-0.1f, 0f, 0f, 0f));
        Assert.Throws<ArgumentException>(() => SvgPadding.Parse("-5%"));
    }

    [Fact]
    public void Padding_That_Leaves_No_Room_Is_Refused()
    {
        Assert.Throws<ArgumentException>(() => SvgPadding.Parse("60% 60%"));
        Assert.Throws<ArgumentException>(() => SvgPadding.Parse("50%"));
    }

    [Fact]
    public void Five_Values_Are_Not_A_Padding()
        => Assert.Throws<ArgumentException>(() => SvgPadding.Parse("1% 2% 3% 4% 5%"));

    [Fact]
    public void Something_That_Is_Not_A_Number_Says_So()
        => Assert.Throws<ArgumentException>(() => SvgPadding.Parse("wide"));
}
