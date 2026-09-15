#if PAINTCODE_ORACLE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Svg.Expressions;
using Svg.Skia;
using Xunit;
using Xunit.Abstractions;

namespace Svg.PaintCode.UnitTests.Oracle;

/// <summary>
/// Measures every canvas against PaintCode at every setting and writes the distribution out.
/// </summary>
/// <remarks>
/// This is where the thresholds in <see cref="PaintCodeOracleTests"/> come from, and the thing to
/// re-run when they need to move. It is behind a second switch because it measures what that suite
/// already measures — running both doubles the work for one number.
/// </remarks>
[Collection(PaintCodeOracleCollection.Name)]
public class PaintCodeOracleReport
{
    private readonly ITestOutputHelper _output;

    public PaintCodeOracleReport(ITestOutputHelper output) => _output = output;

    private static bool Wanted => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SVG_PAINTCODE_ORACLE_REPORT"));

    [SampleFact]
    public void Measure()
    {
        if (!Wanted)
        {
            _output.WriteLine("Set SVG_PAINTCODE_ORACLE_REPORT to measure.");
            return;
        }

        var suite = PaintCodeOracleSuite.Instance;
        var rows = new List<(string Slug, bool Noted, bool Text, double Worst, double Defaults, string Where)>();

        foreach (var drawing in suite.Drawings)
        {
            if (drawing.HasText && PaintCodeOracle.Fonts is null)
            {
                rows.Add((drawing.Slug, drawing.Noted, drawing.HasText, double.NaN, double.NaN, "no faces to compare in"));
                continue;
            }

            SKSvg svg;

            try
            {
                svg = PaintCodeOracle.Load(drawing.Path);
            }
            catch (Exception e)
            {
                rows.Add((drawing.Slug, drawing.Noted, drawing.HasText, double.PositiveInfinity, double.PositiveInfinity, e.Message.Replace(",", ";")));
                continue;
            }

            using var _ = svg;

            var switches = PaintCodeOracle.Switches(drawing.Method);
            var dials = PaintCodeOracle.Dials(drawing.Method).Select(name => (name, PaintCodeOracle.Turns(svg, name))).ToArray();
            var worst = 0d;
            var first = 0d;
            var where = "defaults";
            var combination = 0;

            foreach (var (values, description) in PaintCodeOracleTests.Combinations(suite.Defaults, switches, dials))
            {
                double difference;

                try
                {
                    using var ours = PaintCodeOracle.Ours(svg, values);
                    using var theirs = PaintCodeOracle.Theirs(drawing.Method, values, ours.Width / PaintCodeOracle.Scale, ours.Height / PaintCodeOracle.Scale);

                    difference = PaintCodeOracle.Difference(ours, theirs);
                }
                catch (Exception e)
                {
                    // A drawing that will not bind is the worst outcome there is, and reporting it
                    // beside the measurements beats stopping the run on the first one.
                    worst = double.PositiveInfinity;
                    where = $"{description}: {e.GetType().Name} {e.Message}".Replace(",", ";");
                    break;
                }

                if (combination++ == 0)
                {
                    first = difference;
                }

                if (difference > worst)
                {
                    worst = difference;
                    where = description.Replace(",", ";");
                }
            }

            rows.Add((drawing.Slug, drawing.Noted, drawing.HasText, worst, first, where));
        }

        var directory = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "Tests"));

        Directory.CreateDirectory(directory);

        var csv = new StringBuilder("slug,noted,text,worst,defaults,where").AppendLine();

        foreach (var row in rows.OrderByDescending(r => r.Worst))
        {
            csv.AppendLine(string.Join(
                ",",
                row.Slug,
                row.Noted,
                row.Text,
                row.Worst.ToString("F6", CultureInfo.InvariantCulture),
                row.Defaults.ToString("F6", CultureInfo.InvariantCulture),
                row.Where));
        }

        File.WriteAllText(Path.Combine(directory, "paintcode-oracle (Actual).csv"), csv.ToString());

        // And the exception table itself, ready to replace the committed one: every canvas past the
        // bound, with the cause the importer's own notes give it, or Unexplained where they say
        // nothing. Written beside the measurements rather than into TestAssets, so replacing the
        // committed list is a deliberate copy and shows up as a diff worth reading.
        var exceptions = new StringBuilder("canvas,cause,measured").AppendLine();

        foreach (var row in rows.Where(r => r.Worst > PaintCodeOracleBaseline.Bound).OrderBy(r => r.Slug, StringComparer.Ordinal))
        {
            var drawing = PaintCodeOracleSuite.Instance.Drawings.First(d => d.Slug == row.Slug);
            var cause = row.Text ? "TextMetrics" : drawing.Cause ?? "Unexplained";

            exceptions.AppendLine($"{row.Slug},{cause},{Math.Ceiling(row.Worst * 10000) / 10000:F4}");
        }

        File.WriteAllText(Path.Combine(directory, "paintcode-exceptions (Actual).csv"), exceptions.ToString());

        var clean = rows.Where(r => !r.Noted && !r.Text).ToArray();

        _output.WriteLine($"{rows.Count} canvases: {clean.Length} clean, {rows.Count(r => r.Noted)} noted, {rows.Count(r => r.Text)} text");
        _output.WriteLine($"clean worst      : {clean.Max(r => r.Worst):F6}");
        _output.WriteLine($"clean identical  : {clean.Count(r => r.Worst == 0d)}");

        foreach (var cut in new[] { 0.001, 0.002, 0.004, 0.008, 0.02, 0.05 })
        {
            _output.WriteLine($"clean over {cut,-6}: {clean.Count(r => r.Worst > cut)}");
        }

        _output.WriteLine(string.Empty);
        _output.WriteLine("-- worst 30 --");

        foreach (var row in rows.OrderByDescending(r => r.Worst).Take(30))
        {
            _output.WriteLine($"{row.Worst:F6}  defaults={row.Defaults:F6}  noted={row.Noted,-5} text={row.Text,-5} {row.Slug}  [{row.Where}]");
        }
    }
}
#endif
