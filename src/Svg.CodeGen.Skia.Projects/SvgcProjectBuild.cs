// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Svg.Expressions;
using Svg.Expressions.Recipes;
using Svg.Model;
using Svg.Model.Services;
using Svg.Skia;

namespace Svg.CodeGen.Skia.Projects;

/// <summary>
/// What a build comes to once a project and a command line have both had their say.
/// </summary>
/// <remarks>
/// Separate from <see cref="SvgcProject"/> because a project's settings are nullable — a value it
/// did not mention has to stay distinguishable from one it set, or a flag could not override it.
/// By the time a build starts every question has an answer, and these are the answers.
/// </remarks>
public sealed class SvgcBuildSettings
{
    public SvgEmit Emit { get; set; } = SvgEmit.CSharp;

    public SvgPictureCache Cache { get; set; } = SvgPictureCache.None;

    public SvgHelperScope HelperScope { get; set; } = SvgHelperScope.FileLocal;

    public SkiaSharpTarget SkiaSharp { get; set; } = SkiaSharpTarget.V4;

    public string Namespace { get; set; } = "Svg";

    public string Class { get; set; } = "Generated";

    public string? Recipe { get; set; }

    public string? SingleFile { get; set; }

    public SvgSizeRequest Size { get; set; } = SvgSizeRequest.None;

    /// <summary>The settings a project asks for, with the built-in defaults under them.</summary>
    /// <remarks>
    /// What a caller with no options of its own wants. <c>svgc</c> starts here and lays its flags
    /// on top, which is the whole of "a flag beats the project file, which beats the default".
    /// </remarks>
    public static SvgcBuildSettings For(SvgcProject project)
    {
        if (project is null)
        {
            throw new ArgumentNullException(nameof(project));
        }

        return new SvgcBuildSettings
        {
            Emit = project.Emit ?? SvgEmit.CSharp,
            Cache = project.Cache ?? SvgPictureCache.None,
            HelperScope = project.HelperScope ?? SvgHelperScope.FileLocal,
            SkiaSharp = project.SkiaSharp ?? SkiaSharpTarget.V4,
            Namespace = project.Namespace ?? "Svg",
            Class = project.Class ?? "Generated",
            Recipe = project.Recipe,
            SingleFile = project.SingleFile,
            Size = new SvgSizeRequest(project.Width, project.Height, project.Scale, SvgPadding.Parse(project.Padding))
        };
    }
}

/// <summary>
/// Builds a project: everything <c>svgc</c> does once it has read one.
/// </summary>
/// <remarks>
/// Here rather than in the tool because it is no longer only the tool that builds: an editor
/// showing a project wants to produce the same files, and two implementations of one build disagree
/// eventually — silently, since the output of both compiles.
/// </remarks>
public static class SvgcProjectBuild
{
    /// <summary>Builds every drawing of <paramref name="project"/> and writes what it names.</summary>
    /// <param name="assetLoader">How an <c>&lt;image&gt;</c> is read; the caller's renderer knows.</param>
    /// <param name="log">Told what is being read and written, and warned. Optional.</param>
    /// <returns>The files written, in the order they were written.</returns>
    /// <exception cref="SvgcProjectException">The project asks for a build that cannot be made.</exception>
    public static IReadOnlyList<string> Run(
        SvgcProject project,
        SvgcBuildSettings settings,
        ISvgAssetLoader assetLoader,
        Action<string>? log = null)
    {
        if (project is null)
        {
            throw new ArgumentNullException(nameof(project));
        }

        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        if (assetLoader is null)
        {
            throw new ArgumentNullException(nameof(assetLoader));
        }

        Refuse(project, settings);

        var written = new List<string>();

        if (settings.SingleFile is { } singleFile)
        {
            var drawings = new List<SkiaCSharpDrawing>();

            // A per-item output is ignored here rather than rejected, so one project can be built
            // either way.
            foreach (var item in project.Items)
            {
                log?.Invoke($"Reading: {item.Input}");

                if (Build(item, settings, assetLoader, log) is { } drawing)
                {
                    drawings.Add(drawing);
                }
            }

            log?.Invoke($"Generating: {singleFile}");

            File.WriteAllText(
                singleFile,
                SkiaCSharpCodeGen.GenerateFile(
                    drawings,
                    settings.HelperScope,
                    HelperClassNameFor(settings.HelperScope, singleFile),
                    settings.Cache,
                    settings.SkiaSharp));

            written.Add(singleFile);

            return written;
        }

        foreach (var item in project.Items)
        {
            written.Add(Write(item, settings, assetLoader, log));
        }

        return written;
    }

