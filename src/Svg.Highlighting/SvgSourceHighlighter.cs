// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;

namespace Svg.Highlighting;

/// <summary>What a piece of a drawing's text is, for the purpose of colouring it.</summary>
public enum SvgSourceTokenKind
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
    /// Expression code that is not one of the kinds below: the <c>{{</c> and <c>}}</c> that fence a
    /// placeholder, the whitespace between pieces, and anything past a point the language cannot
    /// read.
    /// </summary>
    Expression,

    /// <summary>A number in an expression, percent sign and all — <c>55%</c> is one literal.</summary>
    ExpressionNumber,

    /// <summary>A colour literal in an expression, such as <c>#3fb5b5</c>.</summary>
    ExpressionColor,

    /// <summary>A name the language defines as a function: <c>hsl</c>, <c>mix</c>, <c>lerp</c>.</summary>
    ExpressionFunction,

    /// <summary>
    /// A value the language spells as a word: <c>pi</c>, <c>tau</c>, <c>true</c>, <c>false</c>.
    /// </summary>
    ExpressionConstant,

    /// <summary>
    /// A word form of an operator — <c>and</c>, <c>or</c>, <c>not</c>, <c>lt</c>, <c>eq</c> and the
    /// rest — which exist because XML escaping makes <c>&lt;</c> and <c>&amp;&amp;</c> awkward to
    /// author inside an attribute.
    /// </summary>
    ExpressionKeyword,

    /// <summary>A symbolic operator.</summary>
    ExpressionOperator,

    /// <summary>Parentheses and commas.</summary>
    ExpressionPunctuation,

    /// <summary>
    /// Anything else the language reads as a name: a parameter, a let, or a typo.
    /// </summary>
    /// <remarks>
    /// Telling those three apart needs the document's declarations and a way to say a name is wrong,
    /// which is what diagnostics will be for. Until then they share a colour.
    /// </remarks>
    ExpressionIdentifier,
}

/// <summary>
/// One run of text and what it is, as a range into the document rather than a copy of it.
/// </summary>
/// <remarks>
/// A view holds the tokens for a whole drawing once it shows them a line at a time, and a substring
/// each would be tens of megabytes for a file it can display comfortably. The text is cut only when
/// a line is actually realised on screen. Ranges are also what a diagnostic will want to point at.
/// </remarks>
public readonly record struct SvgSourceToken(string Source, int Start, int Length, SvgSourceTokenKind Kind)
{
    public string Text => Source.Substring(Start, Length);
}

/// <summary>One line of a drawing, and what its pieces are.</summary>
public sealed class SvgSourceLine
{
    public SvgSourceLine(int number, int start, int length, IReadOnlyList<SvgSourceToken> tokens)
    {
        Number = number;
        Start = start;
        Length = length;
        Tokens = tokens;
    }

    /// <summary>Counting from one, as an editor would show it.</summary>
    public int Number { get; }

    /// <summary>Where the line begins in the document.</summary>
    /// <remarks>
    /// A line knows its own range so that anything keyed to a position — a diagnostic, a search hit,
    /// a bookmark — can be found for it without walking the tokens of every other line.
    /// </remarks>
    public int Start { get; }

    /// <summary>How long the line is, its ending newline excluded.</summary>
    public int Length { get; }

    public IReadOnlyList<SvgSourceToken> Tokens { get; }

    /// <summary>The rest of the line from <paramref name="from"/> on, as one uncoloured piece.</summary>
    /// <remarks>
    /// Virtualising by line bounds what a document costs, but not what a <em>line</em> costs, and a
    /// minified drawing is the whole file on one of them: 132KB of it took 1.4 seconds to colour as
    /// a single row. Past <see cref="SvgSourceHighlighter.RowTokenLimit"/> a consumer shows the
    /// remainder plainly, so the text is all there and the row costs what plain text costs.
    /// </remarks>
    public string Rest(int from)
    {
        if (from >= Tokens.Count)
        {
            return string.Empty;
        }

        var first = Tokens[from];
        var last = Tokens[^1];

        return first.Source[first.Start..(last.Start + last.Length)];
    }
}

