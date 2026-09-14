#if PAINTCODE_ORACLE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Svg.Expressions;

namespace Svg.PaintCode.UnitTests.Oracle;

/// <summary>One import of the sample document, shared by every row that compares against it.</summary>
/// <remarks>
/// Importing 1014 drawings costs seconds and the rows number in the thousands, so it happens once
/// per process. The temp directory is left to the operating system: a static has no disposal point,
/// and the alternative — re-importing per row — is the thing this exists to avoid.
/// </remarks>
internal sealed class PaintCodeOracleSuite
{
    private static readonly Lazy<PaintCodeOracleSuite> s_instance =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

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
        var source = SampleFactAttribute.Path
            ?? throw new InvalidOperationException("Set SVG_PAINTCODE_SAMPLE to the PaintCode document.");

        var directory = Directory.CreateTempSubdirectory("paintcode-oracle").FullName;
        var document = PaintCodeDocument.Load(source);
        var result = PaintCodeImport.Run(document, new PaintCodeImportOptions(directory) { IncludeSymbolOnlyCanvases = true });

        // The importer reports where it approximated or dropped something. Those canvases are the
        // ones that cannot match, and deriving the list from the report rather than writing it out
        // means it shrinks by itself as the conversion improves.
        var noted = new HashSet<string>(result.Notes.Select(note => PaintCodeSlug.Of(note.Canvas)), StringComparer.Ordinal);

        var defaults = new Dictionary<string, ExprValue>(StringComparer.Ordinal);
        var drawings = new List<PaintCodeDrawing>(result.Files.Count);
        var claimed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var unmatched = new List<string>();

        foreach (var file in result.Files.OrderBy(f => f, StringComparer.Ordinal))
        {
            var slug = Path.GetFileNameWithoutExtension(file);
            var text = File.ReadAllText(file);
            var declarations = SvgExpressionDeclarations.Parse(text);
            var evaluator = ExprEvaluator.Create(declarations);

            foreach (var parameter in declarations.Parameters)
            {
                if (parameter.DefaultExpression is { } expression && !defaults.ContainsKey(parameter.Name))
                {
                    defaults[parameter.Name] = evaluator.EvaluateTo(expression, parameter.Type, parameter.Name);
                }
            }

            var key = PaintCodeOracle.Normalise(slug);

            if (!PaintCodeOracle.Methods.TryGetValue(key, out var method))
            {
                unmatched.Add(slug);
                continue;
            }

            if (!claimed.TryGetValue(method.Name, out var by))
            {
                claimed[method.Name] = by = new List<string>();
            }

            by.Add(slug);

            drawings.Add(new PaintCodeDrawing(slug, file, method, text.Contains("<text", StringComparison.Ordinal), noted.Contains(slug)));
        }

        var shared = claimed
            .Where(pair => pair.Value.Count > 1)
            .Select(pair => $"{pair.Key} <- {string.Join(", ", pair.Value)}")
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();

        return new PaintCodeOracleSuite(drawings, defaults, unmatched, shared);
    }
}

/// <summary>One imported drawing, and the PaintCode method that draws the same canvas.</summary>
internal sealed record PaintCodeDrawing(string Slug, string Path, MethodInfo Method, bool HasText, bool Noted);
#endif
