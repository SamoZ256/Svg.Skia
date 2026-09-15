#if PAINTCODE_ORACLE
using System;
using System.IO;
using System.Linq;
using SkiaSharp;
using Svg.Expressions;
using Svg.Skia;
using Xunit;
using Xunit.Abstractions;

namespace Svg.PaintCode.UnitTests.Oracle;

/// <summary>
/// Writes the handful of drawings and PaintCode rasters that <see cref="PaintCodeSliceTests"/>
/// compares everywhere, including where neither the document nor PaintCode's code exists.
/// </summary>
/// <remarks>
/// Behind its own switch because it overwrites committed files: running it is a deliberate act, and
/// the diff it produces is the thing to read before committing.
/// </remarks>
[Collection(PaintCodeOracleCollection.Name)]
public class PaintCodeSliceAssets
{
    private readonly ITestOutputHelper _output;

    public PaintCodeSliceAssets(ITestOutputHelper output) => _output = output;

    [SampleFact]
    public void Write()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SVG_PAINTCODE_WRITE_SLICE")))
        {
            _output.WriteLine("Set SVG_PAINTCODE_WRITE_SLICE to rewrite the committed slice.");
            return;
        }

        var suite = PaintCodeOracleSuite.Instance;

        // Written into the source tree rather than the build output, since the point is to commit it.
        var directory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "TestAssets", "Oracle", Path.GetFileName(suite.Folder)));

        Directory.CreateDirectory(directory);

        foreach (var slug in PaintCodeSliceTests.Slice(suite.Folder))
        {
            var drawing = suite.Drawings.Single(d => d.Slug == slug);

            File.Copy(drawing.Path, Path.Combine(directory, slug + ".svg"), overwrite: true);

            using var svg = new SKSvg();

            Assert.NotNull(svg.Load(drawing.Path));

            using var ours = PaintCodeOracle.Ours(svg, suite.Defaults);
            using var theirs = PaintCodeOracle.Theirs(
                drawing.Method,
                suite.Defaults,
                ours.Width / PaintCodeOracle.Scale,
                ours.Height / PaintCodeOracle.Scale);

            using var image = SKImage.FromBitmap(theirs);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(Path.Combine(directory, slug + ".png"));

            data.SaveTo(stream);

            _output.WriteLine($"{slug}: {PaintCodeOracle.Difference(ours, theirs):F5}");
        }
    }
}
#endif
