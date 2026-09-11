// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Svg.Expressions;

namespace Svg.SourceEditing;

/// <summary>One attribute of an element, as the file writes it.</summary>
/// <remarks>
/// <see cref="Value"/> is the text between the quotes with entities resolved, which is what an
/// editor shows and what it compares against; the escaping back into markup is
/// <see cref="SvgAttributeEditor.SetAttribute"/>'s.
/// </remarks>
public readonly record struct SvgSourceAttribute(string Name, string Value);

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
    /// <summary>Whether <paramref name="addressKey"/> names an element of <paramref name="svgText"/>.</summary>
    /// <remarks>
    /// Told apart from an element written with no attributes, which <see cref="Attributes"/> answers
    /// for with an empty list either way.
    /// </remarks>
    public static bool Contains(string svgText, string addressKey)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        if (addressKey is null)
        {
            throw new ArgumentNullException(nameof(addressKey));
        }

        return SvgDeclarationEditor.Open(svgText, out var document, out _, out _, declarationsMustBeValid: false)
               && Resolve(document!, addressKey) is { };
    }

    /// <summary>
    /// The attributes the element at <paramref name="addressKey"/> is written with, in the order the
    /// file writes them.
    /// </summary>
    /// <remarks>
    /// Read from the text and not from a parsed drawing, because the text is what is being edited:
    /// what is listed is what is there, in the order somebody wrote it, and an attribute holding
    /// <c>{{ … }}</c> reads back as that rather than as the placeholder the parser substitutes for
    /// it. <c>SvgElement.Attributes</c> could not answer this from outside the parser anyway.
    ///
    /// Namespace declarations are left out: they say what the document's prefixes mean and are not
    /// attributes of the element in the sense anybody edits.
    ///
    /// Empty where the address names nothing, which is also what a document mid-typing looks like.
    /// </remarks>
    public static IReadOnlyList<SvgSourceAttribute> Attributes(string svgText, string addressKey)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        if (addressKey is null)
        {
            throw new ArgumentNullException(nameof(addressKey));
        }

        if (!SvgDeclarationEditor.Open(svgText, out var document, out _, out _, declarationsMustBeValid: false)
            || Resolve(document!, addressKey) is not { } element)
        {
            return Array.Empty<SvgSourceAttribute>();
        }

        return element.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration)
            .Select(attribute => new SvgSourceAttribute(attribute.Name.LocalName, attribute.Value))
            .ToList();
    }

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

    /// <inheritdoc cref="Contains(string, string)"/>
    public static bool Contains(SvgSourceDocument source, string addressKey)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (addressKey is null)
        {
            throw new ArgumentNullException(nameof(addressKey));
        }

        return Resolve(source.Document, addressKey) is { };
    }

    /// <inheritdoc cref="Attributes(string, string)"/>
    public static IReadOnlyList<SvgSourceAttribute> Attributes(SvgSourceDocument source, string addressKey)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (addressKey is null)
        {
            throw new ArgumentNullException(nameof(addressKey));
        }

        return Resolve(source.Document, addressKey) is not { } element
            ? Array.Empty<SvgSourceAttribute>()
            : element.Attributes()
                .Where(attribute => !attribute.IsNamespaceDeclaration)
                .Select(attribute => new SvgSourceAttribute(Spelling(attribute), attribute.Value))
                .ToList();
    }

    /// <inheritdoc cref="SetAttribute(string, string, string, string?)"/>
    /// <returns>The sentence refusing the edit, or null where it was made.</returns>
    /// <remarks>
    /// Writing the tree rather than the text it was read from. What the file looks like afterwards
    /// is <see cref="SvgSourceDocument"/>'s to answer, and it writes the value over the old one
    /// rather than the tag over the tag, so the layout somebody gave the element survives.
    /// </remarks>
    public static string? SetAttribute(
        SvgSourceDocument source,
        string addressKey,
        string attributeName,
        string? value)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (addressKey is null)
        {
            throw new ArgumentNullException(nameof(addressKey));
        }

        var name = (attributeName ?? throw new ArgumentNullException(nameof(attributeName))).Trim();

        if (name.Length == 0)
        {
            return "An edit has to name an attribute.";
        }

        // Not an attribute of the element in the sense anybody edits, and taking one away would
        // leave every name under it standing for something else -- a root written back without its
        // xmlns reads as a different document, and nothing about it would look wrong.
        if (name == "xmlns" || name.StartsWith("xmlns:", StringComparison.Ordinal))
        {
            return "A namespace declaration is not something to edit here.";
        }

        if (Resolve(source.Document, addressKey) is not { } element)
        {
            return "That element is no longer in the drawing.";
        }

        // A style declaration beats the presentation attribute under it, so writing the attribute
        // would leave a document where the change paints nothing. Editing inside the declaration is
        // another matter and not one this can reach.
        if (Shadowed(element, name))
        {
            return $"'{name}' is set in this element's style attribute, which wins over the attribute. Change it there instead.";
        }

        // A prefix stands for a namespace, and an attribute named for one is that attribute rather
        // than a second one beside it: writing href on a <use> that has xlink:href would otherwise
        // add a second href and leave the drawing pointing two ways at once.
        var colon = name.IndexOf(':');

        if (colon > 0 && element.GetNamespaceOfPrefix(name.Substring(0, colon)) is null)
        {
            return $"This drawing does not say what '{name.Substring(0, colon)}' stands for, so '{name}' cannot be written.";
        }

        try
        {
            // Inside, because naming the attribute is itself what rejects a name XML will not take.
            var written = colon > 0
                ? element.GetNamespaceOfPrefix(name.Substring(0, colon))! + name.Substring(colon + 1)
                : XName.Get(name);

            element.SetAttributeValue(written, value);
        }
        catch (XmlException)
        {
            // Splicing text would have written this and left a document that no longer parses; the
            // tree says no first, which is the one place this is stricter than the span it replaces.
            return $"'{name}' is not a name an attribute can have.";
        }
        catch (ArgumentException)
        {
            return $"'{name}' is not a name an attribute can have.";
        }

        return null;
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
    /// <summary>The element an address key names, or null where it names nothing.</summary>
    /// <remarks>Internal because the key convention should have one reader, not one per editor.</remarks>
    internal static XElement? Resolve(XDocument document, string addressKey)
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

    /// <summary>An attribute's name as the file spells it, prefix and all.</summary>
    /// <remarks>
    /// The prefix is part of the name and not decoration: <c>href</c> and <c>xlink:href</c> are two
    /// attributes, and reporting the second as the first is how an editor comes to write both.
    /// </remarks>
    private static string Spelling(XAttribute attribute)
    {
        if (attribute.Name.Namespace == XNamespace.None)
        {
            return attribute.Name.LocalName;
        }

        var prefix = attribute.Parent?.GetPrefixOfNamespace(attribute.Name.Namespace);

        return string.IsNullOrEmpty(prefix) ? attribute.Name.LocalName : prefix + ":" + attribute.Name.LocalName;
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
