// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>What a piece of a drawing's text is, for the purpose of colouring it.</summary>
internal enum SvgViewerSourceTokenKind
{
    /// <summary>Anything between tags, and the whitespace inside one.</summary>
    Text,

    /// <summary>Brackets, slashes and equals signs — the punctuation that holds markup together.</summary>
    Punctuation,

    /// <summary>An element name, prefix included.</summary>
    Element,

    /// <summary>An attribute name, prefix included.</summary>
    Attribute,

    /// <summary>An attribute value, quotes included.</summary>
    Value,

    /// <summary>A comment, a CDATA section, a processing instruction or a doctype, in full.</summary>
    Comment,

    /// <summary>
    /// Expression code: the <c>{{ … }}</c> part of a value, and the body of an <c>&lt;e:let&gt;</c>.
    /// </summary>
    Expression,
}

/// <summary>One run of text and what it is.</summary>
internal readonly record struct SvgViewerSourceToken(string Text, SvgViewerSourceTokenKind Kind);

/// <summary>
/// Splits a drawing's text into coloured pieces.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than a grammar from an editor library, for two reasons. A viewer package
/// should not make everyone who references it carry a text editor, and no stock XML grammar knows
/// what <c>{{ hsl(hue, 74%, 55%) }}</c> is — to one of those it is a string like any other, when it
/// is the thing a reader opened the pane to find.
/// </para>
/// <para>
/// It describes rather than validates: a malformed document still colours, because refusing to
/// colour the file someone is trying to work out what is wrong with would be perverse. The
/// invariant that keeps that honest is that concatenating every token reproduces the input exactly,
/// which is asserted for well-formed and broken documents alike.
/// </para>
/// </remarks>
internal static class SvgViewerSourceHighlighter
{
    /// <summary>
    /// Above this many tokens the caller should show plain text instead.
    /// </summary>
    /// <remarks>
    /// Splitting the text is free — under 7ms for 200,000 characters. Laying the pieces out is not:
    /// one styled run each costs 130ms at 1,100 runs, 433ms at 4,500, and 18 seconds at 45,000,
    /// because the text stack's cost climbs far faster than the count does. The limit counts tokens
    /// rather than characters because that is what is actually being paid for: a drawing exported as
    /// enormous path data is a handful of tokens per kilobyte and colours fine at any size, while
    /// markup of many tiny elements reaches the limit while still small.
    /// </remarks>
    public const int TokenLimit = 5_000;

    /// <summary>Splits <paramref name="source"/>, never throwing and never losing a character.</summary>
    public static IReadOnlyList<SvgViewerSourceToken> Tokenize(string? source)
    {
        var tokens = new List<SvgViewerSourceToken>();

        if (string.IsNullOrEmpty(source))
        {
            return tokens;
        }

        var index = 0;

        // What element the text between tags belongs to, which is how a let's body is recognised.
        string? element = null;

        while (index < source!.Length)
        {
            var open = source.IndexOf('<', index);
            var body = IsLet(element) ? SvgViewerSourceTokenKind.Expression : SvgViewerSourceTokenKind.Text;

            if (open < 0)
            {
                Add(tokens, source, index, source.Length, body);
                break;
            }

            Add(tokens, source, index, open, body);

            index = source[open..] switch
            {
                var rest when rest.StartsWith("<!--", StringComparison.Ordinal) => Fenced(tokens, source, open, "-->"),
                var rest when rest.StartsWith("<![CDATA[", StringComparison.Ordinal) => Fenced(tokens, source, open, "]]>"),
                var rest when rest.StartsWith("<?", StringComparison.Ordinal) => Fenced(tokens, source, open, "?>"),
                var rest when rest.StartsWith("<!", StringComparison.Ordinal) => Fenced(tokens, source, open, ">"),
                _ => Tag(tokens, source, open, ref element),
            };
        }

        return tokens;
    }

    /// <summary>Takes everything up to and including <paramref name="close"/> as one comment-like run.</summary>
    private static int Fenced(List<SvgViewerSourceToken> tokens, string source, int start, string close)
    {
        var end = source.IndexOf(close, start, StringComparison.Ordinal);
        end = end < 0 ? source.Length : end + close.Length;

        Add(tokens, source, start, end, SvgViewerSourceTokenKind.Comment);

        return end;
    }

