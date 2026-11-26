using System;
using System.Linq;
using ShimSkiaSharp;
using ShimSkiaSharp.Editing;
using Xunit;

namespace Svg.Skia.UnitTests;

public class SvgPaintServerRenderingTests
{
    [Fact]
    public void RetainedScene_StopCurrentColorDefaultsToBlack()
    {
        const string svgMarkup = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="80">
              <defs>
                <linearGradient id="paint" x1="0" y1="0" x2="10" y2="0" gradientUnits="userSpaceOnUse">
                  <stop offset="0" stop-color="yellow" />
                  <stop offset="1" stop-color="currentColor" />
                </linearGradient>
              </defs>
              <rect id="target" x="0" y="0" width="50" height="40" fill="url(#paint)" />
            </svg>
            """;

        using var svg = new SKSvg();
        svg.FromSvg(svgMarkup);

        var shader = GetFillShader<LinearGradientShader>(svg, "target");

        Assert.NotNull(shader.Colors);
        Assert.Equal(2, shader.Colors!.Length);
        Assert.Equal(0f, shader.Colors[1].Red);
        Assert.Equal(0f, shader.Colors[1].Green);
        Assert.Equal(0f, shader.Colors[1].Blue);
        Assert.Equal(1f, shader.Colors[1].Alpha);
    }

    [Fact]
    public void RetainedScene_RadialGradientCorrectsFocalPointOutsideCircle()
    {
        const string svgMarkup = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <defs>
                <radialGradient id="paint" gradientUnits="userSpaceOnUse" cx="10" cy="10" r="75" fx="83.33" fy="75">
                  <stop offset="0" stop-color="white" />
                  <stop offset="1" stop-color="black" />
                </radialGradient>
              </defs>
              <rect id="target" width="100" height="100" fill="url(#paint)" />
            </svg>
            """;

        using var svg = new SKSvg();
        svg.FromSvg(svgMarkup);

        var shader = GetFillShader<TwoPointConicalGradientShader>(svg, "target");
        var dx = shader.Start.X - shader.End.X;
        var dy = shader.Start.Y - shader.End.Y;
        var correctedDistance = MathF.Sqrt((dx * dx) + (dy * dy));

        Assert.InRange(correctedDistance, 74.99f, 75.01f);
        Assert.True(shader.Start.X < 83.33f);
        Assert.True(shader.Start.Y < 75f);
    }

    [Fact]
    public void RetainedScene_SweepGradientRecordsItsCentreAnglesAndTransform()
    {
        const string svgMarkup = """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <defs>
                <sweepGradient id="paint" gradientUnits="userSpaceOnUse" cx="12" cy="8" startAngle="45" endAngle="270"
                               spreadMethod="reflect" gradientTransform="rotate(-90 12 8)">
                  <stop offset="0" stop-color="red" />
                  <stop offset="1" stop-color="blue" />
                </sweepGradient>
              </defs>
              <rect id="target" width="24" height="24" fill="url(#paint)" />
            </svg>
            """;

        using var svg = new SKSvg();
        svg.FromSvg(svgMarkup);

        var shader = GetFillShader<SweepGradientShader>(svg, "target");
        var turned = shader.LocalMatrix!.Value.MapPoint(new SKPoint(13f, 8f));

        Assert.Equal(new SKPoint(12f, 8f), shader.Center);
        Assert.Equal(45f, shader.StartAngle);
        Assert.Equal(270f, shader.EndAngle);
        Assert.Equal(SKShaderTileMode.Mirror, shader.Mode);
        Assert.Equal(2, shader.Colors!.Length);
        Assert.Equal(12f, turned.X, 3);
        Assert.Equal(7f, turned.Y, 3);
    }

    [Fact]
    public void RetainedScene_SweepGradientInBoundingBoxUnitsUsesTheBoxMatrix()
    {
        const string svgMarkup = """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <defs>
                <sweepGradient id="paint" cx="0.25">
                  <stop offset="0" stop-color="red" />
                  <stop offset="1" stop-color="blue" />
                </sweepGradient>
              </defs>
              <rect id="target" x="4" y="2" width="20" height="8" fill="url(#paint)" />
            </svg>
            """;

        using var svg = new SKSvg();
        svg.FromSvg(svgMarkup);

        var shader = GetFillShader<SweepGradientShader>(svg, "target");

        Assert.Equal(new SKPoint(0.25f, 0.5f), shader.Center);
        Assert.Equal(new SKMatrix { ScaleX = 20f, ScaleY = 8f, TransX = 4f, TransY = 2f, Persp2 = 1f }, shader.LocalMatrix);
    }

