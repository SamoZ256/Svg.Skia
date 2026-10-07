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
            if (Parted(element, "duplicated") is { } parted)
            {
                return parted;
            }
        }

        var ids = Ids(source);
        var made = new List<XElement>(elements.Count);

        foreach (var original in elements)
        {
            var copy = Renamed(SvgSourceDocument.Copy(original), ids);

            Put(original, SvgElementDrop.After, copy, Indent(original));
            made.Add(copy);
        }

        // Only once every copy is in: each one shifts the addresses after it.
        copies = made.Select(SvgAttributeEditor.Key).ToList();

        return null;
    }

    /// <summary>
    /// Clips or masks an element with another: a <c>&lt;clipPath&gt;</c> or <c>&lt;mask&gt;</c> by
    /// reference, and a shape by moving it into a new one made for it.
    /// </summary>
    /// <param name="property"><c>clip-path</c> or <c>mask</c>.</param>
    /// <param name="carried">
    /// A transform written in front of the shape's own, which keeps a shape drawn on the canvas where
    /// it was once the target's space draws it; null where it is taken as written.
    /// </param>
    /// <param name="held">The row to pick afterwards: the shape, or the target where one was only pointed at.</param>
    /// <param name="region">
    /// For a new mask, the box it shows in the target's own space, spelt as a viewBox is; null keeps
    /// the default round the target's bounding box, which a straight line has none of.
    /// </param>
    /// <returns>The sentence refusing it, or null where it was made.</returns>
    /// <remarks>
    /// A mask made from a shape masks by its alpha: a drawn shape mostly carries no fill, paints black
    /// inside a mask, and by luminance would hide the whole element. A shape something else draws,
    /// itself or through a group around it, is copied rather than moved so that drawing stays as it
    /// was. What the target was clipped with before stays where it is, as when another
    /// <c>url(#…)</c> is picked.
    /// </remarks>
    public static string? Clip(
        SvgSourceDocument source,
        string targetKey,
        string property,
        string contentKey,
        string? carried,
        out string? held,
        string? region = null)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        held = null;

        var mask = Masks(property);
        var noun = mask ? "mask" : "clip path";
        var verb = mask ? "mask" : "clip";

        if (SvgAttributeEditor.Resolve(source.Document, targetKey) is not { } target ||
            SvgAttributeEditor.Resolve(source.Document, contentKey) is not { } content)
        {
            return "One of those is not in this drawing any more.";
        }

        if (Unclipped(target, property) is { } unclipped)
        {
            return unclipped;
        }

        if (Chosen(source, new[] { contentKey }, "made into a " + noun, out _) is { } unchosen)
        {
            return unchosen;
        }

        if (ReferenceEquals(content, target))
        {
            return $"An element cannot {verb} itself.";
        }

        if (target.Ancestors().Contains(content))
        {
            return $"A <{content.Name.LocalName}> holds the <{target.Name.LocalName}> it would {verb}.";
        }

        var ids = Ids(source);

        if (content.Name.LocalName is "clipPath" or "mask")
        {
            if (content.Name.LocalName != (mask ? "mask" : "clipPath"))
            {
                return mask ? "A <clipPath> cannot be used as a mask." : "A <mask> cannot be used as a clip path.";
            }

            var id = (string?)content.Attribute("id");

            if (id is { } && id.Contains("{{", StringComparison.Ordinal))
            {
                return $"This <{content.Name.LocalName}> is named by an expression, so nothing can point at it here.";
            }

            if (id is not { Length: > 0 })
            {
                content.SetAttributeValue("id", id = Free(verb, ids));
            }

            target.SetAttributeValue(property, $"url(#{id})");
            held = SvgAttributeEditor.Key(target);

            return null;
        }

        if (Parted(content, "made into a " + noun) is { } parted)
        {
            return parted;
        }

        var shelter = Shelter(content);

        if (shelter is { } && shelter.Name.LocalName != "defs")
        {
            return $"That is part of a <{shelter.Name.LocalName}>, so it cannot be made into a {noun} of its own.";
        }

        if (!Clippable(content.Name.LocalName, "clip-path"))
        {
            return $"A <{content.Name.LocalName}> draws nothing of its own, so it cannot be made into a {noun}.";
        }

        var wrapper = Wrapper(target, mask, ids, region);

        if (Holds(wrapper, content) is { } cannot)
        {
            return cannot;
        }

        // Browsers and the renderer alike clip with nothing where a <use> draws anything but a shape or text.
        if (!mask && content.Name.LocalName == "use" && Used(content) is var used && (used is null || !Clips(used.Name.LocalName)))
        {
            return used is null
                ? "That <use> draws nothing in this drawing, so it cannot be made into a clip path."
                : $"A clip path takes no <{used.Name.LocalName}>, which is what that <use> draws; use it as a mask instead.";
        }

        carried = string.IsNullOrWhiteSpace(carried) ? null : carried;

        if (carried is { } && SvgAttributeEditor.Overridden(content, "transform") is { } styled)
        {
            return styled;
        }

        if (mask)
        {
            wrapper.SetAttributeValue("mask-type", "alpha");
        }

        var copied = Referenced(content);
        var shape = copied ? Renamed(SvgSourceDocument.Copy(content), ids) : content;
        var was = Indent(content);
        var inherited = Inherited(content, leaving: shelter is null, mask).ToList();
        var (anchor, where) = shelter is null
            ? (Defs(source), SvgElementDrop.Inside)
            : (content, copied ? SvgElementDrop.After : SvgElementDrop.Before);

        // Never refused: the shelter test and Parted above leave no text, gradient or clip path to land in.
        Insert(source, SvgAttributeEditor.Key(anchor), where, wrapper, out _);

        if (!copied)
        {
            Cut(content);
        }

        var now = Indent(wrapper) + source.IndentUnit;

        Put(wrapper, SvgElementDrop.Inside, shape, now);
        Reindent(shape, was, now);

        if (carried is { })
        {
            shape.SetAttributeValue("transform", (carried + " " + (string?)shape.Attribute("transform")).Trim());
        }

        foreach (var (name, value) in inherited)
        {
            shape.SetAttributeValue(name, value);
        }

        target.SetAttributeValue(property, $"url(#{(string?)wrapper.Attribute("id")})");
        held = SvgAttributeEditor.Key(shape);

        return null;
    }

    /// <summary>Clips or masks an element with a new clip path or mask holding <paramref name="seed"/>.</summary>
    /// <remarks>
    /// The seed is built in no namespace, as for <see cref="Insert"/>. A mask is left masking by
    /// luminance here, since the caller chose what the seed paints.
    /// </remarks>
    /// <param name="region"><inheritdoc cref="Clip(SvgSourceDocument, string, string, string, string?, out string?, string?)" path="/param[@name='region']"/></param>
    /// <returns>The sentence refusing it, or null where it was made.</returns>
    public static string? Clip(
        SvgSourceDocument source,
        string targetKey,
        string property,
        XElement seed,
        out string? held,
        string? region = null)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (seed is null)
        {
            throw new ArgumentNullException(nameof(seed));
        }

        held = null;

        var mask = Masks(property);

        if (SvgAttributeEditor.Resolve(source.Document, targetKey) is not { } target)
        {
            return "That is not in this drawing any more.";
        }

        if (Unclipped(target, property) is { } unclipped)
        {
            return unclipped;
        }

        var wrapper = Wrapper(target, mask, Ids(source), region);

        if (Holds(wrapper, seed) is { } cannot)
        {
            return cannot;
        }

        Insert(source, SvgAttributeEditor.Key(Defs(source)), SvgElementDrop.Inside, wrapper, out var kept);
        target.SetAttributeValue(property, $"url(#{(string?)wrapper.Attribute("id")})");

        return Insert(source, kept!, SvgElementDrop.Inside, seed, out held);
    }

    private static bool Masks(string property)
        => property switch
        {
            "mask" => true,
            "clip-path" => false,
            _ => throw new ArgumentException($"'{property}' is neither 'clip-path' nor 'mask'.", nameof(property))
        };

    /// <summary>Why <paramref name="target"/> cannot be given a clip path or mask, or null where it can.</summary>
    /// <remarks>
    /// The drawing itself has no place of its own on the canvas to cover or to carry a shape into.
    /// Clip content is only an outline, so a mask on it is ignored, though a clip path is not.
    /// </remarks>
    private static string? Unclipped(XElement target, string property)
    {
        var name = target.Name.LocalName;

        if (target.Parent is null)
        {
            return "The drawing itself cannot be clipped or masked here.";
        }

        if (!Clippable(name, property))
        {
            return Clippable(name, "clip-path")
                ? $"A mask on an <{name}> is not drawn, so mask what it holds instead."
                : $"A <{name}> is not drawn, so it cannot be clipped or masked.";
        }

        return Masks(property) && Shelter(target)?.Name.LocalName == "clipPath"
            ? "Inside a <clipPath> only its outline counts, so a mask does nothing there."
            : SvgAttributeEditor.Overridden(target, property);
    }

    /// <summary>A new, empty clip path or mask, named after what it is for where that has a plain name.</summary>
    /// <remarks>
    /// Plain because <c>url(#…)</c> is written unquoted, and a browser drops one holding a space or a
    /// bracket — which an id Figma writes from a layer name can.
    /// </remarks>
    private static XElement Wrapper(XElement target, bool mask, HashSet<string> ids, string? region)
    {
        var named = (string?)target.Attribute("id") is { Length: > 0 } id
                    && (char.IsLetter(id[0]) || id[0] == '_')
                    && id.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.')
            ? id + "-"
            : string.Empty;

        var wrapper = new XElement(mask ? "mask" : "clipPath", new XAttribute("id", Free(named + (mask ? "mask" : "clip"), ids)));

        if (mask && region?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var x, var y, var width, var height])
        {
            wrapper.Add(
                new XAttribute("maskUnits", "userSpaceOnUse"),
                new XAttribute("x", x),
                new XAttribute("y", y),
                new XAttribute("width", width),
                new XAttribute("height", height));
        }

        return wrapper;
    }

    /// <summary>What a <c>&lt;use&gt;</c> draws in the end, through any use it draws; null where that is not in this drawing.</summary>
    private static XElement? Used(XElement use)
    {
        var seen = new HashSet<XElement>();
        var at = use;

        while (at.Name.LocalName == "use")
        {
            var href = (at.Attribute("href") ?? at.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "href"))?.Value.Trim();

            if (!seen.Add(at) || href is not { Length: > 1 } || href[0] != '#' ||
                at.Document?.Descendants().FirstOrDefault(element => (string?)element.Attribute("id") == href.Substring(1)) is not { } next)
            {
                return null;
            }

            at = next;
        }

        return at;
    }

    /// <summary>
    /// What a shape has to have written on it to be drawn in a new clip path or mask as it was
    /// drawn: what the groups it is <paramref name="leaving"/> gave it, and its fill rule as the
    /// clip rule a clip path goes by instead.
    /// </summary>
    /// <remarks>Only what applies: a clip path is an outline and takes no paint, and a basic shape has no letters.</remarks>
    private static IEnumerable<(string Name, string Value)> Inherited(XElement shape, bool leaving, bool mask)
    {
        var groups = leaving ? shape.Ancestors().Where(group => group.Parent is { }).ToList() : new List<XElement>();
        var lettered = !Clips(shape.Name.LocalName) || shape.Name.LocalName is "text" or "use";
        var names = (lettered ? s_lettered : Array.Empty<string>()).Concat(mask ? s_painted : Array.Empty<string>());

        foreach (var name in names)
        {
            if (Set(new[] { shape }, name) is null && Set(groups, name) is { } value)
            {
                yield return (name, value);
            }
        }

        if (!mask && Set(new[] { shape }, "clip-rule") is null && Set(shape.AncestorsAndSelf(), "fill-rule") == "evenodd")
        {
            yield return ("clip-rule", "evenodd");
        }
    }

    private static readonly string[] s_lettered =
    {
        "font-family", "font-size", "font-style", "font-variant", "font-weight", "font-stretch",
        "text-anchor", "letter-spacing", "word-spacing"
    };

    private static readonly string[] s_painted =
    {
        "fill", "fill-rule", "fill-opacity", "stroke", "stroke-width", "stroke-opacity", "stroke-linecap",
        "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset", "color"
    };

    /// <summary>What the first of <paramref name="elements"/> to set <paramref name="name"/> sets it to, its style before its attribute.</summary>
    private static string? Set(IEnumerable<XElement> elements, string name)
        => elements
            .Select(element => SvgAttributeEditor.Styled(element, name) ?? (string?)element.Attribute(name))
            .FirstOrDefault(value => value is { } && value != "inherit");

    /// <summary>Whether anything outside <paramref name="element"/> points at it, at what it holds, or at a group it is in.</summary>
    /// <remarks>
    /// Only <c>#id</c> and <c>url(#id)</c> in attributes: a shape is drawn elsewhere by an href, and
    /// a stylesheet's <c>url()</c> names paint and clips rather than shapes.
    /// </remarks>
    private static bool Referenced(XElement element)
    {
        var ids = element.DescendantsAndSelf()
            .Concat(element.Ancestors().TakeWhile(group => group.Parent is { } && !Kept(group)))
            .Select(named => (string?)named.Attribute("id"))
            .OfType<string>()
            .Where(id => id.Length > 0 && !id.Contains("{{", StringComparison.Ordinal))
            .ToList();

        return ids.Count > 0 && element.Document!.Descendants()
            .Where(other => !other.AncestorsAndSelf().Contains(element))
            .SelectMany(other => other.Attributes())
            .Any(attribute => ids.Any(id =>
                string.Equals(attribute.Value.Trim(), "#" + id, StringComparison.Ordinal) ||
                attribute.Value.Contains("url(#" + id + ")", StringComparison.Ordinal)));
    }

    private static HashSet<string> Ids(SvgSourceDocument source)
        => new(
            source.Document.Descendants().Select(element => (string?)element.Attribute("id")).OfType<string>(),
            StringComparer.Ordinal);

    /// <summary>Renames every id in a copy to one the drawing does not have.</summary>
    private static XElement Renamed(XElement copy, HashSet<string> ids)
    {
        foreach (var named in copy.DescendantsAndSelf())
        {
            if (named.Attribute("id") is { } id && !id.Value.Contains("{{", StringComparison.Ordinal))
            {
                id.Value = Free(id.Value, ids);
            }
        }

        return copy;
    }

    /// <summary>The first of <c>id</c>, <c>id-2</c>, <c>id-3</c>… the drawing does not have, taken.</summary>
    private static string Free(string id, HashSet<string> ids)
    {
        if (ids.Add(id))
        {
            return id;
        }

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

    /// <summary>Whether an element named <paramref name="localName"/> keeps what it holds off the canvas.</summary>
    public static bool Keeps(string localName)
        => localName is
            "defs" or "clipPath" or "mask" or "marker" or "pattern" or "symbol" or
            "linearGradient" or "radialGradient" or "filter";

    /// <summary>Whether an element named <paramref name="localName"/> is drawn with <paramref name="property"/>, <c>clip-path</c> or <c>mask</c>.</summary>
    /// <remarks>The renderer clips an <c>&lt;svg&gt;</c> and an <c>&lt;a&gt;</c> but masks neither.</remarks>
    public static bool Clippable(string localName, string property)
        => Clips(localName) || localName is "image" or "g" or "switch" or "foreignObject" ||
           localName is "svg" or "a" && !Masks(property);

    /// <summary>What SVG 1.1 lets a <c>&lt;clipPath&gt;</c> hold.</summary>
    public static bool Clips(string localName)
        => localName is
            "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path" or "text" or "use";

    private static bool Kept(XElement element) => Keeps(element.Name.LocalName);

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
    /// A shape is not content these take: a gradient stops having stops, and a run of text stops
    /// being one run. A clip path takes shapes, text and uses but no group, which clips with nothing.
    /// </remarks>
    private static string? Holds(XElement parent, XElement placed)
        => parent.Name.LocalName is "linearGradient" or "radialGradient" or "filter" || IsText(parent) ||
           parent.Name.LocalName == "clipPath" && !Clips(placed.Name.LocalName)
            ? $"A <{parent.Name.LocalName}> cannot hold a <{placed.Name.LocalName}>."
            : null;

    private static bool IsText(XElement element) => element.Name.LocalName is "text" or "tspan" or "textPath";

    private static string? Parted(XElement element, string verb)
        => element.Parent is { } parent && IsText(parent)
            ? $"What a <{parent.Name.LocalName}> holds is one run of text, so a part of it cannot be {verb}."
            : null;
}
