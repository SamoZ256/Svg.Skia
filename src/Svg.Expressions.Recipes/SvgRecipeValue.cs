// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Svg.Expressions.Recipes;

/// <summary>
/// What a <c>&lt;replace&gt;</c> can name, and how a value of each kind is reduced to the key that
/// matching compares.
/// </summary>
/// <remarks>
/// Normalising goes through whatever the document parser reads that attribute with, for the reason
/// <see cref="SvgRecipeColor"/> gives about colours: a value this understood but the parser did not
/// would attach an expression to something that was never painted. So a colour is an
/// <c>SvgColourConverter</c> away from its key, a number is a <c>float</c> because the parser reads
/// these as one, and a keyword is compared the way the pipeline compares it — case-insensitively,
/// as <c>MaskingService.IsDisplayRendered</c> does.
///
/// A key is also the canonical spelling, so a survey row and a new rule agree without a second
/// renderer for each kind.
/// </remarks>
public static class SvgRecipeValue
{
    /// <summary>The one rule name that is not an attribute: every colour attribute at once.</summary>
    /// <remarks>
    /// A colour means the same thing wherever it is painted, so an author naming one means all of
    /// them — which is what a recipe over a family of icons wants and what the feature has always
    /// done. The other kinds have no such name: a group's <c>opacity</c> and a stop's
    /// <c>stop-opacity</c> are different quantities, and one name for both would replace a value in
    /// a place nobody meant.
    ///
    /// Because colours are named only this way, a rule name and an attribute never claim the same
    /// value twice, and there is no precedence to define.
    /// </remarks>
    public const string ColorName = "color";

    /// <summary>The names a rule can take, in the order a survey meets them.</summary>
    public static IReadOnlyList<string> Names { get; } =
        new[] { ColorName }
            .Concat(SvgExpressionAttributes.Supported.Where(name => SvgExpressionAttributes.TypeFor(name) != ExprType.Color))
            .ToList();

    /// <summary>What a rule called <paramref name="name"/> replaces, or null where nothing does.</summary>
    public static ExprType? TypeFor(string name)
        => name == ColorName
            ? ExprType.Color
            : SvgExpressionAttributes.TypeFor(name) is { } type && type != ExprType.Color
                ? type
                : null;

    /// <summary>The rule name that claims <paramref name="attribute"/>.</summary>
    public static string NameFor(string attribute)
        => SvgExpressionAttributes.TypeFor(attribute) == ExprType.Color ? ColorName : attribute;

    /// <summary>
    /// Reduces <paramref name="value"/> to the key matching compares, or answers false where it is
    /// not a value a rule could name.
    /// </summary>
    /// <remarks>
    /// Never an error. An unreadable value is simply not a match — the document parser reports its
    /// own diagnostics for these, and failing here would refuse a recipe over values it never
    /// mentions.
    /// </remarks>
    public static bool TryKey(ExprType type, string? value, out string key)
    {
        key = string.Empty;

        switch (type)
        {
            case ExprType.Color:
                if (!SvgRecipeColor.TryParse(value, out var argb))
                {
                    return false;
                }

                key = SvgRecipeColor.ToText(argb);
                return true;

            case ExprType.Number:
                // Plain float, because that is what the parser reads these attributes as
                // (SvgElementStyle.Opacity and its kind are float properties). A percentage or a
                // unit is deliberately not a match: it is narrower than the parser rather than
                // wider, so nothing is offered that the parser could not read back.
                if (value is null ||
                    !float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    return false;
                }

                key = number.ToString(CultureInfo.InvariantCulture);
                return true;

            case ExprType.Boolean:
                // A keyword, kept as the parser keeps it: visibility and display are string
                // properties, compared case-insensitively where they are read.
                var text = value?.Trim() ?? string.Empty;

                if (text.Length == 0)
                {
                    return false;
                }

                key = text.ToLowerInvariant();
                return true;

            default:
                return false;
        }
    }

    /// <summary>Reads a value written in a recipe, where one that cannot be read is an error.</summary>
    public static string Key(ExprType type, string? value, string what)
        => TryKey(type, value, out var key)
            ? key
            : throw new SvgRecipeException($"{what} is not a {Describe(type)}: '{value}'.");

    /// <summary>What a rule of this kind names, for a message.</summary>
    public static string Describe(ExprType type)
        => type switch
        {
            ExprType.Color => "colour",
            ExprType.Number => "number",
            ExprType.Boolean => "keyword",
            _ => type.ToString().ToLowerInvariant()
        };
}
