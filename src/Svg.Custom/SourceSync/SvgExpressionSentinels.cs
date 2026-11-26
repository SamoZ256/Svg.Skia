// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Globalization;
using System.Linq;
using Svg.Expressions;

namespace Svg;

/// <summary>Stand-ins for lifted expressions that say which expression they stand for.</summary>
/// <remarks>
/// The loader's placeholders are one value per attribute, so a command that copies a driven fill
/// writes the same <c>#808080</c> a literal grey would be. Read twice with two runs of these, a value
/// that differs between the two was worked out from a stand-in, and one equal to the same
/// expression's stand-in in both was copied from it. Keyed on the expression's text rather than on
/// where it is written, so a third read of the edited file gives every expression the stand-in it
/// had before and the result can be compared value for value.
///
/// Colours, numbers and text only. A visibility has no value to spare that still draws, and what a
/// typeface or an anchor is read from is left absent by the loader anyway.
/// </remarks>
internal static class SvgExpressionSentinels
{
    /// <summary>Puts run <paramref name="run"/>'s stand-in, 0 or 1, in place of every placeholder in <paramref name="document"/>.</summary>
    internal static void Mark(SvgDocument document, int run)
    {
        foreach (var element in document.Descendants().Prepend(document).ToList())
        {
            var custom = element.CustomAttributes;

            foreach (var name in SvgExpressionAttributes.Supported)
            {
                if (SvgExpressionAttributes.Lifted(custom, name) is not { } expression
                    || SvgExpressionAttributes.IsResolvedBeforeRecording(name)
                    || SvgExpressionAttributes.TypeFor(name) is not (ExprType.Color or ExprType.Number))
                {
                    continue;
                }

                var text = SvgExpressionAttributes.IsInArguments(name)
                    ? SvgTransformExpression.Parse(name, expression).With(argument => Number(run, argument.Expression!))
                    : SvgExpressionAttributes.TypeFor(name) == ExprType.Color ? Colour(run, expression) : Number(run, expression);

                SvgElementFactory.SetPropertyValue(element, string.Empty, name, text, document);
            }

            if (SvgExpressionAttributes.Lifted(custom, SvgExpressionAttributes.ContentName) is { } said)
            {
                var text = "" + run.ToString(CultureInfo.InvariantCulture) + Hash(run, 't', said).ToString("x8", CultureInfo.InvariantCulture);

                element.Nodes.Clear();
                element.Nodes.Add(new SvgContentNode { Content = text });
                element.Content = text;
            }
        }
    }

    /// <remarks>
    /// Each run in a range of its own, so no expression has the same stand-in in both; within [0.25, 0.75)
    /// every one is a float exactly, inside an opacity's range, and neither nought nor one.
    /// </remarks>
    private static string Number(int run, string expression)
        => SvgValueSpelling.Number(0.25f + (run * 0.25f) + ((Hash(run, 'n', expression.Trim()) & 0x1FFFFF) / (float)(1 << 23)))!;

    private static string Colour(int run, string expression)
        => "#" + ((run << 23) | (Hash(run, 'c', expression.Trim()) & 0x7FFFFF)).ToString("x6", CultureInfo.InvariantCulture);

    /// <summary>FNV-1a, which a later process reading the same file agrees with.</summary>
    private static int Hash(int run, char kind, string text)
    {
        unchecked
        {
            var hash = (int)2166136261;

            foreach (var character in run.ToString(CultureInfo.InvariantCulture) + kind + text)
            {
                hash = (hash ^ character) * 16777619;
            }

            return hash;
        }
    }
}
