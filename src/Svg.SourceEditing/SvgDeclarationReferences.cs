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
/// Finding, and rewriting, where a declared name is used.
/// </summary>
/// <remarks>
/// Renaming only the declaration would leave a drawing that still parses and no longer draws: every
/// <c>{{ … }}</c> and every let naming the old one would stop resolving, and nothing about the
/// document's shape would say why.
///
/// An attribute's value on a tree has had its references resolved already, so there is no offset
/// table to carry between the text somebody wrote and the expression the language reads. What is
/// found is a count and, when a new name is given, a value rewritten.
/// </remarks>
internal static class SvgDeclarationReferences
{
    private static readonly XNamespace Ns = SvgExpressionDeclarations.Namespace;

    /// <summary>Whether what is between this element's tags is expression code rather than text.</summary>
    /// <remarks>
    /// A let in a drawing, and a rule in an svgc recipe: <c>&lt;replace color="red"&gt;alert&lt;/replace&gt;</c>
    /// names <c>alert</c> just as a let's body does. Left out, removing a parameter a rule still
    /// needed was allowed and renaming one left the rule pointing at a name that no longer existed —
    /// and neither said anything, because a rule is not a placeholder and was searched by nothing.
    /// </remarks>
    private static bool IsCode(XName name) => name == Ns + "let" || name == Ns + "replace";

    /// <summary>
    /// Counts every use of <paramref name="name"/>, rewriting each to <paramref name="to"/> where one
    /// is given, or explains why they cannot be found.
    /// </summary>
    public static string? Walk(XDocument document, string name, string? to, out int count)
    {
        var found = 0;

        var refusal = Visit(document, (text, tokens) =>
        {
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

            return builder?.Append(text, at, text.Length - at).ToString();
        });

        count = found;

        return refusal;
    }

    /// <summary>Every name the document's expressions read, or null where one cannot be read.</summary>
    /// <remarks>
    /// Every identifier, not only the declared ones: what a function or a constant is called is the
    /// caller's to know, and filtering here would need this to hold the language's own table.
    /// </remarks>
    public static IReadOnlyCollection<string>? Names(XDocument document)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        return Visit(document, (_, tokens) => Collect(tokens, names)) is { } ? null : names;
    }

    /// <summary>The same, for one expression rather than a document.</summary>
    public static IReadOnlyCollection<string>? Names(string expression)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        return Read(expression, out _) is { } tokens && Collect(tokens, names) is null ? names : null;
    }

    private static string? Collect(IReadOnlyList<ExprToken> tokens, HashSet<string> names)
    {
        foreach (var token in tokens)
        {
            if (token.Kind == ExprTokenKind.Identifier)
            {
                names.Add(token.Text);
            }
        }

        // Nothing is written back: reading is all this does.
        return null;
    }

    /// <summary>
    /// Hands every expression in <paramref name="document"/> to <paramref name="visit"/>, and writes
    /// back whatever it answers.
    /// </summary>
    /// <param name="visit">
    /// Given the expression and its tokens, and answering the text to put in its place, or null to
    /// leave it as it was.
    /// </param>
    /// <returns>The sentence refusing the walk, where an expression cannot be read.</returns>
    /// <remarks>
    /// One traversal, because there is one answer to where an expression lives in a drawing — the
    /// <c>{{ … }}</c> spans of every attribute and of every run of text, and the whole of what is
    /// between a code element's tags. A second walker beside this one would be a second answer, and
    /// the two would come to disagree about a place only one of them had been taught to look — which
    /// is what text was, so a rename carried every attribute and left
    /// <c>&lt;text&gt;{{ label }}&lt;/text&gt;</c> naming something that had gone.
    /// </remarks>
    private static string? Visit(XDocument document, Func<string, IReadOnlyList<ExprToken>, string?> visit)
    {
        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes().ToList())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    continue;
                }

                if (Placeholders(attribute.Value, visit, out var rewritten) is { } bad)
                {
                    return bad;
                }

                if (rewritten is { } && !string.Equals(rewritten, attribute.Value, StringComparison.Ordinal))
                {
                    attribute.Value = rewritten;
                }
            }

            if (!IsCode(element.Name))
            {
                // Each run on its own and in place: assigning XElement.Value would take every other
                // child with it, which is what SvgAttributeEditor.SetContent avoids, and a
                // placeholder can sit either side of a <tspan> rather than in one run.
                foreach (var run in element.Nodes().OfType<XText>().ToList())
                {
                    if (Placeholders(run.Value, visit, out var said) is { } trouble)
                    {
                        return trouble;
                    }

                    if (said is { } && !string.Equals(said, run.Value, StringComparison.Ordinal))
                    {
                        run.Value = said;
                    }
                }

                continue;
            }

            // The whole of what is between the tags is the expression, braces and all left out.
            var body = element.Value;

            if (body.Length == 0)
            {
                continue;
            }

            if (Read(body, out var unreadable) is not { } tokens)
            {
                return unreadable;
            }

            if (visit(body, tokens) is { } written && !string.Equals(written, body, StringComparison.Ordinal))
            {
                element.Value = written;
            }
        }

        return null;
    }

    /// <summary>Every <c>{{ … }}</c> in one value, visited, and the value rebuilt around them.</summary>
    /// <param name="written">The value as the visitor left it, or null where it changed nothing.</param>
    private static string? Placeholders(string value, Func<string, IReadOnlyList<ExprToken>, string?> visit, out string? written)
    {
        written = null;

        var builder = new StringBuilder();
        var changed = false;
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

            var text = value.Substring(open + 2, close - open - 2);

            builder.Append(value, at, open + 2 - at);
            at = close;

            if (text.Length == 0)
            {
                continue;
            }

            if (Read(text, out var unreadable) is not { } tokens)
            {
                return unreadable;
            }

            if (visit(text, tokens) is { } inner)
            {
                builder.Append(inner);
                changed |= !string.Equals(inner, text, StringComparison.Ordinal);
            }
            else
            {
                builder.Append(text);
            }
        }

        if (changed)
        {
            written = builder.Append(value, at, value.Length - at).ToString();
        }

        return null;
    }

    /// <summary>The tokens of one expression, or null and the sentence refusing it.</summary>
    /// <remarks>
    /// Refused rather than skipped: a use that cannot be read is still a use, and renaming around it
    /// would leave the drawing naming something that no longer exists.
    /// </remarks>
    private static IReadOnlyList<ExprToken>? Read(string text, out string? unreadable)
    {
        try
        {
            unreadable = null;

            return ExprLexer.Tokenize(text);
        }
        catch (ExprException bad)
        {
            unreadable = $"'{text.Trim()}' cannot be read, so what it uses cannot be found: {bad.Message}";

            return null;
        }
    }
}
