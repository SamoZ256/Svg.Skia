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
/// The half of a drawing the expression checker does not read. <c>width="abc"</c> goes unreported:
/// <c>SvgElement.SetValue</c> catches whatever the converter threw, warns to <c>Trace</c> and
/// returns <c>true</c>, so the property keeps its default and nothing above can tell. It reads the
/// text a second time rather than a loaded document because an <c>SvgElement</c> holds no position,
/// and text is the only thing there is to check while someone is still typing.
/// </remarks>
internal static class SvgSourceAttributes
{
    /// <summary>Reports the attribute values in <paramref name="source"/> that will not convert.</summary>
    /// <remarks>
    /// Returns why rather than throwing: what is wrong with a file is not a reason to take it off the
    /// screen. Nothing is added to <paramref name="found"/> — where to mark it is the caller's.
    /// </remarks>
    /// <returns>Null when the document is well-formed; what the reader refused otherwise.</returns>
    public static XmlException? Analyse(
        string source,
        IReadOnlyList<SvgSourceToken> tokens,
        List<SvgSourceDiagnostic> found)
    {
        XDocument document;

        try
        {
            // The loader's own settings, because anything stricter invents faults: four W3C
            // fixtures declare their shapes as entities in an internal subset, and ignoring the DTD
            // turns every use into `Reference to undeclared entity` in a file that opens perfectly.
            using var reader = XmlReader.Create(
                new StringReader(source),
                new XmlReaderSettings
                {
                    DtdProcessing = SvgDocument.DisableDtdProcessing ? DtdProcessing.Ignore : DtdProcessing.Parse,
                    XmlResolver = new SvgDtdResolver(),
                });

            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException malformed)
        {
            return malformed;
        }

        var positions = new SvgExpressionDeclarations.Positions(source);

        // One element per name for the whole document: which converter an attribute uses depends on
        // the element carrying it, and a drawing is mostly the same few names over and over.
        var probes = new Dictionary<string, SvgElement?>(StringComparer.Ordinal);

        // Read before anything is checked, because a reference may name an id declared further down
        // the file than the attribute holding it.
        var ids = new HashSet<string>(StringComparer.Ordinal);

        var scripted = false;

        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName == "script")
            {
                // A document that runs code can make an id after it is read, so this pass cannot
                // know what will exist by the time anything is drawn.
                scripted = true;
            }

            if ((string?)element.Attribute("id") is { Length: > 0 } id && !ids.Add(id)
                && element.Attribute("id") is { } repeated)
            {
                // The manager keeps the first id it was given, so a repeat quietly decides what
                // every url(#…) means. A warning, because the drawing still opens.
                found.Add(SvgSourceDiagnostics.Mark(
                    positions.Value(repeated),
                    source.Length,
                    $"The id '{id}' is already used in this drawing, and a reference to it finds the first.",
                    tokens,
                    source,
                    SvgSourceSeverity.Warning));
            }
        }

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
                // No element to ask what its attributes mean. Children are still visited: a real
                // <rect> inside a misspelt group still has values that must convert.
                if (SvgElementFactory.FindElementFault(name) is { } unknown)
                {
                    found.Add(SvgSourceDiagnostics.Mark(
                        positions.Of(element, null),
                        source.Length,
                        unknown,
                        tokens,
                        source,
                        SvgSourceSeverity.Warning));
                }

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
                            // The declaration's span, not the token's: a style attribute is one
                            // value to the splitter, and underlining all of it points at nothing.
                            found.Add(new SvgSourceDiagnostic(
                                declaration.Start,
                                declaration.Length,
                                SvgSourceSeverity.Error,
                                refused));
                        }
                    }

                    continue;
                }

                if (!scripted
                    && SvgElementFactory.FindReferencedId(attribute.Name.LocalName, attribute.Value) is { } referenced
                    && !ids.Contains(referenced))
                {
                    found.Add(SvgSourceDiagnostics.Mark(
                        positions.Value(attribute),
                        source.Length,
                        $"Nothing in this drawing has the id '{referenced}'.",
                        tokens,
                        source));
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

        return null;
    }

    /// <summary>
    /// The declarations of a <c>style</c> attribute, placed in the document, or null where they
    /// cannot be.
    /// </summary>
    /// <remarks>
    /// Entities arrive already resolved, so <c>&amp;quot;</c> is one character and every offset after
    /// it is short. An ampersand anywhere in the span gives up on placing the pieces — the <c>;</c>
    /// ending an entity is not the <c>;</c> ending a declaration.
    /// </remarks>
    internal static List<(string Name, string Value, int Start, int Length)>? Declarations(
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
