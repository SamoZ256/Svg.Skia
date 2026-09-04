// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Collections.Generic;

namespace Svg.Expressions;

// Recursive descent over the grammar, loosest binding first:
//
//   conditional := or ( '?' conditional ':' conditional )?
//   or          := and ( '||' and )*
//   and         := equality ( '&&' equality )*
//   equality    := comparison ( ('==' | '!=') comparison )*
//   comparison  := additive ( ('<' | '<=' | '>' | '>=') additive )*
//   additive    := multiplicative ( ('+' | '-') multiplicative )*
//   multiplicative := unary ( ('*' | '/') unary )*
//   unary       := ('-' | '!') unary | primary
//   primary     := number | color | string | identifier | call | '(' conditional ')'
internal static class ExprParser
{
    public static ExprNode Parse(string text)
    {
        var tokens = ExprLexer.Tokenize(text);
        var index = 0;

        if (tokens[index].Kind == ExprTokenKind.End)
        {
            throw new ExprException("The expression is empty.", 0);
        }

        var node = ParseConditional(tokens, ref index);

        if (tokens[index].Kind != ExprTokenKind.End)
        {
            throw new ExprException($"Unexpected '{tokens[index].Text}' after the end of the expression.", tokens[index].Position);
        }

        return node;
    }

    private static ExprNode ParseConditional(IReadOnlyList<ExprToken> tokens, ref int index)
    {
        var condition = ParseOr(tokens, ref index);

        if (tokens[index].Kind != ExprTokenKind.Question)
        {
            return condition;
        }

        var position = tokens[index].Position;
        index++;

        var whenTrue = ParseConditional(tokens, ref index);

        if (tokens[index].Kind != ExprTokenKind.Colon)
        {
            throw new ExprException("Expected ':' to complete the conditional.", tokens[index].Position);
        }

        index++;
        var whenFalse = ParseConditional(tokens, ref index);

        return new ConditionalExpr(position, condition, whenTrue, whenFalse);
    }

    private static ExprNode ParseOr(IReadOnlyList<ExprToken> tokens, ref int index)
    {
        var left = ParseAnd(tokens, ref index);

        while (tokens[index].Kind == ExprTokenKind.Or)
        {
            var position = tokens[index].Position;
            index++;
            left = new BinaryExpr(position, ExprBinaryOp.Or, left, ParseAnd(tokens, ref index));
        }

        return left;
    }

    private static ExprNode ParseAnd(IReadOnlyList<ExprToken> tokens, ref int index)
    {
        var left = ParseEquality(tokens, ref index);

        while (tokens[index].Kind == ExprTokenKind.And)
        {
            var position = tokens[index].Position;
            index++;
            left = new BinaryExpr(position, ExprBinaryOp.And, left, ParseEquality(tokens, ref index));
        }

        return left;
    }

    private static ExprNode ParseEquality(IReadOnlyList<ExprToken> tokens, ref int index)
    {
        var left = ParseComparison(tokens, ref index);

        while (tokens[index].Kind is ExprTokenKind.Equal or ExprTokenKind.NotEqual)
        {
            var token = tokens[index];
            index++;
            var op = token.Kind == ExprTokenKind.Equal ? ExprBinaryOp.Equal : ExprBinaryOp.NotEqual;
            left = new BinaryExpr(token.Position, op, left, ParseComparison(tokens, ref index));
        }

        return left;
    }

    private static ExprNode ParseComparison(IReadOnlyList<ExprToken> tokens, ref int index)
    {
        var left = ParseAdditive(tokens, ref index);

        while (tokens[index].Kind is ExprTokenKind.Less or ExprTokenKind.LessOrEqual
               or ExprTokenKind.Greater or ExprTokenKind.GreaterOrEqual)
        {
            var token = tokens[index];
            index++;

            var op = token.Kind switch
            {
                ExprTokenKind.Less => ExprBinaryOp.Less,
                ExprTokenKind.LessOrEqual => ExprBinaryOp.LessOrEqual,
                ExprTokenKind.Greater => ExprBinaryOp.Greater,
                _ => ExprBinaryOp.GreaterOrEqual
            };

            left = new BinaryExpr(token.Position, op, left, ParseAdditive(tokens, ref index));
        }

        return left;
    }

