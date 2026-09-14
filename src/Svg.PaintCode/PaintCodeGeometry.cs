// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Globalization;

namespace Svg.PaintCode;

/// <summary>A point in PaintCode's document space, which is y-up.</summary>
public readonly struct PaintCodePoint
{
    public PaintCodePoint(double x, double y)
    {
        X = x;
        Y = y;
    }

    public double X { get; }

    public double Y { get; }

    public override string ToString()
        => string.Format(CultureInfo.InvariantCulture, "{{{0}, {1}}}", X, Y);
}

/// <summary>A rectangle in PaintCode's document space.</summary>
public readonly struct PaintCodeRect
{
    public PaintCodeRect(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    public override string ToString()
        => string.Format(CultureInfo.InvariantCulture, "{{{{{0}, {1}}}, {{{2}, {3}}}}}", X, Y, Width, Height);
}
