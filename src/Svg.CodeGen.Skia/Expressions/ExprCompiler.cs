// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
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

    // Null when not folding, which is what keeps the C# back end exercised on constants.
    private readonly Dictionary<string, ExprValue>? _constants;

    public ExprCompiler(IReadOnlyDictionary<string, ExprType> symbols)
        : this(symbols, null)
    {
    }

    /// <param name="symbolNames">
    /// Names to emit in place of a declared one, for the symbols whose value reaches the body
    /// through a local rather than directly — see <see cref="ExprCSharpBackend.Emit"/>.
    /// </param>
    /// <param name="fold">Whether to emit what an expression already knows as its value.</param>
    public ExprCompiler(
        IReadOnlyDictionary<string, ExprType> symbols,
        IReadOnlyDictionary<string, string>? symbolNames,
        bool fold = false)
    {
        // Not copied. Callers add to the table between calls — see ExprChecker.
        _checker = new ExprChecker(symbols);
        _symbolNames = symbolNames;
        _constants = fold ? new Dictionary<string, ExprValue>(StringComparer.Ordinal) : null;
    }

    public static bool IsReservedName(string name) => ExprFunctions.IsReservedName(name);

    public static ExprType ParseType(string text, int position) => ExprFunctions.ParseType(text, position);

    public static string CSharpTypeOf(ExprType type) => ExprCSharpBackend.CSharpTypeOf(type);

    /// <summary>
    /// Compiles <paramref name="text"/> and requires it to produce <paramref name="expected"/>.
    /// </summary>
    public string CompileTo(string text, ExprType expected, string what)
        => ExprCSharpBackend.Emit(Fold(_checker.CheckAs(text, expected, what)), _symbolNames);

    public (ExprType Type, string Code) Compile(string text)
    {
        var checked_ = Fold(_checker.Check(text));

        return (checked_.Type, ExprCSharpBackend.Emit(checked_, _symbolNames));
    }

    /// <summary>
    /// The value <paramref name="text"/> comes to without anything bound, when this compiler folds
    /// and the expression needs nothing from a parameter.
    /// </summary>
    public bool TryConstant(string text, ExprType expected, string what, out ExprValue value)
        => TryConstant(_checker.CheckAs(text, expected, what), out value);

    public bool TryConstant(string text, out ExprValue value)
        => TryConstant(_checker.Check(text), out value);

    /// <summary>Folds every later reference to <paramref name="name"/> into <paramref name="value"/>.</summary>
    internal void DeclareConstant(string name, ExprValue value)
        => (_constants ?? throw new InvalidOperationException("This compiler does not fold."))[name] = value;

    private bool TryConstant(TypedExpr checked_, out ExprValue value)
    {
        value = default;

        return _constants is { } constants && ExprFolder.TryValue(ExprFolder.Fold(checked_, constants), out value);
    }

    private TypedExpr Fold(TypedExpr checked_)
        => _constants is { } constants ? ExprFolder.Fold(checked_, constants) : checked_;
}
