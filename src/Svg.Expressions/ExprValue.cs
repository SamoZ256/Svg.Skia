// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Globalization;

namespace Svg.Expressions;

/// <summary>
/// A value of the expression language: a number, a colour, or a boolean.
/// </summary>
/// <remarks>
/// <para>
/// A number is a <see cref="float"/>, not a <see cref="double"/>, even though
/// <see cref="TypedNumber"/> carries a double. The C# back end narrows every literal through
/// <c>(float)</c> and computes in <c>float</c>, so evaluating in double would produce a different
/// answer from the generated code for the same document — which is the one thing the two back ends
/// may not do.
/// </para>
/// <para>
/// A colour is four bytes rather than any renderer's colour type, so the language stays free of
/// Skia. The model's own colour types convert at the boundary.
/// </para>
/// </remarks>
public readonly struct ExprValue : IEquatable<ExprValue>
{
    private readonly float _number;
    private readonly byte _r;
    private readonly byte _g;
    private readonly byte _b;
    private readonly byte _a;
    private readonly bool _boolean;

    private ExprValue(ExprType type, float number, byte r, byte g, byte b, byte a, bool boolean)
    {
        Type = type;
        _number = number;
        _r = r;
        _g = g;
        _b = b;
        _a = a;
        _boolean = boolean;
    }

    public ExprType Type { get; }

    public static ExprValue Number(float value)
        => new(ExprType.Number, value, 0, 0, 0, 0, false);

    public static ExprValue Color(byte r, byte g, byte b, byte a)
        => new(ExprType.Color, 0f, r, g, b, a, false);

    public static ExprValue Boolean(bool value)
        => new(ExprType.Boolean, 0f, 0, 0, 0, 0, value);

    public float AsNumber => Require(ExprType.Number)._number;

    public bool AsBoolean => Require(ExprType.Boolean)._boolean;

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
            _ => _boolean == other._boolean
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
                _ => (hash * 397) ^ (_boolean ? 1 : 0)
            };

            return hash;
        }
    }

    public override string ToString()
        => Type switch
        {
            ExprType.Number => _number.ToString("R", CultureInfo.InvariantCulture),
            ExprType.Color => $"#{_r:x2}{_g:x2}{_b:x2}{_a:x2}",
            _ => _boolean ? "true" : "false"
        };

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
