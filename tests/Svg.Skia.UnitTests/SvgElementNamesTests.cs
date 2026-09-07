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
}
