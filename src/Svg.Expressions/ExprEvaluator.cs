// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;

namespace Svg.Expressions;

/// <summary>
/// Checks an expression and evaluates it against values: <see cref="ExprChecker"/> followed by the
/// value back end, which is what a renderer wants of the language every time.
/// </summary>
/// <remarks>
/// <para>
/// The runtime counterpart of the code generator's <c>ExprCompiler</c>, and deliberately the same
/// shape. Both are a facade over one checker and one back end, so a change to the language reaches
/// them symmetrically.
/// </para>
/// <para>
/// Neither the symbol table nor the value map is copied. <see cref="Create"/> keeps adding to both
/// as it resolves the lets, and the checker holds the table by reference for the same reason — see
/// <see cref="ExprChecker"/>.
/// </para>
/// </remarks>
public sealed class ExprEvaluator
{
    private readonly ExprChecker _checker;
    private readonly IReadOnlyDictionary<string, ExprValue> _values;

    public ExprEvaluator(
        IReadOnlyDictionary<string, ExprType> symbols,
        IReadOnlyDictionary<string, ExprValue> values)
    {
        _checker = new ExprChecker(symbols);
        _values = values ?? throw new ArgumentNullException(nameof(values));
    }

    /// <summary>
    /// Binds <paramref name="parameterValues"/> to <paramref name="declarations"/> and resolves the
    /// lets, returning an evaluator every expression in the document can be evaluated against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every value is resolved here, before anything is evaluated: a supplied value if there is one,
    /// otherwise the declared <c>default</c>, otherwise an error naming the parameter. Nothing falls
    /// back to the placeholder, which keeps this the same rule the generated code enforces — there,
    /// a parameter without a default is simply required. A host that wants the placeholder can bind
    /// it as a value, so leniency needs no support here.
    /// </para>
    /// <para>
    /// A value supplied for a name the document does not declare is ignored rather than rejected.
    /// The two directions are not symmetric: asking to render with a value missing is
    /// under-specified and worth failing, while carrying a stale value across an edit that removed
    /// a parameter is ordinary and should not stop a drawing appearing.
    /// </para>
    /// </remarks>
    public static ExprEvaluator Create(
        SvgExpressionDeclarations declarations,
        IReadOnlyDictionary<string, ExprValue>? parameterValues = null)
    {
        if (declarations is null)
        {
            throw new ArgumentNullException(nameof(declarations));
        }

        var symbols = declarations.CreateSymbolTable();
        var values = new Dictionary<string, ExprValue>(StringComparer.Ordinal);

        foreach (var parameter in declarations.Parameters)
        {
            values[parameter.Name] = ResolveParameter(parameter, parameterValues);
        }

        var evaluator = new ExprEvaluator(symbols, values);

        // Lets resolve in order, so a let may use the ones declared above it but not below. Adding
        // to both maps is what makes each one visible to whatever is evaluated after it.
        foreach (var let in declarations.Lets)
        {
            var value = evaluator.Evaluate(let.Expression);
            symbols[let.Name] = value.Type;
            values[let.Name] = value;
        }

        return evaluator;
    }

    /// <summary>
    /// Evaluates <paramref name="text"/> and requires it to produce <paramref name="expected"/>.
    /// </summary>
    public ExprValue EvaluateTo(string text, ExprType expected, string what)
        => ExprValueBackend.Evaluate(_checker.CheckAs(text, expected, what), _values);

    public ExprValue Evaluate(string text)
        => ExprValueBackend.Evaluate(_checker.Check(text), _values);

    private static ExprValue ResolveParameter(
        SvgExpressionParameter parameter,
        IReadOnlyDictionary<string, ExprValue>? parameterValues)
    {
        if (parameterValues is { } supplied && supplied.TryGetValue(parameter.Name, out var value))
        {
            if (value.Type != parameter.Type)
            {
                throw new ExprException(
                    $"The value supplied for '{parameter.Name}' is a {ExprFunctions.Describe(value.Type)}, but it is declared as a {ExprFunctions.Describe(parameter.Type)}.",
                    0);
            }

            return value;
        }

        if (parameter.DefaultExpression is null)
        {
            throw new ExprException(
                $"No value was supplied for '{parameter.Name}', and it has no default. Supply one, or give it a default in <e:param>.",
                0);
        }

        return Isolated.EvaluateTo(
            parameter.DefaultExpression,
            parameter.Type,
            $"The default for '{parameter.Name}'");
    }

    /// <summary>
    /// An evaluator over nothing at all, for the parts of a declaration that may not reference
    /// anything the document declares.
    /// </summary>
    /// <remarks>
    /// A <c>default</c> may not reference other parameters — the same restriction the code generator
    /// applies, for the same reason: an ordering dependency between them would be invisible in the
    /// document — and <c>min</c>, <c>max</c> and <c>step</c> inherit it. Shared rather than
    /// constructed per call so the two cannot resolve against different scopes, and because it holds
    /// no state: both maps stay empty, and nothing here ever writes to them.
    /// </remarks>
    internal static readonly ExprEvaluator Isolated = new(
        new Dictionary<string, ExprType>(StringComparer.Ordinal),
        new Dictionary<string, ExprValue>(StringComparer.Ordinal));
}