/// <summary>
/// Splits an SVG document into coloured pieces.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than a grammar from an editor library, for two reasons. Neither a viewer nor
/// an editor should make everyone who references it carry a text editor, and no stock XML grammar
/// knows what <c>{{ hsl(hue, 74%, 55%) }}</c> is — to one of those it is a string like any other,
/// when it is the thing a reader opened a source view to find.
/// </para>
/// <para>
/// It describes rather than validates: a malformed document still colours, because refusing to
/// colour the file someone is trying to work out what is wrong with would be perverse. The
/// invariant that keeps that honest is that concatenating every token reproduces the input exactly,
/// which is asserted for well-formed and broken documents alike.
/// </para>
/// <para>
/// This assembly knows nothing about how any of it is drawn — no brushes, no controls — which is
/// what lets a viewer pane and an editor share it. Two things are meant to arrive here rather than
/// in either of them: colouring the expression language itself, which subdivides
/// <see cref="SvgSourceTokenKind.Expression"/> by running <c>Svg.Expressions</c>' own lexer over the
/// span; and diagnostics, which have the ranges they need already, since a token is a position in
/// the document rather than a copy of part of it.
/// </para>
/// </remarks>
public static class SvgSourceHighlighter
{
    /// <summary>
    /// Splits a drawing into lines, which is what the pane shows one of at a time.
    /// </summary>
    /// <remarks>
    /// A line at a time because the cost of colouring was never the splitting — 7ms for 200,000
    /// characters — but laying out one styled run per token: 130ms at 1,100 runs, 433ms at 4,500 and
    /// 18 seconds at 45,000, in a single text block. Rows in a virtualising list lay out only what is
    /// on screen, so a 132KB drawing costs what a 2KB one does and there is no size at which the
    /// consumer gives up and shows plain text.
    /// </remarks>
    public static IReadOnlyList<SvgSourceLine> Lines(string? source)
    {
        var lines = new List<SvgSourceLine>();
        var current = new List<SvgSourceToken>();

        // Where the line being gathered began, which the tokens cannot say for a line that is empty.
        var from = 0;

        foreach (var token in Tokenize(source))
        {
            var start = token.Start;
            var end = token.Start + token.Length;

            for (var index = start; index < end; index++)
            {
                if (token.Source[index] != '\n')
                {
                    continue;
                }

                // A carriage return would otherwise be drawn as a glyph at the end of every line.
                var stop = index > start && token.Source[index - 1] == '\r' ? index - 1 : index;

                if (stop > start)
                {
                    current.Add(token with { Start = start, Length = stop - start });
                }

                lines.Add(new SvgSourceLine(lines.Count + 1, from, stop - from, current.ToArray()));
                current.Clear();

                from = index + 1;
                start = index + 1;
            }

            if (end > start)
            {
                current.Add(token with { Start = start, Length = end - start });
            }
        }

        if (current.Count > 0 || lines.Count == 0)
        {
            lines.Add(new SvgSourceLine(lines.Count + 1, from, Math.Max(0, (source?.Length ?? 0) - from), current.ToArray()));
        }

        return lines;
    }

    /// <summary>
    /// How many pieces of one line are coloured before the rest is shown plainly.
    /// </summary>
    /// <remarks>
    /// An ordinary line of SVG is a few dozen tokens and never reaches this. A minified drawing is
    /// one line of tens of thousands, which is the case this exists for: a consumer that builds one
    /// styled run per token pays for every one of them on that row.
    /// </remarks>
    public const int RowTokenLimit = 250;

