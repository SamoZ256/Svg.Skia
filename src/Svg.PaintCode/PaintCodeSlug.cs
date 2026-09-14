// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Text;

namespace Svg.PaintCode;

/// <summary>Turns a PaintCode name into something that can be an XML id and a file name.</summary>
internal static class PaintCodeSlug
{
    internal static string Of(string name)
    {
        var builder = new StringBuilder(name.Length);
        var dashed = false;

        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                dashed = false;

                continue;
            }

            // Runs of anything else collapse to one dash, and a leading or trailing one is dropped,
            // so "number 2-state" and "number  2 state" do not become different files.
            if (builder.Length > 0 && !dashed)
            {
                builder.Append('-');
                dashed = true;
            }
        }

        if (dashed)
        {
            builder.Length--;
        }

        return builder.Length == 0 ? "item" : builder.ToString();
    }

    /// <summary>The same name as an identifier C# will take: PascalCase, and never starting with a digit.</summary>
    internal static string Pascal(string name)
    {
        var builder = new StringBuilder(name.Length);
        var capitalise = true;

        foreach (var character in name)
        {
            if (!char.IsLetterOrDigit(character))
            {
                capitalise = true;

                continue;
            }

            builder.Append(capitalise ? char.ToUpperInvariant(character) : character);
            capitalise = false;
        }

        if (builder.Length == 0)
        {
            return "Item";
        }

        return char.IsDigit(builder[0]) ? "_" + builder : builder.ToString();
    }
}
