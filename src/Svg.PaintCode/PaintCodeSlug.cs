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

    /// <summary>
    /// The name as PaintCode's own generated code spells it, and so as its expressions do: letters,
    /// digits and underscores, the first letter lowered and a word after a space capitalised.
    /// </summary>
    /// <remarks>
    /// PaintCode lets a variable be called "level-17" and then writes "level17" in the expressions
    /// that use it, so a name and the identifier standing for it are two different strings. The
    /// library colour "Tint Color" is the parameter tintColor of the methods it generates; spelled
    /// TintColor here, a caller passing it by name had nothing to pass it to.
    /// </remarks>
    internal static string Identifier(string name)
    {
        var builder = new StringBuilder(name.Length);
        var capitalise = false;

        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                builder.Append(builder.Length == 0 ? char.ToLowerInvariant(character) : capitalise ? char.ToUpperInvariant(character) : character);
                capitalise = false;
            }
            else if (char.IsWhiteSpace(character) && builder.Length > 0)
            {
                capitalise = true;
            }
        }

        if (builder.Length == 0)
        {
            return "value";
        }

        return char.IsDigit(builder[0]) ? "_" + builder : builder.ToString();
    }

    /// <summary>The same name as an identifier C# will take: PascalCase, and never starting with a digit.</summary>
    /// <summary>The method PaintCode draws a canvas of this name by.</summary>
    /// <remarks>Measured against the 1,011 public methods of the sample's own export, all of which it names.</remarks>
    internal static string Method(string name)
    {
        var builder = new StringBuilder(name.Length + 5);

        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                builder.Append(builder.Length == 0 ? char.ToUpperInvariant(character) : character);
            }
        }

        if (builder.Length > 0 && char.IsDigit(builder[0]))
        {
            builder.Insert(0, '_');
        }

        return "draw" + builder;
    }

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
