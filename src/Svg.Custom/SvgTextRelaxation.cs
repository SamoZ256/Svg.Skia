// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Collections.Generic;
using System.Linq;
using Svg.Expressions;

namespace Svg;

/// <summary>
/// Rewrites text the author drives into the one shape a recorded drawing can be given a new string
/// for: one run, at one origin, anchored by whoever draws it.
/// </summary>
/// <remarks>
/// <para>
/// A generated picture replays commands. Text laid out the way the specification says -- a span
/// positioned of its own, a length the run is fitted to, a glyph rotated on a path -- is a set of
/// positions computed from the string, so the positions and the string cannot both be free. Rather
/// than teach every one of those layouts to vary, <see cref="SvgTextLayout.Relaxed"/> takes the
/// text away from them: the words move to the element itself and everything that would have placed
/// them per glyph is dropped.
/// </para>
/// <para>
/// So this is a deliberate non-conformance, and the only honest thing to do about it is to say what
/// it dropped, which <see cref="Apply"/> hands back for a build to report. Nothing here runs unless
/// the author asked for it.
/// </para>
/// </remarks>
public static class SvgTextRelaxation
{
    /// <summary>Rewrites every driven text in <paramref name="document"/>, saying what it gave up.</summary>
    /// <returns>One sentence per rule dropped, in document order.</returns>
    public static IReadOnlyList<string> Apply(SvgDocument? document)
    {
        var dropped = new List<string>();

        if (document is null)
        {
            return dropped;
        }

        // Collected before anything moves: hoisting rewrites the tree these came out of.
        var driven = Driven(document).ToList();

        foreach (var element in driven)
        {
            Relax(element, dropped);
        }

        return dropped;
    }

    /// <summary>The outermost &lt;text&gt; of every element whose words an expression writes.</summary>
    private static IEnumerable<SvgTextBase> Driven(SvgElement element)
    {
        if (element is SvgTextBase text && Carries(text))
        {
            yield return text;

            // Its own subtree is this one's business now, and nothing below it is separately driven
            // as far as the rewrite is concerned: the words all become the element's.
            yield break;
        }

        foreach (var child in element.Children)
        {
            foreach (var found in Driven(child))
            {
                yield return found;
            }
        }
    }

    /// <summary>Whether this element or anything under it has words an expression writes.</summary>
    private static bool Carries(SvgElement element)
        => SvgExpressionAttributes.Lifted(element.CustomAttributes, SvgExpressionAttributes.ContentName) is { } ||
           element.Children.Any(Carries);

    private static void Relax(SvgTextBase text, ICollection<string> dropped)
    {
        Hoist(text, dropped);

        // One position, since the rest place glyphs the string decides the count of.
        Single(text.X, text, "x", dropped);
        Single(text.Y, text, "y", dropped);
        Single(text.Dx, text, "dx", dropped);
        Single(text.Dy, text, "dy", dropped);

        if (!string.IsNullOrWhiteSpace(text.Rotate))
        {
            text.Rotate = null;
            Say(dropped, text, "rotate turns each glyph in turn, and how many there are is the string's to say");
        }

        if (Spaced(text.TextLength))
        {
            text.TextLength = SvgUnit.None;
            Say(dropped, text, "textLength fits the run to a width it was measured against");
        }

        // Both space glyphs apart, which is a measurement of the string said per gap.
        if (Spaced(text.LetterSpacing))
        {
            text.LetterSpacing = SvgUnit.None;
            Say(dropped, text, "letter-spacing is applied between glyphs");
        }

        if (Spaced(text.WordSpacing))
        {
            text.WordSpacing = SvgUnit.None;
            Say(dropped, text, "word-spacing is applied between words");
        }
    }

    /// <summary>Moves the words of whatever holds them onto <paramref name="text"/> itself.</summary>
    private static void Hoist(SvgTextBase text, ICollection<string> dropped)
    {
        if (SvgExpressionAttributes.Lifted(text.CustomAttributes, SvgExpressionAttributes.ContentName) is { })
        {
            // Already the element's own words; anything beside them would be drawn after the
            // expression and is not something one command can hold.
            if (text.Children.Count > 0)
            {
                text.Children.Clear();
                Say(dropped, text, "what it held beside the driven words is not drawn");
            }

            return;
        }

        var holder = text.Children.OfType<SvgElement>().Select(Find).FirstOrDefault(found => found is { });

        if (holder is null)
        {
            return;
        }

        SvgExpressionAttributes.Lift(
            text.CustomAttributes,
            SvgExpressionAttributes.ContentName,
            SvgExpressionAttributes.Lifted(holder.CustomAttributes, SvgExpressionAttributes.ContentName)!,
            SvgElement.StyleSpecificity_PresAttribute);

        text.Children.Clear();
        text.Nodes.Clear();
        text.Nodes.Add(new SvgContentNode { Content = string.Empty });
        text.Content = string.Empty;

        Say(dropped, text, $"<{holder.ElementName}> is drawn as the text's own words, at the text's own place");
    }

    private static SvgElement? Find(SvgElement element)
        => SvgExpressionAttributes.Lifted(element.CustomAttributes, SvgExpressionAttributes.ContentName) is { }
            ? element
            : element.Children.Select(Find).FirstOrDefault(found => found is { });

    private static void Single(SvgUnitCollection units, SvgTextBase text, string name, ICollection<string> dropped)
    {
        if (units.Count <= 1)
        {
            return;
        }

        var first = units[0];

        units.Clear();
        units.Add(first);

        Say(dropped, text, $"{name} places glyphs one at a time, and only the first is kept");
    }

    private static bool Spaced(SvgUnit spacing) => !spacing.IsNone && !spacing.IsEmpty && spacing.Value != 0f;

    private static void Say(ICollection<string> dropped, SvgTextBase text, string what)
        => dropped.Add($"<{text.ElementName}>: {what}.");
}
