// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Svg.Expressions.Recipes;

/// <summary>
/// The conditions a drawing has to meet for a recipe to be offered for it:
///
/// <code>
///   &lt;match colors="2-3" strokes="1" fills="1+" family="*line*" style="!*solid*" name="*" has="#4147d5|#2859c5" /&gt;
/// </code>
///
/// Counts are <c>n</c>, <c>n-m</c> or <c>n+</c>, over the drawing's distinct colours, those painted
/// by a stroke, and those painted by a fill. Family, style and name are globs with <c>*</c> and
/// <c>?</c>, negated by a leading <c>!</c>, and <c>has</c> passes when any of its colours is present.
/// Each property is the text as written, null when the condition is not set.
/// </summary>
public sealed class SvgRecipeMatch
{
    private readonly (int Min, int Max)? _colors;
    private readonly (int Min, int Max)? _strokes;
    private readonly (int Min, int Max)? _fills;
    private readonly HashSet<string>? _has;

    private SvgRecipeMatch(XElement element)
    {
        foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
        {
            var value = attribute.Value.Trim();

            switch (attribute.Name.LocalName)
            {
                case "colors":
                    Colors = value;
                    _colors = Count(value, "colors");
                    break;
                case "strokes":
                    Strokes = value;
                    _strokes = Count(value, "strokes");
                    break;
                case "fills":
                    Fills = value;
                    _fills = Count(value, "fills");
                    break;
                case "family":
                    Family = value;
                    break;
                case "style":
                    Style = value;
                    break;
                case "name":
                    Name = value;
                    break;
                case "has":
                    Has = value;
                    _has = new HashSet<string>(
                        value.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(color => SvgRecipeValue.Key(ExprType.Color, color, "A colour in <match has>")),
                        StringComparer.Ordinal);
                    break;
                default:
                    throw new SvgRecipeException(
                        $"<match> has no '{attribute.Name.LocalName}'. Expected colors, strokes, fills, family, style, name or has.");
            }
        }
    }

    public string? Colors { get; }

    public string? Strokes { get; }

    public string? Fills { get; }

    public string? Family { get; }

    public string? Style { get; }

    public string? Name { get; }

    public string? Has { get; }

    internal static SvgRecipeMatch Read(XElement element) => new(element);

    /// <summary>Whether a drawing with this survey, and these facts from wherever it came from, passes.</summary>
    /// <remarks>A fact that is not known is matched as empty, so <c>*</c> and a negation still pass it.</remarks>
    public bool IsMatch(IReadOnlyList<SvgRecipeSurveyValue> survey, string? family = null, string? style = null, string? name = null)
    {
        var colours = survey.Where(value => value.Name == SvgRecipeValue.ColorName).ToList();

        return Within(_colors, colours.Count)
               && Within(_strokes, colours.Count(value => value.Strokes > 0))
               && Within(_fills, colours.Count(value => value.Fills > 0))
               && Glob(Family, family)
               && Glob(Style, style)
               && Glob(Name, name)
               && (_has is null || colours.Any(value => _has.Contains(value.Text)));
    }

    private static bool Within((int Min, int Max)? range, int count)
        => range is not { } r || (count >= r.Min && count <= r.Max);

    private static bool Glob(string? pattern, string? text)
    {
        if (pattern is null)
        {
            return true;
        }

        var negated = pattern.StartsWith("!", StringComparison.Ordinal);
        var body = negated ? pattern.Substring(1) : pattern;
        var regex = "^" + Regex.Escape(body).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";

        return Regex.IsMatch(text ?? string.Empty, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) != negated;
    }

    private static (int Min, int Max) Count(string value, string what)
    {
        var match = Regex.Match(value, @"^([0-9]+)(?:(\+)|-([0-9]+))?$");

        var max = 0;

        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var min) ||
            (match.Groups[3].Success && !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out max)))
        {
            throw new SvgRecipeException($"<match {what}=\"{value}\"> is not a count. Write one as 2, 2-3 or 2+.");
        }

        return match.Groups[2].Success ? (min, int.MaxValue)
            : match.Groups[3].Success ? (min, max)
            : (min, min);
    }
}
