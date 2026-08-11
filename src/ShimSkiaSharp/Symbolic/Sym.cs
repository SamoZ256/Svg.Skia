// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;

namespace ShimSkiaSharp;

// Pairs a concrete value with an optional description of how it was derived. Value is always
// populated, so back ends that do not care about Expression keep working unchanged.
//
// There is no implicit conversion to T on purpose. It would let every existing arithmetic
// expression keep compiling while silently dropping Expression, which fails by producing
// wrong output rather than a build error.
public readonly struct Sym<T> : IEquatable<Sym<T>>
    where T : struct
{
    public T Value { get; }

    public SymNode? Expression { get; }

    public bool IsSymbolic => Expression is not null;

    public Sym(T value)
    {
        Value = value;
        Expression = null;
    }

    public Sym(T value, SymNode? expression)
    {
        Value = value;
        Expression = expression;
    }

    public static implicit operator Sym<T>(T value) => new(value);

    public Sym<T> WithExpression(SymNode? expression) => new(Value, expression);

    public bool Equals(Sym<T> other)
        => EqualityComparer<T>.Default.Equals(Value, other.Value) &&
           Equals(Expression, other.Expression);

    public override bool Equals(object? obj) => obj is Sym<T> other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = EqualityComparer<T>.Default.GetHashCode(Value!);
            hash = (hash * 397) ^ (Expression?.GetHashCode() ?? 0);
            return hash;
        }
    }

    public static bool operator ==(Sym<T> left, Sym<T> right) => left.Equals(right);

    public static bool operator !=(Sym<T> left, Sym<T> right) => !left.Equals(right);

    public override string ToString()
        => Expression is null ? $"{Value}" : $"{Value} [{Expression}]";
}
