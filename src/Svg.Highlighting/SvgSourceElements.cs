// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using Svg.Expressions;

namespace Svg.Highlighting;

/// <summary>One element of a drawing, and the start tag it is written as.</summary>
/// <remarks>
/// <see cref="Name"/> is carried so a caller can check it landed where it meant to. Nothing
/// correlates this text with a parsed document — the two are read separately — and the check is what
/// turns a disagreement into nothing happening rather than a jump to the wrong line.
/// </remarks>
public readonly record struct SvgSourceElement(string Name, int Start, int Length);

/// <summary>
/// Where each element of a drawing is written in its text.
/// </summary>
/// <remarks>
/// An <see cref="SvgElement"/> holds no source position — the parser never asks the reader for one —
/// so a host that wants to show an element in the file has nothing to go on. This reads the text a
/// second time for it, the way <see cref="SvgSourceAttributes"/> already does to find attribute
/// values a converter would refuse.
///
/// Keyed by the child-index path <see cref="SvgElementAddress.Key"/> spells, because that is the one
/// name both sides can produce: this side counts <see cref="XElement"/>s, the other counts
/// <c>SvgElement.Children</c>, and neither counts a comment or a run of text. It is also the key
/// that survives the document being rebuilt from edited text.
/// </remarks>
public static class SvgSourceElements
{
    /// <summary>Every element in <paramref name="source"/>, by address.</summary>
    /// <remarks>
    /// Empty for a document that will not parse, which is what a drawing looks like for most of the
    /// time somebody is typing one. Reporting that is <see cref="SvgSourceDiagnostics"/>'s job.
    /// </remarks>
    public static IReadOnlyDictionary<string, SvgSourceElement> Map(string? source)
    {
        var found = new Dictionary<string, SvgSourceElement>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(source))
        {
            return found;
        }

        XDocument document;

        try
        {
            // The loader's own settings, as in SvgSourceAttributes: four W3C fixtures declare their
            // shapes as entities in an internal subset, and ignoring the DTD would fail to read a
            // file that opens perfectly.
            using var reader = XmlReader.Create(
                new StringReader(source!),
                new XmlReaderSettings
                {
                    DtdProcessing = SvgDocument.DisableDtdProcessing ? DtdProcessing.Ignore : DtdProcessing.Parse,
                    XmlResolver = new SvgDtdResolver(),
                });

            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return found;
        }

        if (document.Root is not { } root)
        {
            return found;
        }

        Walk(root, string.Empty, new SvgExpressionDeclarations.Positions(source!), source!, found);

        return found;
    }

    private static void Walk(
        XElement element,
        string addressKey,
        SvgExpressionDeclarations.Positions positions,
        string source,
        Dictionary<string, SvgSourceElement> found)
    {
        if (Place(element, positions, source) is { } placed)
        {
            found[addressKey] = placed;
        }

        var index = 0;

        foreach (var child in element.Elements())
        {
            Walk(
                child,
                // Invariant, because SvgElementAddress.Key is: the two have to spell the same path.
                addressKey.Length == 0
                    ? index.ToString(CultureInfo.InvariantCulture)
                    : addressKey + "/" + index.ToString(CultureInfo.InvariantCulture),
                positions,
                source,
                found);

            index++;
        }
    }

    /// <summary>The whole start tag, angle brackets included, or nothing where it cannot be found.</summary>
    private static SvgSourceElement? Place(XElement element, SvgExpressionDeclarations.Positions positions, string source)
    {
        // The reader points at the name, which is one past the '<' that opens the tag.
        var name = positions.Of(element, null);

        if (name <= 0 || name >= source.Length || source[name - 1] != '<')
        {
            return null;
        }

        var end = EndOfTag(source, name);

        return end < 0
            ? null
            : new SvgSourceElement(element.Name.LocalName, name - 1, end - name + 2);
    }

    /// <summary>Where the start tag beginning at <paramref name="from"/> closes.</summary>
    /// <remarks>
    /// Scanned rather than taken from the token list, because the only thing that can hide a
    /// <c>&gt;</c> inside a start tag is an attribute value, and an entity arrives here still
    /// written as <c>&amp;gt;</c>. A tag that never closes is a document that did not parse.
    /// </remarks>
    private static int EndOfTag(string source, int from)
    {
        var quote = '\0';

        for (var index = from; index < source.Length; index++)
        {
            var character = source[index];

            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;

                continue;
            }

            if (character == '>')
            {
                return index;
            }
        }

        return -1;
    }
}
