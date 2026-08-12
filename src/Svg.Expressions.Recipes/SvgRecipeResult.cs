// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace Svg.Expressions.Recipes;

/// <summary>How many attributes one rule claimed.</summary>
public sealed class SvgRecipeRuleMatch
{
    public SvgRecipeRuleMatch(SvgColorRule rule, int count)
    {
        Rule = rule;
        Count = count;
    }

    public SvgColorRule Rule { get; }

    public int Count { get; }
}

/// <summary>
/// The rewritten document, with a per-rule tally. A rule that matched nothing is reported
/// rather than treated as an error: the same recipe is often applied to a family of drawings,
/// not all of which use every colour.
/// </summary>
public sealed class SvgRecipeResult
{
    public SvgRecipeResult(string svg, IReadOnlyList<SvgRecipeRuleMatch> matches)
    {
        Svg = svg;
        Matches = matches;
    }

    public string Svg { get; }

    public IReadOnlyList<SvgRecipeRuleMatch> Matches { get; }

    public int TotalReplacements => Matches.Sum(match => match.Count);

    public IEnumerable<SvgColorRule> UnmatchedRules
        => Matches.Where(match => match.Count == 0).Select(match => match.Rule);
}
