// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Globalization;

namespace Svg.Expressions;

/// <summary>
/// The range a host should offer for a <see cref="ExprType.Number"/> parameter.
/// </summary>
/// <remarks>
/// Advice, not a constraint: nothing clamps to it, a default outside its own range is legal, and
/// generated C# does not mention it. It exists so a slider has ends the author chose.
/// <see cref="Default"/> is the 0..1 hosts hardcoded before the format could say anything else.
/// </remarks>
public readonly struct SvgExpressionRange : IEquatable<SvgExpressionRange>
{
    /// <summary>The range of a parameter that declares none: 0 to 1, continuous.</summary>
    public static readonly SvgExpressionRange Default = new(0f, 1f, 0f);

    public SvgExpressionRange(float minimum, float maximum, float step)
    {
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
    }

    public float Minimum { get; }

    public float Maximum { get; }

    /// <summary>The increment, or zero when the author declared none and the range is continuous.</summary>
    public float Step { get; }

    public bool HasStep => Step > 0f;

    public bool Equals(SvgExpressionRange other)
        => Minimum.Equals(other.Minimum)
           && Maximum.Equals(other.Maximum)
           && Step.Equals(other.Step);

    public override bool Equals(object? obj) => obj is SvgExpressionRange other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Minimum.GetHashCode();
            hash = (hash * 397) ^ Maximum.GetHashCode();
            hash = (hash * 397) ^ Step.GetHashCode();

            return hash;
        }
    }

    public static bool operator ==(SvgExpressionRange left, SvgExpressionRange right) => left.Equals(right);

    public static bool operator !=(SvgExpressionRange left, SvgExpressionRange right) => !left.Equals(right);

    public override string ToString()
        => HasStep
            ? string.Format(CultureInfo.InvariantCulture, "{0} .. {1} step {2}", Minimum, Maximum, Step)
            : string.Format(CultureInfo.InvariantCulture, "{0} .. {1}", Minimum, Maximum);
}
