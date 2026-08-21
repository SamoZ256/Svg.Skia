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
        // The reason for colouring at all: an XML grammar sees a string here.
        var tokens = SvgSourceHighlighter.Tokenize("<circle fill=\"{{ hsl(hue, 74%, 55%) }}\" r=\"10\" />");

        Assert.Contains(tokens, t =>
            t.Kind == SvgSourceTokenKind.Expression && t.Text == "{{ hsl(hue, 74%, 55%) }}");

        Assert.Contains(tokens, t => t.Kind == SvgSourceTokenKind.Element && t.Text == "circle");
        Assert.Contains(tokens, t => t.Kind == SvgSourceTokenKind.Attribute && t.Text == "fill");
        Assert.Contains(tokens, t => t.Kind == SvgSourceTokenKind.Value && t.Text == "\"10\"");
    }

    [Fact]
    public void A_Let_Body_Is_Expression_Code_As_Much_As_A_Placeholder_Is()
    {
        // <e:let name="primary">hsl(hue, 74%, 55%)</e:let> is the same language as {{ … }}, and the
        // declarations everything else in the drawing refers to. As XML text it would be prose.
        var tokens = SvgSourceHighlighter.Tokenize(
            "<e:code><e:let name=\"primary\">hsl(hue, 74%, 55%)</e:let><e:param name=\"hue\" /></e:code>");

        Assert.Contains(tokens, t =>
            t.Kind == SvgSourceTokenKind.Expression && t.Text == "hsl(hue, 74%, 55%)");

        // The element that merely holds them is not code, and neither is a self-closing one.
        Assert.DoesNotContain(tokens, t =>
            t.Kind == SvgSourceTokenKind.Expression && t.Text.Contains("param", StringComparison.Ordinal));
    }
}
