// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Svg.Expressions;

namespace Svg.SourceEditing;

/// <summary>Finds where a declared name is used, so renaming one can carry its uses with it.</summary>
/// <remarks>
/// Renaming only the declaration would leave a drawing that still parses and no longer draws: every
/// <c>{{ … }}</c> and every let naming the old one would stop resolving, and nothing about the
/// document's shape would say why.
/// </remarks>
internal static class SvgDeclarationReferences
{
    private static readonly XNamespace Ns = SvgExpressionDeclarations.Namespace;

    /// <summary>Adds an edit for every use of <paramref name="from"/>, or explains why it cannot.</summary>
    /// <remarks>
    /// Only placeholders and let bodies are searched. A <c>default</c>, <c>min</c>, <c>max</c> or
    /// <c>step</c> is an expression too, but the language puts nothing a document declares in scope
    /// there, so no name in one can be a use of this.
    /// </remarks>
    public static string? Rename(
        string svgText,
        XDocument document,
        SvgExpressionDeclarations.Positions positions,
        string from,
        string to,
        List<SvgTextEdit> edits)
    {
        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                var start = positions.Value(attribute);
                var end = positions.EndOfValue(attribute);

                if (start < 0 || end < 0)
                {
                    continue;
                }

                if (Placeholders(svgText, start, end, from, to, edits) is { } bad)
                {
                    return bad;
                }
            }

            if (element.Name != Ns + "let")
            {
                continue;
            }

            var body = positions.ContentStart(element);

            if (body < 0)
            {
                continue;
            }

            var close = svgText.IndexOf("</", body, StringComparison.Ordinal);

            if (close >= 0 && In(svgText, body, close, from, to, edits) is { } trouble)
            {
                return trouble;
            }
        }

        return null;
    }

    private static string? Placeholders(
        string svgText,
        int start,
        int end,
        string from,
        string to,
        List<SvgTextEdit> edits)
    {
        var at = start;

        while (at < end)
        {
            var open = svgText.IndexOf("{{", at, StringComparison.Ordinal);

            if (open < 0 || open >= end)
            {
                return null;
            }

            var close = svgText.IndexOf("}}", open + 2, StringComparison.Ordinal);

            if (close < 0 || close > end)
            {
                return null;
            }

            if (In(svgText, open + 2, close, from, to, edits) is { } bad)
            {
                return bad;
            }

            at = close + 2;
        }

        return null;
    }

    /// <summary>Adds an edit for each identifier in one expression that names <paramref name="from"/>.</summary>
    private static string? In(string svgText, int start, int end, string from, string to, List<SvgTextEdit> edits)
    {
        if (end <= start)
        {
            return null;
        }

        var (text, offsets) = ExprText.Decode(svgText, start, end);

        List<ExprToken> tokens;

        try
        {
            tokens = ExprLexer.Tokenize(text);
        }
        catch (ExprException bad)
        {
            // Refused rather than skipped: a use that cannot be read is still a use, and renaming
            // around it would leave the drawing naming something that no longer exists.
            return $"'{text.Trim()}' cannot be read, so renaming cannot find what it uses: {bad.Message}";
        }

        foreach (var token in tokens)
        {
            if (token.Kind != ExprTokenKind.Identifier || !string.Equals(token.Text, from, StringComparison.Ordinal))
            {
                continue;
            }

            var at = offsets[token.Position];
            var past = token.Position + from.Length < offsets.Length ? offsets[token.Position + from.Length] : end;

            edits.Add(new SvgTextEdit(at, past - at, to));
        }

        return null;
    }
}
