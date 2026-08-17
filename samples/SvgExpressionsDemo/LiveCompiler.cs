using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Expressions;
using Svg.Expressions;
using Svg.Model.Services;
using Svg.Skia;

namespace SvgExpressionsDemo;

public sealed class LiveCompileResult
{
    private LiveCompileResult(
        string? generatedCode,
        IReadOnlyList<string> errors,
        IReadOnlyList<SvgCodeParameter> parameters,
        MethodInfo? record)
    {
        GeneratedCode = generatedCode;
        Errors = errors;
        Parameters = parameters;
        Record = record;
    }

    public string? GeneratedCode { get; }

    public IReadOnlyList<string> Errors { get; }

    public IReadOnlyList<SvgCodeParameter> Parameters { get; }

    private MethodInfo? Record { get; }

    public bool Success => Record is not null;

    public static LiveCompileResult Failed(IReadOnlyList<string> errors, string? generatedCode = null)
        => new(generatedCode, errors, Array.Empty<SvgCodeParameter>(), null);

    public static LiveCompileResult Succeeded(
        string generatedCode,
        IReadOnlyList<SvgCodeParameter> parameters,
        MethodInfo record)
        => new(generatedCode, Array.Empty<string>(), parameters, record);

    /// <summary>Calls the generated Record(...) with one argument per declared parameter.</summary>
    public SkiaSharp.SKPicture? Invoke(object?[] arguments)
        => Record?.Invoke(null, arguments) as SkiaSharp.SKPicture;
}

/// <summary>
/// Runs the real pipeline at runtime: SVG text to a scene model, to generated C#, to a compiled
/// assembly whose Record(...) can be called with live argument values.
/// </summary>
public sealed class LiveCompiler
{
    private const string GeneratedNamespace = "SvgExpressionsDemo.Live";

    private const string GeneratedClass = "LiveLogo";

    private static readonly Svg.Model.ISvgAssetLoader s_assetLoader =
        new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings()));

    // Every compile produces a fresh assembly, so each needs a distinct name.
    private int _generation;

    // Collectible so a long editing session does not accumulate assemblies.
    private AssemblyLoadContext? _current;

    private static IReadOnlyList<MetadataReference>? s_references;

    public LiveCompileResult Compile(string svgText)
    {
        string generated;
        IReadOnlyList<SvgCodeParameter> parameters;

        try
        {
            var document = SvgService.FromSvg(svgText);
            if (document is null)
            {
                return LiveCompileResult.Failed(new[] { "The document could not be parsed as SVG." });
            }

            var picture = SvgSceneRuntime.CreateModel(document, s_assetLoader);
            if (picture?.Commands is null)
            {
                return LiveCompileResult.Failed(new[] { "The document produced no drawing commands." });
            }

            var declarations = SvgCodeDeclarations.Parse(svgText);
            parameters = declarations.Parameters;
            generated = SkiaCSharpCodeGen.Generate(picture, GeneratedNamespace, GeneratedClass, declarations);
        }
        catch (ExprException error)
        {
            // An expression problem is a diagnostic about the document, not a crash.
            return LiveCompileResult.Failed(new[] { error.ToDiagnostic() });
        }
        catch (Exception error)
        {
            return LiveCompileResult.Failed(new[] { error.Message });
        }

        var assemblyName = $"SvgExpressionsDemo.Live.{++_generation}";

        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { CSharpSyntaxTree.ParseText(SourceText.From(generated)) },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        using var peStream = new MemoryStream();
        var emitted = compilation.Emit(peStream);

        if (!emitted.Success)
        {
            var errors = emitted.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"{d.Id}: {d.GetMessage()} (generated line {d.Location.GetLineSpan().StartLinePosition.Line + 1})")
                .ToList();

            return LiveCompileResult.Failed(errors, generated);
        }

        peStream.Seek(0, SeekOrigin.Begin);

        var context = new AssemblyLoadContext(assemblyName, isCollectible: true);
        var assembly = context.LoadFromStream(peStream);

        var type = assembly.GetType($"{GeneratedNamespace}.{GeneratedClass}");
        // A document with no parameters generates `private static SKPicture Record()` — the
        // shape plain SVGs have always had — so NonPublic is required, not optional.
        var record = type?.GetMethod("Record", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        if (record is null)
        {
            context.Unload();
            return LiveCompileResult.Failed(new[] { "The generated assembly has no Record method." }, generated);
        }

        // Swap only once the new assembly is known good, so a failed edit keeps the last one.
        _current?.Unload();
        _current = context;

        return LiveCompileResult.Succeeded(generated, parameters, record);
    }

    private static IReadOnlyList<MetadataReference> References
    {
        get
        {
            if (s_references is { })
            {
                return s_references;
            }

            var paths = new HashSet<string>(StringComparer.Ordinal);

            // The trusted platform assemblies cover the BCL for this runtime.
            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
            {
                foreach (var path in trusted.Split(Path.PathSeparator))
                {
                    if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    {
                        paths.Add(path);
                    }
                }
            }

            // Generated code uses SkiaSharp types, which may not be in the platform list.
            var skia = typeof(SkiaSharp.SKColor).Assembly.Location;
            if (!string.IsNullOrEmpty(skia))
            {
                paths.Add(skia);
            }

            s_references = paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();

            return s_references;
        }
    }
}
