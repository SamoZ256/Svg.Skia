// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Svg.Expressions;

/// <summary>
/// Type checks an expression against a symbol table and returns it checked. The only thing that
/// parses the language; back ends consume <see cref="TypedExpr"/>.
/// </summary>
/// <remarks>
/// The symbol table is held by reference: a document's lets are added to it as each is checked, so
/// freezing it would make every reference to one an unknown name. Checking throws on the first error
/// in a fixed visit order, which is observable — <c>#fff + nope</c> reports the unknown name rather
/// than the colour arithmetic, because operands are checked before the operator.
/// </remarks>
public sealed class ExprChecker
{
    private readonly IReadOnlyDictionary<string, ExprType> _symbols;

    public ExprChecker(IReadOnlyDictionary<string, ExprType> symbols)
    {
        _symbols = symbols;
    }

    /// <summary>Checks <paramref name="text"/> and requires it to produce <paramref name="expected"/>.</summary>
    /// <remarks>
    /// The type mismatch carries no expression text, so <see cref="ExprException.ToDiagnostic"/>
    /// renders it as one line. The expression is named by <paramref name="what"/> instead, which
    /// says more than a caret under a value that is the right shape but the wrong type.
    /// </remarks>
    public TypedExpr CheckAs(string text, ExprType expected, string what)
    {
        var checked_ = Check(text);

        if (checked_.Type != expected)
        {
            throw new ExprException(
                $"{what} must be a {ExprFunctions.Describe(expected)} expression, but this one is a {ExprFunctions.Describe(checked_.Type)}.",
                0);
        }

        return checked_;
    }

    public TypedExpr Check(string text)
    {
        try
        {
            return Check(ExprParser.Parse(text));
        }
        catch (ExprException error)
        {
            // The first frame holding the text a caret is measured against. Parsing is inside the
            // try too, or a malformed expression would report without one.
            throw error.WithExpressionText(text);
        }
    }

    private TypedExpr Check(ExprNode node)
    {
        switch (node)
        {
            case NumberExpr number:
                return new TypedNumber(number.Position, number.Value);

            case ColorExpr color:
                return new TypedColor(color.Position, color.R, color.G, color.B, color.A);

            case BooleanExpr boolean:
                return new TypedBoolean(boolean.Position, boolean.Value);

            case IdentifierExpr identifier:
                return CheckIdentifier(identifier);

            case UnaryExpr unary:
                return CheckUnary(unary);

            case BinaryExpr binary:
                return CheckBinary(binary);

            case ConditionalExpr conditional:
                return CheckConditional(conditional);

            case CallExpr call:
                return CheckCall(call);

            default:
                throw new ExprException($"Unsupported expression node {node.GetType().Name}.", node.Position);
        }
    }

    private TypedExpr CheckIdentifier(IdentifierExpr identifier)
    {
        // The symbol table first: a declared name shadows a built-in constant.
        if (_symbols.TryGetValue(identifier.Name, out var declared))
        {
            return new TypedSymbol(declared, identifier.Position, identifier.Name);
        }

        if (ExprFunctions.TryGetConstant(identifier.Name, out var constant, out var constantType))
        {
            return new TypedConstant(constantType, identifier.Position, constant);
        }

        if (ExprFunctions.IsFunction(identifier.Name))
        {
            throw new ExprException($"'{identifier.Name}' is a function; call it with arguments.", identifier.Position);
        }

        var known = _symbols.Keys.Concat(ExprFunctions.ConstantNames).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var hint = known.Count == 0 ? "no names are in scope" : "in scope: " + string.Join(", ", known);

        throw new ExprException($"Unknown name '{identifier.Name}' ({hint}).", identifier.Position);
    }

    private TypedExpr CheckUnary(UnaryExpr unary)
    {
        var operand = Check(unary.Operand);

        if (unary.Op == ExprUnaryOp.Negate)
        {
            Require(operand.Type, ExprType.Number, "'-'", unary.Position);
            return new TypedUnary(ExprType.Number, unary.Position, ExprUnaryOp.Negate, operand);
        }

        Require(operand.Type, ExprType.Boolean, "'!'", unary.Position);
        return new TypedUnary(ExprType.Boolean, unary.Position, ExprUnaryOp.Not, operand);
    }

