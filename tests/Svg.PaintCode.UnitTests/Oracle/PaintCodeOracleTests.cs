#if PAINTCODE_ORACLE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SkiaSharp;
using Svg.Expressions;
using Svg.Skia;
using Xunit;
using Xunit.Abstractions;

namespace Svg.PaintCode.UnitTests.Oracle;

/// <summary>
/// Draws every imported canvas beside the one PaintCode's own generated code draws, at every
/// setting of the booleans PaintCode varies it on.
/// </summary>
/// <remarks>
/// Everything else about the import is checked structurally — the archive reads, the markup is
/// asserted, every drawing loads and type checks. None of that looks at a pixel, and every geometry
/// fault this conversion has had (a whole oval drawing nothing, a star exploding, a symbol at the
/// anchor rather than the box corner, a nested group's anchor dropped) passed all of it.
///
/// **This does not assert parity, and the conversion does not have it.** Measured over the sample
/// document, 698 of 997 comparable canvases draw differently from PaintCode by more than antialiasing
/// accounts for — 498 of them only once a parameter leaves its default, which is why a defaults-only
/// check never saw it. So each canvas is pinned at what it currently measures, from
/// <c>TestAssets/Oracle/parity.csv</c>: the suite fails when a canvas gets worse, and the file is the
/// list of work outstanding. Closing a gap means re-running <see cref="PaintCodeOracleReport"/> and
/// committing the smaller numbers, which is the only way an entry ever shrinks.
/// </remarks>
[Collection(PaintCodeOracleCollection.Name)]
public class PaintCodeOracleTests
{
    /// <summary>
    /// Below this, two rasters are the same drawing.
    /// </summary>
    /// <remarks>
    /// Not zero: the two sides reach Skia through different models, and an axis-aligned rect alone
    /// costs about 850 antialiased edge pixels at RMS 0.0045 through two models
    /// (<c>SkiaCSharpRenderTests</c>) — and these canvases are built from axis-aligned rects. It is
    /// also the floor every baseline entry is raised to, so noise at the bottom cannot fail a row.
    /// </remarks>
    internal const double Parity = 0.004;

    /// <summary>
    /// Room for a baseline recorded to four decimals, and nothing more.
    /// </summary>
    private const double Rounding = 0.0001;

    private readonly ITestOutputHelper _output;

    public PaintCodeOracleTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Rows()
        => PaintCodeOracleSuite.Instance.Drawings.Select(drawing => new object[] { drawing.Slug });

    [SampleFact]
    public void Every_Canvas_Draws_Through_A_Method_Of_Its_Own()
    {
        var suite = PaintCodeOracleSuite.Instance;

        _output.WriteLine($"{suite.Drawings.Count} drawings, {PaintCodeOracle.MethodCount} methods, {PaintCodeOracle.Methods.Count} names");

        // A normalised name can collide, and two canvases compared against one method would pass by
        // drawing the same picture as each other rather than as PaintCode.
        Assert.Empty(suite.Unmatched);
        Assert.Empty(suite.Shared);
    }

    /// <summary>
    /// The baseline names every canvas that can be compared, and nothing else.
    /// </summary>
    /// <remarks>
    /// Without this a canvas could leave the comparison — by gaining text, by failing to import, by
    /// being renamed — and take its own gap with it, silently.
    /// </remarks>
    [SampleFact]
    public void The_Baseline_Names_Every_Comparable_Canvas()
    {
        var suite = PaintCodeOracleSuite.Instance;
        var comparable = suite.Drawings.Select(d => d.Slug).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var recorded = PaintCodeOracleBaseline.Allowed.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray();

        _output.WriteLine($"{comparable.Length} canvases, {suite.Drawings.Count(d => d.HasText)} of them drawing words");

        Assert.Equal(recorded, comparable);
    }

    /// <summary>
    /// The 17 canvases that draw words, which cannot be compared at any threshold.
    /// </summary>
    /// <remarks>
    /// PaintCode asks for four SF-UI-Display faces by filename. They are not beside its generated
    /// code, nothing sets <c>TypefaceManager.Assembly</c>, and its lookup falls through to whatever
    /// family the system happens to list first — without failing. Excluded by name and counted, so
    /// the exclusion is visible rather than a gap in the rows.
    /// </remarks>
    [SampleFact]
    public void The_Canvases_That_Draw_Words_Are_Named()
    {
        var text = PaintCodeOracleSuite.Instance.Drawings.Where(d => d.HasText).Select(d => d.Slug).ToArray();

        _output.WriteLine(PaintCodeOracle.Fonts is { } fonts ? $"compared, in the faces under {fonts}" : "not compared: set SVG_PAINTCODE_FONTS");

        foreach (var slug in text)
        {
            _output.WriteLine("  " + slug);
        }

        Assert.Equal(17, text.Length);
    }

