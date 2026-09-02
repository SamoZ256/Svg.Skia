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
/// Edits the colour rules of a recipe, as spans over its text.
/// </summary>
/// <remarks>
/// The other half of what a recipe says. <see cref="SvgDeclarationEditor"/> already writes its
/// <c>&lt;param&gt;</c> and <c>&lt;let&gt;</c> — it finds declarations by namespace rather than by
/// document shape, so a recipe's unprefixed <c>&lt;code&gt;</c> is the same block to it — and this
/// writes the <c>&lt;replace&gt;</c> beside them.
///
/// Spans and not a rewritten document, for the reason everything here is: the file keeps its
/// comments, its layout and the order somebody put it in, and a host applies the edits to the buffer
/// it is already showing so they land on one undo stack.
///
/// It knows nothing about colours. A colour has many spellings and only
/// <c>Svg.Expressions.Recipes</c> can say which of them are one colour; asking that here would mean
/// a second answer to it, and referencing that package to borrow the first would pull a whole SVG
/// parser into a text editor. So the caller decides which rule it means and passes the colour as
/// that rule already writes it.
/// </remarks>
public static class SvgRecipeRuleEditor
{
    private static readonly XNamespace Ns = SvgExpressionDeclarations.Namespace;

    /// <summary>
    /// Writes what <paramref name="color"/> is painted with, adding the rule if there is none.
    /// </summary>
    /// <param name="color">
    /// The colour exactly as the rule for it writes it, or as a new rule should. A caller holding a
    /// parsed recipe has the first from the rule it matched by value; one adding a rule writes the
    /// colour canonically. Passing a second spelling of a colour a rule already names would add a
    /// rule the recipe then refuses to read.
    /// </param>
    public static SvgSourceEditResult SetRule(string recipeText, string color, string expression)
    {
        if (recipeText is null)
        {
            throw new ArgumentNullException(nameof(recipeText));
        }

        var text = (color ?? throw new ArgumentNullException(nameof(color))).Trim();
        var written = (expression ?? throw new ArgumentNullException(nameof(expression))).Trim();

        if (text.Length == 0)
        {
            return SvgSourceEditResult.Refuse("A rule has to name a colour.");
        }

        if (written.Length == 0)
        {
            return SvgSourceEditResult.Refuse("A rule with no expression paints nothing. Remove it instead.");
        }

        // The braces are the drawing's, not the recipe's — the rewrite adds them when it writes the
        // expression out — so a recipe carrying them would produce {{ {{ … }} }}.
        if (written.IndexOf("{{", StringComparison.Ordinal) >= 0 || written.IndexOf("}}", StringComparison.Ordinal) >= 0)
        {
            return SvgSourceEditResult.Refuse("An expression here is written without braces; they are added when it is used.");
        }

        if (!Open(recipeText, out var root, out var positions, out var refusal))
        {
            return SvgSourceEditResult.Refuse(refusal!);
        }

        var rules = Rules(root!);

        if (Rule(rules, text) is { } existing)
        {
            // The body alone, so the colour keeps the spelling it was written with and anything
            // else on the line — a comment saying what it is — stays where it is.
            if (SvgDeclarationEditor.Body(recipeText, existing, positions) is not { } body)
            {
                return SvgSourceEditResult.Refuse($"The rule for '{text}' has no expression to replace.");
            }

            var replaced = SvgDeclarationEditor.EscapeText(written);

            return string.Equals(recipeText.Substring(body.Start, body.Length), replaced, StringComparison.Ordinal)
                ? SvgSourceEditResult.Nothing
                : SvgSourceEditResult.From(new[] { new SvgTextEdit(body.Start, body.Length, replaced) });
        }

        return Add(recipeText, root!, rules, positions, text, written);
    }

