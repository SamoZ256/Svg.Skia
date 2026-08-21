using System;
using System.Linq;
using Xunit;

namespace Svg.Highlighting.UnitTests;

/// <summary>
/// The tokenizer, which is what the library is.
/// </summary>
/// <remarks>
/// These ran against a viewer before the splitting moved out of it, and needed a UI thread to say
/// anything about a pure function. They are plain facts now.
/// </remarks>
public class SvgSourceHighlighterTests
{
    [Theory]
    [InlineData("<svg><rect fill=\"#fff\" /></svg>")]
    [InlineData("<!-- a comment --><svg/>")]
    [InlineData("<?xml version=\"1.0\"?><svg><![CDATA[ raw < > text ]]></svg>")]
    [InlineData("<svg fill=\"{{ hsl(hue, 74%, 55%) }}\" />")]
    [InlineData("text before <svg attr='single' > and after")]
    [InlineData("<svg <<< unclosed attr=\"no end")]
    [InlineData("<!-- unterminated comment")]
    [InlineData("no markup at all")]
    [InlineData("")]
    public void Splitting_The_Source_Never_Loses_A_Character(string source)
    {
        // The pane shows what the file says. A highlighter that drops, reorders or invents a
        // character while colouring would quietly lie about the document.
        var tokens = SvgSourceHighlighter.Tokenize(source);

        Assert.Equal(source, string.Concat(tokens.Select(t => t.Text)));
    }

