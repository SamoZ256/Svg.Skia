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

    /// <summary>
    /// A drawing built from something made of it is keyed as the built document has it.
    /// </summary>
    /// <remarks>
    /// A recipe puts the declarations at the front of the root, so the rect a tree of the built
    /// document calls "1" is the one the file writes first. Keyed by the file's own path, every row
    /// of every drawing under a recipe looked up nothing.
    /// </remarks>
    [Fact]
    public void An_Injected_Block_Moves_The_Addresses_And_Not_The_Places()
    {
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <rect width="24" height="24" fill="#00ff00" />
            </svg>
            """;

        const string built = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24">
              <defs><e:code><e:param name="tint" type="color" /></e:code></defs>
              <rect width="24" height="24" fill="{{ tint }}" />
            </svg>
            """;

        var map = SvgSourceElements.Map(source, built);

        // Where the built document has it...
        Assert.True(map.ContainsKey("1"));

        // ...and the span is the file's, which is what the pane shows and saves.
        Assert.Equal("""<rect width="24" height="24" fill="#00ff00" />""", Written(source, map["1"]));

        // The injected block is in neither the file nor this map: its row rightly finds nothing.
        Assert.False(map.ContainsKey("0"));
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void A_Block_Injected_Into_An_Existing_Defs_Moves_Its_Siblings()
    {
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs><linearGradient id="g" /></defs>
              <rect width="24" height="24" />
            </svg>
            """;

        const string built = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code /><linearGradient id="g" /></defs>
              <rect width="24" height="24" />
            </svg>
            """;

        var map = SvgSourceElements.Map(source, built);

        // The gradient moved along inside the defs; the rect beside it did not move at all.
        Assert.Equal("""<linearGradient id="g" />""", Written(source, map["0/1"]));
        Assert.Equal("""<rect width="24" height="24" />""", Written(source, map["1"]));
    }

    [Fact]
    public void Two_Documents_That_Are_Not_One_With_Insertions_Fall_Back_To_The_File()
    {
        // A guess about which element is which would scroll somebody confidently to the wrong line,
        // which is the one failure this side is designed against.
        const string source = """<svg xmlns="http://www.w3.org/2000/svg"><rect /><circle /></svg>""";
        const string built = """<svg xmlns="http://www.w3.org/2000/svg"><rect /></svg>""";

        var map = SvgSourceElements.Map(source, built);

        Assert.Equal("<rect />", Written(source, map["0"]));
        Assert.Equal("<circle />", Written(source, map["1"]));
    }

    [Fact]
    public void No_Rewrite_Is_The_Text_Itself()
    {
        const string source = """<svg xmlns="http://www.w3.org/2000/svg"><rect /></svg>""";

        Assert.Equal(SvgSourceElements.Map(source).Count, SvgSourceElements.Map(source, source).Count);
        Assert.Equal(SvgSourceElements.Map(source).Count, SvgSourceElements.Map(source, null).Count);
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
