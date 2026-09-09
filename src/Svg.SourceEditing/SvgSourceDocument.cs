// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Svg.Expressions;

namespace Svg.SourceEditing;

/// <summary>
/// A drawing held as a tree, which writes back as the file it was read from.
/// </summary>
/// <remarks>
/// <para>
/// The truth about an open drawing, for a host that edits the tree rather than the text. It is an
/// <see cref="XDocument"/> and not an <c>SvgDocument</c> because only one of the two can be written
/// back at all: the SVG reader's node switch has no case for a comment, so a comment is not dropped
/// on the way out but never modelled, and the writer rebuilds <c>style</c> from a dictionary,
/// reorders attributes into the order its properties were declared in, and adds a doctype and a
/// version nobody asked for. An <see cref="XDocument"/> keeps comments, keeps an attribute's value
/// as the string it was written as -- so <c>{{ }}</c> survives untouched -- and keeps the order the
/// attributes were put in.
/// </para>
/// <para>
/// That alone is not enough to write a file back as it was found, which is the whole point of
/// holding one. Measured over the 2,988 drawings in the two suites, re-serialising the tree the
/// ordinary way returned 28 of them unchanged: XML says nothing about the whitespace inside a start
/// tag, so attributes written one to a line come back on one line, <c>&lt;rect/&gt;</c> comes back
/// as <c>&lt;rect /&gt;</c>, and a parser must fold CRLF to LF before the tree ever sees it, so
/// every line of a file written on Windows changes. Reformatting somebody's whole file to edit one
/// attribute is the thing this type exists not to do.
/// </para>
/// <para>
/// So a tag is remembered, not regenerated. Each element keeps the bytes of the start tag it was
/// read as, and writes them back unless its own name or attributes have since changed -- in which
/// case that one tag, and only it, is written afresh. Everything between the tags is already
/// modelled faithfully, and the prologue and whatever follows the root are carried across verbatim.
/// </para>
/// </remarks>
public sealed class SvgSourceDocument
{
    /// <summary>A byte order mark, spelled rather than written, so this file stays text to a tool.</summary>
    private const char Mark = '\uFEFF';

    private readonly string _prologue;
    private readonly string _epilogue;
    private readonly bool _carriageReturns;

    private SvgSourceDocument(
        XDocument document,
        bool byteOrderMark,
        string prologue,
        string epilogue,
        bool carriageReturns)
    {
        Document = document;
        ByteOrderMark = byteOrderMark;
        _prologue = prologue;
        _epilogue = epilogue;
        _carriageReturns = carriageReturns;
    }

    /// <summary>The tree, to be read and mutated in place.</summary>
    public XDocument Document { get; }

    /// <summary>Whether the file this was read from began with a byte order mark.</summary>
    public bool ByteOrderMark { get; }

    /// <summary>One level of indentation, as this file writes one.</summary>
    /// <remarks>
    /// Measured from the text at the one moment there is still text to measure, by the rule that
    /// already answers this for the span editors, so a file written with tabs goes on being written
    /// with tabs whichever half writes it.
    /// </remarks>
    public string IndentUnit { get; private set; } = "  ";

    /// <summary>Reads a drawing, or refuses with a sentence saying why it could not be read.</summary>
    /// <remarks>
    /// A refusal rather than an exception, for the reason the rest of this assembly gives one: a
    /// document that is not well formed is something a person can act on, not a fault in the caller.
    /// </remarks>
    public static SvgSourceDocument? Read(string svgText, out string? refusal)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        var byteOrderMark = svgText.Length > 0 && svgText[0] == Mark;
        var body = byteOrderMark ? svgText[1..] : svgText;

