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
    public void An_Expression_Is_Checked_Against_What_Its_Attribute_Will_Do_With_It()
    {
        // opacity="{{ tint }}" is a well-formed colour expression in a place that takes a number.
        // This used to be pinned as silent, on the grounds that which attribute expects which type
        // lived in the scene compiler -- but both back ends already refuse it as they read the
        // document, so the answer only had to be asked for earlier.
        var one = Assert.Single(Of("<rect opacity=\"{{ tint }}\" />"));

        // Worded by the language, so the pane, the emitter and the renderer say the same sentence.
        Assert.Equal(
            "An opacity expression must be a number expression, but this one is a colour.",
            one.Message);
    }

    [Fact]
    public void Every_Attribute_An_Expression_Can_Drive_Knows_What_It_Wants()
    {
        Assert.Equal("A paint expression must be a colour expression, but this one is a number.",
            Assert.Single(Of("<rect fill=\"{{ hue }}\" />")).Message);

        Assert.Equal("A paint expression must be a colour expression, but this one is a number.",
            Assert.Single(Of("<rect stroke=\"{{ hue }}\" />")).Message);

        Assert.Equal("A visibility expression must be a boolean expression, but this one is a number.",
            Assert.Single(Of("<rect visibility=\"{{ hue }}\" />")).Message);

        // And the ones that match say nothing.
        Assert.Empty(Of("<rect fill=\"{{ primary }}\" stroke=\"{{ tint }}\" opacity=\"{{ hue / 360 }}\" visibility=\"{{ hue gt 1 }}\" />"));
        Assert.Empty(Of("<stop stop-color=\"{{ tint }}\" />"));
    }

    [Fact]
    public void An_Expression_In_A_Style_Declaration_Is_Checked_Against_Its_Property()
    {
        // The value of a style attribute is a list, so what a placeholder in it may evaluate to is
        // decided by the declaration it sits in and not by "style", which decides nothing.
        Assert.Equal("A paint expression must be a colour expression, but this one is a number.",
            Assert.Single(Of("<rect style=\"fill: {{ hue }}\" />")).Message);

        Assert.Equal("A visibility expression must be a boolean expression, but this one is a number.",
            Assert.Single(Of("<rect style=\"stroke: #000; visibility: {{ hue }}\" />")).Message);

        // And the ones that match say nothing.
        Assert.Empty(Of("<rect style=\"fill: {{ primary }}; opacity: {{ hue / 360 }}\" />"));
    }

    [Fact]
    public void What_Is_Wrong_With_An_Expression_Is_Said_Before_What_It_Evaluates_To_Is()
    {
        // A name nothing declares is reported as that rather than as a type, because the checker
        // has to read the expression before it can have a type to compare.
        Assert.Contains("sweeep", Assert.Single(Of("<rect opacity=\"{{ sweeep }}\" />")).Message);
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
    public void A_Document_With_Nothing_Wrong_With_It_Has_Nothing_To_Say()
    {
        // This used to say that a document with no expressions was not analysed at all, which was
        // the whole cost of the pass on an ordinary drawing. It is now read for what its attribute
        // values convert to as well, so what is pinned here is the answer rather than the work.
        Assert.Empty(SvgSourceDiagnostics.Analyse("<svg><rect fill=\"#fff\" /></svg>"));
        Assert.Empty(SvgSourceDiagnostics.Analyse(""));
        Assert.Empty(SvgSourceDiagnostics.Analyse(null));
    }

    [Fact]
    public void A_Mark_Never_Begins_On_A_Space()
    {
        // A rule about a value as a whole reports position zero, which here is the gap the author
        // left before writing it. A one-space underline is a mark nobody can see.
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code><e:param name="tint" type="color" default=" 1 " /></e:code></defs>
            </svg>
            """;

        var one = Assert.Single(SvgSourceDiagnostics.Analyse(source));

        Assert.Equal("1", Slice(source, one));
    }

    [Fact]
    public void An_Undeclared_Prefix_Is_Reported_Rather_Than_Its_Consequences()
    {
        // The case this rule exists for. Writing <e:code> without declaring the prefix is the
        // ordinary way to add expressions to a drawing that already exists, and it leaves the file
        // with no `https://svg.skia/expr/1.0` in it anywhere -- so the declarations reader, which
        // looks for exactly that string before it parses anything, concludes there is nothing here
        // to read. What used to come back was every name in every expression reported as
        // undeclared, and no word about the prefix.
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs><e:code>
                <e:param name="hue" type="number" default="1" />
                <e:let name="primary">hue + 1</e:let>
              </e:code></defs>
              <rect fill="{{ primary }}" />
            </svg>
            """;

        var one = Assert.Single(SvgSourceDiagnostics.Analyse(source));

        Assert.Equal("e:code", Slice(source, one));
        Assert.Contains("undeclared prefix", one.Message);
    }

    [Fact]
    public void A_Document_That_Will_Not_Parse_Is_Reported_Once_And_Only_Once()
    {
        // Both passes can see this one: the text names the namespace, so the declarations reader
        // parses too and refuses for the same reason. Two marks in the same place saying the same
        // sentence is the regression this guards.
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <g>
              <rect fill="{{ hue }}" />
            </svg>
            """;

        Assert.Single(SvgSourceDiagnostics.Analyse(source));
    }

    [Fact]
    public void What_A_Document_That_Parses_Reports_Is_Unchanged()
    {
        // The well-formed paths are not routed through the new branch: a bad value still reports,
        // and so does a bad declaration.
        var value = Assert.Single(SvgSourceDiagnostics.Analyse(
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"abc\" /></svg>"));

        Assert.StartsWith("'width' cannot be set from 'abc'.", value.Message);

        Assert.NotEmpty(SvgSourceDiagnostics.Analyse(Declarations.Replace(
            "<e:param name=\"tint\" type=\"color\" />",
            "<e:param name=\"tint\" type=\"color\" min=\"1\" />")));
    }

    [Fact]
    public void Entities_Are_Read_By_Both_Passes_Or_One_Contradicts_The_Other()
    {
        // The two readers have to agree about what a document is. This one declares its shape as an
        // entity and its parameters in the same file: read the entities and it is fine, ignore them
        // and every use of one is undeclared -- and the pass that says whether a document parses at
        // all would be calling it well-formed while the pass beside it refused the same text.
        Assert.Empty(SvgSourceDiagnostics.Analyse("""
            <?xml version="1.0"?>
            <!DOCTYPE svg [ <!ENTITY Shape "<rect width='10' height='10' />"> ]>
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code><e:param name="tint" type="color" default="#0f0" /></e:code></defs>
              &Shape;
              <circle fill="{{ tint }}" r="4" />
            </svg>
            """));
    }

    // ---- an expression as the file holds it, which is not as the language reads it ----

    [Theory]
    [InlineData("hue &lt; 100")]
    [InlineData("hue &gt; 100")]
    [InlineData("hue &gt;= 100 &amp;&amp; hue &lt; 200")]
    [InlineData("hue &gt; 100 ? 1 : 0")]
    public void An_Escaped_Comparison_Is_The_Comparison_And_Not_An_Ampersand(string body)
    {
        // A bare < opens a tag, so a document holding `hue < 100` can only spell it this way, and
        // a writer that escapes it has no choice either. Lexing the raw span stops at the ampersand
        // and reports a broken `&&` -- against text nobody typed, on a line that is perfectly good.
        Assert.Empty(Of($"<e:code xmlns:e=\"https://svg.skia/expr/1.0\"><e:let name=\"cold\">{body}</e:let></e:code>"));
    }

    [Fact]
    public void An_Escaped_Placeholder_Is_Read_The_Same_Way()
    {
        // The other half: an attribute escapes > as well as <, so both reach the file encoded.
        Assert.Empty(Of("<rect opacity=\"{{ hue &gt; 100 ? 1 : 0.5 }}\" />"));
    }

    [Fact]
    public void An_Escaped_Declaration_Attribute_Is_Read_The_Same_Way()
    {
        // The third site, and the one this repository's own writer escapes > in: an attribute value
        // takes all four of the set, so a bound written from a form arrives encoded.
        Assert.Empty(SvgSourceDiagnostics.Analyse("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="204" min="0" max="1 &gt; 2 ? 100 : 360" />
                </e:code>
              </defs>
            </svg>
            """));
    }

    [Fact]
    public void A_Mistake_Inside_An_Escaped_Expression_Is_Marked_Where_It_Was_Written()
    {
        // Decoding shifts every position after an entity, so a rule reporting where it stopped is
        // talking about text that is shorter than the file's. Marking `nope` rather than something
        // four characters to its left is the whole reason the offsets are carried.
        Assert.Equal(
            "nope",
            Marked("<e:code xmlns:e=\"https://svg.skia/expr/1.0\"><e:let name=\"cold\">hue &lt; nope</e:let></e:code>"));
    }

    private static string Slice(string source, SvgSourceDiagnostic diagnostic)
        => source.Substring(diagnostic.Start, diagnostic.Length);
}
