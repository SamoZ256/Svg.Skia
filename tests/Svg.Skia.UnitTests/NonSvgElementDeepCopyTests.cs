// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Linq;
using Svg;
using Svg.Model.Services;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// Copying a document that contains foreign-namespace content.
/// </summary>
/// <remarks>
/// <see cref="SvgElement.DeepCopy{T}"/> builds the clone through the parameterless constructor, so a
/// <see cref="NonSvgElement"/> used to come back with the default SVG namespace while keeping its
/// element name and attributes. That is a quiet failure rather than a loud one: the copy looks like an
/// SVG element with an unrecognised name, and anything matching on name <em>and</em> namespace stops
/// matching without a word. Found because a cloned document's <c>&lt;e:code&gt;</c> block reported no
/// declarations.
/// </remarks>
public class NonSvgElementDeepCopyTests
{
    private const string Markup = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" xmlns:other="https://example.invalid/other" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code><e:param name="t" type="number" default="1" /></e:code>
            <other:thing data="kept" />
          </defs>
        </svg>
        """;

    private static SvgDocument Document()
    {
        var document = SvgService.FromSvg(Markup);
        Assert.NotNull(document);

        return document!;
    }

    private static NonSvgElement Find(SvgElement element, string name)
        => Descendants(element).OfType<NonSvgElement>().Single(e => e.Name == name);

    private static System.Collections.Generic.IEnumerable<SvgElement> Descendants(SvgElement element)
    {
        foreach (var child in element.Children)
        {
            yield return child;

            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    [Fact]
    public void A_Copied_Foreign_Element_Keeps_Its_Namespace()
    {
        var copy = (SvgDocument)Document().DeepCopy();

        // Serialization is the observable proof: the prefix is only written when the namespace is
        // known, so a lost namespace shows up as an element written without one.
        var xml = copy.GetXML();

        Assert.Contains("https://svg.skia/expr/1.0", xml);
        Assert.Contains(":code", xml);
        Assert.Contains(":thing", xml);
    }

    [Fact]
    public void A_Copied_Document_Still_Reports_Its_Declarations()
    {
        // What the defect actually broke, and the reason a cloned SKSvg could render once and then
        // never respond to a new value.
        var copy = (SvgDocument)Document().DeepCopy();

        var parameter = Assert.Single(copy.ExpressionDeclarations.Parameters);

        Assert.Equal("t", parameter.Name);
        Assert.Equal("1", parameter.DefaultExpression);
    }

    [Fact]
    public void A_Copied_Foreign_Element_Keeps_Its_Name_And_Attributes()
    {
        // These already survived; asserted so a change to the copy path cannot trade one for the
        // other.
        var copy = (SvgDocument)Document().DeepCopy();

        var thing = Find(copy, "thing");

        Assert.Equal("thing", thing.Name);
        Assert.Equal("kept", thing.CustomAttributes["data"]);
    }
}