        XDocument document;

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(body),
                new XmlReaderSettings
                {
                    // Parsed rather than ignored so a document that declares a doctype keeps it;
                    // the null resolver is what stops the declared DTD from being fetched.
                    DtdProcessing = DtdProcessing.Parse,
                    XmlResolver = null,
                    IgnoreWhitespace = false,
                    IgnoreComments = false,
                    IgnoreProcessingInstructions = false,
                });

            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            refusal = $"The drawing is not well formed XML: {ex.Message}";

            return null;
        }

        if (document.Root is not { } root)
        {
            refusal = "The drawing has no root element.";

            return null;
        }

        // A reader replaces a reference with what the entity stands for and there is no node for
        // the reference itself, so the file could be read but never written back as it was written.
        // Refused rather than opened, so that what this reads it can always write.
        if (document.DocumentType?.InternalSubset is { } subset
            && subset.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
        {
            refusal = "The drawing declares entities of its own, which cannot be written back as they were written.";

            return null;
        }

        var positions = new SvgExpressionDeclarations.Positions(body);

        foreach (var element in document.Descendants())
        {
            Remember(element, body, positions);

            foreach (var text in element.Nodes())
            {
                if (text is XText and not XCData)
                {
                    Remember((XText)text, body, positions);
                }
            }
        }

        var (start, length) = positions.Span(root);
        var prologue = start >= 0 ? body[..start] : string.Empty;
        var epilogue = start >= 0 && start + length <= body.Length ? body[(start + length)..] : string.Empty;

        refusal = null;

        return new SvgSourceDocument(
            document,
            byteOrderMark,
            prologue,
            epilogue,
            body.Contains('\r'))
        {
            IndentUnit = SvgDeclarationEditor.IndentUnit(body),
        };
    }

    /// <summary>The document as text: the file it was read from, plus whatever was changed in it.</summary>
    /// <remarks>Not a property, because it writes the whole tree every time it is asked.</remarks>
    public string ToText()
    {
        var builder = new StringBuilder();

        if (ByteOrderMark)
        {
            builder.Append(Mark);
        }

        builder.Append(_prologue);

        if (Document.Root is { } root)
        {
            Write(builder, root);
        }

        builder.Append(_epilogue);

        var text = builder.ToString();

        // A parser folds every line ending to a newline before the tree sees one, so a file written
        // with CRLF would otherwise come back written with LF -- every line of it changed to edit
        // one attribute. The tags carried over verbatim already hold theirs.
        return _carriageReturns ? Restore(text) : text;
    }

    private static string Restore(string text)
    {
        var builder = new StringBuilder(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n' && (index == 0 || text[index - 1] != '\r'))
            {
                builder.Append('\r');
            }

            builder.Append(text[index]);
        }

        return builder.ToString();
    }

    /// <summary>Keeps the bytes of the tag an element was read as, and what it said at the time.</summary>
    private static void Remember(XElement element, string body, SvgExpressionDeclarations.Positions positions)
    {
        if (element is not IXmlLineInfo info || !info.HasLineInfo())
        {
            return;
        }

        // Line info points at the name rather than at the bracket, so the start steps back over it.
        var name = positions.At(info.LineNumber, info.LinePosition);
        var start = name > 0 && body[name - 1] == '<' ? name - 1 : name;
        var open = SvgExpressionDeclarations.EndOfStartTag(body, name);

        if (open < 0 || open >= body.Length || start >= open)
        {
            return;
        }

        var text = body[start..(open + 1)];
        var selfClosed = body[open - 1] == '/';

        element.AddAnnotation(new Tag(
            text,
            Spelling(body, name),
            selfClosed,
            Tail(text, selfClosed),
            Named(element),
            Valued(element),
            Slots(text)));
    }

    /// <summary>
    /// Keeps the bytes a run of text was written as, and what it read as at the time.
    /// </summary>
    /// <remarks>
    /// A parser resolves <c>&amp;gt;</c> to the character it stands for and says nothing about which
    /// of the two the file used, so writing the character back is a change to a file nobody edited.
    /// Text cannot hold a <c>&lt;</c>, so the next one ends the run.
    /// </remarks>
    private static void Remember(XText text, string body, SvgExpressionDeclarations.Positions positions)
    {
        if (text is not IXmlLineInfo info || !info.HasLineInfo())
        {
            return;
        }

        var start = positions.At(info.LineNumber, info.LinePosition);
        var end = body.IndexOf('<', start);

        if (start < 0 || end < start)
        {
            return;
        }

        text.AddAnnotation(new Run(body[start..end], text.Value));
    }

    /// <summary>
    /// How the file closed the tag: <c>&gt;</c>, <c>/&gt;</c>, or either with the space some
    /// authors put in front of it. Kept so that rewriting a tag changes the attribute that was
    /// edited and nothing else on the line.
    /// </summary>
    private static string Tail(string text, bool selfClosed)
    {
        var at = text.Length - (selfClosed ? 2 : 1);

        while (at > 0 && char.IsWhiteSpace(text[at - 1]))
        {
            at--;
        }

        return text[at..];
    }

    /// <summary>The element's name as the file spells it, prefix and all, for its closing tag.</summary>
    private static string Spelling(string body, int from)
    {
        var end = from;

        while (end < body.Length && body[end] != '>' && body[end] != '/' && !char.IsWhiteSpace(body[end]))
        {
            end++;
        }

        return body[from..end];
    }

    /// <summary>An element's name and its attributes' names, in the order they were written.</summary>
    private static string[] Named(XElement element)
    {
        var names = new List<string> { element.Name.ToString() };

        foreach (var attribute in element.Attributes())
        {
            names.Add(attribute.Name.ToString());
        }

        return names.ToArray();
    }

    /// <summary>What those attributes say, kept apart from their names so one value can change alone.</summary>
    private static string[] Valued(XElement element)
    {
        var values = new List<string>();

        foreach (var attribute in element.Attributes())
        {
            values.Add(attribute.Value);
        }

        return values.ToArray();
    }

    /// <summary>Where each attribute's value sits inside a start tag, and which quote holds it.</summary>
    /// <remarks>
    /// So that changing one value rewrites that value and not the tag around it. The root is the
    /// likeliest tag in a drawing to be laid out by hand, one attribute to a line, and it is also
    /// the one a resize writes to and the one the first parameter declares a namespace on.
    /// </remarks>
    private static Slot[] Slots(string tag)
    {
        var slots = new List<Slot>();
        var index = 1;

        while (index < tag.Length && tag[index] != '>' && tag[index] != '/' && !char.IsWhiteSpace(tag[index]))
        {
            index++;
        }

        while (index < tag.Length)
        {
            var run = index;

            while (index < tag.Length && char.IsWhiteSpace(tag[index]))
            {
                index++;
            }

            if (index >= tag.Length || tag[index] == '>' || tag[index] == '/')
            {
                break;
            }

            while (index < tag.Length && tag[index] != '=' && !char.IsWhiteSpace(tag[index]))
            {
                index++;
            }

            while (index < tag.Length && char.IsWhiteSpace(tag[index]))
            {
                index++;
            }

            if (index >= tag.Length || tag[index] != '=')
            {
                break;
            }

            index++;

            while (index < tag.Length && char.IsWhiteSpace(tag[index]))
            {
                index++;
            }

            if (index >= tag.Length || (tag[index] != '"' && tag[index] != '\''))
            {
                break;
            }

            var quote = tag[index++];
            var start = index;

            while (index < tag.Length && tag[index] != quote)
            {
                index++;
            }

            if (index >= tag.Length)
            {
                break;
            }

            slots.Add(new Slot(run, index + 1 - run, start, index - start, quote));
            index++;
        }

        return slots.ToArray();
    }

    private static void Write(StringBuilder builder, XElement element)
    {
        var tag = element.Annotation<Tag>();
        var children = element.FirstNode is { };

        // An element that closed itself cannot go on doing so once something has been put inside
        // it, and then there is nothing of the tag left to keep.
        var kept = tag is { } known && (!known.SelfClosed || !children) ? known.Written(element) : null;

        if (kept is { })
        {
            builder.Append(kept);

            if (tag!.SelfClosed)
            {
                return;
            }
        }
        else
        {
            Open(builder, element, children, tag);
        }

        foreach (var node in element.Nodes())
        {
            Write(builder, node);
        }

        if (kept is { })
        {
            builder.Append("</").Append(tag!.Name).Append('>');
        }
        else if (children)
        {
            builder.Append("</").Append(Name(element)).Append('>');
        }
    }

    private static void Write(StringBuilder builder, XNode node)
    {
        switch (node)
        {
            case XElement element:
                Write(builder, element);
                break;
            // Before the text it derives from, or every section would be written as escaped text.
            case XCData data:
                builder.Append("<![CDATA[").Append(data.Value).Append("]]>");
                break;
            case XText text:
                if (text.Annotation<Run>() is { } run && run.Holds(text))
                {
                    builder.Append(run.Text);
                }
                else
                {
                    Content(builder, text.Value);
                }

                break;
            case XComment comment:
                builder.Append("<!--").Append(comment.Value).Append("-->");
                break;
            case XProcessingInstruction instruction:
                builder.Append("<?").Append(instruction.Target);

                if (instruction.Data.Length > 0)
                {
                    builder.Append(' ').Append(instruction.Data);
                }

                builder.Append("?>");
                break;
        }
    }

    private static void Open(StringBuilder builder, XElement element, bool children, Tag? tag)
    {
        builder.Append('<').Append(Name(element));

        foreach (var attribute in element.Attributes())
        {
            builder.Append(' ').Append(Name(attribute)).Append("=\"");
            Value(builder, attribute.Value);
            builder.Append('"');
        }

        // The close the file gave this tag, where it had one. An element nobody wrote before is
        // closed the way the rest of the repository writes one, so a declaration added to a block
        // looks like the declarations already in it.
        builder.Append(tag is { } known && known.SelfClosed != children ? known.Tail : children ? ">" : " />");
    }

    private static string Name(XElement element)
    {
        var prefix = element.GetPrefixOfNamespace(element.Name.Namespace);

        return string.IsNullOrEmpty(prefix) ? element.Name.LocalName : prefix + ":" + element.Name.LocalName;
    }

    private static string Name(XAttribute attribute)
    {
        if (attribute.Name.Namespace == XNamespace.Xmlns)
        {
            return "xmlns:" + attribute.Name.LocalName;
        }

        if (attribute.Name.Namespace == XNamespace.None)
        {
            return attribute.Name.LocalName;
        }

        var prefix = attribute.Parent?.GetPrefixOfNamespace(attribute.Name.Namespace);

        return string.IsNullOrEmpty(prefix) ? attribute.Name.LocalName : prefix + ":" + attribute.Name.LocalName;
    }

    /// <remarks>
    /// A carriage return is escaped because a parser folds a literal one to a newline, so a value
    /// holding one would come back holding something else; a tab and a newline are escaped because
    /// attribute values are normalised to spaces. A <c>&gt;</c> is left alone -- it is only special
    /// where it closes a CDATA section, which is not here.
    /// </remarks>
    private static void Value(StringBuilder builder, string value, char quote = '"')
    {
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '&' => "&amp;",
                '<' => "&lt;",

                // Only the one holding it: a file that quoted with apostrophes may hold a " as
                // itself, and escaping the other would be a change to a value nobody edited.
                '"' when quote == '"' => "&quot;",
                '\'' when quote == '\'' => "&apos;",
                '\r' => "&#xD;",
                '\n' => "&#xA;",
                '\t' => "&#x9;",
                _ => character.ToString(),
            });
        }
    }

    private static void Content(StringBuilder builder, string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '\r':
                    builder.Append("&#xD;");
                    break;

                // Only where it would close a section that was never opened here.
                case '>' when index >= 2 && value[index - 1] == ']' && value[index - 2] == ']':
                    builder.Append("&gt;");
                    break;
                default:
                    builder.Append(value[index]);
                    break;
            }
        }
    }

    private sealed class Run
    {
        private readonly string _said;

        public Run(string text, string said)
        {
            Text = text;
            _said = said;
        }

        /// <summary>The run exactly as the file wrote it, entities and all.</summary>
        public string Text { get; }

        public bool Holds(XText text) => string.Equals(text.Value, _said, StringComparison.Ordinal);
    }

    /// <summary>One attribute inside a start tag: the whole of it, and the value within it.</summary>
    /// <remarks>
    /// Both, because the three things that happen to an attribute need different spans: a changed
    /// value is written over <see cref="Start"/>, a removed attribute takes its leading whitespace
    /// with it and so is cut at <see cref="RunStart"/>, and an added one is put before the close.
    /// </remarks>
    private readonly struct Slot
    {
        public Slot(int runStart, int runLength, int start, int length, char quote)
        {
            RunStart = runStart;
            RunLength = runLength;
            Start = start;
            Length = length;
            Quote = quote;
        }

        /// <summary>Where the attribute begins, counting the whitespace in front of its name.</summary>
        public int RunStart { get; }

        public int RunLength { get; }

        /// <summary>Where its value begins, past the opening quote.</summary>
        public int Start { get; }

        public int Length { get; }

        public char Quote { get; }
    }

    private sealed class Tag
    {
        private readonly string[] _names;
        private readonly string[] _values;
        private readonly Slot[] _slots;

        public Tag(
            string text,
            string name,
            bool selfClosed,
            string tail,
            string[] names,
            string[] values,
            Slot[] slots)
        {
            Text = text;
            Name = name;
            SelfClosed = selfClosed;
            Tail = tail;
            _names = names;
            _values = values;
            _slots = slots;
        }

        /// <summary>The start tag exactly as the file wrote it.</summary>
        public string Text { get; }

        /// <summary>The name as the file spells it, for the closing tag.</summary>
        public string Name { get; }

        public bool SelfClosed { get; }

        /// <summary>How the file closed this tag, whitespace and all.</summary>
        public string Tail { get; }

        /// <summary>
        /// The tag to write for this element, or null where it has to be written afresh.
        /// </summary>
        /// <remarks>
        /// The bytes it was read as while it still says the same thing; otherwise those bytes with
        /// the values written over, the attributes that went away cut out, and the ones that
        /// arrived put in before the close. Without this, changing one number on a root written
        /// across four lines would fold it onto one -- and declaring the namespace for the first
        /// parameter a drawing ever gets does exactly that to exactly that tag.
        /// </remarks>
        public string? Written(XElement element)
        {
            var names = Named(element);

            if (names[0] != _names[0])
            {
                return null;
            }

            var values = Valued(element);

            if (Same(_names, names) && Same(_values, values))
            {
                return Text;
            }

            // One slot per attribute read, or the scan did not follow the tag and writing by
            // position would put a value somewhere it does not belong.
            if (_slots.Length != _values.Length)
            {
                return null;
            }

            var attributes = element.Attributes().ToList();
            var taken = new int[_slots.Length];
            var added = new List<int>();
            var cursor = 0;

            Array.Fill(taken, -1);

            for (var index = 0; index < attributes.Count; index++)
            {
                var at = Array.IndexOf(_names, names[index + 1], cursor + 1) - 1;

                if (at < 0)
                {
                    added.Add(index);
                    continue;
                }

                taken[at] = index;
                cursor = at;
            }

            var builder = new StringBuilder();
            var written = 0;

            for (var slot = 0; slot < _slots.Length; slot++)
            {
                var where = _slots[slot];
                var index = taken[slot];

                if (index < 0)
                {
                    // Gone: cut it out with the whitespace that led up to it.
                    builder.Append(Text, written, where.RunStart - written);
                    written = where.RunStart + where.RunLength;
                    continue;
                }

                builder.Append(Text, written, where.Start - written);
                Value(builder, values[index], where.Quote);
                written = where.Start + where.Length;
            }

            builder.Append(Text, written, Text.Length - Tail.Length - written);

            foreach (var index in added)
            {
                builder.Append(' ').Append(Name(attributes[index])).Append("=\"");
                Value(builder, values[index]);
                builder.Append('"');
            }

            return builder.Append(Tail).ToString();
        }

        private static bool Same(string[] left, string[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (var index = 0; index < left.Length; index++)
            {
                if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