    /// <summary>
    /// Whether text inside this element is expression code.
    /// </summary>
    /// <remarks>
    /// By local name, because the prefix bound to the extension's namespace is the document's choice
    /// — <c>e</c> by convention and in every example, but nothing requires it.
    /// </remarks>
    private static bool IsLet(string? element)
    {
        if (element is null)
        {
            return false;
        }

        var colon = element.LastIndexOf(':');

        return element.AsSpan(colon + 1).Equals("let", StringComparison.Ordinal);
    }

    private static int Tag(List<SvgViewerSourceToken> tokens, string source, int start, ref string? element)
    {
        // An unclosed tag runs to the end of the document rather than swallowing the rest silently.
        var close = source.IndexOf('>', start);
        var end = close < 0 ? source.Length : close + 1;

        var index = start + 1;

        // '<' or '</'
        while (index < end && source[index] == '/')
        {
            index++;
        }

        Add(tokens, source, start, index, SvgViewerSourceTokenKind.Punctuation);

        var name = index;
        while (name < end && !char.IsWhiteSpace(source[name]) && source[name] is not ('>' or '/'))
        {
            name++;
        }

        Add(tokens, source, index, name, SvgViewerSourceTokenKind.Element);

        // A closing tag ends whatever was open; a self-closing one never opens anything.
        var closing = index > start + 1;
        element = closing ? null : source[index..name];

        index = name;

        while (index < end)
        {
            var character = source[index];

            if (char.IsWhiteSpace(character))
            {
                var run = index;
                while (run < end && char.IsWhiteSpace(source[run]))
                {
                    run++;
                }

                Add(tokens, source, index, run, SvgViewerSourceTokenKind.Text);
                index = run;
                continue;
            }

            if (character is '=' or '/' or '>' or '?')
            {
                if (character == '/')
                {
                    element = null;
                }

                Add(tokens, source, index, index + 1, SvgViewerSourceTokenKind.Punctuation);
                index++;
                continue;
            }

            if (character is '"' or '\'')
            {
                var quote = source.IndexOf(character, index + 1);
                var valueEnd = quote < 0 ? end : quote + 1;

                Value(tokens, source, index, valueEnd);
                index = valueEnd;
                continue;
            }

            var attribute = index;
            while (attribute < end
                   && !char.IsWhiteSpace(source[attribute])
                   && source[attribute] is not ('=' or '/' or '>' or '"' or '\''))
            {
                attribute++;
            }

            // A character that starts nothing — the '<' of an unclosed tag, say — must still advance,
            // or the document stops colouring at the first thing it does not understand.
            if (attribute == index)
            {
                attribute++;
            }

            Add(tokens, source, index, attribute, SvgViewerSourceTokenKind.Attribute);
            index = attribute;
        }

        return end;
    }

    /// <summary>Adds a value, lifting any <c>{{ … }}</c> out of it.</summary>
    private static void Value(List<SvgViewerSourceToken> tokens, string source, int start, int end)
    {
        var index = start;

        while (index < end)
        {
            var open = source.IndexOf("{{", index, StringComparison.Ordinal);

            if (open < 0 || open >= end)
            {
                Add(tokens, source, index, end, SvgViewerSourceTokenKind.Value);
                return;
            }

            var close = source.IndexOf("}}", open, StringComparison.Ordinal);
            var expressionEnd = close < 0 || close + 2 > end ? end : close + 2;

            Add(tokens, source, index, open, SvgViewerSourceTokenKind.Value);
            Add(tokens, source, open, expressionEnd, SvgViewerSourceTokenKind.Expression);

            index = expressionEnd;
        }
    }

    private static void Add(List<SvgViewerSourceToken> tokens, string source, int start, int end, SvgViewerSourceTokenKind kind)
    {
        if (end > start)
        {
            tokens.Add(new SvgViewerSourceToken(source[start..end], kind));
        }
    }
}
