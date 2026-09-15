// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Svg.PaintCode.UnitTests;

public class PaintCodeSymbolTests
{
    [Fact]
    public void An_Instance_That_Passes_Everything_Along_Shares_One_Copy_With_The_Next()
    {
        var uses = Host().Descendants().Where(element => element.Name.LocalName == "use").ToList();

        Assert.Equal(4, uses.Count);
        Assert.NotEqual(uses[0].Attribute("href")!.Value, uses[1].Attribute("href")!.Value);
    }

    [Fact]
    public void An_Instance_Is_Scaled_To_The_Box_It_Was_Placed_In()
        => Assert.Equal("translate(0,15) scale(0.5,0.5)", Host().Descendants().First(element => element.Name.LocalName == "use").Attribute("transform")!.Value);

    [Fact]
    public void An_Instance_Is_Clipped_To_The_Canvas_It_Points_At()
    {
        var document = Host();
        var use = document.Descendants().First(element => element.Name.LocalName == "use");
        var clip = document.Descendants().First(element => element.Name.LocalName == "clipPath");

        Assert.Equal($"url(#{clip.Attribute("id")!.Value})", use.Attribute("clip-path")!.Value);
        Assert.Equal("30", clip.Elements().Single().Attribute("width")!.Value);
    }

    [Fact]
    public void An_Instance_That_Rebinds_A_Variable_Gets_A_Copy_With_That_Expression_In_It()
    {
        var fills = Host().Descendants()
            .Where(element => element.Name.LocalName == "path")
            .Select(element => element.Attribute("fill")!.Value)
            .ToList();

        Assert.Contains("{{ isLight ? colorPurple : colorPurple }}", fills);
        Assert.Contains("{{ isNotLight ? colorPurple : colorPurple }}", fills);
    }

    /// <summary>
    /// An instance that pins a number still moves what that number drives.
    /// </summary>
    /// <remarks>
    /// The offset a driven transform carries is the gap between the expression and the number saved
    /// beside it, and it belongs to the canvas rather than to whoever draws it. Measuring it against
    /// the caller's value instead left nothing free in the expression, so the offset came to exactly
    /// minus the expression, the two cancelled, and every instance drew at the pose it was saved in:
    /// a tank at a twentieth full drew full, and 20 canvases of the sample were wrong by it.
    ///
    /// Here the shape sits 13 above its anchor and is moved by <c>level * 10</c>, saved at level 1,
    /// so the offset is 13 - 10 = 3 whatever an instance passes. The copy the pinning instance gets
    /// must read 0.5 * 10 + 3, not the 13 the target was saved at.
    /// </remarks>
    [Fact]
    public void A_Pinned_Number_Still_Drives_The_Transform_It_Was_Given_To()
    {
        var copies = Host().Descendants()
            .Where(element => element.Name.LocalName == "g" && element.Attribute("id")?.Value.StartsWith("sym-slider") == true)
            .ToList();

        var bar = Assert.Single(copies).Elements().Single();

        Assert.Equal("translate(0,{{ 0.5 * 10 + 3 }})", bar.Attribute("transform")!.Value);
    }

    // Verified against PaintCode's own generated code, which writes the box as an SKRect and clips
    // and translates to its corner: the anchor is only where a turn pivots.
    [Fact]
    public void An_Instance_Whose_Anchor_Was_Moved_Is_Placed_At_Its_Box_Corner()
    {
        var use = Host().Descendants()
            .Where(element => element.Name.LocalName == "use")
            .Last();

        Assert.Equal("translate(20,25) translate(-5,-5) scale(0.5,0.5)", use.Attribute("transform")!.Value);
    }

    [Fact]
    public void An_Instance_Naming_A_Canvas_The_Document_Does_Not_Hold_Is_Reported_Rather_Than_Drawn()
    {
        var notes = new List<PaintCodeImportNote>();
        Host(notes);

        var note = Assert.Single(notes, one => one.Element == "Missing");

        // A severity of its own, because this one is not the converter's to fix: however much SVG
        // learns to say, a symbol naming a canvas the document has no copy of still draws nothing.
        Assert.Equal(PaintCodeImportSeverity.Missing, note.Severity);
        Assert.Contains("the document has no canvas called 'nowhere'", note.Message);

        // And it says what it cost without the property column, which names nothing here.
        Assert.StartsWith("host/Missing: the document has no canvas", note.ToString());
    }

    [Fact]
    public void A_Symbol_That_Contains_Itself_Says_So_Rather_Than_Running_Out_Of_Stack()
    {
        var document = PaintCodeDocument.Parse(SymbolDocument.Bytes(cycle: true));
        var canvas = document.Canvases.Single(one => one.Name == "loop");

        var failure = Assert.Throws<PaintCodeException>(() => PaintCodeSvgWriter.Write(
            canvas,
            PaintCodeDeclarations.Of(document),
            PaintCodeSymbols.Of(document),
            new List<PaintCodeImportNote>()));

        Assert.Contains("contains itself", failure.Message);
    }

    private static XDocument Host(ICollection<PaintCodeImportNote>? notes = null)
    {
        var document = PaintCodeDocument.Parse(SymbolDocument.Bytes());

        return PaintCodeSvgWriter.Write(
            document.Canvases.Single(canvas => canvas.Name == "host"),
            PaintCodeDeclarations.Of(document),
            PaintCodeSymbols.Of(document),
            notes ?? new List<PaintCodeImportNote>());
    }
}