    /// <summary>Splits <paramref name="source"/>, never throwing and never losing a character.</summary>
    /// <remarks>
    /// Expression spans are split again by <see cref="SvgSourceExpressions"/>, so what comes back is
    /// markup and code in one flat sequence — a consumer needs a brush per kind and nothing else.
    /// </remarks>
    public static IReadOnlyList<SvgSourceToken> Tokenize(string? source) => Tokenize(source, null);

    /// <summary>Splits, and records where the expression code was if asked.</summary>
    internal static IReadOnlyList<SvgSourceToken> Tokenize(string? source, List<SvgSourceSite>? sites)
    {
        var tokens = new List<SvgSourceToken>();

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
            var let = IsLet(element);

            if (open < 0)
            {
                AddBody(tokens, source, index, source.Length, let, sites);
                break;
            }

            AddBody(tokens, source, index, open, let, sites);

            index = source[open..] switch
            {
                var rest when rest.StartsWith("<!--", StringComparison.Ordinal) => Fenced(tokens, source, open, "-->"),
                var rest when rest.StartsWith("<![CDATA[", StringComparison.Ordinal) => Fenced(tokens, source, open, "]]>"),
                var rest when rest.StartsWith("<?", StringComparison.Ordinal) => Fenced(tokens, source, open, "?>"),
                var rest when rest.StartsWith("<!", StringComparison.Ordinal) => Fenced(tokens, source, open, ">"),
                _ => Tag(tokens, source, open, ref element, sites),
            };
        }