    private TypedExpr CheckBinary(BinaryExpr binary)
    {
        // Both operands first, so a bad name inside them is reported before the operator's own
        // complaint about their types.
        var left = Check(binary.Left);
        var right = Check(binary.Right);
        var symbol = ExprFunctions.OperatorText(binary.Op);

        switch (binary.Op)
        {
            case ExprBinaryOp.Add:
            case ExprBinaryOp.Subtract:
            case ExprBinaryOp.Multiply:
            case ExprBinaryOp.Divide:
                {
                    if (left.Type == ExprType.Color || right.Type == ExprType.Color)
                    {
                        throw new ExprException(
                            $"'{symbol}' does not apply to colours. Use mix(a, b, t) to blend them.",
                            binary.Position);
                    }

                    Require(left.Type, ExprType.Number, $"'{symbol}'", binary.Position);
                    Require(right.Type, ExprType.Number, $"'{symbol}'", binary.Position);

                    return new TypedBinary(ExprType.Number, binary.Position, binary.Op, left, right);
                }

            case ExprBinaryOp.Less:
            case ExprBinaryOp.LessOrEqual:
            case ExprBinaryOp.Greater:
            case ExprBinaryOp.GreaterOrEqual:
                {
                    Require(left.Type, ExprType.Number, $"'{symbol}'", binary.Position);
                    Require(right.Type, ExprType.Number, $"'{symbol}'", binary.Position);

                    return new TypedBinary(ExprType.Boolean, binary.Position, binary.Op, left, right);
                }

            case ExprBinaryOp.Equal:
            case ExprBinaryOp.NotEqual:
                {
                    if (left.Type != right.Type)
                    {
                        throw new ExprException(
                            $"'{symbol}' compares a {ExprFunctions.Describe(left.Type)} with a {ExprFunctions.Describe(right.Type)}.",
                            binary.Position);
                    }

                    return new TypedBinary(ExprType.Boolean, binary.Position, binary.Op, left, right);
                }

            default:
                {
                    Require(left.Type, ExprType.Boolean, $"'{symbol}'", binary.Position);
                    Require(right.Type, ExprType.Boolean, $"'{symbol}'", binary.Position);

                    return new TypedBinary(ExprType.Boolean, binary.Position, binary.Op, left, right);
                }
        }
    }

    private TypedExpr CheckConditional(ConditionalExpr conditional)
    {
        // The condition before the branches, so `t ? nope : 2` reports the condition.
        var condition = Check(conditional.Condition);

        if (condition.Type != ExprType.Boolean)
        {
            throw new ExprException(
                $"The condition before '?' must be a boolean, but it is a {ExprFunctions.Describe(condition.Type)}.",
                conditional.Position);
        }

        var whenTrue = Check(conditional.WhenTrue);
        var whenFalse = Check(conditional.WhenFalse);

        if (whenTrue.Type != whenFalse.Type)
        {
            throw new ExprException(
                $"Both branches of '?:' must have the same type, but they are {ExprFunctions.Describe(whenTrue.Type)} and {ExprFunctions.Describe(whenFalse.Type)}.",
                conditional.Position);
        }

        return new TypedConditional(whenTrue.Type, conditional.Position, condition, whenTrue, whenFalse);
    }

    private TypedExpr CheckCall(CallExpr call)
    {
        // Name and arity before any argument is visited: sin(nope, 2) reports the count.
        if (!ExprFunctions.TryGetFunction(call.Name, out var signature))
        {
            var known = string.Join(", ", ExprFunctions.FunctionNames.OrderBy(k => k, StringComparer.Ordinal));
            throw new ExprException($"Unknown function '{call.Name}'. Available: {known}.", call.Position);
        }

        if (call.Arguments.Count != signature.Parameters.Count)
        {
            throw new ExprException(
                $"'{call.Name}' takes {signature.Parameters.Count} argument(s), but {call.Arguments.Count} were given.",
                call.Position);
        }

        var arguments = new List<TypedExpr>(call.Arguments.Count);

        for (var i = 0; i < call.Arguments.Count; i++)
        {
            var argument = Check(call.Arguments[i]);

            if (argument.Type != signature.Parameters[i])
            {
                throw new ExprException(
                    $"Argument {i + 1} of '{call.Name}' must be a {ExprFunctions.Describe(signature.Parameters[i])}, but it is a {ExprFunctions.Describe(argument.Type)}.",
                    call.Arguments[i].Position);
            }

            arguments.Add(argument);
        }

        return new TypedCall(signature.Result, call.Position, signature.Function, arguments);
    }

    private static void Require(ExprType actual, ExprType expected, string what, int position)
    {
        if (actual != expected)
        {
            throw new ExprException(
                $"{what} expects a {ExprFunctions.Describe(expected)}, but got a {ExprFunctions.Describe(actual)}.",
                position);
        }
    }
}
