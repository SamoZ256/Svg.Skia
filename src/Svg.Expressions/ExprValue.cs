// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Svg.Expressions;

/// <summary>
/// A value of the expression language: a number, an integer, a colour, a boolean, a string, or a
/// list of numbers or of colours.
/// </summary>
/// <remarks>
/// A number is a <see cref="float"/> even though <see cref="TypedNumber"/> carries a double: the C#
/// back end computes in float, and the two back ends may not disagree about the same document. A
/// colour is four bytes rather than a renderer's type, so the language stays free of Skia.
/// </remarks>
/// <remarks>
/// An integer has a field of its own rather than riding in <c>_number</c>: a float stops counting by
/// one above 2^24, so 16777217 would come back as 16777216.
/// </remarks>
/// <remarks>
/// A string, a list of numbers and a list of colours share the one reference field, each list as an
/// array copied on the way in, so the struct stays a value however its caller's list changes after.
/// A list's colours are packed 0xRRGGBBAA, the lexer's own spelling of one.
/// </remarks>
public readonly struct ExprValue : IEquatable<ExprValue>
{
    private readonly float _number;
    private readonly int _integer;
    private readonly byte _r;
    private readonly byte _g;
    private readonly byte _b;
    private readonly byte _a;
    private readonly bool _boolean;
    private readonly object? _reference;

    private ExprValue(ExprType type, float number, int integer, byte r, byte g, byte b, byte a, bool boolean, object? reference)
    {
        Type = type;
        _number = number;
        _integer = integer;
        _r = r;
        _g = g;
        _b = b;
        _a = a;
        _boolean = boolean;
        _reference = reference;
    }

    public ExprType Type { get; }

    public static ExprValue Number(float value)
        => new(ExprType.Number, value, 0, 0, 0, 0, 0, false, null);

    public static ExprValue Integer(int value)
        => new(ExprType.Integer, 0f, value, 0, 0, 0, 0, false, null);

    public static ExprValue Color(byte r, byte g, byte b, byte a)
        => new(ExprType.Color, 0f, 0, r, g, b, a, false, null);

    public static ExprValue Boolean(bool value)
        => new(ExprType.Boolean, 0f, 0, 0, 0, 0, 0, value, null);

    public static ExprValue String(string value)
        => new(ExprType.String, 0f, 0, 0, 0, 0, 0, false, value ?? throw new ArgumentNullException(nameof(value)));

    public static ExprValue NumberList(IEnumerable<float> values)
        => new(ExprType.NumberList, 0f, 0, 0, 0, 0, 0, false, (values ?? throw new ArgumentNullException(nameof(values))).ToArray());

    /// <param name="colours">Each one a value made by <see cref="Color"/>.</param>
    public static ExprValue ColorList(IEnumerable<ExprValue> colours)
        => new(
            ExprType.ColorList,
            0f, 0, 0, 0, 0, 0, false,
            (colours ?? throw new ArgumentNullException(nameof(colours)))
            .Select(colour => colour.Type == ExprType.Color
                ? Pack(colour._r, colour._g, colour._b, colour._a)
                : throw new ArgumentException($"A list of colours cannot hold a {ExprFunctions.Describe(colour.Type)}.", nameof(colours)))
            .ToArray());

    public float AsNumber => Require(ExprType.Number)._number;

    public int AsInteger => Require(ExprType.Integer)._integer;

    public bool AsBoolean => Require(ExprType.Boolean)._boolean;

    public string AsString => (string)Require(ExprType.String)._reference!;

    public IReadOnlyList<float> AsNumberList => Array.AsReadOnly(Numbers(Require(ExprType.NumberList)));

    public IReadOnlyList<ExprValue> AsColorList => Array.AsReadOnly(Array.ConvertAll(Colours(Require(ExprType.ColorList)), Unpack));

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
            ExprType.Integer => _integer == other._integer,
            ExprType.Color => _r == other._r && _g == other._g && _b == other._b && _a == other._a,
            ExprType.Boolean => _boolean == other._boolean,
            ExprType.String => string.Equals((string)_reference!, (string)other._reference!, StringComparison.Ordinal),
            ExprType.NumberList => Numbers(this).SequenceEqual(Numbers(other)),
            ExprType.ColorList => Colours(this).SequenceEqual(Colours(other)),
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
                ExprType.Integer => (hash * 397) ^ _integer,
                ExprType.Color => (((((hash * 397) ^ _r) * 397) ^ _g) * 397 ^ _b) * 397 ^ _a,
                ExprType.Boolean => (hash * 397) ^ (_boolean ? 1 : 0),
                ExprType.String => (hash * 397) ^ StringComparer.Ordinal.GetHashCode((string)_reference!),
                ExprType.NumberList => Numbers(this).Aggregate(hash, (sum, number) => (sum * 397) ^ number.GetHashCode()),
                ExprType.ColorList => Colours(this).Aggregate(hash, (sum, colour) => (sum * 397) ^ (int)colour),
                _ => throw Unknown(Type)
            };

            return hash;
        }
    }

    public override string ToString()
        => Type switch
        {
            ExprType.Number => Spell(_number),
            // The lexer reads -2147483648 as a minus over 2147483648, which no integer holds.
            ExprType.Integer => _integer == int.MinValue ? "-2147483647 - 1" : _integer.ToString(CultureInfo.InvariantCulture),
            ExprType.Color => $"#{_r:x2}{_g:x2}{_b:x2}{_a:x2}",
            ExprType.Boolean => _boolean ? "true" : "false",
            ExprType.String => Quote((string)_reference!),
            ExprType.NumberList => List(Numbers(this).Select(Spell)),
            ExprType.ColorList => List(Colours(this).Select(colour => Unpack(colour).ToString())),
            _ => throw Unknown(Type)
        };

    /// <summary>How many elements a list holds.</summary>
    internal int Count
        => Type == ExprType.ColorList ? Colours(this).Length : Numbers(Require(ExprType.NumberList)).Length;

    /// <summary>The element at <paramref name="index"/>, clamped into the list.</summary>
    /// <remarks>
    /// Never out of range, because a drawing has to draw whatever index it is handed: an empty list
    /// answers zero or transparent, and ExprHelpers.SvgAt answers the same.
    /// </remarks>
    internal ExprValue At(int index)
    {
        var count = Count;
        var at = index < 0 ? 0 : index >= count ? count - 1 : index;

        return Type == ExprType.ColorList
            ? count == 0 ? Color(0, 0, 0, 0) : Unpack(Colours(this)[at])
            : Number(count == 0 ? 0f : Numbers(this)[at]);
    }

    private static string List(IEnumerable<string> elements) => "[" + string.Join(", ", elements) + "]";

    /// <summary>A number in digits the lexer reads back as the same float.</summary>
    /// <remarks>
    /// The language has no exponent, so the point is moved where "R" writes 1E-05, and no literal for
    /// an infinity or a NaN, so those are the divisions that answer them. The lexer narrows a double:
    /// of all 4,278,190,080 finite floats, ±7.038531E-26 alone have shortest digits that narrow to
    /// their neighbour, so the double's own digits, which are exact, are written for those.
    /// </remarks>
    private static string Spell(float number)
    {
        if (float.IsNaN(number))
        {
            return "0 / 0";
        }

        if (float.IsInfinity(number))
        {
            return number > 0f ? "1 / 0" : "-1 / 0";
        }

        var shortest = Plain(number.ToString("R", CultureInfo.InvariantCulture));

        return (float)double.Parse(shortest, NumberStyles.Float, CultureInfo.InvariantCulture) == number
            ? shortest
            : Plain(((double)number).ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>Round-trip digits with any exponent moved into the point.</summary>
    private static string Plain(string written)
    {
        var exponent = written.IndexOf('E');

        if (exponent < 0)
        {
            return written;
        }

        var sign = written[0] == '-' ? "-" : string.Empty;
        var mantissa = written.Substring(sign.Length, exponent - sign.Length);
        var point = mantissa.IndexOf('.');
        var digits = point < 0 ? mantissa : mantissa.Remove(point, 1);
        var at = (point < 0 ? mantissa.Length : point)
                 + int.Parse(written.Substring(exponent + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        return sign + (at <= 0 ? "0." + new string('0', -at) + digits
            : at >= digits.Length ? digits + new string('0', at - digits.Length)
            : digits.Substring(0, at) + "." + digits.Substring(at));
    }

    private static float[] Numbers(ExprValue value) => (float[])value._reference!;

    private static uint[] Colours(ExprValue value) => (uint[])value._reference!;

    private static uint Pack(byte r, byte g, byte b, byte a) => ((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a;

    private static ExprValue Unpack(uint colour)
        => Color((byte)(colour >> 24), (byte)(colour >> 16), (byte)(colour >> 8), (byte)colour);

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
