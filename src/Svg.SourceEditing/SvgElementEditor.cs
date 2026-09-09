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

    /// <summary>Wraps the elements at <paramref name="addressKeys"/> in a new group.</summary>
    /// <remarks>
    /// The group is written where the first of them sits, and the rest are moved to it. Where they
    /// were not already neighbours that changes the order they paint in, which is the cost of
    /// grouping them at all and is why the caller asks before offering it.
    /// </remarks>
    public static SvgSourceEditResult Wrap(string svgText, IReadOnlyList<string> addressKeys)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        if (addressKeys is null)
        {
            throw new ArgumentNullException(nameof(addressKeys));
        }

        if (addressKeys.Distinct(StringComparer.Ordinal).Count() < 2)
        {
            return SvgSourceEditResult.Refuse("Grouping takes two elements or more.");
        }

        if (!SvgDeclarationEditor.Open(svgText, out var document, out var positions, out var refusal, declarationsMustBeValid: false))
        {
            return SvgSourceEditResult.Refuse(refusal!);
        }

        var picked = new List<XElement>(addressKeys.Count);

        foreach (var addressKey in addressKeys.Distinct(StringComparer.Ordinal))
        {
            if (SvgAttributeEditor.Resolve(document!, addressKey) is not { } element)
            {
                return SvgSourceEditResult.Refuse("One of the elements picked is not in this drawing any more.");
            }

            if (element.Parent is null)
            {
                return SvgSourceEditResult.Refuse("The drawing itself cannot be put inside a group.");
            }

            if (element.Name.NamespaceName == SvgExpressionDeclarations.Namespace ||
                element.Parent.Name.NamespaceName == SvgExpressionDeclarations.Namespace)
            {
                return SvgSourceEditResult.Refuse("A declaration is not part of the drawing, so it cannot be grouped with one.");
            }

            picked.Add(element);
        }

        if (picked.Select(Shelter).Distinct().Count() > 1)
        {
            return SvgSourceEditResult.Refuse(
                "Those are not all in the same place: one is inside a <defs>, a <clipPath> or a <mask> and another is not. Grouping them would change what the drawing paints.");
        }

        if (Holds(picked[0].Parent!) is { } cannot)
        {
            return SvgSourceEditResult.Refuse(cannot);
        }

        foreach (var element in picked)
        {
            if (picked.Any(other => !ReferenceEquals(other, element) && other.Ancestors().Contains(element)))
            {
                return SvgSourceEditResult.Refuse(
                    $"<{element.Name.LocalName}> was picked together with something inside it, and an element cannot be put inside itself.");
            }
        }

        var taken = new List<(int Start, int Length, XElement Element)>(picked.Count);

        foreach (var element in picked)
        {
            if (SvgDeclarationEditor.Line(svgText, element, positions) is not { } line)
            {
                return SvgSourceEditResult.Refuse(
                    $"<{element.Name.LocalName}> shares its line with something else, so there is no line to move. Put it on a line of its own first.");
            }

            taken.Add((line.Start, line.Length, element));
        }

        taken.Sort((left, right) => left.Start.CompareTo(right.Start));

        var newline = SvgDeclarationEditor.Newline(svgText);
        var indent = SvgDeclarationEditor.IndentUnit(svgText);
        var at = LeadingOf(svgText.Substring(taken[0].Start, taken[0].Length));

        var group = new StringBuilder();

        group.Append(newline).Append(at).Append("<g>");

        foreach (var (start, length, _) in taken)
        {
            group.Append(Deeper(svgText.Substring(start, length), indent));
        }

        group.Append(newline).Append(at).Append("</g>");

        var edits = new List<SvgTextEdit>(taken.Count)
        {
            new(taken[0].Start, taken[0].Length, group.ToString())
        };

        for (var index = 1; index < taken.Count; index++)
        {
            edits.Add(new SvgTextEdit(taken[index].Start, taken[index].Length, string.Empty));
        }

        return Verify(svgText, edits);
    }

    /// <summary>Takes the children of the group at <paramref name="addressKey"/> out of it.</summary>
    public static SvgSourceEditResult Unwrap(string svgText, string addressKey)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        if (addressKey is null)
        {
            throw new ArgumentNullException(nameof(addressKey));
        }

        if (!SvgDeclarationEditor.Open(svgText, out var document, out var positions, out var refusal, declarationsMustBeValid: false))
        {
            return SvgSourceEditResult.Refuse(refusal!);
        }

        if (SvgAttributeEditor.Resolve(document!, addressKey) is not { } group)
        {
            return SvgSourceEditResult.Refuse("That group is not in this drawing any more.");
        }

        if (group.Parent is null || group.Name.LocalName != "g")
        {
            return SvgSourceEditResult.Refuse("Only a <g> can be ungrouped.");
        }

        if (group.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration))
        {
            return SvgSourceEditResult.Refuse(
                $"<g> carries {string.Join(", ", group.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Name.LocalName))}, which its children would lose. Take those off first.");
        }

        if (!group.Elements().Any())
        {
            return SvgSourceEditResult.Refuse("That group holds nothing to take out of it.");
        }

        if (SvgDeclarationEditor.Line(svgText, group, positions) is not { } line)
        {
            return SvgSourceEditResult.Refuse(
                "That <g> shares its line with something else, so there is no line to take. Put it on a line of its own first.");
        }

        if (SvgDeclarationEditor.Body(svgText, group, positions) is not { } body)
        {
            return SvgSourceEditResult.Refuse("That group's contents cannot be found in the document's own text.");
        }

        // The body rather than the children, so a comment or a stray line written inside the group
        // comes out with them instead of being deleted along with the tags.
        var indent = SvgDeclarationEditor.IndentUnit(svgText);
        var lifted = Shallower(svgText.Substring(body.Start, body.Length).TrimEnd(' ', '\t'), indent);

        return Verify(svgText, new List<SvgTextEdit> { new(line.Start, line.Length, lifted.TrimEnd('\r', '\n')) });
    }

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

    /// <summary>The indentation a taken line is written at, past the break that opens it.</summary>
    private static string LeadingOf(string taken)
    {
        var at = 0;

        while (at < taken.Length && (taken[at] == '\r' || taken[at] == '\n'))
        {
            at++;
        }

        var from = at;

        while (at < taken.Length && (taken[at] == ' ' || taken[at] == '\t'))
        {
            at++;
        }

        return taken.Substring(from, at - from);
    }

    /// <summary>The same text one level further in.</summary>
    /// <remarks>
    /// After every line break rather than at the front: what is taken carries its own children, and
    /// they move with it.
    /// </remarks>
    private static string Deeper(string taken, string indent)
        => taken.Replace("\n", "\n" + indent);

    private static string Shallower(string taken, string indent)
        => taken.Replace("\n" + indent, "\n");

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
