#nullable enable
using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using System.Threading.Tasks;
using Svg.CodeGen.Skia;
using Svg.Expressions.Recipes;
using Svg.Skia;

namespace svgc;

class Program
{
    private static readonly Svg.Model.ISvgAssetLoader AssetLoader = new ImageSharpAssetLoader();

    static void Log(string message)
    {
        Console.WriteLine(message);
    }

    private static int s_exitCode;

    static void Error(Exception ex)
    {
        s_exitCode = 1;

        // An expression error is a diagnostic about the author's SVG, not a crash, so it is
        // reported like a compiler message instead of a stack trace.
        if (ex is Svg.CodeGen.Skia.Expressions.ExprException expression)
        {
            Log($"error: {expression.ToDiagnostic()}");
            return;
        }

        // A faulty recipe is the same kind of thing: a diagnostic about the input files.
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

    static void Generate(
        string inputPath,
        string outputPath,
        string namespaceName = "Svg",
        string className = "Generated",
        string? recipePath = null,
        string? emitSvgPath = null)
    {
        var svg = System.IO.File.ReadAllText(inputPath);

        if (recipePath is { })
        {
            svg = ApplyRecipe(svg, recipePath, emitSvgPath);
        }

        var svgDocument = Svg.Model.Services.SvgService.FromSvg(svg);
        if (svgDocument is { })
        {
            var picture = SvgSceneRuntime.CreateModel(svgDocument, AssetLoader);
            if (picture is { } && picture.Commands is { })
            {
                var declarations = SvgCodeDeclarations.Parse(svg);
                var text = SkiaCSharpCodeGen.Generate(picture, namespaceName, className, declarations);
                System.IO.File.WriteAllText(outputPath, text);
            }
        }
    }

    // Rewrites a plain drawing into the expression format before it is generated from, so one
    // recipe can parameterise a whole icon set through the json batch mode.
    static string ApplyRecipe(string svg, string recipePath, string? emitSvgPath)
    {
        var result = SvgRecipeRewriter.Apply(svg, SvgRecipe.Load(recipePath));

        foreach (var match in result.Matches)
        {
            Log($"  {match.Rule.ColorText} -> {{{{ {match.Rule.Expression} }}}} ({match.Count})");
        }

        // Not an error: the same recipe usually covers a family of drawings, and not every
        // drawing uses every colour.
        foreach (var rule in result.UnmatchedRules)
        {
            Log($"warning: nothing in {System.IO.Path.GetFileName(recipePath)} matched '{rule.ColorText}'.");
        }

        if (emitSvgPath is { })
        {
            System.IO.File.WriteAllText(emitSvgPath, result.Svg);
        }

        return result.Svg;
    }

    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand
        {
            Description = "Converts a svg file to a C# code."
        };

        var optionInputFile = new Option(new[] { "--inputFile", "-i" }, "The relative or absolute path to the input file")
        {
            IsRequired = false,
            Argument = new Argument<System.IO.FileInfo?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionInputFile);

        var optionOutputFile = new Option(new[] { "--outputFile", "-o" }, "The relative or absolute path to the output file")
        {
            IsRequired = false,
            Argument = new Argument<System.IO.FileInfo?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionOutputFile);

        var optionJsonFile = new Option(new[] { "--jsonFile", "-j" }, "The relative or absolute path to the json file")
        {
            IsRequired = false,
            Argument = new Argument<System.IO.FileInfo?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionJsonFile);

        var optionRecipeFile = new Option(new[] { "--recipeFile", "-r" }, "The relative or absolute path to a recipe applied to the input before generating")
        {
            IsRequired = false,
            Argument = new Argument<System.IO.FileInfo?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionRecipeFile);

        var optionEmitSvg = new Option(new[] { "--emitSvg" }, "Also write the converted svg, for inspecting what the recipe produced")
        {
            IsRequired = false,
            Argument = new Argument<System.IO.FileInfo?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionEmitSvg);

        var optionNamespace = new Option(new[] { "--namespace", "-n" }, "The generated C# namespace name")
        {
            IsRequired = false,
            Argument = new Argument<string>(getDefaultValue: () => "Svg")
        };
        rootCommand.AddOption(optionNamespace);

        var optionClass = new Option(new[] { "--class", "-c" }, "The generated C# class name")
        {
            IsRequired = false,
            Argument = new Argument<string>(getDefaultValue: () => "Generated")
        };
        rootCommand.AddOption(optionClass);

        rootCommand.Handler = CommandHandler.Create((Settings settings) =>
        {
            try
            {
                if (settings.JsonFile is { })
                {
                    var json = System.IO.File.ReadAllText(settings.JsonFile.FullName);
                    var options = new JsonSerializerOptions
                    {
                        ReadCommentHandling = JsonCommentHandling.Skip
                    };
                    var items = JsonSerializer.Deserialize<Item[]>(json, options);
                    if (items is { })
                    {
                        foreach (var item in items)
                        {
                            if (item.InputFile is { } && item.OutputFile is { })
                            {
                                Log($"Generating: {item.OutputFile}");
                                Generate(
                                    item.InputFile,
                                    item.OutputFile,
                                    item.Namespace ?? settings.Namespace,
                                    item.Class ?? settings.Class,
                                    // One recipe usually covers the whole set, so the item only
                                    // has to name its own when it differs.
                                    item.Recipe ?? settings.RecipeFile?.FullName,
                                    item.EmitSvg);
                            }
                        }
                    }
                }

                if (settings.InputFile is { } && settings.OutputFile is { })
                {
                    Log($"Generating: {settings.OutputFile.FullName}");
                    Generate(
                        settings.InputFile.FullName,
                        settings.OutputFile.FullName,
                        settings.Namespace,
                        settings.Class,
                        settings.RecipeFile?.FullName,
                        settings.EmitSvg?.FullName);
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
