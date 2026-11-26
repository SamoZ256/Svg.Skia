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
            ExprType.Integer => Value.AsInteger,
            ExprType.Color => new SKColor(Value.Red, Value.Green, Value.Blue, Value.Alpha),
            ExprType.Boolean => Value.AsBoolean,
            ExprType.String => Value.AsString,

            // An array, as a host might pass one; a List<T> is the other common shape and takes the
            // same path through IReadOnlyList.
            ExprType.NumberList => Value.AsNumberList.ToArray(),
            ExprType.ColorList => Value.AsColorList.Select(c => new SKColor(c.Red, c.Green, c.Blue, c.Alpha)).ToList(),
            _ => throw new NotSupportedException($"Unsupported {nameof(ExprType)}: {Value.Type}.")
        };
    }

    private static Argument Number(string name, float value) => new(name, ExprValue.Number(value));

    private static Argument Colour(string name, byte r, byte g, byte b, byte a)
        => new(name, ExprValue.Color(r, g, b, a));

    private static Argument Boolean(string name, bool value) => new(name, ExprValue.Boolean(value));

    private static Argument Text(string name, string value) => new(name, ExprValue.String(value));

    private static Argument Integer(string name, int value) => new(name, ExprValue.Integer(value));

    private static Argument Numbers(string name, params float[] values) => new(name, ExprValue.NumberList(values));

    private static Argument Colours(string name, params uint[] packed)
        => new(name, ExprValue.ColorList(packed.Select(c => ExprValue.Color((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c))));

    /// <summary>The fields a compiler registered on the class its code lands in, as that class declares them.</summary>
    private static void AppendMembers(StringBuilder source, SkiaCSharpCodeGenCounter? members)
    {
        if (members is null)
        {
            return;
        }

        foreach (var (type, name, value) in members.StaticFields)
        {
            source.AppendLine($"    private static readonly {type} s_{name} = {value};");
        }

        foreach (var (type, name) in members.ThreadStatics)
        {
            source.AppendLine("    [ThreadStatic]");
            source.AppendLine($"    private static {type} s_{name};");
        }
    }

    /// <summary>Compiles <paramref name="code"/> into a method and invokes it.</summary>
    /// <remarks>
    /// Helper bodies are emitted unconditionally rather than selected by scanning the text, so one
    /// the generator would have failed to select is still exercised here.
    /// </remarks>
    private static object Compiled(string code, ExprType resultType, IReadOnlyList<Argument> arguments, SkiaCSharpCodeGenCounter? members)
    {
        var parameters = string.Join(
            ", ",
            arguments.Select(a => $"{ExprCompiler.CSharpTypeOf(a.Type)} {a.Name}"));

        var source = new StringBuilder();
        source.AppendLine("using System;");
        source.AppendLine("using System.Collections.Generic;");
        source.AppendLine("using SkiaSharp;");
        source.AppendLine();
        source.AppendLine($"namespace {Namespace};");
        source.AppendLine();
        source.AppendLine("public static class Probe");
        source.AppendLine("{");
        AppendMembers(source, members);
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
    /// The whole point: evaluate, emit and run, and require the two to agree exactly — once as
    /// written, and once folded, which is the only thing showing folding never changes an answer.
    /// </summary>
    private static void AssertSameValue(string expression, params Argument[] arguments)
    {
        AssertSameValue(expression, fold: false, arguments);
        AssertSameValue(expression, fold: true, arguments);
    }

    /// <param name="inClass">
    /// Whether the code lands in a class that keeps its list literals' arrays, as the generator's
    /// does, or is written by the public compiler, which has none.
    /// </param>
    private static void AssertSameValue(string expression, bool fold, Argument[] arguments, bool inClass = true)
    {
        var symbols = arguments.ToDictionary(a => a.Name, a => a.Type, StringComparer.Ordinal);
        var values = arguments.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal);

        var members = inClass ? new SkiaCSharpCodeGenCounter() : null;
        var (type, code) = (inClass ? new ExprCompiler(symbols, null, fold, members) : new ExprCompiler(symbols, null, fold)).Compile(expression);
        var evaluated = new ExprEvaluator(symbols, values).Evaluate(expression);

        Assert.Equal(type, evaluated.Type);

        AssertSame(type, Compiled(code, type, arguments, members), evaluated);
    }

    private static void AssertSame(ExprType type, object compiled, ExprValue evaluated)
    {
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

            case ExprType.Integer:
                Assert.Equal((int)compiled, evaluated.AsInteger);
                break;

            case ExprType.Boolean:
                Assert.Equal((bool)compiled, evaluated.AsBoolean);
                break;

            case ExprType.String:
                Assert.Equal((string)compiled, evaluated.AsString, StringComparer.Ordinal);
                break;

            // Element by element, each held to what a lone value of its type is held to.
            case ExprType.NumberList:
                {
                    var expected = (IReadOnlyList<float>)compiled;

                    Assert.Equal(expected.Count, evaluated.AsNumberList.Count);

                    for (var i = 0; i < expected.Count; i++)
                    {
                        AssertSame(ExprType.Number, expected[i], ExprValue.Number(evaluated.AsNumberList[i]));
                    }

                    break;
                }

            case ExprType.ColorList:
                {
                    var expected = (IReadOnlyList<SKColor>)compiled;

                    Assert.Equal(expected.Count, evaluated.AsColorList.Count);

                    for (var i = 0; i < expected.Count; i++)
                    {
                        AssertSame(ExprType.Color, expected[i], evaluated.AsColorList[i]);
                    }

                    break;
                }

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
    // Folded, a negative zero comes back as a literal and has to stay one.
    [InlineData("-0")]
    [InlineData("1 / -0")]
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

    // Written out here rather than derived from SkiaSharp's own round trip, so the two have to be
    // shown to agree: grey, primaries, the sector boundaries where the hue formula changes branch,
    // and both ends of the clamp.
    [InlineData("withSaturation(#ff8800, 0.5)")]
    [InlineData("withSaturation(#ff8800, 0)")]
    [InlineData("withSaturation(#ff8800, 1)")]
    [InlineData("withSaturation(#ff8800, 2)")]
    [InlineData("withSaturation(#ff8800, -1)")]
    [InlineData("withSaturation(#808080, 0.5)")]
    [InlineData("withSaturation(#000000, 0.5)")]
    [InlineData("withSaturation(#ffffff, 0.002)")]
    [InlineData("withSaturation(#00ff00, 0.25)")]
    [InlineData("withSaturation(#0000ff, 0.75)")]
    [InlineData("withSaturation(#00ffff, 0.5)")]
    [InlineData("withSaturation(#ff00ff, 0.5)")]
    [InlineData("withSaturation(#ffff00, 0.5)")]
    [InlineData("withSaturation(#12345680, 0.5)")]
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
        AssertSameValue("theme == 'dark'", Text("theme", "dark"));
        AssertSameValue("theme == 'dark'", Text("theme", "light"));
        AssertSameValue("theme != 'dark' ? #22c55e : #1e40af", Text("theme", "dark"));
        AssertSameValue("on ? theme : 'plain'", Text("theme", "dark"), Boolean("on", true));
        AssertSameValue("'icon-' + theme", Text("theme", "home"));
        AssertSameValue("upper(theme)", Text("theme", "stra\u00dfe"));
        AssertSameValue("lower(theme)", Text("theme", "STRASSE"));
        AssertSameValue("len(theme) * 2", Text("theme", "home"));

        // A whole number has to read as one on both sides, with no point and no trailing zero.
        AssertSameValue("str(t)", Number("t", 100f));
        AssertSameValue("str(t)", Number("t", 7.5f));
        AssertSameValue("str(t)", Number("t", -30f));
        AssertSameValue("'step ' + str(t)", Number("t", 3f));

        // A case fold that differs by culture, which is why both back ends spell it invariant.
        AssertSameValue("upper(theme)", Text("theme", "istanbul"));
        AssertSameValue("lower(theme)", Text("theme", "ISTANBUL"));
    }

    [Fact]
    public void The_Crossings_Between_A_Number_And_An_Integer_Agree()
    {
        AssertSameValue("num(steps)", Integer("steps", 4));
        AssertSameValue("num(steps) / 10", Integer("steps", 4));

        // Above 2^24 a float stops counting by one, so both back ends have to lose the same digit.
        AssertSameValue("num(steps)", Integer("steps", 16777217));

        // Toward zero, not down: -2.5 truncates to -2, which is not floor.
        AssertSameValue("int(t)", Number("t", 2.5f));
        AssertSameValue("int(t)", Number("t", -2.5f));
        AssertSameValue("int(t)", Number("t", 0f));

        // The ends, which a bare cast does not define. NaN included, since sqrt of a negative
        // reaches here through arithmetic nobody wrote deliberately.
        AssertSameValue("int(t)", Number("t", 1e30f));
        AssertSameValue("int(t)", Number("t", -1e30f));
        AssertSameValue("int(t)", Number("t", float.NaN));
        AssertSameValue("int(t)", Number("t", float.PositiveInfinity));
        AssertSameValue("int(t)", Number("t", float.NegativeInfinity));

        AssertSameValue("int(num(steps))", Integer("steps", -7));

        // With nothing bound, so folded these come back as integer literals, int.MinValue among them.
        AssertSameValue("int(7.9) / int(0.2)");
        AssertSameValue("-int(-3000000000)");
        AssertSameValue("abs(int(-3000000000))");
        AssertSameValue("mod(int(-7), int(3))");
    }

    [Fact]
    public void Integer_Arithmetic_Agrees_Including_Where_Csharp_Would_Throw()
    {
        AssertSameValue("steps + 1", Integer("steps", 4));
        AssertSameValue("steps - 10", Integer("steps", 4));
        AssertSameValue("steps * 3", Integer("steps", 4));
        AssertSameValue("-steps", Integer("steps", 4));
        AssertSameValue("(steps + 1) * (steps - 1)", Integer("steps", 7));

        // Toward zero, which is neither floor nor the number path's exact quotient.
        AssertSameValue("steps / 2", Integer("steps", 7));
        AssertSameValue("steps / 2", Integer("steps", -7));

        // Wrapping, not throwing, and the same wrap in both.
        AssertSameValue("steps * steps", Integer("steps", 2147483647));
        AssertSameValue("steps + 1", Integer("steps", 2147483647));

        // The two divisions C# throws on. A render must not fail because a divisor reached zero.
        AssertSameValue("steps / 0", Integer("steps", 1));
        AssertSameValue("steps / 0", Integer("steps", -1));
        AssertSameValue("steps / 0", Integer("steps", 0));
        AssertSameValue("steps / n", Integer("steps", -2147483648), Integer("n", -1));

        // int() saturates an infinity to the same end integer division saturates a zero divisor to.
        AssertSameValue("int(num(steps) / 0) == steps / 0", Integer("steps", 1));

        AssertSameValue("steps == 3", Integer("steps", 3));
        AssertSameValue("steps == 3", Integer("steps", 4));
        AssertSameValue("steps > 3 and steps le 9", Integer("steps", 5));
        AssertSameValue("steps > 0 ? steps : 0", Integer("steps", -4));
    }

    [Fact]
    public void The_Integer_Library_Agrees_Including_Where_Csharp_Would_Throw()
    {
        AssertSameValue("min(steps, 3)", Integer("steps", 7));
        AssertSameValue("max(steps, 3)", Integer("steps", 7));
        AssertSameValue("clamp(steps, 0, 9)", Integer("steps", 42));
        AssertSameValue("abs(steps)", Integer("steps", -7));
        AssertSameValue("mod(steps, 3)", Integer("steps", 7));

        // The remainder's sign follows the dividend in both, as it does for numbers.
        AssertSameValue("mod(steps, 3)", Integer("steps", -7));

        // Math.Abs and % both throw on these; the language answers instead.
        AssertSameValue("abs(steps)", Integer("steps", -2147483648));
        AssertSameValue("mod(steps, 0)", Integer("steps", 7));
        AssertSameValue("mod(steps, n)", Integer("steps", -2147483648), Integer("n", -1));

        // len() is whole now, and str() has an overload for it.
        AssertSameValue("len(theme)", Text("theme", "home"));
        AssertSameValue("len(theme) * 2", Text("theme", "home"));
        AssertSameValue("str(len(theme))", Text("theme", "home"));
        AssertSameValue("str(steps)", Integer("steps", 100));
        AssertSameValue("str(steps)", Integer("steps", -30));
        AssertSameValue("'step ' + str(steps)", Integer("steps", 3));
    }

    [Theory]
    // Every escape the language has, so the C# literal the emitter writes has to mean the same
    // thing as the value the lexer resolved.
    [InlineData(@"'a\\b'")]
    [InlineData(@"'a\nb'")]
    [InlineData(@"'a\tb'")]
    [InlineData(@"'it\'s'")]
    [InlineData("'quote\"inside'")]
    [InlineData("'caf\u00e9 \u2014 \u4e2d'")]
    [InlineData("''")]
    public void A_String_Expression_Evaluates_To_What_The_Generated_Code_Computes(string expression)
        => AssertSameValue(expression);

    [Theory]
    [InlineData(-2147483648)]
    [InlineData(-5)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(100)]
    [InlineData(2147483647)]
    public void An_Index_Is_Clamped_The_Same_Way_In_Both(int index)
    {
        var i = Integer("i", index);
        var xs = Numbers("xs", 10f, 20.5f, -30f);
        var cs = Colours("cs", 0x1e40afff, 0xdc262680);

        AssertSameValue("xs[i]", xs, i);
        AssertSameValue("cs[i]", cs, i);
        AssertSameValue("xs[i]", Numbers("xs"), i);
        AssertSameValue("cs[i]", Colours("cs"), i);
        AssertSameValue("[1, 2, 3][i]", i);
        AssertSameValue("[t, t * 2][i]", Number("t", 1.5f), i);
        AssertSameValue("[#fff, tint][i]", Colour("tint", 0x1e, 0x40, 0xaf, 0xff), i);
        AssertSameValue("xs[i + 1] - xs[i - 1]", xs, i);
    }

    [Fact]
    public void A_List_Evaluates_To_What_The_Generated_Code_Computes()
    {
        var xs = Numbers("xs", 0f, 0.5f, 1f);
        var ys = Numbers("ys", 4f);
        var cs = Colours("cs", 0xff0000ff, 0x00ff0080, 0x0000ffff);
        var t = Number("t", 0.25f);
        var tint = Colour("tint", 0x1e, 0x40, 0xaf, 0xff);

        // Whole lists, as written and as an argument hands them in.
        AssertSameValue("[1, 2, 3]");
        AssertSameValue("[0, 0.5, 1]");
        AssertSameValue("[t, t * 2, 1]", t);
        AssertSameValue("[pi, tau, sin(1), 1 / 0]");
        AssertSameValue("[#fff, #00000080]");
        AssertSameValue("[#fff, tint, mix(tint, #000, t)]", tint, t);
        AssertSameValue("xs", xs);
        AssertSameValue("cs", cs);
        AssertSameValue("on ? xs : ys", Boolean("on", true), xs, ys);
        AssertSameValue("on ? xs : ys", Boolean("on", false), xs, ys);
        AssertSameValue("on ? xs : []", Boolean("on", false), xs);
        AssertSameValue("on ? [] : cs", Boolean("on", false), cs);
        AssertSameValue("on ? [t] : [1, t]", Boolean("on", false), t);

        // Their lengths.
        AssertSameValue("len(xs)", xs);
        AssertSameValue("len(cs)", cs);
        AssertSameValue("len(xs)", Numbers("xs"));
        AssertSameValue("len([t, 1])", t);
        AssertSameValue("len(on ? xs : [])", Boolean("on", false), xs);
        AssertSameValue("len([#fff]) + len('ab')");

        // Elements in the arithmetic around them.
        AssertSameValue("-xs[1]", xs);
        AssertSameValue("xs[0] + xs[2] * 2", xs);
        AssertSameValue("xs[len(xs) - 1]", xs);
        AssertSameValue("xs[int(t * 10)]", xs, t);
        AssertSameValue("mix(cs[0], cs[2], xs[1])", cs, xs);
        AssertSameValue("withAlpha(cs[1], 1)", cs);
        AssertSameValue("(on ? xs : ys)[1]", Boolean("on", false), xs, ys);
        AssertSameValue("(on ? [] : xs)[0]", Boolean("on", true), xs);
        AssertSameValue("[sin(1), 2][0]");
        AssertSameValue("[xs[2], ys[0]][1]", xs, ys);
        AssertSameValue("str(xs[1])", xs);
        AssertSameValue("xs[0] == 0", xs);
        AssertSameValue("xs[0]", Numbers("xs", float.NaN));
        AssertSameValue("cs[0] == cs[1]", Colours("cs", 0xffffffff, 0xffffffff));
    }

    [Theory]
    [InlineData("len([t, 1])")]
    [InlineData("len(on ? [] : [t])")]
    [InlineData("len([#fff, #000])")]
    [InlineData("[t, 1][1]")]
    [InlineData("num(len([t, 1])) + [t, 2][len([t]) - 1]")]
    public void A_List_The_Public_Compiler_Writes_Out_Is_Read_As_A_List(string expression)
    {
        AssertSameValue(expression, fold: false, new[] { Number("t", 0.25f), Boolean("on", true) }, inClass: false);
        AssertSameValue(expression, fold: true, new[] { Number("t", 0.25f), Boolean("on", true) }, inClass: false);
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
        var expected = CompiledLets(declarations, "faded", Number("t", 0.25f));

        Assert.Equal(ExprType.Color, evaluated.Type);

        Assert.Equal(
            (expected.Red, expected.Green, expected.Blue, expected.Alpha),
            (evaluated.Red, evaluated.Green, evaluated.Blue, evaluated.Alpha));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9)]
    public void A_List_Let_Chain_Evaluates_The_Same_Way_Round(int index)
    {
        var declarations = SvgExpressionDeclarations.Parse("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="10" height="10">
              <defs><e:code>
                <e:param name="t" type="number" default="0.25" />
                <e:param name="i" type="integer" default="0" />
                <e:param name="tints" type="color[]" default="[#1e40af, #dc2626]" />
                <e:let name="stops">[0, t, 1]</e:let>
                <e:let name="fixed">[0.1, 0.9]</e:let>
                <e:let name="ramp">[tints[0], mix(#fff, #000, stops[i]), tints[1]]</e:let>
                <e:let name="none" type="color[]">[]</e:let>
                <e:let name="chosen">len(ramp) > i ? ramp : none</e:let>
              </e:code></defs>
            </svg>
            """);

        var values = new Dictionary<string, ExprValue>(StringComparer.Ordinal) { ["i"] = ExprValue.Integer(index) };
        var evaluated = ExprEvaluator.Create(declarations, values).Evaluate("withAlpha(chosen[i], stops[1] + fixed[1])");
        var expected = CompiledLets(
            declarations,
            "withAlpha(chosen[i], stops[1] + fixed[1])",
            Number("t", 0.25f),
            Integer("i", index),
            Colours("tints", 0x1e40afff, 0xdc2626ff));

        Assert.Equal(
            (expected.Red, expected.Green, expected.Blue, expected.Alpha),
            (evaluated.Red, evaluated.Green, evaluated.Blue, evaluated.Alpha));
    }

    /// <summary>
    /// The lets compiled as the generator compiles them, into a class keeping what their list
    /// literals need, then <paramref name="expression"/> over them as a colour.
    /// </summary>
    private static SKColor CompiledLets(SvgExpressionDeclarations declarations, string expression, params Argument[] arguments)
    {
        // Resolve grows the symbol table as it goes, exactly as the generator drives it, so
        // CompileTo below sees every let.
        var members = new SkiaCSharpCodeGenCounter();
        var (compiler, lets) = declarations.Resolve(members);

        var locals = new StringBuilder();

        // What the body reads in place of a parameter that can arrive as null.
        foreach (var (parameter, local, defaultCode, type) in declarations.ComputedDefaults())
        {
            locals.AppendLine($"        {ExprCompiler.CSharpTypeOf(type)} {local} = {parameter} ?? {defaultCode};");
        }

        foreach (var let in lets)
        {
            locals.AppendLine($"        var {let.Name} = {let.Code};");
        }

        var code = compiler.CompileTo(expression, ExprType.Color, "The expression");

        return CompiledWithLocals(locals.ToString(), code, arguments, members);
    }

    private static SKColor CompiledWithLocals(string locals, string code, IReadOnlyList<Argument> arguments, SkiaCSharpCodeGenCounter members)
    {
        var parameters = string.Join(
            ", ",
            arguments.Select(a => $"{ExprCompiler.CSharpTypeOf(a.Type)} {a.Name}"));

        var source = new StringBuilder();
        source.AppendLine("using System;");
        source.AppendLine("using System.Collections.Generic;");
        source.AppendLine("using SkiaSharp;");
        source.AppendLine();
        source.AppendLine($"namespace {Namespace};");
        source.AppendLine();
        source.AppendLine("public static class Probe");
        source.AppendLine("{");
        AppendMembers(source, members);
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
