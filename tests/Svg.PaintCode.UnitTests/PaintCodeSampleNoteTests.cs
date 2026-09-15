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

    /// <summary>The note kinds the sample document produces, by what each of them is about.</summary>
    private static readonly (PaintCodeImportSeverity Severity, string Property, string Fragment, int Count)[] s_expected =
    {
        // SVG anchors a run where PaintCode measures one, so the words land in the right box but not
        // to the same tenth. Unconditional: one per text-bearing shape.
        (PaintCodeImportSeverity.Approximated, "text", "placed from the shape's box", 46),

        // A gradient turned by a dial rather than laid by its ends. PaintCode works its two points
        // out from the shape's own middle, which is not the box's, and the handles beside the angle
        // are stale for these.
        (PaintCodeImportSeverity.Approximated, "fill", "laid across the shape's box", 46),

        // An oval's sweep driven by an expression, which path data cannot carry.
        (PaintCodeImportSeverity.Dropped, "endAngle", "keeps this value literal", 10),
        (PaintCodeImportSeverity.Dropped, "startAngle", "keeps this value literal", 1),

        // A whole gradient chosen by an expression rather than its stops being driven.
        (PaintCodeImportSeverity.Dropped, "fill", "a gradient has no type", 6),

        // The document's own fault, and the only kind here that is.
        (PaintCodeImportSeverity.Missing, "symbol", "the document has no canvas called", 5),

        // PaintCode's numbering is not SVG's, and one that is nearly right is worse than reported.
        (PaintCodeImportSeverity.Dropped, "blendMode", "has no name here", 1)
    };

    [SampleFact]
    public void The_Sample_Reports_What_It_Has_Always_Reported()
    {
        var notes = Notes();

        foreach (var (severity, property, fragment, count) in s_expected)
        {
            var matched = notes.Count(note =>
                note.Severity == severity &&
                note.Property == property &&
                note.Message.Contains(fragment, StringComparison.Ordinal));

            _output.WriteLine($"{matched,4} (expected {count,4})  {severity} {property} — {fragment}");

            Assert.Equal(count, matched);
        }

        // And nothing else: a kind nobody has an expectation for is a kind nobody has looked at.
        Assert.Equal(s_expected.Sum(e => e.Count), notes.Count);
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
