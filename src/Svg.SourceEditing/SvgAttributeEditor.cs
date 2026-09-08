// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Svg.Expressions;

namespace Svg.SourceEditing;

/// <summary>
/// Writes one attribute of one element as a span into the text it was read from.
/// </summary>
/// <remarks>
/// The other half of what a drawing says. <see cref="SvgDeclarationEditor"/> writes what it declares
/// and <see cref="SvgFrameEditor"/> writes the frame of its root; this writes anything else, on any
/// element, which is what an editor showing one element's attributes needs.
///
/// A span and not a rewritten document, for the reason everything here is: a document regenerated
/// from a parsed tree comes back without the author's formatting, attribute order or comments, and
/// changing one value has no business touching any of them.
///
/// The element is named by the address <c>SvgElementAddress.Key</c> spells, as a string. That type
/// belongs to the SVG parser, and this assembly deliberately cannot see it — the same rule
/// <see cref="SvgRecipeRuleEditor"/> states about the recipe package. The key is the identity every
/// caller already holds, and both sides spell it the same way because the walk below counts what
/// <c>SvgSourceElements</c> counts.
/// </remarks>
public static class SvgAttributeEditor
{
    /// <summary>
    /// Sets <paramref name="attributeName"/> on the element at <paramref name="addressKey"/>, or
    /// takes it away when <paramref name="value"/> is null.
    /// </summary>
    public static SvgSourceEditResult SetAttribute(
        string svgText,
        string addressKey,
        string attributeName,
        string? value)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        if (addressKey is null)
        {
            throw new ArgumentNullException(nameof(addressKey));
        }

        var name = (attributeName ?? throw new ArgumentNullException(nameof(attributeName))).Trim();

        if (name.Length == 0)
        {
            return SvgSourceEditResult.Refuse("An edit has to name an attribute.");
        }

        // What the declarations say is not this edit's business, so a fault in them does not stop
        // it: a drawing with a broken <e:code> block still has a fill somebody may want to change.
        if (!SvgDeclarationEditor.Open(svgText, out var document, out var positions, out var refusal, declarationsMustBeValid: false))
        {
            return SvgSourceEditResult.Refuse(refusal!);
        }

        if (Resolve(document!, addressKey) is not { } element)
        {
            return SvgSourceEditResult.Refuse("That element is no longer in the drawing.");
        }

        // A style declaration beats the presentation attribute under it, so writing the attribute
        // would leave a document where the change paints nothing. Editing inside the declaration is
        // another matter and not one this can reach.
        if (Shadowed(element, name))
        {
            return SvgSourceEditResult.Refuse(
                $"'{name}' is set in this element's style attribute, which wins over the attribute. Change it there instead.");
        }

        return SvgDeclarationEditor.Write(svgText, element, positions, name, value) is { } edit
            ? SvgSourceEditResult.From(new[] { edit })
            : SvgSourceEditResult.Nothing;
    }

    /// <summary>
    /// The element <paramref name="addressKey"/> names, or null where the document has no such path.
    /// </summary>
    /// <remarks>
    /// The inverse of the walk in <c>SvgSourceElements</c>, and it has to count exactly what that
    /// counts or the two spell different paths: the root is the empty key, children are
    /// <see cref="XContainer.Elements()"/> so a comment or a run of text takes no index, and the
    /// indexes are invariant.
    /// </remarks>
    private static XElement? Resolve(XDocument document, string addressKey)
    {
        if (document.Root is not { } root)
        {
            return null;
        }

        if (addressKey.Length == 0)
        {
            return root;
        }

        var at = root;

        foreach (var segment in addressKey.Split('/'))
        {
            if (!int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index < 0)
            {
                return null;
            }

            if (at.Elements().ElementAtOrDefault(index) is not { } child)
            {
                return null;
            }

            at = child;
        }

        return at;
    }

    /// <summary>Whether a <c>style</c> declaration on the element overrides the attribute.</summary>
    /// <remarks>
    /// Read rather than parsed: the scanner that splits a style attribute properly is internal to
    /// the SVG parser, and a name followed by a colon is enough to know the attribute is not the
    /// value being painted. Saying so wrongly costs a refusal; missing it costs an edit that does
    /// nothing and says it worked.
    /// </remarks>
    private static bool Shadowed(XElement element, string attributeName)
    {
        if ((string?)element.Attribute("style") is not { } style)
        {
            return false;
        }

        foreach (var declaration in style.Split(';'))
        {
            var colon = declaration.IndexOf(':');

            if (colon > 0 && string.Equals(declaration.Substring(0, colon).Trim(), attributeName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
