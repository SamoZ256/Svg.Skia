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

        // A faulty recipe is the same kind of thing: a diagnostic about the input files. So is a
        // bad option value, which is a mistake in the command line rather than a fault in svgc.
        if (ex is SvgRecipeException or ArgumentException)
        {
            Log($"error: {ex.Message}");
            return;
        }

        Log($"{ex.Message}");
        Log($"{ex.StackTrace}");
        if (ex.InnerException is { })
        {
            Error(ex.InnerException);
        }
    }

    /// <summary>Reads a drawing through the recipe, if any, and builds its model.</summary>
    static SkiaCSharpDrawing? Build(
        string inputPath,
        string namespaceName,
        string className,
        string? recipePath)
    {
        var svg = System.IO.File.ReadAllText(inputPath);

        if (recipePath is { })
        {
            svg = ApplyRecipe(svg, recipePath);
        }

        var svgDocument = Svg.Model.Services.SvgService.FromSvg(svg);
        if (svgDocument is null)
        {
            return null;
        }

        var picture = SvgSceneRuntime.CreateModel(svgDocument, AssetLoader);
        if (picture is null || picture.Commands is null)
        {
            return null;
        }

        return new SkiaCSharpDrawing(picture, namespaceName, className, SvgCodeDeclarations.Parse(svg));
    }

    static void Generate(
        string inputPath,
        string outputPath,
        string namespaceName = "Svg",
        string className = "Generated",
        string? recipePath = null,
        SvgPictureCache cache = SvgPictureCache.None)
    {
        if (Build(inputPath, namespaceName, className, recipePath) is { } drawing)
        {
            var text = SkiaCSharpCodeGen.Generate(
                drawing.Picture,
                drawing.NamespaceName,
                drawing.ClassName,
                drawing.Declarations,
                cache);

            System.IO.File.WriteAllText(outputPath, text);
        }
    }

    static SvgHelperScope ParseHelperScope(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "file" => SvgHelperScope.FileLocal,
        "internal" => SvgHelperScope.Internal,
        "perclass" => SvgHelperScope.PerClass,
        _ => throw new ArgumentException($"'{value}' is not a helper scope. Expected file, internal or perClass.")
    };

    static SvgPictureCache ParseCache(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "none" => SvgPictureCache.None,
        "lastvalue" => SvgPictureCache.LastValue,
        "lastvaluelocked" => SvgPictureCache.LastValueLocked,
        _ => throw new ArgumentException($"'{value}' is not a cache mode. Expected none, lastValue or lastValueLocked.")
    };

    /// <summary>
    /// An internal helper class sits in the global namespace, so two single-file outputs in one
    /// assembly would collide on the name. Deriving it from the output file keeps them apart.
    /// A file scoped one is invisible outside its file and needs no such thing.
    /// </summary>
    static string HelperClassNameFor(SvgHelperScope scope, string outputPath)
    {
        if (scope != SvgHelperScope.Internal)
        {
            return SkiaCSharpCodeGen.DefaultHelperClassName;
        }

        var stem = System.IO.Path.GetFileNameWithoutExtension(outputPath) ?? string.Empty;
        var identifier = new System.Text.StringBuilder();

        foreach (var c in stem)
        {
            identifier.Append(c == '_' || char.IsLetterOrDigit(c) ? c : '_');
        }

        if (identifier.Length == 0 || char.IsDigit(identifier[0]))
        {
            identifier.Insert(0, '_');
        }

        return identifier + "_" + SkiaCSharpCodeGen.DefaultHelperClassName;
    }

    // Rewrites a plain drawing into the expression format before it is generated from, so one
    // recipe can parameterise a whole icon set through the json batch mode.
    static string ApplyRecipe(string svg, string recipePath)
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


        var optionSingleFile = new Option(new[] { "--singleFile" }, "Emit every drawing of the json batch into one C# file at this path")
        {
            IsRequired = false,
            Argument = new Argument<System.IO.FileInfo?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionSingleFile);

        var optionHelperScope = new Option(new[] { "--helperScope" }, "Where shared helpers go in a single file: file (C# 11), internal, or perClass")
        {
            IsRequired = false,
            Argument = new Argument<string>(getDefaultValue: () => "file")
        };
        rootCommand.AddOption(optionHelperScope);

        var optionCache = new Option(new[] { "--cache" }, "Keep the last picture Draw built and reuse it while the arguments are unchanged: none, lastValue or lastValueLocked")
        {
            IsRequired = false,
            Argument = new Argument<string>(getDefaultValue: () => "none")
        };
        rootCommand.AddOption(optionCache);

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
                    if (items is { } && settings.SingleFile is { } singleFile)
                    {
                        var scope = ParseHelperScope(settings.HelperScope);
                        var cache = ParseCache(settings.Cache);
                        var drawings = new System.Collections.Generic.List<SkiaCSharpDrawing>();

                        // OutputFile is ignored here rather than rejected, so the same batch file
                        // works in both modes.
                        foreach (var item in items)
                        {
                            if (item.InputFile is null)
                            {
                                continue;
                            }

                            Log($"Reading: {item.InputFile}");

                            var drawing = Build(
                                item.InputFile,
                                item.Namespace ?? settings.Namespace,
                                item.Class ?? settings.Class,
                                item.Recipe ?? settings.RecipeFile?.FullName);

                            if (drawing is { })
                            {
                                drawings.Add(drawing);
                            }
                        }

                        Log($"Generating: {singleFile.FullName}");

                        var text = SkiaCSharpCodeGen.GenerateFile(
                            drawings,
                            scope,
                            HelperClassNameFor(scope, singleFile.FullName),
                            cache);

                        System.IO.File.WriteAllText(singleFile.FullName, text);
                    }
                    else if (items is { })
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
                                    ParseCache(settings.Cache));
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
                        ParseCache(settings.Cache));
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
