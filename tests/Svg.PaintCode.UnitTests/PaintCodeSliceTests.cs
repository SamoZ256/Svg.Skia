// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Concurrent;
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

namespace Svg.PaintCode.UnitTests;

/// <summary>
/// Freely licensed PaintCode documents, imported afresh and drawn against the rasters PaintCode's own
/// code drew for them.
/// </summary>
/// <remarks>
/// Everything else here checks the import's structure; this follows a document from the archive to
/// the pixel, on every platform. The full comparison, every setting of every canvas against
/// PaintCode's generated code, lives in a separate repository beside documents that cannot be
/// committed here. That is what writes these rasters, and a document is only written here when it
/// carries a NOTICE.md saying it may be.
///
/// SampleStyleKit, the first, exposed four faults the full comparison's own document never had:
/// colours saved in an older form read as black, a highlight and an alias derived as their parent,
/// parameters named unlike PaintCode's, and a nearly whole arc drawing nothing.
/// </remarks>
public class PaintCodeSliceTests
{
    private static readonly string s_root = Path.Combine(AppContext.BaseDirectory, "TestAssets", "PaintCode");

    private static readonly ConcurrentDictionary<string, Lazy<PaintCodeImportResult>> s_imported = new(StringComparer.Ordinal);

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
        foreach (var folder in Directory.EnumerateDirectories(s_root).OrderBy(p => p, StringComparer.Ordinal))
        {
            foreach (var line in File.ReadAllLines(Path.Combine(folder, "slice.csv")).Skip(1).Where(line => line.Length > 0))
            {
                var fields = PaintCodeCsv.Fields(line);

                yield return new object[] { Path.GetFileName(folder), fields[0], double.Parse(fields[1], CultureInfo.InvariantCulture) };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Draws_What_PaintCode_Drew(string document, string slug, double threshold)
    {
        var imported = Imported(document);

        // A canvas the import could not finish draws something short of it; say why, not only how far.
        Assert.DoesNotContain(imported.Notes, note => note.Canvas == slug && note.Severity is PaintCodeImportSeverity.Missing);

        using var svg = new SKSvg();

        Assert.NotNull(svg.Load(imported.Files.Single(file => Path.GetFileNameWithoutExtension(file) == slug)));

        // Bound rather than merely loaded, so every expression the drawing carries is type checked
        // and evaluated at its default on the way to the picture.
        var picture = svg.SetExpressionValues(new Dictionary<string, Svg.Expressions.ExprValue>()) ?? svg.Picture;

        Assert.NotNull(picture);

        using var expected = Image.Load<Rgba32>(Path.Combine(s_root, document, slug + ".png"));
        using var actual = Raster(picture!, expected.Width, expected.Height);

        var difference = ImageHelper.CompareImages(actual, expected);

        Assert.True(
            difference <= threshold,
            $"{slug}: {difference.ToString("F5", CultureInfo.InvariantCulture)} against PaintCode, allowed {threshold.ToString("F3", CultureInfo.InvariantCulture)}.");
    }

    /// <summary>The document imported once, into the build output, where the next run overwrites it.</summary>
    private static PaintCodeImportResult Imported(string document)
        => s_imported.GetOrAdd(document, name => new Lazy<PaintCodeImportResult>(() =>
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Imported", name);

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return PaintCodeImport.Run(Path.Combine(s_root, name, name + ".pcvd"), new PaintCodeImportOptions(directory));
        })).Value;

    internal static Image<Rgba32> Raster(SKPicture picture, int width, int height)
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
