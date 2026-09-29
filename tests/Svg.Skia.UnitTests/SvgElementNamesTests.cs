// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Linq;
using Svg;
using Svg.Model.Services;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// Naming an element from outside <c>Svg.Custom</c>.
/// </summary>
/// <remarks>
/// The rules are pinned here because they are not guessable from the type: three kinds of element
/// answer from somewhere other than the generated name table, and a caller that only consulted the
/// table would show a class name for each of them.
/// </remarks>
public class SvgElementNamesTests
{
    private const string Markup = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:other="https://example.invalid/other" viewBox="0 0 24 24" width="24" height="24">
          <g id="wrap">
            <rect width="24" height="24" />
            <widget spin="3" />
          </g>
          <other:thing data="kept" />
        </svg>
        """;

    private static SvgDocument Document() => SvgService.FromSvg(Markup)!;

    [Fact]
    public void The_Document_Is_Its_Root_Element()
    {
        // It is an SvgDocument, not an SvgFragment, and the table has no entry for one.
        Assert.Equal("svg", SvgElementNames.NameOf(Document()));
    }

    [Fact]
    public void A_Recognised_Element_Is_Named_By_Its_Type()
    {
        var document = Document();

        Assert.Equal("g", SvgElementNames.NameOf(document.Children.OfType<SvgGroup>().Single()));
        Assert.Equal("rect", SvgElementNames.NameOf(document.Descendants().OfType<SvgRectangle>().Single()));
    }

    [Fact]
    public void An_Unrecognised_Element_Keeps_The_Name_It_Was_Written_With()
    {
        var unknown = Document().Descendants().OfType<SvgUnknownElement>().Single();

        Assert.Equal("widget", SvgElementNames.NameOf(unknown));
    }

    [Fact]
    public void A_Foreign_Element_Carries_Its_Own_Name()
    {
        var foreign = Document().Descendants().OfType<NonSvgElement>().Single();

        Assert.Equal("thing", SvgElementNames.NameOf(foreign));
    }

    [Fact]
    public void A_Shape_Takes_Its_Own_Geometry_And_Every_Presentation_Attribute()
    {
        var rect = SvgElementNames.AttributesOf("rect");

        foreach (var name in new[] { "x", "rx", "fill", "stroke-linecap", "pointer-events", "mask", "transform", "class", "style", "id" })
        {
            Assert.Contains(name, rect);
        }

        // Another shape's geometry, an event, and the three the parser reads nowhere or only from style.
        foreach (var name in new[] { "cx", "points", "onclick", "marker", "mix-blend-mode", "isolation" })
        {
            Assert.DoesNotContain(name, rect);
        }
    }

    [Fact]
    public void A_Link_Is_Spelt_Bare_And_A_Space_Rule_With_Its_Prefix()
    {
        Assert.Contains("href", SvgElementNames.AttributesOf("use"));
        Assert.DoesNotContain("xlink:href", SvgElementNames.AttributesOf("use"));

        Assert.Contains("xml:space", SvgElementNames.AttributesOf("text"));
        Assert.Contains("textLength", SvgElementNames.AttributesOf("text"));
    }

    [Fact]
    public void An_Element_The_Parser_Does_Not_Know_Takes_Nothing()
    {
        Assert.Empty(SvgElementNames.AttributesOf("widget"));
        Assert.Contains("viewBox", SvgElementNames.AttributesOf("svg"));
    }
}
