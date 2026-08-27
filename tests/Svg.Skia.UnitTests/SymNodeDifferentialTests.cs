// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using ShimSkiaSharp;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Expressions;
using Svg.Expressions;
using Svg.SceneGraph;
using Svg.Skia.UnitTests.Common;
using Xunit;
using SkiaColor = SkiaSharp.SKColor;
using SkiaColorF = SkiaSharp.SKColorF;

namespace Svg.Skia.UnitTests;

/// <summary>
/// The same comparison <c>ExprEvaluatorDifferentialTests</c> makes, one layer up: a
/// <see cref="SymNode"/> emitted as C# and run, against the same node resolved by the runtime
/// rewriter.
/// </summary>
/// <remarks>
/// The expression-level suite cannot see a disagreement introduced above the language. A real one
/// lived there: <c>SvgToColorF</c> divided a channel by <c>255f</c> while
/// <c>ShimSkiaSharp.SKColor</c> multiplies by <c>1 / 255.0f</c>, differing for 126 of the 256 byte
/// values, and it took a one-pixel difference in a demo render to notice.
/// </remarks>
public class SymNodeDifferentialTests
{
    private const string Namespace = "Svg.Skia.UnitTests.SymDifferential";

    private static int s_generation;

    private static readonly IReadOnlyDictionary<string, ExprType> s_symbols =
        new Dictionary<string, ExprType>(StringComparer.Ordinal)
        {
            ["tint"] = ExprType.Color,
            ["fade"] = ExprType.Number
        };

    private static Dictionary<string, ExprValue> Values(byte r, byte g, byte b, byte a, float fade)
        => new(StringComparer.Ordinal)
        {
            ["tint"] = ExprValue.Color(r, g, b, a),
            ["fade"] = ExprValue.Number(fade)
        };

    /// <summary>Emits <paramref name="node"/>, compiles it, and runs it over the same values.</summary>
    private static object Compiled(SymNode node, ExprType expected, bool asColorF, SKColor tint, float fade)
    {
        var compiler = new ExprCompiler(new Dictionary<string, ExprType>(s_symbols, StringComparer.Ordinal));

        string body;
        using (SymCSharpEmitter.UseCompiler(compiler))
        {
            body = asColorF ? SymCSharpEmitter.EmitAsColorF(node) : SymCSharpEmitter.Emit(node, expected);
        }

        var returnType = asColorF ? "SKColorF" : ExprCompiler.CSharpTypeOf(expected);

        var source = new StringBuilder();
        source.AppendLine("using System;");
        source.AppendLine("using SkiaSharp;");
        source.AppendLine();
        source.AppendLine($"namespace {Namespace};");
        source.AppendLine();
        source.AppendLine("public static class Probe");
        source.AppendLine("{");
        source.AppendLine($"    public static {returnType} Run(SKColor tint, float fade)");
        source.AppendLine($"        => {body};");

        foreach (var helper in ExprHelpers.All)
        {
            source.AppendLine();

            foreach (var line in helper.Value)
            {
                source.AppendLine(line.Length == 0 ? string.Empty : "    " + line);
            }
        }

        source.AppendLine("}");

        var assemblyName = $"{Namespace}.{++s_generation}";

        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { CSharpSyntaxTree.ParseText(SourceText.From(source.ToString())) },
            CSharpReferences.All,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);

        if (!result.Success)
        {
            var errors = string.Join(
                Environment.NewLine,
                result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.GetMessage()));