    [SampleTheory]
    [MemberData(nameof(Rows))]
    public void Draws_No_Worse_Than_It_Did(string slug)
    {
        var suite = PaintCodeOracleSuite.Instance;
        var drawing = suite.Drawings.Single(d => d.Slug == slug);

        if (drawing.HasText && PaintCodeOracle.Fonts is null)
        {
            // Without the faces PaintCode names, its own lookup falls back to an arbitrary installed
            // family without saying so, and there is nothing stable to compare against. Named by the
            // fact below rather than quietly skipped here.
            return;
        }

        var allowed = PaintCodeOracleBaseline.Allowed[slug];

        using var svg = PaintCodeOracle.Load(drawing.Path);

        var switches = PaintCodeOracle.Switches(drawing.Method);
        var worst = 0d;
        var where = "defaults";

        foreach (var (values, description) in Combinations(suite.Defaults, switches))
        {
            using var ours = PaintCodeOracle.Ours(svg, values);
            using var theirs = PaintCodeOracle.Theirs(drawing.Method, values, ours.Width / PaintCodeOracle.Scale, ours.Height / PaintCodeOracle.Scale);

            var difference = PaintCodeOracle.Difference(ours, theirs);

            if (difference > worst)
            {
                worst = difference;
                where = description;
            }

            if (difference > allowed + Rounding)
            {
                Keep(slug, description, ours, theirs);
            }
        }

        Assert.True(
            worst <= allowed + Rounding,
            $"{slug} draws further from PaintCode than it did: {worst.ToString("F5", CultureInfo.InvariantCulture)} at [{where}], "
            + $"recorded {allowed.ToString("F4", CultureInfo.InvariantCulture)}. Both rasters and a heat map are in tests/Tests.");
    }

    /// <summary>
    /// Every combination of the booleans PaintCode's own method takes, with everything else at the
    /// document's defaults.
    /// </summary>
    /// <remarks>
    /// Driven from the method rather than from our declarations on purpose. A drawing's block is
    /// narrowed to what it references, so a binding we failed to carry across would drop the
    /// parameter and take itself out of the comparison — the one fault the comparison most needs to
    /// find, and the one that turned out to be there.
    /// </remarks>
    private static IEnumerable<(IReadOnlyDictionary<string, ExprValue> Values, string Description)> Combinations(
        IReadOnlyDictionary<string, ExprValue> defaults,
        IReadOnlyList<string> switches)
    {
        for (var combination = 0; combination < 1 << switches.Count; combination++)
        {
            var values = new Dictionary<string, ExprValue>(defaults, StringComparer.Ordinal);

            for (var bit = 0; bit < switches.Count; bit++)
            {
                values[switches[bit]] = ExprValue.Boolean(((combination >> bit) & 1) == 1);
            }

            yield return (values, PaintCodeOracle.Describe(switches, combination));
        }
    }

    /// <summary>
    /// Leaves both rasters and a heat map beside the other suites' output, so a failure can be
    /// looked at rather than only read about. <c>.gitignore</c> already covers the name.
    /// </summary>
    private static void Keep(string slug, string description, SKBitmap ours, SKBitmap theirs)
    {
        var directory = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "Tests"));

        Directory.CreateDirectory(directory);

        var name = $"paintcode {slug} {new string(description.Where(c => char.IsLetterOrDigit(c) || c == '=' || c == ' ').ToArray())}";

        Write(ours, Path.Combine(directory, $"{name} (Actual).png"));
        Write(theirs, Path.Combine(directory, $"{name} paintcode (Actual).png"));

        using var heat = Heat(ours, theirs);

        Write(heat, Path.Combine(directory, $"{name} heat (Actual).png"));
    }

    private static SKBitmap Heat(SKBitmap ours, SKBitmap theirs)
    {
        var heat = new SKBitmap(ours.Width, ours.Height);
        var a = ours.Pixels;
        var b = theirs.Pixels;

        for (var i = 0; i < a.Length; i++)
        {
            var difference = Math.Max(
                Math.Abs(Over(a[i].Red, a[i].Alpha) - Over(b[i].Red, b[i].Alpha)),
                Math.Max(
                    Math.Abs(Over(a[i].Green, a[i].Alpha) - Over(b[i].Green, b[i].Alpha)),
                    Math.Abs(Over(a[i].Blue, a[i].Alpha) - Over(b[i].Blue, b[i].Alpha))));

            heat.SetPixel(
                i % heat.Width,
                i / heat.Width,
                difference > 40 ? new SKColor(255, 0, 0) : difference > 8 ? new SKColor(255, 190, 0) : new SKColor(240, 240, 240));
        }

        return heat;

        static int Over(byte channel, byte alpha) => ((channel * alpha) + (255 * (255 - alpha))) / 255;
    }

    private static void Write(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);

        data.SaveTo(stream);
    }
}
#endif
