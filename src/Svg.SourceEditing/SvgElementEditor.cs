// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Svg.Expressions;

namespace Svg.SourceEditing;

/// <summary>
/// Puts elements of a drawing inside a <c>&lt;g&gt;</c>, and takes them out again.
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
    /// <summary>Moves the element at <paramref name="addressKey"/> to where a drop puts it.</summary>
    /// <remarks>
    /// The line it is written on, moved whole and re-indented to its new depth, so what it carries
    /// travels with it and nothing about how it was written changes.
    /// </remarks>
    public static SvgSourceEditResult Move(
        string svgText,
        string addressKey,
        string targetKey,
        SvgElementDrop where)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        if (!SvgDeclarationEditor.Open(svgText, out var document, out var positions, out var refusal, declarationsMustBeValid: false))
        {
            return SvgSourceEditResult.Refuse(refusal!);
        }

        if (SvgAttributeEditor.Resolve(document!, addressKey) is not { } moved ||
            SvgAttributeEditor.Resolve(document!, targetKey) is not { } target)
        {
            return SvgSourceEditResult.Refuse("One of those is not in this drawing any more.");
        }

        if (moved.Parent is null)
        {
            return SvgSourceEditResult.Refuse("The drawing itself cannot be moved.");
        }

        if (ReferenceEquals(moved, target) || target.AncestorsAndSelf().Contains(moved))
        {
            return SvgSourceEditResult.Refuse($"<{moved.Name.LocalName}> cannot be put inside itself.");
        }

        if (target.Parent is null)
        {
            where = SvgElementDrop.Inside;
        }

        var into = where == SvgElementDrop.Inside ? target : target.Parent!;

        if (Holds(into) is { } cannot)
        {
            return SvgSourceEditResult.Refuse(cannot);
        }

        if (!ReferenceEquals(Shelter(moved), Shelter(into.Elements().FirstOrDefault() ?? into)) &&
            !ReferenceEquals(Shelter(moved), into.AncestorsAndSelf().FirstOrDefault(Kept)))
        {
            return SvgSourceEditResult.Refuse(
                "That would move it across a <defs>, a <clipPath> or a <mask>, which changes what the drawing paints.");
        }

        if (SvgDeclarationEditor.Line(svgText, moved, positions) is not { } cut)
        {
            return SvgSourceEditResult.Refuse(
                $"<{moved.Name.LocalName}> shares its line with something else, so there is no line to move. Put it on a line of its own first.");
        }

        if (Landing(svgText, target, positions, where, out var nowhere) is not { } landing)
        {
            return SvgSourceEditResult.Refuse(nowhere!);
        }

        if (landing.At > cut.Start && landing.At < cut.Start + cut.Length)
        {
            return SvgSourceEditResult.Nothing;
        }

        var newline = SvgDeclarationEditor.Newline(svgText);
        var was = SvgDeclarationEditor.LeadingWhitespace(svgText, cut.Element.Start);
        var written = svgText.Substring(cut.Element.Start, cut.Element.Length).Replace(newline + was, newline + landing.Indent);

        var edits = new List<SvgTextEdit>
        {
            new(cut.Start, cut.Length, string.Empty),
            new(landing.At, 0, newline + landing.Indent + written)
        };

        edits.Sort((left, right) => left.Position.CompareTo(right.Position));

        return Verify(svgText, edits);
    }

    /// <summary>Writes an empty group where a drop puts it.</summary>
    public static SvgSourceEditResult NewGroup(string svgText, string targetKey, SvgElementDrop where)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        if (!SvgDeclarationEditor.Open(svgText, out var document, out var positions, out var refusal, declarationsMustBeValid: false))
        {
            return SvgSourceEditResult.Refuse(refusal!);
        }

        if (SvgAttributeEditor.Resolve(document!, targetKey) is not { } target)
        {
            return SvgSourceEditResult.Refuse("That is not in this drawing any more.");
        }

        // Beside the drawing itself means inside it: the root has no siblings, and asking for a
        // group next to it is asking for one at the end of it.
        if (target.Parent is null)
        {
            where = SvgElementDrop.Inside;
        }

        var into = where == SvgElementDrop.Inside ? target : target.Parent!;

        if (Holds(into) is { } cannot)
        {
            return SvgSourceEditResult.Refuse(cannot);
        }

        if (Landing(svgText, target, positions, where, out var nowhere) is not { } landing)
        {
            return SvgSourceEditResult.Refuse(nowhere!);
        }

        var newline = SvgDeclarationEditor.Newline(svgText);

        return Verify(
            svgText,
            new List<SvgTextEdit>
            {
                new(landing.At, 0, newline + landing.Indent + "<g>" + newline + landing.Indent + "</g>")
            });
    }

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

        if (Holds(into) is { } cannot)
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

    /// <inheritdoc cref="NewGroup(string, string, SvgElementDrop)"/>
    /// <returns>The sentence refusing it, or null where the group was written.</returns>
    public static string? NewGroup(SvgSourceDocument source, string targetKey, SvgElementDrop where)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (SvgAttributeEditor.Resolve(source.Document, targetKey) is not { } target)
        {
            return "That is not in this drawing any more.";
        }

        if (target.Parent is null)
        {
            where = SvgElementDrop.Inside;
        }

        var into = where == SvgElementDrop.Inside ? target : target.Parent!;

        if (Holds(into) is { } cannot)
        {
            return cannot;
        }

        var indent = where == SvgElementDrop.Inside ? Indent(target) + source.IndentUnit : Indent(target);

        // The break inside it is not decoration: an element holding no nodes at all is written as
        // <g/>, and a group somebody is about to drop things into wants a pair of tags.
        var group = new XElement(
            into.Name.Namespace + "g",
            new XText("\n" + indent));

        Put(target, where, group, indent);

        return null;
    }

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

    /// <summary>Where a drop lands, and at what indentation.</summary>
    /// <remarks>
    /// Inside means last among the target's children, which is where the pointer says it goes: the
    /// outline is drawn round the whole row rather than between two of them.
    /// </remarks>
    private static (int At, string Indent)? Landing(
        string svgText,
        XElement target,
        SvgExpressionDeclarations.Positions positions,
        SvgElementDrop where,
        out string? refusal)
    {
        refusal = null;

        var (start, _) = positions.Span(target);

        if (start < 0)
        {
            refusal = $"<{target.Name.LocalName}> cannot be found in the document's own text.";

            return null;
        }

        var at = SvgDeclarationEditor.LeadingWhitespace(svgText, start);

        if (where != SvgElementDrop.Inside)
        {
            // Beside a row means beside the line carrying it, which one sharing its line has not got.
            // Landing inside asks nothing of the line, which is why it is not asked for above: the
            // root begins the file and so has no line break in front of it either.
            if (SvgDeclarationEditor.Line(svgText, target, positions) is not { } line)
            {
                refusal = $"<{target.Name.LocalName}> shares its line with something else, so there is nothing to put anything beside. Put it on a line of its own first.";

                return null;
            }

            return (where == SvgElementDrop.Before ? line.Start : line.Start + line.Length, at);
        }

        if (SvgDeclarationEditor.Body(svgText, target, positions) is not { } body)
        {
            refusal = $"<{target.Name.LocalName}> closes itself, so it has no inside to put anything in. Write it as a pair of tags first.";

            return null;
        }

        // Back past the break and indent that carry the closing tag, so what lands goes after the
        // last child rather than after the whitespace written to line </g> up.
        var end = body.Start + body.Length;

        while (end > body.Start && char.IsWhiteSpace(svgText[end - 1]))
        {
            end--;
        }

        return (end, at + SvgDeclarationEditor.IndentUnit(svgText));
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

    /// <summary>Why <paramref name="parent"/> cannot hold a group, or null where it can.</summary>
    /// <remarks>
    /// A <c>&lt;g&gt;</c> is not content these take: a clip path stops clipping, a gradient stops
    /// having stops, and a run of text stops being one run.
    /// </remarks>
    private static string? Holds(XElement parent)
        => parent.Name.LocalName is
            "clipPath" or "linearGradient" or "radialGradient" or "filter" or "text" or "tspan" or "textPath"
            ? $"A <{parent.Name.LocalName}> cannot hold a <g>, so what is written in one cannot be grouped."
            : null;

    /// <summary>
    /// Applies the edits and reads the result, so a bad splice cannot be handed over.
    /// </summary>
    private static SvgSourceEditResult Verify(string svgText, List<SvgTextEdit> edits)
    {
        var rewritten = SvgTextEdit.ApplyAll(svgText, edits);

        try
        {
            XDocument.Parse(rewritten, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException malformed)
        {
            return SvgSourceEditResult.Refuse($"Grouping those would leave the drawing unreadable: {malformed.Message}");
        }

        return SvgSourceEditResult.From(edits);
    }
}
