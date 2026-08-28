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
/// Every message comes from the converter the attribute uses, so these assert placement and, above
/// all, what is <em>not</em> asked: a wave under a value the drawing used correctly is worse than
/// no wave.
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
        // Renders as a zero-width rectangle and says nothing: SetValue catches the converter's
        // throw, warns to Trace and returns true.
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
        // The gradient is really there: a reference that resolves to nothing is a separate report,
        // and this case is about the converters.
        Assert.Empty(Of("<defs><linearGradient id=\"g\" /></defs>"
                        + "<circle cx=\"5%\" cy=\"5em\" r=\"3pt\" stroke=\"url(#g)\" stroke-width=\"2\" />"));
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
        // Code the extension's checker has already read; converting the braces would refuse every
        // drawing that uses it.
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
        // Unlifted, so the braces stay and the converter refuses them — a true refusal for a
        // misleading reason.
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
        foreach (var supported in new[]
                 {
                     "fill", "stroke", "stop-color", "flood-color", "lighting-color",
                     "opacity", "fill-opacity", "stroke-opacity", "stop-opacity",
                     "visibility", "display"
                 })
        {
            Assert.Contains(supported, one.Message, StringComparison.Ordinal);
        }
        // The braces rather than the value: the pane colours a placeholder as code wherever it is
        // written, and they are also the part doing nothing here.
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
        // The same converter, only later: AddStyle stages them and FlushStyles hands them back.
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
        // Entities arrive resolved, so offsets after one are short and the ';' ending an entity is
        // not the ';' ending a declaration.
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
    public void An_Id_Used_Twice_Is_A_Warning_On_The_Later_One()
    {
        // The first keeps the name -- that is what the id manager does -- so the first is not what
        // is wrong. A warning, because the drawing opens and draws one of them.
        var one = Assert.Single(Of("<rect id=\"a\" /><rect id=\"a\" />"));

        Assert.Equal(SvgSourceSeverity.Warning, one.Severity);
        Assert.Equal("The id 'a' is already used in this drawing, and a reference to it finds the first.", one.Message);
        Assert.Equal("\"a\"", Marked("<rect id=\"a\" /><rect id=\"a\" />"));
    }

    [Fact]
    public void Every_Repeat_After_The_First_Is_Reported()
    {
        Assert.Equal(2, Of("<rect id=\"a\" /><rect id=\"a\" /><rect id=\"a\" />").Length);
    }

    [Fact]
    public void A_Repeated_Id_Does_Not_Also_Break_What_Refers_To_It()
    {
        // One warning, not a warning and an unresolved reference: the name is still there to find.
        var one = Assert.Single(
            Of("<defs><linearGradient id=\"g\" /><linearGradient id=\"g\" /></defs><rect fill=\"url(#g)\" />"));

        Assert.Equal(SvgSourceSeverity.Warning, one.Severity);
    }

    [Theory]
    [InlineData("<rect id=\"a\" /><rect id=\"b\" />")]
    [InlineData("<rect /><rect />")]
    // Nothing was named, so nothing was named twice.
    [InlineData("<rect id=\"\" /><rect id=\"\" />")]
    public void Ids_That_Differ_Are_Not_Reported(string body)
    {
        Assert.Empty(Of(body));
    }

    [Fact]
    public void A_Reference_To_An_Id_The_Drawing_Does_Not_Have_Is_Reported()
    {
        // The reference resolves to null, so "broken" and "absent" reach the scene graph alike.
        foreach (var body in new[]
        {
            "<rect clip-path=\"url(#gone)\" />",
            "<rect fill=\"url(#gone)\" />",
            "<rect filter=\"url(#gone)\" />",
            "<use href=\"#gone\" />",
        })
        {
            var one = Assert.Single(Of(body));

            Assert.Equal("Nothing in this drawing has the id 'gone'.", one.Message);
            Assert.Equal(SvgSourceSeverity.Error, one.Severity);
        }
    }

    [Fact]
    public void An_Id_Is_Looked_For_Anywhere_In_The_Drawing()
    {
        // Declared after the attribute that names it, which is ordinary in a file that puts its
        // <defs> at the bottom, so the ids are read before anything is checked.
        Assert.Empty(Of("<rect fill=\"url(#g)\" /><defs><linearGradient id=\"g\" /></defs>"));
        Assert.Empty(Of("<defs><linearGradient id=\"g\" /></defs><rect fill=\"url(#g)\" />"));
    }

    [Theory]
    // A paint that carries a fallback is not broken: SVG says the fallback is used, and this parser
    // implements that.
    [InlineData("<rect fill=\"url(#gone) none\" />")]
    [InlineData("<rect fill=\"url(#gone) green\" />")]
    // The fallback ends in a parenthesis of its own, which is why the url is closed by looking for
    // its own bracket rather than the end of the value.
    [InlineData("<rect fill=\"url(#gone) green icc-color(acmecmyk, 0.11, 0.48)\" />")]
    // A list, for the same reason.
    [InlineData("<rect filter=\"url(#gone) url(#alsogone)\" />")]
    [InlineData("<rect filter=\"url(#gone) grayscale()\" />")]
    // Another file, which this pass cannot open to check.
    [InlineData("<rect fill=\"url(other.svg#g)\" />")]
    // Not a reference at all.
    [InlineData("<rect fill=\"#ff0000\" />")]
    [InlineData("<a href=\"https://example.invalid/\"><rect /></a>")]
    // No closing bracket, so there is no id to be sure of.
    [InlineData("<rect fill=\"url(#gone\" />")]
    public void What_Names_No_Id_Of_This_Drawing_Is_Not_Reported(string body)
    {
        Assert.Empty(Of(body));
    }

    [Fact]
    public void A_Reference_Is_Read_However_It_Was_Quoted()
    {
        Assert.Equal("Nothing in this drawing has the id 'gone'.",
            Assert.Single(Of("<rect fill=\"url('#gone')\" />")).Message);

        // xlink:href is the same reference under an older spelling.
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
              <use xlink:href="#gone" />
            </svg>
            """;

        Assert.Equal("Nothing in this drawing has the id 'gone'.",
            Assert.Single(SvgSourceDiagnostics.Analyse(source)).Message);
    }

    [Fact]
    public void A_Drawing_That_Runs_Code_Is_Not_Told_What_It_Will_Have()
    {
        // A script can make an id after the document is read, so what exists by the time anything
        // is drawn is not a question the text can answer.
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <script>/* builds #made */</script>
              <rect fill="url(#made)" />
            </svg>
            """;

        Assert.Empty(SvgSourceDiagnostics.Analyse(source));
    }

    [Fact]
    public void An_Element_The_Parser_Does_Not_Know_Is_A_Warning_On_Its_Name()
    {
        // An SvgUnknownElement: read, kept, drawn by nothing. A warning, since the drawing opens.
        var one = Assert.Single(Of("<rekt width=\"abc\" />"));

        Assert.Equal(SvgSourceSeverity.Warning, one.Severity);
        Assert.Equal("rekt", Marked("<rekt width=\"abc\" />"));

        // Its own attributes are not asked about -- there is no element to ask -- which is why the
        // bad width above is not a second diagnostic.
        Assert.StartsWith("'rekt' is not an element this renderer knows", one.Message);
    }

    [Fact]
    public void A_Real_Element_Inside_An_Unknown_One_Is_Still_A_Real_Element()
    {
        var found = Of("<rekt><rect width=\"abc\" /></rekt>");

        Assert.Equal(2, found.Length);
        Assert.Equal(SvgSourceSeverity.Warning, found[0].Severity);
        Assert.Equal(SvgSourceSeverity.Error, found[1].Severity);
    }

    [Fact]
    public void An_Element_Written_In_Another_Namespace_Is_Not_This_Parsers_Business()
    {
        Assert.Empty(Of("<foo:bar xmlns:foo=\"urn:x\" width=\"abc\" />"));
    }

    [Fact]
    public void Style_Misses_The_Table_And_Is_Still_Used()
    {
        // The one name that is not in the element table and is not a mistake: the loader picks the
        // unknown elements of that name back out and reads them as stylesheets.
        Assert.Empty(Of("<style>rect { fill: red }</style>"));
    }

    [Fact]
    public void An_Element_This_Renderer_Does_Not_Implement_Reads_The_Same_As_A_Typo()
    {
        // <view> is real SVG 1.1 and the table cannot tell it from a misspelling, so the wording
        // says what this renderer knows rather than what SVG defines.
        var one = Assert.Single(Of("<view viewBox=\"0 0 1 1\" />"));

        Assert.Equal(SvgSourceSeverity.Warning, one.Severity);
    }

    [Fact]
    public void A_Converter_That_Refuses_Nothing_Reports_Nothing()
    {
        // A path builder reads 'd' as far as it makes sense and drops the rest, so nonsense
        // converts as happily as a real path. Calling that an error would invent a rule.
        Assert.Empty(Of("<path d=\"M 1 zz\" />"));
        Assert.Empty(Of("<path d=\"QQQ\" />"));
    }

    [Fact]
    public void A_Document_That_Is_Not_Well_Formed_Is_Not_Guessed_At()
    {
        // Half-typed markup is the ordinary case in an editor, and saying which values are wrong in
        // a document nobody can parse would be an invention. The markup itself is now reported --
        // that is the one thing there is to say -- and `abc` is not, though it would be the moment
        // the author closes the tag.
        var one = Assert.Single(SvgSourceDiagnostics.Analyse("<svg><rect width=\"abc\""));

        Assert.DoesNotContain("abc", one.Message);
    }

    [Fact]
    public void A_Drawing_Whose_Shapes_Are_Entities_Is_Read_The_Way_The_Loader_Reads_It()
    {
        // The suite declares shapes in an internal subset and uses them by reference. Ignoring the
        // DTD would make every one of those an undeclared entity in a file that opens perfectly, so
        // this pass reads with the settings SvgDocument uses rather than stricter ones.
        Assert.Empty(SvgSourceDiagnostics.Analyse("""
            <?xml version="1.0"?>
            <!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN"
              "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd" [
              <!ENTITY Shape "<rect width='10' height='10' />">
            ]>
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">&Shape;</svg>
            """));
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

            // References the suite makes on purpose and leaves unresolved -- #notthere, #unknown,
            // #invalidlink, #bad-link -- because how a renderer handles them is what is being tested.
            "filters-felem-01-b.svg",
            "masking-path-08-b.svg",
            "masking-path-10-b.svg",
            "pservers-pattern-07-f.svg",
            "pservers-pattern-08-f.svg",
            "pservers-pattern-09-f.svg",
            "struct-use-12-f.svg",
            "text-altglyph-02-b.svg",
            "text-altglyph-03-b.svg",
        };

        // Elements of SVG 1.1 this renderer does not implement. Kept apart from the list above
        // because they are a different claim: nothing is wrong with these drawings, and what the
        // warning says is that this renderer will not draw part of them.
        var unimplemented = new HashSet<string>(StringComparer.Ordinal)
        {
            "color-prof-01-f.svg",
            "interact-cursor-01-f.svg",
            "linking-uri-01-b.svg",

            // And drawings that use one id twice, which the suite does by accident rather than to
            // test anything: every reference to the name finds the first of them.
            "animate-elem-24-t.svg",
            "animate-pservers-grad-01-b.svg",
            "filters-light-05-f.svg",
            "masking-intro-01-f.svg",
            "struct-image-12-b.svg",
            "struct-use-12-f.svg",
        };

        var read = 0;
        var errors = new List<string>();
        var warned = new List<string>();

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
            var found = SvgSourceDiagnostics.Analyse(text);

            if (found.Any(diagnostic => diagnostic.Severity == SvgSourceSeverity.Error) && !known.Contains(name))
            {
                errors.Add(name);
            }

            if (found.Any(diagnostic => diagnostic.Severity == SvgSourceSeverity.Warning))
            {
                warned.Add(name);
            }
        }

        Assert.True(read > 500, $"Only {read} drawings were read; the suite looks incomplete.");

        // The invariant that matters, and the strict one: nothing valid is called wrong.
        Assert.Empty(errors);

        // Warnings are named too, so a change that starts warning about a drawing nobody expected
        // fails here saying which.
        Assert.Equal(unimplemented.OrderBy(name => name, StringComparer.Ordinal), warned);
    }
}
