// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using Svg.Expressions;

namespace Svg.Highlighting;

/// <summary>
/// Finds attribute values the SVG parser's own converters refuse.
/// </summary>
/// <remarks>
/// <para>
/// The half of a drawing the expression checker does not read. <c>width="abc"</c> is not merely
/// unreported today: the generated <c>SvgElement.SetValue</c> catches whatever the converter threw,
/// warns to <c>Trace</c> and returns <c>true</c>, so the property silently keeps its default and
/// nothing above it can tell that from a value that converted. Reading a drawing should not cost the
/// picture over one bad number, but a source view exists to say what is wrong with it.
/// </para>
/// <para>
/// Nothing here decides what is wrong with a value. <see cref="SvgElementFactory.FindAttributeFault"/>
/// asks the converter that attribute actually uses, and its refusal is the message; this half only
/// turns a place in a parsed tree back into a place in the text.
/// </para>
/// <para>
/// Which is the whole reason this reads the document a second time rather than looking at a loaded
/// one. An <c>SvgElement</c> holds no line, no column and no offset — the DOM cannot say where any
/// of it was written — so a diagnostic gathered while loading would have nowhere to point. The text
/// is also the only thing there is to check while someone is still typing it, which is when a source
/// view is most likely open and when the document most often will not load at all.
/// </para>
/// </remarks>
internal static class SvgSourceAttributes
{
    /// <summary>Reports the attribute values in <paramref name="source"/> that will not convert.</summary>
    /// <remarks>
    /// Silent rather than throwing for a document that cannot be read, for the reason
    /// <see cref="SvgSourceDiagnostics.Analyse"/> gives: what is wrong with a file is not a reason to
    /// take the file off the screen. A document that is not well-formed reports nothing here — saying
    /// so is a separate question from what a converter thinks of a value.
    /// </remarks>
    public static void Analyse(
        string source,
        IReadOnlyList<SvgSourceToken> tokens,
        List<SvgSourceDiagnostic> found)
    {
        XDocument document;

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(source),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });

            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return;
        }

        var positions = new SvgExpressionDeclarations.Positions(source);

        // One element per name for the whole document: which converter an attribute uses depends on
        // the element carrying it, and a drawing is mostly the same few names over and over.
        var probes = new Dictionary<string, SvgElement?>(StringComparer.Ordinal);

        // Nothing is loaded from it. A converter reached for a paint server wants a document to
        // resolve against, and one that has never read a file resolves nothing rather than throwing.
        var owner = new SvgDocument();

        foreach (var element in document.Descendants())
        {
            if (element.Name.NamespaceName is not (SvgNamespaces.SvgNamespace or ""))
            {
                // A foreign namespace is carried whole and converted by nothing.
                continue;
            }

            var name = element.Name.LocalName;

            if (!probes.TryGetValue(name, out var probe))
            {
                probe = SvgElementFactory.CreateProbe(name);
                probes[name] = probe;
            }

            if (probe is null)
            {
                continue;
            }

            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name.NamespaceName.Length == 0
                    && attribute.Name.LocalName == "style"
                    && Declarations(source, positions.Value(attribute), attribute.Value) is { } declarations)
                {
                    foreach (var declaration in declarations)
                    {
                        var refused = SvgElementFactory.FindStyleFault(
                            probe,
                            declaration.Name,
                            declaration.Value,
                            owner);

                        if (refused is not null)
                        {
                            // The span of the declaration rather than the token it sits in: a style
                            // attribute is one value to the splitter, and underlining all of it to
                            // say one declaration in six is wrong points at the wrong thing.
                            found.Add(new SvgSourceDiagnostic(
                                declaration.Start,
                                declaration.Length,
                                SvgSourceSeverity.Error,
                                refused));
                        }
                    }

                    continue;
                }

                var fault = SvgElementFactory.FindAttributeFault(
                    probe,
                    attribute.Name.NamespaceName,
                    attribute.Name.LocalName,
                    attribute.Value,
                    owner);

                if (fault is null)
                {
                    continue;
                }

                var at = positions.Value(attribute);

                found.Add(SvgSourceDiagnostics.Mark(at, source.Length, fault, tokens, source));
            }
        }
    }

    /// <summary>
    /// The declarations of a <c>style</c> attribute, placed in the document, or null where they
    /// cannot be.
    /// </summary>
    /// <remarks>
    /// The reader hands over a value with its entities already resolved, so <c>&amp;quot;</c> arrives
    /// as one character and every offset after it in that attribute is a character short. An
    /// ampersand anywhere in the raw span is enough to give up on placing the pieces -- and it would
    /// split them wrongly in any case, since the <c>;</c> ending an entity is not the <c>;</c>
    /// ending a declaration. The whole value is still checked as one attribute below, which is what
    /// this returning null asks for.
    /// </remarks>
    private static List<(string Name, string Value, int Start, int Length)>? Declarations(
        string source,
        int valueStart,
        string value)
    {
        if (valueStart <= 0 || valueStart >= source.Length)
        {
            return null;
        }

        var quote = source[valueStart - 1];

        if (quote is not ('"' or '\''))
        {
            return null;
        }

        var valueEnd = source.IndexOf(quote, valueStart);

        if (valueEnd < 0 || valueEnd - valueStart != value.Length)
        {
            return null;
        }

        var placed = new List<(string, string, int, int)>();

        foreach (var declaration in SvgInlineStyleAttributeParser.Split(value))
        {
            placed.Add((
                declaration.Name,
                declaration.Value,
                valueStart + declaration.ValueStart,
                declaration.ValueLength));
        }

        return placed;
    }
}
