using System;
using System.Collections.Generic;
using Xunit;
using ShimPath = ShimSkiaSharp.SKPath;
using ShimPoint = ShimSkiaSharp.SKPoint;
using ShimRect = ShimSkiaSharp.SKRect;

namespace Svg.Skia.UnitTests;

/// <summary>
/// The shim's path bounds against Skia's own tight bounds for the same path: what objectBoundingBox
/// units, transform-origin, hit testing and CommandPlan all read.
/// </summary>
public class PathBoundsTests
{
    public static IEnumerable<object[]> Arcs()
    {
        // Seeded, so a failure names the same arc every run. Radii run from far too small, which
        // SVG scales up to reach the end point, to large, and include negative ones, taken as absolute.
        var random = new Random(133);
        for (var i = 0; i < 200; i++)
        {
            yield return new object[]
            {
                i,
                Next(-50, 50), Next(-50, 50), Next(-50, 50), Next(-50, 50),
                Next(-60, 60), Next(-60, 60), Next(-180, 360),
                random.Next(2) == 0, random.Next(2) == 0
            };
        }

        float Next(int min, int max) => (float)Math.Round(min + random.NextDouble() * (max - min), 2);
    }

    [Theory]
    [MemberData(nameof(Arcs))]
    public void An_Arc_Is_Bounded_Where_Skia_Draws_It(int seed, float x0, float y0, float x1, float y1, float rx, float ry, float rotation, bool large, bool clockwise)
    {
        var path = new ShimPath();
        path.MoveTo(x0, y0);
        path.ArcTo(
            rx,
            ry,
            rotation,
            large ? ShimSkiaSharp.SKPathArcSize.Large : ShimSkiaSharp.SKPathArcSize.Small,
            clockwise ? ShimSkiaSharp.SKPathDirection.Clockwise : ShimSkiaSharp.SKPathDirection.CounterClockwise,
            x1,
            y1);

        AssertSameBounds($"arc {seed}", path);
    }

    [Fact]
    public void A_Segment_After_A_Closed_Contour_Starts_Where_Skia_Starts_It()
    {
        var rect = new ShimRect(10, 20, 50, 80);
        var shapes = new Dictionary<string, Action<ShimPath>>
        {
            ["close"] = path =>
            {
                path.MoveTo(10, 20);
                path.LineTo(50, 20);
                path.LineTo(50, 80);
                path.Close();
            },
            ["rect"] = path => path.AddRect(rect),
            ["round rect"] = path => path.Add(new ShimSkiaSharp.AddRoundRectPathCommand(rect, 5, 8)),
            ["round rect with radii too large"] = path => path.Add(new ShimSkiaSharp.AddRoundRectPathCommand(rect, 40, 50)),
            ["oval"] = path => path.AddOval(rect),
            ["circle"] = path => path.Add(new ShimSkiaSharp.AddCirclePathCommand(30, 50, 20)),
            ["closed poly"] = path => path.AddPoly(new[] { new ShimPoint(10, 20), new ShimPoint(50, 20), new ShimPoint(50, 80) }, true),
            ["open poly"] = path => path.AddPoly(new[] { new ShimPoint(10, 20), new ShimPoint(50, 20), new ShimPoint(50, 80) }, false)
        };

        foreach (var (name, add) in shapes)
        {
            // An arc to a point beside the shape bulges by an amount that depends on where it starts.
            var path = new ShimPath();
            add(path);
            path.ArcTo(30, 30, 0, ShimSkiaSharp.SKPathArcSize.Small, ShimSkiaSharp.SKPathDirection.Clockwise, 120, 60);

            AssertSameBounds(name, path);
        }
    }

    private static void AssertSameBounds(string name, ShimPath path)
    {
        using var skia = new SkiaModel(new SKSvgSettings()).ToSKPath(path);
        var expected = skia!.TightBounds;
        var actual = path.Bounds;

        // Skia draws an arc as conics, which trace the ellipse exactly; what is left is float rounding.
        var tolerance = 1e-3f * Math.Max(1f, Math.Max(expected.Width, expected.Height));
        Assert.True(
            Math.Abs(actual.Left - expected.Left) <= tolerance &&
            Math.Abs(actual.Top - expected.Top) <= tolerance &&
            Math.Abs(actual.Right - expected.Right) <= tolerance &&
            Math.Abs(actual.Bottom - expected.Bottom) <= tolerance,
            $"{name}: shim {actual.Left}, {actual.Top}, {actual.Right}, {actual.Bottom}; Skia {expected.Left}, {expected.Top}, {expected.Right}, {expected.Bottom}");
    }
}
