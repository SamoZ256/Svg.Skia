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
    public static string? Rename(
        string svgText,
        XDocument document,
        SvgExpressionDeclarations.Positions positions,
        string from,
        string to,
        List<SvgTextEdit> edits)
    {
        var found = new List<(int Start, int Length)>();

        if (Uses(svgText, document, positions, from, found) is { } bad)
        {
            return bad;
        }

        foreach (var (start, length) in found)
        {
            edits.Add(new SvgTextEdit(start, length, to));
        }

        return null;
    }

    /// <summary>Whether what is between this element's tags is expression code rather than text.</summary>
    /// <remarks>
    /// A let in a drawing, and a rule in an svgc recipe: <c>&lt;replace color="red"&gt;alert&lt;/replace&gt;</c>
    /// names <c>alert</c> just as a let's body does. Left out, removing a parameter a rule still
    /// needed was allowed and renaming one left the rule pointing at a name that no longer existed —
    /// and neither said anything, because a rule is not a placeholder and was searched by nothing.
    /// </remarks>
    private static bool IsCode(XName name) => name == Ns + "let" || name == Ns + "replace";

    /// <summary>Finds where <paramref name="name"/> is used, or explains why it cannot.</summary>
    /// <remarks>
    /// Only placeholders and element bodies the language reads as code are searched. A
    /// <c>default</c>, <c>min</c>, <c>max</c> or <c>step</c> is an expression too, but the language
    /// puts nothing a document declares in scope there, so no name in one can be a use of this.
    /// Renaming rewrites what this finds and removing refuses over it, which is the same question
    /// asked twice.
    /// </remarks>
    public static string? Uses(
        string svgText,
        XDocument document,
        SvgExpressionDeclarations.Positions positions,
        string name,
        List<(int Start, int Length)> found)
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

                if (Placeholders(svgText, start, end, name, found) is { } bad)
                {
                    return bad;
                }
            }

            if (!IsCode(element.Name))
            {
                continue;
            }

            var body = positions.ContentStart(element);

            if (body < 0)
            {
                continue;
            }

            var close = svgText.IndexOf("</", body, StringComparison.Ordinal);

            if (close >= 0 && In(svgText, body, close, name, found) is { } trouble)
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
        string name,
        List<(int Start, int Length)> found)
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

            if (In(svgText, open + 2, close, name, found) is { } bad)
            {
                return bad;
            }

            at = close + 2;
        }

        return null;
    }

    /// <summary>Finds each identifier in one expression that names <paramref name="name"/>.</summary>
    private static string? In(string svgText, int start, int end, string name, List<(int Start, int Length)> found)
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
            return $"'{text.Trim()}' cannot be read, so what it uses cannot be found: {bad.Message}";
        }

        foreach (var token in tokens)
        {
            if (token.Kind != ExprTokenKind.Identifier || !string.Equals(token.Text, name, StringComparison.Ordinal))
            {
                continue;
            }

            var at = offsets[token.Position];
            var past = token.Position + name.Length < offsets.Length ? offsets[token.Position + name.Length] : end;

            found.Add((at, past - at));
        }

        return null;
    }
}
