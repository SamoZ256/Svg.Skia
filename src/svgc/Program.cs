#nullable enable
using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Linq;
using System.Threading.Tasks;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions;
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
        if (ex is Svg.Expressions.ExprException expression)
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
        string? recipePath,
        SvgSizeRequest size)
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

        // Resizing the document rather than the picture it compiles to, so the drawing is fitted
        // to the new size the way the format defines rather than by a scale wrapped around it.
        SvgSceneSizing.Apply(svgDocument, AssetLoader, size);

        var picture = SvgSceneRuntime.CreateModel(svgDocument, AssetLoader);
        if (picture is null || picture.Commands is null)
        {
            return null;
        }

        var declarations = SvgExpressionDeclarations.Parse(svg);

        Warn(inputPath, declarations);

        return new SkiaCSharpDrawing(picture, namespaceName, className, declarations);
    }

    /// <summary>Says when a drawing's declared defaults will not reach the generated signature.</summary>
    /// <remarks>
    /// The one place every generating path goes through, so a batch says it once per drawing rather
    /// than once per way of being asked. The generated file says the same thing where the signature
    /// is, since that is where a caller reads it.
    /// </remarks>
    static void Warn(string inputPath, SvgExpressionDeclarations declarations)
    {
        if (declarations.EmitsDefaultArguments())
        {
            return;
        }

        var lost = declarations.Parameters
            .Where(parameter => parameter.DefaultExpression is { })
            .Select(parameter => $"'{parameter.Name}'")
            .ToList();

        Log(
            $"warning: {System.IO.Path.GetFileName(inputPath)} declares a parameter with no default after one that has a default, "
            + $"so every argument is generated as required and {string.Join(", ", lost)} {(lost.Count == 1 ? "loses its" : "lose their")} default. "
            + "C# takes optional arguments last, and reordering them would change what a positional call means.");
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
        string namespaceName,
        string className,
        string? recipePath,
        SvgSizeRequest size,
        SvgPictureCache cache = SvgPictureCache.None,
        SkiaSharpTarget skiaSharp = SkiaSharpTarget.V4)
    {
        if (Build(inputPath, namespaceName, className, recipePath, size) is { } drawing)
        {
            var text = SkiaCSharpCodeGen.Generate(
                drawing.Picture,
                drawing.NamespaceName,
                drawing.ClassName,
                drawing.Declarations,
                cache,
                skiaSharp);

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

    /// <summary>
    /// The size an item is built at: its own when it names one, the project's otherwise. An item
    /// that names any of width, height or scale replaces the whole group rather than merging with
    /// it, for the same reason a flag does.
    /// </summary>
    static SvgSizeRequest SizeFor(SvgcProjectItem item, SvgSizeRequest projectSize)
    {
        // Overlaid on its own, unlike the three sizing values: an item asking for room to leave has
        // not thereby said anything about what size to be, so it keeps the project's.
        var padding = item.Padding is { } text ? SvgPadding.Parse(text) : projectSize.Padding;

        return item.HasSize
            ? new SvgSizeRequest(item.Width, item.Height, item.Scale, padding)
            : new SvgSizeRequest(projectSize.Width, projectSize.Height, projectSize.Scale, padding);
    }

    /// <summary>Whether any item asks for a frame of its own, by size or by padding.</summary>
    static bool AnyItemReframes(SvgcProject? project)
    {
        if (project is null)
        {
            return false;
        }

        foreach (var item in project.Items)
        {
            if (item.HasSize || item.Padding is { })
            {
                return true;
            }
        }

        return false;
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


        var optionSkiaSharp = new Option(new[] { "--skiaSharp" }, "The SkiaSharp major version the generated code is compiled against: 3 or 4")
        {
            IsRequired = false,
            Argument = new Argument<string?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionSkiaSharp);

        var optionWidth = new Option(new[] { "--width" }, "Resize the drawing to this width in pixels, keeping its aspect ratio")
        {
            IsRequired = false,
            Argument = new Argument<string?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionWidth);

        var optionHeight = new Option(new[] { "--height" }, "Resize the drawing to this height in pixels, keeping its aspect ratio")
        {
            IsRequired = false,
            Argument = new Argument<string?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionHeight);

        var optionScale = new Option(new[] { "--scale" }, "Resize the drawing by this factor of the size it already has")
        {
            IsRequired = false,
            Argument = new Argument<string?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionScale);

        var optionPadding = new Option(
            new[] { "--padding" },
            "Space to leave around the drawing inside its size, as fractions of it: one, two, three or four values the CSS way, each 10% or 0.1")
        {
            IsRequired = false,
            Argument = new Argument<string?>(getDefaultValue: () => null)
        };
        rootCommand.AddOption(optionPadding);

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
                var skiaSharp = settings.SkiaSharp is { } ? SvgcProject.ParseSkiaSharpTarget(settings.SkiaSharp) : project?.SkiaSharp ?? SkiaSharpTarget.V4;
                var namespaceName = settings.Namespace ?? project?.Namespace ?? "Svg";
                var className = settings.Class ?? project?.Class ?? "Generated";
                var recipePath = settings.RecipeFile?.FullName ?? project?.Recipe;
                var singleFilePath = settings.SingleFile?.FullName ?? project?.SingleFile;

                // The three sizing options are one group rather than three settings, so a command
                // line that names any of them replaces the project's sizing outright. Merging them
                // would let a flag width join a project scale, which is a contradiction rather
                // than an override.
                // Padding is not one of that group: it says how much room to leave rather than what
                // size to be, so a command line naming only it keeps the project's sizing.
                var padding = SvgPadding.Parse(settings.Padding ?? project?.Padding);

                var size = settings.Width is { } || settings.Height is { } || settings.Scale is { }
                    ? new SvgSizeRequest(
                        SvgcProject.ParseLength(settings.Width, "width"),
                        SvgcProject.ParseLength(settings.Height, "height"),
                        SvgcProject.ParseScale(settings.Scale),
                        padding)
                    : new SvgSizeRequest(project?.Width, project?.Height, project?.Scale, padding);

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

                    // A conversion rewrites the document's text and never builds a drawing, so
                    // there is nothing for a size to apply to.
                    if (!size.IsEmpty || AnyItemReframes(project))
                    {
                        throw new ArgumentException("Emitting svg cannot be combined with a resize or a padding: the conversion rewrites the document's text and never compiles it.");
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
                            item.Recipe ?? recipePath,
                            SizeFor(item, size));

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
                        cache,
                        skiaSharp);

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
                                SizeFor(item, size),
                                cache,
                                skiaSharp);
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
                        size,
                        cache,
                        skiaSharp);
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