    [Fact]
    public void RetainedScene_SweepGradientTakesStopsThroughHrefButNotARadialsCentre()
    {
        const string svgMarkup = """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="16">
              <defs>
                <radialGradient id="base" gradientUnits="userSpaceOnUse" cx="3" cy="5" spreadMethod="repeat">
                  <stop offset="0" stop-color="red" />
                  <stop offset="0.5" stop-color="lime" />
                  <stop offset="1" stop-color="blue" />
                </radialGradient>
                <sweepGradient id="paint" href="#base" />
              </defs>
              <rect id="target" width="24" height="16" fill="url(#paint)" />
            </svg>
            """;

        using var svg = new SKSvg();
        svg.FromSvg(svgMarkup);

        var shader = GetFillShader<SweepGradientShader>(svg, "target");

        Assert.Equal(3, shader.Colors!.Length);
        Assert.Equal(SKShaderTileMode.Repeat, shader.Mode);
        Assert.Equal(new SKPoint(12f, 8f), shader.Center);
    }

    [Fact]
    public void RetainedScene_SweepGradientTakesItsCentreAndAnglesThroughHrefFromASweep()
    {
        const string svgMarkup = """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="16">
              <defs>
                <sweepGradient id="base" gradientUnits="userSpaceOnUse" cx="3" cy="5" startAngle="30" endAngle="200">
                  <stop offset="0" stop-color="red" />
                  <stop offset="1" stop-color="blue" />
                </sweepGradient>
                <sweepGradient id="paint" href="#base" endAngle="300" />
              </defs>
              <rect id="target" width="24" height="16" fill="url(#paint)" />
            </svg>
            """;

        using var svg = new SKSvg();
        svg.FromSvg(svgMarkup);

        var shader = GetFillShader<SweepGradientShader>(svg, "target");

        Assert.Equal(new SKPoint(3f, 5f), shader.Center);
        Assert.Equal(30f, shader.StartAngle);
        Assert.Equal(300f, shader.EndAngle);
    }

    [Theory]
    [InlineData("startAngle=\"270\" endAngle=\"90\"")]
    [InlineData("endAngle=\"NaN\"")]
    [InlineData("startAngle=\"-Infinity\"")]
    public void RetainedScene_SweepGradientWithReversedOrNonFiniteAnglesPaintsItsFallback(string angles)
    {
        var svgMarkup = $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <defs>
                <sweepGradient id="paint" {angles}>
                  <stop offset="0" stop-color="red" />
                  <stop offset="1" stop-color="blue" />
                </sweepGradient>
              </defs>
              <rect id="target" width="24" height="24" fill="url(#paint) #00ff00" />
            </svg>
            """;

        using var svg = new SKSvg();
        svg.FromSvg(svgMarkup);

        var paint = GetPaint(svg, "target", SKPaintStyle.Fill);

        Assert.Null(paint.Shader);
        Assert.Equal(new SKColor(0, 255, 0, 255), paint.Color);
    }

    [Fact]
    public void RetainedScene_SweepGradientOnABoxWithNoHeightPaintsItsFallback()
    {
        const string svgMarkup = """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <defs>
                <sweepGradient id="paint">
                  <stop offset="0" stop-color="red" />
                  <stop offset="1" stop-color="blue" />
                </sweepGradient>
              </defs>
              <path id="target" d="M 2 12 H 22" stroke="url(#paint) #00ff00" stroke-width="2" />
            </svg>
            """;

        using var svg = new SKSvg();
        svg.FromSvg(svgMarkup);

        var paint = GetPaint(svg, "target", SKPaintStyle.Stroke);

        Assert.Null(paint.Shader);
        Assert.Equal(new SKColor(0, 255, 0, 255), paint.Color);
    }

    [Fact]
    public void RetainedScene_SweepGradientWithOneStopIsItsColour()
    {
        const string svgMarkup = """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <defs>
                <sweepGradient id="paint">
                  <stop offset="0.5" stop-color="red" />
                </sweepGradient>
              </defs>
              <rect id="target" width="24" height="24" fill="url(#paint)" />
            </svg>
            """;

        using var svg = new SKSvg();
        svg.FromSvg(svgMarkup);

        var shader = GetFillShader<ColorShader>(svg, "target");

        Assert.Equal(new SKColor(255, 0, 0, 255), shader.Color);
    }

    private static TShader GetFillShader<TShader>(SKSvg svg, string sourceElementId)
        where TShader : SKShader
        => Assert.IsType<TShader>(GetPaint(svg, sourceElementId, SKPaintStyle.Fill).Shader);

    private static SKPaint GetPaint(SKSvg svg, string sourceElementId, SKPaintStyle style)
    {
        var command = svg.Model!
            .FindCommandsBySourceElementId<DrawPathCanvasCommand>(sourceElementId)
            .First(candidate => candidate.Paint?.Style == style);

        return command.Paint!;
    }
}
