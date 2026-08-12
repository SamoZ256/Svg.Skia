// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Svg.CodeGen.Skia.Expressions;

internal enum ExprTokenKind
{
    Number,
    Color,
    Identifier,
    Plus,
    Minus,
    Star,
    Slash,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Equal,
    NotEqual,
    And,
    Or,
    Not,
    Question,
    Colon,
    Comma,
    OpenParen,
    CloseParen,
    End
}

internal readonly struct ExprToken
{
    public ExprToken(ExprTokenKind kind, int position, string text, double number = 0, uint color = 0)
    {
        Kind = kind;
        Position = position;
        Text = text;
        Number = number;
        Color = color;
    }

    public ExprTokenKind Kind { get; }

    public int Position { get; }

    public string Text { get; }

    public double Number { get; }

    // Packed 0xRRGGBBAA, only meaningful for Color tokens.
    public uint Color { get; }
}

internal static class ExprLexer
{
    // Word forms of the operators. XML requires escaping '<' and '&', which makes the symbolic
    // spellings awkward to author inside an SVG; these read the same and need no escaping. The
    // full set is provided rather than only the two affected ones, so the language does not have
    // a word form for '<' but not for '>'.
    private static readonly Dictionary<string, ExprTokenKind> s_keywords = new(StringComparer.Ordinal)
    {
        ["and"] = ExprTokenKind.And,
        ["or"] = ExprTokenKind.Or,
        ["not"] = ExprTokenKind.Not,
        ["lt"] = ExprTokenKind.Less,
        ["le"] = ExprTokenKind.LessOrEqual,
        ["gt"] = ExprTokenKind.Greater,
        ["ge"] = ExprTokenKind.GreaterOrEqual,
        ["eq"] = ExprTokenKind.Equal,
        ["ne"] = ExprTokenKind.NotEqual
    };

    public static bool IsKeyword(string name) => s_keywords.ContainsKey(name);

    public static IEnumerable<string> Keywords => s_keywords.Keys;

    public static List<ExprToken> Tokenize(string text)
    {
        var tokens = new List<ExprToken>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;

            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                tokens.Add(ReadNumber(text, ref i));
                continue;
            }

            if (c == '#')
            {
                tokens.Add(ReadColor(text, ref i));
                continue;
            }

            if (c == '_' || char.IsLetter(c))
            {
                while (i < text.Length && (text[i] == '_' || char.IsLetterOrDigit(text[i])))
                {
                    i++;
                }

                var word = text.Substring(start, i - start);

                tokens.Add(s_keywords.TryGetValue(word, out var keyword)
                    ? new ExprToken(keyword, start, word)
                    : new ExprToken(ExprTokenKind.Identifier, start, word));
                continue;
            }

