// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;

namespace ShimSkiaSharp;

// One command of a path whose numbers may be expressions, spelled as absolute coordinates: the
// reader has already resolved relative ones, H and V, and the control points S and T reflect.
public enum SymPathOp
{
    // (x, y)
    MoveTo,

    // (x, y)
    LineTo,

    // (x1, y1, x, y)
    QuadTo,

    // (x1, y1, x2, y2, x, y)
    CubicTo,

    // ()
    Close,

    // (rx, ry, rotate, largeArc, sweep, x, y) — a flag is set wherever it is not nought.
    ArcTo,

    // (x, y, width, height, rx, ry, rx across, ry across) — one op for a rect and a round one, since
    // which of the two it draws is decided by the values. Across is each radius on the other axis,
    // which is where SvgRectangle's getters take it when the other one is nought; a percentage is
    // the one length that differs there.
    Rect,

    // (cx, cy, rx, ry)
    Ellipse,

    // (cx, cy, r) — apart from Ellipse because AddCircle and AddOval of the same box differ by an ulp.
    Circle
}

public sealed record SymPathCommand(SymPathOp Op, IReadOnlyList<SymNode> Arguments) : IDeepCloneable<SymPathCommand>
{
    // Immutable all the way down, so sharing the instance is a correct deep clone.
    public SymPathCommand DeepClone() => this;
}

/// <summary>A clip shape whose numbers may be expressions, kept as the commands that draw it.</summary>
/// <remarks>
/// Not a list of <see cref="PathCommand"/> with expressions beside it: what a driven shape draws
/// depends on its values — an arc of no radius is a line, a rect of no width is nothing — so the
/// commands a path records are known only once they are.
/// </remarks>
public sealed record SymPath(SKPathFillType FillType, IReadOnlyList<SymPathCommand> Commands) : IDeepCloneable<SymPath>
{
    public SymPath DeepClone() => this;

