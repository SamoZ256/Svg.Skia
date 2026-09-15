// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using SkiaSharp;
using Xunit;

namespace Svg.PaintCode.UnitTests;

/// <summary>
/// The derived-colour operations, against the ones PaintCode itself runs.
/// </summary>
/// <remarks>
/// <c>Svg.PaintCode</c> targets netstandard2.0 and does not reference SkiaSharp, so the conversion
/// PaintCode reaches through <c>SKColor.ToHsv</c> is written out by hand there. This is where the two
/// are held to each other — the drawing these colours are compared against goes through SkiaSharp's,
/// so a scale or a rounding that differs would be a difference nothing else would name.
/// </remarks>
public class PaintCodeColorOperationsTests
{
    private static PaintCodeColor Colour(byte red, byte green, byte blue, double alpha = 1)
        => new("c", red, green, blue, alpha);

    /// <summary>
    /// Saturation is a percentage, which is the whole of why this is worth a test.
    /// </summary>
    /// <remarks>
    /// The sample asks for 0.2 and means two tenths of one percent: the colour it names is
    /// accentColorGray, and on a 0-to-1 reading it would come out barely touched instead of grey.
    /// </remarks>
    [Theory]
    [InlineData(0xfb, 0x3e, 0x72, 0.2)]     // accentColorOn, the sample's own case
    [InlineData(0xfb, 0x3e, 0x72, 50)]
    [InlineData(0x1f, 0x21, 0x23, 0.2)]     // nearly grey already
    [InlineData(0x00, 0x00, 0x00, 40)]      // no hue to keep
    [InlineData(0xff, 0xff, 0xff, 100)]
    [InlineData(0x4e, 0xc3, 0x6b, 12.5)]    // green, so the sector is not the first
    [InlineData(0x14, 0x95, 0xff, 80)]      // blue
    public void Changing_The_Saturation_Agrees_With_Skia(byte red, byte green, byte blue, double saturation)
    {
        var ours = PaintCodeColorOperations.Saturation(Colour(red, green, blue), saturation);

        new SKColor(red, green, blue).ToHsv(out var hue, out _, out var value);

        var theirs = SKColor.FromHsv(hue, (float)saturation, value);

        Assert.Equal((theirs.Red, theirs.Green, theirs.Blue), (ours.Red, ours.Green, ours.Blue));
    }

    /// <summary>A shadow only darkens: PaintCode blends against black carrying the colour's own alpha,
    /// so the alpha term cancels and nothing but the channels moves.</summary>
    [Theory]
    [InlineData(0xfb, 0x3e, 0x72, 0.2)]
    [InlineData(0x4e, 0xc3, 0x6b, 0.5)]
    [InlineData(0xff, 0xff, 0xff, 1)]
    public void A_Shadow_Agrees_With_Skia(byte red, byte green, byte blue, double amount)
    {
        var ours = PaintCodeColorOperations.Shadow(Colour(red, green, blue, 0.5), amount);

        // colorByBlendingColors, against black at the colour's own alpha.
        var expected = (
            (byte)((1f - (float)amount) * red),
            (byte)((1f - (float)amount) * green),
            (byte)((1f - (float)amount) * blue));

        Assert.Equal(expected, (ours.Red, ours.Green, ours.Blue));
        Assert.Equal(0.5, ours.Alpha);
    }

    /// <summary>
    /// The chain the sample's accentColorOff is: desaturated, shadowed, then given an alpha.
    /// </summary>
    /// <remarks>
    /// Only the last of the three was read before, so every icon drawn in accentColorOff came out in
    /// the accent's own hue rather than the grey it is named for.
    /// </remarks>
    [Fact]
    public void The_Sample_Chain_Comes_Out_Grey()
    {
        var gray = PaintCodeColorOperations.Saturation(Colour(0xfb, 0x3e, 0x72), 0.2);
        var shadowed = PaintCodeColorOperations.Shadow(gray, 0.2);

        Assert.Equal((byte)0xfb, gray.Red);
        Assert.InRange(gray.Red - gray.Blue, -2, 2);

        // Darker than what it came from, and still grey.
        Assert.True(shadowed.Red < gray.Red);
        Assert.InRange(shadowed.Red - shadowed.Blue, -2, 2);
    }
}
