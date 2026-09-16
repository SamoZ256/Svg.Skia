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

    /// <summary>
    /// A checker over what <paramref name="declarations"/> puts in scope, lets included.
    /// </summary>
    /// <remarks>
    /// The parameters are a symbol table already; the lets are not, because each one's type is only
    /// known once it has been checked against everything before it. Folding them in is what anything
    /// asking "would this expression work here" has to do first, and it was being written out
    /// wherever that was asked.
    ///
    /// A let that does not check is left out rather than thrown over: what it strands is a fault of
    /// its own, and reporting it in answer to a question about some other expression names the wrong
    /// one.
    /// </remarks>
    public static ExprChecker For(SvgExpressionDeclarations declarations)
    {
        if (declarations is null)
        {
            throw new ArgumentNullException(nameof(declarations));
        }

        var symbols = declarations.CreateSymbolTable();
        var checker = new ExprChecker(symbols);

        foreach (var let in declarations.Lets)
        {
            try
            {
                // Into the table the checker is holding, so each let is in scope for the next.
                symbols[let.Name] = checker.Check(let.Expression).Type;
            }
            catch (ExprException)
            {
            }
        }

        return checker;
    }

    /// <summary>Checks <paramref name="text"/> and requires it to produce <paramref name="expected"/>.</summary>
    /// <remarks>
    /// The type mismatch carries no expression text, so <see cref="ExprException.ToDiagnostic"/>
    /// renders it as one line. The expression is named by <paramref name="what"/> instead, which
    /// says more than a caret under a value that is the right shape but the wrong type.
    /// </remarks>
    public TypedExpr CheckAs(string text, ExprType expected, string what)
    {
        var checked_ = Check(text, expected);

        if (checked_.Type != expected)
        {
            throw new ExprException(
                $"{what} must be a {ExprFunctions.Describe(expected)} expression, but this one is a {ExprFunctions.Describe(checked_.Type)}.",
                0);
        }

        return checked_;
    }

    public TypedExpr Check(string text) => Check(text, expected: null);

    private TypedExpr Check(string text, ExprType? expected)
    {
        try
        {
            return Settle(CheckNode(ExprParser.Parse(text)), expected);
        }
        catch (ExprException error)
        {
            // The first frame holding the text a caret is measured against. Parsing is inside the
            // try too, or a malformed expression would report without one.
            throw error.WithExpressionText(text);
        }
    }

    /// <summary>A checked expression, and whether the context may still decide its type.</summary>
    /// <remarks>
    /// A literal written without a point or a percent is neither numeric type until something says
    /// which. The node carries <see cref="ExprType.Integer"/> meanwhile -- what it would be if the
    /// question were put to the literal alone -- and <see cref="Untyped"/> says the answer is still
    /// open, so an operand beside it, the parameter it is passed to, or the attribute it lands in
    /// can settle it.
    /// </remarks>
    private readonly struct Checked
    {
        public Checked(TypedExpr node, bool untyped = false)
        {
            Node = node;
            Untyped = untyped;
        }

        public TypedExpr Node { get; }

        public bool Untyped { get; }

        public ExprType Type => Node.Type;
    }

    /// <summary>Closes an open type, with the context's answer where there is one.</summary>
    /// <remarks>
    /// Unconstrained, a whole literal is a number. That is what every one of them meant before there
    /// was a second numeric type, so <c>step="1/60"</c> still divides in float and a document that
    /// never mentions an integer cannot acquire one.
    /// </remarks>
    private static TypedExpr Settle(Checked value, ExprType? expected)
        => value.Untyped
            ? Retype(value.Node, expected is ExprType.Integer ? ExprType.Integer : ExprType.Number)
            : value.Node;

    /// <summary>Settles an open operand against the one beside it.</summary>
    /// <remarks>
    /// Against anything but an integer this is a number, so an operator that could never have taken
    /// a whole literal still refuses it in the words it always used.
    /// </remarks>
    private static Checked Against(Checked value, ExprType type)
        => value.Untyped
            ? new Checked(Retype(value.Node, type is ExprType.Integer ? ExprType.Integer : ExprType.Number))
            : value;

    /// <summary>Stamps a concrete type on a tree that was still open.</summary>
    /// <remarks>
    /// Only the open spine is rebuilt -- literals, and the operators written over nothing but
    /// literals. A definite subtree cannot be reached from here, because an operator with one
    /// definite operand was settled where it was checked.
    /// </remarks>
    private static TypedExpr Retype(TypedExpr node, ExprType type)
        => node switch
        {
            TypedInteger integer => type == ExprType.Integer
                ? Fits(integer)
                : new TypedNumber(integer.Position, integer.Value),
            TypedUnary unary => new TypedUnary(type, unary.Position, unary.Op, Retype(unary.Operand, type)),
            TypedBinary binary => new TypedBinary(
                type, binary.Position, binary.Op, Retype(binary.Left, type), Retype(binary.Right, type)),
            TypedConditional conditional => new TypedConditional(
                type,
                conditional.Position,
                conditional.Condition,
                Retype(conditional.WhenTrue, type),
                Retype(conditional.WhenFalse, type)),
            _ => throw new NotSupportedException($"Unsupported {nameof(TypedExpr)}: {node.GetType().Name}.")
        };

    // Checked here rather than at the lexer, which does not yet know whether the literal is going to
    // be an integer at all: 3000000000 is a perfectly good number and only a bad integer.
    private static TypedExpr Fits(TypedInteger integer)
        => integer.Value is >= int.MinValue and <= int.MaxValue
            ? integer
            : throw new ExprException(
                $"{integer.Value} is outside the range of an integer.", integer.Position);

    private Checked CheckNode(ExprNode node)
    {
        switch (node)
        {
            case NumberExpr number:
                return new Checked(new TypedNumber(number.Position, number.Value));

            case IntegerExpr integer:
                return new Checked(new TypedInteger(integer.Position, integer.Value), untyped: true);

            case ColorExpr color:
                return new Checked(new TypedColor(color.Position, color.R, color.G, color.B, color.A));

            case BooleanExpr boolean:
                return new Checked(new TypedBoolean(boolean.Position, boolean.Value));

            case StringExpr text:
                return new Checked(new TypedString(text.Position, text.Value));

            case IdentifierExpr identifier:
                return new Checked(CheckIdentifier(identifier));

            case UnaryExpr unary:
                return CheckUnary(unary);

            case BinaryExpr binary:
                return CheckBinary(binary);

            case ConditionalExpr conditional:
                return CheckConditional(conditional);

            case CallExpr call:
                return new Checked(CheckCall(call));

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

    private Checked CheckUnary(UnaryExpr unary)
    {
        var operand = CheckNode(unary.Operand);

        if (unary.Op == ExprUnaryOp.Negate)
        {
            // Negation does not close the question: -3 is as open as 3, so it can still land in
            // either numeric type.
            if (operand.Type == ExprType.Integer)
            {
                return new Checked(
                    new TypedUnary(ExprType.Integer, unary.Position, ExprUnaryOp.Negate, operand.Node),
                    operand.Untyped);
            }

            Require(operand.Type, ExprType.Number, "'-'", unary.Position);
            return new Checked(new TypedUnary(ExprType.Number, unary.Position, ExprUnaryOp.Negate, operand.Node));
        }

        var settled = Settle(operand, ExprType.Boolean);

        Require(settled.Type, ExprType.Boolean, "'!'", unary.Position);
        return new Checked(new TypedUnary(ExprType.Boolean, unary.Position, ExprUnaryOp.Not, settled));
    }

    private Checked CheckBinary(BinaryExpr binary)
    {
        // Both operands first, so a bad name inside them is reported before the operator's own
        // complaint about their types.
        var left = CheckNode(binary.Left);
        var right = CheckNode(binary.Right);
        var symbol = ExprFunctions.OperatorText(binary.Op);

        // An open operand takes the type of the one beside it: that is what makes `steps + 1` whole
        // and `1/60` in a number slot fractional. Where both are open the answer stays open and
        // travels up, so the context above decides for the whole expression at once.
        if (left.Untyped != right.Untyped)
        {
            left = Against(left, right.Type);
            right = Against(right, left.Type);
        }

        var open = left.Untyped && right.Untyped;

        switch (binary.Op)
        {
            case ExprBinaryOp.Add:
            case ExprBinaryOp.Subtract:
            case ExprBinaryOp.Multiply:
            case ExprBinaryOp.Divide:
                {
                    // Only with another string. '+' between a string and anything else would be a
                    // spelling of "make this text", and the language has no conversions.
                    if (binary.Op == ExprBinaryOp.Add
                        && (left.Type == ExprType.String || right.Type == ExprType.String))
                    {
                        Require(left.Type, ExprType.String, $"'{symbol}'", binary.Position);
                        Require(right.Type, ExprType.String, $"'{symbol}'", binary.Position);

                        return new Checked(
                            new TypedBinary(ExprType.String, binary.Position, binary.Op, left.Node, right.Node));
                    }

                    if (left.Type == ExprType.Color || right.Type == ExprType.Color)
                    {
                        throw new ExprException(
                            $"'{symbol}' does not apply to colours. Use mix(a, b, t) to blend them.",
                            binary.Position);
                    }

                    if (left.Type == ExprType.Integer && right.Type == ExprType.Integer)
                    {
                        return new Checked(
                            new TypedBinary(ExprType.Integer, binary.Position, binary.Op, left.Node, right.Node),
                            open);
                    }

                    Require(left.Type, ExprType.Number, $"'{symbol}'", binary.Position);
                    Require(right.Type, ExprType.Number, $"'{symbol}'", binary.Position);

                    return new Checked(
                        new TypedBinary(ExprType.Number, binary.Position, binary.Op, left.Node, right.Node));
                }

            case ExprBinaryOp.Less:
            case ExprBinaryOp.LessOrEqual:
            case ExprBinaryOp.Greater:
            case ExprBinaryOp.GreaterOrEqual:
                {
                    // The answer is a boolean either way, so a pair that is still open settles to
                    // numbers rather than reaching for an integer comparison nothing asked for.
                    if (open)
                    {
                        left = new Checked(Retype(left.Node, ExprType.Number));
                        right = new Checked(Retype(right.Node, ExprType.Number));
                    }

                    if (left.Type != ExprType.Integer || right.Type != ExprType.Integer)
                    {
                        Require(left.Type, ExprType.Number, $"'{symbol}'", binary.Position);
                        Require(right.Type, ExprType.Number, $"'{symbol}'", binary.Position);
                    }

                    return new Checked(
                        new TypedBinary(ExprType.Boolean, binary.Position, binary.Op, left.Node, right.Node));
                }

            case ExprBinaryOp.Equal:
            case ExprBinaryOp.NotEqual:
                {
                    if (open)
                    {
                        left = new Checked(Retype(left.Node, ExprType.Number));
                        right = new Checked(Retype(right.Node, ExprType.Number));
                    }

                    if (left.Type != right.Type)
                    {
                        throw new ExprException(
                            $"'{symbol}' compares a {ExprFunctions.Describe(left.Type)} with a {ExprFunctions.Describe(right.Type)}.",
                            binary.Position);
                    }

                    return new Checked(
                        new TypedBinary(ExprType.Boolean, binary.Position, binary.Op, left.Node, right.Node));
                }

            default:
                {
                    var l = Settle(left, ExprType.Boolean);
                    var r = Settle(right, ExprType.Boolean);

                    Require(l.Type, ExprType.Boolean, $"'{symbol}'", binary.Position);
                    Require(r.Type, ExprType.Boolean, $"'{symbol}'", binary.Position);

                    return new Checked(new TypedBinary(ExprType.Boolean, binary.Position, binary.Op, l, r));
                }
        }
    }

    private Checked CheckConditional(ConditionalExpr conditional)
    {
        // The condition before the branches, so `t ? nope : 2` reports the condition.
        var condition = Settle(CheckNode(conditional.Condition), ExprType.Boolean);

        if (condition.Type != ExprType.Boolean)
        {
            throw new ExprException(
                $"The condition before '?' must be a boolean, but it is a {ExprFunctions.Describe(condition.Type)}.",
                conditional.Position);
        }

        var whenTrue = CheckNode(conditional.WhenTrue);
        var whenFalse = CheckNode(conditional.WhenFalse);

        // One branch settles the other, so `steps > 0 ? steps : 0` is whole throughout and
        // `t > 0 ? t : 0` is not. Both open leaves the whole conditional open.
        if (whenTrue.Untyped != whenFalse.Untyped)
        {
            whenTrue = Against(whenTrue, whenFalse.Type);
            whenFalse = Against(whenFalse, whenTrue.Type);
        }

        if (whenTrue.Type != whenFalse.Type)
        {
            throw new ExprException(
                $"Both branches of '?:' must have the same type, but they are {ExprFunctions.Describe(whenTrue.Type)} and {ExprFunctions.Describe(whenFalse.Type)}.",
                conditional.Position);
        }

        return new Checked(
            new TypedConditional(whenTrue.Type, conditional.Position, condition, whenTrue.Node, whenFalse.Node),
            whenTrue.Untyped && whenFalse.Untyped);
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
            var argument = Settle(CheckNode(call.Arguments[i]), signature.Parameters[i]);

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
