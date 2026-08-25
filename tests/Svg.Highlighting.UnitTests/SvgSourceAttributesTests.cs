using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Svg.Highlighting.UnitTests;

/// <summary>
/// What the SVG parser's own converters say about an attribute's value, placed where a view can
/// mark it.
/// </summary>
/// <remarks>
/// Nothing here decides what is wrong with a value: every message comes from the converter that
/// attribute actually uses, so these assert placement, what is asked at all, and above all what is
/// <em>not</em> asked. A wave under a value the drawing used correctly is worse than no wave, so the
/// silences below are the point of the exercise rather than an afterthought.
/// </remarks>
public class SvgSourceAttributesTests
{
    private static SvgSourceDiagnostic[] Of(string body)
        => SvgSourceDiagnostics.Analyse(
            $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
              {body}
            </svg>
            """).ToArray();

    /// <summary>What a diagnostic is pointing at, which is the half a message does not say.</summary>
    private static string Marked(string body)
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
              {body}
            </svg>
            """;

        var one = Assert.Single(SvgSourceDiagnostics.Analyse(source));

        return source.Substring(one.Start, one.Length);
    }

    [Fact]
    public void A_Value_The_Converter_Refuses_Is_Reported_Where_It_Is_Written()
    {
        // The point of the whole exercise. Today this renders as a zero-width rectangle and says
        // nothing: the converter throws, the generated SetValue catches it, warns to Trace and
        // returns true, so the property keeps its default and nothing above can tell.
        Assert.Equal("\"abc\"", Marked("<rect width=\"abc\" height=\"10\" />"));
    }

    [Fact]
    public void The_Message_Names_The_Attribute_And_The_Value()
    {
        // The converter decides, and its wording is kept -- but it is answering about a bare string,
        // so on its own it says neither which attribute nor which value it was given.
        var one = Assert.Single(Of("<rect width=\"abc\" />"));

        Assert.StartsWith("'width' cannot be set from 'abc'.", one.Message);
    }

    [Fact]
    public void A_Drawing_Whose_Values_All_Convert_Has_Nothing_To_Say()
    {
        Assert.Empty(Of("<rect x=\"1\" y=\"2\" width=\"10\" height=\"1e2\" fill=\"#0f0\" opacity=\".5\" />"));
        Assert.Empty(Of("<circle cx=\"5%\" cy=\"5em\" r=\"3pt\" stroke=\"url(#g)\" stroke-width=\"2\" />"));
    }

    [Fact]
    public void Every_Element_Is_Asked_About_Its_Own_Attributes()
    {
        // rx belongs to both, and 'orient' to neither -- the descriptor comes from the element
        // carrying the attribute rather than from a table of names.
        Assert.Empty(Of("<ellipse rx=\"4\" ry=\"4\" />"));
        Assert.Single(Of("<ellipse rx=\"q\" ry=\"4\" />"));
    }

    [Fact]
    public void Every_Bad_Value_Is_Reported_Rather_Than_Only_The_First()
    {
        var found = Of("<rect width=\"a\" height=\"b\" />");

        Assert.Equal(2, found.Length);
        Assert.True(found[0].Start < found[1].Start);
    }

    [Fact]
    public void An_Expression_In_An_Attribute_That_Takes_One_Is_Not_A_Value()
    {
        // It is code, the extension's own checker has already read it, and what it evaluates to is
        // not known until it is bound. Converting the braces would refuse every drawing that uses
        // the extension at all.
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code><e:param name="tint" type="color" /></e:code></defs>
              <rect fill="{{ tint }}" opacity="{{ 0.5 }}" visibility="{{ true }}" />
            </svg>
            """;

        Assert.Empty(SvgSourceDiagnostics.Analyse(source));
    }

    [Fact]
    public void An_Expression_In_An_Attribute_That_Takes_None_Says_So()
    {
        // The limitation SVG_EXPRESSIONS.md used to call silent. The parser does not lift these, so
        // the braces stay in the value and the converter refuses them -- a true refusal for a
        // misleading reason. What an author needs told is that the attribute takes no expression.
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code><e:param name="w" type="number" default="4" /></e:code></defs>
              <rect stroke-width="{{ w }}" />
            </svg>
            """;

        var one = Assert.Single(SvgSourceDiagnostics.Analyse(source));

        Assert.StartsWith("'stroke-width' does not take an expression.", one.Message);

        // Which ones do, rather than in which order: the list is read off the extension's own table
        // of placeholders, and what that enumerates in is not worth pinning.
        foreach (var supported in new[] { "fill", "stroke", "stop-color", "opacity", "visibility" })
        {
            Assert.Contains(supported, one.Message, StringComparison.Ordinal);
        }
        // The braces, rather than the value around them: the pane colours a placeholder as code
        // wherever it is written, so they are the piece at that offset -- and they are also the part
        // that does nothing here.
        Assert.Equal("{{", source.Substring(one.Start, one.Length));
    }

