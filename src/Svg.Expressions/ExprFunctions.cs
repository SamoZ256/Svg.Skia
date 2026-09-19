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
    WithAlpha,
    WithSaturation,
    Upper,
    Lower,
    Len,
    Str,
    Int,
    Num
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
    private const ExprType I = ExprType.Integer;
    private const ExprType C = ExprType.Color;
    private const ExprType S = ExprType.String;

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
    //
    // A name may have more than one signature, which is how the numeric library reaches both
    // numeric types. Every overload of a name takes the same number of arguments, so arity is still
    // one question with one answer and is asked before any argument is looked at.
    private static readonly Dictionary<string, ExprSignature[]> s_functions = new(StringComparer.Ordinal)
    {
        ["sin"] = new[] { new ExprSignature(ExprFunction.Sin, N, N) },
        ["cos"] = new[] { new ExprSignature(ExprFunction.Cos, N, N) },
        ["tan"] = new[] { new ExprSignature(ExprFunction.Tan, N, N) },
        ["abs"] = new[] { new ExprSignature(ExprFunction.Abs, N, N), new ExprSignature(ExprFunction.Abs, I, I) },
        ["sqrt"] = new[] { new ExprSignature(ExprFunction.Sqrt, N, N) },
        ["floor"] = new[] { new ExprSignature(ExprFunction.Floor, N, N) },
        ["ceil"] = new[] { new ExprSignature(ExprFunction.Ceil, N, N) },
        ["round"] = new[] { new ExprSignature(ExprFunction.Round, N, N) },
        ["pow"] = new[] { new ExprSignature(ExprFunction.Pow, N, N, N) },
        ["min"] = new[] { new ExprSignature(ExprFunction.Min, N, N, N), new ExprSignature(ExprFunction.Min, I, I, I) },
        ["max"] = new[] { new ExprSignature(ExprFunction.Max, N, N, N), new ExprSignature(ExprFunction.Max, I, I, I) },
        ["mod"] = new[] { new ExprSignature(ExprFunction.Mod, N, N, N), new ExprSignature(ExprFunction.Mod, I, I, I) },
        ["clamp"] = new[] { new ExprSignature(ExprFunction.Clamp, N, N, N, N), new ExprSignature(ExprFunction.Clamp, I, I, I, I) },
        ["lerp"] = new[] { new ExprSignature(ExprFunction.Lerp, N, N, N, N) },
        ["rgb"] = new[] { new ExprSignature(ExprFunction.Rgb, C, N, N, N) },
        ["rgba"] = new[] { new ExprSignature(ExprFunction.Rgba, C, N, N, N, N) },
        ["hsl"] = new[] { new ExprSignature(ExprFunction.Hsl, C, N, N, N) },
        ["hsla"] = new[] { new ExprSignature(ExprFunction.Hsla, C, N, N, N, N) },
        ["mix"] = new[] { new ExprSignature(ExprFunction.Mix, C, C, C, N) },
        ["withAlpha"] = new[] { new ExprSignature(ExprFunction.WithAlpha, C, C, N) },

        // Value, not lightness: this is the space SkiaSharp's own ToHsv works in, and a colour
        // desaturated there keeps the brightness the eye reads rather than the midpoint hsl would
        // move it to. Written beside withAlpha because it is the same shape of thing -- one channel
        // of a colour replaced, the rest of it left alone.
        ["withSaturation"] = new[] { new ExprSignature(ExprFunction.WithSaturation, C, C, N) },
        ["upper"] = new[] { new ExprSignature(ExprFunction.Upper, S, S) },
        ["lower"] = new[] { new ExprSignature(ExprFunction.Lower, S, S) },

        // A count of code units is whole, and now has a type that says so. It still reaches the
        // arithmetic, through num() where the arithmetic is fractional.
        ["len"] = new[] { new ExprSignature(ExprFunction.Len, I, S) },

        // The other direction, and the only one: + never converts, so this is how a number reaches
        // the text of a <text> element.
        ["str"] = new[] { new ExprSignature(ExprFunction.Str, S, N), new ExprSignature(ExprFunction.Str, S, I) },

        // The crossings between the two numeric types. Functions rather than conversions for the
        // reason the string ones are: a value that changed type without being asked to would make
        // every arithmetic operator mean two things.
        ["int"] = new[] { new ExprSignature(ExprFunction.Int, I, N) },
        ["num"] = new[] { new ExprSignature(ExprFunction.Num, N, I) }
    };

    /// <summary>Function names as authored. Diagnostics list these, not the enum.</summary>
    public static IEnumerable<string> FunctionNames => s_functions.Keys;

    public static IEnumerable<string> ConstantNames => s_constants.Keys;

    /// <summary>Every signature a name has, in the order a tie is broken.</summary>
    /// <remarks>
    /// The number overload is written first everywhere, and <see cref="ExprChecker"/> takes the
    /// first candidate: a call whose arguments are all open literals -- <c>min(1, 2)</c> -- is the
    /// one case where both fit, and answering with a number is what it answered before there was a
    /// second numeric type. <c>int(min(1, 2))</c> is how to ask for the other.
    /// </remarks>
    public static bool TryGetFunction(string name, out IReadOnlyList<ExprSignature> overloads)
    {
        var found = s_functions.TryGetValue(name, out var signatures);

        overloads = signatures ?? Array.Empty<ExprSignature>();

        return found;
    }

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
            "integer" => ExprType.Integer,
            "color" => ExprType.Color,
            "boolean" => ExprType.Boolean,
            "string" => ExprType.String,
            _ => throw new ExprException($"Unknown type '{text}'. Expected number, integer, color, boolean or string.", position, part: part)
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
            ExprType.Integer => "integer",
            ExprType.Color => "color",
            ExprType.Boolean => "boolean",
            ExprType.String => "string",
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
        ExprType.String => "A text expression",

        // Integer falls here rather than being named: no attribute holds one, so no expression is
        // ever asked to produce one, and there is no use to describe.
        _ => throw Unknown(expected),
    };

    /// <summary>How a type is named in a diagnostic.</summary>
    public static string Describe(ExprType type)
        => type switch
        {
            ExprType.Number => "number",
            ExprType.Integer => "integer",
            ExprType.Color => "colour",
            ExprType.Boolean => "boolean",
            ExprType.String => "string",
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
