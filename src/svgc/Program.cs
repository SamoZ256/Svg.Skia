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

                var build = new SvgcBuildSettings
                {
                    Emit = emit,
                    Cache = cache,
                    HelperScope = scope,
                    SkiaSharp = skiaSharp,
                    Namespace = namespaceName,
                    Class = className,
                    Recipe = recipePath,
                    SingleFile = singleFilePath,
                    Size = size
                };

                if (project is { })
                {
                    SvgcProjectBuild.Run(project, build, AssetLoader, Log);
                }
                else if (settings.InputFile is { } input && settings.OutputFile is { } output)
                {
                    // Without a recipe there is nothing to convert, so the output would be a copy
                    // of the input — which is never what was meant.
                    if (emit == SvgEmit.Svg && recipePath is null)
                    {
                        throw new ArgumentException("Emitting svg needs a recipe. Pass -r, name one in the project, or emit csharp.");
                    }

                    // Described as an item so the one drawing on a command line is built by what
                    // builds every other drawing, rather than by a second copy of it.
                    SvgcProjectBuild.Write(
                        new SvgcProjectItem(input.FullName, output.FullName, null, null, null),
                        build,
                        AssetLoader,
                        Log);
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