    [Theory]
    // Substituted from the cascade before any converter sees it.
    [InlineData("<rect width=\"var(--w)\" />")]
    // Ignored by the parser on purpose, rather than refused.
    [InlineData("<rect stroke-width=\"10PX\" />")]
    // Rewritten before conversion.
    [InlineData("<rect opacity=\"undefined\" />")]
    [InlineData("<rect opacity=\"50%\" />")]
    [InlineData("<rect opacity=\"q%\" />")]
    // Staged as authored, because the cascade still has to see the word.
    [InlineData("<rect width=\"inherit\" height=\"initial\" x=\"unset\" />")]
    [InlineData("<stop stop-opacity=\"inherit\" />")]
    // Kept as custom attributes, ahead of any converter.
    [InlineData("<rect mix-blend-mode=\"screen\" isolation=\"isolate\" text-decoration=\"q\" />")]
    // Stored raw.
    [InlineData("<rect onclick=\"not(js\" />")]
    // Bound by nothing.
    [InlineData("<rect data-note=\"q\" xmlns:foo=\"urn:x\" foo:bar=\"q\" />")]
    // A custom property is a declaration, not a value.
    [InlineData("<rect style=\"--w: q\" />")]
    public void What_The_Parser_Never_Converts_Is_Never_Reported(string body)
    {
        Assert.Empty(Of(body));
    }

    [Fact]
    public void A_Declaration_In_A_Style_Attribute_Is_Checked_Like_The_Attribute_It_Stands_For()
    {
        // These reach the same converter, only later -- AddStyle stages them and FlushStyles hands
        // them back once the document is read -- so reporting one spelling and not the other would
        // be an accident of which the XML reader passed over directly.
        Assert.Equal("#gggggg", Marked("<rect style=\"fill:#gggggg\" />"));
        Assert.Equal("abc", Marked("<rect style=\"stroke-width:abc\" />"));
    }

    [Fact]
    public void The_Declaration_Is_Marked_Rather_Than_The_Whole_Attribute()
    {
        // A style attribute is one value to the splitter. Underlining all of it to say the second of
        // three declarations is wrong points at the two that are right as well.
        Assert.Equal("abc", Marked("<rect style=\"fill:red;stroke-width:abc;opacity:.5\" />"));
    }

    [Fact]
    public void Important_Is_Part_Of_The_Declaration_And_Not_Of_The_Value()
    {
        // CSS strips it before the value is converted, and the parser applies the result. Leaving it
        // on would refuse a declaration that works.
        Assert.Empty(Of("<rect style=\"fill:red !important\" />"));
    }

    [Theory]
    // The same silences the attribute form keeps, reached through the style form.
    [InlineData("<rect style=\"fill:var(--c)\" />")]
    [InlineData("<rect style=\"fill:inherit;width:auto\" />")]
    [InlineData("<rect style=\"--c: nonsense\" />")]
    // Nothing was written, which is what a half-typed declaration looks like.
    [InlineData("<rect style=\"fill:\" />")]
    [InlineData("<rect style=\"\" />")]
    // A ';' inside url() does not end a declaration, so there is no 'b)' to complain about.
    [InlineData("<rect style=\"fill:url(#a;b)\" />")]
    [InlineData("<rect style=\"/*x*/fill:red\" />")]
    public void A_Style_The_Parser_Would_Not_Convert_Is_Not_Reported(string body)
    {
        Assert.Empty(Of(body));
    }

    [Fact]
    public void A_Style_Whose_Pieces_Cannot_Be_Placed_Is_Left_Alone()
    {
        // The reader resolves entities, so &quot; arrives as one character and every offset after it
        // is short -- and the ';' ending an entity is not the ';' ending a declaration. Saying
        // nothing beats underlining the wrong run of somebody's file.
        Assert.Empty(Of("<rect style=\"font-family:&quot;A&quot;;stroke-width:abc\" />"));

        // Likewise where the scanner itself gives up.
        Assert.Empty(Of("<rect style=\"fill:&apos;\" />"));
    }

