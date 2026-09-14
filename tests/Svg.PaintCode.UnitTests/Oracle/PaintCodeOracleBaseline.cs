#if PAINTCODE_ORACLE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Svg.PaintCode.UnitTests.Oracle;

/// <summary>
/// How far each canvas currently draws from PaintCode, as measured, one row per canvas.
/// </summary>
/// <remarks>
/// A record of the gap rather than a licence for it. Numbers above
/// <see cref="PaintCodeOracleTests.Parity"/> mark a canvas the conversion does not yet reproduce;
/// re-running <see cref="PaintCodeOracleReport"/> after a fix writes the smaller number, and
/// committing it is what stops the gap reopening.
///
/// Committed even though neither the document nor PaintCode's generated code is in this repository:
/// it is the only part of the comparison that can be, and without it a fix has nothing to be
/// measured against.
/// </remarks>
internal static class PaintCodeOracleBaseline
{
    internal static IReadOnlyDictionary<string, double> Allowed { get; } = Read();

    private static IReadOnlyDictionary<string, double> Read()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestAssets", "Oracle", "parity.csv");

        var allowed = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var line in File.ReadAllLines(path).Skip(1))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var comma = line.IndexOf(',');

            allowed[line.Substring(0, comma)] = double.Parse(line.Substring(comma + 1), CultureInfo.InvariantCulture);
        }

        return allowed;
    }
}
#endif
