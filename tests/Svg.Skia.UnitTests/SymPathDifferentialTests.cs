// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using ShimSkiaSharp;
using Svg.CodeGen.Skia.Expressions;
using Svg.Skia.UnitTests.Common;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// The helper generated code replays a driven clip shape with, against <see cref="SymPath.Apply"/>,
/// which is what the runtime draws: the same numbers have to come to the same native path through
/// either SkiaSharp's way of building one.
/// </summary>
/// <remarks>
/// The render rows meet a few of these rules; this meets every one, at the edges where the two
/// copies of them could part.
/// </remarks>
public class SymPathDifferentialTests
{
    private const string Namespace = "Svg.Skia.UnitTests.SymPathDifferential";

    private static readonly Lazy<Type> s_probe = new(Compile);

    private static Type Compile()
    {
        var source = new StringBuilder();
        source.AppendLine("using System;");
        source.AppendLine("using SkiaSharp;");
        source.AppendLine($"namespace {Namespace};");
        source.AppendLine("public static class Probe");
        source.AppendLine("{");
        source.AppendLine("    public static string Builder(byte[] ops, float[] p)");
        source.AppendLine("    {");
        source.AppendLine("        using var builder = new SKPathBuilder();");
        source.AppendLine($"        {ExprHelpers.DrivenPathBuilderData}(builder, ops, p);");
        source.AppendLine("        using var path = builder.Detach();");
        source.AppendLine("        return path.ToSvgPathData();");
        source.AppendLine("    }");
        source.AppendLine("    public static string Path(byte[] ops, float[] p)");
        source.AppendLine("    {");
        source.AppendLine("        using var path = new SKPath();");
        source.AppendLine($"        {ExprHelpers.DrivenPathData}(path, ops, p);");
        source.AppendLine("        return path.ToSvgPathData();");
        source.AppendLine("    }");

        foreach (var helper in ExprHelpers.All.Where(helper => helper.Key is ExprHelpers.DrivenPathData or ExprHelpers.DrivenPathBuilderData))
        {
            source.AppendLine();

            foreach (var line in helper.Value)
            {
                source.AppendLine(line.Length == 0 ? string.Empty : "    " + line);
            }
        }

        source.AppendLine("}");

        var compilation = CSharpCompilation.Create(
            Namespace,
            new[] { CSharpSyntaxTree.ParseText(SourceText.From(source.ToString())) },
            CSharpReferences.All,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.GetMessage())) + Environment.NewLine + source);

        peStream.Seek(0, SeekOrigin.Begin);

        return new AssemblyLoadContext(Namespace, isCollectible: false).LoadFromStream(peStream).GetType($"{Namespace}.Probe")!;
    }

    private static string Run(string method, SymPathOp[] ops, float[] arguments)
        => (string)s_probe.Value.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { ops.Select(op => (byte)op).ToArray(), arguments })!;

    private static string Applied(SymPathOp[] ops, float[] arguments)
    {
        var commands = ops.Select(op => new SymPathCommand(op, Enumerable.Repeat(SymNode.Zero, SymPath.Arity(op)).ToArray())).ToArray();
        using var path = new SkiaModel(new SKSvgSettings()).ToSKPath(new SymPath(SKPathFillType.Winding, commands).Apply(arguments));

        return path.ToSvgPathData();
    }

    private const SymPathOp M = SymPathOp.MoveTo;
    private const SymPathOp L = SymPathOp.LineTo;
    private const SymPathOp A = SymPathOp.ArcTo;
    private const SymPathOp Z = SymPathOp.Close;

    public static TheoryData<string, SymPathOp[], float[]> Cases => new()
    {
        { "an arc with no radius", new[] { M, A }, new[] { 0f, 0f, 0f, 5f, 0f, 0f, 0f, 4f, 0f } },
        { "an arc with the least radius", new[] { M, A }, new[] { 0f, 0f, float.Epsilon, 5f, 0f, 0f, 0f, 4f, 0f } },
        { "an arc ending where it starts", new[] { M, L, A }, new[] { 0f, 0f, 3f, 3f, 5f, 5f, 0f, 1f, 1f, 3f, 3f } },
        { "an arc with signed radii and flags", new[] { M, A }, new[] { 0f, 0f, -5f, -3f, 30f, -2f, 0.5f, 8f, 2f } },
        { "an arc after a close", new[] { M, L, Z, A }, new[] { 2f, 2f, 8f, 2f, 4f, 4f, 0f, 0f, 1f, 2f, 2f } },
        { "an arc after a close, away", new[] { M, L, Z, A }, new[] { 2f, 2f, 8f, 2f, 4f, 4f, 0f, 0f, 1f, 6f, 2f } },
        { "curves", new[] { M, SymPathOp.QuadTo, SymPathOp.CubicTo, Z }, new[] { 1f, 1f, 6f, 0f, 9f, 4f, 10f, 9f, 3f, 9f, 1f, 5f } },
        { "a rect borrowing rx", new[] { SymPathOp.Rect }, new[] { 1f, 1f, 10f, 8f, 0f, 3f, 0f, 2.5f } },
        { "a rect borrowing ry", new[] { SymPathOp.Rect }, new[] { 1f, 1f, 10f, 8f, 3f, 0f, 1.5f, 0f } },
        { "a rect with one radius signed", new[] { SymPathOp.Rect }, new[] { 1f, 1f, 10f, 8f, -3f, 2f, -3f, 2f } },
        { "a rect with both signed", new[] { SymPathOp.Rect }, new[] { 1f, 1f, 10f, 8f, -3f, -2f, -3f, -2f } },
        { "a rect clamped", new[] { SymPathOp.Rect }, new[] { 1f, 1f, 10f, 8f, 9f, 9f, 9f, 9f } },
        { "a rect of no width", new[] { SymPathOp.Rect }, new[] { 1f, 1f, 0f, 8f, 2f, 2f, 2f, 2f } },
        { "an ellipse signed", new[] { SymPathOp.Ellipse }, new[] { 5f, 5f, -4f, 3f } },
        { "an ellipse of no radius", new[] { SymPathOp.Ellipse }, new[] { 5f, 5f, 4f, 0f } },
        { "a circle", new[] { SymPathOp.Circle }, new[] { 5f, 5f, 2f } },
        { "a circle of no radius", new[] { SymPathOp.Circle }, new[] { 5f, 5f, -1f } },
        { "a number that is not one", new[] { M, L, L, Z }, new[] { 1f, 1f, float.NaN, 1f, 1f, 9f } }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Generated_Code_Replays_A_Driven_Shape_As_The_Runtime_Applies_It(string because, SymPathOp[] ops, float[] arguments)
    {
        var expected = Applied(ops, arguments);

        Assert.True(expected == Run("Builder", ops, arguments), because);
        Assert.True(expected == Run("Path", ops, arguments), because);
    }
}
