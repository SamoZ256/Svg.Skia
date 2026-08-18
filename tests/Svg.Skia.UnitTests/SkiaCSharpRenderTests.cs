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
using Svg.Expressions;
using Svg.Model.Services;
using Svg.SceneGraph;
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
/// wrong picture. Most cases here have both paths start from the same <c>ShimPicture</c>, so a
/// difference can only come from the emitter.
/// <para>
/// The exception is the expression cases that pass an <c>expectedMarkup</c>: the renderer draws the
/// placeholder an expression leaves behind, so proving the expression's <em>value</em> arrives means
/// comparing against a second document that states it as a literal. Those two models differ, so a
/// difference there can also come from the renderer — which is worth knowing, because an
/// axis-aligned <c>rect</c> with edges inside the canvas does not rasterise identically through the
/// two paths (~850 antialiased edge pixels, RMS 0.0045, reproducible with no expressions at all and
/// covered by no case here). The expression cases avoid that shape rather than inherit the
/// discrepancy.
/// </para>
/// </remarks>
public class SkiaCSharpRenderTests
{
    private const string Namespace = "Svg.Skia.UnitTests.Rendered";

    private const int Size = 256;

    // Floats are emitted with the shortest round-trippable form, so the two paths replay the same
    // command list exactly. Any difference at all is a defect rather than tolerable noise.
    private const double Threshold = 0d;

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
    private static SKPicture Generated(
        ShimPicture model,
        string className,
        SkiaSharpTarget skiaSharp,
        SvgExpressionDeclarations declarations,
        object?[] arguments)
    {
        var code = SkiaCSharpCodeGen.Generate(
            model,
            Namespace,
            className,
            declarations,
            SvgPictureCache.None,
            skiaSharp);

        var assemblyName = $"{Namespace}.{className}.{++s_generation}";

        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { CSharpSyntaxTree.ParseText(SourceText.From(code)) },
            CSharpReferences.All,
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

        // A document with no declarations generates `private static SKPicture Record()`; one with
        // parameters generates a public one taking them.
        var record = type!.GetMethod("Record", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(record);

        var picture = record!.Invoke(null, arguments) as SKPicture;
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

        using var generated = Generated(model, name, skiaSharp, SvgExpressionDeclarations.Empty, Array.Empty<object?>());

        AssertSamePicture(name, runtime!, generated);
    }

    /// <summary>
    /// Renders generated code that carries expressions, and compares it against the runtime
    /// renderer — with <paramref name="arguments"/> for the parameters the document declares.
    /// </summary>
    /// <param name="expectedMarkup">
    /// The document the runtime side renders, when it differs from the one being generated. Needed
    /// because the renderer draws the <em>placeholder</em> state an expression leaves behind, not
    /// the expression's value: comparing a drawing against itself only shows that the two agree
    /// wherever the expression happens to evaluate to the placeholder. Passing a document that
    /// states the expected value as a literal is what proves the value reached the paint at all.
    /// </param>
    private static void AssertExpressionsRenderTheSame(
        string name,
        string svgMarkup,
        object?[]? arguments = null,
        string? expectedMarkup = null)
    {
        var model = Model(svgMarkup);

        using var runtime = new SkiaModel(new SKSvgSettings())
            .ToSKPicture(expectedMarkup is null ? model : Model(expectedMarkup));
        Assert.NotNull(runtime);

        var declarations = SvgExpressionDeclarations.Parse(svgMarkup);

        using var generated = Generated(
            model,
            name,
            SkiaSharpTarget.V4,
            declarations,
            arguments ?? Array.Empty<object?>());

        AssertSamePicture(name, runtime!, generated);

        // The evaluated leg: the runtime path resolving the same expressions against the same
        // values, which has to produce what the generated code produces. This is what the plain
        // comparison above cannot show, because the renderer there is drawing the placeholder.
        var evaluated = SvgSceneExpressionEvaluator.Evaluate(
            model,
            declarations,
            Bind(declarations, arguments));

        using var evaluatedPicture = new SkiaModel(new SKSvgSettings()).ToSKPicture(evaluated);
        Assert.NotNull(evaluatedPicture);

        AssertSamePicture($"{name}-evaluated", evaluatedPicture!, generated);
    }

