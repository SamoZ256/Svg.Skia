// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace Svg.Expressions.Recipes;

/// <summary>
/// A <c>&lt;replace&gt;</c> whose value is picked from the drawing when the recipe is bound,
/// rather than written in the recipe:
///
/// <code>
///   &lt;slot name="outline" paint="stroke"&gt;accent&lt;/slot&gt;
///   &lt;slot name="underlay" paint="fill" by="lightness" rank="1"&gt;withAlpha(accent, 0.3)&lt;/slot&gt;
///   &lt;slot attribute="stroke-width" rest="true" optional="true"&gt;$value * weight&lt;/slot&gt;
/// </code>
///
/// The candidates are the survey's values under <see cref="Attribute"/> that no earlier slot took,
/// narrowed by <see cref="Paint"/>, <see cref="Colors"/> and <see cref="Palette"/>, ordered by
/// <see cref="By"/>, and then either the one at <see cref="Rank"/> or, for <see cref="Rest"/>, all.
/// </summary>
public sealed class SvgRecipeSlot
{
    private SvgRecipeSlot(
        string? name,
        string attribute,
        ExprType type,
        string? paint,
        string by,
        int rank,
        IReadOnlyList<string> colors,
        int? palette,
        bool rest,
        bool optional,
        string expression)
    {
        Name = name;
        Attribute = attribute;
        Type = type;
        Paint = paint;
        By = by;
        Rank = rank;
        Colors = colors;
        Palette = palette;
        Rest = rest;
        Optional = optional;
        Expression = expression;
    }

    /// <summary>What the slot is called, for a picker and for messages; not referenced by anything.</summary>
    public string? Name { get; }

    /// <summary>The rule name the slot fills: <see cref="SvgRecipeValue.ColorName"/> or a number attribute.</summary>
    public string Attribute { get; }

    public ExprType Type { get; }

    /// <summary>A colour attribute a candidate must paint at least once, such as <c>fill</c> or <c>stroke</c>.</summary>
    public string? Paint { get; }

    /// <summary><c>coverage</c> (most used first), <c>lightness</c> (darkest first) or <c>document</c>.</summary>
    public string By { get; }

    /// <summary>1-based position in the ordered candidates; negative counts from the end.</summary>
    public int Rank { get; }

    /// <summary>Colour keys a candidate must be one of; empty for any.</summary>
    public IReadOnlyList<string> Colors { get; }

    /// <summary>1-based index into the palette the drawing's source reports; a candidate must be that colour.</summary>
    public int? Palette { get; }

    /// <summary>Takes every candidate rather than the one at <see cref="Rank"/>.</summary>
    public bool Rest { get; }

    /// <summary>Finding nothing leaves the slot empty instead of failing the bind.</summary>
    public bool Optional { get; }

    /// <summary>Expression text, where <c>$value</c> stands for the value matched.</summary>
    public string Expression { get; }

    internal string Written => Name is null ? "<slot>" : $"<slot name=\"{Name}\">";