        return tokens;
    }

    /// <summary>Adds text between tags, as code when the element it belongs to is a let.</summary>
    private static void AddBody(
        List<SvgSourceToken> tokens,
        string source,
        int start,
        int end,
        bool let,
        List<SvgSourceSite>? sites)
    {
        if (end <= start)
        {
            return;
        }

        if (let)
        {
            SvgSourceExpressions.Code(tokens, source, start, end, SvgSourceSiteKind.Let, sites);
            return;
        }

        Add(tokens, source, start, end, SvgSourceTokenKind.Text);
    }

    /// <summary>Takes everything up to and including <paramref name="close"/> as one comment-like run.</summary>
    private static int Fenced(List<SvgSourceToken> tokens, string source, int start, string close)
    {
        var end = source.IndexOf(close, start, StringComparison.Ordinal);
        end = end < 0 ? source.Length : end + close.Length;

        Add(tokens, source, start, end, SvgSourceTokenKind.Comment);

        return end;
    }

    /// <summary>
    /// Whether a name is <paramref name="local"/>, whatever prefix it carries.
    /// </summary>
    /// <remarks>
    /// By local name, because the prefix bound to the extension's namespace is the document's choice
    /// — <c>e</c> by convention and in every example, but nothing requires it.
    /// </remarks>
    private static bool Is(string? name, string local)
        => name is { } && name.AsSpan(name.LastIndexOf(':') + 1).Equals(local, StringComparison.Ordinal);

    /// <summary>Whether text inside this element is expression code.</summary>
    private static bool IsLet(string? element) => Is(element, "let");

    /// <summary>
    /// Whether this attribute of an <c>&lt;e:param&gt;</c> holds an expression rather than a word.
    /// </summary>
    /// <remarks>
    /// <c>name</c> and <c>type</c> are neither: they are the parameter's identity. The other four
    /// are code — <c>default="tau / 4"</c>, <c>max="100%"</c> and <c>step="1/60"</c> are all things
    /// the language evaluates, so showing them as strings hides what they are.
    /// </remarks>
    private static bool IsDeclaredExpression(string? element, string? attribute)
        => Is(element, "param") && attribute is "default" or "min" or "max" or "step";

    private static int Tag(
        List<SvgSourceToken> tokens,
        string source,
        int start,
        ref string? element,
        List<SvgSourceSite>? sites)
    {
        // A declaration's attributes may be written in any order, so what it declares is not known
        // until the tag is closed. The sites it contributes are stamped with the name afterwards.
        var declared = sites?.Count ?? 0;
        string? name = null;

        // An unclosed tag runs to the end of the document rather than swallowing the rest silently.
        var close = source.IndexOf('>', start);
        var end = close < 0 ? source.Length : close + 1;

        var index = start + 1;

        // '<' or '</'
        while (index < end && source[index] == '/')
        {
            index++;
        }

        Add(tokens, source, start, index, SvgSourceTokenKind.Punctuation);

        var local = index;
        while (local < end && !char.IsWhiteSpace(source[local]) && source[local] is not ('>' or '/'))
        {
            local++;
        }

        Add(tokens, source, index, local, SvgSourceTokenKind.Element);

        // A closing tag ends whatever was open; a self-closing one never opens anything.
        var closing = index > start + 1;
        element = closing ? null : source[index..local];

        index = local;

        string? attributeName = null;

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

                Add(tokens, source, index, run, SvgSourceTokenKind.Text);
                index = run;
                continue;
            }

            if (character is '=' or '/' or '>' or '?')
            {
                if (character == '/')
                {
                    element = null;
                }

                Add(tokens, source, index, index + 1, SvgSourceTokenKind.Punctuation);
                index++;
                continue;
            }

            if (character is '"' or '\'')
            {
                var quote = source.IndexOf(character, index + 1);
                var valueEnd = quote < 0 ? end : quote + 1;

                if (IsDeclaredExpression(element, attributeName))
                {
                    Declared(tokens, source, index, valueEnd, quote, sites, attributeName);
                }
                else
                {
                    Value(tokens, source, index, valueEnd, sites);
                    name ??= attributeName == "name" ? source[(index + 1)..(quote < 0 ? valueEnd : quote)] : null;
                }

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

            Add(tokens, source, index, attribute, SvgSourceTokenKind.Attribute);
            attributeName = source[index..attribute];
            index = attribute;
        }

        if (name is { } && sites is { })
        {
            for (var at = declared; at < sites.Count; at++)
            {
                if (sites[at].Kind == SvgSourceSiteKind.Declaration)
                {
                    sites[at] = sites[at] with { Owner = name };
                }
            }
        }

        return end;
    }

    /// <summary>Adds a value whose whole content is expression code, quotes excepted.</summary>
    private static void Declared(
        List<SvgSourceToken> tokens,
        string source,
        int start,
        int end,
        int quote,
        List<SvgSourceSite>? sites,
        string? attribute)
    {
        // The quotes belong to the markup, not to the expression — and an unterminated value has
        // only the opening one.
        Add(tokens, source, start, start + 1, SvgSourceTokenKind.Value);

        var close = quote < 0 ? end : quote;

        SvgSourceExpressions.Code(tokens, source, start + 1, close, SvgSourceSiteKind.Declaration, sites, attribute);

        Add(tokens, source, close, end, SvgSourceTokenKind.Value);
    }

    /// <summary>Adds a value, lifting any <c>{{ … }}</c> out of it.</summary>
    private static void Value(List<SvgSourceToken> tokens, string source, int start, int end, List<SvgSourceSite>? sites)
    {
        var index = start;

        while (index < end)
        {
            var open = source.IndexOf("{{", index, StringComparison.Ordinal);

            if (open < 0 || open >= end)
            {
                Add(tokens, source, index, end, SvgSourceTokenKind.Value);
                return;
            }

            var close = source.IndexOf("}}", open, StringComparison.Ordinal);
            var expressionEnd = close < 0 || close + 2 > end ? end : close + 2;

            Add(tokens, source, index, open, SvgSourceTokenKind.Value);
            SvgSourceExpressions.Placeholder(tokens, source, open, expressionEnd, sites);

            index = expressionEnd;
        }
    }

    internal static void Add(List<SvgSourceToken> tokens, string source, int start, int end, SvgSourceTokenKind kind)
    {
        if (end > start)
        {
            tokens.Add(new SvgSourceToken(source, start, end - start, kind));
        }
    }
}
