// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Svg.PaintCode.UnitTests;

/// <summary>
/// A tally somebody committed for one document, and what to do when nobody has yet.
/// </summary>
/// <remarks>
/// Numbers about a document belong beside the document, not in the assembly that reads it. The
/// difference matters the first time this suite is pointed at a second PaintCode file: a count
/// compiled into a test fails a thousand times and tells you nothing, whereas an absent file can
/// simply be written.
///
/// So an absent tally means "nobody has measured this yet" -- the test prints what it found, writes
/// it out ready to be committed, and passes. Present, it is compared whole, so a key appearing or
/// vanishing is as much a result as a count changing.
/// </remarks>
internal static class PaintCodeExpected
{
    /// <summary>
    /// Where one document's committed answers live, named after the document itself.
    /// </summary>
    /// <remarks>
    /// A PaintCode file carries its own styleKitName, which is the name PaintCode gives the class it
    /// generates -- so a document says which folder is its own and there is nothing to configure. A
    /// second one adds a folder rather than displacing the first.
    /// </remarks>
    internal static string Folder(PaintCodeDocument document)
        => Path.Combine(AppContext.BaseDirectory, "TestAssets", "Oracle", PaintCodeSlug.Pascal(document.Name));

    /// <summary>Every document this repository has committed answers for.</summary>
    /// <remarks>Read without a document in hand, which is what the slice has to do.</remarks>
    internal static IEnumerable<string> Folders()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "TestAssets", "Oracle");

        return Directory.Exists(root) ? Directory.EnumerateDirectories(root).OrderBy(p => p, StringComparer.Ordinal) : Enumerable.Empty<string>();
    }

    /// <summary>The committed tally, or null where this document has none.</summary>
    internal static IReadOnlyDictionary<string, int>? Read(string folder, string name)
    {
        var path = Path.Combine(folder, name);

        if (!File.Exists(path))
        {
            return null;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var line in File.ReadAllLines(path).Skip(1).Where(line => line.Length > 0))
        {
            var fields = PaintCodeCsv.Fields(line);

            counts[fields[0]] = int.Parse(fields[1], CultureInfo.InvariantCulture);
        }

        return counts;
    }

    /// <summary>Holds what was counted against what was committed, or records it if nothing was.</summary>
    internal static void Assert(string folder, string name, IReadOnlyDictionary<string, int> counted, ITestOutputHelper output)
    {
        foreach (var entry in counted.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"{entry.Value,8}  {entry.Key}");
        }

        if (Read(folder, name) is { } expected)
        {
            Xunit.Assert.Equal(expected.OrderBy(e => e.Key, StringComparer.Ordinal), counted.OrderBy(e => e.Key, StringComparer.Ordinal));

            return;
        }

        var written = Write(name, counted);

        output.WriteLine(string.Empty);
        // The source tree, not the copy under bin that this reads from: a file dropped in the output
        // is gone at the next build.
        output.WriteLine(
            $"Nothing is committed for this document. What was counted is in {written} — copy it to "
            + $"tests/Svg.PaintCode.UnitTests/TestAssets/Oracle/{Path.GetFileName(folder)}/{name} to pin it.");
    }

    private static string Write(string name, IReadOnlyDictionary<string, int> counted)
    {
        var directory = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "Tests"));

        Directory.CreateDirectory(directory);

        var csv = new StringBuilder("key,value").AppendLine();

        foreach (var entry in counted.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            csv.Append(PaintCodeCsv.Field(entry.Key)).Append(',').AppendLine(entry.Value.ToString(CultureInfo.InvariantCulture));
        }

        // Named the way every other artefact this suite leaves behind is, so .gitignore already
        // covers it and it cannot be committed by accident in place of a deliberate copy.
        var path = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + " (Actual).csv");

        File.WriteAllText(path, csv.ToString());

        return path;
    }
}
