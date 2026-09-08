// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
namespace Svg.Expressions.Recipes;

/// <summary>One literal value a drawing uses, and how many attributes carry it.</summary>
/// <remarks>
/// What an editor needs to offer a rule for: a recipe's <c>&lt;replace&gt;</c> names a value, and
/// the values worth naming are the ones the drawing actually has. Found by
/// <see cref="SvgRecipeRewriter.Survey"/>, which walks the document exactly as the rewrite does.
///
/// One of these is one rule. Colours are counted under <see cref="SvgRecipeValue.ColorName"/>
/// however many attributes paint them, because that is the rule an author would write for one;
/// every other kind is counted per attribute, because that is the rule for one of those.
/// </remarks>
public sealed class SvgRecipeSurveyValue
{
    public SvgRecipeSurveyValue(string name, string text, ExprType type, int count)
    {
        Name = name;
        Text = text;
        Type = type;
        Count = count;
    }

    /// <summary>The rule name that would claim this: an attribute, or <c>color</c>.</summary>
    public string Name { get; }

    /// <summary>
    /// The value as a rule should name it.
    /// </summary>
    /// <remarks>
    /// Canonical rather than as the document spelled it. The drawing may say <c>red</c> in one
    /// place and <c>#f00</c> in another and they are one value here, so there is no one spelling
    /// to show — and this is the text a rule written for it will carry.
    /// </remarks>
    public string Text { get; }

    /// <summary>What an expression replacing it has to come to.</summary>
    public ExprType Type { get; }

    /// <summary>How many attributes carry this value, which is how much a rule for it would move.</summary>
    public int Count { get; }
}