    /// <summary>Says why a build cannot be made, before any of it is.</summary>
    private static void Refuse(SvgcProject project, SvgcBuildSettings settings)
    {
        if (settings.Emit != SvgEmit.Svg)
        {
            return;
        }

        // Without a recipe there is nothing to convert, so the output would be a copy of the input
        // — which is never what was meant.
        if (settings.Recipe is null && project.Items.All(item => item.Recipe is null))
        {
            throw new SvgcProjectException("Emitting svg needs a recipe. Pass -r, name one in the project, or emit csharp.");
        }

        // One file holds any number of C# classes but only ever one svg document.
        if (settings.SingleFile is { })
        {
            throw new SvgcProjectException("Emitting svg cannot be combined with a single file: an svg document holds one drawing.");
        }

        // A conversion rewrites the document's text and never builds a drawing, so there is nothing
        // for a size to apply to.
        if (!settings.Size.IsEmpty || project.Items.Any(item => item.HasSize || item.Padding is { }))
        {
            throw new SvgcProjectException("Emitting svg cannot be combined with a resize or a padding: the conversion rewrites the document's text and never compiles it.");
        }
    }

    /// <summary>
    /// Builds one drawing and writes it where it says, as C# or as the document a recipe made.
    /// </summary>
    /// <remarks>
    /// The single drawing <c>svgc</c> is given on its command line is one of these too, so the tool
    /// describes it as an item and comes back through here rather than keeping a second way to do
    /// the same thing.
    /// </remarks>
    /// <returns>The file written.</returns>
    public static string Write(
        SvgcProjectItem item,
        SvgcBuildSettings settings,
        ISvgAssetLoader assetLoader,
        Action<string>? log = null)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        if (item.Output is not { } output)
        {
            throw new SvgcProjectException(
                $"<svg input=\"{item.Input}\"> has no output, and the project names no singleFile to fold it into.");
        }

        if (settings.Emit == SvgEmit.Svg)
        {
            log?.Invoke($"Converting: {output}");

            // A recipe is a text transformation, so it has no business failing because the drawing
            // uses a filter or a font the renderer cannot model. Read, rewrite, write.
            File.WriteAllText(output, Recipe(File.ReadAllText(item.Input), item.Recipe ?? settings.Recipe!, log));

            return output;
        }

        log?.Invoke($"Generating: {output}");

        if (Build(item, settings, assetLoader, log) is { } drawing)
        {
            File.WriteAllText(
                output,
                SkiaCSharpCodeGen.Generate(
                    drawing.Picture,
                    drawing.NamespaceName,
                    drawing.ClassName,
                    drawing.Declarations,
                    settings.Cache,
                    settings.SkiaSharp));
        }

