using System;
using System.Linq;
using Xunit;

namespace Svg.Highlighting.UnitTests;

/// <summary>
/// What the checker says about a document, placed where a view can mark it.
/// </summary>
/// <remarks>
/// Nothing here decides what is an error: every message comes from the language's own checker, so
/// these assert placement, scoping and what is reported at all — not wording.
/// </remarks>
public class SvgSourceDiagnosticsTests
{
    private const string Declarations = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24">
          <defs>
            <e:code>
              <e:param name="hue" type="number" default="204" />
              <e:param name="tint" type="color" />
              <e:let name="primary">hsl(hue, 74%, 55%)</e:let>
            </e:code>
          </defs>
          BODY
        </svg>
        """;

    private static SvgSourceDiagnostic[] Of(string body)
        => SvgSourceDiagnostics.Analyse(Declarations.Replace("BODY", body)).ToArray();

    /// <summary>What a diagnostic is pointing at, which is the half a message does not say.</summary>
    private static string Marked(string body)
    {
        var source = Declarations.Replace("BODY", body);
        var one = Assert.Single(SvgSourceDiagnostics.Analyse(source));

        return source.Substring(one.Start, one.Length);
    }

    [Fact]
    public void A_Document_That_Is_Right_Has_Nothing_To_Say()
    {
        Assert.Empty(Of("<rect fill=\"{{ primary }}\" opacity=\"{{ hue / 360 }}\" />"));
    }

    [Fact]
    public void A_Name_Nothing_Declares_Is_Reported_Where_It_Is_Written()
    {
        // The point of the whole exercise: a typo says so while the file is being read, rather than
        // when the generator runs.
        Assert.Equal("sweeep", Marked("<rect opacity=\"{{ sweeep }}\" />"));
    }

    [Fact]
    public void A_Refusal_Marks_The_Piece_It_Stopped_On_Rather_Than_The_Whole_Expression()
    {
        // A single '=' is not an operator. Underlining a name or a symbol is legible; underlining
        // the expression around it says only that something in there is wrong.
        Assert.Equal("=", Marked("<rect opacity=\"{{ hue = 3 }}\" />"));
    }

    [Fact]
    public void A_Function_Given_The_Wrong_Arguments_Is_Reported()
    {
        var one = Assert.Single(Of("<rect fill=\"{{ hsl(hue) }}\" />"));

        Assert.Equal(SvgSourceSeverity.Error, one.Severity);
        Assert.NotEmpty(one.Message);
    }

    [Fact]
    public void An_Expression_Is_Checked_On_Its_Own_Terms_And_Not_Against_Its_Use()
    {
        // opacity="{{ tint }}" is a well-formed colour expression in a place that takes a number,
        // and nothing here says so: which attribute expects which type lives in the scene compiler.
        // Pinned rather than left silent, because it is the obvious next thing to want.
        Assert.Empty(Of("<rect opacity=\"{{ tint }}\" />"));
    }

    [Fact]
    public void A_Let_Sees_The_Ones_Before_It_And_Not_Itself()
    {
        // Lets resolve in order, so an earlier one is in scope and a later one is not.
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code>
                <e:param name="hue" type="number" default="0" />
                <e:let name="first">hue + 1</e:let>
                <e:let name="second">first * 2</e:let>
              </e:code></defs>
            </svg>
            """;

        Assert.Empty(SvgSourceDiagnostics.Analyse(source));

        var backwards = source
            .Replace("<e:let name=\"first\">hue + 1</e:let>", "<e:let name=\"first\">second + 1</e:let>");