    /// <summary>Takes the rule for <paramref name="color"/> away, with the line it sits on.</summary>
    public static SvgSourceEditResult RemoveRule(string recipeText, string color)
    {
        if (recipeText is null)
        {
            throw new ArgumentNullException(nameof(recipeText));
        }

        var text = (color ?? throw new ArgumentNullException(nameof(color))).Trim();

        if (!Open(recipeText, out var root, out var positions, out var refusal))
        {
            return SvgSourceEditResult.Refuse(refusal!);
        }

        if (Rule(Rules(root!), text) is not { } existing)
        {
            // Nothing to do rather than a refusal: a colour with no rule is the ordinary state of
            // most colours, and clearing one twice is not a mistake worth a sentence.
            return SvgSourceEditResult.Nothing;
        }

        var line = SvgDeclarationEditor.Line(recipeText, existing, positions);

        if (line is { } whole)
        {
            return SvgSourceEditResult.From(new[] { new SvgTextEdit(whole.Start, whole.Length, string.Empty) });
        }

        // Sharing its line with something else, so only the element goes and whatever it sat beside
        // keeps its place.
        var (start, length) = positions.Span(existing);

        return start < 0
            ? SvgSourceEditResult.Refuse($"The rule for '{text}' cannot be found in the text.")
            : SvgSourceEditResult.From(new[] { new SvgTextEdit(start, length, string.Empty) });
    }

    /// <summary>Writes a rule that is not there yet, under whatever the recipe says last.</summary>
    /// <remarks>
    /// After the last rule where there is one, so the rules stay together and the file still reads
    /// top to bottom as what is declared and then what it paints; otherwise after whatever the
    /// recipe ends with, which is its <c>&lt;code&gt;</c>. Anchored on an element rather than on the
    /// closing tag, so there is one shape to get right and no counting of where a tag ends.
    /// </remarks>
    private static SvgSourceEditResult Add(
        string recipeText,
        XElement root,
        List<XElement> rules,
        SvgExpressionDeclarations.Positions positions,
        string color,
        string expression)
    {
        var newline = SvgDeclarationEditor.Newline(recipeText);

        var element = $"<replace color=\"{SvgDeclarationEditor.EscapeText(color)}\">"
                      + SvgDeclarationEditor.EscapeText(expression)
                      + "</replace>";

        var anchor = rules.Count > 0 ? rules[rules.Count - 1] : root.Elements().LastOrDefault();

        if (anchor is null)
        {
            // A recipe with nothing in it yet. One level in from the root, since there is nothing
            // whose indentation to follow.
            var at = positions.ContentStart(root);

            return at < 0
                ? SvgSourceEditResult.Refuse("The recipe cannot be found in the text.")
                : SvgSourceEditResult.From(
                    new[] { new SvgTextEdit(at, 0, newline + SvgDeclarationEditor.IndentUnit(recipeText) + element) });
        }

        var (start, length) = positions.Span(anchor);

        if (start < 0)
        {
            return SvgSourceEditResult.Refuse("The recipe cannot be found in the text.");
        }

        return SvgSourceEditResult.From(
            new[]
            {
                new SvgTextEdit(
                    start + length,
                    0,
                    newline + SvgDeclarationEditor.LeadingWhitespace(recipeText, start) + element)
            });
    }

    private static List<XElement> Rules(XElement root) => root.Elements(Ns + "replace").ToList();

    private static XElement? Rule(List<XElement> rules, string color)
        => rules.FirstOrDefault(
            rule => string.Equals(((string?)rule.Attribute("color"))?.Trim(), color, StringComparison.Ordinal));

    /// <summary>Reads the recipe, or says why there is nowhere to write.</summary>
    private static bool Open(
        string recipeText,
        out XElement? root,
        out SvgExpressionDeclarations.Positions positions,
        out string? refusal)
    {
        root = null;

        // The declarations are not checked: a rule can be written into a recipe whose parameters are
        // halfway through being typed, and refusing here would make the two halves take turns.
        if (!SvgDeclarationEditor.Open(recipeText, out var document, out positions, out refusal, declarationsMustBeValid: false))
        {
            return false;
        }

        if (document!.Root is not { } found || found.Name != Ns + "recipe")
        {
            refusal = $"This is not a recipe: the root is <{document.Root?.Name.LocalName ?? "nothing"}>.";

            return false;
        }

        root = found;

        return true;
    }
}
