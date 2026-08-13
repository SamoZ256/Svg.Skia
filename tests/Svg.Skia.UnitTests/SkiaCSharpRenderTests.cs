using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SkiaSharp;
using Svg.CodeGen.Skia;
using Svg.Model.Services;
using Svg.Skia.UnitTests.Common;
using Xunit;
using ShimPicture = ShimSkiaSharp.SKPicture;

namespace Svg.Skia.UnitTests;

/// <summary>
/// Renders generated C# and compares it against the runtime renderer.
/// </summary>
/// <remarks>
/// The W3C and resvg suites exercise <c>SkiaModel</c>; the code generator's own tests assert the
/// emitted text and the source generator sample proves it compiles. Nothing else ever draws
/// generated code, so an emitter change can be green across the whole suite and still produce a
/// wrong picture. Both paths here start from the same <c>ShimPicture</c>, so a
/// difference can only come from the emitter.
/// </remarks>
public class SkiaCSharpRenderTests
{
    private const string Namespace = "Svg.Skia.UnitTests.Rendered";

    private const int Size = 256;

    // Floats are emitted with the shortest round-trippable form, so the two paths replay the same
    // command list exactly. Any difference at all is a defect rather than tolerable noise.
    private const double Threshold = 0d;

    private static IReadOnlyList<MetadataReference>? s_references;

    private static int s_generation;

