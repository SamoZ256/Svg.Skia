// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Svg.Expressions;

namespace Svg.CodeGen.Skia.Expressions;

/// <summary>Renders a checked expression as C#.</summary>
/// <remarks>
/// Every parenthesis is deliberate: each operand is wrapped so C# precedence cannot regroup what the
/// language already grouped, and the output is compared byte for byte. A call is emitted as a name
/// immediately followed by <c>(</c> — helper selection scans the finished class for that exact text,
/// so a space would leave <c>SvgHsl</c> undefined.
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
        [ExprFunction.WithAlpha] = ExprHelpers.WithAlpha,
        [ExprFunction.Upper] = ExprHelpers.Upper,
        [ExprFunction.Lower] = ExprHelpers.Lower,
        [ExprFunction.Len] = ExprHelpers.Len
        // Mod is absent on purpose: no BCL function has the semantics, so it is emitted inline. It
        // used to be MathF.IEEERemainder here, which is a different operation.
    };

    /// <param name="symbolNames">
    /// Names to emit in place of a declared one. A colour parameter carrying a default is emitted as
    /// a nullable parameter and coalesced into a local, and the body has to reference that local:
    /// C# will not let a local shadow the parameter it is derived from. Empty for every other
    /// expression, including the defaults themselves.
    /// </param>
    public static string Emit(TypedExpr node, IReadOnlyDictionary<string, string>? symbolNames = null)
        => node switch
        {
            TypedNumber number => Literal(number.Value),
            TypedColor color => $"new SKColor({color.R}, {color.G}, {color.B}, {color.A})",
            TypedBoolean boolean => boolean.Value ? "true" : "false",
            TypedString text => Literal(text.Value),
            TypedSymbol symbol => Name(symbol, symbolNames),
            TypedConstant constant => EmitConstant(constant.Constant),
            TypedUnary unary => EmitUnary(unary, symbolNames),
            TypedBinary binary =>
                $"({Emit(binary.Left, symbolNames)} {ExprFunctions.OperatorText(binary.Op)} {Emit(binary.Right, symbolNames)})",
            TypedConditional conditional =>
                $"({Emit(conditional.Condition, symbolNames)} ? {Emit(conditional.WhenTrue, symbolNames)} : {Emit(conditional.WhenFalse, symbolNames)})",
            TypedCall call => EmitCall(call, symbolNames),
            _ => throw new NotSupportedException($"Unsupported {nameof(TypedExpr)}: {node.GetType().Name}.")
        };

    private static string Name(TypedSymbol symbol, IReadOnlyDictionary<string, string>? symbolNames)
        => symbolNames is { } names && names.TryGetValue(symbol.Name, out var rewritten) ? rewritten : symbol.Name;

    public static string CSharpTypeOf(ExprType type)
        => type switch
        {
            ExprType.Number => "float",
            ExprType.Color => "SKColor",
            ExprType.Boolean => "bool",
            ExprType.String => "string",
            _ => throw new NotSupportedException($"Unsupported {nameof(ExprType)}: {type}.")
        };

    private static string EmitConstant(ExprConstant constant)
        => constant switch
        {
            ExprConstant.Pi => "MathF.PI",
            _ => "(MathF.PI * 2f)"
        };

    private static string EmitUnary(TypedUnary unary, IReadOnlyDictionary<string, string>? symbolNames)
        => unary.Op == ExprUnaryOp.Negate
            ? $"(-{Emit(unary.Operand, symbolNames)})"
            : $"(!{Emit(unary.Operand, symbolNames)})";

    private static string EmitCall(TypedCall call, IReadOnlyDictionary<string, string>? symbolNames)
    {
        var arguments = call.Arguments.Select(argument => Emit(argument, symbolNames)).ToList();

        // Remainder has no BCL function with the semantics we want, so it is emitted inline.
        // Both operands are already parenthesised sub-expressions, so each is evaluated once.
        if (call.Function == ExprFunction.Mod)
        {
            return $"({arguments[0]} % {arguments[1]})";
        }

        return $"{s_names[call.Function]}({string.Join(", ", arguments)})";
    }

    /// <summary>A string as a C# literal.</summary>
    /// <remarks>
    /// Written out rather than taken from a formatter, because the output is compared byte for byte
    /// against the interpreter's own answer. Anything outside printable ASCII is escaped by code
    /// point, so a generated file is ASCII whatever encoding it is later saved in.
    /// </remarks>
    private static string Literal(string value)
    {
        var literal = new StringBuilder(value.Length + 2);

        literal.Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': literal.Append("\\\\"); break;
                case '"': literal.Append("\\\""); break;
                case '\n': literal.Append("\\n"); break;
                case '\r': literal.Append("\\r"); break;
                case '\t': literal.Append("\\t"); break;
                default:
                    literal.Append(c is >= ' ' and <= '~'
                        ? c.ToString()
                        : "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
            }
        }

        literal.Append('"');

        return literal.ToString();
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
