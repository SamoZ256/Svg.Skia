// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Drawing;
using System.Globalization;

namespace Svg.Expressions.Recipes;

/// <summary>
/// Normalises the colour forms an SVG paint attribute can take, so that a recipe written
/// against <c>#3388ff</c> also matches <c>#38f</c>, <c>rgb(51,136,255)</c> and any other
/// spelling of the same colour.
/// </summary>
/// <remarks>
/// Parsing goes through <see cref="SvgColourConverter"/> — the same converter the document
/// parser uses — rather than a private implementation. Matching has to agree with what the
/// pipeline actually reads: a value this tool understood but the parser did not would produce
/// an expression attached to a colour that was never painted.
/// </remarks>
public static class SvgRecipeColor
{
    private static readonly SvgColourConverter s_converter = new();

    /// <summary>
    /// Parses a paint value into a normalised ARGB key.
    /// </summary>
    /// <remarks>
    /// The keywords (<c>none</c>, <c>inherit</c>, <c>currentColor</c>) and paint server
    /// references are deliberately not colours: they select a paint rather than name one, and
    /// substituting an expression for them would change what the element does, not its value.
    /// </remarks>
    public static bool TryParse(string? value, out int argb)
    {
        argb = 0;

        if (value is null)
        {
            return false;
        }

        var text = value.Trim();

        if (text.Length == 0 ||
            text.StartsWith("url(", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        object? converted;
        try
        {
            converted = s_converter.ConvertFrom(null, CultureInfo.InvariantCulture, text);
        }
        catch (Exception)
        {
            // An unreadable colour is simply not a match. The document parser reports its own
            // diagnostics for these; duplicating them here would fail conversions over values
            // the recipe never mentions.
            return false;
        }

        // The converter answers with a paint server for values it recognises but cannot reduce
        // to a colour, so the result type is the test, not the absence of an exception.
        if (converted is not Color color)
        {
            return false;
        }

        argb = color.ToArgb();
        return true;
    }

    /// <summary>Parses a colour written in a recipe, where an unreadable value is an error.</summary>
    public static int Parse(string? value, string what)
    {
        if (!TryParse(value, out var argb))
        {
            throw new SvgRecipeException($"{what} is not a colour: '{value}'.");
        }

        return argb;
    }

    /// <summary>Renders an ARGB key back as <c>#rrggbb</c>, or <c>#rrggbbaa</c> when translucent.</summary>
    public static string ToText(int argb)
    {
        var color = Color.FromArgb(argb);

        var rgb = string.Format(
            CultureInfo.InvariantCulture,
            "#{0:x2}{1:x2}{2:x2}",
            color.R,
            color.G,
            color.B);

        return color.A == 255
            ? rgb
            : rgb + color.A.ToString("x2", CultureInfo.InvariantCulture);
    }
}
