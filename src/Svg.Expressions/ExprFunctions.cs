// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;

namespace Svg.Expressions;

/// <summary>A named constant of the language.</summary>
public enum ExprConstant
{
    Pi,
    Tau
}

/// <summary>
/// A function of the language, identified rather than named. A back end maps this to whatever it
/// calls the operation; the language itself only says what the arguments and the result are.
/// </summary>
public enum ExprFunction
{
    Sin,
    Cos,
    Tan,
    Abs,
    Sqrt,
    Floor,
    Ceil,
    Round,
    Pow,
    Min,
    Max,
    Mod,
    Clamp,
    Lerp,
    Rgb,
    Rgba,
    Hsl,
    Hsla,
    Mix,
    WithAlpha
}

/// <summary>What a function takes and returns. No spelling in any target language.</summary>
public sealed class ExprSignature
{
    internal ExprSignature(ExprFunction function, ExprType result, params ExprType[] parameters)
    {
        Function = function;
        Result = result;
        Parameters = parameters;
    }

    public ExprFunction Function { get; }

    public ExprType Result { get; }

    public IReadOnlyList<ExprType> Parameters { get; }
}

/// <summary>
/// The vocabulary of the language: its functions, its constants, and the words it reserves.
/// </summary>
/// <remarks>
/// Everything here is semantics. What a function is <em>called</em> in a back end used to live in
/// this table, which made the two inseparable and left <c>mod</c> registered as a BCL function it
/// is not, purely to carry an arity. Each back end now owns its own mapping.
/// </remarks>
public static class ExprFunctions
{
    private const ExprType N = ExprType.Number;
    private const ExprType C = ExprType.Color;

    private static readonly Dictionary<string, ExprType> s_constants = new(StringComparer.Ordinal)
    {
        ["pi"] = ExprType.Number,
        ["tau"] = ExprType.Number
    };

    private static readonly Dictionary<string, ExprConstant> s_constantIds = new(StringComparer.Ordinal)
    {
        ["pi"] = ExprConstant.Pi,
        ["tau"] = ExprConstant.Tau
    };

    // Ordinal, so 'SIN' is not a spelling of 'sin'. Keyed by the name as authored, which is also
    // what a diagnostic has to show, so the enum is never the source of the text.
    private static readonly Dictionary<string, ExprSignature> s_functions = new(StringComparer.Ordinal)
    {
        ["sin"] = new(ExprFunction.Sin, N, N),
        ["cos"] = new(ExprFunction.Cos, N, N),
        ["tan"] = new(ExprFunction.Tan, N, N),
        ["abs"] = new(ExprFunction.Abs, N, N),
        ["sqrt"] = new(ExprFunction.Sqrt, N, N),
        ["floor"] = new(ExprFunction.Floor, N, N),
        ["ceil"] = new(ExprFunction.Ceil, N, N),
        ["round"] = new(ExprFunction.Round, N, N),
        ["pow"] = new(ExprFunction.Pow, N, N, N),
        ["min"] = new(ExprFunction.Min, N, N, N),
        ["max"] = new(ExprFunction.Max, N, N, N),
        ["mod"] = new(ExprFunction.Mod, N, N, N),
        ["clamp"] = new(ExprFunction.Clamp, N, N, N, N),
        ["lerp"] = new(ExprFunction.Lerp, N, N, N, N),
        ["rgb"] = new(ExprFunction.Rgb, C, N, N, N),
        ["rgba"] = new(ExprFunction.Rgba, C, N, N, N, N),
        ["hsl"] = new(ExprFunction.Hsl, C, N, N, N),
        ["hsla"] = new(ExprFunction.Hsla, C, N, N, N, N),
        ["mix"] = new(ExprFunction.Mix, C, C, C, N),
        ["withAlpha"] = new(ExprFunction.WithAlpha, C, C, N)
    };

    /// <summary>Function names as authored. Diagnostics list these, not the enum.</summary>
    public static IEnumerable<string> FunctionNames => s_functions.Keys;

    public static IEnumerable<string> ConstantNames => s_constants.Keys;

    public static bool TryGetFunction(string name, out ExprSignature signature)
        => s_functions.TryGetValue(name, out signature!);

    public static bool IsFunction(string name) => s_functions.ContainsKey(name);

    public static bool TryGetConstant(string name, out ExprConstant constant, out ExprType type)
    {
        type = ExprType.Number;

        return s_constantIds.TryGetValue(name, out constant) && s_constants.TryGetValue(name, out type);
    }

    public static bool IsReservedName(string name)
        => s_constants.ContainsKey(name) ||
           s_functions.ContainsKey(name) ||
           ExprLexer.IsKeyword(name) ||
           name == "true" ||
           name == "false";

    public static ExprType ParseType(string text, int position, SvgDeclarationPart? part = null)
        => text switch
        {
            "number" => ExprType.Number,
            "color" => ExprType.Color,
            "boolean" => ExprType.Boolean,
            _ => throw new ExprException($"Unknown type '{text}'. Expected number, color or boolean.", position, part: part)
        };

    /// <summary>How a type is written in a document, which is the spelling <see cref="ParseType"/> takes.</summary>
    /// <remarks>
    /// The inverse of the table above and kept beside it, because anything writing a declaration and
    /// the reader checking it afterwards have to agree on one word. <see cref="Describe"/> is not
    /// that word: it says "colour", which belongs in a sentence somebody reads rather than in an
    /// attribute the parser has to accept back.
    /// </remarks>
    public static string NameOf(ExprType type)
        => type switch
        {
            ExprType.Number => "number",
            ExprType.Color => "color",
            ExprType.Boolean => "boolean",
            _ => throw Unknown(type)
        };

    /// <summary>
    /// What an expression in a document is called, by the type its attribute demands.
    /// </summary>
    /// <remarks>
    /// One definition rather than one per back end: it was a ternary in the emitter and the same
    /// ternary in the scene evaluator, kept in step by a comment saying so. Neither could express
    /// the boolean case, so <c>visibility</c> — the only attribute that is a condition rather than
    /// a value — was told it was an opacity.
    /// </remarks>
    public static string DescribeUse(ExprType expected) => expected switch
    {
        ExprType.Color => "A paint expression",
        ExprType.Boolean => "A visibility expression",
        ExprType.Number => "An opacity expression",
        _ => throw Unknown(expected),
    };

    /// <summary>How a type is named in a diagnostic.</summary>
    public static string Describe(ExprType type)
        => type switch
        {
            ExprType.Number => "number",
            ExprType.Color => "colour",
            ExprType.Boolean => "boolean",
            _ => throw Unknown(type)
        };

    /// <summary>
    /// How an operator is spelled. One table rather than two, because the same spelling appears in
    /// the diagnostic that rejects an operand and in the C# a back end emits: two copies could
    /// drift, with byte-identical output riding on one and message text on the other.
    /// </summary>
    private static Exception Unknown(ExprType type)
        => new NotSupportedException($"Unsupported {nameof(ExprType)}: {type}.");

    public static string OperatorText(ExprBinaryOp op)
        => op switch
        {
            ExprBinaryOp.Add => "+",
            ExprBinaryOp.Subtract => "-",
            ExprBinaryOp.Multiply => "*",
            ExprBinaryOp.Divide => "/",
            ExprBinaryOp.Less => "<",
            ExprBinaryOp.LessOrEqual => "<=",
            ExprBinaryOp.Greater => ">",
            ExprBinaryOp.GreaterOrEqual => ">=",
            ExprBinaryOp.Equal => "==",
            ExprBinaryOp.NotEqual => "!=",
            ExprBinaryOp.And => "&&",
            _ => "||"
        };
}