    [Fact]
    public void An_Expression_Is_Not_Just_Another_Attribute_Value()
    {
        // The reason for colouring at all: an XML grammar sees a string in the first attribute and
        // a string in the second, and cannot tell you that one of them is code.
        const string source = "<circle fill=\"{{ hsl(hue, 74%, 55%) }}\" r=\"10\" />";

        var tokens = SvgSourceHighlighter.Tokenize(source);

        Assert.Contains(tokens, t => t.Kind == SvgSourceTokenKind.ExpressionFunction && t.Text == "hsl");
        Assert.Contains(tokens, t => t.Kind == SvgSourceTokenKind.Element && t.Text == "circle");
        Assert.Contains(tokens, t => t.Kind == SvgSourceTokenKind.Attribute && t.Text == "fill");

        // The value that is only a value stays one, whole and uncoloured inside.
        Assert.Contains(tokens, t => t.Kind == SvgSourceTokenKind.Value && t.Text == "\"10\"");
        Assert.DoesNotContain(tokens, t =>
            t.Kind == SvgSourceTokenKind.Value && t.Text.Contains("hsl", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Let_Body_Is_Expression_Code_As_Much_As_A_Placeholder_Is()
    {
        // <e:let name="primary">hsl(hue, 74%, 55%)</e:let> is the same language as {{ … }}, and the
        // declarations everything else in the drawing refers to. As XML text it would be prose.
        var tokens = SvgSourceHighlighter.Tokenize(
            "<e:code><e:let name=\"primary\">hsl(hue, 74%, 55%)</e:let><e:param name=\"hue\" /></e:code>");

        Assert.Contains(tokens, t => t.Kind == SvgSourceTokenKind.ExpressionFunction && t.Text == "hsl");
        Assert.Contains(tokens, t => t.Kind == SvgSourceTokenKind.ExpressionNumber && t.Text == "74%");

        // The element that merely holds them is not code, and neither is a self-closing one.
        Assert.DoesNotContain(tokens, t =>
            t.Kind == SvgSourceTokenKind.Expression && t.Text.Contains("param", StringComparison.Ordinal));
    }

    private static string TextOf(string source, SvgSourceTokenKind kind)
        => string.Concat(SvgSourceHighlighter.Tokenize(source).Where(t => t.Kind == kind).Select(t => t.Text));

    private static SvgSourceToken[] Of(string source, SvgSourceTokenKind kind)
        => SvgSourceHighlighter.Tokenize(source).Where(t => t.Kind == kind).ToArray();

    [Fact]
    public void A_Placeholder_Is_Split_Into_The_Language_Rather_Than_Left_As_One_Piece()
    {
        const string source = "<circle fill=\"{{ hsl(hue, 74%, 55%) }}\" />";

        Assert.Equal("hsl", TextOf(source, SvgSourceTokenKind.ExpressionFunction));
        Assert.Equal("hue", TextOf(source, SvgSourceTokenKind.ExpressionIdentifier));
        Assert.Equal("(,,)", TextOf(source, SvgSourceTokenKind.ExpressionPunctuation));

        // The fences stay expression-coloured; the code between them is what gets taken apart.
        Assert.Contains(Of(source, SvgSourceTokenKind.Expression), t => t.Text == "{{");
        Assert.Contains(Of(source, SvgSourceTokenKind.Expression), t => t.Text == "}}");
    }

    [Fact]
    public void A_Percent_Belongs_To_The_Number_It_Follows()
    {
        // The language reads 55% as a fraction, so that a % b stays unambiguous. Colouring the sign
        // as an operator would say the opposite of what the compiler does with it.
        var numbers = Of("<rect opacity=\"{{ 55% + 0.25 }}\" />", SvgSourceTokenKind.ExpressionNumber);

        Assert.Equal(new[] { "55%", "0.25" }, numbers.Select(t => t.Text));
        Assert.Equal("+", TextOf("<rect opacity=\"{{ 55% + 0.25 }}\" />", SvgSourceTokenKind.ExpressionOperator));
    }

    [Fact]
    public void Word_Forms_Of_The_Operators_Are_Not_Names()
    {
        // and/or/not/lt/le/gt/ge/eq/ne exist because XML escaping makes < and && awkward inside an
        // attribute. They lex to the operators they spell, not to identifiers.
        const string source = "<rect visibility=\"{{ level gt 3 and not hidden }}\" />";

        Assert.Equal("gtandnot", TextOf(source, SvgSourceTokenKind.ExpressionKeyword));
        Assert.Equal("levelhidden", TextOf(source, SvgSourceTokenKind.ExpressionIdentifier));
    }

    [Fact]
    public void A_Let_Body_Is_Split_Like_A_Placeholder()
    {
        const string source = "<e:code><e:let name=\"primary\">mix(tint, #3fb5b5, tau)</e:let></e:code>";

        Assert.Equal("mix", TextOf(source, SvgSourceTokenKind.ExpressionFunction));
        Assert.Equal("#3fb5b5", TextOf(source, SvgSourceTokenKind.ExpressionColor));
        Assert.Equal("tau", TextOf(source, SvgSourceTokenKind.ExpressionConstant));
        Assert.Equal("tint", TextOf(source, SvgSourceTokenKind.ExpressionIdentifier));
    }

    [Fact]
    public void An_Expression_The_Language_Refuses_Colours_As_Far_As_It_Read()
    {
        // A single '=' is not an operator, and the lexer says so by refusing. Someone reading the
        // file to find that out is exactly who has the pane open.
        const string source = "<rect opacity=\"{{ hsl(hue) = 3 }}\" />";

        Assert.Equal("hsl", TextOf(source, SvgSourceTokenKind.ExpressionFunction));
        Assert.Equal("hue", TextOf(source, SvgSourceTokenKind.ExpressionIdentifier));

        // Everything from the refusal on is left plain rather than guessed at.
        Assert.Contains(Of(source, SvgSourceTokenKind.Expression), t => t.Text.Contains('='));
    }

    [Theory]
    [InlineData("<rect fill=\"{{ hsl(hue, 74%, 55%) }}\" />")]
    [InlineData("<e:let name=\"a\">mix(x, y, 0.5)</e:let>")]
    [InlineData("<rect fill=\"{{ = }}\" />")]
    [InlineData("<rect fill=\"{{ unterminated\" />")]
    [InlineData("<e:let name=\"a\">1 % 2 @ 3</e:let>")]
    [InlineData("<e:param name=\"h\" type=\"number\" default=\"tau / 4\" max=\"100%\" />")]
    [InlineData("<e:param default=\"unterminated />")]
    public void Splitting_The_Language_Never_Loses_A_Character_Either(string source)
    {
        Assert.Equal(source, string.Concat(SvgSourceHighlighter.Tokenize(source).Select(t => t.Text)));
    }

    [Fact]
    public void A_Declaration_Carries_Expressions_In_Its_Attributes()
    {
        // default, min, max and step are each an expression, so max="tau" and step="1/60" are code
        // in a place that looks like an ordinary attribute value.
        const string source = "<e:param name=\"hue\" type=\"number\" default=\"tau / 4\" min=\"0\" max=\"100%\" step=\"1/60\" />";

        Assert.Equal("tau", TextOf(source, SvgSourceTokenKind.ExpressionConstant));
        Assert.Equal(new[] { "4", "0", "100%", "1", "60" }, Of(source, SvgSourceTokenKind.ExpressionNumber).Select(t => t.Text));
        // Two divisions: tau / 4 and 1/60. The slash closing the tag is markup, not an operator.
        Assert.Equal("//", TextOf(source, SvgSourceTokenKind.ExpressionOperator));

        // What identifies the parameter is not code, and the quotes still belong to the markup.
        Assert.Contains(Of(source, SvgSourceTokenKind.Value), t => t.Text == "\"hue\"");
        Assert.Contains(Of(source, SvgSourceTokenKind.Value), t => t.Text == "\"number\"");
        Assert.Empty(Of(source, SvgSourceTokenKind.ExpressionIdentifier));
    }

    [Fact]
    public void True_And_False_Are_Values_Rather_Than_Names()
    {
        // They lex as identifiers, but the parser reads them as boolean literals, and a reader
        // should see them as the values they are rather than as something declared somewhere.
        const string source = "<e:param name=\"badge\" type=\"boolean\" default=\"true\" />";

        Assert.Equal("true", TextOf(source, SvgSourceTokenKind.ExpressionConstant));
        Assert.Empty(Of(source, SvgSourceTokenKind.ExpressionIdentifier));
    }

    [Fact]
    public void The_Same_Attribute_Names_Elsewhere_Are_Ordinary_Values()
    {
        // Only a declaration's attributes are code. A rect with a min is a rect with a string.
        const string source = "<rect default=\"tau\" min=\"0\" />";

        Assert.Empty(Of(source, SvgSourceTokenKind.ExpressionConstant));
        Assert.Empty(Of(source, SvgSourceTokenKind.ExpressionNumber));
        Assert.Contains(Of(source, SvgSourceTokenKind.Value), t => t.Text == "\"tau\"");
    }

}
