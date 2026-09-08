// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Linq;
using Svg.Expressions;
using Svg.Skia;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// The ring around an element whose transform an expression drives.
/// </summary>
/// <remarks>
/// The scene holds the matrix the drawing was compiled with, and binding a value rewrites the
/// recorded drawing rather than the scene — so a ring read straight off the scene sits where the
/// element was written rather than where it is.
/// </remarks>
public class SvgViewerOutlineExpressionTests
{
    private const string Ns = "https://svg.skia/expr/1.0";

    private static string Driven(string transform, string declarations)
        => $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" viewBox="0 0 100 100" width="100" height="100">
              <defs><e:code>{declarations}</e:code></defs>
              <rect id="bar" x="10" y="46" width="60" height="8" transform="{transform}" fill="#c0392b" />
            </svg>
            """;

    private static SKSvg Load(string markup)
    {
        var svg = new SKSvg();
        Assert.NotNull(svg.FromSvg(markup));

        return svg;
    }

    private static SvgElement Bar(SKSvg svg)
        => svg.SourceDocument!.Descendants().Single(element => element.ID == "bar");

    private static SkiaSharp.SKRect Ring(SKSvg svg)
    {
        using var outline = SvgViewerOutline.Of(svg, Bar(svg));
        Assert.NotNull(outline);

        return outline!.Bounds;
    }

    private static Dictionary<string, ExprValue> Values(params (string Name, ExprValue Value)[] values)
        => values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);

    [Fact]
    public void A_Bound_Rotation_Moves_The_Ring_To_Where_It_Is_Drawn()
    {
        var svg = Load(Driven("rotate({{ angle }} 50 50)", """<e:param name="angle" type="number" default="0" />"""));

        var still = Ring(svg);

        svg.SetExpressionValues(Values(("angle", ExprValue.Number(90f))));

        var turned = Ring(svg);

        Assert.NotEqual(still, turned);

        // Not merely different: the same ring the document draws with the value written as a literal.
        var literal = Ring(Load(Driven("rotate(90 50 50)", """<e:param name="angle" type="number" default="0" />""")));

        Assert.Equal(literal.Left, turned.Left, 3);
        Assert.Equal(literal.Top, turned.Top, 3);
        Assert.Equal(literal.Right, turned.Right, 3);
        Assert.Equal(literal.Bottom, turned.Bottom, 3);
    }

    [Fact]
    public void A_Bound_Translation_Moves_The_Ring_By_What_Was_Bound()
    {
        var svg = Load(Driven("translate({{ dx }}, 0)", """<e:param name="dx" type="number" default="0" />"""));

        var still = Ring(svg);

        svg.SetExpressionValues(Values(("dx", ExprValue.Number(20f))));

        var moved = Ring(svg);

        Assert.Equal(still.Left + 20f, moved.Left, 3);
        Assert.Equal(still.Top, moved.Top, 3);
    }

    /// <summary>Unbound, the ring is where the drawing was compiled — which is what it draws.</summary>
    [Fact]
    public void An_Unbound_Drawing_Is_Ringed_Where_It_Was_Compiled()
    {
        var svg = Load(Driven("translate({{ dx }}, 0)", """<e:param name="dx" type="number" default="0" />"""));

        var literal = Ring(Load(Driven("translate(0, 0)", """<e:param name="dx" type="number" default="0" />""")));

        Assert.Equal(literal.Left, Ring(svg).Left, 3);
    }
}
