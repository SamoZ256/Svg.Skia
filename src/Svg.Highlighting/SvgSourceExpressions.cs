// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using Svg.Expressions;

namespace Svg.Highlighting;

/// <summary>
/// Colours expression code by asking the language what it says.
/// </summary>
/// <remarks>
/// <para>
/// Through <c>Svg.Expressions</c>' own lexer rather than a second description of the language,
/// which would drift from it in ways nothing would catch. Two of them are already here: a percent
/// sign is a <em>suffix on a number literal</em> and never an operator, so that <c>55%</c> reads as
/// a fraction without making <c>a % b</c> ambiguous; and <c>and</c>, <c>or</c>, <c>not</c>,
/// <c>lt</c>, <c>le</c>, <c>gt</c>, <c>ge</c>, <c>eq</c> and <c>ne</c> are word forms of the
/// symbolic operators, because XML escaping makes <c>&lt;</c> and <c>&amp;&amp;</c> awkward to
/// author inside an attribute. A tokenizer written here would have coloured the first as an
/// operator and the second as names.
/// </para>
/// <para>
/// The lexer refuses malformed input, and a source view has to colour a file precisely when someone
/// is working out what is wrong with it. So a failure colours up to where the language stopped
/// reading and leaves the remainder plain, and the position it stopped at is what a diagnostic will
/// later underline.
/// </para>
/// </remarks>
internal static class SvgSourceExpressions
{
    /// <summary>Adds a <c>{{ … }}</c> placeholder: its fences, and the code between them.</summary>
    public static void Placeholder(
        List<SvgSourceToken> tokens,
        string source,
        int start,
        int end,
        List<SvgSourceSite>? sites = null,
        string? attribute = null)
    {
        const int fence = 2;

        var opened = end - start >= fence && source[start] == '{' && source[start + 1] == '{';
        var closed = end - start >= fence * 2 && source[end - 1] == '}' && source[end - 2] == '}';

        var from = opened ? start + fence : start;
        var to = closed ? end - fence : end;

        if (opened)
        {
            SvgSourceHighlighter.Add(tokens, source, start, from, SvgSourceTokenKind.Expression);
        }

        Code(tokens, source, from, to, SvgSourceSiteKind.Placeholder, sites, attribute);

        if (closed)
        {
            SvgSourceHighlighter.Add(tokens, source, to, end, SvgSourceTokenKind.Expression);
        }
    }

    /// <summary>Adds a span that is expression code with nothing fencing it — a let's body.</summary>
    public static void Code(
        List<SvgSourceToken> tokens,
        string source,
        int start,
        int end,
        SvgSourceSiteKind site = SvgSourceSiteKind.Placeholder,
        List<SvgSourceSite>? sites = null,
        string? attribute = null)
    {
        if (end <= start)
        {
            return;
        }

        // Every expression in a document passes through here, which makes this the one place that
        // knows where the code in it is. Checking one needs the whole document's declarations, so it
        // cannot happen while splitting — but recording where to look costs nothing.
        sites?.Add(new SvgSourceSite(start, end - start, site, Attribute: attribute));

        var text = source.Substring(start, end - start);
        var lexed = Read(text);

        var cursor = start;

        foreach (var token in lexed)
        {
            if (token.Kind == ExprTokenKind.End || token.Text.Length == 0)
            {
                continue;
            }

            var at = start + token.Position;
            var length = token.Text.Length;

            // A number's text is the literal as written, captured before the lexer consumed the
            // percent sign after it — the sign belongs to the same literal and would otherwise be
            // left to the gap filler and coloured as though it were something else.
            if (token.Kind == ExprTokenKind.Number && at + length < end && source[at + length] == '%')
            {
                length++;
            }

            // The lexer skips whitespace, so the gaps it leaves are filled from the source rather
            // than from the tokens: what comes out has to add back up to what went in.
            SvgSourceHighlighter.Add(tokens, source, cursor, at, SvgSourceTokenKind.Expression);
            SvgSourceHighlighter.Add(tokens, source, at, at + length, Kind(token));

            cursor = at + length;
        }

        // Whatever is left: trailing whitespace, or everything past the point the language stopped.
        SvgSourceHighlighter.Add(tokens, source, cursor, end, SvgSourceTokenKind.Expression);
    }

    /// <summary>
    /// Lexes as much as the language will accept.
    /// </summary>
    /// <remarks>
    /// On a refusal the prefix before the offending position is lexed instead, which is what colours
    /// a half-written expression up to the point it stops making sense. That second attempt is
    /// guarded too: it cannot fail on anything seen so far, but a highlighter that threw would take
    /// out the pane showing the file rather than the expression in it.
    /// </remarks>
    private static IReadOnlyList<ExprToken> Read(string text)
    {
        try
        {
            return ExprLexer.Tokenize(text);
        }
        catch (ExprException failure)
        {
            var position = Math.Max(0, Math.Min(failure.Position, text.Length));

            try
            {
                return ExprLexer.Tokenize(text.Substring(0, position));
            }
            catch (ExprException)
            {
                return Array.Empty<ExprToken>();
            }
        }
    }

    private static SvgSourceTokenKind Kind(ExprToken token) => token.Kind switch
    {
        ExprTokenKind.Number => SvgSourceTokenKind.ExpressionNumber,
        ExprTokenKind.Color => SvgSourceTokenKind.ExpressionColor,
        ExprTokenKind.Comma or ExprTokenKind.OpenParen or ExprTokenKind.CloseParen
            => SvgSourceTokenKind.ExpressionPunctuation,
        ExprTokenKind.Identifier => Name(token.Text),

        // A word form lexes to the operator it spells, so the text is what tells them apart.
        _ => char.IsLetter(token.Text[0])
            ? SvgSourceTokenKind.ExpressionKeyword
            : SvgSourceTokenKind.ExpressionOperator,
    };

    /// <summary>
    /// What a name is, as far as can be known without the document.
    /// </summary>
    /// <remarks>
    /// Functions and constants belong to the language, so they are recognisable anywhere. Whether a
    /// remaining name is a declared parameter, a let, or a typo depends on the <c>&lt;e:code&gt;</c>
    /// block it sits under — and saying it is a typo is a diagnostic, not a colour.
    /// </remarks>
    private static SvgSourceTokenKind Name(string text)
    {
        if (ExprFunctions.IsFunction(text))
        {
            return SvgSourceTokenKind.ExpressionFunction;
        }

        // true and false lex as identifiers but the parser reads them as boolean literals, so a
        // name is what they look like and a value is what they are.
        if (text is "true" or "false" || ExprFunctions.TryGetConstant(text, out _, out _))
        {
            return SvgSourceTokenKind.ExpressionConstant;
        }

        return SvgSourceTokenKind.ExpressionIdentifier;
    }
}
