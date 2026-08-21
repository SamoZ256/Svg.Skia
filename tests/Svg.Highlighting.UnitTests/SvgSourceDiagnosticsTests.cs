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
    public void A_Declarations_Block_That_Cannot_Be_Read_Reports_Nothing_Here()
    {
        // Every name would look undeclared, burying the one error that matters — which the
        // declaration reader reports on its own.
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code><e:param name="tint" type="color" min="0" max="1" /></e:code></defs>
              <rect fill="{{ tint }}" opacity="{{ nonsense }}" />
            </svg>
            """;

        Assert.Empty(SvgSourceDiagnostics.Analyse(source));
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
