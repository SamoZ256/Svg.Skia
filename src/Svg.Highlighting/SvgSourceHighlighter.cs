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

    /// <summary>Expression code that is none of the kinds below: the fences, the whitespace between
    /// pieces, and anything past a point the language cannot read.</summary>
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
    /// A word form of an operator, which exist because XML escaping makes <c>&lt;</c> and
    /// <c>&amp;&amp;</c> awkward to author inside an attribute.
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
/// A substring per token would be tens of megabytes for a file a view can display comfortably, so
/// the text is cut only when a line is realised on screen.
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
    /// A line knows its own range, so anything keyed to a position is found without walking every
    /// other line's tokens.
    /// </remarks>
    public int Start { get; }

    /// <summary>How long the line is, its ending newline excluded.</summary>
    public int Length { get; }

    public IReadOnlyList<SvgSourceToken> Tokens { get; }
}

/// <summary>
/// Splits an SVG document into coloured pieces.
/// </summary>
/// <remarks>
/// Hand-written rather than a stock XML grammar, which would not know what
/// <c>{{ hsl(hue, 74%, 55%) }}</c> is — to one of those it is a string like any other. It describes
/// rather than validates, so a malformed document still colours; the invariant keeping that honest
/// is that concatenating every token reproduces the input exactly, asserted for broken documents too.
/// </remarks>
public static class SvgSourceHighlighter
{
    /// <summary>
    /// Splits a drawing into lines, which is what the pane shows one of at a time.
    /// </summary>
    /// <remarks>
    /// The cost was never the splitting — 7ms for 200,000 characters — but laying out one styled run
    /// per token: 130ms at 1,100 runs, 433ms at 4,500, 18 seconds at 45,000. A line at a time means a
    /// consumer pays for the screenful.
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
    /// How many pieces of one line are coloured before the rest is left plain.
    /// </summary>
    /// <remarks>
    /// Colouring by line bounds what a document costs but not what a <em>line</em> costs, and a
    /// minified drawing is the whole file on one of them. An ordinary line of SVG is a few dozen
    /// tokens and never reaches this; 132KB on a single line took 1.1 seconds coloured whole and
    /// 39ms stopping here. Nothing is hidden either way — the text past it is still there, plain.
    /// </remarks>
    public const int RowTokenLimit = 250;

    /// <summary>Splits <paramref name="source"/>, never throwing and never losing a character.</summary>
    /// <remarks>
    /// Expression spans are split again by <see cref="SvgSourceExpressions"/>, so what comes back is
    /// markup and code in one flat sequence — a consumer needs a brush per kind and nothing else.
    /// </remarks>
    public static IReadOnlyList<SvgSourceToken> Tokenize(string? source) => Tokenize(source, null);

    /// <summary>Splits one expression on its own, with no document around it.</summary>
    /// <remarks>
    /// What a box holding a single <c>&lt;e:let&gt;</c> body or a declaration's <c>default</c> needs:
    /// the same kinds <see cref="Tokenize(string?)"/> gives that text inside a file, so one brush
    /// table serves a source view and an editor beside it. A body the language cannot read is split
    /// as far as it got, since colouring is not the place to report that.
    /// </remarks>
    public static IReadOnlyList<SvgSourceToken> Expression(string? text)
    {
        var tokens = new List<SvgSourceToken>();

        if (!string.IsNullOrEmpty(text))
        {
            SvgSourceExpressions.Code(tokens, text!, 0, text!.Length);
        }

        return tokens;
    }

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
                    Value(tokens, source, index, valueEnd, sites, attributeName);
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
    /// <remarks>
    /// The attribute is carried through because what a placeholder may evaluate to depends on which
    /// one holds it -- a <c>fill</c> wants a colour and an <c>opacity</c> a number -- and this is
    /// the only point that knows both.
    /// </remarks>
    private static void Value(
        List<SvgSourceToken> tokens,
        string source,
        int start,
        int end,
        List<SvgSourceSite>? sites,
        string? attribute)
    {
        // A style attribute is not one value but a list of them, and each declaration drives a
        // different property — so a placeholder in it is typed by the property it was written in
        // rather than by "style", which types nothing.
        if (attribute == "style"
            && end - start > 2
            && SvgSourceAttributes.Declarations(source, start + 1, source[(start + 1)..(end - 1)]) is { Count: > 0 } declarations)
        {
            var at = start;

            foreach (var declaration in declarations)
            {
                // The property, the colon and whatever spacing is around them: ordinary value text.
                Add(tokens, source, at, declaration.Start, SvgSourceTokenKind.Value);
                Placeholders(tokens, source, declaration.Start, declaration.Start + declaration.Length, sites, declaration.Name);

                at = declaration.Start + declaration.Length;
            }

            Add(tokens, source, at, end, SvgSourceTokenKind.Value);
            return;
        }

        Placeholders(tokens, source, start, end, sites, attribute);
    }

    /// <summary>Adds a span of value text, lifting any <c>{{ … }}</c> out of it.</summary>
    private static void Placeholders(
        List<SvgSourceToken> tokens,
        string source,
        int start,
        int end,
        List<SvgSourceSite>? sites,
        string? attribute)
    {
        var index = start;

        while (index < end)
        {
            // Bounded by the value, not left to run to the end of the document: an unbounded search
            // rescans everything after each attribute, which is one whole file per attribute on the
            // overwhelmingly common document that has no placeholders in it at all.
            var open = source.IndexOf("{{", index, end - index, StringComparison.Ordinal);

            if (open < 0)
            {
                Add(tokens, source, index, end, SvgSourceTokenKind.Value);
                return;
            }

            var close = source.IndexOf("}}", open, end - open, StringComparison.Ordinal);
            var expressionEnd = close < 0 ? end : close + 2;

            Add(tokens, source, index, open, SvgSourceTokenKind.Value);
            SvgSourceExpressions.Placeholder(tokens, source, open, expressionEnd, sites, attribute);

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
