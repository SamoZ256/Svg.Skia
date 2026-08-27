// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Svg.Expressions;

/// <summary>An expression as a document holds it, which is not as the lexer reads it.</summary>
public static class ExprText
{
    /// <summary>The text an XML reader would see, and where each of its characters was written.</summary>
    /// <remarks>
    /// An expression reaches a file XML-escaped, so <c>a &amp;lt; b</c> is what a let holding
    /// <c>a &lt; b</c> looks like. Lexing that span raw stops at the ampersand, and searching it for
    /// a name would rename the <c>amp</c> inside an entity, given a parameter called that — which the
    /// language allows. So a span is decoded, lexed, and each decoded character remembers where it
    /// came from.
    /// </remarks>
    /// <param name="offsets">
    /// One entry per decoded character and one past the end, so a token running to the last
    /// character still has somewhere to measure its length against.
    /// </param>
    public static (string Text, int[] Offsets) Decode(string svgText, int start, int end)
    {
        var text = new StringBuilder(end - start);
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

        offsets.Add(end);

        return (text.ToString(), offsets.ToArray());
    }

    /// <summary>Where a position in decoded text was written, clamped to the span it came from.</summary>
    public static int Written(int[]? offsets, int at, int fallback)
    {
        if (offsets is null || offsets.Length == 0)
        {
            return fallback;
        }

        return offsets[at < 0 ? 0 : at >= offsets.Length ? offsets.Length - 1 : at];
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
            hex ? NumberStyles.HexNumber : NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var code)
            && code is > 0 and <= 0xFFFF
            ? ((char)code).ToString()
            : null;
    }
}
