// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;

namespace Svg.Expressions;

/// <summary>
/// The colour operations the <i>model</i> applies to a value, as opposed to the ones an author
/// writes.
/// </summary>
/// <remarks>
/// <para>
/// A scene records that it scaled a paint's alpha by an opacity, or converted a colour to linear
/// RGB, and the code generator reproduces those by calling <c>SvgScaleAlpha</c> and
/// <c>SvgToLinearRgb</c> in the emitted source. These are the same two operations for a back end
/// that computes rather than emits.
/// </para>
/// <para>
/// They live here, beside the language, so that everything which has to match <c>ExprHelpers</c>
/// byte for byte is in one assembly and is covered by one differential test — rather than half of it
/// being in whichever project happens to walk the model.
/// </para>
/// </remarks>
public static class ExprColor
{
    /// <summary>
    /// Multiplies a colour's alpha by <paramref name="factor"/>, mirroring
    /// <c>ExprHelpers.SvgScaleAlpha</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately unclamped, and the widening to double is the helper's own: a factor above 1 or
    /// below 0 pushes the product outside a byte, and what the cast then does is whatever the
    /// generated code's identical cast does.
    /// </remarks>
    public static ExprValue ScaleAlpha(ExprValue color, float factor)
        => color.WithAlpha((byte)Math.Round(color.Alpha * (double)factor));

    /// <summary>
    /// Converts a colour's channels from sRGB to linear RGB, mirroring
    /// <c>ExprHelpers.SvgToLinearRgb</c>. Alpha is left alone.
    /// </summary>
    public static ExprValue ToLinearRgb(ExprValue color)
        => ExprValue.Color(
            ToLinearRgbChannel(color.Red),
            ToLinearRgbChannel(color.Green),
            ToLinearRgbChannel(color.Blue),
            color.Alpha);

    // Math.Pow rather than ExprMath.Pow: the helper spells this one in double precision, so there is
    // no MathF to match here and nothing for netstandard2.0 to fall back from.
    private static byte ToLinearRgbChannel(byte value)
    {
        var srgb = value / 255f;
        var linear = srgb <= 0.04045f ? srgb / 12.92f : (float)Math.Pow((srgb + 0.055f) / 1.055f, 2.4f);

        return (byte)Math.Round(linear * 255f);
    }
}
