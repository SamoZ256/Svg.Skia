using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Svg;
using Xunit;

namespace Svg.Highlighting.UnitTests;

/// <summary>
/// Placing an element in the text it was written in.
/// </summary>
/// <remarks>
/// The addresses are asserted as much as the offsets. The key has to be the one
/// <c>SvgElementAddress</c> spells, or a caller holding a parsed element looks up a path this map
/// does not have — and the two are read separately, so nothing but a test says they agree.
/// </remarks>
public class SvgSourceElementsTests
{
    private static string Written(string source, SvgSourceElement element)
        => source.Substring(element.Start, element.Length);

    [Fact]
    public void Each_Element_Is_Its_Whole_Start_Tag()
    {
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <g id="wrap">
                <rect width="24" height="24" />
              </g>
            </svg>
            """;

        var map = SvgSourceElements.Map(source);

        Assert.Equal(3, map.Count);
        Assert.Equal("""<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">""", Written(source, map[""]));
        Assert.Equal("""<g id="wrap">""", Written(source, map["0"]));
        Assert.Equal("""<rect width="24" height="24" />""", Written(source, map["0/0"]));
    }

    [Fact]
    public void The_Address_Counts_Elements_And_Nothing_Else()
    {
        // Comments and runs of text are children of neither tree, which is the whole reason a
        // child-index path can be produced from the text and from the document independently.
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <!-- a note -->
              <rect />
              some text
              <circle />
            </svg>
            """;

        var map = SvgSourceElements.Map(source);

        Assert.Equal("rect", map["0"].Name);
        Assert.Equal("circle", map["1"].Name);
    }

    [Fact]
    public void A_Foreign_Element_Is_Named_Without_Its_Prefix()
    {
        // The name is what a caller checks the lookup against, and the other side has no prefix
        // to compare with.
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code><e:param name="t" type="number" default="1" /></e:code></defs>
            </svg>
            """;

        var map = SvgSourceElements.Map(source);

        Assert.Equal("defs", map["0"].Name);
        Assert.Equal("code", map["0/0"].Name);
        Assert.Equal("param", map["0/0/0"].Name);
        Assert.Equal("""<e:code>""", Written(source, map["0/0"]));
    }

    [Fact]
    public void An_Angle_Bracket_Inside_A_Value_Does_Not_End_The_Tag()
    {
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <rect width="24" height="24" visibility="{{ a &gt; b }}" fill='#00ff00' />
            </svg>
            """;

        var map = SvgSourceElements.Map(source);

        Assert.Equal(
            """<rect width="24" height="24" visibility="{{ a &gt; b }}" fill='#00ff00' />""",
            Written(source, map["0"]));
    }

    [Fact]
    public void A_Document_Declaring_Entities_Is_Read()
    {
        // The reader settings exist for this: four W3C fixtures declare their shapes in an internal
        // subset, and a stricter reader refuses a file that opens perfectly.
        const string source = """
            <?xml version="1.0"?>
            <!DOCTYPE svg [ <!ENTITY box "<rect width='10' height='10' />"> ]>
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">&box;</svg>
            """;

        var map = SvgSourceElements.Map(source);

        Assert.Equal("svg", map[""].Name);
    }

    [Fact]
    public void Every_Address_The_Parser_Produces_Is_One_This_Map_Has()
    {
        // The invariant the whole thing rests on. Two readers walk the same file and neither knows
        // about the other; if their child indexes ever disagreed, a caller would be handed the span
        // of some other element and never be told.
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24">
              <!-- a note, which is a child of neither -->
              <defs>
                <e:code><e:param name="t" type="number" default="1" /></e:code>
                <linearGradient id="fade"><stop offset="0" /><stop offset="1" /></linearGradient>
              </defs>
              <title>A drawing</title>
              <g id="wrap">
                text between the elements
                <rect width="24" height="24" fill="url(#fade)" />
                <widget spin="3" />
                <text x="2" y="20">hi<tspan>!</tspan></text>
              </g>
            </svg>
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));

        var document = SvgDocument.Open<SvgDocument>(stream);
        var map = SvgSourceElements.Map(source);

        var walked = new[] { (SvgElement)document }.Concat(document.Descendants()).ToArray();

        Assert.Equal(map.Count, walked.Length);

        foreach (var element in walked)
        {
            var address = SvgElementAddress.Create(element).Key;

            Assert.True(map.ContainsKey(address), $"nothing is written at '{address}'");
            Assert.Equal(SvgElementNames.NameOf(element), map[address].Name);
        }
    }

    [Fact]
    public void A_Document_That_Will_Not_Parse_Places_Nothing()
    {
        // Which is what a drawing looks like for most of the time somebody is typing one. Saying
        // what is wrong with it belongs to the diagnostics pass, not to this.
        Assert.Empty(SvgSourceElements.Map("<svg><rect"));
        Assert.Empty(SvgSourceElements.Map(""));
        Assert.Empty(SvgSourceElements.Map(null));
    }
}
