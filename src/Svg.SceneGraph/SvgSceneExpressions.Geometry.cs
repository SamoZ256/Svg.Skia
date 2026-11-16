// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;
using System.Globalization;
using ShimSkiaSharp;
using Svg;
using Svg.Model.Services;

namespace Svg.Skia;

internal static partial class SvgSceneExpressions
{
    /// <summary>The first geometry attribute an expression drives on <paramref name="element"/>, or null.</summary>
    /// <remarks>Read off the table, so a shape attribute added there is found here without a list of its own.</remarks>
    internal static string? DrivenGeometry(SvgElement element)
    {
        if (element.CustomAttributes is not { Count: > 0 } attributes)
        {
            return null;
        }

        foreach (var name in SvgExpressionAttributes.Supported)
        {
            if (!SvgExpressionAttributes.IsSupported(name, elementName: null) &&
                SvgExpressionAttributes.IsSupported(name, element.ElementName) &&
                SvgExpressionAttributes.Lifted(attributes, name) is { })
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// The shape a clip draws from <paramref name="element"/>, as commands whose numbers may be
    /// expressions.
    /// </summary>
    /// <returns>
    /// False where nothing drives the shape, or where what drives it cannot be read — then the shape
    /// is compiled from its stand-in, as an unreadable transform is, and the audit says why. Otherwise
    /// true, with <paramref name="geometry"/> null where the shape draws no part whatever is bound,
    /// as the same shape written with literals would not.
    /// </returns>
    /// <remarks>
    /// Each reader is the compile it stands in for with the numbers kept symbolic: path data reads as
    /// <c>PathingService.ToPath</c> does, a rect and a circle from their attributes alone as the clip
    /// compiler reads them, an ellipse through the cascade as <c>SvgGeometryService</c> does. Whatever
    /// depends on the values is left to <see cref="SymPath.Apply"/>.
    /// </remarks>
    internal static bool TryGetGeometry(SvgVisualElement element, SvgFillRule fillRule, SKRect viewport, out SymPath? geometry)
    {
        geometry = null;

        if (DrivenGeometry(element) is null)
        {
            return false;
        }

        var fillType = fillRule == SvgFillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;

        switch (element)
        {
            case SvgPath path:
                return TryGetPathData(path, fillType, out geometry);

            case SvgRectangle rect:
                {
                    // As written rather than through the getters, which borrow the other radius
                    // where one is nought: SymPath.Apply borrows once the values are known, from
                    // each radius measured on the other axis as well.
                    var rx = Written(rect, "rx");
                    var ry = Written(rect, "ry");

                    geometry = Shape(fillType, SymPathOp.Rect,
                        Number(rect, "x", rect.X, UnitRenderingType.Horizontal, viewport),
                        Number(rect, "y", rect.Y, UnitRenderingType.Vertical, viewport),
                        Number(rect, "width", rect.Width, UnitRenderingType.Horizontal, viewport),
                        Number(rect, "height", rect.Height, UnitRenderingType.Vertical, viewport),
                        Number(rect, "rx", rx, UnitRenderingType.Horizontal, viewport),
                        Number(rect, "ry", ry, UnitRenderingType.Vertical, viewport),
                        Number(rect, "rx", rx, UnitRenderingType.Vertical, viewport),
                        Number(rect, "ry", ry, UnitRenderingType.Horizontal, viewport));
                    return true;
                }

            case SvgCircle circle:
                geometry = Shape(fillType, SymPathOp.Circle,
                    Number(circle, "cx", circle.CenterX, UnitRenderingType.Horizontal, viewport),
                    Number(circle, "cy", circle.CenterY, UnitRenderingType.Vertical, viewport),
                    Number(circle, "r", circle.Radius, UnitRenderingType.Other, viewport));
                return true;

            case SvgEllipse ellipse:
                return TryGetEllipse(ellipse, fillType, viewport, out geometry);

            default:
                return false;
        }
    }

    private static SvgUnit Written(SvgElement element, string name)
        => element.Attributes.TryGetValue(name, out var value) && value is SvgUnit unit ? unit : new SvgUnit(0f);

    private static SymPath Shape(SKPathFillType fillType, SymPathOp op, params SymNode[] arguments)
        => new(fillType, new[] { new SymPathCommand(op, arguments) });

    private static SymNode Number(SvgElement element, string name, SvgUnit unit, UnitRenderingType type, SKRect viewport)
        => Own(element, name) is { } expression
            ? SymNode.Source(expression)
            : SymNode.Literal(unit.ToDeviceValue(type, element, viewport));

    private static bool TryGetEllipse(SvgEllipse ellipse, SKPathFillType fillType, SKRect viewport, out SymPath? geometry)
    {
        geometry = null;

        var css = ellipse.MayHaveGeometryLengthCssDeclarations();
        var cx = Computed(ellipse, "cx", ellipse.CenterX, UnitRenderingType.Horizontal, css, viewport, out _);
        var cy = Computed(ellipse, "cy", ellipse.CenterY, UnitRenderingType.Vertical, css, viewport, out _);
        var rx = Computed(ellipse, "rx", ellipse.RadiusX, UnitRenderingType.Horizontal, css, viewport, out var rxAuto);
        var ry = Computed(ellipse, "ry", ellipse.RadiusY, UnitRenderingType.Vertical, css, viewport, out var ryAuto);

        if (rxAuto && ryAuto)
        {
            return true;
        }

        geometry = Shape(fillType, SymPathOp.Ellipse, cx, cy, rxAuto ? ry : rx, ryAuto ? rx : ry);

        return true;
    }

    // A driven radius is never auto: the expression is the value.
    private static SymNode Computed(SvgEllipse ellipse, string name, SvgUnit unit, UnitRenderingType type, bool css, SKRect viewport, out bool isAuto)
    {
        isAuto = false;

        if (Own(ellipse, name) is { } expression)
        {
            return SymNode.Source(expression);
        }

        return SymNode.Literal(
            SvgGeometryService.GetComputedUnit(ellipse, name, unit, css, out isAuto, defaultAuto: name is "rx" or "ry")
                .ToDeviceValue(type, ellipse, viewport));
    }

    /// <summary>Path data, segment by segment as <c>PathingService.ToPath</c> converts it.</summary>
    /// <remarks>
    /// Everything that depends only on which segments are written — where the current point comes
    /// from, which control point a smooth curve reflects, where a path stops — is decided here, as
    /// the converter decides it, and the commands come out absolute. A coordinate relative to a
    /// driven one is the sum of the two, folded where both are known, so a literal path reads the
    /// float it always did. The reflection is <c>m + (m − p)</c>, which is the float
    /// <c>PathingService.Reflect</c> computes down either of its branches.
    /// </remarks>
    private static bool TryGetPathData(SvgPath element, SKPathFillType fillType, out SymPath? geometry)
    {
        geometry = null;

        if (Own(element, SvgTransformExpression.PathDataName) is not { } text)
        {
            return false;
        }

        var written = SvgTransformExpression.Parse(SvgTransformExpression.PathDataName, text);

        if (written.Stray.Count > 0)
        {
            return false;
        }

        var segments = written.Functions;

        if (segments.Count == 0)
        {
            return true;
        }

        var commands = new List<SymPathCommand>();
        var hasCurrentPoint = false;
        var hasRenderableCommand = false;
        SymNode x = SymNode.Zero, y = SymNode.Zero, moveX = SymNode.Zero, moveY = SymNode.Zero;
        SymNode cubicX = SymNode.Zero, cubicY = SymNode.Zero, quadX = SymNode.Zero, quadY = SymNode.Zero;
        var hasCubic = false;
        var hasQuad = false;

        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var command = segment.Name[0];
            var relative = char.IsLower(command);

            if (!TryReadNumbers(segment.Arguments, out var n))
            {
                return false;
            }

            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    if (index == segments.Count - 1)
                    {
                        geometry = new SymPath(fillType, commands);
                        return true;
                    }

                    x = moveX = Absolute(n[0], relative, x);
                    y = moveY = Absolute(n[1], relative, y);
                    commands.Add(Command(SymPathOp.MoveTo, x, y));
                    hasCurrentPoint = true;
                    hasCubic = hasQuad = false;
                    break;

                case 'L':
                case 'H':
                case 'V':
                    if (!hasCurrentPoint)
                    {
                        return true;
                    }

                    var lineX = command is 'V' or 'v' ? x : Absolute(n[0], relative, x);
                    var lineY = command is 'H' or 'h' ? y : Absolute(n[command is 'V' or 'v' ? 0 : 1], relative, y);
                    x = lineX;
                    y = lineY;
                    commands.Add(Command(SymPathOp.LineTo, x, y));
                    hasRenderableCommand = true;
                    hasCubic = hasQuad = false;
                    break;

                case 'C':
                case 'S':
                    {
                        if (!hasCurrentPoint)
                        {
                            return true;
                        }

                        var smooth = command is 'S' or 's';
                        var offset = smooth ? 0 : 2;
                        var firstX = smooth ? hasCubic ? Reflect(cubicX, x) : x : Absolute(n[0], relative, x);
                        var firstY = smooth ? hasCubic ? Reflect(cubicY, y) : y : Absolute(n[1], relative, y);
                        var endX = Absolute(n[offset + 2], relative, x);
                        var endY = Absolute(n[offset + 3], relative, y);
                        cubicX = Absolute(n[offset], relative, x);
                        cubicY = Absolute(n[offset + 1], relative, y);
                        commands.Add(Command(SymPathOp.CubicTo, firstX, firstY, cubicX, cubicY, endX, endY));
                        x = endX;
                        y = endY;
                        hasRenderableCommand = true;
                        hasCubic = true;
                        hasQuad = false;
                        break;
                    }

                case 'Q':
                case 'T':
                    {
                        if (!hasCurrentPoint)
                        {
                            return true;
                        }

                        var smooth = command is 'T' or 't';
                        var offset = smooth ? 0 : 2;
                        quadX = smooth ? hasQuad ? Reflect(quadX, x) : x : Absolute(n[0], relative, x);
                        quadY = smooth ? hasQuad ? Reflect(quadY, y) : y : Absolute(n[1], relative, y);
                        var endX = Absolute(n[offset], relative, x);
                        var endY = Absolute(n[offset + 1], relative, y);
                        commands.Add(Command(SymPathOp.QuadTo, quadX, quadY, endX, endY));
                        x = endX;
                        y = endY;
                        hasRenderableCommand = true;
                        hasQuad = true;
                        hasCubic = false;
                        break;
                    }

                case 'A':
                    {
                        if (!hasCurrentPoint)
                        {
                            return true;
                        }

                        var endX = Absolute(n[5], relative, x);
                        var endY = Absolute(n[6], relative, y);
                        commands.Add(Command(SymPathOp.ArcTo, n[0], n[1], n[2], n[3], n[4], endX, endY));
                        x = endX;
                        y = endY;
                        hasRenderableCommand = true;
                        hasCubic = hasQuad = false;
                        break;
                    }

                case 'Z':
                    if (!hasCurrentPoint)
                    {
                        continue;
                    }

                    commands.Add(Command(SymPathOp.Close));
                    x = moveX;
                    y = moveY;
                    hasRenderableCommand = true;
                    hasCubic = hasQuad = false;
                    break;

                default:
                    // A bearing turns what follows it, which would take the sine of a bound angle.
                    return false;
            }
        }

        if (!hasCurrentPoint || hasRenderableCommand)
        {
            geometry = new SymPath(fillType, commands);
        }

        return true;
    }

