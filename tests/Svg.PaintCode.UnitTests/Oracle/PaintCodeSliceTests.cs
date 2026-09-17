// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    public static IEnumerable<object[]> Rows()
    {
        foreach (var folder in PaintCodeExpected.Folders())
        {
            var list = Path.Combine(folder, "slice.csv");

            if (!File.Exists(list))
            {
                continue;
            }

            foreach (var line in File.ReadAllLines(list).Skip(1).Where(line => line.Length > 0))
            {
                var fields = PaintCodeCsv.Fields(line);

                yield return new object[] { folder, fields[0], double.Parse(fields[1], CultureInfo.InvariantCulture) };
            }
        }
    }

    /// <summary>The canvases one document contributes, for the generator that writes their rasters.</summary>
    internal static IReadOnlyList<string> Slice(string folder)
    {
        var list = Path.Combine(folder, "slice.csv");

        return File.Exists(list)
            ? File.ReadAllLines(list).Skip(1).Where(line => line.Length > 0).Select(line => PaintCodeCsv.Fields(line)[0]).ToList()
            : new List<string>();
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Draws_What_PaintCode_Drew(string directory, string slug, double threshold)
    {
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
