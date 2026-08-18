// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Collections.Generic;
using Svg.Expressions;

namespace Svg.CodeGen.Skia.Expressions;

/// <summary>
/// Checks an expression and renders it as C#: <see cref="ExprChecker"/> followed by the C# back
/// end, which is what the code generator wants of the language every time.
/// </summary>
/// <remarks>
/// This used to be the whole implementation — checking and emission in one pass, because a node's
/// C# form was decided by its resolved type as it was worked out. It is a facade now, so the
/// checker can serve a second back end (evaluation against real values) without the code generator
/// being in the way.
/// </remarks>
public sealed class ExprCompiler
{
    private readonly ExprChecker _checker;
    private readonly IReadOnlyDictionary<string, string>? _symbolNames;

    public ExprCompiler(IReadOnlyDictionary<string, ExprType> symbols)
        : this(symbols, null)
    {
    }

    /// <param name="symbolNames">
    /// Names to emit in place of a declared one, for the symbols whose value reaches the body
    /// through a local rather than directly — see <see cref="ExprCSharpBackend.Emit"/>.
    /// </param>
    public ExprCompiler(
        IReadOnlyDictionary<string, ExprType> symbols,
        IReadOnlyDictionary<string, string>? symbolNames)
    {
        // Not copied. Callers add to the table between calls — see ExprChecker.
        _checker = new ExprChecker(symbols);
        _symbolNames = symbolNames;
    }

    public static bool IsReservedName(string name) => ExprFunctions.IsReservedName(name);

    public static ExprType ParseType(string text, int position) => ExprFunctions.ParseType(text, position);

    public static string CSharpTypeOf(ExprType type) => ExprCSharpBackend.CSharpTypeOf(type);

    /// <summary>
    /// Compiles <paramref name="text"/> and requires it to produce <paramref name="expected"/>.
    /// </summary>
    public string CompileTo(string text, ExprType expected, string what)
        => ExprCSharpBackend.Emit(_checker.CheckAs(text, expected, what), _symbolNames);

    public (ExprType Type, string Code) Compile(string text)
    {
        var checked_ = _checker.Check(text);

        return (checked_.Type, ExprCSharpBackend.Emit(checked_, _symbolNames));
    }
}
