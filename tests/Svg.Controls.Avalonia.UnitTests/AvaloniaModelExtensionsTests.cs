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
}