            Assert.Fail($"The emitted C# did not compile:{Environment.NewLine}{errors}{Environment.NewLine}{Environment.NewLine}{source}");
        }

        peStream.Seek(0, SeekOrigin.Begin);

        var assembly = new AssemblyLoadContext(assemblyName, isCollectible: false).LoadFromStream(peStream);
        var run = assembly.GetType($"{Namespace}.Probe")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

        return run.Invoke(null, new object?[] { new SkiaColor(tint.Red, tint.Green, tint.Blue, tint.Alpha), fade })!;
    }

    /// <summary>
    /// Resolves the same node through the public rewriter, by hanging it on a paint's colour.
    /// </summary>
    private static SKColor Evaluated(SymNode node, SKColor tint, float fade)
    {
        var paint = new SKPaint { Color = new SKColor(0x80, 0x80, 0x80, 0xFF).WithExpression(node) };

        var picture = new SKPicture(
            SKRect.Create(0, 0, 8, 8),
            new List<CanvasCommand> { new DrawPathCanvasCommand(new SKPath(), paint) });

        var evaluated = SvgSceneExpressionEvaluator.Evaluate(
            picture,
            new ExprEvaluator(s_symbols, Values(tint.Red, tint.Green, tint.Blue, tint.Alpha, fade)));

        var command = Assert.Single(evaluated!.Commands!.OfType<DrawPathCanvasCommand>());

        return command.Paint!.Color!.Value;
    }

    /// <summary>
    /// The same, reaching the colour through a gradient stop so the <c>SKColorF</c> conversion is the
    /// one under test. This is the path the divide-versus-reciprocal bug lived on.
    /// </summary>
    private static SKColorF EvaluatedAsColorF(SymNode node, SKColor tint, float fade)
    {
        var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0),
            new SKPoint(8, 0),
            new[]
            {
                new SKColorF(0.5f, 0.5f, 0.5f, 1f, node),
                new SKColorF(0f, 0f, 0f, 1f)
            },
            SKColorSpace.Srgb,
            new[] { 0f, 1f },
            SKShaderTileMode.Clamp);

        var picture = new SKPicture(
            SKRect.Create(0, 0, 8, 8),
            new List<CanvasCommand> { new DrawPathCanvasCommand(new SKPath(), new SKPaint { Shader = shader }) });

        var evaluated = SvgSceneExpressionEvaluator.Evaluate(
            picture,
            new ExprEvaluator(s_symbols, Values(tint.Red, tint.Green, tint.Blue, tint.Alpha, fade)));

        var command = Assert.Single(evaluated!.Commands!.OfType<DrawPathCanvasCommand>());
        var gradient = Assert.IsType<LinearGradientShader>(command.Paint!.Shader);

        return gradient.Colors![0];
    }

    private static void AssertSameColor(SymNode node, SKColor tint, float fade = 1f)
    {
        var compiled = (SkiaColor)Compiled(node, ExprType.Color, asColorF: false, tint, fade);
        var evaluated = Evaluated(node, tint, fade);

        Assert.Equal(
            (compiled.Red, compiled.Green, compiled.Blue, compiled.Alpha),
            (evaluated.Red, evaluated.Green, evaluated.Blue, evaluated.Alpha));
    }

    private static void AssertSameColorF(SymNode node, SKColor tint, float fade = 1f)
    {
        var compiled = (SkiaColorF)Compiled(node, ExprType.Color, asColorF: true, tint, fade);
        var evaluated = EvaluatedAsColorF(node, tint, fade);

        // Compared as bits. A float comparison with any tolerance would have let the very bug this
        // file exists for straight through.
        Assert.Equal(BitConverter.SingleToInt32Bits(compiled.Red), BitConverter.SingleToInt32Bits(evaluated.Red));
        Assert.Equal(BitConverter.SingleToInt32Bits(compiled.Green), BitConverter.SingleToInt32Bits(evaluated.Green));
        Assert.Equal(BitConverter.SingleToInt32Bits(compiled.Blue), BitConverter.SingleToInt32Bits(evaluated.Blue));
        Assert.Equal(BitConverter.SingleToInt32Bits(compiled.Alpha), BitConverter.SingleToInt32Bits(evaluated.Alpha));
    }

    /// <summary>Every byte value, since the conversions disagree on 126 of the 256.</summary>
    public static TheoryData<byte> EveryChannelValue()
    {
        var data = new TheoryData<byte>();

        for (var value = 0; value <= 255; value++)
        {
            data.Add((byte)value);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryChannelValue))]
    public void A_Gradient_Stop_Converts_To_ColorF_The_Same_Way_In_Both(byte channel)
        // The regression this file was written for. A plain authored colour, reaching a stop, has to
        // land on the same float whether the code generator converted it or the rewriter did.
        => AssertSameColorF(SymNode.Source("tint"), new SKColor(channel, channel, channel, 255));

    [Fact]
    public void An_Authored_Colour_Resolves_The_Same_Way_In_Both()
        => AssertSameColor(SymNode.Source("tint"), new SKColor(0x3F, 0xB5, 0xB5, 0xFF));

    [Theory]
    [InlineData(0f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    // Unclamped in the helper, so these push the product outside a byte on purpose.
    [InlineData(1.5f)]
    [InlineData(-0.5f)]
    public void Scaling_The_Alpha_Agrees(float factor)
        => AssertSameColor(
            SymNode.ScaleAlpha(SymNode.Source("tint"), SymNode.Literal(factor)),
            new SKColor(0x3F, 0xB5, 0xB5, 0xC0));

    [Theory]
    [MemberData(nameof(EveryChannelValue))]
    public void Converting_To_Linear_Rgb_Agrees(byte channel)
        // Math.Pow on each channel, then a rounded cast. Swept, because a rounding difference would
        // show on some channel values and not others.
        => AssertSameColor(
            SymNode.ToLinearRgb(SymNode.Source("tint")),
            new SKColor(channel, channel, channel, 255));

    [Fact]
    public void The_Opacity_Layer_Shape_Agrees()
        // What the model records for an opacity expression: white scaled by the authored value,
        // where the white is a colour literal written in the authored language.
        => AssertSameColor(
            SymNode.ScaleAlpha(SymNode.Source("#ffffff"), SymNode.Source("fade")),
            new SKColor(0, 0, 0, 255),
            fade: 0.3f);

    [Fact]
    public void A_Scale_Inside_A_Linear_Conversion_Agrees()
        => AssertSameColor(
            SymNode.ToLinearRgb(SymNode.ScaleAlpha(SymNode.Source("tint"), SymNode.Literal(0.25))),
            new SKColor(0x3F, 0xB5, 0xB5, 0xFF));

    [Fact]
    public void A_Scaled_Colour_Reaching_A_Stop_Agrees()
        => AssertSameColorF(
            SymNode.ScaleAlpha(SymNode.Source("tint"), SymNode.Literal(0.6)),
            new SKColor(0x3F, 0xB5, 0xB5, 0xFF));
}
