// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

        if (Read(source) is not { } root)
        {
            return found;
        }

        Walk(root, string.Empty, new SvgExpressionDeclarations.Positions(source!), source!, found);

        return found;
    }

    /// <summary>
    /// Every element of the document <paramref name="built"/> holds, placed in
    /// <paramref name="source"/>.
    /// </summary>
    /// <remarks>
    /// For a drawing shown as its file but drawn from something made of it — an svgc project
    /// applying a recipe. The recipe injects a declarations block at the front, and an address is a
    /// path of child indices, so every element after it answers to a different address in the two.
    /// Mapping the file alone left every row of every drawing under a recipe finding nothing.
    ///
    /// A rewrite only inserts: it does not remove, rename or reorder. So the two are walked
    /// together and a built element with no counterpart in the file is passed over. Anything else
    /// they disagree about gives the plain map back rather than a guess — a lookup that misses
    /// moves nothing, which is what this side is built to fail as.
    /// </remarks>
    public static IReadOnlyDictionary<string, SvgSourceElement> Map(string? source, string? built)
    {
        if (built is null || string.Equals(source, built, StringComparison.Ordinal))
        {
            return Map(source);
        }

        if (Read(source) is not { } file || Read(built) is not { } drawn)
        {
            return Map(source);
        }

        var found = new Dictionary<string, SvgSourceElement>(StringComparer.Ordinal);

        return Pair(file, drawn, string.Empty, string.Empty, new SvgExpressionDeclarations.Positions(source!), source!, found, null)
            ? found
            : Map(source);
    }

    /// <summary>
    /// What each element of <paramref name="built"/> is addressed as in <paramref name="source"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Map(string?, string?)"/> answers where an element is written; this answers what to
    /// call it when writing back. An editor addresses the element it is changing, and the address it
    /// was handed is the built document's — a recipe's injected block having shifted every index
    /// after it.
    ///
    /// The same walk, and the same refusal: where the two are not one document with insertions, the
    /// file's own addresses are given back rather than a guess.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Addresses(string? source, string? built)
    {
        var itself = new Dictionary<string, string>(StringComparer.Ordinal);

        if (built is null || string.Equals(source, built, StringComparison.Ordinal))
        {
            foreach (var key in Map(source).Keys)
            {
                itself[key] = key;
            }

            return itself;
        }

        if (Read(source) is not { } file || Read(built) is not { } drawn)
        {
            return Addresses(source, null);
        }

        var paired = new Dictionary<string, string>(StringComparer.Ordinal);

        return Pair(
            file,
            drawn,
            string.Empty,
            string.Empty,
            new SvgExpressionDeclarations.Positions(source!),
            source!,
            new Dictionary<string, SvgSourceElement>(StringComparer.Ordinal),
            paired)
            ? paired
            : Addresses(source, null);
    }

    /// <summary>
    /// Walks one element of the file beside the one it became, keyed as the built document has it.
    /// </summary>
    /// <returns>Whether every element of the file was accounted for.</returns>
    private static bool Pair(
        XElement source,
        XElement built,
        string sourceKey,
        string builtKey,
        SvgExpressionDeclarations.Positions positions,
        string text,
        Dictionary<string, SvgSourceElement> found,
        Dictionary<string, string>? paired)
    {
        if (source.Name != built.Name)
        {
            return false;
        }

        if (Place(source, positions, text) is { } placed)
        {
            found[builtKey] = placed;
        }

        if (paired is { })
        {
            paired[builtKey] = sourceKey;
        }

        var mine = source.Elements().ToList();
        var theirs = built.Elements().ToList();

        var next = 0;

        for (var index = 0; index < theirs.Count && next < mine.Count; index++)
        {
            // Not in the file, so it is what the rewrite put there — the declarations block, which
            // has no place to be shown and whose row rightly finds nothing.
            if (theirs[index].Name != mine[next].Name)
            {
                continue;
            }

            if (!Pair(
                    mine[next],
                    theirs[index],
                    Child(sourceKey, next),
                    Child(builtKey, index),
                    positions,
                    text,
                    found,
                    paired))
            {
                return false;
            }

            next++;
        }

        // Anything left over means they are not the same document with insertions, and every
        // address from here on would be a guess.
        return next == mine.Count;
    }

    /// <summary>One step down a path.</summary>
    /// <remarks>
    /// Invariant, because <c>SvgElementAddress.Key</c> is: the two have to spell the same path, and
    /// the three walks here have to spell it the same way as each other.
    /// </remarks>
    private static string Child(string addressKey, int index)
        => addressKey.Length == 0
            ? index.ToString(CultureInfo.InvariantCulture)
            : addressKey + "/" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The root of <paramref name="text"/>, or null where it will not parse.</summary>
    /// <remarks>
    /// The loader's own settings, as in <see cref="SvgSourceAttributes"/>: four W3C fixtures declare
    /// their shapes as entities in an internal subset, and ignoring the DTD would fail to read a
    /// file that opens perfectly.
    /// </remarks>
    private static XElement? Read(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(text!),
                new XmlReaderSettings
                {
                    DtdProcessing = SvgDocument.DisableDtdProcessing ? DtdProcessing.Ignore : DtdProcessing.Parse,
                    XmlResolver = new SvgDtdResolver(),
                });

            return XDocument.Load(reader, LoadOptions.SetLineInfo).Root;
        }
        catch (XmlException)
        {
            return null;
        }
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
            Walk(child, Child(addressKey, index), positions, source, found);

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
