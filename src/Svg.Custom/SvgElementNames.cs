// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;

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
}
