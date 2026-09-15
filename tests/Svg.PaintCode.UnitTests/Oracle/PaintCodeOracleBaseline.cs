#if PAINTCODE_ORACLE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Svg.PaintCode.UnitTests.Oracle;

/// <summary>Why one canvas does not draw what PaintCode draws, and how far off it is.</summary>
/// <param name="Why">The evidence, in a sentence: what was found, and what it was worth.</param>
internal sealed record PaintCodeException(string Canvas, string Cause, double Measured, string Why);

/// <summary>
/// The canvases that do not meet the bound, each with the reason it does not.
/// </summary>
/// <remarks>
/// A list of work rather than a record of what was measured. Every entry has to name a cause, so a
/// canvas cannot quietly join by having a number written beside it; the measurement is kept only to
/// stop a known gap widening.
///
/// Committed even though neither the document nor PaintCode's generated code is in this repository,
/// because it is the only part of the comparison that can be, and without it a fix has nothing to be
/// measured against. Rewrite it with <see cref="PaintCodeOracleReport"/>, never by hand.
/// </remarks>
internal static class PaintCodeOracleBaseline
{
    /// <summary>
    /// What every canvas must meet unless it is excepted below.
    /// </summary>
    /// <remarks>
    /// Two things put a floor under this and neither is ours. The two sides reach Skia through
    /// different models, where one axis-aligned rect alone costs about 0.0045
    /// (<c>SkiaCSharpRenderTests</c>) and an icon is many; and PaintCode's generated code rounds
    /// every literal to two decimals, so the drawing being compared against is itself a rounded one
    /// -- of the 24 canvases it rounds nothing in, 23 match outright.
    ///
    /// The number is where the excepted canvases stop being a list of noise and start being a list
    /// of faults: at 0.015 two thirds of the exceptions have no cause anybody can name, at 0.03 a
    /// third do, and the named causes hardly move between the two. For scale, this repository's own
    /// W3C rows sit at 0.022 for whole rendered pages and its resvg rows at 0.12.
    /// </remarks>
    internal const double Bound = 0.03;

    /// <summary>The list, or null where this document has none yet.</summary>
    internal static IReadOnlyDictionary<string, PaintCodeException>? Excepted { get; } = Read();

    private static IReadOnlyDictionary<string, PaintCodeException>? Read()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestAssets", "Oracle", "exceptions.csv");
        var excepted = new Dictionary<string, PaintCodeException>(StringComparer.Ordinal);

        // A document nobody has measured yet has no list, which is not the same as having an empty
        // one: the comparison stands aside and asks for the report rather than failing once per
        // canvas. Told apart by Exists rather than by Count, so an empty list still means "nothing
        // is excepted" and holds every canvas to the bound.
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var line in File.ReadAllLines(path).Skip(1))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var parts = PaintCodeCsv.Fields(line);

            if (parts.Count != 4 || parts[1].Length == 0 || parts[3].Length == 0)
            {
                throw new InvalidOperationException(
                    $"'{line}' does not both name a cause and say what was found. An exception that explains nothing is a number nobody can act on.");
            }

            excepted[parts[0]] = new PaintCodeException(parts[0], parts[1], double.Parse(parts[2], CultureInfo.InvariantCulture), parts[3]);
        }

        return excepted;
    }
}
#endif
