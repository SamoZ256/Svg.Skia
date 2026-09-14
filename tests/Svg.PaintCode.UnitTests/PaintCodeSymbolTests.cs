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

        Assert.Equal(2, uses.Count);
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

    [Fact]
    public void An_Instance_Naming_A_Canvas_The_Document_Does_Not_Hold_Is_Reported_Rather_Than_Drawn()
    {
        var notes = new List<PaintCodeImportNote>();
        Host(notes);

        var note = Assert.Single(notes);

        Assert.Equal("Missing", note.Element);
        Assert.Contains("does not hold", note.Message);
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
