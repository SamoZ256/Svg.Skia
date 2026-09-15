// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;

namespace Svg.PaintCode;

/// <summary>
/// The operations a library colour derives from another by.
/// </summary>
/// <remarks>
/// Ported from the runtime PaintCode ships beside its generated code — <c>PaintCodeColor</c>'s
/// <c>colorByChangingSaturation</c> and <c>colorByApplyingShadow</c> — rather than reinvented, so a
/// derived colour comes out the shade PaintCode draws it. They are worth having: the sample's
/// accentColorOff is accentColorOn desaturated, then shadowed, then given an alpha, and reading only
/// the last of those left every icon that uses it in the accent's own hue.
/// </remarks>
internal static class PaintCodeColorOperations
{
    /// <summary>Blends towards black, which is what PaintCode's shadow does.</summary>
    /// <remarks>
    /// Its <c>colorByBlendingColors</c> blends against black carrying the colour's own alpha, so the
    /// alpha term cancels and only the channels move. The cast it does is a truncation, not a round.
    /// </remarks>
    internal static PaintCodeColor Shadow(PaintCodeColor colour, double amount)
        => new(
            colour.Name,
            (byte)((1 - amount) * colour.Red),
            (byte)((1 - amount) * colour.Green),
            (byte)((1 - amount) * colour.Blue),
            colour.Alpha,
            colour.IsApproximate);

    /// <summary>The same hue and brightness at a different saturation.</summary>
    /// <remarks>
    /// Saturation is a percentage, not a fraction: the sample asks for 0.2 and means two tenths of
    /// one percent, which is why the colour it names is called a grey. That is the scale SkiaSharp's
    /// own <c>ToHsv</c> and <c>FromHsv</c> use, and this has to agree with them because the drawing
    /// it is compared against goes through those.
    /// </remarks>
    internal static PaintCodeColor Saturation(PaintCodeColor colour, double saturation)
    {
        ToHsv(colour, out var hue, out _, out var value);
        FromHsv(hue, saturation, value, out var red, out var green, out var blue);

        return new PaintCodeColor(colour.Name, red, green, blue, colour.Alpha, colour.IsApproximate);
    }

    private static void ToHsv(PaintCodeColor colour, out double hue, out double saturation, out double value)
    {
        var red = colour.Red / 255d;
        var green = colour.Green / 255d;
        var blue = colour.Blue / 255d;
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var delta = max - min;

        hue = 0;

        if (delta > 0)
        {
            if (max == red)
            {
                hue = 60 * (((green - blue) / delta) % 6);
            }
            else if (max == green)
            {
                hue = 60 * (((blue - red) / delta) + 2);
            }
            else
            {
                hue = 60 * (((red - green) / delta) + 4);
            }
        }

        if (hue < 0)
        {
            hue += 360;
        }

        saturation = max <= 0 ? 0 : delta / max * 100;
        value = max * 100;
    }

    private static void FromHsv(double hue, double saturation, double value, out byte red, out byte green, out byte blue)
    {
        var s = Math.Min(100, Math.Max(0, saturation)) / 100;
        var v = Math.Min(100, Math.Max(0, value)) / 100;
        var chroma = v * s;
        var sector = (hue % 360 + 360) % 360 / 60;
        var second = chroma * (1 - Math.Abs((sector % 2) - 1));
        var match = v - chroma;

        var (r, g, b) = (int)sector switch
        {
            0 => (chroma, second, 0d),
            1 => (second, chroma, 0d),
            2 => (0d, chroma, second),
            3 => (0d, second, chroma),
            4 => (second, 0d, chroma),
            _ => (chroma, 0d, second)
        };

        red = Channel(r + match);
        green = Channel(g + match);
        blue = Channel(b + match);
    }

    // Truncated, not rounded, because the SkiaSharp conversion this has to agree with truncates.
    private static byte Channel(double value) => (byte)(Math.Min(1, Math.Max(0, value)) * 255);
}