        Assert.NotEmpty(SvgSourceDiagnostics.Analyse(backwards));
    }

    [Fact]
    public void A_Default_May_Not_Reach_What_The_Document_Declares()
    {
        // The code generator forbids it, because an ordering dependency between parameters would be
        // invisible in the document. Checking a default against the full table would accept what the
        // generator then rejects.
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code>
                <e:param name="a" type="number" default="1" />
                <e:param name="b" type="number" default="a + 1" />
              </e:code></defs>
            </svg>
            """;

        Assert.Equal("a", SvgSourceDiagnostics.Analyse(source).Select(d => Slice(source, d)).Single());
    }

    [Fact]
    public void A_Bad_Declaration_Is_Reported_Where_It_Is_Written_And_Nothing_Else()
    {
        // The block is what is wrong, so the block is what is marked. The undeclared 'nonsense'
        // below it is not reported: with the declaration refused, its parameter is missing from the
        // table and every use of the document's names would look undeclared too. A hundred of those
        // bury the one that is real.
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code><e:param name="tint" type="color" min="0" max="1" /></e:code></defs>
              <rect fill="{{ tint }}" opacity="{{ nonsense }}" />
            </svg>
            """;

        var one = Assert.Single(SvgSourceDiagnostics.Analyse(source));

        Assert.Contains("cannot carry min, max or step", one.Message);
        Assert.Equal("0", Slice(source, one));
    }

    [Fact]
    public void Every_Bad_Declaration_Is_Reported_Rather_Than_Only_The_First()
    {
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code>
                <e:param name="1t" type="number" />
                <e:param name="hue" type="colour" />
                <e:let name="w"></e:let>
              </e:code></defs>
            </svg>
            """;

        var found = SvgSourceDiagnostics.Analyse(source);

        // A mark is the piece the pane already draws, which is why two of these carry their quotes
        // and the range attributes below do not: a bound is split into the language, a name is not.
        Assert.Equal(3, found.Count);
        Assert.Equal(new[] { "\"1t\"", "\"colour\"", "e:let" }, found.Select(d => Slice(source, d)));
    }

    [Fact]
    public void A_Rule_About_Something_Left_Out_Marks_The_Declaration()
    {
        // A missing type has nothing of its own to point at, so the declaration missing it is
        // marked. Underlining nothing, or the whole line, both say less.
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code><e:param name="hue" /></e:code></defs>
            </svg>
            """;

        Assert.Equal("e:param", Slice(source, Assert.Single(SvgSourceDiagnostics.Analyse(source))));
    }

    [Theory]
    // What only numbers can settle, so it cannot be decided while the document is read.
    [InlineData("""min="5" max="1" """, "5", "greater than its max")]
    [InlineData("""min="0" max="1" step="0" """, "0", "greater than zero")]
    // A default that type-checks and still will not produce a value. clamp refuses a reversed range
    // by throwing something that is not the language's own exception, which must not escape.
    [InlineData("""default="clamp(2, 5, 1)" """, "clamp", "cannot be greater than")]
    public void A_Declaration_Is_Run_As_Well_As_Read(string attributes, string marked, string says)
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code><e:param name="hue" type="number" {attributes}/></e:code></defs>
            </svg>
            """;

        var one = Assert.Single(SvgSourceDiagnostics.Analyse(source));

        Assert.Contains(says, one.Message);
        Assert.Equal(marked, Slice(source, one));
    }

    [Fact]
    public void A_Bound_That_Will_Not_Read_Is_Reported_Once()
    {
        // The checker rejects it, and running it would reject it again for the same reason. One
        // mistake, one mark.
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code><e:param name="hue" type="number" min="1 +" max="9" /></e:code></defs>
            </svg>
            """;

        Assert.Single(SvgSourceDiagnostics.Analyse(source));
    }

    [Fact]
    public void A_Document_That_Is_Not_Well_Formed_Says_Where_It_Stopped()
    {
        // Silently contributing no declarations is what the reader does, because the SVG parser is
        // the authority on well-formedness. That leaves a source view showing a file with an unclosed
        // tag and nothing said about it, which is the one moment someone needs telling.
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code><e:param name="hue" type="number" /></e:code>
            </svg>
            """;

        Assert.True(Assert.Single(SvgSourceDiagnostics.Analyse(source)).Start > 0);
    }

    [Fact]
    public void A_Document_With_No_Expressions_Is_Not_Analysed_At_All()
    {
        Assert.Empty(SvgSourceDiagnostics.Analyse("<svg><rect fill=\"#fff\" /></svg>"));
        Assert.Empty(SvgSourceDiagnostics.Analyse(""));
        Assert.Empty(SvgSourceDiagnostics.Analyse(null));
    }

    private static string Slice(string source, SvgSourceDiagnostic diagnostic)
        => source.Substring(diagnostic.Start, diagnostic.Length);
}