    private static SymPathCommand Command(SymPathOp op, params SymNode[] arguments) => new(op, arguments);

    private static SymNode Absolute(SymNode coordinate, bool relative, SymNode current)
        => relative ? Sum(coordinate, current) : coordinate;

    private static SymNode Reflect(SymNode point, SymNode mirror)
        => point is SymLit p && mirror is SymLit m
            ? SymNode.Literal((float)m.Value + ((float)m.Value - (float)p.Value))
            : SymNode.Add(mirror, SymNode.Subtract(mirror, point));

    private static SymNode Sum(SymNode left, SymNode right)
        => left is SymLit a && right is SymLit b
            ? SymNode.Literal((float)a.Value + (float)b.Value)
            : SymNode.Add(left, right);

    // In float, as the path builder parses them, so a literal reads the number it always did.
    private static bool TryReadNumbers(IReadOnlyList<SvgTransformExpressionArgument> arguments, out SymNode[] read)
    {
        read = new SymNode[arguments.Count];

        for (var index = 0; index < read.Length; index++)
        {
            if (arguments[index].Expression is { } expression)
            {
                read[index] = SymNode.Source(expression);
                continue;
            }

            if (!float.TryParse(arguments[index].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var literal))
            {
                return false;
            }

            read[index] = SymNode.Literal(literal);
        }

        return true;
    }
}
