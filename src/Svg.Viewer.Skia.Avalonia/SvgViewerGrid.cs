// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// The lines a gesture lands on, and the step a turn lands on.
/// </summary>
/// <remarks>
/// <para>
/// Measured in the space the drawings are arranged in, which is the space the canvas draws the grid
/// in — a gesture has to land on the line somebody can see, and every other space on the way there
/// (control pixels, a drawing's own units, an element's geometry) is at a different zoom or a
/// different origin.
/// </para>
/// <para>
/// A turn has its own step because a grid has no angle. Fifteen degrees is the usual answer and it
/// is what this starts at, but it is settable for the same reason the step is: what is a round
/// number depends on what is being drawn.
/// </para>
/// <para>
/// <c>SelectionService.Snap</c> does the same rounding in three lines and is on the reference path
/// already. It is not used because it rounds about zero with no way to say otherwise, and a drawing
/// on a board is not at zero — see <see cref="From"/>.
/// </para>
/// </remarks>
public readonly record struct SvgViewerGrid(float Step, float Turn)
{
    /// <summary>How far apart the lines are unless somebody says otherwise.</summary>
    public const double DefaultStep = 10d;

    /// <summary>
    /// The least a step may be.
    /// </summary>
    /// <remarks>
    /// Not zero, which is how this says it is off, and not a hairline: a step finer than the two
    /// decimals a place is written to is a grid nothing could ever land off.
    /// </remarks>
    public const double MinimumStep = 0.1d;

    public const double MaximumStep = 1000d;

    /// <summary>How far apart the angles are unless somebody says otherwise.</summary>
    public const double DefaultTurn = 15d;

    public const double MinimumTurn = 1d;

    /// <summary>A quarter turn, past which the stalk could only ever reach four angles.</summary>
    public const double MaximumTurn = 90d;

    /// <summary>Nothing to land on: every gesture writes where the pointer stopped.</summary>
    public static readonly SvgViewerGrid None = new(0f, 0f);

    /// <summary>Where the lines are reckoned from, for a space that is not the board's.</summary>
    public float OriginX { get; init; }

    public float OriginY { get; init; }

    /// <summary>Whether anything is being snapped at all.</summary>
    public bool IsOn => Step > 0f;

    /// <summary>
    /// The same grid, for something working in the space of a drawing placed at
    /// (<paramref name="x"/>, <paramref name="y"/>).
    /// </summary>
    /// <remarks>
    /// The lines stay where they are on the board; what moves is the zero they are counted from. A
    /// gizmo composes in the element's own space and would otherwise land the shape on a line of its
    /// drawing's making, which is the board's lines shifted by wherever the tile happens to sit.
    /// </remarks>
    public SvgViewerGrid From(float x, float y)
        => this with { OriginX = OriginX - x, OriginY = OriginY - y };

    public float SnapX(float value) => Snap(value, OriginX);

    public float SnapY(float value) => Snap(value, OriginY);

    /// <summary>The nearest angle, in degrees.</summary>
    public float Turns(float degrees)
        => Turn > 0f ? MathF.Round(degrees / Turn) * Turn : degrees;

    private float Snap(float value, float origin)
        => Step > 0f ? (MathF.Round((value - origin) / Step) * Step) + origin : value;
}
