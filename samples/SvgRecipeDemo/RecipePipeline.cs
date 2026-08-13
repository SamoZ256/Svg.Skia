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
        LiveCompileResult? compiled)
    {
        ConvertedSvg = convertedSvg;
        Matches = matches;
        RecipeErrors = recipeErrors;
        Compiled = compiled;
    }

    /// <summary>The document in the expression format, once the recipe has been applied.</summary>
    public string? ConvertedSvg { get; }

    public IReadOnlyList<SvgRecipeRuleMatch> Matches { get; }

    /// <summary>Faults in the recipe or in the drawing it was applied to.</summary>
    public IReadOnlyList<string> RecipeErrors { get; }

    /// <summary>The C# stage, or null when the conversion never got that far.</summary>
    public LiveCompileResult? Compiled { get; }

    public bool Success => Compiled is { Success: true };

    public IEnumerable<string> AllErrors
        => RecipeErrors.Concat(Compiled?.Errors ?? Array.Empty<string>());

    internal static RecipeRunResult RecipeFailed(string error)
        => new(null, Array.Empty<SvgRecipeRuleMatch>(), new[] { error }, null);

    internal static RecipeRunResult Converted(SvgRecipeResult conversion, LiveCompileResult compiled)
        => new(conversion.Svg, conversion.Matches, Array.Empty<string>(), compiled);
}

/// <summary>
/// The demo's subject: recipe plus plain SVG, to a document in the expression format, to
/// generated C#, to a loaded assembly whose <c>Record(...)</c> draws.
/// </summary>
/// <remarks>
/// Every stage is the shipping code — <see cref="SvgRecipeRewriter"/> and, behind
/// <see cref="LiveCompiler"/>, <c>SkiaCSharpCodeGen</c>. There is no second implementation here
/// that could drift from what <c>svgc</c> produces.
/// </remarks>
public sealed class RecipePipeline
{
    private readonly LiveCompiler _compiler = new();

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

        return RecipeRunResult.Converted(conversion, _compiler.Compile(conversion.Svg));
    }
}
