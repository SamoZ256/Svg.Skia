// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Globalization;
using System.Text;

namespace Svg.Expressions;

/// <summary>
/// A value of the expression language: a number, a colour, a boolean, or a string.
/// </summary>
/// <remarks>
/// A number is a <see cref="float"/> even though <see cref="TypedNumber"/> carries a double: the C#
/// back end computes in float, and the two back ends may not disagree about the same document. A
/// colour is four bytes rather than a renderer's type, so the language stays free of Skia.
/// </remarks>
public readonly struct ExprValue : IEquatable<ExprValue>
{
    private readonly float _number;
    private readonly byte _r;
    private readonly byte _g;
    private readonly byte _b;
    private readonly byte _a;
    private readonly bool _boolean;
    private readonly string? _text;

    private ExprValue(ExprType type, float number, byte r, byte g, byte b, byte a, bool boolean, string? text)
    {
        Type = type;
        _number = number;
        _r = r;
        _g = g;
        _b = b;
        _a = a;
        _boolean = boolean;
        _text = text;
    }

    public ExprType Type { get; }

    public static ExprValue Number(float value)
        => new(ExprType.Number, value, 0, 0, 0, 0, false, null);

    public static ExprValue Color(byte r, byte g, byte b, byte a)
        => new(ExprType.Color, 0f, r, g, b, a, false, null);

    public static ExprValue Boolean(bool value)
        => new(ExprType.Boolean, 0f, 0, 0, 0, 0, value, null);

    public static ExprValue String(string value)
        => new(ExprType.String, 0f, 0, 0, 0, 0, false, value ?? throw new ArgumentNullException(nameof(value)));

    public float AsNumber => Require(ExprType.Number)._number;

    public bool AsBoolean => Require(ExprType.Boolean)._boolean;

    public string AsString => Require(ExprType.String)._text!;

    public byte Red => Require(ExprType.Color)._r;

    public byte Green => Require(ExprType.Color)._g;

    public byte Blue => Require(ExprType.Color)._b;

    public byte Alpha => Require(ExprType.Color)._a;

    /// <summary>The same colour with its alpha replaced, leaving the channels alone.</summary>
    public ExprValue WithAlpha(byte alpha)
    {
        var colour = Require(ExprType.Color);

        return Color(colour._r, colour._g, colour._b, alpha);
    }

    /// <remarks>
    /// Conventional .NET equality, so NaN equals itself and the struct behaves in a dictionary.
    /// This is deliberately *not* what the language's <c>==</c> operator does — that one is float
    /// comparison, where NaN equals nothing, and the evaluator implements it directly rather than
    /// coming through here.
    /// </remarks>
    public bool Equals(ExprValue other)
    {
        if (Type != other.Type)
        {
            return false;
        }

        return Type switch
        {
            ExprType.Number => _number.Equals(other._number),
            ExprType.Color => _r == other._r && _g == other._g && _b == other._b && _a == other._a,
            ExprType.Boolean => _boolean == other._boolean,
            ExprType.String => string.Equals(_text, other._text, StringComparison.Ordinal),
            _ => throw Unknown(Type)
        };
    }

    public override bool Equals(object? obj) => obj is ExprValue other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = (int)Type * 397;

            hash = Type switch
            {
                ExprType.Number => (hash * 397) ^ _number.GetHashCode(),
                ExprType.Color => (((((hash * 397) ^ _r) * 397) ^ _g) * 397 ^ _b) * 397 ^ _a,
                ExprType.Boolean => (hash * 397) ^ (_boolean ? 1 : 0),
                ExprType.String => (hash * 397) ^ StringComparer.Ordinal.GetHashCode(_text!),
                _ => throw Unknown(Type)
            };

            return hash;
        }
    }

    public override string ToString()
        => Type switch
        {
            ExprType.Number => _number.ToString("R", CultureInfo.InvariantCulture),
            ExprType.Color => $"#{_r:x2}{_g:x2}{_b:x2}{_a:x2}",
            ExprType.Boolean => _boolean ? "true" : "false",
            ExprType.String => Quote(_text!),
            _ => throw Unknown(Type)
        };

    /// <summary>A string as a literal of the language, which is how one is written down.</summary>
    /// <remarks>
    /// Single quotes, because an expression is authored inside a double-quoted XML attribute and
    /// this spelling needs no entity there. Public so that anything writing a value back into a
    /// document -- a committed default, a readout -- quotes it the one way the lexer reads back.
    /// </remarks>
    public static string Quote(string text)
    {
        var quoted = new StringBuilder(text.Length + 2);

        quoted.Append('\'');

        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': quoted.Append("\\\\"); break;
                case '\'': quoted.Append("\\'"); break;
                case '\n': quoted.Append("\\n"); break;
                case '\t': quoted.Append("\\t"); break;
                default: quoted.Append(c); break;
            }
        }

        quoted.Append('\'');

        return quoted.ToString();
    }

    private static Exception Unknown(ExprType type)
        => new NotSupportedException($"Unsupported {nameof(ExprType)}: {type}.");

    private ExprValue Require(ExprType type)
    {
        if (Type != type)
        {
            throw new InvalidOperationException(
                $"This value is a {ExprFunctions.Describe(Type)}, not a {ExprFunctions.Describe(type)}.");
        }

        return this;
    }
}
