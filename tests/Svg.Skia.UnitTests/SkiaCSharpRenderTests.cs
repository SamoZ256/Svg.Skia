using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using ShimSkiaSharp.Editing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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
/// Nothing else ever draws generated code, so an emitter change can be green across the whole suite
/// and still produce a wrong picture. Most cases start both paths from the same <c>ShimPicture</c>,
/// so a difference can only be the emitter's; the <c>expectedMarkup</c> cases compare two different
/// models.
/// </remarks>
public class SkiaCSharpRenderTests
{
    private const string Namespace = "Svg.Skia.UnitTests.Rendered";

    private const int Size = 256;

    // Floats are emitted with the shortest round-trippable form, so the two paths replay the same
    // command list exactly. Any difference at all is a defect rather than tolerable noise.
    private const double Threshold = 0d;

    // What the generator ships adds each translation to the coordinates of the shape it moves, which
    // the interpreter does not: antialiased edges move by a few levels. TapHome's icons, at 1x to 3x
    // and whole and half pixel offsets, measure at most 0.0138; a shape drawn out of place, 0.13.
    private const double BakedThreshold = 0.02d;

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

    /// <summary>The model a build compiles: the declared defaults in the document while it is measured.</summary>
    /// <remarks>
    /// A value a compile consumes -- the text itself -- has to be in the document before it is
    /// measured, and what a build has to put there is the default. Without this the element draws
    /// nothing at all, which is what a document says while an expression has not been resolved.
    /// </remarks>
    private static ShimPicture DrivenModel(string svgMarkup)
    {
        var document = SvgService.FromSvg(svgMarkup);
        Assert.NotNull(document);

        using var substitution = SvgExpressionSubstitution.Begin(
            document!,
            ExprEvaluator.Create(document!.ExpressionDeclarations, null));

        var picture = SvgSceneRuntime.CreateModel(document!, new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings())));
        Assert.NotNull(picture);

        return picture!;
    }

    /// <summary>Compiles the emitted C# and calls its Record, exactly as a consumer would.</summary>
    private static SKPicture Generated(
        ShimPicture model,
        string className,
        SkiaSharpTarget skiaSharp,
        SvgExpressionDeclarations declarations,
        object?[] arguments,
        IReadOnlyList<(string Name, ShimSkiaSharp.SKRect Rect)>? bounds = null,
        bool bake = true)
    {
        var code = SkiaCSharpCodeGen.GenerateFile(
            new[] { new SkiaCSharpDrawing(model, Namespace, className, declarations, bounds: bounds) },
            SvgHelperScope.PerClass,
            skiaSharp: skiaSharp,
            style: new SvgCodeStyle { Bake = bake });

        var assembly = Compile(code, className);
        var type = assembly.GetType($"{Namespace}.{className}");
        Assert.NotNull(type);

        foreach (var (name, rect) in bounds ?? Array.Empty<(string, ShimSkiaSharp.SKRect)>())
        {
            Assert.Equal(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom), type!.GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null));
        }

        var record = type!.GetMethod("Record", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(record);

        var picture = record!.Invoke(null, arguments) as SKPicture;
        Assert.NotNull(picture);

        // Draw disposes the picture it recorded as soon as it has drawn it, which no other row runs.
        using var recorder = new SKPictureRecorder();
        type.GetMethod("Draw")!.Invoke(null, new object?[] { recorder.BeginRecording(picture!.CullRect) }.Concat(arguments).ToArray());
        using var drawn = recorder.EndRecording();
        AssertSamePicture($"{className}-draw", picture, drawn);

        return picture;
    }

    private static Assembly Compile(string code, string name, params string[] alongside)
    {
        var assemblyName = $"{Namespace}.{name}.{++s_generation}";

        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { code }.Concat(alongside).Select(text => CSharpSyntaxTree.ParseText(SourceText.From(text))),
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

        return new AssemblyLoadContext(assemblyName, isCollectible: false).LoadFromStream(peStream);
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

        using var exact = Generated(model, name, skiaSharp, SvgExpressionDeclarations.Empty, Array.Empty<object?>(), bake: false);
        AssertSamePicture(name, runtime!, exact);

        using var generated = Generated(model, name, skiaSharp, SvgExpressionDeclarations.Empty, Array.Empty<object?>());
        AssertSamePicture($"{name}-baked", runtime!, generated, BakedThreshold);

        AssertDrawsDirectlyTheSame(name, model, runtime!, SvgExpressionDeclarations.Empty, Array.Empty<object?>(), skiaSharp);
    }

    /// <summary>
    /// The same drawing drawn directly, unbaked and as shipped: recorded through its Record, and drawn
    /// by its Draw onto a canvas that already holds a transform of its own, which every matrix it sets
    /// is relative to.
    /// </summary>
    private static void AssertDrawsDirectlyTheSame(
        string name,
        ShimPicture model,
        SKPicture runtime,
        SvgExpressionDeclarations declarations,
        object?[] arguments,
        SkiaSharpTarget skiaSharp = SkiaSharpTarget.V4,
        string? typeface = null)
    {
        foreach (var bake in new[] { false, true })
        {
            var code = SkiaCSharpCodeGen.GenerateFile(
                new[] { new SkiaCSharpDrawing(model, Namespace, name, declarations) },
                SvgHelperScope.PerClass,
                skiaSharp: skiaSharp,
                style: new SvgCodeStyle { Body = SvgBody.Direct, Typeface = typeface, Bake = bake });

            var type = Compile(code, name).GetType($"{Namespace}.{name}")!;
            var label = bake ? $"{name}-direct-baked" : $"{name}-direct";

            using var recorded = (SKPicture)type.GetMethod("Record", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, arguments)!;
            AssertSamePicture(label, runtime, recorded, bake ? BakedThreshold : Threshold);

            var cull = runtime.CullRect;
            var under = new SKRect(0, 0, cull.Width * 1.5f + 6, cull.Height * 1.5f + 10);

            // Against its own recording rather than the runtime's: under a fractional scale those two
            // already differ along antialiased edges in record mode too (Gradients, RMS 0.0023).
            using var expected = Transformed(under, canvas => canvas.DrawPicture(recorded));
            using var actual = Transformed(under, canvas => type.GetMethod("Draw")!.Invoke(null, new object?[] { canvas }.Concat(arguments).ToArray()));

            AssertSamePicture($"{label}-transformed", expected, actual);
        }
    }

    private static string Code(ShimPicture model, string name, SvgBody body, SvgExpressionDeclarations? declarations = null)
        => SkiaCSharpCodeGen.GenerateFile(
            new[] { new SkiaCSharpDrawing(model, Namespace, name, declarations ?? SvgExpressionDeclarations.Empty) },
            SvgHelperScope.PerClass,
            style: new SvgCodeStyle { Body = body });

    private static SKPicture Transformed(SKRect cull, Action<SKCanvas> draw)
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(cull);
        canvas.Translate(6, 10);
        canvas.Scale(1.5f);
        draw(canvas);

        return recorder.EndRecording();
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

        using var exact = Generated(model, name, SkiaSharpTarget.V4, declarations, arguments ?? Array.Empty<object?>(), bake: false);
        AssertSamePicture(name, runtime!, exact);

        using var generated = Generated(model, name, SkiaSharpTarget.V4, declarations, arguments ?? Array.Empty<object?>());
        AssertSamePicture($"{name}-baked", runtime!, generated, BakedThreshold);

        AssertDrawsDirectlyTheSame(name, model, runtime!, declarations, arguments ?? Array.Empty<object?>());

        // The runtime path resolving the same expressions, which the comparison above cannot show
        // because the renderer there draws the placeholder.
        var evaluated = SvgSceneExpressionEvaluator.Evaluate(
            model,
            declarations,
            Bind(declarations, arguments));

        using var evaluatedPicture = new SkiaModel(new SKSvgSettings()).ToSKPicture(evaluated);
        Assert.NotNull(evaluatedPicture);

        AssertSamePicture($"{name}-evaluated", evaluatedPicture!, exact);
        AssertSamePicture($"{name}-evaluated-baked", evaluatedPicture!, generated, BakedThreshold);
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
            // A null argument is a parameter left to its default -- a colour or a string, the two
            // that reach the body through a local -- which leaving the name unbound is on the
            // evaluator's side.
            if (arguments[index] is null)
            {
                continue;
            }

            values[declarations.Parameters[index].Name] = arguments[index] switch
            {
                float number => ExprValue.Number(number),
                int whole => ExprValue.Integer(whole),
                bool boolean => ExprValue.Boolean(boolean),
                SKColor color => ExprValue.Color(color.Red, color.Green, color.Blue, color.Alpha),
                string text => ExprValue.String(text),
                var other => throw new NotSupportedException($"Unsupported argument type: {other!.GetType().Name}.")
            };
        }

        return values;
    }

    private static void AssertSamePicture(string name, SKPicture runtime, SKPicture generated, double threshold = Threshold)
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var expected = Draw(runtime, Path.Combine(directory, $"{name}-runtime.png"));
            var actual = Draw(generated, Path.Combine(directory, $"{name}-generated.png"));

            ImageHelper.CompareImages(name, actual, expected, threshold);
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

    /// <summary>
    /// The one thing about driven text a string assertion cannot show: the argument reaches the
    /// picture, rather than reaching the source and being drawn over by what was recorded.
    /// </summary>
    /// <remarks>
    /// Two arguments and a difference, rather than a comparison against the runtime renderer at the
    /// usual zero threshold. The renderer draws text through a shaped blob it builds itself, and
    /// generated code hands the string to SkiaSharp; measured on this machine the two agree to
    /// about 0.6% of a 256px render on the same string, which is a gap that predates this and has
    /// nothing to do with which string is drawn. Comparing generated against generated is the
    /// question this test is actually asking.
    /// </remarks>
    [Fact]
    public void A_Driven_Text_Draws_The_Argument_It_Is_Given()
    {
        const string Markup = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0"
                 viewBox="0 0 64 24" width="64" height="24">
              <defs><e:code><e:param name="step" type="string" default="'1'" /></e:code></defs>
              <text x="4" y="18" font-family="Arial" font-size="12" fill="#111827">{{ step }}</text>
            </svg>
            """;

        var model = DrivenModel(Markup);
        var declarations = SvgExpressionDeclarations.Parse(Markup);

        using var one = Generated(model, "DrivenTextOne", SkiaSharpTarget.V4, declarations, new object?[] { "1" });
        using var again = Generated(model, "DrivenTextAgain", SkiaSharpTarget.V4, declarations, new object?[] { "1" });
        using var other = Generated(model, "DrivenTextOther", SkiaSharpTarget.V4, declarations, new object?[] { "8888" });

        // The same argument twice is the same picture, so the difference below is the argument's.
        AssertSamePicture("DrivenText", one, again);

        var directory = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var drawn = Image.Load<Rgba32>(Draw(one, Path.Combine(directory, "one.png")));
            using var different = Image.Load<Rgba32>(Draw(other, Path.Combine(directory, "other.png")));

            Assert.True(ImageHelper.CompareImages(different, drawn) > 0d, "The argument did not reach the drawing.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A box the drawing reserves for its host is a constant beside the drawing, never in it.</summary>
    [Fact]
    public void A_Box_For_The_Host_Leaves_The_Drawing_As_It_Was()
    {
        var model = Model("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 30 30" width="30" height="30">
              <circle cx="15" cy="15" r="13" fill="none" stroke="#3b82f6" stroke-width="1.5" />
            </svg>
            """);

        using var runtime = new SkiaModel(new SKSvgSettings()).ToSKPicture(model);
        Assert.NotNull(runtime);

        using var generated = Generated(
            model,
            "Boxed",
            SkiaSharpTarget.V4,
            SvgExpressionDeclarations.Empty,
            Array.Empty<object?>(),
            new[]
            {
                ("LevelRect", new ShimSkiaSharp.SKRect(2f, 6f, 28f, 24f)),
                ("ValueRect", new ShimSkiaSharp.SKRect(20f, 67.36f, 160f, 113.89f)),

                // A keyword is a name the class can still declare.
                ("default", new ShimSkiaSharp.SKRect(0f, 0f, 1f, 1f))
            },
            bake: false);

        AssertSamePicture("Boxed", runtime!, generated);
    }

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

    // Drawn as a path, a rect's top and bottom edges rasterise apart from the DrawRect the renderer
    // uses (symbol-widget-small, RMS 0.0165); a rect with corners square is what shows it.
    [Fact]
    public void A_Lone_Filled_Primitive_Is_Drawn_As_The_Renderer_Draws_It()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect x="2.3" y="3.7" width="8.4" height="5.1" fill="#ef4444" />
              <rect x="13.2" y="1.6" width="8.3" height="7.9" rx="2" fill="#0ea5e9" />
              <circle cx="6.4" cy="16.6" r="4.3" fill="#22c55e" />
              <ellipse cx="17.3" cy="17.1" rx="5.1" ry="3.3" fill="#a855f7" />
            </svg>
            """;

        AssertRendersTheSame("LonePrimitives", svg);

        var code = Code(Model(svg), "LonePrimitives", SvgBody.Direct);
        Assert.Contains(".DrawRect(new SKRect(", code);
        Assert.Contains(".DrawRoundRect(new SKRect(", code);
        Assert.Contains(".DrawOval(new SKRect(", code);
        Assert.Contains(".DrawCircle(", code);
        Assert.DoesNotContain(".DrawPath(", code);
        Assert.DoesNotContain("SKPath", code);
    }

    [Fact]
    public void A_Stroked_Layered_Or_Longer_Path_Is_Still_Drawn_As_A_Path()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect x="2.3" y="3.7" width="8.4" height="5.1" fill="none" stroke="#ef4444" />
              <g opacity="0.5">
                <rect x="13.2" y="1.6" width="8.3" height="7.9" fill="#0ea5e9" />
                <rect x="14.2" y="2.6" width="2.3" height="2.9" fill="#111827" />
              </g>
              <g style="isolation: isolate">
                <rect x="2.2" y="13.6" width="8.3" height="7.9" fill="#22c55e" />
              </g>
              <path d="M 13.3 13.7 H 21.6 V 21.2 H 13.3 Z" fill="#a855f7" />
            </svg>
            """;

        AssertRendersTheSame("PathPrimitives", svg);

        var code = Code(Model(svg), "PathPrimitives", SvgBody.Direct);
        Assert.DoesNotContain(".DrawRect(", code);
        Assert.Equal(5, code.Split(".DrawPath(").Length - 1);
    }

    // The second conversion of a model draws a nested picture from a recording made with no layer
    // open, which drew this rect with DrawRect where the first, replaying it, drew a path.
    [Fact]
    public void A_Picture_Inside_A_Layer_Draws_The_Same_On_Every_Conversion()
    {
        var nested = new ShimSkiaSharp.SKPictureRecorder();
        var nestedCanvas = nested.BeginRecording(ShimSkiaSharp.SKRect.Create(0, 0, 24, 24));
        var rect = new ShimSkiaSharp.SKPath();
        rect.AddRect(ShimSkiaSharp.SKRect.Create(2.3f, 3.7f, 8.4f, 5.1f));
        nestedCanvas.DrawPath(rect, new ShimSkiaSharp.SKPaint { Style = ShimSkiaSharp.SKPaintStyle.Fill, IsAntialias = true, Color = new ShimSkiaSharp.SKColor(0xef, 0x44, 0x44, 0xff) });

        var recorder = new ShimSkiaSharp.SKPictureRecorder();
        var canvas = recorder.BeginRecording(ShimSkiaSharp.SKRect.Create(0, 0, 24, 24));
        canvas.SaveLayer(new ShimSkiaSharp.SKPaint { Color = new ShimSkiaSharp.SKColor(0, 0, 0, 128) });
        canvas.DrawPicture(nested.EndRecording());
        canvas.Restore();
        var model = recorder.EndRecording();

        var skiaModel = new SkiaModel(new SKSvgSettings());
        using var first = skiaModel.ToSKPicture(model);
        using var second = skiaModel.ToSKPicture(model);
        AssertSamePicture("NestedInLayer-again", first!, second!);

        using var generated = Generated(model, "NestedInLayer", SkiaSharpTarget.V4, SvgExpressionDeclarations.Empty, Array.Empty<object?>());
        AssertSamePicture("NestedInLayer", second!, generated);
    }

    [Theory]
    [InlineData(SkiaSharpTarget.V3)]
    [InlineData(SkiaSharpTarget.V4)]
    public void A_Path_Built_From_Data_Draws_As_Its_Calls(SkiaSharpTarget skiaSharp)
    {
        // Every verb the data carries, arcs of both sizes either way round, a polygon and a polyline,
        // which it spells as a move and lines, runs a rect and an oval cut in two, and a run too
        // short to be worth an array.
        var path = new ShimSkiaSharp.SKPath { FillType = ShimSkiaSharp.SKPathFillType.EvenOdd };
        path.MoveTo(2f, 2f);
        path.LineTo(20f, 2.5f);
        path.QuadTo(22.25f, 6f, 18f, 8f);
        path.CubicTo(14f, 10.5f, 6f, 4f, 2f, 8f);
        path.Close();
        path.AddRect(ShimSkiaSharp.SKRect.Create(5f, 3f, 6f, 4f));
        path.MoveTo(4f, 12f);
        path.ArcTo(4f, 3f, 15f, ShimSkiaSharp.SKPathArcSize.Large, ShimSkiaSharp.SKPathDirection.CounterClockwise, 12f, 14f);
        path.ArcTo(4f, 3f, 0f, ShimSkiaSharp.SKPathArcSize.Small, ShimSkiaSharp.SKPathDirection.Clockwise, 6f, 16f);
        path.ArcTo(5f, 5f, 0f, ShimSkiaSharp.SKPathArcSize.Large, ShimSkiaSharp.SKPathDirection.Clockwise, 4f, 13f);
        path.ArcTo(2f, 2f, 0f, ShimSkiaSharp.SKPathArcSize.Small, ShimSkiaSharp.SKPathDirection.CounterClockwise, 4f, 12f);
        path.Close();
        path.AddOval(ShimSkiaSharp.SKRect.Create(14f, 12f, 6f, 4f));
        path.AddPoly(new[] { new ShimSkiaSharp.SKPoint(14f, 18f), new ShimSkiaSharp.SKPoint(22f, 18f), new ShimSkiaSharp.SKPoint(18f, 23f) }, close: true);
        path.AddPoly(new[] { new ShimSkiaSharp.SKPoint(2f, 18f), new ShimSkiaSharp.SKPoint(6f, 22f), new ShimSkiaSharp.SKPoint(10f, 18f), new ShimSkiaSharp.SKPoint(12f, 23f) }, close: false);
        path.AddCircle(9f, 9f, 1.5f);
        path.MoveTo(1f, 23f);
        path.LineTo(3f, 21f);

        var recorder = new ShimSkiaSharp.SKPictureRecorder();
        var canvas = recorder.BeginRecording(ShimSkiaSharp.SKRect.Create(0, 0, 24, 24));
        canvas.DrawPath(path, new ShimSkiaSharp.SKPaint { IsAntialias = true, Color = new ShimSkiaSharp.SKColor(0x1e, 0x40, 0xaf, 0xff) });
        canvas.DrawPath(path, new ShimSkiaSharp.SKPaint { Style = ShimSkiaSharp.SKPaintStyle.Stroke, StrokeWidth = 0.75f, IsAntialias = true, Color = new ShimSkiaSharp.SKColor(0xdc, 0x26, 0x26, 0xff) });
        var model = recorder.EndRecording();
        var name = $"PathData_{skiaSharp}";

        using var runtime = new SkiaModel(new SKSvgSettings()).ToSKPicture(model);
        using var generated = Generated(model, name, skiaSharp, SvgExpressionDeclarations.Empty, Array.Empty<object?>());
        AssertSamePicture(name, runtime!, generated);
        AssertDrawsDirectlyTheSame(name, model, runtime!, SvgExpressionDeclarations.Empty, Array.Empty<object?>(), skiaSharp);

        var code = SkiaCSharpCodeGen.Generate(model, Namespace, name, SvgExpressionDeclarations.Empty, skiaSharp);
        Assert.Contains(skiaSharp == SkiaSharpTarget.V3 ? "SvgPathData(" : "SvgPathBuilderData(", code);
        Assert.Contains(".AddRect(", code);
        Assert.Contains(".AddOval(", code);
        Assert.Contains(".LineTo(3f, 21f);", code);
        Assert.DoesNotContain(".CubicTo(14f", code);
    }

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

    [Theory]
    [InlineData(SkiaSharpTarget.V3)]
    [InlineData(SkiaSharpTarget.V4)]
    public void Text_Is_Replayed_Where_It_Is_Drawn(SkiaSharpTarget skiaSharp)
        // The scene wraps every text element in a picture of its own: under a translation and a
        // scale, under a clip that cuts it and in a group's layer. Not turned: a rotated run already
        // differs from the runtime's by RMS 0.018 on master, recorded or not.
        => AssertTextRendersTheSame($"InlineText_{skiaSharp}", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48" width="48" height="48">
              <defs>
                <clipPath id="c" clipPathUnits="userSpaceOnUse"><rect x="0" y="30" width="30" height="7" /></clipPath>
              </defs>
              <g transform="translate(2.5 3) scale(1.25)">
                <text x="2" y="10" font-family="Noto Sans" font-size="9" fill="#1e40af">Ab7</text>
              </g>
              <g clip-path="url(#c)">
                <text x="2" y="38" font-family="Noto Sans" font-size="10" fill="#16a34a">clip</text>
              </g>
              <g opacity="0.5">
                <text x="26" y="44" font-family="Noto Sans" font-size="8" fill="#111827">half</text>
              </g>
            </svg>
            """, skiaSharp);

    [Fact]
    public void Two_Runs_In_One_Font_Read_One_Static()
    {
        var code = AssertTextRendersTheSame("OneFont", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 24" width="48" height="24">
              <text x="2" y="10" font-family="Noto Sans" font-size="9" fill="#1e40af">Ab7</text>
              <text x="2" y="21" font-family="Noto Sans" font-size="9" fill="#dc2626">cd</text>
            </svg>
            """);

        Assert.Single(Regex.Matches(code, @"new SKFont\("));
        Assert.DoesNotContain("skFont0?.Dispose()", code);
    }

    [Fact]
    public void A_Driven_Text_Paint_Is_The_Shared_One_And_Leaves_It_Reset()
    {
        // The strokes either side hold the same settings: the text's replay resets the shared paint, so
        // the second has to set them again or it fills.
        var code = AssertTextRendersTheSame("SharedTextPaint", """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 48 24" width="48" height="24">
              <defs><e:code><e:param name="tint" type="color" default="#7c3aed" /></e:code></defs>
              <path d="M 2 3 L 40 3 L 40 6" fill="none" stroke="{{ tint }}" stroke-width="2" />
              <text x="2" y="15" font-family="Noto Sans" font-size="9" fill="{{ tint }}">Ab7</text>
              <path d="M 2 20 L 40 20 L 40 23" fill="none" stroke="{{ tint }}" stroke-width="2" />
            </svg>
            """, arguments: new object?[] { new SKColor(0x7c, 0x3a, 0xed) }, expectedMarkup: """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 24" width="48" height="24">
              <path d="M 2 3 L 40 3 L 40 6" fill="none" stroke="#7c3aed" stroke-width="2" />
              <text x="2" y="15" font-family="Noto Sans" font-size="9" fill="#7c3aed">Ab7</text>
              <path d="M 2 20 L 40 20 L 40 23" fill="none" stroke="#7c3aed" stroke-width="2" />
            </svg>
            """);

        Assert.Single(Regex.Matches(code, @"new SKPaint\(\)"));
    }

    [Fact]
    public void A_Direct_Draw_Of_Driven_Text_Allocates_Only_What_DrawText_Does()
    {
        // DrawText(string) builds a text blob on every call, which is SkiaSharp's; everything else a
        // run needs, its picture, paint and font, is the drawing's and allocates nothing.
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 48 24" width="48" height="24">
              <defs><e:code><e:param name="tint" type="color" default="#1e40af" /></e:code></defs>
              <g transform="translate(2 3)">
                <text x="0" y="12" font-family="Noto Sans" font-size="9" fill="{{ tint }}">Ab7</text>
                <text x="0" y="20" font-family="Noto Sans" font-size="6" fill="#111827">cd</text>
              </g>
            </svg>
            """;
        var settings = new SKSvgSettings();
        settings.TypefaceProviders!.Insert(0, new Svg.Skia.TypefaceProviders.CustomTypefaceProvider(s_noto));
        var model = SvgSceneRuntime.CreateModel(SvgService.FromSvg(svg)!, new SkiaSvgAssetLoader(new SkiaModel(settings)))!;
        var code = SkiaCSharpCodeGen.GenerateFile(
            new[] { new SkiaCSharpDrawing(model, Namespace, "UnallocatedText", SvgExpressionDeclarations.Parse(svg)) },
            SvgHelperScope.PerClass,
            style: new SvgCodeStyle { Body = SvgBody.Direct, Typeface = $"SKTypeface.FromFile(@\"{s_noto}\")" });
        var draw = Compile(code, "UnallocatedText").GetType($"{Namespace}.UnallocatedText")!
            .GetMethod("Draw")!
            .CreateDelegate<Action<SKCanvas, SKColor?>>();

        using var bitmap = new SKBitmap(48, 24);
        using var canvas = new SKCanvas(bitmap);
        draw(canvas, new SKColor(0x1e, 0x40, 0xaf));

        using var font = new SKFont(SKTypeface.FromFile(s_noto), 9f);
        using var paint = new SKPaint();
        canvas.DrawText("Ab7", 0f, 12f, SKTextAlign.Left, font, paint);

        var before = GC.GetAllocatedBytesForCurrentThread();
        canvas.DrawText("Ab7", 0f, 12f, SKTextAlign.Left, font, paint);
        canvas.DrawText("cd", 0f, 20f, SKTextAlign.Left, font, paint);
        var blobs = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        draw(canvas, new SKColor(0xdc, 0x26, 0x26));

        Assert.Equal(blobs, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Text_In_A_Range_Is_Replayed_Only_Where_The_Range_Runs()
        => AssertTextRendersTheSame("InlineTextRange", """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 48 24" width="48" height="24">
              <defs><e:code><e:param name="laid" type="boolean" default="true" /></e:code></defs>
              <g display="{{ laid }}" transform="translate(3 2)">
                <text x="2" y="14" font-family="Noto Sans" font-size="11" fill="#7c3aed">on</text>
              </g>
              <path d="M 2 20 L 40 20" stroke="#111827" />
            </svg>
            """, arguments: new object?[] { true });

    [Fact]
    public void A_Picture_Replays_Its_Own_Matrices_Under_The_Canvas()
    {
        // Under a scale of its own and a clip, then a shape drawn after it back under the outer matrix.
        // Not a non-scaling stroke: inside a picture the runtime takes it against the canvas's whole
        // matrix and generated code, recorded or replayed, against the picture's (RMS 0.071 on master).
        var nested = new ShimSkiaSharp.SKPictureRecorder();
        var inner = nested.BeginRecording(ShimSkiaSharp.SKRect.Create(0, 0, 24, 24));
        inner.Save();
        inner.ClipRect(ShimSkiaSharp.SKRect.Create(0, 0, 10, 7), ShimSkiaSharp.SKClipOperation.Intersect, true);
        inner.SetMatrix(ShimSkiaSharp.SKMatrix.CreateScale(2f, 1.5f));
        var triangle = new ShimSkiaSharp.SKPath();
        triangle.MoveTo(1, 1);
        triangle.LineTo(6, 1);
        triangle.LineTo(6, 6);
        triangle.Close();
        inner.DrawPath(triangle, new ShimSkiaSharp.SKPaint { Style = ShimSkiaSharp.SKPaintStyle.Fill, IsAntialias = true, Color = new ShimSkiaSharp.SKColor(0xef, 0x44, 0x44, 0xff) });
        inner.Restore();

        // Balanced, as the scene's pictures are: the runtime replays a picture's matrices onto the canvas
        // it draws on, so one left set would outlast the picture there and not under DrawPicture.
        inner.Save();
        inner.SetMatrix(ShimSkiaSharp.SKMatrix.CreateScale(1.5f, 1.5f));
        inner.DrawPath(triangle, new ShimSkiaSharp.SKPaint { Style = ShimSkiaSharp.SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true, Color = new ShimSkiaSharp.SKColor(0x1e, 0x40, 0xaf, 0xff) });
        inner.Restore();

        var recorder = new ShimSkiaSharp.SKPictureRecorder();
        var canvas = recorder.BeginRecording(ShimSkiaSharp.SKRect.Create(0, 0, 24, 24));
        canvas.Save();
        canvas.SetMatrix(ShimSkiaSharp.SKMatrix.CreateTranslation(3.5f, 4f).PreConcat(ShimSkiaSharp.SKMatrix.CreateScale(1.25f, 1.25f)));
        canvas.DrawPicture(nested.EndRecording());
        var square = new ShimSkiaSharp.SKPath();
        square.AddRect(ShimSkiaSharp.SKRect.Create(9f, 9f, 5f, 5f));
        canvas.DrawPath(square, new ShimSkiaSharp.SKPaint { Style = ShimSkiaSharp.SKPaintStyle.Fill, IsAntialias = true, Color = new ShimSkiaSharp.SKColor(0x16, 0xa3, 0x4a, 0xff) });
        canvas.Restore();
        var model = recorder.EndRecording();

        using var runtime = new SkiaModel(new SKSvgSettings()).ToSKPicture(model);
        using var generated = Generated(model, "PictureMatrices", SkiaSharpTarget.V4, SvgExpressionDeclarations.Empty, Array.Empty<object?>(), bake: false);
        AssertSamePicture("PictureMatrices", runtime!, generated);
        AssertDrawsDirectlyTheSame("PictureMatrices", model, runtime!, SvgExpressionDeclarations.Empty, Array.Empty<object?>());
        Assert.Single(Regex.Matches(Code(model, "PictureMatrices", SvgBody.Direct), @"new SKPictureRecorder\(\)"));
    }

    private static readonly string s_noto = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "..", "externals", "resvg", "crates", "resvg", "tests", "fonts", "NotoSans-Regular.ttf"));

    /// <summary>
    /// Text against the runtime, from one font file on both sides: a family looked up by name
    /// resolves differently there and in a font manager (see <see cref="A_Typeface_Template_Draws_The_Typeface_It_Names"/>).
    /// </summary>
    /// <returns>The direct body, for what a row asserts of its text.</returns>
    /// <param name="expectedMarkup">The document with the arguments written in, which the runtime draws; see <see cref="AssertExpressionsRenderTheSame"/>.</param>
    private static string AssertTextRendersTheSame(string name, string markup, SkiaSharpTarget skiaSharp = SkiaSharpTarget.V4, object?[]? arguments = null, string? expectedMarkup = null)
    {
        Assert.True(File.Exists(s_noto), s_noto);
        var settings = new SKSvgSettings();
        settings.TypefaceProviders!.Insert(0, new Svg.Skia.TypefaceProviders.CustomTypefaceProvider(s_noto));

        ShimPicture TextModel(string svg)
        {
            var document = SvgService.FromSvg(svg);
            Assert.NotNull(document);
            var picture = SvgSceneRuntime.CreateModel(document!, new SkiaSvgAssetLoader(new SkiaModel(settings)));
            Assert.NotNull(picture);
            return picture!;
        }

        var model = TextModel(markup);

        using var runtime = new SkiaModel(settings).ToSKPicture(expectedMarkup is null ? model : TextModel(expectedMarkup));
        Assert.NotNull(runtime);

        var declarations = SvgExpressionDeclarations.Parse(markup);
        var template = $"SKTypeface.FromFile(@\"{s_noto}\")";
        arguments ??= Array.Empty<object?>();

        foreach (var bake in new[] { false, true })
        {
            var code = SkiaCSharpCodeGen.GenerateFile(
                new[] { new SkiaCSharpDrawing(model!, Namespace, name, declarations) },
                SvgHelperScope.PerClass,
                skiaSharp: skiaSharp,
                style: new SvgCodeStyle { Typeface = template, Bake = bake });

            // The recorder Record itself records with, and none for the pictures the text is in.
            Assert.Single(Regex.Matches(code, @"new SKPictureRecorder\(\)"));

            var type = Compile(code, name).GetType($"{Namespace}.{name}")!;
            using var recorded = (SKPicture)type.GetMethod("Record", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, arguments)!;
            AssertSamePicture(bake ? $"{name}-baked" : name, runtime!, recorded, bake ? BakedThreshold : Threshold);
        }

        AssertDrawsDirectlyTheSame(name, model!, runtime!, declarations, arguments, skiaSharp, typeface: template);

        return SkiaCSharpCodeGen.GenerateFile(
            new[] { new SkiaCSharpDrawing(model!, Namespace, name, declarations) },
            SvgHelperScope.PerClass,
            skiaSharp: skiaSharp,
            style: new SvgCodeStyle { Body = SvgBody.Direct, Typeface = template });
    }

    [Theory]
    [InlineData(SkiaSharpTarget.V3)]
    [InlineData(SkiaSharpTarget.V4)]
    public void A_Path_Drawn_Twice_Reads_One_Static(SkiaSharpTarget skiaSharp)
        // As AirFlowLevel's bars are: the same shape in two places and two colours, under a clip
        // that does cut, so its identity transform is the one left out.
        => AssertRendersTheSame($"SharedPath_{skiaSharp}", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <clipPath id="c" clipPathUnits="userSpaceOnUse">
                  <rect x="0" y="0" width="24" height="15" />
                </clipPath>
              </defs>
              <g clip-path="url(#c)">
                <path d="M 2 2 L 10 2 L 10 10 Z" fill="#dc2626" />
                <path d="M 2 2 L 10 2 L 10 10 Z" fill="#2563eb" transform="translate(10 8)" />
              </g>
            </svg>
            """, skiaSharp);

    [Theory]
    [InlineData(SkiaSharpTarget.V3)]
    [InlineData(SkiaSharpTarget.V4)]
    public void Nested_Translations_Draw_Without_Their_Saves(SkiaSharpTarget skiaSharp)
        // HueBulbBulb's shape: groups that only move what they hold, a shape after the inner group
        // that is under the outer one's matrix again, one after both under none, and a scale.
        => AssertRendersTheSame($"NestedTranslations_{skiaSharp}", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <g transform="translate(2 2)">
                <circle cx="4" cy="4" r="3" fill="none" stroke="#1e40af" />
                <g transform="translate(8 0)">
                  <path d="M 0 0 L 4 4" stroke="#dc2626" />
                  <path d="M 0 4 L 4 0" stroke="#dc2626" transform="translate(0 2)" />
                </g>
                <path d="M 0 10 L 8 10" stroke="#16a34a" />
              </g>
              <path d="M 1 18 L 7 22 L 1 22 Z" fill="#7c3aed" />
              <g transform="translate(12 14) scale(1.5)">
                <path d="M 0 0 L 6 0 L 3 5 Z" fill="#f59e0b" />
              </g>
            </svg>
            """, skiaSharp);

    [Fact]
    public void A_Layer_Gives_Back_The_Matrix_It_Was_Opened_Under()
        // The faded group's layer opens under its own translation, and its restore puts that back:
        // the rect after it is under the first group's again, and has to be moved there.
        => AssertRendersTheSame("LayerMatrix", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <g transform="translate(2 2)">
                <rect x="0" y="0" width="6" height="4" fill="#1e40af" />
              </g>
              <g transform="translate(10 4)" opacity="0.5">
                <circle cx="4" cy="4" r="4" fill="#dc2626" />
                <circle cx="8" cy="4" r="4" fill="#dc2626" />
              </g>
              <g transform="translate(2 2)">
                <rect x="0" y="14" width="6" height="4" fill="#16a34a" />
              </g>
            </svg>
            """);

    [Fact]
    public void A_Clip_Keeps_The_Save_That_Scopes_It()
        // The group's save is what takes its clip off the shape after it, and its matrix with it.
        => AssertRendersTheSame("ClipInATranslatedGroup", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <clipPath id="c">
                  <rect x="0" y="0" width="10" height="6" />
                </clipPath>
              </defs>
              <g transform="translate(3 3)" clip-path="url(#c)">
                <circle cx="5" cy="5" r="5" fill="#dc2626" />
                <path d="M 0 0 L 10 10" stroke="#1e40af" transform="translate(1 0)" />
              </g>
              <circle cx="12" cy="16" r="6" fill="#16a34a" />
            </svg>
            """);

    [Fact]
    public void A_Driven_Matrix_Two_Shapes_Share_Is_Set_Once()
        => AssertExpressionsRenderTheSame(
            "DrivenMatrixShared",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="x" type="number" default="2" /></e:code></defs>
              <g transform="translate({{ x }} 2)">
                <circle cx="4" cy="4" r="3" fill="#dc2626" />
                <rect x="0" y="9" width="8" height="3" fill="#1e40af" />
              </g>
              <circle cx="12" cy="18" r="4" fill="#16a34a" />
            </svg>
            """,
            arguments: new object?[] { 9f },
            expectedMarkup: """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <g transform="translate(9 2)">
                <circle cx="4" cy="4" r="3" fill="#dc2626" />
                <rect x="0" y="9" width="8" height="3" fill="#1e40af" />
              </g>
              <circle cx="12" cy="18" r="4" fill="#16a34a" />
            </svg>
            """);

    [Theory]
    [InlineData(SvgBody.Record)]
    [InlineData(SvgBody.Direct)]
    public void Translated_Shapes_Record_Only_Themselves(SvgBody body)
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <path d="M 0 0 L 4 4" fill="none" stroke="#dc2626" transform="translate(2 2)" />
              <path d="M 0 0 L 4 4" fill="none" stroke="#dc2626" transform="translate(10 2)" />
              <path d="M 0 0 L 4 4" fill="none" stroke="#dc2626" transform="translate(18 2)" />
            </svg>
            """;
        var model = Model(svg);

        int Operations(bool bake)
        {
            var code = SkiaCSharpCodeGen.GenerateFile(
                new[] { new SkiaCSharpDrawing(model, Namespace, "Translated", SvgExpressionDeclarations.Empty) },
                SvgHelperScope.PerClass,
                style: new SvgCodeStyle { Body = body, Bake = bake });
            var record = Compile(code, "Translated").GetType($"{Namespace}.Translated")!.GetMethod("Record")!;
            using var picture = (SKPicture)record.Invoke(null, null)!;
            return picture.ApproximateOperationCount;
        }

        // Each translation is in its path's coordinates, so the three paths are all there is.
        Assert.Equal(3, Operations(bake: true));

        // Unbaked, a direct body translates before each path, setting the caller's matrix back
        // for two of them, inside one save: 10. A save, a set, a concat and a restore around each
        // made it 15.
        Assert.Equal(body == SvgBody.Direct ? 10 : 6, Operations(bake: false));
    }

    [Theory]
    [InlineData(SkiaSharpTarget.V3)]
    [InlineData(SkiaSharpTarget.V4)]
    public void A_Fractional_Translation_Is_Baked_Within_A_Few_Levels(SkiaSharpTarget skiaSharp)
        // PaintCode's offsets carry four decimals, which no float holds exactly: the coordinates a
        // translation is added to round where the matrix would have, and edges move a little.
        => AssertRendersTheSame($"FractionalTranslations_{skiaSharp}", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 30 30" width="30" height="30">
              <ellipse cx="10.5" cy="10.5" rx="10.5" ry="10.5" fill="none" stroke="#1e40af" stroke-width="2" transform="translate(4.5 4.5)" />
              <path d="M 4.0125 0 C 0.1597 0 -0.1825 2.6719 0.0596 4.3594 C 0.3017 6.0469 1.5107 7.3828 1.9412 8.8125 C 2.3716 10.2422 2.2909 12 4.0125 12"
                    fill="none" stroke="#dc2626" stroke-width="0.5" stroke-miterlimit="10" transform="translate(11 9.5)" />
              <path d="M 0 0.5859 L 3.4434 0" fill="none" stroke="#dc2626" stroke-width="0.5" transform="translate(13.4254 18.8687)" />
              <path d="M 0 2.8363 C 0 2.8363 0 0 0 0" fill="none" stroke="#dc2626" stroke-width="0.25" transform="translate(14.7166 15.1183)" />
              <rect x="0" y="0" width="7.1" height="1.7578" fill="#16a34a" transform="translate(12.9833 23.2241)" />
              <rect x="0" y="0" width="5.3" height="3.1" rx="0.75" fill="#7c3aed" transform="translate(1.3333 22.4457)" />
              <polygon points="0,0 3.7,0.4 1.9,3.3" fill="#f59e0b" transform="translate(24.2838 6.851)" />
            </svg>
            """, skiaSharp);

    [Fact]
    public void A_Mask_Keeps_Its_Layer()
        // Its outer layer is opaque and source-over, so only what it holds says it is not a no-op.
        => AssertRendersTheSame("Mask", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <mask id="m"><circle cx="12" cy="12" r="8" fill="#fff" /></mask>
              </defs>
              <rect x="0" y="0" width="24" height="24" fill="#facc15" />
              <rect x="2" y="2" width="20" height="20" fill="#7c3aed" mask="url(#m)" />
            </svg>
            """);

    // Blinds180ltr's shape: an opacity on each line, which has no area and so gives its layer no
    // bounds, under an antialiased clip turned a float's worth off 90°. That clip is what tells the
    // two apart at a zero threshold: through a layer it covers an edge pixel twice.
    private const string DrivenOpacityUnderAClip = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code><e:param name="o" type="number" default="1" /></e:code>
            <clipPath id="c"><rect x="3.3" y="3.3" width="17.4" height="17.4" transform="rotate(-90 12 12)" /></clipPath>
          </defs>
          <g clip-path="url(#c)">
            <path d="M0,9 L24,9" stroke="#1e40af" stroke-width="3" opacity="{{ o }}" />
            <path d="M0,15 L24,15" stroke="#1e40af" stroke-width="3" opacity="{{ o }}" />
          </g>
        </svg>
        """;

    [Fact]
    public void A_Driven_Opacity_Of_One_Draws_No_Layer()
        => AssertExpressionsRenderTheSame(
            "ExprOpaqueLayer",
            DrivenOpacityUnderAClip,
            new object?[] { 1f },
            DrivenOpacityUnderAClip.Replace(" opacity=\"{{ o }}\"", ""));

    [Fact]
    public void A_Driven_Opacity_Below_One_Fades_Each_Line_As_A_Written_One_Does()
        => AssertExpressionsRenderTheSame(
            "ExprTranslucentLayer",
            DrivenOpacityUnderAClip,
            new object?[] { 0.4f },
            DrivenOpacityUnderAClip.Replace("{{ o }}", "0.4"));

    // A PaintCode symbol: its frame, a fraction of a degree off upright, clipping content that stays
    // well inside it. The miter limit of 10 reaches past the frame only where a join is that sharp.
    private const string FramedSymbol = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <defs><clipPath id="frame"><rect width="20" height="20" /></clipPath></defs>
          <g transform="translate(12,12) rotate(0.5) translate(-10,-10)" clip-path="url(#frame)">
            <circle cx="10" cy="10" r="7" fill="#7c3aed" />
            <path d="M3,15 L8,9 L12,12 L17,5" fill="none" stroke="#facc15" stroke-width="1.5" stroke-miterlimit="10" />
          </g>
        </svg>
        """;

    [Fact]
    public void A_Clip_That_Cuts_Nothing_Draws_As_If_It_Were_Not_There()
    {
        var commands = Model(FramedSymbol).Commands!;
        Assert.Contains(Enumerable.Range(0, commands.Count), CommandPlan.For(commands, symbolic: true).Skips);

        AssertExpressionsRenderTheSame("UncutClip", FramedSymbol, expectedMarkup: FramedSymbol.Replace(" clip-path=\"url(#frame)\"", ""));
    }

    // One shape under a group opacity, against the same group kept a layer by a second shape the
    // first hides: what the layer composited, and what the folded paint draws instead.
    private const string FadedShape = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect width="24" height="24" fill="#facc15" />
          <g opacity="0.4"><circle cx="12" cy="12" r="7.3" fill="#7c3aed" /></g>
        </svg>
        """;

    [Fact]
    public void A_Layer_Folded_Into_Its_Shape_Draws_As_The_Layer_Did_But_For_Rounding()
    {
        AssertRendersTheSame("FoldedLayer", FadedShape);

        // A layer's composite rounds once more than a faded paint: 19,183 of the circle's pixels
        // move by 1/255 and 5 by 2/255, as the same circle drawn with fill-opacity does.
        const double rounding = 0.0011;
        var layered = FadedShape.Replace("<g opacity=\"0.4\">", "<g opacity=\"0.4\"><circle cx=\"12\" cy=\"12\" r=\"3\" />");
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var folded = new SkiaModel(new SKSvgSettings()).ToSKPicture(Model(FadedShape));
            using var layer = new SkiaModel(new SKSvgSettings()).ToSKPicture(Model(layered));
            ImageHelper.CompareImages(
                "FoldedLayer-vs-layer",
                Draw(folded!, Path.Combine(directory, "folded.png")),
                Draw(layer!, Path.Combine(directory, "layer.png")),
                rounding);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_Layer_Over_A_Fill_And_Its_Stroke_Stays_A_Layer()
        => AssertRendersTheSame("OverlappingLayer", FadedShape.Replace(
            "<g opacity=\"0.4\"><circle cx=\"12\" cy=\"12\" r=\"7.3\" fill=\"#7c3aed\" /></g>",
            "<circle cx=\"12\" cy=\"12\" r=\"7.3\" fill=\"#7c3aed\" stroke=\"#1e40af\" stroke-width=\"3\" opacity=\"0.4\" />"));

    // Blinds' slats: what the clip hides depends on how far the level slides them, so the export
    // keeps it at every level while the interpreter, knowing the level, drops it where they fit.
    private const string SlidingSlats = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code><e:param name="level" type="number" default="0" /></e:code>
            <clipPath id="c"><rect x="2" y="2" width="20" height="20" transform="rotate(-90 12 12)" /></clipPath>
          </defs>
          <g clip-path="url(#c)">
            <g transform="translate(0,{{ level }})">
              <path d="M6,8 L18,8" stroke="#1e40af" stroke-width="3" />
              <path d="M6,14 L18,14" stroke="#1e40af" stroke-width="3" />
            </g>
          </g>
        </svg>
        """;

    [Theory]
    [InlineData(0f)]
    [InlineData(9f)]
    public void A_Clip_Over_Driven_Geometry_Is_Kept_For_Every_Value(float level)
        => AssertExpressionsRenderTheSame($"SlidingSlats{level}", SlidingSlats, new object?[] { level }, SlidingSlats.Replace("{{ level }}", level.ToString(CultureInfo.InvariantCulture)));

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

    // ---- expressions ----
    // Two shapes, both needed. Most cases pick values landing on the placeholder the renderer draws,
    // so the comparison is against the same document at a zero threshold — but those alone would
    // pass if the emitter ignored expressions entirely. The `expectedMarkup` ones state the value as
    // a literal in a second document, and are what prove it reaches the paint.

    [Fact]
    public void An_Expression_Value_Reaches_The_Paint()
        // The placeholder is grey, so red can only come from the expression.
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

    // A driven transform is the one expression that moves ink rather than colouring it, so a circle
    // is no longer enough: the comparison has to be able to tell "turned" from "not turned".

    [Fact]
    public void A_Driven_Rotate_Reaches_The_Matrix()
        => AssertExpressionsRenderTheSame(
            "ExprRotate",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="angle" type="number" default="0" /></e:code></defs>
              <rect x="6" y="10" width="12" height="4" transform="rotate({{ angle }} 12 12)" fill="#ff0000" />
            </svg>
            """,
            new object?[] { 30f },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect x="6" y="10" width="12" height="4" transform="rotate(30 12 12)" fill="#ff0000" />
            </svg>
            """);

    [Fact]
    public void A_Driven_Translate_Reaches_The_Matrix()
        => AssertExpressionsRenderTheSame(
            "ExprTranslate",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="dx" type="number" default="0" /></e:code></defs>
              <circle cx="8" cy="12" r="4" transform="translate({{ dx }}, 0)" fill="#ff0000" />
            </svg>
            """,
            new object?[] { 5f },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="8" cy="12" r="4" transform="translate(5, 0)" fill="#ff0000" />
            </svg>
            """);

    /// <summary>Two functions in one attribute, one literal argument among the driven ones.</summary>
    [Fact]
    public void Two_Driven_Functions_Compose_In_The_Order_Written()
        => AssertExpressionsRenderTheSame(
            "ExprTransformPair",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code>
                <e:param name="dx" type="number" default="0" />
                <e:param name="angle" type="number" default="0" />
              </e:code></defs>
              <rect x="6" y="10" width="12" height="4" transform="translate({{ dx }}, 2) rotate({{ angle }} 12 12)" fill="#ff0000" />
            </svg>
            """,
            new object?[] { 3f, 30f },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect x="6" y="10" width="12" height="4" transform="translate(3, 2) rotate(30 12 12)" fill="#ff0000" />
            </svg>
            """);

    /// <remarks>
    /// Generated code assigns an absolute matrix rather than concatenating, so the ancestor has to
    /// reach the emitted composition: emitting only the element's own function draws this at the
    /// wrong scale while every single-element case above still passes.
    /// </remarks>
    [Fact]
    public void A_Driven_Transform_Composes_With_What_It_Sits_Under()
        => AssertExpressionsRenderTheSame(
            "ExprTransformNested",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="dx" type="number" default="0" /></e:code></defs>
              <g transform="translate(2, 3) scale(1.5)">
                <circle cx="6" cy="6" r="3" transform="translate({{ dx }}, 0)" fill="#ff0000" />
              </g>
            </svg>
            """,
            new object?[] { 2f },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <g transform="translate(2, 3) scale(1.5)">
                <circle cx="6" cy="6" r="3" transform="translate(2, 0)" fill="#ff0000" />
              </g>
            </svg>
            """);

    /// <remarks>
    /// What an unbound argument reads as is pinned on the lift, in SvgTransformExpressionTests; what
    /// this covers is the scale arm of the emitter, which no other case reaches.
    /// </remarks>
    [Fact]
    public void A_Driven_Scale_Reaches_The_Matrix()
        => AssertExpressionsRenderTheSame(
            "ExprScale",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="s" type="number" default="1" /></e:code></defs>
              <circle cx="6" cy="6" r="3" transform="scale({{ s }})" fill="#ff0000" />
            </svg>
            """,
            new object?[] { 1.5f },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="6" cy="6" r="3" transform="scale(1.5)" fill="#ff0000" />
            </svg>
            """);

    /// <summary>The skew arm, whose degrees-to-tangent conversion is written on both sides.</summary>
    [Fact]
    public void A_Driven_Skew_Reaches_The_Matrix()
        => AssertExpressionsRenderTheSame(
            "ExprSkew",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="a" type="number" default="0" /></e:code></defs>
              <rect x="4" y="8" width="10" height="6" transform="skewX({{ a }})" fill="#ff0000" />
            </svg>
            """,
            new object?[] { 20f },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect x="4" y="8" width="10" height="6" transform="skewX(20)" fill="#ff0000" />
            </svg>
            """);

    [Fact]
    public void A_Driven_Size_Leaves_A_Lone_Rect_Drawn_As_A_Rect()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="s" type="number" default="1" /></e:code></defs>
              <rect x="2.3" y="3.7" width="8.4" height="5.1" transform="scale({{ s }})" fill="#ff0000" />
            </svg>
            """;

        AssertExpressionsRenderTheSame(
            "ExprLoneRect",
            svg,
            new object?[] { 1.7f },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect x="2.3" y="3.7" width="8.4" height="5.1" transform="scale(1.7)" fill="#ff0000" />
            </svg>
            """);

        var code = Code(Model(svg), "ExprLoneRect", SvgBody.Direct, SvgExpressionDeclarations.Parse(svg));
        Assert.Contains(".DrawRect(new SKRect(", code);
    }

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
    public void An_Integer_Parameter_Reaches_Both_A_Choice_And_A_Paint()
        // Everything the integer added, through the only path that compiles and draws the emitted
        // code: an int argument, the two helpers C# would otherwise throw from (SvgIMod, and
        // Math.Min on ints), and SvgNum crossing into an opacity. An unselected helper is CS0103,
        // so this also says the selection scan found them.
        => AssertExpressionsRenderTheSame(
            "ExprInteger",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <e:code><e:param name="steps" type="integer" default="1" min="0" max="9" step="1" /></e:code>
              </defs>
              <circle cx="12" cy="12" r="9"
                      fill="{{ mod(steps, 2) == 1 ? #22c55e : #1e40af }}"
                      opacity="{{ num(min(steps, 5)) / 10 }}" />
            </svg>
            """,
            new object?[] { 7 },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="12" cy="12" r="9" fill="#22c55e" opacity="0.5" />
            </svg>
            """);

    [Fact]
    public void A_String_Parameter_Chooses_The_Paint()
        // The whole point of the type: a string reaches no attribute, so the only way it can show up
        // in a drawing is by choosing between values that do. `lower` comes with it, which is also
        // what says the helper was selected into the generated class -- an unselected one is CS0103.
        => AssertExpressionsRenderTheSame(
            "ExprString",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="theme" type="string" default="'light'" /></e:code></defs>
              <circle cx="12" cy="12" r="9" fill="{{ lower(theme) == 'dark' ? #22c55e : #1e40af }}" />
            </svg>
            """,
            new object?[] { "DARK" },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="12" cy="12" r="9" fill="#22c55e" />
            </svg>
            """);

    [Fact]
    public void A_String_Default_Paints_When_No_Value_Is_Supplied()
        // Null argument, so the generated body takes the `?? default` local. The default is itself a
        // call rather than a literal, which is why every string default gets that local: as a C#
        // argument default it would be CS1736.
        => AssertExpressionsRenderTheSame(
            "ExprStringDefault",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="theme" type="string" default="lower('DARK')" /></e:code></defs>
              <circle cx="12" cy="12" r="9" fill="{{ theme == 'dark' ? #22c55e : #1e40af }}" />
            </svg>
            """,
            new object?[] { null },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="12" cy="12" r="9" fill="#22c55e" />
            </svg>
            """);

    [Fact]
    public void A_Colour_Default_Still_Paints_When_It_Is_Required()
        // A colour with a default, declared before a parameter without one, so C# cannot take the
        // defaults and every argument is generated as required. That drops the `?? default` local
        // the colour normally reaches the body through, and this is what says the body still reads
        // the value it was handed: a wrong local is CS0103 and a wrong operand is CS0019, but a body
        // reading the right name and the wrong value would compile and draw the wrong picture.
        => AssertExpressionsRenderTheSame(
            "ExprRequiredColourDefault",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <e:code>
                  <e:param name="tint" type="color" default="#1e40af" />
                  <e:param name="alpha" type="number" />
                  <e:let name="solid">withAlpha(tint, alpha)</e:let>
                </e:code>
              </defs>
              <circle cx="12" cy="12" r="9" fill="{{ solid }}" />
            </svg>
            """,
            new object?[] { new SKColor(0x22, 0xc5, 0x5e, 0xff), 1f },
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
    public void Two_Blocks_Are_One_Declaration_List_In_Document_Order()
        // What a Svg.Studio group looks like once it has declared into a drawing: the group's block
        // in front of the drawing's own. The order is the whole of it — it is what a positional call
        // means, so the generated Draw and the runtime renderer agree only if both read the two
        // blocks the same way round, and a let in the first has to see nothing of the second.
        => AssertExpressionsRenderTheSame(
            "ExprTwoBlocks",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <e:code>
                  <e:param name="tint" type="color" />
                  <e:let name="solid">withAlpha(tint, 1)</e:let>
                </e:code>
                <e:code>
                  <e:param name="ring" type="number" default="2" />
                  <e:let name="edge">mix(solid, solid, 0.5)</e:let>
                </e:code>
              </defs>
              <rect x="0" y="0" width="24" height="24" fill="{{ edge }}" stroke="{{ solid }}" stroke-width="{{ ring }}" />
            </svg>
            """,
            new object?[] { new SKColor(128, 128, 128, 255), 3f });

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
    public void A_Stroke_Width_Expression_Value_Reaches_The_Paint()
        => AssertExpressionsRenderTheSame(
            "ExprStrokeWidth",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="weight" type="number" default="1" /></e:code></defs>
              <circle cx="12" cy="12" r="8" fill="none" stroke="#0f766e" stroke-width="{{ weight }}" />
            </svg>
            """,
            new object?[] { 4f },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="12" cy="12" r="8" fill="none" stroke="#0f766e" stroke-width="4" />
            </svg>
            """);

    [Fact]
    public void A_Stroke_Width_Expression_Is_Inherited_The_Way_A_Written_One_Is()
        => AssertExpressionsRenderTheSame(
            "ExprStrokeWidthInherited",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="weight" type="number" default="1" /></e:code></defs>
              <g stroke="#b45309" fill="none" stroke-width="{{ weight * 2 }}">
                <circle cx="12" cy="12" r="8" />
              </g>
            </svg>
            """,
            new object?[] { 1.5f },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <g stroke="#b45309" fill="none" stroke-width="3">
                <circle cx="12" cy="12" r="8" />
              </g>
            </svg>
            """);

    [Fact]
    public void A_Dash_Offset_Expression_Value_Reaches_The_Paint()
        // The whole of what a driven arc needs: a ring dashed at its own circumference shows only
        // what the phase leaves, so the offset is the sweep.
        => AssertExpressionsRenderTheSame(
            "ExprDashOffset",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="shift" type="number" default="0" /></e:code></defs>
              <circle cx="12" cy="12" r="8" fill="none" stroke="#0f766e" stroke-width="3"
                      stroke-dasharray="50.2655 50.2655" stroke-dashoffset="{{ shift }}" />
            </svg>
            """,
            new object?[] { 25f },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="12" cy="12" r="8" fill="none" stroke="#0f766e" stroke-width="3"
                      stroke-dasharray="50.2655 50.2655" stroke-dashoffset="25" />
            </svg>
            """);

    [Fact]
    public void A_Dash_Offset_Expression_Is_Inherited_The_Way_A_Written_One_Is()
        => AssertExpressionsRenderTheSame(
            "ExprDashOffsetInherited",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="shift" type="number" default="0" /></e:code></defs>
              <g stroke="#b45309" fill="none" stroke-width="3" stroke-dasharray="50.2655 50.2655" stroke-dashoffset="{{ shift * 2 }}">
                <circle cx="12" cy="12" r="8" />
              </g>
            </svg>
            """,
            new object?[] { 6f },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <g stroke="#b45309" fill="none" stroke-width="3" stroke-dasharray="50.2655 50.2655" stroke-dashoffset="12">
                <circle cx="12" cy="12" r="8" />
              </g>
            </svg>
            """);

    [Fact]
    public void A_Driven_Dash_Offset_Is_Normalised_By_PathLength()
        // pathLength scales every length along the path, and a literal offset goes through that
        // scale while it is being recorded. An expression is substituted into the recorded paint
        // afterwards, so the same scale has to be carried in the node or the two disagree: here the
        // path is 2.5 times its declared length, and 8 has to land where 20 does.
        => AssertExpressionsRenderTheSame(
            "ExprDashOffsetPathLength",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="shift" type="number" default="0" /></e:code></defs>
              <circle cx="12" cy="12" r="8" pathLength="20" fill="none" stroke="#0f766e" stroke-width="3"
                      stroke-dasharray="20 20" stroke-dashoffset="{{ shift }}" />
            </svg>
            """,
            new object?[] { 8f },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="12" cy="12" r="8" pathLength="20" fill="none" stroke="#0f766e" stroke-width="3"
                      stroke-dasharray="20 20" stroke-dashoffset="8" />
            </svg>
            """);

    [Fact]
    public void A_Dash_Array_Expression_Value_Reaches_The_Paint()
        // An arc whose two ends both move: the dash is the sweep, the gap the rest of the ring.
        => AssertExpressionsRenderTheSame(
            "ExprDashArray",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="arc" type="number" default="0" /></e:code></defs>
              <circle cx="12" cy="12" r="8" fill="none" stroke="#0f766e" stroke-width="3"
                      stroke-dasharray="{{ arc }} {{ 50.2655 - arc }}" stroke-dashoffset="{{ arc / 2 }}" />
            </svg>
            """,
            new object?[] { 30f },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="12" cy="12" r="8" fill="none" stroke="#0f766e" stroke-width="3"
                      stroke-dasharray="30 20.2655" stroke-dashoffset="15" />
            </svg>
            """);

    [Fact]
    public void An_Odd_Dash_Array_Expression_Repeats_As_A_Written_One_Does()
        => AssertExpressionsRenderTheSame(
            "ExprDashArrayOdd",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="dash" type="number" default="1" /></e:code></defs>
              <g stroke="#b45309" fill="none" stroke-width="3" stroke-dasharray="{{ dash }} 2 3">
                <circle cx="12" cy="12" r="8" />
              </g>
            </svg>
            """,
            new object?[] { 6f },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <g stroke="#b45309" fill="none" stroke-width="3" stroke-dasharray="6 2 3">
                <circle cx="12" cy="12" r="8" />
              </g>
            </svg>
            """);

    [Fact]
    public void A_Driven_Dash_Array_Is_Normalised_By_PathLength()
        => AssertExpressionsRenderTheSame(
            "ExprDashArrayPathLength",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="arc" type="number" default="0" /></e:code></defs>
              <circle cx="12" cy="12" r="8" pathLength="20" fill="none" stroke="#0f766e" stroke-width="3"
                      stroke-dasharray="{{ arc }} {{ 20 - arc }}" />
            </svg>
            """,
            new object?[] { 13f },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="12" cy="12" r="8" pathLength="20" fill="none" stroke="#0f766e" stroke-width="3"
                      stroke-dasharray="13 7" />
            </svg>
            """);

    [Fact]
    public void A_Dash_Array_That_Binds_Invalid_Strokes_Solid()
        => AssertExpressionsRenderTheSame(
            "ExprDashArrayInvalid",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="arc" type="number" default="1" /></e:code></defs>
              <circle cx="12" cy="12" r="8" fill="none" stroke="#0f766e" stroke-width="3"
                      stroke-dasharray="{{ arc }} 4" />
            </svg>
            """,
            new object?[] { -2f },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="12" cy="12" r="8" fill="none" stroke="#0f766e" stroke-width="3" />
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
    public void A_False_Display_Expression_Draws_Nothing()
        // Same mechanism as visibility, on the attribute that also decides whether the subtree is
        // compiled at all — so this fails if the placeholder stops keeping it in.
        => AssertExpressionsRenderTheSame(
            "ExprDisplayNone",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="laid" type="boolean" default="true" /></e:code></defs>
              <rect x="0" y="0" width="24" height="24" fill="#facc15" />
              <g display="{{ laid }}">
                <circle cx="12" cy="12" r="8" fill="#111827" />
              </g>
            </svg>
            """,
            new object?[] { false },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect x="0" y="0" width="24" height="24" fill="#facc15" />
            </svg>
            """);

    [Fact]
    public void Display_And_Visibility_Nest_On_One_Element()
        => AssertExpressionsRenderTheSame(
            "ExprDisplayAndVisibility",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <e:code>
                  <e:param name="laid" type="boolean" default="true" />
                  <e:param name="shown" type="boolean" default="true" />
                </e:code>
              </defs>
              <rect x="0" y="0" width="24" height="24" fill="#facc15" />
              <circle cx="12" cy="12" r="8" fill="#111827" display="{{ laid }}" visibility="{{ shown }}" />
            </svg>
            """,
            new object?[] { true, false },
            """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect x="0" y="0" width="24" height="24" fill="#facc15" />
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
    public void An_Opacity_Attribute_Takes_An_Expression()
        // The emitted colour is the authored literal scaled at run time, where the literal document
        // folds the same fade into a constant alpha — the two have to land on the same byte.
        => AssertExpressionsRenderTheSame(
            "ExprFillOpacity",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <rect x="0" y="0" width="24" height="24" fill="#facc15" />
              <circle cx="12" cy="12" r="10" fill="#3366cc" fill-opacity="{{ 0.5 }}" />
            </svg>
            """,
            expectedMarkup: """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect x="0" y="0" width="24" height="24" fill="#facc15" />
              <circle cx="12" cy="12" r="10" fill="#3366cc" fill-opacity="0.5" />
            </svg>
            """);

    [Fact]
    public void A_Driven_Opacity_Fades_A_Black_Shape()
        // Black is a paint's default, and the placeholder opacity of 1 leaves it opaque black: the
        // colour is still the expression, which is what makes the paint follow the argument.
        => AssertExpressionsRenderTheSame(
            "ExprBlackFillOpacity",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="o" type="number" default="1" /></e:code></defs>
              <rect x="0" y="0" width="24" height="24" fill="#facc15" />
              <circle cx="12" cy="12" r="10" fill-opacity="{{ o }}" />
              <path d="M 2 22 L 22 22" stroke="#000" stroke-width="2" stroke-opacity="{{ o }}" />
            </svg>
            """,
            arguments: new object?[] { 0.3f },
            expectedMarkup: """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect x="0" y="0" width="24" height="24" fill="#facc15" />
              <circle cx="12" cy="12" r="10" fill-opacity="0.3" />
              <path d="M 2 22 L 22 22" stroke="#000" stroke-width="2" stroke-opacity="0.3" />
            </svg>
            """);

    [Fact]
    public void A_Gradient_Stop_Opacity_Takes_An_Expression()
        => AssertExpressionsRenderTheSame(
            "ExprStopOpacity",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="24" y2="0">
                  <stop offset="0%" stop-color="#3366cc" stop-opacity="{{ 0.5 }}" />
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
                  <stop offset="0%" stop-color="#3366cc" stop-opacity="0.5" />
                  <stop offset="100%" stop-color="#1e40af" />
                </linearGradient>
              </defs>
              <rect x="0" y="0" width="24" height="24" fill="url(#g)" />
            </svg>
            """);

    [Fact]
    public void An_Expression_In_A_Style_Declaration_Draws_As_The_Attribute_Would()
        => AssertExpressionsRenderTheSame(
            "ExprStyleDeclaration",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <rect x="0" y="0" width="24" height="24" fill="#facc15" />
              <circle cx="12" cy="12" r="10" style="fill: {{ rgb(255, 0, 0) }}; stroke: #111827; stroke-width: 2" />
            </svg>
            """,
            expectedMarkup: """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect x="0" y="0" width="24" height="24" fill="#facc15" />
              <circle cx="12" cy="12" r="10" style="fill: #ff0000; stroke: #111827; stroke-width: 2" />
            </svg>
            """);

    [Fact]
    public void A_Flood_Colour_Takes_An_Expression()
        // The flood is a nested picture behind an image filter, which is the one place a colour
        // reaches the drawing without the emitter walking a command list to get there.
        => AssertExpressionsRenderTheSame(
            "ExprFlood",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <filter id="f" x="0" y="0" width="24" height="24">
                  <feFlood flood-color="{{ rgb(255, 0, 0) }}" flood-opacity="0.5" />
                </filter>
              </defs>
              <rect x="0" y="0" width="24" height="24" fill="#facc15" filter="url(#f)" />
            </svg>
            """,
            expectedMarkup: """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <filter id="f" x="0" y="0" width="24" height="24">
                  <feFlood flood-color="#ff0000" flood-opacity="0.5" />
                </filter>
              </defs>
              <rect x="0" y="0" width="24" height="24" fill="#facc15" filter="url(#f)" />
            </svg>
            """);

    [Fact]
    public void An_Omitted_Colour_Argument_Falls_Back_To_Its_Declared_Default()
        // Passing null is what a caller omitting the argument gets, and the expected document
        // states the default as a literal — so this fails if the fallback is dropped or wrong.
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
        // Measured, not assumed: the recorder emits Begin, Save, SetMatrix, DrawPath, Restore, End,
        // so the range's own Restore pops the matrix whether it is kept or deleted. What this covers
        // is that the conditional resolves with state commands in it; ConditionalRangeTests pins the
        // keep-versus-delete difference.
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
    public void A_Conditional_That_Folds_Away_Draws_Like_The_Document_Without_It()
        // The range is never emitted, Save, SetMatrix and Restore included; the blue circle after it
        // has to land where it would with no group at all. The true one is emitted without its if,
        // so its locals sit one scope further out than they used to.
        => AssertExpressionsRenderTheSame(
            "ExprFoldedConditionals",
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:let name="shown">2 &gt; 1</e:let></e:code></defs>
              <g transform="translate(0 -8)" visibility="{{ !shown }}">
                <circle cx="12" cy="12" r="5" fill="#ff0000" />
              </g>
              <g transform="translate(4 0)" visibility="{{ shown }}">
                <circle cx="4" cy="4" r="3" fill="#22c55e" />
              </g>
              <circle cx="12" cy="16" r="5" fill="#1e40af" />
            </svg>
            """,
            arguments: null,
            expectedMarkup: """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <g transform="translate(4 0)">
                <circle cx="4" cy="4" r="3" fill="#22c55e" />
              </g>
              <circle cx="12" cy="16" r="5" fill="#1e40af" />
            </svg>
            """);

    // Driven strokes one after another: widths and caps that change and stay, a fade that comes and
    // goes over the same colour, a gradient that builds its own paint between them, and a range whose
    // stroke the next one repeats, which only holds where the range ran.
    private const string SharedPaintMarkup = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code>
              <e:param name="tint" type="color" default="#1e40af" />
              <e:param name="shown" type="boolean" default="true" />
            </e:code>
            <linearGradient id="g" x1="0" x2="1">
              <stop offset="0" stop-color="#dc2626" />
              <stop offset="1" stop-color="#16a34a" />
            </linearGradient>
          </defs>
          <path d="M 2 2 L 22 2" stroke="{{ tint }}" stroke-width="2" />
          <path d="M 2 5 L 22 5" stroke="{{ tint }}" stroke-width="2" opacity="0.5" />
          <path d="M 2 8 L 22 8" stroke="{{ tint }}" stroke-width="1" stroke-linecap="round" />
          <rect x="2" y="10" width="20" height="3" fill="url(#g)" />
          <g visibility="{{ shown }}">
            <path d="M 2 15 L 22 15" stroke="{{ tint }}" stroke-width="3" />
          </g>
          <path d="M 2 19 L 22 19" stroke="{{ tint }}" stroke-width="3" />
          <circle cx="12" cy="22" r="1.5" fill="{{ tint }}" />
        </svg>
        """;

    [Fact]
    public void Driven_Paints_Share_One_Paint()
        => AssertExpressionsRenderTheSame(
            "SharedPaint",
            SharedPaintMarkup,
            arguments: new object?[] { new SKColor(0x7c, 0x3a, 0xed), true },
            expectedMarkup: SharedPaintMarkup
                .Replace("""<g visibility="{{ shown }}">""", "<g>")
                .Replace("{{ tint }}", "#7c3aed"));

    [Fact]
    public void A_Range_Not_Taken_Leaves_The_Shared_Paint_To_Be_Set_Again()
        => AssertExpressionsRenderTheSame(
            "SharedPaintSkipped",
            SharedPaintMarkup,
            arguments: new object?[] { new SKColor(0x1e, 0x40, 0xaf), false },
            expectedMarkup: SharedPaintMarkup
                .Replace("""<g visibility="{{ shown }}">""", """<g visibility="hidden">""")
                .Replace("{{ tint }}", "#1e40af"));

    // Models the scene compiler never writes, whose ranges hold whole elements and whose paths open
    // with a move, but which a picture built by hand can hold.
    private static ShimSkiaSharp.SKPath Square(float size)
    {
        var path = new ShimSkiaSharp.SKPath();
        path.AddRect(ShimSkiaSharp.SKRect.Create(0f, 0f, size, size));
        return path;
    }

    private static readonly ShimSkiaSharp.SKPaint s_red = new() { IsAntialias = true, Color = new ShimSkiaSharp.SKColor(0xdc, 0x26, 0x26, 0xff) };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_Range_That_Gives_Back_An_Earlier_Save_Keeps_Every_Save(bool shown)
    {
        // The range restores the clipping save opened before it and opens another, which the
        // restore after it gives back: the shape after both is unclipped whether the range ran.
        var declarations = SvgExpressionDeclarations.Parse("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0">
              <defs><e:code><e:param name="shown" type="boolean" default="true" /></e:code></defs>
            </svg>
            """);
        var recorder = new ShimSkiaSharp.SKPictureRecorder();
        var canvas = recorder.BeginRecording(ShimSkiaSharp.SKRect.Create(0, 0, 24, 24));
        canvas.Save();
        canvas.ClipRect(ShimSkiaSharp.SKRect.Create(0, 0, 12, 24));
        canvas.BeginConditional(ShimSkiaSharp.SymNode.Source("shown"));
        canvas.Restore();
        canvas.Save();
        canvas.EndConditional();
        canvas.Restore();
        canvas.DrawPath(Square(24), s_red);
        var model = recorder.EndRecording();
        var name = $"CrossingRange{shown}";

        var evaluated = SvgSceneExpressionEvaluator.Evaluate(model, declarations, Bind(declarations, new object?[] { shown }));
        using var expected = new SkiaModel(new SKSvgSettings()).ToSKPicture(evaluated);
        using var generated = Generated(model, name, SkiaSharpTarget.V4, declarations, new object?[] { shown });

        AssertSamePicture(name, expected!, generated);
        AssertDrawsDirectlyTheSame(name, model, expected!, declarations, new object?[] { shown });
    }

    [Fact]
    public void A_Restore_With_Nothing_Open_Leaves_The_Callers_Saves_Alone()
    {
        var recorder = new ShimSkiaSharp.SKPictureRecorder();
        var canvas = recorder.BeginRecording(ShimSkiaSharp.SKRect.Create(0, 0, 24, 24));
        canvas.DrawPath(Square(12), s_red);
        canvas.Restore();
        canvas.Restore();
        var draw = Compile(Code(recorder.EndRecording(), "StrayRestore", SvgBody.Direct), "StrayRestore")
            .GetType($"{Namespace}.StrayRestore")!
            .GetMethod("Draw")!;

        using var bitmap = new SKBitmap(24, 24);
        using var target = new SKCanvas(bitmap);
        target.Save();
        target.Translate(5, 5);
        var saves = target.SaveCount;

        draw.Invoke(null, new object?[] { target });

        Assert.Equal(saves, target.SaveCount);
        Assert.Equal(SKMatrix.CreateTranslation(5, 5), target.TotalMatrix);
    }

    [Fact]
    public void A_Restore_With_Nothing_Open_Gives_Back_Nothing()
        // As Skia's recorder ignores it: the clip and the translation set before it still hold.
    {
        var recorder = new ShimSkiaSharp.SKPictureRecorder();
        var canvas = recorder.BeginRecording(ShimSkiaSharp.SKRect.Create(0, 0, 24, 24));
        canvas.ClipRect(ShimSkiaSharp.SKRect.Create(0, 0, 12, 24));
        canvas.SetMatrix(ShimSkiaSharp.SKMatrix.CreateTranslation(3f, 3f));
        canvas.Restore();
        canvas.DrawPath(Square(18), s_red);
        var model = recorder.EndRecording();

        using var runtime = new SkiaModel(new SKSvgSettings()).ToSKPicture(model);
        using var generated = Generated(model, "RestoreAtBase", SkiaSharpTarget.V4, SvgExpressionDeclarations.Empty, Array.Empty<object?>());
        AssertSamePicture("RestoreAtBase", runtime!, generated, BakedThreshold);
        AssertDrawsDirectlyTheSame("RestoreAtBase", model, runtime!, SvgExpressionDeclarations.Empty, Array.Empty<object?>());
    }

    [Theory]
    [InlineData("close")]
    [InlineData("empty polygon")]
    public void A_Baked_Path_Opening_With_Nothing_Starts_At_The_Moved_Origin(string opening)
    {
        // Neither adds a point, so the line after it still starts at Skia's origin, which baked has
        // to be the translation; and a close first must not become a contour of its own.
        var path = new ShimSkiaSharp.SKPath();
        if (opening == "close")
        {
            path.Close();
        }
        else
        {
            path.AddPoly(Array.Empty<ShimSkiaSharp.SKPoint>(), close: true);
        }

        path.LineTo(20f, 0f);
        path.LineTo(20f, 20f);
        var recorder = new ShimSkiaSharp.SKPictureRecorder();
        var canvas = recorder.BeginRecording(ShimSkiaSharp.SKRect.Create(0, 0, 40, 40));
        canvas.Save();
        canvas.SetMatrix(ShimSkiaSharp.SKMatrix.CreateTranslation(10f, 10f));
        canvas.DrawPath(path, new ShimSkiaSharp.SKPaint { Style = ShimSkiaSharp.SKPaintStyle.Stroke, StrokeWidth = 3f, StrokeCap = ShimSkiaSharp.SKStrokeCap.Square, IsAntialias = true, Color = new ShimSkiaSharp.SKColor(0x1e, 0x40, 0xaf, 0xff) });
        canvas.Restore();
        var model = recorder.EndRecording();
        var name = opening == "close" ? "ClosedFirst" : "EmptyPolygonFirst";

        using var runtime = new SkiaModel(new SKSvgSettings()).ToSKPicture(model);
        using var generated = Generated(model, name, SkiaSharpTarget.V4, SvgExpressionDeclarations.Empty, Array.Empty<object?>());
        AssertSamePicture(name, runtime!, generated, BakedThreshold);

        // Skia draws no cap on a move and a close, so only the verbs show a contour added before the close.
        if (opening == "close")
        {
            Assert.Contains("new byte[] { 4, 0, 1, 1 }", SkiaCSharpCodeGen.Generate(model, Namespace, name));
        }
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void A_Width_Skia_Refuses_Leaves_The_Shared_Paint_At_A_New_Ones(float width)
    {
        // Skia keeps what a paint holds when a width is negative or NaN; a new paint holds 0, a
        // hairline, which is what the interpreter draws, and the 4 before it must not carry over.
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="tint" type="color" default="#1e40af" /><e:param name="w" type="number" default="1" /></e:code></defs>
              <path d="M 2 4 L 22 4" fill="none" stroke="{{ tint }}" stroke-width="4" />
              <path d="M 2 14 L 22 14" fill="none" stroke="{{ tint }}" stroke-width="{{ w }}" />
            </svg>
            """;
        var model = Model(svg);
        var declarations = SvgExpressionDeclarations.Parse(svg);
        var arguments = new object?[] { new SKColor(0x1e, 0x40, 0xaf), width };

        var evaluated = SvgSceneExpressionEvaluator.Evaluate(model, declarations, Bind(declarations, arguments));
        using var expected = new SkiaModel(new SKSvgSettings()).ToSKPicture(evaluated);

        AssertDrawsDirectlyTheSame($"RefusedWidth{(float.IsNaN(width) ? "NaN" : "Negative")}", model, expected!, declarations, arguments);
    }

    [Fact]
    public void A_Perspective_Is_Not_Taken_For_The_Identity()
    {
        // The shim's IsIdentity compares the affine part alone.
        var perspective = new ShimSkiaSharp.SKMatrix { ScaleX = 1, ScaleY = 1, Persp0 = 0.01f, Persp2 = 1 };
        var clip = new ShimSkiaSharp.ClipPath
        {
            Clips = new List<ShimSkiaSharp.PathClip> { new() { Path = Square(12), Transform = perspective } }
        };
        var model = new ShimPicture(ShimSkiaSharp.SKRect.Create(0, 0, 24, 24), new List<ShimSkiaSharp.CanvasCommand>
        {
            new ShimSkiaSharp.SaveCanvasCommand(1),
            new ShimSkiaSharp.ClipPathCanvasCommand(clip, ShimSkiaSharp.SKClipOperation.Intersect, true),
            new ShimSkiaSharp.SetMatrixCanvasCommand(perspective, perspective),
            new ShimSkiaSharp.DrawPathCanvasCommand(Square(24), s_red),
            new ShimSkiaSharp.RestoreCanvasCommand(0)
        });

        using var runtime = new SkiaModel(new SKSvgSettings()).ToSKPicture(model);
        using var generated = Generated(model, "Perspective", SkiaSharpTarget.V4, SvgExpressionDeclarations.Empty, Array.Empty<object?>());
        AssertSamePicture("Perspective", runtime!, generated);
        AssertDrawsDirectlyTheSame("Perspective", model, runtime!, SvgExpressionDeclarations.Empty, Array.Empty<object?>());
    }

    [Fact]
    public void A_Baked_Path_Opening_Without_A_Move_Starts_Where_The_Translation_Puts_The_Origin()
    {
        // Skia starts such a contour at the origin, which under the translation was (10, 10).
        var path = new ShimSkiaSharp.SKPath();
        path.LineTo(20f, 0f);
        path.LineTo(20f, 20f);
        var recorder = new ShimSkiaSharp.SKPictureRecorder();
        var canvas = recorder.BeginRecording(ShimSkiaSharp.SKRect.Create(0, 0, 40, 40));
        canvas.Save();
        canvas.SetMatrix(ShimSkiaSharp.SKMatrix.CreateTranslation(10f, 10f));
        canvas.DrawPath(path, new ShimSkiaSharp.SKPaint { Style = ShimSkiaSharp.SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true, Color = new ShimSkiaSharp.SKColor(0x1e, 0x40, 0xaf, 0xff) });
        canvas.Restore();
        var model = recorder.EndRecording();

        using var runtime = new SkiaModel(new SKSvgSettings()).ToSKPicture(model);
        using var generated = Generated(model, "MovelessPath", SkiaSharpTarget.V4, SvgExpressionDeclarations.Empty, Array.Empty<object?>());
        AssertSamePicture("MovelessPath", runtime!, generated, BakedThreshold);
        AssertDrawsDirectlyTheSame("MovelessPath", model, runtime!, SvgExpressionDeclarations.Empty, Array.Empty<object?>());
    }

    [Fact]
    public void A_Direct_Draw_Of_Driven_Paints_Allocates_Nothing()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="tint" type="color" default="#1e40af" /></e:code></defs>
              <g transform="translate(2 3)">
                <path d="M 0 0 L 18 0" stroke="{{ tint }}" stroke-width="2" />
                <path d="M 0 6 L 18 6" stroke="{{ tint }}" stroke-width="1" />
              </g>
              <circle cx="12" cy="16" r="4" fill="{{ tint }}" />
            </svg>
            """;
        var code = SkiaCSharpCodeGen.GenerateFile(
            new[] { new SkiaCSharpDrawing(Model(svg), Namespace, "Unallocated", SvgExpressionDeclarations.Parse(svg)) },
            SvgHelperScope.PerClass,
            style: new SvgCodeStyle { Body = SvgBody.Direct });
        var draw = Compile(code, "Unallocated").GetType($"{Namespace}.Unallocated")!
            .GetMethod("Draw")!
            .CreateDelegate<Action<SKCanvas, SKColor?>>();

        using var bitmap = new SKBitmap(24, 24);
        using var canvas = new SKCanvas(bitmap);
        draw(canvas, new SKColor(0x1e, 0x40, 0xaf));

        var before = GC.GetAllocatedBytesForCurrentThread();
        draw(canvas, new SKColor(0xdc, 0x26, 0x26));

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

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

    [Fact]
    public void A_Non_Scaling_Stroke_Under_A_Transform()
        // The one place a body reads the canvas matrix and resets it, which drawn directly has to
        // mean the caller's matrix rather than identity.
        => AssertRendersTheSame("NonScalingStroke", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <g transform="translate(2 3) scale(2 1.5)">
                <path d="M 1 1 L 9 1 L 9 9 Z" fill="none" stroke="#1e40af" stroke-width="1.5" vector-effect="non-scaling-stroke" />
              </g>
            </svg>
            """);

    [Fact]
    public void A_Pattern_Fill_Draws_Directly_As_It_Records()
    {
        // A picture inside a paint, which stays recorded however the drawing around it is drawn.
        // Against the recorded body, because that already differs from the runtime's (RMS 0.0027).
        var model = Model("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <pattern id="dots" width="6" height="6" patternUnits="userSpaceOnUse">
                  <circle cx="3" cy="3" r="2" fill="#f97316" />
                </pattern>
              </defs>
              <rect x="2" y="2" width="20" height="20" fill="url(#dots)" />
            </svg>
            """);

        using var recorded = Generated(model, "Pattern", SkiaSharpTarget.V4, SvgExpressionDeclarations.Empty, Array.Empty<object?>(), bake: false);

        AssertDrawsDirectlyTheSame("Pattern", model, recorded, SvgExpressionDeclarations.Empty, Array.Empty<object?>());
    }

    [Fact]
    public void A_Typeface_Template_Draws_The_Typeface_It_Names()
    {
        // One file for both sides. A family looked up by name resolves differently in the runtime's
        // typeface providers and in a font manager: the same text missed by RMS 0.06 on Linux and
        // 0.003 on Windows while matching on macOS.
        var font = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "..", "externals", "resvg", "crates", "resvg", "tests", "fonts", "NotoSans-Regular.ttf"));
        var settings = new SKSvgSettings();
        settings.TypefaceProviders!.Insert(0, new Svg.Skia.TypefaceProviders.CustomTypefaceProvider(font));

        var document = SvgService.FromSvg("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 24" width="48" height="24">
              <text x="4" y="18" font-family="Noto Sans" font-size="14" fill="#1e40af">Ab7</text>
            </svg>
            """);
        Assert.NotNull(document);

        var model = SvgSceneRuntime.CreateModel(document!, new SkiaSvgAssetLoader(new SkiaModel(settings)));
        Assert.NotNull(model);

        // Or both sides fall back to whatever the machine has, and match only where they agree on it.
        Assert.True(File.Exists(font), font);
        Assert.Equal("Noto Sans", Assert.Single(Families(model!).Distinct()));

        static IEnumerable<string> Families(ShimPicture picture)
            => (picture.Commands ?? Enumerable.Empty<ShimSkiaSharp.CanvasCommand>()).SelectMany(command => command switch
            {
                ShimSkiaSharp.DrawPictureCanvasCommand { Picture: { } nested } => Families(nested),
                ShimSkiaSharp.DrawPositionedTextRunCanvasCommand run => new[] { run.Font?.Typeface?.FamilyName ?? run.Paint?.Typeface?.FamilyName },
                ShimSkiaSharp.DrawTextCanvasCommand text => new[] { text.Font?.Typeface?.FamilyName ?? text.Paint?.Typeface?.FamilyName },
                _ => Array.Empty<string?>()
            }).OfType<string>();

        using var runtime = new SkiaModel(settings).ToSKPicture(model!);
        Assert.NotNull(runtime);

        var template = $"SKTypeface.FromFile(@\"{font}\")";
        var code = SkiaCSharpCodeGen.GenerateFile(
            new[] { new SkiaCSharpDrawing(model!, Namespace, "TypefaceTemplate", SvgExpressionDeclarations.Empty) },
            SvgHelperScope.PerClass,
            style: new SvgCodeStyle { Typeface = template });

        Assert.Contains("SKTypeface.FromFile(", code);
        Assert.DoesNotContain("SKFontManager", code);

        var type = Compile(code, "TypefaceTemplate").GetType($"{Namespace}.TypefaceTemplate")!;
        using var generated = (SKPicture)type.GetMethod("Record", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;

        AssertSamePicture("TypefaceTemplate", runtime!, generated);
        AssertDrawsDirectlyTheSame("TypefaceTemplateDirect", model!, runtime!, SvgExpressionDeclarations.Empty, Array.Empty<object?>(), typeface: template);
    }

    /// <summary>What a PaintCode host keeps beside the API, which the generated code only names.</summary>
    private const string PaintCodeHost = """
        namespace PaintCode
        {
            public enum ResizingBehavior { AspectFit, AspectFill, Stretch, Center }
            public class Context { }
        }
        """;

    private const string FacadeTank = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="30" height="30">
          <defs><e:code>
            <e:param name="level" type="number" default="1" />
            <e:param name="state" type="boolean" default="true" />
            <e:param name="accentColorOn" type="color" default="#f00" />
          </e:code></defs>
          <circle cx="15" cy="15" r="13" fill="none" stroke="#1e40af" stroke-width="2" />
          <rect x="6" y="{{ 24 - level * 18 }}" width="18" height="{{ level * 18 }}" fill="{{ state ? accentColorOn : '#888888' }}" />
        </svg>
        """;

    /// <summary>
    /// The facade drawn into <paramref name="frame"/>: by the plain overload when it is null, by the
    /// framed one with <c>Stretch</c> otherwise.
    /// </summary>
    private static SKPicture DrawnThroughTheApi(SKRect cull, SKRect? frame, object?[] arguments, out SKRect box)
    {
        var model = Model(FacadeTank);
        var drawing = new SkiaCSharpDrawing(
            model,
            Namespace,
            "FacadeTank",
            SvgExpressionDeclarations.Parse(FacadeTank),
            bounds: new[] { ("LevelRect", new ShimSkiaSharp.SKRect(6, 6, 24, 24)) })
        {
            Method = "drawTanklevel"
        };

        var code = SkiaCSharpCodeGen.GenerateFile(
            new[] { drawing },
            style: new SvgCodeStyle
            {
                Api = "PaintCodeResources.VectorIconsResource",
                Resizing = "PaintCode.ResizingBehavior",
                Order = new[] { "accentColorOn", "state", "level" }
            });

        var assembly = Compile(code, "Facade", PaintCodeHost);
        var api = assembly.GetType("PaintCodeResources.VectorIconsResource")!;
        var stretch = Enum.Parse(assembly.GetType("PaintCode.ResizingBehavior")!, "Stretch");

        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(cull);

        var method = api.GetMethods().Single(m => m.Name == "drawTanklevel" && m.GetParameters().Length == (frame is null ? 5 : 7));
        var call = new object?[] { canvas }
            .Concat(frame is { } target ? new object?[] { target, stretch } : Array.Empty<object?>())
            .Concat(arguments)
            .Append(null)
            .ToArray();

        method.Invoke(null, call);
        box = (SKRect)call[^1]!;

        return recorder.EndRecording();
    }

    private static SKPicture RuntimeTank(SKRect cull, float scale, SKColor accent, bool state, float level)
    {
        var model = Model(FacadeTank);
        var declarations = SvgExpressionDeclarations.Parse(FacadeTank);
        var evaluated = SvgSceneExpressionEvaluator.Evaluate(
            model,
            declarations,
            new Dictionary<string, ExprValue>(StringComparer.Ordinal)
            {
                ["accentColorOn"] = ExprValue.Color(accent.Red, accent.Green, accent.Blue, accent.Alpha),
                ["state"] = ExprValue.Boolean(state),
                ["level"] = ExprValue.Number(level)
            });

        using var drawn = new SkiaModel(new SKSvgSettings()).ToSKPicture(evaluated);
        Assert.NotNull(drawn);

        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(cull);
        canvas.Scale(scale);
        canvas.DrawPicture(drawn);

        return recorder.EndRecording();
    }

    [Fact]
    public void The_Api_Draws_What_The_Drawing_Draws_From_Positional_Arguments()
    {
        var cull = new SKRect(0, 0, 30, 30);
        var accent = new SKColor(0x22, 0xc5, 0x5e);

        using var runtime = RuntimeTank(cull, 1f, accent, true, 0.4f);
        using var generated = DrawnThroughTheApi(cull, null, new object?[] { accent, true, 0.4f }, out var box);

        AssertSamePicture("FacadePlain", runtime, generated);
        Assert.Equal(new SKRect(6, 6, 24, 24), box);
    }

    [Fact]
    public void The_Framed_Overload_Stretches_The_Drawing_Into_The_Target()
    {
        var cull = new SKRect(0, 0, 60, 60);
        var accent = new SKColor(0xf9, 0x73, 0x16);

        using var runtime = RuntimeTank(cull, 2f, accent, false, 0.75f);
        using var generated = DrawnThroughTheApi(cull, cull, new object?[] { accent, false, 0.75f }, out _);

        AssertSamePicture("FacadeFramed", runtime, generated);
    }
}