    /// <summary>Whether any number is something other than one already known.</summary>
    public bool IsSymbolic
    {
        get
        {
            foreach (var command in Commands)
            {
                foreach (var argument in command.Arguments)
                {
                    if (argument is not SymLit)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    /// <summary>How many numbers <see cref="Apply"/> reads, which is every command's in turn.</summary>
    public int ArgumentCount
    {
        get
        {
            var count = 0;

            foreach (var command in Commands)
            {
                count += Arity(command.Op);
            }

            return count;
        }
    }

    public static int Arity(SymPathOp op) => op switch
    {
        SymPathOp.MoveTo or SymPathOp.LineTo => 2,
        SymPathOp.QuadTo or SymPathOp.Ellipse => 4,
        SymPathOp.CubicTo => 6,
        SymPathOp.ArcTo => 7,
        SymPathOp.Rect => 8,
        SymPathOp.Circle => 3,
        _ => 0
    };

    /// <summary>The path drawn with nothing bound: every expression reads nought.</summary>
    /// <remarks>
    /// A method rather than a property because it builds a new path each time, which a reflection
    /// walk over the model would otherwise do on every comparison. The arguments are folded rather
    /// than read, since a relative coordinate after a driven one is the sum of the two.
    /// </remarks>
    public SKPath ToPlaceholder()
    {
        var arguments = new List<float>(ArgumentCount);

        foreach (var command in Commands)
        {
            foreach (var argument in command.Arguments)
            {
                arguments.Add(Unbound(argument));
            }
        }

        return Apply(arguments);
    }

    /// <summary>The path these commands draw with <paramref name="arguments"/>, read in command order.</summary>
    /// <remarks>
    /// The one place a driven clip's numbers become a path; the generated <c>SvgDrivenPathData</c>
    /// repeats it statement for statement. Each rule is the one <c>PathingService</c> or
    /// <c>SvgGeometryService</c> applies to the same numbers written as literals, so a bound clip
    /// records what the literal document does — but it never answers null: a clip part that is null
    /// makes the renderer drop the whole clip, and a shape that comes to nothing has to clip
    /// everything instead. A number that is not finite is that nothing as well.
    /// </remarks>
    public SKPath Apply(IReadOnlyList<float> arguments)
    {
        if (arguments is null)
        {
            throw new ArgumentNullException(nameof(arguments));
        }

        var path = new SKPath { FillType = FillType };

        for (var i = 0; i < arguments.Count; i++)
        {
            if (float.IsNaN(arguments[i]) || float.IsInfinity(arguments[i]))
            {
                return path;
            }
        }

        var index = 0;
        float x = 0f, y = 0f, moveX = 0f, moveY = 0f;

        foreach (var command in Commands)
        {
            var a = index;
            index += Arity(command.Op);

            switch (command.Op)
            {
                case SymPathOp.MoveTo:
                    path.MoveTo(arguments[a], arguments[a + 1]);
                    x = moveX = arguments[a];
                    y = moveY = arguments[a + 1];
                    break;

                case SymPathOp.LineTo:
                    path.LineTo(arguments[a], arguments[a + 1]);
                    x = arguments[a];
                    y = arguments[a + 1];
                    break;

                case SymPathOp.QuadTo:
                    path.QuadTo(arguments[a], arguments[a + 1], arguments[a + 2], arguments[a + 3]);
                    x = arguments[a + 2];
                    y = arguments[a + 3];
                    break;

                case SymPathOp.CubicTo:
                    path.CubicTo(arguments[a], arguments[a + 1], arguments[a + 2], arguments[a + 3], arguments[a + 4], arguments[a + 5]);
                    x = arguments[a + 4];
                    y = arguments[a + 5];
                    break;

                case SymPathOp.Close:
                    path.Close();
                    x = moveX;
                    y = moveY;
                    break;

                case SymPathOp.ArcTo:
                    {
                        // SvgArcSegment keeps its radii unsigned, and PathingService tests what it kept.
                        var rx = Math.Abs(arguments[a]);
                        var ry = Math.Abs(arguments[a + 1]);
                        var endX = arguments[a + 5];
                        var endY = arguments[a + 6];

                        if (rx <= float.Epsilon || ry <= float.Epsilon)
                        {
                            path.LineTo(endX, endY);
                        }
                        else if (!(Math.Abs(x - endX) <= float.Epsilon && Math.Abs(y - endY) <= float.Epsilon))
                        {
                            path.ArcTo(
                                rx,
                                ry,
                                arguments[a + 2],
                                arguments[a + 3] != 0f ? SKPathArcSize.Large : SKPathArcSize.Small,
                                arguments[a + 4] != 0f ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise,
                                endX,
                                endY);
                        }

                        x = endX;
                        y = endY;
                        break;
                    }

                case SymPathOp.Rect:
                    Rect(path, arguments[a], arguments[a + 1], arguments[a + 2], arguments[a + 3], arguments[a + 4], arguments[a + 5], arguments[a + 6], arguments[a + 7]);
                    break;

                case SymPathOp.Ellipse:
                    Ellipse(path, arguments[a], arguments[a + 1], arguments[a + 2], arguments[a + 3]);
                    break;

                case SymPathOp.Circle:
                    if (arguments[a + 2] > 0f)
                    {
                        path.AddCircle(arguments[a], arguments[a + 1], arguments[a + 2]);
                    }

                    break;
            }
        }

        return path;
    }

    // PathingService.ToPath(SvgRectangle), preceded by what SvgRectangle's radius getters do: a
    // radius of nought borrows the other one where that is positive, measured on its own axis.
    private static void Rect(SKPath path, float x, float y, float width, float height, float rx, float ry, float rxAcross, float ryAcross)
    {
        if (width <= 0f || height <= 0f)
        {
            return;
        }

        var borrowedX = rx == 0f && ry > 0f ? ryAcross : rx;
        var borrowedY = ry == 0f && rx > 0f ? rxAcross : ry;
        rx = borrowedX;
        ry = borrowedY;

        if (rx < 0f && ry < 0f)
        {
            rx = 0f;
            ry = 0f;
        }

        if (rx == 0f || ry == 0f)
        {
            rx = 0f;
            ry = 0f;
        }

        if (rx < 0f)
        {
            rx = Math.Abs(rx);
        }

        if (ry < 0f)
        {
            ry = Math.Abs(ry);
        }

        if (rx > 0f && rx > width / 2f)
        {
            rx = width / 2f;
        }

        if (ry > 0f && ry > height / 2f)
        {
            ry = height / 2f;
        }

        var rect = SKRect.Create(x, y, width, height);

        if (rx > 0f && ry > 0f)
        {
            path.AddRoundRect(rect, rx, ry);
        }
        else
        {
            path.AddRect(rect);
        }
    }

    // SvgGeometryService.CreateEllipsePath, after the reader has stood one radius in for an auto one.
    private static void Ellipse(SKPath path, float cx, float cy, float rx, float ry)
    {
        if (rx < 0f && ry < 0f)
        {
            return;
        }

        rx = Math.Abs(rx);
        ry = Math.Abs(ry);

        if (rx <= 0f || ry <= 0f)
        {
            return;
        }

        path.AddOval(SKRect.Create(cx - rx, cy - ry, rx + rx, ry + ry));
    }

    // In float, as the evaluator computes it, so the placeholder is the path a binding of nought draws.
    private static float Unbound(SymNode node) => node switch
    {
        SymLit literal => (float)literal.Value,
        SymUnary { Op: SymOp.Negate } unary => -Unbound(unary.Operand),
        SymBinary { Op: SymOp.Add } binary => Unbound(binary.Left) + Unbound(binary.Right),
        SymBinary { Op: SymOp.Subtract } binary => Unbound(binary.Left) - Unbound(binary.Right),
        SymBinary { Op: SymOp.Multiply } binary => Unbound(binary.Left) * Unbound(binary.Right),
        SymBinary { Op: SymOp.Divide } binary => Unbound(binary.Left) / Unbound(binary.Right),
        _ => 0f
    };
}
