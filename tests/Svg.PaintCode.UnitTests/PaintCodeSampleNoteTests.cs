// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Svg.PaintCode.UnitTests;

/// <summary>
/// What the importer says it could not carry across, counted.
/// </summary>
/// <remarks>
/// The report is the conversion's own account of itself, and nothing tested it: a note class could
/// appear, double or vanish and the only way to notice was to read the output. Pinned per kind
/// rather than as a total, so a number moving says which kind moved.
///
/// These counts are a debt, not a specification. Every one of them going down is the point; a row
/// reaching nought should be deleted, not kept at nought. What must not happen quietly is one going
/// up, or a kind nobody has seen before appearing.
/// </remarks>
public class PaintCodeSampleNoteTests
{
    private readonly ITestOutputHelper _output;

    public PaintCodeSampleNoteTests(ITestOutputHelper output) => _output = output;

    /// <summary>What a note is about, as a key the committed tally is written in.</summary>
    /// <remarks>
    /// The fragment is enough of the message to tell one kind from another and no more, so a
    /// reworded note does not read as a new one while a new kind still does.
    /// </remarks>
    private static string Kind(PaintCodeImportNote note)
        => note.Severity is PaintCodeImportSeverity.Missing ? "Missing/symbol/no canvas called"
            : note.Property is "text" ? "Approximated/text/placed from the shape's box"
            : note.Property is "startAngle" or "endAngle" ? "Dropped/" + note.Property + "/keeps this value literal"
            : note.Property is "blendMode" ? "Dropped/blendMode/has no name here"
            : note.Message.Contains("laid across the shape's box", StringComparison.Ordinal) ? "Approximated/fill/laid across the shape's box"
            : note.Message.Contains("a gradient has no type", StringComparison.Ordinal) ? "Dropped/fill/a gradient has no type"
            : note.Severity + "/" + note.Property + "/" + note.Message;

    [SampleFact]
    public void The_Sample_Reports_What_It_Has_Always_Reported()
    {
        // Counted whole, so a kind appearing or vanishing is as much a result as a count changing --
        // which is what caught nine driven sweeps going unreported on clip shapes.
        PaintCodeExpected.Assert(
            "notes.csv",
            Notes().GroupBy(Kind, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            _output);
    }

    /// <summary>
    /// The five drawings that come out short because the document points at canvases it has not got.
    /// </summary>
    /// <remarks>
    /// Named rather than counted, because this is the list to take back to PaintCode: two canvases,
    /// cooling-state and heating-state, are referenced by symbols and are in no desk of the document
    /// under any spelling. No amount of work here will draw them.
    /// </remarks>
    [SampleFact]
    public void The_Document_Points_At_Two_Canvases_It_Does_Not_Contain()
    {
        var missing = Notes().Where(note => note.Severity is PaintCodeImportSeverity.Missing).ToList();

        foreach (var note in missing)
        {
            _output.WriteLine(note.ToString());
        }

        Assert.Equal(
            new[] { "symbol-cooling-locked", "valve-cold-level", "valve-cold-state", "valve-hot-level", "valve-hot-state" },
            missing.Select(note => note.Canvas).OrderBy(name => name, StringComparer.Ordinal).ToArray());

        Assert.Equal(
            new[] { "cooling-state", "heating-state" },
            missing.Select(note => note.Message.Split('\'')[1]).Distinct().OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<PaintCodeImportNote> Notes()
    {
        var document = PaintCodeDocument.Load(SampleFactAttribute.Path!);
        var directory = Directory.CreateTempSubdirectory("paintcode-notes");

        try
        {
            return PaintCodeImport.Run(document, new PaintCodeImportOptions(directory.FullName) { IncludeSymbolOnlyCanvases = true }).Notes;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