    /// <summary>
    /// The arguments the generated <c>Record</c> is invoked with, as the value map the evaluator
    /// takes. Positional, because the generated signature lists the parameters in declaration order.
    /// </summary>
    private static Dictionary<string, ExprValue> Bind(
        SvgExpressionDeclarations declarations,
        object?[]? arguments)
    {
        var values = new Dictionary<string, ExprValue>(StringComparer.Ordinal);

        if (arguments is null)
        {
            return values;
        }

        Assert.Equal(declarations.Parameters.Count, arguments.Length);

        for (var index = 0; index < arguments.Length; index++)
        {
            // A null argument is a colour left to its default: the generated parameter is nullable
            // and coalesces, so leaving the name unbound here is the same thing on the other side —
            // the evaluator falls back to the declared default too.
            if (arguments[index] is null)
            {
                continue;
            }

            values[declarations.Parameters[index].Name] = arguments[index] switch
            {
                float number => ExprValue.Number(number),
                bool boolean => ExprValue.Boolean(boolean),
                SKColor color => ExprValue.Color(color.Red, color.Green, color.Blue, color.Alpha),
                var other => throw new NotSupportedException($"Unsupported argument type: {other!.GetType().Name}.")
            };
        }

        return values;
    }

    private static void AssertSamePicture(string name, SKPicture runtime, SKPicture generated)
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var expected = Draw(runtime, Path.Combine(directory, $"{name}-runtime.png"));
            var actual = Draw(generated, Path.Combine(directory, $"{name}-generated.png"));

            ImageHelper.CompareImages(name, actual, expected, Threshold);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
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

    // ---------------------------------------------------------------------------------------------
    // Expressions. Until these, nothing anywhere drew generated expression code: every case above
    // passes SvgExpressionDeclarations.Empty and calls a parameterless Record, so the whole language
    // could emit a wrong value and the suite would stay green.
    //
    // Two shapes, and both are needed. Most cases below choose values that land exactly on the
    // placeholder the renderer draws (#808080 for a paint, 1 for an opacity, visible), so the
    // comparison is against the same document at a zero threshold. On their own those would also
    // pass if the emitter ignored expressions entirely and emitted the placeholder — so the ones
    // taking `expectedMarkup` state the expected value as a literal in a second document, and are
    // what prove the computed value reaches the paint.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void An_Expression_Value_Reaches_The_Paint()
        // The load-bearing one: the placeholder is grey, so red can only come from the expression.
        //
        // A circle rather than a rect. An axis-aligned rect whose edges fall inside the canvas
        // rasterises differently through the two paths — ~850 antialiased edge pixels, an RMS error
        // of 0.0045 — which has nothing to do with expressions: it reproduces with no declarations
        // at all, and no case in this file covers that shape. Using one here would test the wrong
        // thing.
        => AssertExpressionsRenderTheSame(
            "ExprValue",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="12" cy="12" r="10" fill="{{ rgb(255, 0, 0) }}" />
            </svg>
            """,
            expectedMarkup: """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="12" cy="12" r="10" fill="#ff0000" />
            </svg>
            """);

    [Fact]
    public void A_Conditional_Expression_Value_Reaches_The_Paint()
        => AssertExpressionsRenderTheSame(
            "ExprTernary",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="hot" type="boolean" default="false" /></e:code></defs>
              <circle cx="12" cy="12" r="9" fill="{{ hot ? #22c55e : #1e40af }}" />
            </svg>
            """,
            new object?[] { true },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="12" cy="12" r="9" fill="#22c55e" />
            </svg>
            """);

    [Fact]
    public void A_Colour_Parameter_Is_Passed_Through()
        // The placeholder value, handed in as an argument: the drawing must be identical.
        => AssertExpressionsRenderTheSame(
            "ExprColourParam",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="tint" type="color" /></e:code></defs>
              <rect x="3" y="3" width="18" height="18" rx="4" fill="{{ tint }}" />
            </svg>
            """,
            new object?[] { new SKColor(128, 128, 128, 255) });

