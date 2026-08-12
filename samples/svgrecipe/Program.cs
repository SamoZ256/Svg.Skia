#nullable enable
using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Threading.Tasks;
using Svg.Expressions.Recipes;

namespace svgrecipe;

class Program
{
    private static int s_exitCode;

    static void Log(string message)
    {
        Console.WriteLine(message);
    }

    static void Error(Exception ex)
    {
        s_exitCode = 1;

        // A malformed recipe is a diagnostic about the author's files, not a crash, so it is
        // reported the way svgc reports an expression error.
        if (ex is SvgRecipeException recipe)
        {
            Log($"error: {recipe.Message}");
            return;
        }

        Log($"{ex.Message}");
        Log($"{ex.StackTrace}");

        if (ex.InnerException is { })
        {
            Error(ex.InnerException);
        }
    }

    static void Convert(string inputPath, string recipePath, string outputPath, bool quiet)
    {
        var recipe = SvgRecipe.Load(recipePath);
        var result = SvgRecipeRewriter.Apply(File.ReadAllText(inputPath), recipe);

        File.WriteAllText(outputPath, result.Svg);

        if (quiet)
        {
            return;
        }

        foreach (var match in result.Matches)
        {
            Log($"  {match.Rule.ColorText} -> {{{{ {match.Rule.Expression} }}}} ({match.Count})");
        }

        // A rule that matched nothing is not an error — the same recipe often covers a family of
        // drawings — but it is the first thing to look at when the output is not what was meant.
        foreach (var rule in result.UnmatchedRules)
        {
            Log($"warning: nothing in the document uses '{rule.ColorText}'.");
        }
    }

    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand
        {
            Description = "Rewrites a svg file into the svg expression extension format using a recipe."
        };

        var optionInputFile = new Option(new[] { "--inputFile", "-i" }, "The relative or absolute path to the input svg file")
        {
            IsRequired = true,
            Argument = new Argument<FileInfo?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionInputFile);

        var optionRecipeFile = new Option(new[] { "--recipeFile", "-r" }, "The relative or absolute path to the recipe file")
        {
            IsRequired = true,
            Argument = new Argument<FileInfo?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionRecipeFile);

        var optionOutputFile = new Option(new[] { "--outputFile", "-o" }, "The relative or absolute path to the output svg file")
        {
            IsRequired = true,
            Argument = new Argument<FileInfo?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionOutputFile);

        var optionQuiet = new Option(new[] { "--quiet", "-q" }, "Do not report what each rule matched")
        {
            IsRequired = false,
            Argument = new Argument<bool>(getDefaultValue: () => false)
        };
        rootCommand.AddOption(optionQuiet);

        rootCommand.Handler = CommandHandler.Create((Settings settings) =>
        {
            try
            {
                if (settings.InputFile is { } input && settings.RecipeFile is { } recipe && settings.OutputFile is { } output)
                {
                    if (!settings.Quiet)
                    {
                        Log($"Converting: {output.FullName}");
                    }

                    Convert(input.FullName, recipe.FullName, output.FullName, settings.Quiet);
                }
            }
            catch (Exception ex)
            {
                Error(ex);
            }
        });

        var result = await rootCommand.InvokeAsync(args);

        return result != 0 ? result : s_exitCode;
    }
}
