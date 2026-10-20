#if PAINTCODE_ORACLE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions;
using Svg.Studio;

namespace Svg.PaintCode.UnitTests.Oracle;

/// <summary>One read of the Studio project, shared by every row that compares against it.</summary>
/// <remarks>
/// The project rather than the PaintCode document it was imported from: the project is what gets
/// edited, so a drawing fixed there is measured as fixed. Read once per process, since the rows
/// number in the thousands; the temp directory is left to the operating system, a static having no
/// disposal point.
/// </remarks>
internal sealed class PaintCodeOracleSuite
{
    private static readonly Lazy<PaintCodeOracleSuite> s_instance =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Where this document's committed answers live.</summary>
    internal string Folder { get; private init; } = string.Empty;

    private PaintCodeOracleSuite(
        IReadOnlyList<PaintCodeDrawing> drawings,
        IReadOnlyDictionary<string, ExprValue> defaults,
        IReadOnlyList<string> unmatched,
        IReadOnlyList<string> shared)
    {
        Drawings = drawings;
        Defaults = defaults;
        Unmatched = unmatched;
        Shared = shared;
    }

    internal static PaintCodeOracleSuite Instance => s_instance.Value;

    internal IReadOnlyList<PaintCodeDrawing> Drawings { get; }

    /// <summary>
    /// Every parameter name the document declares anywhere, at its default.
    /// </summary>
    /// <remarks>
    /// A drawing's own block is narrowed to what that canvas references, but PaintCode's method
    /// still takes every variable its canvas was declared with — so a name our side dropped still
    /// needs a value on theirs. Each name carries one default throughout the document, which is what
    /// makes a single table correct rather than a guess.
    /// </remarks>
    internal IReadOnlyDictionary<string, ExprValue> Defaults { get; }

    /// <summary>Canvases with no drawing method of their own.</summary>
    internal IReadOnlyList<string> Unmatched { get; }

    /// <summary>Drawing methods claimed by more than one canvas.</summary>
    internal IReadOnlyList<string> Shared { get; }

    private static PaintCodeOracleSuite Load()
    {
        var source = OracleFactAttribute.Path
            ?? throw new InvalidOperationException("Set SVG_PAINTCODE_PROJECT to the .svgstudio to compare.");

        // Flattened as the build flattens it, so each drawing carries the declarations the groups
        // above it give it. Written out because a row loads, and leaves its rasters, by path.
        var items = ProjectDocument.Load(source).Flatten().Items;
        var directory = Directory.CreateTempSubdirectory("paintcode-oracle").FullName;

        // PaintCode's own name for a drawing where the project kept one, which is exact; the file's
        // name, matched loosely, where it did not.
        static string Canvas(SvgcProjectItem item)
            => item.Method is { } method && method.StartsWith("draw", StringComparison.Ordinal) ? method.Substring("draw".Length) : item.Input;

        // Which class in the reference draws this project, decided by how much of it each covers.
        PaintCodeOracle.Use(items.Select(Canvas));

        var defaults = new Dictionary<string, ExprValue>(StringComparer.Ordinal);
        var drawings = new List<PaintCodeDrawing>(items.Count);
        var claimed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var unmatched = new List<string>();

        foreach (var item in items.OrderBy(item => item.Input, StringComparer.Ordinal))
        {
            var slug = item.Input;
            var text = item.Source ?? throw new InvalidOperationException($"'{slug}' has no drawing.");
            var file = Path.Combine(directory, slug + ".svg");
            var declarations = SvgExpressionDeclarations.Parse(text);
            var evaluator = ExprEvaluator.Create(declarations);

            File.WriteAllText(file, text);

            foreach (var parameter in declarations.Parameters)
            {
                if (parameter.DefaultExpression is { } expression && !defaults.ContainsKey(parameter.Name))
                {
                    defaults[parameter.Name] = evaluator.EvaluateTo(expression, parameter.Type, parameter.Name);
                }
            }

            if (!PaintCodeOracle.Methods.TryGetValue(PaintCodeOracle.Normalise(Canvas(item)), out var method))
            {
                unmatched.Add(slug);
                continue;
            }

            if (!claimed.TryGetValue(method.Name, out var by))
            {
                claimed[method.Name] = by = new List<string>();
            }

            by.Add(slug);
            drawings.Add(new PaintCodeDrawing(slug, file, method, text.Contains("<text", StringComparison.Ordinal)));
        }

        var shared = claimed
            .Where(pair => pair.Value.Count > 1)
            .Select(pair => $"{pair.Key} <- {string.Join(", ", pair.Value)}")
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();

        var folder = Path.Combine(AppContext.BaseDirectory, "TestAssets", "Oracle", Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(source)));

        return new PaintCodeOracleSuite(drawings, defaults, unmatched, shared) { Folder = folder };
    }
}

/// <summary>One drawing of the project, and the PaintCode method that draws the same canvas.</summary>
internal sealed record PaintCodeDrawing(string Slug, string Path, MethodInfo Method, bool HasText);
#endif
