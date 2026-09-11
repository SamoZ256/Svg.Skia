// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Svg.Expressions;

namespace Svg.SourceEditing;

/// <summary>
/// The replacement rules of a recipe.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not built on <see cref="SvgDeclarationEditor"/>'s tree helpers, though they sit
/// beside these and look like they would serve. That half refuses an edit outright while the
/// declarations have anything wrong with them, and reads them back afterwards; a rule edit does
/// neither on purpose, because a rule can be written into a recipe whose parameters are halfway
/// through being typed, and making the two halves take turns is the thing the span version's own
/// comment says it exists to avoid.
/// </para>
/// <para>
/// It still knows nothing about what a rule names. A colour has many spellings and only
/// <c>Svg.Expressions.Recipes</c> can say which of them are one colour, so the caller decides which
/// rule it means and passes the name and the value as that rule already writes them.
/// </para>
/// </remarks>
public static class SvgRecipeRuleEditor
{
    private static readonly XNamespace Ns = SvgExpressionDeclarations.Namespace;

    /// <summary>
    /// Writes what <paramref name="value"/> is replaced with, adding the rule if there is none.
    /// </summary>
    /// <param name="name">
    /// The attribute the rule is written on — <c>color</c>, <c>opacity</c> and so on. Which names
    /// are meaningful is the recipe package's answer, not this one's.
    /// </param>
    /// <param name="value">
    /// The value exactly as the rule for it writes it, or as a new rule should. Passing a second
    /// spelling of a value a rule already names would add a rule the recipe then refuses to read.
    /// </param>
    /// <returns>The sentence refusing it, or null where it was written.</returns>
    public static string? SetRule(SvgSourceDocument source, string name, string value, string expression)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var attribute = (name ?? throw new ArgumentNullException(nameof(name))).Trim();
        var replaced = (value ?? throw new ArgumentNullException(nameof(value))).Trim();
        var written = (expression ?? throw new ArgumentNullException(nameof(expression))).Trim();

        if (attribute.Length == 0)
        {
            return "A rule has to say what it replaces.";
        }

        if (replaced.Length == 0)
        {
            return "A rule has to name a value.";
        }

        if (written.Length == 0)
        {
            return "A rule with no expression paints nothing. Remove it instead.";
        }

        // The braces are the drawing's, not the recipe's — the rewrite adds them when it writes the
        // expression out — so a recipe carrying them would produce {{ {{ … }} }}.
        if (written.IndexOf("{{", StringComparison.Ordinal) >= 0 || written.IndexOf("}}", StringComparison.Ordinal) >= 0)
        {
            return "An expression here is written without braces; they are added when it is used.";
        }

        if (Root(source, out var root) is { } refusal)
        {
            return refusal;
        }

        var rules = Rules(root!);

        if (Rule(rules, attribute, replaced) is { } existing)
        {
            // The body alone, so the value keeps the spelling it was written with and anything else
            // on the line — a comment saying what it is — stays where it is. A rule that closed
            // itself is opened into a pair by being given one, which the span half had to refuse.
            existing.Value = written;

            return null;
        }

        // After the last rule where there is one, so the rules stay together and the file still
        // reads top to bottom as what is declared and then what it paints; otherwise after whatever
        // the recipe ends with, which is its <code>.
        var made = new XElement(Ns + "replace", new XAttribute(attribute, replaced), written);
        var anchor = rules.Count > 0 ? rules[rules.Count - 1] : root!.Elements().LastOrDefault();

        if (anchor is null)
        {
            // A recipe with nothing in it yet. One level in from the root, since there is nothing
            // whose indentation to follow.
            SvgElementEditor.Put(
                root!,
                SvgElementDrop.Inside,
                made,
                SvgElementEditor.Indent(root!) + source.IndentUnit);
        }
        else
        {
            SvgElementEditor.Put(anchor, SvgElementDrop.After, made, SvgElementEditor.Indent(anchor));
        }

        return null;
    }

    /// <summary>Takes the rule for <paramref name="value"/> away, with the line it sits on.</summary>
    /// <returns>The sentence refusing it, or null where it was taken away or was never there.</returns>
    public static string? RemoveRule(SvgSourceDocument source, string name, string value)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var attribute = (name ?? throw new ArgumentNullException(nameof(name))).Trim();
        var replaced = (value ?? throw new ArgumentNullException(nameof(value))).Trim();

        if (Root(source, out var root) is { } refusal)
        {
            return refusal;
        }

        if (Rule(Rules(root!), attribute, replaced) is not { } existing)
        {
            // Nothing to do rather than a refusal: a value with no rule is the ordinary state of
            // most values, and clearing one twice is not a mistake worth a sentence.
            return null;
        }

        // Takes the one line break carrying it, so a rule on a line of its own leaves no blank
        // behind and one sharing a line leaves what it sat beside where it was.
        SvgElementEditor.Cut(existing);

        return null;
    }

    private static List<XElement> Rules(XElement root) => root.Elements(Ns + "replace").ToList();

    private static XElement? Rule(List<XElement> rules, string name, string value)
        => rules.FirstOrDefault(
            rule => string.Equals(((string?)rule.Attribute(name))?.Trim(), value, StringComparison.Ordinal));

    /// <summary>The recipe's root, or why this file is not one.</summary>
    /// <remarks>
    /// Nothing here says the text will not read: a document that would not has no tree, and was
    /// refused before anybody got this far.
    /// </remarks>
    private static string? Root(SvgSourceDocument source, out XElement? root)
    {
        root = null;

        if (source.Document.Root is not { } found || found.Name != Ns + "recipe")
        {
            return $"This is not a recipe: the root is <{source.Document.Root?.Name.LocalName ?? "nothing"}>.";
        }

        root = found;

        return null;
    }
}