    private static ShimPicture Model(string svgMarkup)
    {
        var document = SvgService.FromSvg(svgMarkup);
        Assert.NotNull(document);

        var assetLoader = new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings()));
        var picture = SvgSceneRuntime.CreateModel(document!, assetLoader);
        Assert.NotNull(picture);

        return picture!;
    }

    /// <summary>Compiles the emitted C# and calls its Record, exactly as a consumer would.</summary>
    private static SKPicture Generated(ShimPicture model, string className, SkiaSharpTarget skiaSharp)
    {
        var code = SkiaCSharpCodeGen.Generate(
            model,
            Namespace,
            className,
            SvgCodeDeclarations.Empty,
            SvgPictureCache.None,
            skiaSharp);

        var assemblyName = $"{Namespace}.{className}.{++s_generation}";

        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { CSharpSyntaxTree.ParseText(SourceText.From(code)) },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        using var peStream = new MemoryStream();
        var emitted = compilation.Emit(peStream);

        if (!emitted.Success)
        {
            var errors = emitted.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"{d.Id}: {d.GetMessage()} — {d.Location.GetLineSpan().StartLinePosition}");

            Assert.Fail($"The generated code did not compile:\n{string.Join("\n", errors)}\n\n{code}");
        }

        peStream.Seek(0, SeekOrigin.Begin);

        var assembly = new AssemblyLoadContext(assemblyName, isCollectible: false).LoadFromStream(peStream);
        var type = assembly.GetType($"{Namespace}.{className}");
        Assert.NotNull(type);

        // A document with no declarations generates `private static SKPicture Record()`.
        var record = type!.GetMethod("Record", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(record);

        var picture = record!.Invoke(null, Array.Empty<object?>()) as SKPicture;
        Assert.NotNull(picture);

        return picture!;
    }

    private static string Draw(SKPicture picture, string path)
    {
        var bounds = picture.CullRect;
        var scale = Math.Min(Size / Math.Max(bounds.Width, 1f), Size / Math.Max(bounds.Height, 1f));

        using var bitmap = new SKBitmap(Size, Size);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.Scale(scale);
            canvas.DrawPicture(picture);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);

        return path;
    }

    private static void AssertRendersTheSame(string name, string svgMarkup, SkiaSharpTarget skiaSharp = SkiaSharpTarget.V4)
    {
        var model = Model(svgMarkup);

        using var runtime = new SkiaModel(new SKSvgSettings()).ToSKPicture(model);
        Assert.NotNull(runtime);

        using var generated = Generated(model, name, skiaSharp);

        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var expected = Draw(runtime!, Path.Combine(directory, $"{name}-runtime.png"));
            var actual = Draw(generated, Path.Combine(directory, $"{name}-generated.png"));

            ImageHelper.CompareImages(name, actual, expected, Threshold);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

            var skia = typeof(SKColor).Assembly.Location;
            if (!string.IsNullOrEmpty(skia))
            {
                paths.Add(skia);
            }

            s_references = paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();

            return s_references;
        }
    }

    [Fact]
    public void Curves_And_Arcs()
        => AssertRendersTheSame("Curves", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <path d="M 2 12 C 2 6 6 2 12 2 A 10 10 0 0 1 22 12 L 12 22 Z" fill="#3b82f6" />
            </svg>
            """);

    [Fact]
    public void Non_Default_Fill_Rule()
        => AssertRendersTheSame("EvenOdd", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <path d="M 2 2 H 22 V 22 H 2 Z M 7 7 H 17 V 17 H 7 Z" fill="#1e40af" fill-rule="evenodd" />
            </svg>
            """);

    [Fact]
    public void Primitive_Shapes()
        => AssertRendersTheSame("Shapes", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect x="1" y="1" width="10" height="8" rx="2" ry="3" fill="#ef4444" />
              <circle cx="17" cy="6" r="5" fill="#22c55e" />
              <ellipse cx="6" cy="17" rx="5" ry="3" fill="#a855f7" />
              <polygon points="14,12 22,12 18,22" fill="#f59e0b" />
            </svg>
            """);

    [Fact]
    public void Strokes()
        => AssertRendersTheSame("Strokes", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <path d="M 2 20 L 8 6 L 14 18 L 22 4" fill="none" stroke="#0ea5e9"
                    stroke-width="3" stroke-linecap="round" stroke-linejoin="round" />
              <path d="M 2 3 H 22" fill="none" stroke="#111827" stroke-width="2" stroke-dasharray="4 2" />
            </svg>
            """);

    [Fact]
    public void Gradients()
        => AssertRendersTheSame("Gradient", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="24" y2="24">
                  <stop offset="0%" stop-color="#3b82f6" />
                  <stop offset="100%" stop-color="#f43f5e" />
                </linearGradient>
                <radialGradient id="r" gradientUnits="userSpaceOnUse" cx="12" cy="12" r="8">
                  <stop offset="0%" stop-color="#ffffff" stop-opacity="0.9" />
                  <stop offset="100%" stop-color="#000000" stop-opacity="0.1" />
                </radialGradient>
              </defs>
              <rect x="0" y="0" width="24" height="24" fill="url(#g)" />
              <circle cx="12" cy="12" r="8" fill="url(#r)" />
            </svg>
            """);

    [Fact]
    public void Clip_With_A_Transform()
        => AssertRendersTheSame("Clip", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <clipPath id="c" transform="rotate(20 12 12)">
                  <rect x="4" y="4" width="16" height="16" />
                </clipPath>
              </defs>
              <g clip-path="url(#c)">
                <rect x="0" y="0" width="24" height="24" fill="#7c3aed" />
                <circle cx="6" cy="6" r="6" fill="#facc15" />
              </g>
            </svg>
            """);

    [Fact]
    public void Opacity_And_Nested_Transforms()
        => AssertRendersTheSame("Layers", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <g opacity="0.55" transform="translate(2 2) scale(0.8)">
                <rect x="0" y="0" width="14" height="14" fill="#0f766e" />
                <g transform="rotate(30 7 7)">
                  <rect x="6" y="6" width="14" height="14" fill="#be123c" fill-opacity="0.6" />
                </g>
              </g>
            </svg>
            """);

    [Fact]
    public void SkiaSharp3_Renders_The_Same_As_SkiaSharp4()
        // The two differ only in how the path is assembled — SKPath directly, or a builder that
        // is detached. The drawing must not notice.
        => AssertRendersTheSame("Curves_V3", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <path d="M 2 12 C 2 6 6 2 12 2 A 10 10 0 0 1 22 12 L 12 22 Z" fill="#3b82f6" />
              <path d="M 2 2 H 22 V 22 H 2 Z M 7 7 H 17 V 17 H 7 Z" fill="#1e40af" fill-rule="evenodd" />
            </svg>
            """, SkiaSharpTarget.V3);
}
