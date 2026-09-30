// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Collections.Generic;

namespace Svg.Expressions.Recipes;

/// <summary>What <see cref="SvgRecipe.Bind"/> made of a template for one drawing.</summary>
public sealed class SvgRecipeBinding
{
    public SvgRecipeBinding(SvgRecipe? recipe, string? failure, IReadOnlyList<SvgRecipeSurveyValue> leftover)
    {
        Recipe = recipe;
        Failure = failure;
        Leftover = leftover;
    }

    /// <summary>A slot-free recipe ready for <see cref="SvgRecipeRewriter.Apply"/>, or null when the bind failed.</summary>
    /// <remarks>Which slot produced a rule is on <see cref="SvgReplaceRule.Slot"/>.</remarks>
    public SvgRecipe? Recipe { get; }

    /// <summary>Why the template does not fit the drawing, when it does not.</summary>
    public string? Failure { get; }

    /// <summary>The drawing's colours no rule claimed, which stay literal.</summary>
    public IReadOnlyList<SvgRecipeSurveyValue> Leftover { get; }
}