            switch (c)
            {
                case '+': tokens.Add(Single(ExprTokenKind.Plus, start, "+")); i++; continue;
                case '-': tokens.Add(Single(ExprTokenKind.Minus, start, "-")); i++; continue;
                case '*': tokens.Add(Single(ExprTokenKind.Star, start, "*")); i++; continue;
                case '/': tokens.Add(Single(ExprTokenKind.Slash, start, "/")); i++; continue;
                case '?': tokens.Add(Single(ExprTokenKind.Question, start, "?")); i++; continue;
                case ':': tokens.Add(Single(ExprTokenKind.Colon, start, ":")); i++; continue;
                case ',': tokens.Add(Single(ExprTokenKind.Comma, start, ",")); i++; continue;
                case '(': tokens.Add(Single(ExprTokenKind.OpenParen, start, "(")); i++; continue;
                case ')': tokens.Add(Single(ExprTokenKind.CloseParen, start, ")")); i++; continue;

                case '<':
                    i++;
                    if (i < text.Length && text[i] == '=')
                    {
                        i++;
                        tokens.Add(Single(ExprTokenKind.LessOrEqual, start, "<="));
                    }
                    else
                    {
                        tokens.Add(Single(ExprTokenKind.Less, start, "<"));
                    }

                    continue;

                case '>':
                    i++;
                    if (i < text.Length && text[i] == '=')
                    {
                        i++;
                        tokens.Add(Single(ExprTokenKind.GreaterOrEqual, start, ">="));
                    }
                    else
                    {
                        tokens.Add(Single(ExprTokenKind.Greater, start, ">"));
                    }

                    continue;

                case '=':
                    i++;
                    if (i < text.Length && text[i] == '=')
                    {
                        i++;
                        tokens.Add(Single(ExprTokenKind.Equal, start, "=="));
                        continue;
                    }

                    throw new ExprException("Expected '==' for equality; a single '=' is not an operator.", start);

                case '!':
                    i++;
                    if (i < text.Length && text[i] == '=')
                    {
                        i++;
                        tokens.Add(Single(ExprTokenKind.NotEqual, start, "!="));
                    }
                    else
                    {
                        tokens.Add(Single(ExprTokenKind.Not, start, "!"));
                    }

                    continue;

                case '&':
                    i++;
                    if (i < text.Length && text[i] == '&')
                    {
                        i++;
                        tokens.Add(Single(ExprTokenKind.And, start, "&&"));
                        continue;
                    }

                    throw new ExprException("Expected '&&'.", start);

                case '|':
                    i++;
                    if (i < text.Length && text[i] == '|')
                    {
                        i++;
                        tokens.Add(Single(ExprTokenKind.Or, start, "||"));
                        continue;
                    }

                    throw new ExprException("Expected '||'.", start);

                case '%':
                    throw new ExprException(
                        "'%' is only valid directly after a number, as in '55%'. Use mod(a, b) for the remainder.",
                        start);

                default:
                    throw new ExprException($"Unexpected character '{c}'.", start);
            }
        }

        tokens.Add(new ExprToken(ExprTokenKind.End, text.Length, string.Empty));

        return tokens;
    }

    private static ExprToken Single(ExprTokenKind kind, int position, string text)
        => new(kind, position, text);

    private static ExprToken ReadNumber(string text, ref int i)
    {
        var start = i;

        while (i < text.Length && char.IsDigit(text[i]))
        {
            i++;
        }

        if (i < text.Length && text[i] == '.')
        {
            i++;
            while (i < text.Length && char.IsDigit(text[i]))
            {
                i++;
            }
        }

        var literal = text.Substring(start, i - start);

        if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new ExprException($"'{literal}' is not a valid number.", start);
        }

        // A percent sign is a suffix on the literal, never an operator, so that 55% reads as a
        // fraction without making 'a % b' ambiguous.
        if (i < text.Length && text[i] == '%')
        {
            i++;
            value /= 100d;
        }

        return new ExprToken(ExprTokenKind.Number, start, literal, value);
    }

    private static ExprToken ReadColor(string text, ref int i)
    {
        var start = i;
        i++;

        var digits = new StringBuilder();
        while (i < text.Length && Uri.IsHexDigit(text[i]))
        {
            digits.Append(text[i]);
            i++;
        }

        var hex = digits.ToString();

        byte r, g, b, a = 0xFF;

        switch (hex.Length)
        {
            case 3:
                r = Repeat(hex[0]);
                g = Repeat(hex[1]);
                b = Repeat(hex[2]);
                break;

            case 4:
                r = Repeat(hex[0]);
                g = Repeat(hex[1]);
                b = Repeat(hex[2]);
                a = Repeat(hex[3]);
                break;

            case 6:
                r = Byte(hex, 0);
                g = Byte(hex, 2);
                b = Byte(hex, 4);
                break;

            case 8:
                r = Byte(hex, 0);
                g = Byte(hex, 2);
                b = Byte(hex, 4);
                a = Byte(hex, 6);
                break;

            default:
                throw new ExprException(
                    $"'#{hex}' is not a colour. Expected 3, 4, 6 or 8 hex digits.",
                    start);
        }

        var packed = ((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a;

        return new ExprToken(ExprTokenKind.Color, start, "#" + hex, 0, packed);
    }

    private static byte Repeat(char c)
    {
        var value = Convert.ToByte(c.ToString() + c, 16);
        return value;
    }

    private static byte Byte(string hex, int offset)
        => Convert.ToByte(hex.Substring(offset, 2), 16);
}
