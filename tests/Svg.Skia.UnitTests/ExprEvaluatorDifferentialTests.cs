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
using SkiaSharp;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Expressions;
using Svg.Expressions;
using Svg.Skia.UnitTests.Common;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// Evaluates an expression and, for the same expression and the same arguments, compiles the C# the
/// code generator emits for it and runs that. The two answers have to be identical, bit for bit.
/// </summary>
/// <remarks>
/// What makes a second back end safe: whether <c>MathF.Sin</c> and its evaluated counterpart produce
/// the same float cannot be argued from the source. A rendered-pixel comparison catches this only
/// blurrily — a wrong channel landing on the same byte passes — where a case here fails on the value.
/// </remarks>
public class ExprEvaluatorDifferentialTests
{
    private const string Namespace = "Svg.Skia.UnitTests.Differential";

    private static int s_generation;

    /// <summary>One declared parameter and the value bound to it, in both worlds at once.</summary>
    private sealed record Argument(string Name, ExprValue Value)
    {
        public ExprType Type => Value.Type;

        /// <summary>The value as the argument the compiled method expects.</summary>
        public object Boxed => Value.Type switch
        {
            ExprType.Number => Value.AsNumber,
            ExprType.Color => new SKColor(Value.Red, Value.Green, Value.Blue, Value.Alpha),
            ExprType.Boolean => Value.AsBoolean,
            _ => throw new NotSupportedException($"Unsupported {nameof(ExprType)}: {Value.Type}.")
        };
    }

    private static Argument Number(string name, float value) => new(name, ExprValue.Number(value));

    private static Argument Colour(string name, byte r, byte g, byte b, byte a)
        => new(name, ExprValue.Color(r, g, b, a));

    private static Argument Boolean(string name, bool value) => new(name, ExprValue.Boolean(value));

    /// <summary>Compiles <paramref name="code"/> into a method and invokes it.</summary>
    /// <remarks>
    /// Helper bodies are emitted unconditionally rather than selected by scanning the text, so one
    /// the generator would have failed to select is still exercised here.
    /// </remarks>
    private static object Compiled(string code, ExprType resultType, IReadOnlyList<Argument> arguments)
    {
        var parameters = string.Join(
            ", ",
            arguments.Select(a => $"{ExprCompiler.CSharpTypeOf(a.Type)} {a.Name}"));

        var source = new StringBuilder();
        source.AppendLine("using System;");
        source.AppendLine("using SkiaSharp;");
        source.AppendLine();
        source.AppendLine($"namespace {Namespace};");
        source.AppendLine();
        source.AppendLine("public static class Probe");
        source.AppendLine("{");
        source.AppendLine($"    public static {ExprCompiler.CSharpTypeOf(resultType)} Run({parameters})");
        source.AppendLine($"        => {code};");

        foreach (var helper in ExprHelpers.All)
        {
            source.AppendLine();

            foreach (var line in helper.Value)
            {
                source.AppendLine(line.Length == 0 ? string.Empty : "    " + line);
            }
        }

        source.AppendLine("}");

        var text = source.ToString();
        var assemblyName = $"{Namespace}.{++s_generation}";

        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { CSharpSyntaxTree.ParseText(SourceText.From(text)) },
            CSharpReferences.All,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);

        if (!result.Success)
        {
            var errors = string.Join(
                Environment.NewLine,
                result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => $"{d.Id}: {d.GetMessage()} — {d.Location.GetLineSpan()}"));

