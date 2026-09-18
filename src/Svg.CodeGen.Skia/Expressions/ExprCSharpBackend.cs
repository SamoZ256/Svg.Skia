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
        [ExprFunction.WithSaturation] = ExprHelpers.WithSaturation,
        [ExprFunction.Upper] = ExprHelpers.Upper,
        [ExprFunction.Lower] = ExprHelpers.Lower,
        [ExprFunction.Len] = ExprHelpers.Len,
        [ExprFunction.Str] = ExprHelpers.Str,
        [ExprFunction.Int] = ExprHelpers.Int,
        [ExprFunction.Num] = ExprHelpers.Num
        // Mod is absent on purpose: no BCL function has the semantics, so it is emitted inline. It
        // used to be MathF.IEEERemainder here, which is a different operation.
    };

    // What the same function is called when it works in integers. Absent from here means the one
    // spelling above serves both: str() is two C# overloads under one name, and len() takes a
    // string whatever it returns.
    private static readonly Dictionary<ExprFunction, string> s_integerNames = new()
    {
        [ExprFunction.Abs] = ExprHelpers.IAbs,
        [ExprFunction.Min] = "Math.Min",
        [ExprFunction.Max] = "Math.Max",
        [ExprFunction.Clamp] = "Math.Clamp",
        [ExprFunction.Mod] = ExprHelpers.IMod
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
            TypedInteger integer => Literal(integer.Value),
            TypedColor color => $"new SKColor({color.R}, {color.G}, {color.B}, {color.A})",
            TypedBoolean boolean => boolean.Value ? "true" : "false",
            TypedString text => Literal(text.Value),
            TypedSymbol symbol => Name(symbol, symbolNames),
            TypedConstant constant => EmitConstant(constant.Constant),
            TypedUnary unary => EmitUnary(unary, symbolNames),
            TypedBinary binary => EmitBinary(binary, symbolNames),
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
            ExprType.Integer => "int",
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

    /// <remarks>
    /// C# spells integer arithmetic with the same operators, so only division is named: <c>/</c>
    /// between two ints throws where the language answers, and <c>+</c>, <c>-</c> and <c>*</c> wrap
    /// in both.
    /// </remarks>
    private static string EmitBinary(TypedBinary binary, IReadOnlyDictionary<string, string>? symbolNames)
    {
        var left = Emit(binary.Left, symbolNames);
        var right = Emit(binary.Right, symbolNames);

        if (binary.Op == ExprBinaryOp.Divide && binary.Type == ExprType.Integer)
        {
            return $"{ExprHelpers.IDiv}({left}, {right})";
        }

        return $"({left} {ExprFunctions.OperatorText(binary.Op)} {right})";
    }

    private static string EmitUnary(TypedUnary unary, IReadOnlyDictionary<string, string>? symbolNames)
        => unary.Op == ExprUnaryOp.Negate
            ? $"(-{Emit(unary.Operand, symbolNames)})"
            : $"(!{Emit(unary.Operand, symbolNames)})";

    private static string EmitCall(TypedCall call, IReadOnlyDictionary<string, string>? symbolNames)
    {
        var arguments = call.Arguments.Select(argument => Emit(argument, symbolNames)).ToList();

        // An integer call is spelled from its own table where the two differ. The result type
        // answers for it, every argument of an integer overload being an integer as well.
        if (call.Type == ExprType.Integer && s_integerNames.TryGetValue(call.Function, out var integerName))
        {
            return $"{integerName}({string.Join(", ", arguments)})";
        }

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

    /// <remarks>
    /// The checker range checks before this, so the narrowing is lossless. A literal is never
    /// negative -- the parser reads a leading minus as negation -- so int.MinValue, which C# will
    /// not accept written out, cannot arise here.
    /// </remarks>
    private static string Literal(long value)
        => ((int)value).ToString(CultureInfo.InvariantCulture);

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
