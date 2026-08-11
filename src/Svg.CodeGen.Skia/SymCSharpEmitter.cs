// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Globalization;
using System.Text;
using ShimSkiaSharp;
using Svg.CodeGen.Skia.Expressions;

namespace Svg.CodeGen.Skia;

// Renders a SymNode as a C# expression. This is the only layer that knows the target language;
// the model builds nodes without any notion of syntax.
public static class SymCSharpEmitter
{
    // The compiler for authored expressions is ambient rather than a parameter because the
    // emission entry points are extension methods on model types (SKColor.ToSKColor() and
    // friends) that have no context argument to thread it through.
    [ThreadStatic]
    private static ExprCompiler? t_compiler;

    internal static IDisposable UseCompiler(ExprCompiler compiler)
    {
        var previous = t_compiler;
        t_compiler = compiler;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly ExprCompiler? _previous;

        public Scope(ExprCompiler? previous) => _previous = previous;

        public void Dispose() => t_compiler = _previous;
    }

    public static string Emit(SymNode node) => Emit(node, ExprType.Color);

    /// <summary>
    /// Emits <paramref name="node"/>, requiring any authored expression inside it to produce
    /// <paramref name="expected"/>. Position decides the type: the factor of an alpha scale is a
    /// number even though the surrounding node is a colour.
    /// </summary>
    public static string Emit(SymNode node, ExprType expected)
    {
        var sb = new StringBuilder();
        Emit(node, sb, expected);
        return sb.ToString();
    }

    // Colors reach gradient stops as SKColorF while an authored expression yields an SKColor,
    // so the conversion is explicit rather than relying on an implicit operator.
    public static string EmitAsColorF(SymNode node) => $"{ExprHelpers.ToColorF}({Emit(node)})";

    private static void Emit(SymNode node, StringBuilder sb, ExprType expected)
    {
        switch (node)
        {
            case SymSource source:
                {
                    var compiler = t_compiler
                        ?? throw new InvalidOperationException(
                            "No expression compiler is in scope. Emission must run inside SkiaCSharpCodeGen.Generate.");

                    var what = expected == ExprType.Color ? "A paint expression" : "An opacity expression";
                    sb.Append(compiler.CompileTo(source.Text, expected, what));
                    break;
                }

            case SymLit lit:
                sb.Append(EmitLiteral(lit.Value));
                break;

            case SymUnary { Op: SymOp.Negate } unary:
                sb.Append("(-");
                Emit(unary.Operand, sb, ExprType.Number);
                sb.Append(')');
                break;

            case SymUnary { Op: SymOp.ToLinearRgb } unary:
                // A call, not inline arithmetic, so the operand is evaluated exactly once.
                sb.Append(ExprHelpers.ToLinearRgb).Append('(');
                Emit(unary.Operand, sb, ExprType.Color);
                sb.Append(')');
                break;

            case SymBinary { Op: SymOp.ScaleAlpha } binary:
                sb.Append(ExprHelpers.ScaleAlpha).Append('(');
                Emit(binary.Left, sb, ExprType.Color);
                sb.Append(", ");
                Emit(binary.Right, sb, ExprType.Number);
                sb.Append(')');
                break;

            case SymBinary binary:
                sb.Append('(');
                Emit(binary.Left, sb, ExprType.Number);
                sb.Append(' ').Append(OperatorText(binary.Op)).Append(' ');
                Emit(binary.Right, sb, ExprType.Number);
                sb.Append(')');
                break;

            default:
                throw new NotSupportedException($"Unsupported {nameof(SymNode)}: {node.GetType().Name}.");
        }
    }

    private static string OperatorText(SymOp op)
        => op switch
        {
            SymOp.Add => "+",
            SymOp.Subtract => "-",
            SymOp.Multiply => "*",
            SymOp.Divide => "/",
            _ => throw new NotSupportedException($"Unsupported binary {nameof(SymOp)}: {op}.")
        };

    private static string EmitLiteral(double value)
    {
        // Opacity and friends arrive as float widened to double, so 0.3f shows up as
        // 0.30000001192092896. Round-tripping through float keeps the generated source readable
        // without changing the value the compiler ends up with.
        var single = (float)value;

        if (float.IsNaN(single))
        {
            return "float.NaN";
        }

        if (float.IsPositiveInfinity(single))
        {
            return "float.PositiveInfinity";
        }

        if (float.IsNegativeInfinity(single))
        {
            return "float.NegativeInfinity";
        }

        return single.ToString("R", CultureInfo.InvariantCulture) + "f";
    }
}