    internal static SvgRecipeSlot Read(XElement element)
    {
        string? name = null;
        var attribute = SvgRecipeValue.ColorName;
        string? paint = null;
        var by = "coverage";
        int? rank = null;
        var colors = new List<string>();
        int? palette = null;
        var rest = false;
        var optional = false;

        foreach (var xattribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
        {
            var value = xattribute.Value.Trim();

            switch (xattribute.Name.LocalName)
            {
                case "name":
                    name = value;
                    break;
                case "attribute":
                    attribute = value;
                    break;
                case "paint":
                    paint = value;
                    break;
                case "by":
                    by = value;
                    break;
                case "rank":
                    rank = Integer(value, "rank");
                    break;
                case "color":
                    colors.AddRange(value
                        .Split(new[] { ' ', '\t', '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(color => SvgRecipeValue.Key(ExprType.Color, color, "A slot's color")));
                    break;
                case "palette":
                    palette = Integer(value, "palette");
                    break;
                case "rest":
                    rest = Flag(value, "rest");
                    break;
                case "optional":
                    optional = Flag(value, "optional");
                    break;
                default:
                    throw new SvgRecipeException(
                        $"<slot> has no '{xattribute.Name.LocalName}'. Expected name, attribute, paint, by, rank, color, palette, rest or optional.");
            }
        }

        var slot = name is null ? "<slot>" : $"<slot name=\"{name}\">";

        // Only these, because $value is written into the expression as the key's text, and a
        // keyword's text is not a literal the language reads.
        if (SvgRecipeValue.TypeFor(attribute) is not ({ } type and (ExprType.Color or ExprType.Number)))
        {
            throw new SvgRecipeException(
                $"{slot} cannot fill '{attribute}'. A slot fills {SvgRecipeValue.ColorName} or one of the number attributes a <replace> can name.");
        }

        var colour = type == ExprType.Color;

        if (!colour && (paint is { } || colors.Count > 0 || palette is { } || by == "lightness"))
        {
            throw new SvgRecipeException($"{slot} fills '{attribute}', so paint, color, palette and by=\"lightness\" do not apply to it.");
        }

        if (paint is { } && SvgExpressionAttributes.TypeFor(paint) != ExprType.Color)
        {
            throw new SvgRecipeException($"{slot} has paint=\"{paint}\", which is not a colour attribute such as fill or stroke.");
        }

        if (by is not ("coverage" or "lightness" or "document"))
        {
            throw new SvgRecipeException($"{slot} has by=\"{by}\". Expected coverage, lightness or document.");
        }

        if (rank == 0 || (rank is { } && rest))
        {
            throw new SvgRecipeException($"{slot} needs a rank other than 0, and none at all with rest=\"true\", which takes every candidate.");
        }

        if (palette < 1)
        {
            throw new SvgRecipeException($"{slot} has palette=\"{palette}\", but palette entries count from 1.");
        }

        var expression = SvgRecipe.ReadExpression(element, slot);

        return new SvgRecipeSlot(name, attribute, type, paint, by, rank ?? 1, colors, palette, rest, optional, expression);
    }

    /// <summary>The values this slot takes out of <paramref name="candidates"/>, in the order it ranks them.</summary>
    internal IReadOnlyList<SvgRecipeSurveyValue> Pick(IEnumerable<SvgRecipeSurveyValue> candidates, IReadOnlyList<string>? palette)
    {
        var wanted = Palette is { } index
            ? palette is { } && index <= palette.Count && SvgRecipeValue.TryKey(ExprType.Color, palette[index - 1], out var key) ? key : null
            : string.Empty;

        // A palette entry the source did not report, or one that is not a colour, leaves nothing to pick.
        if (wanted is null)
        {
            return Array.Empty<SvgRecipeSurveyValue>();
        }

        var filtered = candidates.Where(value =>
            value.Name == Attribute &&
            (Paint is null || value.Attributes.ContainsKey(Paint)) &&
            (Colors.Count == 0 || Colors.Contains(value.Text)) &&
            (wanted.Length == 0 || value.Text == wanted));

        // OrderBy is stable, so ties stay in document order.
        var ordered = By switch
        {
            "coverage" => filtered.OrderByDescending(value => value.Count),
            "lightness" => filtered.OrderBy(value => SvgRecipeColor.Luminance(SvgRecipeColor.Parse(value.Text, value.Text))),
            _ => filtered
        };

        var list = ordered.ToList();

        if (Rest)
        {
            return list;
        }

        var at = Rank > 0 ? Rank - 1 : list.Count + Rank;

        return at >= 0 && at < list.Count ? new[] { list[at] } : Array.Empty<SvgRecipeSurveyValue>();
    }

    private static int Integer(string value, string what)
        => int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new SvgRecipeException($"A slot's {what} is not a whole number: '{value}'.");

    private static bool Flag(string value, string what)
        => value switch
        {
            "true" => true,
            "false" => false,
            _ => throw new SvgRecipeException($"A slot's {what} is 'true' or 'false', not '{value}'.")
        };
}