    private static ExprNode ParseAdditive(IReadOnlyList<ExprToken> tokens, ref int index)
    {
        var left = ParseMultiplicative(tokens, ref index);

        while (tokens[index].Kind is ExprTokenKind.Plus or ExprTokenKind.Minus)
        {
            var token = tokens[index];
            index++;
            var op = token.Kind == ExprTokenKind.Plus ? ExprBinaryOp.Add : ExprBinaryOp.Subtract;
            left = new BinaryExpr(token.Position, op, left, ParseMultiplicative(tokens, ref index));
        }

        return left;
    }

    private static ExprNode ParseMultiplicative(IReadOnlyList<ExprToken> tokens, ref int index)
    {
        var left = ParseUnary(tokens, ref index);

        while (tokens[index].Kind is ExprTokenKind.Star or ExprTokenKind.Slash)
        {
            var token = tokens[index];
            index++;
            var op = token.Kind == ExprTokenKind.Star ? ExprBinaryOp.Multiply : ExprBinaryOp.Divide;
            left = new BinaryExpr(token.Position, op, left, ParseUnary(tokens, ref index));
        }

        return left;
    }

    private static ExprNode ParseUnary(IReadOnlyList<ExprToken> tokens, ref int index)
    {
        var token = tokens[index];

        if (token.Kind == ExprTokenKind.Minus)
        {
            index++;
            return new UnaryExpr(token.Position, ExprUnaryOp.Negate, ParseUnary(tokens, ref index));
        }

        if (token.Kind == ExprTokenKind.Not)
        {
            index++;
            return new UnaryExpr(token.Position, ExprUnaryOp.Not, ParseUnary(tokens, ref index));
        }

        return ParsePrimary(tokens, ref index);
    }

    private static ExprNode ParsePrimary(IReadOnlyList<ExprToken> tokens, ref int index)
    {
        var token = tokens[index];

        switch (token.Kind)
        {
            case ExprTokenKind.Number:
                index++;
                return new NumberExpr(token.Position, token.Number);

            case ExprTokenKind.Color:
                index++;
                return new ColorExpr(
                    token.Position,
                    (byte)((token.Color >> 24) & 0xFF),
                    (byte)((token.Color >> 16) & 0xFF),
                    (byte)((token.Color >> 8) & 0xFF),
                    (byte)(token.Color & 0xFF));

            case ExprTokenKind.String:
                index++;
                return new StringExpr(token.Position, token.Value!);

            case ExprTokenKind.Identifier:
                index++;

                if (token.Text == "true" || token.Text == "false")
                {
                    return new BooleanExpr(token.Position, token.Text == "true");
                }

                if (tokens[index].Kind != ExprTokenKind.OpenParen)
                {
                    return new IdentifierExpr(token.Position, token.Text);
                }

                index++;
                var arguments = new List<ExprNode>();

                if (tokens[index].Kind != ExprTokenKind.CloseParen)
                {
                    while (true)
                    {
                        arguments.Add(ParseConditional(tokens, ref index));

                        if (tokens[index].Kind != ExprTokenKind.Comma)
                        {
                            break;
                        }

                        index++;
                    }
                }

                if (tokens[index].Kind != ExprTokenKind.CloseParen)
                {
                    throw new ExprException($"Expected ')' to close the call to '{token.Text}'.", tokens[index].Position);
                }

                index++;
                return new CallExpr(token.Position, token.Text, arguments);

            case ExprTokenKind.OpenParen:
                index++;
                var inner = ParseConditional(tokens, ref index);

                if (tokens[index].Kind != ExprTokenKind.CloseParen)
                {
                    throw new ExprException("Expected ')'.", tokens[index].Position);
                }

                index++;
                return inner;

            case ExprTokenKind.End:
                throw new ExprException("The expression ends unexpectedly.", token.Position);

            default:
                throw new ExprException($"Unexpected '{token.Text}'.", token.Position);
        }
    }
}
