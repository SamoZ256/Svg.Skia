// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Svg;

/// <summary>
/// What an element is called in the document it was read from.
/// </summary>
/// <remarks>
/// <see cref="SvgElement.ElementName"/> is <c>protected internal</c>, so a consumer that wants to
/// show or match an element by name has to work it out again. Three had: the JavaScript DOM's
/// <c>tagName</c>, the editor's outline, and the SvgML controls. The rules are not obvious — a
/// document is <c>svg</c>, a foreign element carries its own name, an unrecognised one keeps the
/// name it was written with in a custom attribute — so they belong in one place rather than in
/// however many copies happen to be right.
/// </remarks>
public static class SvgElementNames
{
    /// <summary>The name <paramref name="element"/> is written with, prefix excluded.</summary>
    public static string NameOf(SvgElement element)
    {
        if (element is null)
        {
            throw new ArgumentNullException(nameof(element));
        }

        if (element is SvgDocument)
        {
            return "svg";
        }

        if (element is NonSvgElement foreign)
        {
            return foreign.Name;
        }

        if (element is SvgUnknownElement && element.CustomAttributes.TryGetValue("tagName", out var written))
        {
            return written;
        }

        if (!string.IsNullOrEmpty(element.ElementName))
        {
            return element.ElementName;
        }

        // A type the generated table knows, and its own name where it does not: an element with no
        // name at all is a worse answer than a class name somebody can search for.
        return SvgElements.ElementNames.TryGetValue(element.GetType(), out var declared)
            ? declared
            : element.GetType().Name;
    }

    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> s_attributes = new(StringComparer.Ordinal);

    /// <summary>
    /// Every attribute the parser reads on an element called <paramref name="elementName"/>, sorted,
    /// or none for a name it does not know.
    /// </summary>
    /// <remarks>
    /// An xlink <c>href</c> is spelt bare, as SVG 2 writes it and the parser reads it; the other xlink
    /// names are left out, since writing one needs a prefix the document may not declare.
    /// </remarks>
    public static IReadOnlyList<string> AttributesOf(string elementName)
    {
        if (elementName is null)
        {
            throw new ArgumentNullException(nameof(elementName));
        }

        return s_attributes.GetOrAdd(elementName, static name =>
        {
            if (SvgElementFactory.CreateProbe(name) is not { } probe)
            {
                return Array.Empty<string>();
            }

            var names = new SortedSet<string>(StringComparer.Ordinal) { "class", "style" };

            foreach (var property in probe.GetProperties())
            {
                if (property.DescriptorType != DescriptorType.Property)
                {
                    continue;
                }

                switch (property.AttributeNamespace)
                {
                    case SvgNamespaces.SvgNamespace:
                        names.Add(property.AttributeName);
                        break;
                    case SvgNamespaces.XmlNamespace:
                        names.Add("xml:" + property.AttributeName);
                        break;
                    case SvgNamespaces.XLinkNamespace when property.AttributeName == "href":
                        names.Add("href");
                        break;
                }
            }

            // Presentation attributes reach every element through the style path, geometry aside.
            foreach (var style in SvgStyleAttributeNames.All)
            {
                if (!SvgElementFactory.IsSvg2GeometryAttribute(style))
                {
                    names.Add(style);
                }
            }

            // Read only from style, and the raw marker attribute is skipped outright.
            names.RemoveWhere(name => SvgStyleAttributeNames.IsCssOnlyProperty(name) || name == "marker");

            return names.ToArray();
        });
    }
}
