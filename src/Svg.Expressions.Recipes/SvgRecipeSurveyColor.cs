// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
namespace Svg.Expressions.Recipes;

/// <summary>One literal colour a drawing paints with, and how many attributes it paints.</summary>
/// <remarks>
/// What an editor needs to offer a rule for: a recipe's <c>&lt;replace&gt;</c> names a colour, and
/// the colours worth naming are the ones the drawing actually has. Found by
/// <see cref="SvgRecipeRewriter.Survey"/>, which walks the document exactly as the rewrite does.
/// </remarks>
public sealed class SvgRecipeSurveyColor
{
    public SvgRecipeSurveyColor(int argb, int count)
    {
        Argb = argb;
        Count = count;
    }

    /// <summary>Normalised colour key, as <see cref="SvgColorRule.Argb"/> is. Matching is by value.</summary>
    public int Argb { get; }

    /// <summary>
    /// The colour as a rule should name it.
    /// </summary>
    /// <remarks>
    /// Canonical rather than as the document spelled it. The drawing may say <c>red</c> in one
    /// place and <c>#f00</c> in another and they are one colour here, so there is no one spelling
    /// to show — and this is the text a rule written for it will carry.
    /// </remarks>
    public string Text => SvgRecipeColor.ToText(Argb);

    /// <summary>How many attributes this colour paints, which is how much a rule for it would move.</summary>
    public int Count { get; }
}
