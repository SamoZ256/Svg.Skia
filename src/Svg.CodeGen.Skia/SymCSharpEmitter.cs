// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Text;
using ShimSkiaSharp;
using Svg.CodeGen.Skia.Expressions;
using Svg.Expressions;

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
        if (node is SymUnary or SymBinary && TryConstant(node, expected, out var constant))
        {
            sb.Append(ExprCSharpBackend.Emit(ExprFolder.Literal(constant, 0)));
            return;
        }

        switch (node)
        {
            case SymSource source:
                {
                    var compiler = t_compiler
                        ?? throw new InvalidOperationException(
                            "No expression compiler is in scope. Emission must run inside SkiaCSharpCodeGen.Generate.");

                    var what = ExprFunctions.DescribeUse(expected);
                    sb.Append(compiler.CompileTo(source.Text, expected, what));
                    break;
                }

            case SymLit lit:
                sb.Append(ExprCSharpBackend.Literal(lit.Value));
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

    /// <summary>
    /// The value <paramref name="node"/> comes to without anything bound, when the compiler in scope
    /// folds.
    /// </summary>
    /// <remarks>
    /// The walk <c>SvgSceneSymEvaluator.Evaluate</c> makes at run time, which this assembly cannot
    /// reference; <c>SymNodeDifferentialTests</c> holds the two together.
    /// </remarks>
    public static bool TryConstant(SymNode node, ExprType expected, out ExprValue value)
    {
        value = default;

        switch (node)
        {
            case SymSource source:
                return t_compiler is { } compiler
                       && compiler.TryConstant(source.Text, expected, ExprFunctions.DescribeUse(expected), out value);

            case SymLit lit:
                value = ExprValue.Number((float)lit.Value);
                return true;

            case SymUnary { Op: SymOp.Negate } unary when TryConstant(unary.Operand, ExprType.Number, out var operand):
                value = ExprValue.Number(-operand.AsNumber);
                return true;

            case SymUnary { Op: SymOp.ToLinearRgb } unary when TryConstant(unary.Operand, ExprType.Color, out var color):
                value = ExprColor.ToLinearRgb(color);
                return true;

            // Unclamped, so a factor outside [0, 1] casts an out-of-range double to a byte, which
            // .NET answers differently by version and platform. Only the drawing's own runtime may.
            case SymBinary { Op: SymOp.ScaleAlpha } binary
                when TryConstant(binary.Left, ExprType.Color, out var color)
                     && TryConstant(binary.Right, ExprType.Number, out var factor)
                     && factor.AsNumber is >= 0f and <= 1f:
                value = ExprColor.ScaleAlpha(color, factor.AsNumber);
                return true;

            case SymBinary { Op: SymOp.Add or SymOp.Subtract or SymOp.Multiply or SymOp.Divide } binary
                when TryConstant(binary.Left, ExprType.Number, out var left)
                     && TryConstant(binary.Right, ExprType.Number, out var right):
                value = ExprValue.Number(binary.Op switch
                {
                    SymOp.Add => left.AsNumber + right.AsNumber,
                    SymOp.Subtract => left.AsNumber - right.AsNumber,
                    SymOp.Multiply => left.AsNumber * right.AsNumber,
                    _ => left.AsNumber / right.AsNumber
                });
                return true;

            default:
                return false;
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
}
