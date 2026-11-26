using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Svg;
using SkiaSharp;
using Svg.Expressions;
using Svg.SceneGraph;
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
        => AssertSweepDrawsAsSkiaModel(AtCentre, Wheel, Canvas, (32, 20, Red), (20, 32, Yellow), (8, 20, Green), (20, 8, Blue));

    // The brush always turns a whole circle: stops moved to a quarter and three quarters stand in for 90 and 270.
    [AvaloniaFact]
    public void SvgImage_DrawsAPartialSweepGradientWhereSkiaModelDoes()
        => AssertSweepDrawsAsSkiaModel(AtCentre + " startAngle='90' endAngle='270'", Wheel, Canvas, (32, 20, Red), (20, 32, Red), (12, 28, Yellow), (8, 20, Green), (12, 12, Blue), (20, 8, Red));

    // Skia never reaches the stops' first quarter turn, so 3 o'clock is already a quarter of the way to blue.
    // Just above it lies 355 degrees, past endAngle, where the blue pads.
    [AvaloniaFact]
    public void SvgImage_DrawsASweepReachingOutsideTheTurnWhereSkiaModelDoes()
        => AssertSweepDrawsAsSkiaModel(
            AtCentre + " startAngle='-90' endAngle='270'",
            RedToBlue,
            Canvas,
            (32, 21, new(188, 0, 67)), (32, 32, new(159, 0, 96)), (20, 32, new(128, 0, 128)), (8, 20, new(64, 0, 191)),
            (20, 8, Blue), (32, 8, Blue), (32, 19, Blue), (27, 33, null), (9, 14, null));

    // Below the 3 o'clock ray the red pads up to startAngle, and above it the blue pads back from endAngle.
    [AvaloniaFact]
    public void SvgImage_PadsAPartialSweepWithItsEndColoursWhereSkiaModelDoes()
        => AssertSweepDrawsAsSkiaModel(
            AtCentre + " startAngle='45' endAngle='225'",
            RedToBlue,
            Canvas,
            (32, 21, Red), (32, 26, Red), (20, 32, new(191, 0, 64)), (8, 32, new(128, 0, 128)), (8, 20, new(64, 0, 191)),
            (20, 8, Blue), (32, 8, Blue), (32, 19, Blue), (14, 32, null), (9, 25, null));

    [AvaloniaFact]
    public void SvgImage_TurnsASweepByItsGradientTransformWhereSkiaModelDoes()
        => AssertSweepDrawsAsSkiaModel(
            AtCentre + " gradientTransform='rotate(-90 20.5 20.5)'",
            Wheel,
            Canvas,
            (20, 8, Red), (32, 20, Yellow), (20, 32, Green), (8, 20, Blue), (29, 11, null), (30, 27, null), (12, 31, null));

    // The box's matrix is the brush's transform, which has to apply about (0,0) rather than about the box's corner.
    [AvaloniaFact]
    public void SvgImage_DrawsABoundingBoxSweepOnAShapeAwayFromTheOriginWhereSkiaModelDoes()
        => AssertSweepDrawsAsSkiaModel(
            "",
            Wheel,
            "<rect x='14' y='6' width='21' height='21' fill='url(#s)' />",
            (32, 16, Red), (24, 24, Yellow), (16, 16, Green), (24, 8, Blue), (29, 19, null), (18, 11, null));

    // Bound, a gradient's lists reach the brush as the stops they come to, spaced evenly without offsets.
    [AvaloniaTheory]
    [InlineData("linearGradient", " e:stop-offsets='{{ positions }}'")]
    [InlineData("linearGradient", "")]
    [InlineData("sweepGradient", " e:stop-offsets='{{ positions }}'")]
    [InlineData("sweepGradient", "")]
    public void SvgImage_DrawsAGradientGivenItsStopsAsListsWhereSkiaModelDoes(string element, string offsets)
    {
        var svg = $$$"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="40" height="40">
              <defs>
                <e:code><e:param name="colors" type="color[]" /><e:param name="positions" type="number[]" /></e:code>
                <{{{element}}} id="s" gradientUnits="userSpaceOnUse" x1="0" x2="40" cx="20.5" cy="20.5" e:stop-colors="{{ colors }}"{{{offsets}}}>
                  <stop offset="0" stop-color="#ffffff" /><stop offset="1" stop-color="#000000" />
                </{{{element}}}>
              </defs>
              <rect width="40" height="40" fill="url(#s)" />
            </svg>
            """;
        var bound = SvgSceneExpressionEvaluator.Evaluate(
            SvgSource.LoadPictureFromSvg(svg),
            SvgExpressionDeclarations.Parse(svg),
            new Dictionary<string, ExprValue>
            {
                ["colors"] = ExprValue.ColorList(new[] { ExprValue.Color(255, 0, 0, 255), ExprValue.Color(0, 255, 0, 255), ExprValue.Color(0, 0, 255, 255) }),
                ["positions"] = ExprValue.NumberList(new[] { 0f, 0.2f, 1f })
            });

        AssertDrawsAsSkiaModel(
            bound!,
            (1, 20, null), (6, 20, null), (14, 20, null), (26, 20, null), (32, 20, null), (20, 32, null), (8, 28, null), (20, 8, null), (30, 10, null));
    }

    // Before the brushes turned their matrix about (0,0), this shape sat wholly past the red end and drew solid blue.
    [AvaloniaFact]
    public void SvgImage_DrawsABoundingBoxLinearGradientOnAShapeAwayFromTheOriginWhereSkiaModelDoes()
        => AssertGradientDrawsAsSkiaModel(
            $"<linearGradient id='s'>{RedToBlue}</linearGradient>",
            "<rect x='14' y='6' width='21' height='21' fill='url(#s)' />",
            (15, 16, null), (20, 16, null), (24, 16, null), (29, 16, null), (34, 16, null));

    [AvaloniaFact]
    public void SvgImage_TurnsABoundingBoxLinearGradientByItsGradientTransformWhereSkiaModelDoes()
        => AssertGradientDrawsAsSkiaModel(
            $"<linearGradient id='s' gradientTransform='rotate(45 0.5 0.5)'>{RedToBlue}</linearGradient>",
            "<rect x='14' y='6' width='21' height='21' fill='url(#s)' />",
            (15, 7, null), (24, 16, null), (34, 26, null), (34, 7, null), (15, 26, null), (20, 10, null));

    // The box is not square, so a radius taken relative to the shape's width would draw an ellipse within the ellipse.
    [AvaloniaFact]
    public void SvgImage_DrawsABoundingBoxRadialGradientOnAShapeAwayFromTheOriginWhereSkiaModelDoes()
        => AssertGradientDrawsAsSkiaModel(
            $"<radialGradient id='s'>{RedToBlue}</radialGradient>",
            "<rect x='6' y='10' width='28' height='14' fill='url(#s)' />",
            (20, 17, null), (26, 17, null), (32, 17, null), (20, 20, null), (20, 23, null), (8, 11, null), (13, 21, null));

    [AvaloniaFact]
    public void SvgImage_DrawsAUserSpaceRadialGradientOnAShapeThatIsNotSquareWhereSkiaModelDoes()
        => AssertGradientDrawsAsSkiaModel(
            $"<radialGradient id='s' gradientUnits='userSpaceOnUse' cx='20' cy='20' r='15'>{RedToBlue}</radialGradient>",
            "<rect x='0' y='10' width='40' height='20' fill='url(#s)' />",
            (20, 20, null), (28, 20, null), (34, 20, null), (20, 25, null), (20, 29, null), (10, 14, null));

    // A focus off the centre makes a two-point conical shader, whose radius the brush once fixed at half the shape.
    [AvaloniaFact]
    public void SvgImage_DrawsAFocalRadialGradientOnAShapeAwayFromTheOriginWhereSkiaModelDoes()
        => AssertGradientDrawsAsSkiaModel(
            $"<radialGradient id='s' fx='0.3' fy='0.35' r='0.4'>{RedToBlue}</radialGradient>",
            "<rect x='12' y='8' width='24' height='24' fill='url(#s)' />",
            (19, 16, null), (24, 20, null), (30, 20, null), (24, 27, null), (33, 29, null), (14, 10, null), (16, 22, null));

    // Centred on a pixel's centre, so a sample on an axis or a diagonal lies exactly on its angle.
    private const string AtCentre = "gradientUnits='userSpaceOnUse' cx='20.5' cy='20.5'";
    private const string Canvas = "<rect width='40' height='40' fill='url(#s)' />";
    private const string RedToBlue = "<stop offset='0' stop-color='#ff0000' /><stop offset='1' stop-color='#0000ff' />";
    private const string Wheel = """
        <stop offset="0" stop-color="#ff0000" />
        <stop offset="0.25" stop-color="#ffff00" />
        <stop offset="0.5" stop-color="#00ff00" />
        <stop offset="0.75" stop-color="#0000ff" />
        <stop offset="1" stop-color="#ff0000" />
        """;

    private static readonly Pixel Red = new(255, 0, 0);
    private static readonly Pixel Yellow = new(255, 255, 0);
    private static readonly Pixel Green = new(0, 255, 0);
    private static readonly Pixel Blue = new(0, 0, 255);

    private static void AssertSweepDrawsAsSkiaModel(string attributes, string stops, string shape, params (int X, int Y, Pixel? Colour)[] samples)
        => AssertGradientDrawsAsSkiaModel($"<sweepGradient id='s' {attributes}>{stops}</sweepGradient>", shape, samples);

    // A sample with no colour is held to SkiaModel's alone, which is how the rows check the blend between stops.
    private static void AssertGradientDrawsAsSkiaModel(string gradient, string shape, params (int X, int Y, Pixel? Colour)[] samples)
    {
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="40" height="40">
              <defs>
                {gradient}
              </defs>
              {shape}
            </svg>
            """;

        AssertDrawsAsSkiaModel(SvgSource.LoadPictureFromSvg(svg)!, samples);
    }

    private static void AssertDrawsAsSkiaModel(ShimSkiaSharp.SKPicture model, params (int X, int Y, Pixel? Colour)[] samples)
    {
        using var bitmap = RenderPicture(model);
        using var picture = new SkiaModel(new SKSvgSettings()).ToSKPicture(model);
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
            if (colour is { } named)
            {
                AssertColor(drawn, named, 6);
            }

            AssertColor(GetPixel(bitmap, x, y), drawn, 6);
        }
    }

    private static WriteableBitmap RenderSvg(string svg)
        => RenderPicture(SvgSource.LoadPictureFromSvg(svg)!);

    private static WriteableBitmap RenderPicture(ShimSkiaSharp.SKPicture picture)
    {
        var source = new SvgSource { Picture = picture };

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
