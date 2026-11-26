using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ShimSkiaSharp;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class AvaloniaModelExtensionsTests
{
    [Fact]
    public void ToBrush_Maps_SweepGradient_To_Conic_Brush_On_Skias_Axis()
    {
        var positions = new[] { 0f, 0.5f, 1f };
        var shader = new SweepGradientShader(
            new SKPoint(12f, 10f),
            90f,
            270f,
            new[] { new SKColorF(1f, 0f, 0f, 1f), new SKColorF(0f, 1f, 0f, 1f), new SKColorF(0f, 0f, 1f, 1f) },
            SKColorSpace.Srgb,
            positions,
            SKShaderTileMode.Mirror,
            new SKMatrix { ScaleX = 2f, SkewX = 0.5f, TransX = 3f, SkewY = 0.25f, ScaleY = 1f, TransY = 4f, Persp2 = 1f });

        var brush = Assert.IsType<ImmutableConicGradientBrush>(shader.ToBrush(SKRect.Create(0f, 0f, 24f, 24f)));

        Assert.Equal(90d, brush.Angle);
        Assert.Equal(new RelativePoint(12, 10, RelativeUnit.Absolute), brush.Center);
        Assert.Equal(new RelativePoint(0, 0, RelativeUnit.Absolute), brush.TransformOrigin);
        Assert.Equal(new Matrix(2, 0.25, 0.5, 1, 3, 4), brush.Transform!.Value);
        Assert.Equal(GradientSpreadMethod.Pad, brush.SpreadMethod);
        Assert.Equal(new[] { 0.25, 0.5, 0.75 }, brush.GradientStops.Select(stop => stop.Offset));
        Assert.Same(positions, shader.ColorPos);
        Assert.Equal(new[] { 0f, 0.5f, 1f }, positions);
    }

    // The bounds are a text run's empty box and a shape that is not square: neither may scale the brush.
    [Fact]
    public void ToBrush_Keeps_A_Linear_Gradient_In_Skias_Coordinates()
    {
        var shader = new LinearGradientShader(new SKPoint(0f, 0f), new SKPoint(1f, 0f), TwoColours, SKColorSpace.Srgb, TwoPositions, SKShaderTileMode.Clamp, BoxMatrix);

        var brush = Assert.IsType<ImmutableLinearGradientBrush>(shader.ToBrush(SKRect.Create(14f, 6f, 21f, 9f)));

        Assert.Equal(new RelativePoint(0, 0, RelativeUnit.Absolute), brush.TransformOrigin);
        Assert.Equal(new RelativePoint(0, 0, RelativeUnit.Absolute), brush.StartPoint);
        Assert.Equal(new RelativePoint(1, 0, RelativeUnit.Absolute), brush.EndPoint);
        Assert.Equal(new Matrix(21, 0, 0, 9, 14, 6), brush.Transform!.Value);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(21f)]
    public void ToBrush_Keeps_A_Radial_Gradient_In_Skias_Coordinates(float width)
    {
        var shader = new RadialGradientShader(new SKPoint(0.5f, 0.5f), 0.4f, TwoColours, SKColorSpace.Srgb, TwoPositions, SKShaderTileMode.Clamp, BoxMatrix);

        var brush = Assert.IsType<ImmutableRadialGradientBrush>(shader.ToBrush(SKRect.Create(14f, 6f, width, 9f)));

        Assert.Equal(new RelativePoint(0, 0, RelativeUnit.Absolute), brush.TransformOrigin);
        Assert.Equal(new RelativePoint(0.5, 0.5, RelativeUnit.Absolute), brush.Center);
        Assert.Equal(brush.Center, brush.GradientOrigin);
        Assert.Equal(new RelativeScalar(0.4f, RelativeUnit.Absolute), brush.RadiusX);
        Assert.Equal(brush.RadiusX, brush.RadiusY);
    }

    [Fact]
    public void ToBrush_Keeps_A_Focal_Radial_Gradient_In_Skias_Coordinates()
    {
        var shader = new TwoPointConicalGradientShader(new SKPoint(0.3f, 0.35f), 0f, new SKPoint(0.5f, 0.5f), 0.4f, TwoColours, SKColorSpace.Srgb, TwoPositions, SKShaderTileMode.Clamp, BoxMatrix);

        var brush = Assert.IsType<ImmutableRadialGradientBrush>(shader.ToBrush(SKRect.Create(14f, 6f, 21f, 9f)));

        Assert.Equal(new RelativePoint(0, 0, RelativeUnit.Absolute), brush.TransformOrigin);
        Assert.Equal(new RelativePoint(0.5, 0.5, RelativeUnit.Absolute), brush.Center);
        Assert.Equal(new RelativePoint(0.3f, 0.35f, RelativeUnit.Absolute), brush.GradientOrigin);
        Assert.Equal(new RelativeScalar(0.4f, RelativeUnit.Absolute), brush.RadiusX);
        Assert.Equal(brush.RadiusX, brush.RadiusY);
    }

    private static readonly SKColorF[] TwoColours = { new(1f, 0f, 0f, 1f), new(0f, 0f, 1f, 1f) };
    private static readonly float[] TwoPositions = { 0f, 1f };
    private static readonly SKMatrix BoxMatrix = new() { ScaleX = 21f, ScaleY = 9f, TransX = 14f, TransY = 6f, Persp2 = 1f };
}