            Assert.Fail($"The emitted C# did not compile:{Environment.NewLine}{errors}{Environment.NewLine}{Environment.NewLine}{text}");
        }

        peStream.Seek(0, SeekOrigin.Begin);

        var assembly = new AssemblyLoadContext(assemblyName, isCollectible: false).LoadFromStream(peStream);
        var run = assembly.GetType($"{Namespace}.Probe")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

        return run.Invoke(null, arguments.Select(a => a.Boxed).ToArray())!;
    }

    /// <summary>
    /// The whole point: evaluate, emit and run, and require the two to agree exactly.
    /// </summary>
    private static void AssertSameValue(string expression, params Argument[] arguments)
    {
        var symbols = arguments.ToDictionary(a => a.Name, a => a.Type, StringComparer.Ordinal);
        var values = arguments.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal);

        var (type, code) = new ExprCompiler(symbols).Compile(expression);
        var evaluated = new ExprEvaluator(symbols, values).Evaluate(expression);

        Assert.Equal(type, evaluated.Type);

        var compiled = Compiled(code, type, arguments);

        switch (type)
        {
            case ExprType.Number:
                {
                    var expected = (float)compiled;
                    var actual = evaluated.AsNumber;

                    // Compared as bits, so a NaN mismatch or a signed zero cannot slip through as
                    // "equal enough". Any NaN counts as any other NaN, since the payload is not
                    // something either back end promises.
                    if (float.IsNaN(expected) && float.IsNaN(actual))
                    {
                        return;
                    }

                    Assert.Equal(
                        BitConverter.SingleToInt32Bits(expected),
                        BitConverter.SingleToInt32Bits(actual));
                    break;
                }

            case ExprType.Color:
                {
                    var expected = (SKColor)compiled;

                    Assert.Equal(
                        (expected.Red, expected.Green, expected.Blue, expected.Alpha),
                        (evaluated.Red, evaluated.Green, evaluated.Blue, evaluated.Alpha));
                    break;
                }

            case ExprType.Boolean:
                Assert.Equal((bool)compiled, evaluated.AsBoolean);
                break;

            default:
                throw new NotSupportedException($"Unsupported {nameof(ExprType)}: {type}.");
        }
    }

    [Theory]
    // Literals and the shape of arithmetic.
    [InlineData("1")]
    [InlineData("1 + 2 * 3")]
    [InlineData("(1 + 2) * 3")]
    [InlineData("-4.5")]
    [InlineData("1 / 3")]
    [InlineData("10 / 4")]
    [InlineData("0.1 + 0.2")]
    // A float literal that is not representable, so the narrowing has to happen in both.
    [InlineData("0.30000000000000004")]
    [InlineData("16777217")]
    // Division by zero is an infinity in C#, not an error.
    [InlineData("1 / 0")]
    [InlineData("-1 / 0")]
    [InlineData("0 / 0")]
    // Percent is a literal suffix meaning /100, not an operator.
    [InlineData("50%")]
    [InlineData("12.5% * 2")]
    // Constants, and the parenthesised tau that makes this exactly 1.
    [InlineData("pi")]
    [InlineData("tau")]
    [InlineData("tau / (pi * 2)")]
    [InlineData("pi * 2 - tau")]
    // Every unary and binary numeric function.
    [InlineData("sin(1)")]
    [InlineData("sin(pi)")]
    [InlineData("cos(0.5)")]
    [InlineData("tan(1.2)")]
    [InlineData("abs(-3.5)")]
    [InlineData("sqrt(2)")]
    [InlineData("sqrt(-1)")]
    [InlineData("floor(2.7)")]
    [InlineData("floor(-2.7)")]
    [InlineData("ceil(2.1)")]
    [InlineData("ceil(-2.1)")]
    // Banker's rounding: 2.5 goes to 2, not 3.
    [InlineData("round(2.5)")]
    [InlineData("round(3.5)")]
    [InlineData("round(-2.5)")]
    [InlineData("pow(2, 10)")]
    [InlineData("pow(2, 0.5)")]
    [InlineData("pow(-8, 1 / 3)")]
    [InlineData("min(3, 7)")]
    [InlineData("max(3, 7)")]
    [InlineData("min(0 / 0, 1)")]
    // Remainder, not IEEERemainder: mod(5, 3) is 2, and the sign follows the left operand.
    [InlineData("mod(5, 3)")]
    [InlineData("mod(-5, 3)")]
    [InlineData("mod(5, -3)")]
    [InlineData("mod(5.5, 2)")]
    [InlineData("clamp(5, 0, 1)")]
    [InlineData("clamp(-5, 0, 1)")]
    [InlineData("clamp(0.5, 0, 1)")]
    // Lerp does not clamp, so t outside the unit interval extrapolates.
    [InlineData("lerp(0, 10, 0.25)")]
    [InlineData("lerp(0, 10, 2)")]
    [InlineData("lerp(0, 10, -1)")]
    public void A_Number_Expression_Evaluates_To_What_The_Generated_Code_Computes(string expression)
        => AssertSameValue(expression);

    [Theory]
    // Colour literals in every accepted width.
    [InlineData("#fff")]
    [InlineData("#ffff")]
    [InlineData("#ff8800")]
    [InlineData("#ff880080")]
    // Construction, including the clamping and the rounding of each channel.
    [InlineData("rgb(255, 0, 0)")]
    [InlineData("rgb(300, -20, 12.5)")]
    [InlineData("rgb(0.5, 1.5, 2.5)")]
    [InlineData("rgba(255, 0, 0, 0.5)")]
    [InlineData("rgba(255, 0, 0, 2)")]
    [InlineData("rgba(255, 0, 0, -1)")]
    // hsl is the one that has to match SkiaSharp's own truncating conversion.
    [InlineData("hsl(0, 1, 0.5)")]
    [InlineData("hsl(210, 0.5, 0.5)")]
    [InlineData("hsl(120, 0, 0.05)")]
    [InlineData("hsl(-30, 0.5, 0.5)")]
    [InlineData("hsl(720, 0.5, 0.5)")]
    [InlineData("hsl(37.5, 0.74, 0.55)")]
    [InlineData("hsl(200, 2, -1)")]
    [InlineData("hsla(210, 0.5, 0.5, 0.25)")]
    [InlineData("mix(#000, #fff, 0.5)")]
    [InlineData("mix(#000, #fff, 0.333)")]
    [InlineData("mix(#ff0000, #0000ff, 1.5)")]
    [InlineData("mix(#ff000080, #0000ffff, 0.5)")]
    [InlineData("withAlpha(#ff8800, 0.5)")]
    [InlineData("withAlpha(#ff8800, 1)")]
    [InlineData("withAlpha(#ff8800, 3)")]
    public void A_Colour_Expression_Evaluates_To_What_The_Generated_Code_Computes(string expression)
        => AssertSameValue(expression);

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("!true")]
    [InlineData("2 > 1")]
    [InlineData("2 gt 1")]
    [InlineData("1 >= 1")]
    [InlineData("1 < 0")]
    [InlineData("1 le 1")]
    [InlineData("1 == 1")]
    [InlineData("1 != 1")]
    [InlineData("#fff == #ffffffff")]
    [InlineData("#fff != #000")]
    [InlineData("true == false")]
    // NaN equals nothing, itself included, which is C# and not .NET Equals.
    [InlineData("(0 / 0) == (0 / 0)")]
    [InlineData("(0 / 0) != (0 / 0)")]
    [InlineData("true and false")]
    [InlineData("true or false")]
    [InlineData("2 gt 1 and !false")]
    // clamp with a reversed range throws, which is the only way to tell: a right operand producing
    // a merely wrong value is swallowed by && and || anyway.
    [InlineData("false and clamp(0, 1, 0) > 0")]
    [InlineData("true or clamp(0, 1, 0) > 0")]
    public void A_Boolean_Expression_Evaluates_To_What_The_Generated_Code_Computes(string expression)
        => AssertSameValue(expression);

    [Fact]
    public void Parameters_Reach_Both_Back_Ends_Identically()
    {
        AssertSameValue("t * 2 + 1", Number("t", 3.25f));
        AssertSameValue("sin(t * tau)", Number("t", 0.125f));
        AssertSameValue("withAlpha(tint, fade)", Colour("tint", 255, 136, 0, 255), Number("fade", 0.5f));
        AssertSameValue("mix(a, b, k)", Colour("a", 0, 0, 0, 255), Colour("b", 255, 255, 255, 255), Number("k", 0.4f));
        AssertSameValue("hot ? #22c55e : #1e40af", Boolean("hot", true));
        AssertSameValue("hot ? #22c55e : #1e40af", Boolean("hot", false));
        AssertSameValue("hsl(hue, 0.74, 0.55)", Number("hue", 37.5f));
    }

    [Fact]
    public void Only_The_Taken_Branch_Of_A_Conditional_Is_Evaluated()
    {
        // clamp throws when its range is reversed, so an eagerly evaluated branch would surface as
        // an exception rather than a wrong value. Both back ends have to skip it.
        AssertSameValue("take ? 1 : clamp(0, 1, 0)", Boolean("take", true));
        AssertSameValue("take ? clamp(0, 1, 0) : 1", Boolean("take", false));
    }

    [Fact]
    public void A_Let_Chain_Evaluates_The_Same_Way_Round()
    {
        // Not AssertSameValue, because lets are declarations rather than one expression: this walks
        // the same growing symbol table both back ends use, and compares the final value.
        var declarations = SvgExpressionDeclarations.Parse("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="10" height="10">
              <defs><e:code>
                <e:param name="t" type="number" default="0.25" />
                <e:let name="wave">(sin(t * tau) + 1) / 2</e:let>
                <e:let name="tone">hsl(200 + wave * 60, 0.6, 0.4 + wave * 0.2)</e:let>
                <e:let name="faded">withAlpha(tone, wave)</e:let>
              </e:code></defs>
            </svg>
            """);

        var evaluated = ExprEvaluator.Create(declarations).Evaluate("faded");

        // Resolve() grows the symbol table as it goes, exactly as the generator drives it, so
        // CompileTo below sees every let.
        var (compiler, lets) = declarations.Resolve();

        var locals = new StringBuilder();
        foreach (var let in lets)
        {
            locals.AppendLine($"        var {let.Name} = {let.Code};");
        }

        var arguments = new[] { Number("t", 0.25f) };
        var code = compiler.CompileTo("faded", ExprType.Color, "The expression");
        var expected = CompiledWithLocals(locals.ToString(), code, arguments);

        Assert.Equal(ExprType.Color, evaluated.Type);

        Assert.Equal(
            (expected.Red, expected.Green, expected.Blue, expected.Alpha),
            (evaluated.Red, evaluated.Green, evaluated.Blue, evaluated.Alpha));
    }

    private static SKColor CompiledWithLocals(string locals, string code, IReadOnlyList<Argument> arguments)
    {
        var parameters = string.Join(
            ", ",
            arguments.Select(a => $"{ExprCompiler.CSharpTypeOf(a.Type)} {a.Name}"));

        var source = new StringBuilder();
        source.AppendLine("using System;");
        source.AppendLine("using SkiaSharp;");
        source.AppendLine();
        source.AppendLine($"namespace {Namespace};");
        source.AppendLine();
        source.AppendLine("public static class Probe");
        source.AppendLine("{");
        source.AppendLine($"    public static SKColor Run({parameters})");
        source.AppendLine("    {");
        source.Append(locals);
        source.AppendLine($"        return {code};");
        source.AppendLine("    }");

        foreach (var helper in ExprHelpers.All)
        {
            source.AppendLine();

            foreach (var line in helper.Value)
            {
                source.AppendLine(line.Length == 0 ? string.Empty : "    " + line);
            }
        }

        source.AppendLine("}");

        var assemblyName = $"{Namespace}.Locals.{++s_generation}";

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
                result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => $"{d.Id}: {d.GetMessage()} — {d.Location.GetLineSpan()}"));

            Assert.Fail($"The emitted C# did not compile:{Environment.NewLine}{errors}{Environment.NewLine}{Environment.NewLine}{source}");
        }

        peStream.Seek(0, SeekOrigin.Begin);

        var assembly = new AssemblyLoadContext(assemblyName, isCollectible: false).LoadFromStream(peStream);
        var run = assembly.GetType($"{Namespace}.Probe")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

        return (SKColor)run.Invoke(null, arguments.Select(a => a.Boxed).ToArray())!;
    }
}