        return output;
    }

    /// <summary>Reads one drawing through its recipe, if any, and builds its model.</summary>
    public static SkiaCSharpDrawing? Build(
        SvgcProjectItem item,
        SvgcBuildSettings settings,
        ISvgAssetLoader assetLoader,
        Action<string>? log = null)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        var svg = File.ReadAllText(item.Input);

        if ((item.Recipe ?? settings.Recipe) is { } recipe)
        {
            svg = Recipe(svg, recipe, log);
        }

        if (SvgService.FromSvg(svg) is not { } svgDocument)
        {
            return null;
        }

        // Resizing the document rather than the picture it compiles to, so the drawing is fitted to
        // the new size the way the format defines rather than by a scale wrapped around it.
        SvgSceneSizing.Apply(svgDocument, assetLoader, SizeFor(item, settings.Size));

        if (SvgSceneRuntime.CreateModel(svgDocument, assetLoader) is not { Commands: { } } picture)
        {
            return null;
        }

        if (SvgExpressionSubstitution.WhyNotGeneratable(svgDocument) is { } refusal)
        {
            throw new SvgcProjectException($"{Path.GetFileName(item.Input)}: {refusal}");
        }

        var declarations = SvgExpressionDeclarations.Parse(svg);

        Warn(item.Input, declarations, log);

        return new SkiaCSharpDrawing(
            picture,
            item.Namespace ?? settings.Namespace,
            item.Class ?? settings.Class,
            declarations);
    }

    /// <summary>
    /// The size an item is built at: its own when it names one, the project's otherwise.
    /// </summary>
    /// <remarks>
    /// An item that names any of width, height or scale replaces the whole group rather than
    /// merging with it, for the same reason a flag does. Padding is overlaid on its own: an item
    /// asking for room to leave has not thereby said anything about what size to be.
    /// </remarks>
    public static SvgSizeRequest SizeFor(SvgcProjectItem item, SvgSizeRequest projectSize)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        var padding = item.Padding is { } text ? SvgPadding.Parse(text) : projectSize.Padding;

        return item.HasSize
            ? new SvgSizeRequest(item.Width, item.Height, item.Scale, padding)
            : new SvgSizeRequest(projectSize.Width, projectSize.Height, projectSize.Scale, padding);
    }

    /// <summary>
    /// An internal helper class sits in the global namespace, so two single-file outputs in one
    /// assembly would collide on the name. Deriving it from the output file keeps them apart. A
    /// file scoped one is invisible outside its file and needs no such thing.
    /// </summary>
    public static string HelperClassNameFor(SvgHelperScope scope, string outputPath)
    {
        if (scope != SvgHelperScope.Internal)
        {
            return SkiaCSharpCodeGen.DefaultHelperClassName;
        }

        var identifier = new StringBuilder();

        foreach (var c in Path.GetFileNameWithoutExtension(outputPath) ?? string.Empty)
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
    /// Rewrites a plain drawing into the expression format, so one recipe can parameterise a whole
    /// icon set through a project file.
    /// </summary>
    private static string Recipe(string svg, string recipePath, Action<string>? log)
    {
        var result = SvgRecipeRewriter.Apply(svg, SvgRecipe.Load(recipePath));

        foreach (var match in result.Matches)
        {
            log?.Invoke($"  {match.Rule.ColorText} -> {{{{ {match.Rule.Expression} }}}} ({match.Count})");
        }

        // Not an error: the same recipe usually covers a family of drawings, and not every drawing
        // uses every colour.
        foreach (var rule in result.UnmatchedRules)
        {
            log?.Invoke($"warning: nothing in {Path.GetFileName(recipePath)} matched '{rule.ColorText}'.");
        }

        return result.Svg;
    }

    /// <summary>Says when a drawing's declared defaults will not reach the generated signature.</summary>
    /// <remarks>
    /// The one place every generating path goes through, so a batch says it once per drawing rather
    /// than once per way of being asked. The generated file says the same thing where the signature
    /// is, since that is where a caller reads it.
    /// </remarks>
    private static void Warn(string inputPath, SvgExpressionDeclarations declarations, Action<string>? log)
    {
        if (log is null || declarations.EmitsDefaultArguments())
        {
            return;
        }

        var lost = declarations.Parameters
            .Where(parameter => parameter.DefaultExpression is { })
            .Select(parameter => $"'{parameter.Name}'")
            .ToList();

        log($"warning: {Path.GetFileName(inputPath)} declares a parameter with no default after one that has a default, "
            + $"so every argument is generated as required and {string.Join(", ", lost)} {(lost.Count == 1 ? "loses its" : "lose their")} default. "
            + "C# takes optional arguments last, and reordering them would change what a positional call means.");
    }
}
