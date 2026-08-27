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
/// Through the language's own lexer, not a second description of it: a percent sign is a suffix on a
/// number literal and never an operator, and <c>and</c>/<c>lt</c>/<c>eq</c> are word forms of the
/// symbolic ones — a tokenizer written here would have coloured both wrongly. A failure colours up to
/// where the language stopped and leaves the remainder plain.
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

        // The one place that knows where the code is. Checking needs the whole document's
        // declarations and cannot happen while splitting, but recording where costs nothing.
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

            // Captured before the lexer consumed the percent sign: it belongs to the same literal
            // and would otherwise fall to the gap filler.
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
    /// On a refusal the prefix before the offending position is lexed instead. That attempt is
    /// guarded too: a highlighter that threw would take out the pane, not just the expression.
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
    /// Functions and constants belong to the language and are recognisable anywhere; whether another
    /// name is a parameter, a let or a typo depends on the block, and a typo is a diagnostic.
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
