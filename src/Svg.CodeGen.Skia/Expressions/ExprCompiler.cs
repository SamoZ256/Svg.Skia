// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Svg.CodeGen.Skia.Expressions;

// Type checks an expression against a symbol table and renders it as C#. Checking and emission
// run in one pass because every node's C# form is decided by its resolved type.
public sealed class ExprCompiler
{
    private static readonly Dictionary<string, ExprType> s_constants = new(StringComparer.Ordinal)
    {
        ["pi"] = ExprType.Number,
        ["tau"] = ExprType.Number
    };

    private sealed class Signature
    {
        public Signature(string emitAs, ExprType result, params ExprType[] parameters)
        {
            EmitAs = emitAs;
            Result = result;
            Parameters = parameters;
        }

        public string EmitAs { get; }

        public ExprType Result { get; }

        public IReadOnlyList<ExprType> Parameters { get; }
    }

    private const ExprType N = ExprType.Number;
    private const ExprType C = ExprType.Color;

    private static readonly Dictionary<string, Signature> s_functions = new(StringComparer.Ordinal)
    {
        ["sin"] = new("MathF.Sin", N, N),
        ["cos"] = new("MathF.Cos", N, N),
        ["tan"] = new("MathF.Tan", N, N),
        ["abs"] = new("MathF.Abs", N, N),
        ["sqrt"] = new("MathF.Sqrt", N, N),
        ["floor"] = new("MathF.Floor", N, N),
        ["ceil"] = new("MathF.Ceiling", N, N),
        ["round"] = new("MathF.Round", N, N),
        ["pow"] = new("MathF.Pow", N, N, N),
        ["min"] = new("MathF.Min", N, N, N),
        ["max"] = new("MathF.Max", N, N, N),
        ["mod"] = new("MathF.IEEERemainder", N, N, N), // replaced below; kept for arity/type only
        ["clamp"] = new("Math.Clamp", N, N, N, N),
        ["lerp"] = new(ExprHelpers.Lerp, N, N, N, N),
        ["rgb"] = new(ExprHelpers.Rgb, C, N, N, N),
        ["rgba"] = new(ExprHelpers.Rgba, C, N, N, N, N),
        ["hsl"] = new(ExprHelpers.Hsl, C, N, N, N),
        ["hsla"] = new(ExprHelpers.Hsla, C, N, N, N, N),
        ["mix"] = new(ExprHelpers.Mix, C, C, C, N),
        ["withAlpha"] = new(ExprHelpers.WithAlpha, C, C, N)
    };

    private readonly IReadOnlyDictionary<string, ExprType> _symbols;

    public ExprCompiler(IReadOnlyDictionary<string, ExprType> symbols)
    {
        _symbols = symbols;
    }

    public static IEnumerable<string> FunctionNames => s_functions.Keys;

    public static IEnumerable<string> ConstantNames => s_constants.Keys;

    public static bool IsReservedName(string name)
        => s_constants.ContainsKey(name) ||
           s_functions.ContainsKey(name) ||
           name == "true" ||
           name == "false";

    public static ExprType ParseType(string text, int position)
        => text switch
        {
            "number" => ExprType.Number,
            "color" => ExprType.Color,
            "boolean" => ExprType.Boolean,
            _ => throw new ExprException($"Unknown type '{text}'. Expected number, color or boolean.", position)
        };

    public static string CSharpTypeOf(ExprType type)
        => type switch
        {
            ExprType.Number => "float",
            ExprType.Color => "SKColor",
            _ => "bool"
        };

    /// <summary>
    /// Compiles <paramref name="text"/> and requires it to produce <paramref name="expected"/>.
    /// </summary>
    public string CompileTo(string text, ExprType expected, string what)
    {
        var (type, code) = Compile(text);

        if (type != expected)
        {
            throw new ExprException(
                $"{what} must be a {Describe(expected)} expression, but this one is a {Describe(type)}.",
                0);
        }

        return code;
    }

