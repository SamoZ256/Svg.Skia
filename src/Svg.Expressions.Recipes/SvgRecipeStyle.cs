// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace Svg.Expressions.Recipes;

/// <summary>
/// The declarations of a <c>style</c> attribute, in order.
/// </summary>
/// <remarks>
/// Drawing tools export paint into <c>style</c> as readily as into presentation attributes, so a
/// converter that only looked at attributes would find nothing in a large share of real files.
/// Declarations this class does not touch keep their original text, so promoting one property
/// does not reformat the rest.
/// </remarks>
internal sealed class SvgRecipeStyle
{
    private readonly List<Declaration> _declarations;

    private SvgRecipeStyle(List<Declaration> declarations) => _declarations = declarations;

    public static SvgRecipeStyle Parse(string? style)
    {
        var declarations = new List<Declaration>();

        if (string.IsNullOrWhiteSpace(style))
        {
            return new SvgRecipeStyle(declarations);
        }

        foreach (var part in Split(style!))
        {
            if (part.Trim().Length == 0)
            {
                continue;
            }

            var colon = part.IndexOf(':');

            declarations.Add(colon < 0
                ? new Declaration(string.Empty, string.Empty, part)
                : new Declaration(
                    part.Substring(0, colon).Trim().ToLowerInvariant(),
                    part.Substring(colon + 1).Trim(),
                    part));
        }

        return new SvgRecipeStyle(declarations);
    }

    /// <summary>The winning value for a property, which in CSS is the last one declared.</summary>
    public bool TryGetValue(string name, out string value)
    {
        for (var i = _declarations.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_declarations[i].Name, name, StringComparison.Ordinal))
            {
                value = _declarations[i].Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    /// <summary>Drops every declaration of a property, including the ones it was overriding.</summary>
    public void Remove(string name)
        => _declarations.RemoveAll(declaration => string.Equals(declaration.Name, name, StringComparison.Ordinal));

    public string ToText()
    {
        var builder = new StringBuilder();

        foreach (var declaration in _declarations)
        {
            if (builder.Length > 0)
            {
                builder.Append(';');
            }

            builder.Append(declaration.Raw.Trim());
        }

        return builder.ToString();
    }

    // A semicolon inside parentheses belongs to the value — a data URI is the usual way this
    // shows up — so splitting on every semicolon would tear the declaration in half.
    private static IEnumerable<string> Split(string style)
    {
        var depth = 0;
        var start = 0;

        for (var i = 0; i < style.Length; i++)
        {
            switch (style[i])
            {
                case '(':
                    depth++;
                    break;

                case ')':
                    if (depth > 0)
                    {
                        depth--;
                    }

                    break;

                case ';' when depth == 0:
                    yield return style.Substring(start, i - start);
                    start = i + 1;
                    break;
            }
        }

        yield return style.Substring(start);
    }

    private sealed class Declaration
    {
        public Declaration(string name, string value, string raw)
        {
            Name = name;
            Value = value;
            Raw = raw;
        }

        public string Name { get; }

        public string Value { get; }

        public string Raw { get; }
    }
}
