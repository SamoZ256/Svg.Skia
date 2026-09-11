// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Svg.Expressions;

namespace Svg.SourceEditing;

/// <summary>
/// Finding, and rewriting, where a declared name is used — on the tree rather than in the text.
/// </summary>
/// <remarks>
/// The same question the span half answers, asked of a document that is already open. It is simpler
/// here for one reason worth stating: an attribute's value on a tree has had its entities resolved
/// already, so there is no offset table to carry between the text somebody wrote and the expression
/// the language reads. What is found is a count and, when a new name is given, a value rewritten.
/// </remarks>
internal static partial class SvgDeclarationReferences
{
    /// <summary>
    /// Counts every use of <paramref name="name"/>, rewriting each to <paramref name="to"/> where one
    /// is given, or explains why they cannot be found.
    /// </summary>
    public static string? Walk(XDocument document, string name, string? to, out int count)
    {
        var found = 0;

        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes().ToList())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    continue;
                }

                if (Placeholders(attribute.Value, name, to, ref found, out var rewritten) is { } bad)
                {
                    count = found;

                    return bad;
                }

                if (to is { } && !string.Equals(rewritten, attribute.Value, StringComparison.Ordinal))
                {
                    attribute.Value = rewritten;
                }
            }

            if (!IsCode(element.Name))
            {
                continue;
            }

            // The whole of what is between the tags is the expression, braces and all left out.
            var body = element.Value;

            if (In(body, name, to, ref found, out var written) is { } trouble)
            {
                count = found;

                return trouble;
            }

            if (to is { } && !string.Equals(written, body, StringComparison.Ordinal))
            {
                element.Value = written;
            }
        }

        count = found;

        return null;
    }

    /// <summary>Every <c>{{ … }}</c> in one value, and what each of them names.</summary>
    private static string? Placeholders(string value, string name, string? to, ref int found, out string written)
    {
        var builder = new StringBuilder();
        var at = 0;

        while (true)
        {
            var open = value.IndexOf("{{", at, StringComparison.Ordinal);

            if (open < 0)
            {
                break;
            }

            var close = value.IndexOf("}}", open + 2, StringComparison.Ordinal);

            if (close < 0)
            {
                break;
            }

            builder.Append(value, at, open + 2 - at);

            if (In(value.Substring(open + 2, close - open - 2), name, to, ref found, out var inner) is { } bad)
            {
                written = value;

                return bad;
            }

            builder.Append(inner);
            at = close;
        }

        written = builder.Append(value, at, value.Length - at).ToString();

        return null;
    }

    /// <summary>Each identifier in one expression that names <paramref name="name"/>.</summary>
    private static string? In(string text, string name, string? to, ref int found, out string written)
    {
        written = text;

        if (text.Length == 0)
        {
            return null;
        }

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

        var builder = to is null ? null : new StringBuilder();
        var at = 0;

        foreach (var token in tokens)
        {
            if (token.Kind != ExprTokenKind.Identifier || !string.Equals(token.Text, name, StringComparison.Ordinal))
            {
                continue;
            }

            found++;

            if (builder is { })
            {
                builder.Append(text, at, token.Position - at).Append(to);
                at = token.Position + name.Length;
            }
        }

        if (builder is { })
        {
            written = builder.Append(text, at, text.Length - at).ToString();
        }

        return null;
    }
}