    [Fact]
    public void A_Let_Sees_The_Parameters_Declared_Above_It()
        // The symbol table is handed to the checker and then grown, so a let reaching a parameter
        // is the case that breaks if anything copies it.
        => AssertExpressionsRenderTheSame(
            "ExprLet",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <e:code>
                  <e:param name="tint" type="color" />
                  <e:let name="solid">withAlpha(tint, 1)</e:let>
                  <e:let name="same">mix(solid, solid, 0.5)</e:let>
                </e:code>
              </defs>
              <rect x="0" y="0" width="24" height="24" fill="{{ same }}" />
            </svg>
            """,
            new object?[] { new SKColor(128, 128, 128, 255) });

    [Fact]
    public void Arithmetic_And_The_Constants_Drive_An_Opacity()
        // tau / (pi * 2) is exactly 1, which is the opacity placeholder, so the two agree only if
        // the constants and the division are emitted correctly.
        => AssertExpressionsRenderTheSame(
            "ExprConstants",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <g opacity="{{ tau / (pi * 2) }}">
                <rect x="2" y="2" width="20" height="20" fill="#0f766e" />
              </g>
            </svg>
            """);

    [Fact]
    public void Mod_Is_A_Remainder_Rather_Than_IEEERemainder()
        // mod(5, 3) is 2 under `%` and -1 under Math.IEEERemainder, which is what the language's
        // table used to name. Halved it is the opacity placeholder; the other reading is not.
        => AssertExpressionsRenderTheSame(
            "ExprMod",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <g opacity="{{ mod(5, 3) / 2 }}">
                <rect x="2" y="2" width="20" height="20" fill="#7c3aed" />
              </g>
            </svg>
            """);

    [Fact]
    public void An_Opacity_Expression_Value_Reaches_The_Layer()
        => AssertExpressionsRenderTheSame(
            "ExprOpacity",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="fade" type="number" default="1" /></e:code></defs>
              <g opacity="{{ fade }}">
                <rect x="2" y="2" width="20" height="20" fill="#be123c" />
              </g>
            </svg>
            """,
            new object?[] { 0.5f },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <g opacity="0.5">
                <rect x="2" y="2" width="20" height="20" fill="#be123c" />
              </g>
            </svg>
            """);

    [Fact]
    public void A_False_Visibility_Expression_Draws_Nothing()
        // The conditional becomes `if (shown)`, so the subtree has to disappear from the drawing
        // rather than merely be painted differently.
        => AssertExpressionsRenderTheSame(
            "ExprHidden",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="shown" type="boolean" default="true" /></e:code></defs>
              <rect x="0" y="0" width="24" height="24" fill="#facc15" />
              <circle cx="12" cy="12" r="8" fill="#111827" visibility="{{ shown }}" />
            </svg>
            """,
            new object?[] { false },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect x="0" y="0" width="24" height="24" fill="#facc15" />
            </svg>
            """);

    [Fact]
    public void A_True_Visibility_Expression_Draws_The_Subtree()
        => AssertExpressionsRenderTheSame(
            "ExprShown",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <rect x="0" y="0" width="24" height="24" fill="#facc15" />
              <circle cx="12" cy="12" r="8" fill="#111827" visibility="{{ 2 gt 1 and !false }}" />
            </svg>
            """);

    [Fact]
    public void A_Gradient_Stop_Takes_An_Expression()
        // Stops reach the model as SKColorF, so this is the one path that emits SvgToColorF.
        => AssertExpressionsRenderTheSame(
            "ExprStop",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="24" y2="0">
                  <stop offset="0%" stop-color="{{ rgb(255, 0, 0) }}" />
                  <stop offset="100%" stop-color="#1e40af" />
                </linearGradient>
              </defs>
              <rect x="0" y="0" width="24" height="24" fill="url(#g)" />
            </svg>
            """,
            expectedMarkup: """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="24" y2="0">
                  <stop offset="0%" stop-color="#ff0000" />
                  <stop offset="100%" stop-color="#1e40af" />
                </linearGradient>
              </defs>
              <rect x="0" y="0" width="24" height="24" fill="url(#g)" />
            </svg>
            """);

    [Fact]
    public void An_Omitted_Colour_Argument_Falls_Back_To_Its_Declared_Default()
        // A colour cannot be a C# argument default, so the generated parameter is nullable and
        // coalesces to the compiled default. Passing null here is what a caller omitting the
        // argument gets, and the expected document states the default as a literal — so this fails
        // if the fallback is ever dropped, or resolves to something other than what was declared.
        => AssertExpressionsRenderTheSame(
            "ExprColourDefault",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="tint" type="color" default="rgb(255, 0, 0)" /></e:code></defs>
              <circle cx="12" cy="12" r="10" fill="{{ tint }}" />
            </svg>
            """,
            arguments: new object?[] { null },
            expectedMarkup: """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="12" cy="12" r="10" fill="#ff0000" />
            </svg>
            """);

