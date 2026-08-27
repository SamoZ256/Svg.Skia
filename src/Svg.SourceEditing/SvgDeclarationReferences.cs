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

        var (text, offsets) = Decode(svgText, start, end);

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

    /// <summary>The text an XML reader would see, and where each of its characters was written.</summary>
    /// <remarks>
    /// An expression reaches the file XML-escaped, so <c>a &amp;lt; b</c> is what a let holding
    /// <c>a &lt; b</c> looks like. Lexing the raw span would choke on the ampersand; searching it for
    /// the name would rename the <c>amp</c> inside an entity, given a parameter called that — which
    /// the language allows. So the span is decoded, lexed, and each decoded character remembers where
    /// it came from.
    /// </remarks>
    private static (string Text, int[] Offsets) Decode(string svgText, int start, int end)
    {
        var text = new System.Text.StringBuilder(end - start);
        var offsets = new List<int>(end - start);

        var at = start;

        while (at < end)
        {
            if (svgText[at] == '&')
            {
                var semicolon = svgText.IndexOf(';', at);

                if (semicolon > at && semicolon < end && Entity(svgText.Substring(at + 1, semicolon - at - 1)) is { } decoded)
                {
                    text.Append(decoded);
                    offsets.Add(at);

                    at = semicolon + 1;

                    continue;
                }
            }

            text.Append(svgText[at]);
            offsets.Add(at);

            at++;
        }

        // One past the end, so an identifier running to the last character still has somewhere to
        // measure its length against.
        offsets.Add(end);

        return (text.ToString(), offsets.ToArray());
    }

    private static string? Entity(string name) => name switch
    {
        "lt" => "<",
        "gt" => ">",
        "amp" => "&",
        "quot" => "\"",
        "apos" => "'",
        _ => Numeric(name),
    };

    private static string? Numeric(string name)
    {
        if (name.Length < 2 || name[0] != '#')
        {
            return null;
        }

        var hex = name[1] is 'x' or 'X';
        var digits = name.Substring(hex ? 2 : 1);

        return int.TryParse(
            digits,
            hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var code)
            && code is > 0 and <= 0xFFFF
            ? ((char)code).ToString()
            : null;
    }
}
