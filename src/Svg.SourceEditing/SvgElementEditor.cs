// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Svg.Expressions;

namespace Svg.SourceEditing;

/// <summary>
/// Moves, adds, copies and removes the elements of a drawing on the tree.
/// </summary>
/// <remarks>
/// A span and not a rewritten document, for the reason everything here is: a document regenerated
/// from a parsed tree comes back without the author's formatting, attribute order or comments.
///
/// Beside <see cref="SvgDeclarationEditor"/> rather than inside it — that type is about the
/// <c>&lt;e:code&gt;</c> block and is long enough — but built from its helpers, which already know
/// how to measure a whole element, take the line carrying it, and read a document's own indent.
/// </remarks>
/// <summary>Where a drop puts what is being moved.</summary>
public enum SvgElementDrop
{
    Before,
    After,
    Inside
}

public static class SvgElementEditor
{
    /// <inheritdoc cref="Move(string, string, string, SvgElementDrop)"/>
    /// <returns>The sentence refusing the move, or null where it was made.</returns>
    /// <remarks>
    /// The element itself moves, rather than the line of text carrying it, so what it holds travels
    /// with it without being cut out and written again. Three refusals the span version gives are
    /// gone: a tree has no lines to share, so a minified drawing can now be rearranged; a target
    /// that closes itself is opened by the writer when it gains a child; and a splice into a tree
    /// cannot produce markup nobody can read.
    /// </remarks>
    public static string? Move(
        SvgSourceDocument source,
        string addressKey,
        string targetKey,
        SvgElementDrop where)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (SvgAttributeEditor.Resolve(source.Document, addressKey) is not { } moved ||
            SvgAttributeEditor.Resolve(source.Document, targetKey) is not { } target)
        {
            return "One of those is not in this drawing any more.";
        }

        if (moved.Parent is null)
        {
            return "The drawing itself cannot be moved.";
        }

        if (ReferenceEquals(moved, target) || target.AncestorsAndSelf().Contains(moved))
        {
            return $"<{moved.Name.LocalName}> cannot be put inside itself.";
        }

        // Beside the drawing itself means inside it: the root has no siblings.
        if (target.Parent is null)
        {
            where = SvgElementDrop.Inside;
        }

        var into = where == SvgElementDrop.Inside ? target : target.Parent!;

        if (Holds(into, moved) is { } cannot)
        {
            return cannot;
        }

        if (!ReferenceEquals(Shelter(moved), Shelter(into.Elements().FirstOrDefault() ?? into)) &&
            !ReferenceEquals(Shelter(moved), into.AncestorsAndSelf().FirstOrDefault(Kept)))
        {
            return "That would move it across a <defs>, a <clipPath> or a <mask>, which changes what the drawing paints.";
        }

        // Both measured before anything moves. Once the element is out, the whitespace that told
        // us where it sat has gone with it, and the target's own run has changed too.
        var was = Indent(moved);
        var now = where == SvgElementDrop.Inside ? Indent(target) + source.IndentUnit : Indent(target);

        Cut(moved);
        Put(target, where, moved, now);
        Reindent(moved, was, now);

