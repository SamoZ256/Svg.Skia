using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Svg;
using SkiaSharp;
using Svg.Skia;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgOpacityVisualTests
{
    [AvaloniaFact]
    public void SvgImage_AppliesOpacityToIndividualElement()
    {
        using var bitmap = RenderSvg("""
            <svg xmlns="http://www.w3.org/2000/svg" width="80" height="40">
              <rect x="0" y="0" width="40" height="40" fill="black" opacity="0"/>
              <rect x="40" y="0" width="40" height="40" fill="black" opacity="0.5"/>
            </svg>
            """);

        AssertColor(GetPixel(bitmap, 20, 20), 255, 6);
        AssertColor(GetPixel(bitmap, 60, 20), 127, 6);
    }

    [AvaloniaFact]
    public void SvgImage_CompositesGroupBeforeApplyingOpacity()
    {
        using var bitmap = RenderSvg("""
            <svg xmlns="http://www.w3.org/2000/svg" width="80" height="40">
              <g opacity="0.5">
                <rect x="0" y="0" width="60" height="40" fill="black"/>
                <rect x="20" y="0" width="60" height="40" fill="black"/>
              </g>
            </svg>
            """);

        var single = GetPixel(bitmap, 10, 20);
        var overlap = GetPixel(bitmap, 40, 20);

        AssertColor(single, 127, 6);
        AssertColor(overlap, 127, 6);
    }

    [AvaloniaFact]
    public void SvgImage_MeasuresANonScalingStrokeFromTheDrawing()
    {
        // 4 wide however the group stretches it, where a plain stroke would be 16 and reach y = 15.
        using var bitmap = RenderSvg("""
            <svg xmlns="http://www.w3.org/2000/svg" width="80" height="40">
              <g transform="scale(1 4)">
                <line x1="10" y1="5" x2="70" y2="5" stroke="black" stroke-width="4" vector-effect="non-scaling-stroke"/>
              </g>
            </svg>
            """);

        AssertColor(GetPixel(bitmap, 40, 20), 0, 6);
        AssertColor(GetPixel(bitmap, 40, 15), 255, 6);
        AssertColor(GetPixel(bitmap, 40, 25), 255, 6);
    }

    // 0 degrees is 3 o'clock and the angles grow clockwise, which the conic brush draws only at Angle 90.
    [AvaloniaFact]
    public void SvgImage_DrawsASweepGradientWhereSkiaModelDoes()
        => AssertSweepDrawsAsSkiaModel(0, 360, (32, 20, Red), (20, 32, Yellow), (8, 20, Green), (20, 8, Blue));

    // The brush always turns a whole circle: stops moved to a quarter and three quarters stand in for 90 and 270.
    [AvaloniaFact]
    public void SvgImage_DrawsAPartialSweepGradientWhereSkiaModelDoes()
        => AssertSweepDrawsAsSkiaModel(90, 270, (32, 20, Red), (20, 32, Red), (12, 28, Yellow), (8, 20, Green), (12, 12, Blue), (20, 8, Red));

    private static readonly Pixel Red = new(255, 0, 0);
    private static readonly Pixel Yellow = new(255, 255, 0);
    private static readonly Pixel Green = new(0, 255, 0);
    private static readonly Pixel Blue = new(0, 0, 255);

    private static void AssertSweepDrawsAsSkiaModel(int startAngle, int endAngle, params (int X, int Y, Pixel Colour)[] samples)
    {
        // Centred on a pixel's centre, so each sample lies exactly on an axis or a diagonal, and so on a stop.
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="40" height="40">
              <defs>
                <sweepGradient id="s" gradientUnits="userSpaceOnUse" cx="20.5" cy="20.5" startAngle="{startAngle}" endAngle="{endAngle}">
                  <stop offset="0" stop-color="#ff0000" />
                  <stop offset="0.25" stop-color="#ffff00" />
                  <stop offset="0.5" stop-color="#00ff00" />
                  <stop offset="0.75" stop-color="#0000ff" />
                  <stop offset="1" stop-color="#ff0000" />
                </sweepGradient>
              </defs>
              <rect width="40" height="40" fill="url(#s)" />
            </svg>
            """;

        using var bitmap = RenderSvg(svg);
        using var picture = new SkiaModel(new SKSvgSettings()).ToSKPicture(SvgSource.LoadPictureFromSvg(svg));
        using var expected = new SKBitmap(40, 40);
        using (var canvas = new SKCanvas(expected))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawPicture(picture);
        }

        foreach (var (x, y, colour) in samples)
        {
            var skia = expected.GetPixel(x, y);
            var drawn = new Pixel(skia.Red, skia.Green, skia.Blue);
            AssertColor(drawn, colour, 6);
            AssertColor(GetPixel(bitmap, x, y), drawn, 6);
        }
    }

    private static WriteableBitmap RenderSvg(string svg)
    {
        var source = SvgSource.LoadFromSvg(svg);
        Assert.NotNull(source.Picture);

        var image = new Image
        {
            Source = new SvgImage { Source = source },
            Width = source.Picture!.CullRect.Width,
            Height = source.Picture.CullRect.Height,
            Stretch = Stretch.None
        };

        var window = new Window
        {
            Width = source.Picture.CullRect.Width,
            Height = source.Picture.CullRect.Height,
            Background = Brushes.White,
            Content = image
        };

        window.Show();
        var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("No rendered frame was captured.");
        window.Close();
        return frame;
    }

    private static Pixel GetPixel(WriteableBitmap bitmap, int x, int y)
    {
        using var framebuffer = bitmap.Lock();
        var offset = y * framebuffer.RowBytes + x * framebuffer.Format.BitsPerPixel / 8;
        if (framebuffer.Format == PixelFormat.Bgra8888)
        {
            return new Pixel(
                Marshal.ReadByte(framebuffer.Address, offset + 2),
                Marshal.ReadByte(framebuffer.Address, offset + 1),
                Marshal.ReadByte(framebuffer.Address, offset));
        }

        Assert.Equal(PixelFormat.Rgba8888, framebuffer.Format);
        return new Pixel(
            Marshal.ReadByte(framebuffer.Address, offset),
            Marshal.ReadByte(framebuffer.Address, offset + 1),
            Marshal.ReadByte(framebuffer.Address, offset + 2));
    }

    private static void AssertColor(Pixel color, byte expected, byte tolerance)
        => AssertColor(color, new Pixel(expected, expected, expected), tolerance);

    private static void AssertColor(Pixel color, Pixel expected, byte tolerance)
    {
        Assert.InRange(color.Red, expected.Red - tolerance, expected.Red + tolerance);
        Assert.InRange(color.Green, expected.Green - tolerance, expected.Green + tolerance);
        Assert.InRange(color.Blue, expected.Blue - tolerance, expected.Blue + tolerance);
    }

    private readonly record struct Pixel(byte Red, byte Green, byte Blue);
}
