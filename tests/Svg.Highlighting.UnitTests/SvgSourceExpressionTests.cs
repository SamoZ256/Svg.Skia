// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Linq;
using Xunit;

namespace Svg.Highlighting.UnitTests;

/// <summary>
/// Splitting one expression with no document around it.
/// </summary>
/// <remarks>
/// The same kinds the source view gives that text inside a file, so a box beside the pane and the
/// pane itself cannot disagree about what a piece of an expression is.
/// </remarks>
public class SvgSourceExpressionTests
{
    private static (string Text, SvgSourceTokenKind Kind)[] Of(string? text)
        => SvgSourceHighlighter.Expression(text).Select(t => (t.Text, t.Kind)).ToArray();

    private static SvgSourceTokenKind Kind(string body, string piece)
        => Of(body).First(t => t.Text == piece).Kind;

    [Fact]
    public void Nothing_Splits_Into_Nothing()
    {
        Assert.Empty(Of(null));
        Assert.Empty(Of(string.Empty));
    }

    [Fact]
    public void Every_Piece_Of_A_Body_Is_Named()
    {
        Assert.Equal(
            new[]
            {
                ("hsl", SvgSourceTokenKind.ExpressionFunction),
                ("(", SvgSourceTokenKind.ExpressionPunctuation),
                ("hue", SvgSourceTokenKind.ExpressionIdentifier),
                (",", SvgSourceTokenKind.ExpressionPunctuation),
                (" ", SvgSourceTokenKind.Expression),
                ("74%", SvgSourceTokenKind.ExpressionNumber),
                (",", SvgSourceTokenKind.ExpressionPunctuation),
                (" ", SvgSourceTokenKind.Expression),
                ("55%", SvgSourceTokenKind.ExpressionNumber),
                (")", SvgSourceTokenKind.ExpressionPunctuation),
            },
            Of("hsl(hue, 74%, 55%)"));
    }

    [Fact]
    public void What_Belongs_To_The_Language_Is_Told_From_What_The_Document_Named()
    {
        // A constant is recognisable anywhere; whether `wave` is a let, a parameter or a typo needs
        // the block, and saying so is the diagnostics' job rather than the colour's.
        Assert.Equal(SvgSourceTokenKind.ExpressionConstant, Kind("tau * wave", "tau"));
        Assert.Equal(SvgSourceTokenKind.ExpressionIdentifier, Kind("tau * wave", "wave"));
    }

    [Fact]
    public void A_Colour_Literal_Is_Its_Own_Kind()
        => Assert.Contains(Of("mix(tint, #000000, 0.4)"), t => t.Kind == SvgSourceTokenKind.ExpressionColor);

    [Fact]
    public void A_String_Literal_Is_Its_Own_Kind()
    {
        // Quotes and escapes included: the token's span is the literal as written, so a highlighter
        // that took the resolved value would underline the wrong run.
        Assert.Equal(SvgSourceTokenKind.ExpressionString, Kind("theme == 'dark'", "'dark'"));
        Assert.Equal(SvgSourceTokenKind.ExpressionString, Kind(@"theme == 'it\'s'", @"'it\'s'"));
    }

    [Fact]
    public void A_Word_Operator_Is_Told_From_A_Name()
    {
        // `gt` lexes to the operator `>` spells, so only the text tells them apart.
        Assert.Equal(SvgSourceTokenKind.ExpressionKeyword, Kind("wave gt 0.5", "gt"));
    }

    [Fact]
    public void A_Body_That_Stops_Lexing_Keeps_What_Came_Before_It()
    {
        var tokens = Of("hue / @@@");

        Assert.Equal(SvgSourceTokenKind.ExpressionIdentifier, tokens[0].Kind);

        // The remainder is still there to read, uncoloured. Colouring is not where a mistake is
        // reported -- the row's own diagnostic says it, in the language's words.
        Assert.Equal("hue / @@@", string.Concat(tokens.Select(t => t.Text)));
    }

    [Fact]
    public void Nothing_Is_Lost()
    {
        const string body = "clamp(t * tau, 0, 1) + lerp(0.45, 1, wave)";

        Assert.Equal(body, string.Concat(SvgSourceHighlighter.Expression(body).Select(t => t.Text)));
    }
}