    [Fact]
    public void A_Supplied_Colour_Argument_Wins_Over_The_Default()
        => AssertExpressionsRenderTheSame(
            "ExprColourDefaultOverridden",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="tint" type="color" default="rgb(255, 0, 0)" /></e:code></defs>
              <circle cx="12" cy="12" r="10" fill="{{ tint }}" />
            </svg>
            """,
            arguments: new object?[] { new SKColor(0x1e, 0x40, 0xaf, 0xff) },
            expectedMarkup: """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="12" cy="12" r="10" fill="#1e40af" />
            </svg>
            """);

    [Fact]
    public void A_False_Conditional_Around_A_Transform_Leaves_Later_Geometry_Where_It_Was()
        // A hidden group carrying a transform, with an untransformed sibling after it that must not
        // move. This does not distinguish keeping a suppressed range's state commands from deleting
        // the range — measured, not assumed: the recorder emits Begin, Save, SetMatrix, DrawPath,
        // Restore, End, so the range's own Restore pops the matrix either way. What it does cover is
        // that the conditional resolves at all with state commands in the range, and that the
        // evaluated picture matches generated code through a transform.
        // The keep-versus-delete difference is pinned by ConditionalRangeTests instead.
        => AssertExpressionsRenderTheSame(
            "ExprHiddenTransform",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="shown" type="boolean" default="true" /></e:code></defs>
              <g transform="translate(0 -8)" visibility="{{ shown }}">
                <circle cx="12" cy="12" r="5" fill="#ff0000" />
              </g>
              <circle cx="12" cy="16" r="5" fill="#1e40af" />
            </svg>
            """,
            arguments: new object?[] { false },
            expectedMarkup: """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="12" cy="16" r="5" fill="#1e40af" />
            </svg>
            """);

    [Fact]
    public void A_False_Conditional_Around_A_Clip_Does_Not_Clip_What_Follows()
        // The same question for clips rather than matrices. A clip inside the range is scoped by the
        // range's own save, so keeping it must not narrow the blue circle that comes after.
        => AssertExpressionsRenderTheSame(
            "ExprHiddenClip",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <e:code><e:param name="shown" type="boolean" default="true" /></e:code>
                <clipPath id="half"><rect x="0" y="0" width="24" height="6" /></clipPath>
              </defs>
              <g clip-path="url(#half)" visibility="{{ shown }}">
                <circle cx="12" cy="6" r="5" fill="#ff0000" />
              </g>
              <circle cx="12" cy="16" r="7" fill="#1e40af" />
            </svg>
            """,
            arguments: new object?[] { false },
            expectedMarkup: """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="12" cy="16" r="7" fill="#1e40af" />
            </svg>
            """);

    [Fact]
    public void A_True_Conditional_Around_A_Transform_Still_Transforms()
        // The other half: keeping a range must not lose its state either, so the red circle has to
        // land where its group's translate puts it.
        => AssertExpressionsRenderTheSame(
            "ExprShownTransform",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="shown" type="boolean" default="false" /></e:code></defs>
              <g transform="translate(0 -8)" visibility="{{ shown }}">
                <circle cx="12" cy="12" r="5" fill="#ff0000" />
              </g>
              <circle cx="12" cy="16" r="5" fill="#1e40af" />
            </svg>
            """,
            arguments: new object?[] { true },
            expectedMarkup: """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <g transform="translate(0 -8)">
                <circle cx="12" cy="12" r="5" fill="#ff0000" />
              </g>
              <circle cx="12" cy="16" r="5" fill="#1e40af" />
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
