// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Svg.Expressions;

namespace Svg.CodeGen.Skia.Expressions;

/// <summary>Renders a checked expression as C#.</summary>
/// <remarks>
/// <para>
/// Every parenthesis here is deliberate. The output is compared byte for byte by the code
/// generator's tests, and the shape also protects the meaning: each operand is wrapped so C#
/// precedence cannot regroup what the language already grouped.
/// </para>
/// <para>
/// A call is emitted as name immediately followed by <c>(</c>, with no space. Helper selection in
/// <see cref="SkiaCSharpCodeGen"/> works by scanning the finished class for that exact text, so a
/// space here would silently leave <c>SvgHsl</c> undefined in the generated file.
/// </para>
/// </remarks>
internal static class ExprCSharpBackend
{
    // What each function of the language is called in C#. The language's own table says only what
    // the arguments and result are; this is the half that knows about MathF.
    private static readonly Dictionary<ExprFunction, string> s_names = new()
    {
        [ExprFunction.Sin] = "MathF.Sin",
        [ExprFunction.Cos] = "MathF.Cos",
        [ExprFunction.Tan] = "MathF.Tan",
        [ExprFunction.Abs] = "MathF.Abs",
        [ExprFunction.Sqrt] = "MathF.Sqrt",
        [ExprFunction.Floor] = "MathF.Floor",
        [ExprFunction.Ceil] = "MathF.Ceiling",
        [ExprFunction.Round] = "MathF.Round",
        [ExprFunction.Pow] = "MathF.Pow",
        [ExprFunction.Min] = "MathF.Min",
        [ExprFunction.Max] = "MathF.Max",
        [ExprFunction.Clamp] = "Math.Clamp",
        [ExprFunction.Lerp] = ExprHelpers.Lerp,
        [ExprFunction.Rgb] = ExprHelpers.Rgb,
        [ExprFunction.Rgba] = ExprHelpers.Rgba,
        [ExprFunction.Hsl] = ExprHelpers.Hsl,
        [ExprFunction.Hsla] = ExprHelpers.Hsla,
        [ExprFunction.Mix] = ExprHelpers.Mix,
        [ExprFunction.WithAlpha] = ExprHelpers.WithAlpha
        // Mod is absent on purpose: there is no BCL function with the semantics we want, so it is
        // emitted inline below. It used to sit in the language's table as MathF.IEEERemainder,
        // which is a different operation, carried only to state an arity.
    };

    public static string Emit(TypedExpr node)
        => node switch
        {
            TypedNumber number => Literal(number.Value),
            TypedColor color => $"new SKColor({color.R}, {color.G}, {color.B}, {color.A})",
            TypedBoolean boolean => boolean.Value ? "true" : "false",
            TypedSymbol symbol => symbol.Name,
            TypedConstant constant => EmitConstant(constant.Constant),
            TypedUnary unary => EmitUnary(unary),
            TypedBinary binary => $"({Emit(binary.Left)} {ExprFunctions.OperatorText(binary.Op)} {Emit(binary.Right)})",
            TypedConditional conditional =>
                $"({Emit(conditional.Condition)} ? {Emit(conditional.WhenTrue)} : {Emit(conditional.WhenFalse)})",
            TypedCall call => EmitCall(call),
            _ => throw new NotSupportedException($"Unsupported {nameof(TypedExpr)}: {node.GetType().Name}.")
        };

    public static string CSharpTypeOf(ExprType type)
        => type switch
        {
            ExprType.Number => "float",
            ExprType.Color => "SKColor",
            _ => "bool"
        };

    private static string EmitConstant(ExprConstant constant)
        => constant switch
        {
            ExprConstant.Pi => "MathF.PI",
            _ => "(MathF.PI * 2f)"
        };

    private static string EmitUnary(TypedUnary unary)
        => unary.Op == ExprUnaryOp.Negate
            ? $"(-{Emit(unary.Operand)})"
            : $"(!{Emit(unary.Operand)})";

    private static string EmitCall(TypedCall call)
    {
        var arguments = call.Arguments.Select(Emit).ToList();

        // Remainder has no BCL function with the semantics we want, so it is emitted inline.
        // Both operands are already parenthesised sub-expressions, so each is evaluated once.
        if (call.Function == ExprFunction.Mod)
        {
            return $"({arguments[0]} % {arguments[1]})";
        }

        return $"{s_names[call.Function]}({string.Join(", ", arguments)})";
    }

    private static string Literal(double value)
    {
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