    [Fact]
    public void A_Style_Is_Placed_However_It_Was_Written()
    {
        var single = "<svg xmlns=\"http://www.w3.org/2000/svg\"><rect style='stroke-width:abc' /></svg>";
        var one = Assert.Single(SvgSourceDiagnostics.Analyse(single));

        Assert.Equal("abc", single.Substring(one.Start, one.Length));

        // Whitespace around the colon and across lines is the author's, not the value's.
        Assert.Equal("abc", Marked("<rect style=\"\n  stroke-width : abc ;\n  fill:red\n\" />"));
    }

    [Fact]
    public void The_Root_Is_A_Drawing_Like_Any_Other()
    {
        // Easy to lose: the outermost element is the one carrying the size, and a walk written to
        // visit children would never ask about it.
        var one = Assert.Single(
            SvgSourceDiagnostics.Analyse("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"abc\" />"));

        Assert.StartsWith("'width' cannot be set from 'abc'.", one.Message);
    }

    [Fact]
    public void An_Element_The_Parser_Does_Not_Know_Is_Asked_Nothing()
    {
        // It becomes an SvgUnknownElement, which accepts anything and so can be wrong about nothing.
        // Whether the name itself is a mistake is a question this does not answer.
        Assert.Empty(Of("<rekt width=\"abc\" />"));
        Assert.Empty(Of("<foo:bar xmlns:foo=\"urn:x\" width=\"abc\" />"));
    }

    [Fact]
    public void A_Converter_That_Refuses_Nothing_Reports_Nothing()
    {
        // The boundary worth knowing. A path builder reads 'd' as far as it makes sense and drops
        // the rest without complaint, so it converts nonsense as happily as a real path. Calling
        // that an error would mean deciding that unread input is one -- a rule the parser does not
        // have, and that this must not invent on its behalf.
        Assert.Empty(Of("<path d=\"M 1 zz\" />"));
        Assert.Empty(Of("<path d=\"QQQ\" />"));
    }

    [Fact]
    public void A_Document_That_Is_Not_Well_Formed_Is_Not_Guessed_At()
    {
        // Half-typed markup is the ordinary case in an editor, and saying which values are wrong in
        // a document nobody can parse would be an invention. Whether to report the markup itself is
        // a separate question this pass does not answer.
        Assert.Empty(SvgSourceDiagnostics.Analyse("<svg><rect width=\"abc\""));
    }

    [Fact]
    public void What_A_Converter_Refuses_Is_Reported_Beside_What_The_Declarations_Get_Wrong()
    {
        // Two passes, one list, in document order. A converter's verdict does not depend on the
        // symbol table, so a broken <e:code> block does not silence it.
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code><e:param name="tint" type="colour" /></e:code></defs>
              <rect width="abc" />
            </svg>
            """;

        var found = SvgSourceDiagnostics.Analyse(source);

        Assert.Equal(new[] { "\"colour\"", "\"abc\"" }, found.Select(d => source.Substring(d.Start, d.Length)));
    }

    /// <summary>
    /// Every drawing in the W3C suite, save the few whose values this library really does not apply.
    /// </summary>
    /// <remarks>
    /// The guard that matters. A source view that underlines valid documents is worse than one that
    /// underlines nothing, and no set of hand-written cases can stand in for 525 real drawings. The
    /// exceptions are named rather than counted so that a change which starts marking a good file
    /// fails here saying which one.
    /// </remarks>
    [Fact]
    public void The_W3C_Suite_Is_Marked_Only_Where_A_Value_Truly_Does_Not_Apply()
    {
        var directory = Path.Combine(
            "..", "..", "..", "..", "..",
            "externals", "W3C_SVG_11_TestSuite", "W3C_SVG_11_TestSuite", "svg");

        Assert.True(Directory.Exists(directory), $"The W3C suite is missing from '{directory}'. Run: git submodule update --init --recursive");

        // 'orient' in radians and '!important' in a presentation attribute: both are values this
        // parser does not take, so in both the drawing on screen is not what the file says.
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "styling-pres-01-t.svg",
            "types-dom-02-f.svg",
            "types-dom-05-b.svg",
            "types-dom-07-f.svg",
        };

        var read = 0;
        var marked = new List<string>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.svg").OrderBy(path => path, StringComparer.Ordinal))
        {
            string text;

            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }

            read++;

            var name = Path.GetFileName(file);

            if (SvgSourceDiagnostics.Analyse(text).Count > 0 && !known.Contains(name))
            {
                marked.Add(name);
            }
        }

        Assert.True(read > 500, $"Only {read} drawings were read; the suite looks incomplete.");
        Assert.Empty(marked);
    }
}
