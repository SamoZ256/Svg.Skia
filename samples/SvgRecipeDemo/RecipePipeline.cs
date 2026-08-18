using System;
using System.Collections.Generic;
using System.Linq;
using Svg.Expressions.Recipes;
using SvgExpressionsDemo;

namespace SvgRecipeDemo;

/// <summary>The state of one run of the whole chain, whichever stage it stopped at.</summary>
public sealed class RecipeRunResult
{
    private RecipeRunResult(
        string? convertedSvg,
        IReadOnlyList<SvgRecipeRuleMatch> matches,
        IReadOnlyList<string> recipeErrors,
        LivePreviewResult? preview)
    {
        ConvertedSvg = convertedSvg;
        Matches = matches;
        RecipeErrors = recipeErrors;
        Preview = preview;
    }

    /// <summary>The document in the expression format, once the recipe has been applied.</summary>
    public string? ConvertedSvg { get; }

    public IReadOnlyList<SvgRecipeRuleMatch> Matches { get; }

    /// <summary>Faults in the recipe or in the drawing it was applied to.</summary>
    public IReadOnlyList<string> RecipeErrors { get; }

    /// <summary>The drawing stage, or null when the conversion never got that far.</summary>
    public LivePreviewResult? Preview { get; }

    public bool Success => Preview is { Success: true };

    public IEnumerable<string> AllErrors
        => RecipeErrors.Concat(Preview?.Errors ?? Array.Empty<string>());

    internal static RecipeRunResult RecipeFailed(string error)
        => new(null, Array.Empty<SvgRecipeRuleMatch>(), new[] { error }, null);

    internal static RecipeRunResult Converted(SvgRecipeResult conversion, LivePreviewResult preview)
        => new(conversion.Svg, conversion.Matches, Array.Empty<string>(), preview);
}

/// <summary>
/// The demo's subject: recipe plus plain SVG, to a document in the expression format, to a drawing
/// whose expressions are evaluated against live values.
/// </summary>
/// <remarks>
/// <para>
/// Every stage is the shipping code — <see cref="SvgRecipeRewriter"/> for the conversion, and behind
/// <see cref="LivePreview"/> the scene compiler and <c>SvgSceneExpressionEvaluator</c>. There is no
/// second implementation here that could drift from what the library does.
/// </para>
/// <para>
/// It used to end in generated C#, which meant the demo doubled as a check that the code generator
/// produced the same drawing. It no longer does: this renders through the evaluator, while
/// <c>svgc</c> emits C#. The two agreeing is pinned by the test suite instead —
/// <c>ExprEvaluatorDifferentialTests</c> compares evaluated values against compiled generated code,
/// and the render tests compare the two as pixels.
/// </para>
/// </remarks>
public sealed class RecipePipeline
{
    private readonly LivePreview _preview = new();

    public RecipeRunResult Run(string svgSource, string recipeText)
    {
        SvgRecipeResult conversion;

        try
        {
            conversion = SvgRecipeRewriter.Apply(svgSource, SvgRecipe.Parse(recipeText));
        }
        catch (SvgRecipeException error)
        {
            return RecipeRunResult.RecipeFailed(error.Message);
        }
        catch (Exception error)
        {
            return RecipeRunResult.RecipeFailed(error.Message);
        }

        return RecipeRunResult.Converted(conversion, _preview.Load(conversion.Svg));
    }
}