        return null;
    }

    /// <summary>Writes a new element where a drop says, on a line of its own.</summary>
    /// <remarks>
    /// The element is built by the caller in no namespace and given the drawing's here, so a
    /// <c>&lt;g&gt;</c> made beside a file whose tree holds it under the SVG namespace lands in that
    /// namespace too — a bare name would disagree with every later lookup. Blank text inside it is
    /// taken as written at depth zero and moved to the depth it lands at, so an element holding no
    /// nodes at all is written as <c>&lt;g/&gt;</c>, and one given a single break as a pair of tags.
    /// </remarks>
    /// <returns>The sentence refusing it, or null where it was written.</returns>
    public static string? Insert(
        SvgSourceDocument source,
        string targetKey,
        SvgElementDrop where,
        XElement element,
        out string? addressKey)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        addressKey = null;

        if (SvgAttributeEditor.Resolve(source.Document, targetKey) is not { } target)
        {
            return "That is not in this drawing any more.";
        }

        if (target.Parent is null)
        {
            where = SvgElementDrop.Inside;
        }

        var into = where == SvgElementDrop.Inside ? target : target.Parent!;

        if (Holds(into, element) is { } cannot)
        {
            return cannot;
        }

        var indent = where == SvgElementDrop.Inside ? Indent(target) + source.IndentUnit : Indent(target);

        foreach (var named in element.DescendantsAndSelf())
        {
            if (named.Name.Namespace == XNamespace.None)
            {
                named.Name = into.Name.Namespace + named.Name.LocalName;
            }
        }

        Reindent(element, string.Empty, indent);
        Put(target, where, element, indent);

        addressKey = SvgAttributeEditor.Key(element);

        return null;
    }

    /// <summary>Takes elements out of the drawing, each with the line that carried it.</summary>
    /// <returns>The sentence refusing it, or null where they were removed.</returns>
    public static string? Remove(SvgSourceDocument source, IReadOnlyList<string> addressKeys)
    {
        if (Chosen(source, addressKeys, "deleted", out var elements) is { } cannot)
        {
            return cannot;
        }

        foreach (var element in elements)
        {
            Cut(element);
        }

        return null;
    }

    /// <summary>Writes a copy of each element directly after it, at the same depth.</summary>
    /// <remarks>
    /// Every id in a copy is renamed to one the drawing does not have, because the parser renames a
    /// duplicate silently on the way in and the file would then disagree with what was drawn. What
    /// refers to an id is left pointing at the original, which paints the same.
    /// </remarks>
    /// <returns>The sentence refusing it, or null where the copies were written.</returns>
    public static string? Duplicate(
        SvgSourceDocument source,
        IReadOnlyList<string> addressKeys,
        out IReadOnlyList<string> copies)
    {
        copies = Array.Empty<string>();

        if (Chosen(source, addressKeys, "duplicated", out var elements) is { } cannot)
        {
            return cannot;
        }

        foreach (var element in elements)
        {
            if (element.Parent is { } parent && IsText(parent))
            {
                return $"What a <{parent.Name.LocalName}> holds is one run of text, so a part of it cannot be duplicated.";
            }
        }

        var ids = new HashSet<string>(
            source.Document.Descendants().Select(element => (string?)element.Attribute("id")).OfType<string>(),
            StringComparer.Ordinal);
        var made = new List<XElement>(elements.Count);

        foreach (var original in elements)
        {
            var copy = SvgSourceDocument.Copy(original);

            foreach (var named in copy.DescendantsAndSelf())
            {
                if (named.Attribute("id") is { } id && !id.Value.Contains("{{", StringComparison.Ordinal))
                {
                    id.Value = Free(id.Value, ids);
                }
            }

            Put(original, SvgElementDrop.After, copy, Indent(original));
            made.Add(copy);
        }

        // Only once every copy is in: each one shifts the addresses after it.
        copies = made.Select(SvgAttributeEditor.Key).ToList();

        return null;
    }

    /// <summary>The first of <c>id-2</c>, <c>id-3</c>… the drawing does not have, taken.</summary>
    private static string Free(string id, HashSet<string> ids)
    {
        for (var n = 2; ; n++)
        {
            var candidate = id + "-" + n.ToString(CultureInfo.InvariantCulture);

            if (ids.Add(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// The elements a selection names, outermost only, or the sentence refusing the selection.
    /// </summary>
    /// <remarks>
    /// A child whose parent is also named goes with the parent, so it is not acted on twice. The
    /// declarations are the Parameters panel's: a <c>&lt;defs&gt;</c> holding the block is refused
    /// as a whole, since cutting or doubling it is what the block was put there to prevent.
    /// </remarks>
    private static string? Chosen(
        SvgSourceDocument source,
        IReadOnlyList<string> addressKeys,
        string verb,
        out List<XElement> elements)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        elements = new List<XElement>(addressKeys.Count);

        foreach (var key in addressKeys)
        {
            if (SvgAttributeEditor.Resolve(source.Document, key) is not { } element)
            {
                return "That is not in this drawing any more.";
            }

            if (element.Parent is null)
            {
                return $"The drawing itself cannot be {verb}.";
            }

            if (element.DescendantsAndSelf().Any(held => held.Name.Namespace == Ns))
            {
                return $"The declarations cannot be {verb} here: the Parameters panel edits them.";
            }

            elements.Add(element);
        }

        var chosen = elements;

        elements = chosen.Where(element => !element.Ancestors().Any(chosen.Contains)).ToList();

        return null;
    }

    private static readonly XNamespace Ns = SvgExpressionDeclarations.Namespace;

    /// <summary>The whitespace an element is written after, or nothing where it shares its line.</summary>
    /// <remarks>
    /// The same answer <c>LeadingWhitespace</c> gives about the text, read off the tree instead.
    /// A CDATA section is a run of text to the type system and is not indentation to anybody.
    /// </remarks>
    internal static string Indent(XNode node)
    {
        if (node.PreviousNode is not XText text || node.PreviousNode is XCData)
        {
            return string.Empty;
        }

        var at = text.Value.LastIndexOf('\n');

        if (at < 0)
        {
            return string.Empty;
        }

        var run = text.Value.Substring(at + 1);

        return Blank(run) ? run : string.Empty;
    }

    /// <summary>Takes an element out, and the one line break that was carrying it.</summary>
    /// <remarks>
    /// One break and not the whole run, so a blank line somebody wrote above the element stays
    /// where they put it rather than closing up behind what was removed.
    /// </remarks>
    internal static void Cut(XElement element)
    {
        if (element.PreviousNode is XText text && element.PreviousNode is not XCData)
        {
            var at = text.Value.LastIndexOf('\n');

            if (at >= 0 && Blank(text.Value.Substring(at)))
            {
                text.Value = text.Value.Substring(0, at);
            }
        }

        element.Remove();
    }

    /// <summary>Puts an element where a drop says, on a line of its own.</summary>
    /// <remarks>
    /// The break and the element go in together, because adding them one after the other reverses
    /// them. Always a newline and never the file's own line ending: a reader folds every ending to
    /// one before the tree sees it, so a carriage return written here would be escaped as
    /// <c>&amp;#xD;</c>, and the ones a file had are put back when it is written.
    /// </remarks>
    internal static void Put(XElement target, SvgElementDrop where, XElement placed, string indent)
    {
        switch (where)
        {
            // The break goes after what is put in and not in front of it: the whitespace already
            // carrying the target is what carries this now, and adding another would leave a blank
            // line where the two met.
            case SvgElementDrop.Before:
                target.AddBeforeSelf(placed, new XText("\n" + indent));
                break;

            case SvgElementDrop.After:
                target.AddAfterSelf(new XText("\n" + indent), placed);
                break;

            default:
                // In front of the whitespace that closes the tag, or the closing tag ends up on the
                // same line as what was just put in.
                if (target.LastNode is XText last && target.LastNode is not XCData && Blank(last.Value))
                {
                    last.AddBeforeSelf(new XText("\n" + indent), placed);
                }
                else
                {
                    // Nothing was in it, so it has no line to close on either.
                    target.Add(new XText("\n" + indent), placed, new XText("\n" + Indent(target)));
                }

                break;
        }
    }

    /// <summary>The drawing's <c>&lt;defs&gt;</c>, made first in the root where it has none.</summary>
    public static XElement Defs(SvgSourceDocument source)
    {
        if (source?.Document.Root is not { } root)
        {
            throw new ArgumentException("The source has no root element.", nameof(source));
        }

        if (root.Elements().FirstOrDefault(element => element.Name == root.Name.Namespace + "defs") is { } existing)
        {
            return existing;
        }

        var defs = new XElement(root.Name.Namespace + "defs");

        First(root, defs, Indent(root) + source.IndentUnit);

        return defs;
    }

    /// <summary>Puts an element first inside a parent, on a line of its own.</summary>
    /// <remarks>
    /// What a declaration block needs and a drop does not: the &lt;e:code&gt; goes at the top of the
    /// &lt;defs&gt; rather than beside a sibling somebody pointed at.
    /// </remarks>
    internal static void First(XElement parent, XElement placed, string indent)
    {
        if (parent.FirstNode is { } first)
        {
            first.AddBeforeSelf(new XText("\n" + indent), placed);
        }
        else
        {
            parent.Add(new XText("\n" + indent), placed, new XText("\n" + Indent(parent)));
        }
    }

    /// <summary>Writes a moved subtree at its new depth.</summary>
    /// <remarks>
    /// Only the runs of whitespace between its elements, so a newline inside an attribute value or
    /// inside a run of text is left alone — which the replacement over an element's written form
    /// was not careful about.
    /// </remarks>
    private static void Reindent(XElement element, string was, string now)
    {
        if (string.Equals(was, now, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var text in element.DescendantNodes().OfType<XText>().ToList())
        {
            if (text is not XCData && Blank(text.Value))
            {
                text.Value = text.Value.Replace("\n" + was, "\n" + now);
            }
        }
    }

    internal static bool Blank(string value)
    {
        foreach (var character in value)
        {
            if (character is not (' ' or '\t' or '\n' or '\r'))
            {
                return false;
            }
        }

        return true;
    }
    private static bool Kept(XElement element)
        => element.Name.LocalName is
            "defs" or "clipPath" or "mask" or "marker" or "pattern" or "symbol" or
            "linearGradient" or "radialGradient" or "filter";

    /// <summary>
    /// What <paramref name="element"/> is kept inside, or null where it is on the canvas.
    /// </summary>
    /// <remarks>
    /// Two elements may be grouped inside a <c>&lt;defs&gt;</c> together, and on the canvas together,
    /// but not one of each: the group would have to go one side of the line and would take the other
    /// element out of the drawing, or into it.
    /// </remarks>
    private static XElement? Shelter(XElement element) => element.Ancestors().FirstOrDefault(Kept);

    /// <summary>Why <paramref name="parent"/> cannot hold <paramref name="placed"/>, or null where it can.</summary>
    /// <remarks>
    /// A shape is not content these take: a clip path stops clipping, a gradient stops having
    /// stops, and a run of text stops being one run.
    /// </remarks>
    private static string? Holds(XElement parent, XElement placed)
        => parent.Name.LocalName is "clipPath" or "linearGradient" or "radialGradient" or "filter" || IsText(parent)
            ? $"A <{parent.Name.LocalName}> cannot hold a <{placed.Name.LocalName}>."
            : null;

    private static bool IsText(XElement element) => element.Name.LocalName is "text" or "tspan" or "textPath";
}
