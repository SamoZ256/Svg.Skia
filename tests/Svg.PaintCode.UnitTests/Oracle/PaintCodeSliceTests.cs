// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;
using Svg.Skia;
using Svg.Skia.UnitTests.Common;
using Xunit;

namespace Svg.PaintCode.UnitTests.Oracle;

/// <summary>
/// A dozen converted drawings against the raster PaintCode's own code produced for them.
/// </summary>
/// <remarks>
/// The rest of the comparison needs a 16 MB document and 6.7 MB of generated code, neither of which
/// belongs in this repository, so it only runs where those are. These twelve carry their answer with
/// them and run everywhere — which matters because what breaks them is not usually this package. The
/// drawings lean on clips, symbols, nested group anchors, whole ovals, stars and gradients, so a
/// change in <c>Svg.SceneGraph</c>, <c>SkiaModel</c> or <c>Svg.Expressions</c> that moves any of
/// those lands here rather than waiting for somebody to run the full suite.
///
/// Each was chosen for a fault the conversion actually had: stars scaled by a percentage read as a
/// fraction, a house drawn outside its box, a symbol placed at its anchor rather than its box corner,
/// a nested group's anchor dropped, a whole oval read as a zero sweep, a symbol's constants ignored.
///
/// Rewrite them with <see cref="PaintCodeSliceAssets"/>, never by hand.
/// </remarks>
public class PaintCodeSliceTests
{
    /// <summary>
    /// Thresholds are per drawing, in <see cref="ImageHelper"/>'s metric.
    /// </summary>
    /// <remarks>
    /// Generous next to what each measures on the machine that wrote the rasters, because this is
    /// the one part of the comparison that runs on three operating systems and Skia does not
    /// antialias identically on all of them. Tight enough that a shape moving fails.
    /// </remarks>
    public static IEnumerable<object[]> Rows() => new[]
    {
        Row("symbol-overlay-error", 0.02),
        Row("symbol-notstarred", 0.02),
        Row("symbol-starred", 0.02),
        Row("starofdavid-state", 0.02),
        Row("battery-starting", 0.02),
        Row("symbol-forceupstart", 0.02),
        Row("ventilation", 0.02),
        Row("symbol-housewithventilation", 0.02),
        Row("circle-housewithventilation", 0.02),
        Row("circle-heatpump", 0.02),
        Row("10colors", 0.02),

        // The nested group anchor, which is the fault this one caught. It sits further out than the
        // others at its own defaults, and is pinned here rather than left out for being awkward.
        Row("symbol-daikin", 0.03),

        // Arcs. Nothing else here contains one, and an arc drawn the wrong way round was worth 36
        // canvases before it was found: a wedge closed through the middle, and a stroked arc left
        // open.
        Row("symbol-saturation-level", 0.02),
        Row("convector", 0.02),

        // A symbol instance that pins a number driving a transform — a tank a twentieth full, which
        // drew full while the offset was measured against the caller instead of the canvas.
        Row("tank-empty", 0.02),

        // A gradient laid by dragging its two ends rather than by turning a dial, which was being
        // read off the angle beside them and drawn top to bottom.
        Row("symbol-grafana", 0.04),

        // A library colour desaturated and then shadowed before it is given an alpha — two
        // operations that were passing the parent's own hue straight through.
        Row("ok-state", 0.02),

        // A radial gradient, which was being laid over the shape's box rather than between the two
        // circles PaintCode gives it.
        Row("scene-on", 0.05)
    };

    internal static IReadOnlyList<string> Slice { get; } = new[]
    {
        "symbol-overlay-error", "symbol-notstarred", "symbol-starred", "starofdavid-state",
        "battery-starting", "symbol-forceupstart", "ventilation", "symbol-housewithventilation",
        "circle-housewithventilation", "circle-heatpump", "10colors", "symbol-daikin",
        "symbol-saturation-level", "convector", "tank-empty", "symbol-grafana", "ok-state",
        "scene-on"
    };

    private static object[] Row(string slug, double threshold) => new object[] { slug, threshold };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Draws_What_PaintCode_Drew(string slug, double threshold)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "TestAssets", "Oracle");

        using var svg = new SKSvg();

        Assert.NotNull(svg.Load(Path.Combine(directory, slug + ".svg")));

        // Bound rather than merely loaded, so every expression the drawing carries is type checked
        // and evaluated at its default on the way to the picture.
        var picture = svg.SetExpressionValues(new Dictionary<string, Svg.Expressions.ExprValue>());

        Assert.NotNull(picture);

        using var expected = Image.Load<Rgba32>(Path.Combine(directory, slug + ".png"));
        using var actual = Raster(picture!, expected.Width, expected.Height);

        var difference = ImageHelper.CompareImages(actual, expected);

        Assert.True(
            difference <= threshold,
            $"{slug}: {difference.ToString("F5", CultureInfo.InvariantCulture)} against PaintCode, allowed {threshold.ToString("F3", CultureInfo.InvariantCulture)}.");
    }

    private static Image<Rgba32> Raster(SKPicture picture, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);

        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(width / picture.CullRect.Width, height / picture.CullRect.Height);
            canvas.DrawPicture(picture);
        }

        var pixels = bitmap.Pixels;
        var image = new Image<Rgba32>(width, height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = pixels[(y * width) + x];

                image[x, y] = new Rgba32(pixel.Red, pixel.Green, pixel.Blue, pixel.Alpha);
            }
        }

        return image;
    }
}
