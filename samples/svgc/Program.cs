#nullable enable
using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading.Tasks;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Projects;
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

        // A faulty recipe or project is the same kind of thing: a diagnostic about the input
        // files. So is a bad option value, which is a mistake in the command line rather than a
        // fault in svgc.
        if (ex is SvgRecipeException or SvgcProjectException or ArgumentException)
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

    /// <summary>
    /// Converts a drawing and writes the result, without building a scene model.
    /// </summary>
    /// <remarks>
    /// A recipe is a text transformation, so it has no business failing because the drawing uses
    /// a filter or a font the renderer cannot model. This path is read, rewrite, write.
    /// </remarks>
    static void Convert(string inputPath, string outputPath, string recipePath)
    {
        var svg = ApplyRecipe(System.IO.File.ReadAllText(inputPath), recipePath);

        System.IO.File.WriteAllText(outputPath, svg);
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
    // recipe can parameterise a whole icon set through a project file.
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

        var optionProjectFile = new Option(new[] { "--projectFile", "-p" }, "The relative or absolute path to a project file describing a whole build")
        {
            IsRequired = false,
            Argument = new Argument<System.IO.FileInfo?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionProjectFile);

        var optionRecipeFile = new Option(new[] { "--recipeFile", "-r" }, "The relative or absolute path to a recipe applied to the input before generating")
        {
            IsRequired = false,
            Argument = new Argument<System.IO.FileInfo?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionRecipeFile);


        var optionEmit = new Option(new[] { "--emit" }, "What the output file receives: csharp, or svg for the document the recipe produced")
        {
            IsRequired = false,
            Argument = new Argument<string?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionEmit);

        var optionSingleFile = new Option(new[] { "--singleFile" }, "Emit every drawing of the batch into one C# file at this path")
        {
            IsRequired = false,
            Argument = new Argument<System.IO.FileInfo?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionSingleFile);

        var optionHelperScope = new Option(new[] { "--helperScope" }, "Where shared helpers go in a single file: file (C# 11), internal, or perClass")
        {
            IsRequired = false,
            Argument = new Argument<string?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionHelperScope);

        var optionCache = new Option(new[] { "--cache" }, "Keep the last picture Draw built and reuse it while the arguments are unchanged: none, lastValue or lastValueLocked")
        {
            IsRequired = false,
            Argument = new Argument<string?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionCache);

        var optionNamespace = new Option(new[] { "--namespace", "-n" }, "The generated C# namespace name")
        {
            IsRequired = false,
            Argument = new Argument<string?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionNamespace);

        var optionClass = new Option(new[] { "--class", "-c" }, "The generated C# class name")
        {
            IsRequired = false,
            Argument = new Argument<string?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionClass);

        rootCommand.Handler = CommandHandler.Create((Settings settings) =>
        {
            try
            {
                var project = settings.ProjectFile is { } projectFile
                    ? SvgcProject.Load(projectFile.FullName)
                    : null;

                // A flag beats the project file, which beats the built-in default — the ordinary
                // convention, and the reason every one of these is nullable up to this point.
                var emit = settings.Emit is { } ? SvgcProject.ParseEmit(settings.Emit) : project?.Emit ?? SvgEmit.CSharp;
                var cache = settings.Cache is { } ? SvgcProject.ParseCache(settings.Cache) : project?.Cache ?? SvgPictureCache.None;
                var scope = settings.HelperScope is { } ? SvgcProject.ParseHelperScope(settings.HelperScope) : project?.HelperScope ?? SvgHelperScope.FileLocal;
                var namespaceName = settings.Namespace ?? project?.Namespace ?? "Svg";
                var className = settings.Class ?? project?.Class ?? "Generated";
                var recipePath = settings.RecipeFile?.FullName ?? project?.Recipe;
                var singleFilePath = settings.SingleFile?.FullName ?? project?.SingleFile;

                if (emit == SvgEmit.Svg)
                {
                    // Without a recipe there is nothing to convert, so the output would be a copy
                    // of the input — which is never what was meant.
                    if (recipePath is null)
                    {
                        throw new ArgumentException("Emitting svg needs a recipe. Pass -r, name one in the project, or emit csharp.");
                    }

                    // One file holds any number of C# classes but only ever one svg document.
                    if (singleFilePath is { })
                    {
                        throw new ArgumentException("Emitting svg cannot be combined with a single file: an svg document holds one drawing.");
                    }
                }

                if (project is { } && singleFilePath is { })
                {
                    var drawings = new System.Collections.Generic.List<SkiaCSharpDrawing>();

                    // A per-item output is ignored here rather than rejected, so one project can
                    // be built either way.
                    foreach (var item in project.Items)
                    {
                        Log($"Reading: {item.Input}");

                        var drawing = Build(
                            item.Input,
                            item.Namespace ?? namespaceName,
                            item.Class ?? className,
                            item.Recipe ?? recipePath);

                        if (drawing is { })
                        {
                            drawings.Add(drawing);
                        }
                    }

                    Log($"Generating: {singleFilePath}");

                    var text = SkiaCSharpCodeGen.GenerateFile(
                        drawings,
                        scope,
                        HelperClassNameFor(scope, singleFilePath),
                        cache);

                    System.IO.File.WriteAllText(singleFilePath, text);
                }
                else if (project is { })
                {
                    foreach (var item in project.Items)
                    {
                        if (item.Output is null)
                        {
                            throw new SvgcProjectException(
                                $"<svg input=\"{item.Input}\"> has no output, and the project names no singleFile to fold it into.");
                        }

                        if (emit == SvgEmit.Svg)
                        {
                            Log($"Converting: {item.Output}");
                            Convert(item.Input, item.Output, item.Recipe ?? recipePath!);
                        }
                        else
                        {
                            Log($"Generating: {item.Output}");
                            Generate(
                                item.Input,
                                item.Output,
                                item.Namespace ?? namespaceName,
                                item.Class ?? className,
                                // One recipe usually covers the whole set, so an item only has to
                                // name its own when it differs.
                                item.Recipe ?? recipePath,
                                cache);
                        }
                    }
                }

                if (settings.InputFile is { } && settings.OutputFile is { } && emit == SvgEmit.Svg)
                {
                    Log($"Converting: {settings.OutputFile.FullName}");
                    Convert(settings.InputFile.FullName, settings.OutputFile.FullName, recipePath!);
                }
                else if (settings.InputFile is { } && settings.OutputFile is { })
                {
                    Log($"Generating: {settings.OutputFile.FullName}");
                    Generate(
                        settings.InputFile.FullName,
                        settings.OutputFile.FullName,
                        namespaceName,
                        className,
                        recipePath,
                        cache);
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