    public (ExprType Type, string Code) Compile(string text)
    {
        try
        {
            return Compile(ExprParser.Parse(text));
        }
        catch (ExprException error)
        {
            // Positions are offsets into this text, so this is the first frame that can attach
            // the source needed to render a caret.
            throw error.WithExpressionText(text);
        }
    }

    private (ExprType Type, string Code) Compile(ExprNode node)
    {
        switch (node)
        {
            case NumberExpr number:
                return (ExprType.Number, Literal(number.Value));

            case ColorExpr color:
                return (ExprType.Color, $"new SKColor({color.R}, {color.G}, {color.B}, {color.A})");

            case BooleanExpr boolean:
                return (ExprType.Boolean, boolean.Value ? "true" : "false");

            case IdentifierExpr identifier:
                return CompileIdentifier(identifier);

            case UnaryExpr unary:
                return CompileUnary(unary);

            case BinaryExpr binary:
                return CompileBinary(binary);

            case ConditionalExpr conditional:
                return CompileConditional(conditional);

            case CallExpr call:
                return CompileCall(call);

            default:
                throw new ExprException($"Unsupported expression node {node.GetType().Name}.", node.Position);
        }
    }

    private (ExprType, string) CompileIdentifier(IdentifierExpr identifier)
    {
        if (_symbols.TryGetValue(identifier.Name, out var declared))
        {
            return (declared, identifier.Name);
        }

        switch (identifier.Name)
        {
            case "pi":
                return (ExprType.Number, "MathF.PI");
            case "tau":
                return (ExprType.Number, "(MathF.PI * 2f)");
        }

        if (s_functions.ContainsKey(identifier.Name))
        {
            throw new ExprException($"'{identifier.Name}' is a function; call it with arguments.", identifier.Position);
        }

        var known = _symbols.Keys.Concat(s_constants.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var hint = known.Count == 0 ? "no names are in scope" : "in scope: " + string.Join(", ", known);

        throw new ExprException($"Unknown name '{identifier.Name}' ({hint}).", identifier.Position);
    }

    private (ExprType, string) CompileUnary(UnaryExpr unary)
    {
        var (type, code) = Compile(unary.Operand);

        if (unary.Op == ExprUnaryOp.Negate)
        {
            Require(type, ExprType.Number, "'-'", unary.Position);
            return (ExprType.Number, $"(-{code})");
        }

        Require(type, ExprType.Boolean, "'!'", unary.Position);
        return (ExprType.Boolean, $"(!{code})");
    }

    private (ExprType, string) CompileBinary(BinaryExpr binary)
    {
        var (leftType, left) = Compile(binary.Left);
        var (rightType, right) = Compile(binary.Right);

        switch (binary.Op)
        {
            case ExprBinaryOp.Add:
            case ExprBinaryOp.Subtract:
            case ExprBinaryOp.Multiply:
            case ExprBinaryOp.Divide:
                {
                    var symbol = binary.Op switch
                    {
                        ExprBinaryOp.Add => "+",
                        ExprBinaryOp.Subtract => "-",
                        ExprBinaryOp.Multiply => "*",
                        _ => "/"
                    };

                    if (leftType == ExprType.Color || rightType == ExprType.Color)
                    {
                        throw new ExprException(
                            $"'{symbol}' does not apply to colours. Use mix(a, b, t) to blend them.",
                            binary.Position);
                    }

                    Require(leftType, ExprType.Number, $"'{symbol}'", binary.Position);
                    Require(rightType, ExprType.Number, $"'{symbol}'", binary.Position);

                    return (ExprType.Number, $"({left} {symbol} {right})");
                }

            case ExprBinaryOp.Less:
            case ExprBinaryOp.LessOrEqual:
            case ExprBinaryOp.Greater:
            case ExprBinaryOp.GreaterOrEqual:
                {
                    var symbol = binary.Op switch
                    {
                        ExprBinaryOp.Less => "<",
                        ExprBinaryOp.LessOrEqual => "<=",
                        ExprBinaryOp.Greater => ">",
                        _ => ">="
                    };

                    Require(leftType, ExprType.Number, $"'{symbol}'", binary.Position);
                    Require(rightType, ExprType.Number, $"'{symbol}'", binary.Position);

                    return (ExprType.Boolean, $"({left} {symbol} {right})");
                }

            case ExprBinaryOp.Equal:
            case ExprBinaryOp.NotEqual:
                {
                    var symbol = binary.Op == ExprBinaryOp.Equal ? "==" : "!=";

                    if (leftType != rightType)
                    {
                        throw new ExprException(
                            $"'{symbol}' compares a {Describe(leftType)} with a {Describe(rightType)}.",
                            binary.Position);
                    }

                    return (ExprType.Boolean, $"({left} {symbol} {right})");
                }

            default:
                {
                    var symbol = binary.Op == ExprBinaryOp.And ? "&&" : "||";

                    Require(leftType, ExprType.Boolean, $"'{symbol}'", binary.Position);
                    Require(rightType, ExprType.Boolean, $"'{symbol}'", binary.Position);

                    return (ExprType.Boolean, $"({left} {symbol} {right})");
                }
        }
    }

    private (ExprType, string) CompileConditional(ConditionalExpr conditional)
    {
        var (conditionType, condition) = Compile(conditional.Condition);

        if (conditionType != ExprType.Boolean)
        {
            throw new ExprException(
                $"The condition before '?' must be a boolean, but it is a {Describe(conditionType)}.",
                conditional.Position);
        }

        var (trueType, whenTrue) = Compile(conditional.WhenTrue);
        var (falseType, whenFalse) = Compile(conditional.WhenFalse);

        if (trueType != falseType)
        {
            throw new ExprException(
                $"Both branches of '?:' must have the same type, but they are {Describe(trueType)} and {Describe(falseType)}.",
                conditional.Position);
        }

        return (trueType, $"({condition} ? {whenTrue} : {whenFalse})");
    }

    private (ExprType, string) CompileCall(CallExpr call)
    {
        if (!s_functions.TryGetValue(call.Name, out var signature))
        {
            var known = string.Join(", ", s_functions.Keys.OrderBy(k => k, StringComparer.Ordinal));
            throw new ExprException($"Unknown function '{call.Name}'. Available: {known}.", call.Position);
        }

        if (call.Arguments.Count != signature.Parameters.Count)
        {
            throw new ExprException(
                $"'{call.Name}' takes {signature.Parameters.Count} argument(s), but {call.Arguments.Count} were given.",
                call.Position);
        }

        var arguments = new List<string>(call.Arguments.Count);

        for (var i = 0; i < call.Arguments.Count; i++)
        {
            var (type, code) = Compile(call.Arguments[i]);

            if (type != signature.Parameters[i])
            {
                throw new ExprException(
                    $"Argument {i + 1} of '{call.Name}' must be a {Describe(signature.Parameters[i])}, but it is a {Describe(type)}.",
                    call.Arguments[i].Position);
            }

            arguments.Add(code);
        }

        // Remainder has no BCL function with the semantics we want, so it is emitted inline.
        // Both operands are already parenthesised sub-expressions, so each is evaluated once.
        if (call.Name == "mod")
        {
            return (ExprType.Number, $"({arguments[0]} % {arguments[1]})");
        }

        return (signature.Result, $"{signature.EmitAs}({string.Join(", ", arguments)})");
    }

    private static void Require(ExprType actual, ExprType expected, string what, int position)
    {
        if (actual != expected)
        {
            throw new ExprException(
                $"{what} expects a {Describe(expected)}, but got a {Describe(actual)}.",
                position);
        }
    }

    private static string Describe(ExprType type)
        => type switch
        {
            ExprType.Number => "number",
            ExprType.Color => "colour",
            _ => "boolean"
        };

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
