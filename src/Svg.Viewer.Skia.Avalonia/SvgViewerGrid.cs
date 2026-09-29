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

    /// <summary>A step that suits a page this size, for a drawing nobody has chosen one for.</summary>
    /// <remarks>
    /// A step is a length rather than a proportion, so no one number serves every drawing: ten is a
    /// tenth of a hundred-unit page and the whole of a ten-unit one. A drawing imported from
    /// PaintCode is a few tens of units across, where ten leaves three lines in the whole page and
    /// every gesture jumps between them.
    ///
    /// About a tenth of the shorter side, taken down to 1, 2 or 5 times a power of ten so that the
    /// numbers a place is written with are ones somebody would have typed. A hundred-unit page comes
    /// out at ten, which is where <see cref="DefaultStep"/> came from.
    /// </remarks>
    public static float For(float width, float height)
    {
        var side = MathF.Min(MathF.Abs(width), MathF.Abs(height));

        if (side <= 0f || float.IsNaN(side) || float.IsInfinity(side))
        {
            return (float)DefaultStep;
        }

        var wanted = side / 10f;
        var magnitude = MathF.Pow(10f, MathF.Floor(MathF.Log10(wanted)));
        var scaled = wanted / magnitude;

        return Math.Clamp(
            (scaled >= 5f ? 5f : scaled >= 2f ? 2f : 1f) * magnitude,
            (float)MinimumStep,
            (float)MaximumStep);
    }

    /// <summary>How near a line is near enough, on screen.</summary>
    /// <remarks>
    /// About the slack in a hand holding a mouse still, at any zoom.
    /// </remarks>
    public const double PullPixels = 8d;

    /// <summary>Where the lines are reckoned from, for a space that is not the board's.</summary>
    public float OriginX { get; init; }

    public float OriginY { get; init; }

    /// <summary>
    /// How near a line a gesture has to come before it lands on it, in the grid's own units.
    /// </summary>
    /// <remarks>
    /// Zero rounds everything to the nearest line, which is what the lines themselves are drawn from
    /// and what a caller with no zoom to reckon with gets.
    /// </remarks>
    public float Pull { get; init; }

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

    /// <summary>The nearest line, wherever the value is.</summary>
    /// <remarks>Where the lines are drawn from, and the rounding a gesture took before it pulled.</remarks>
    public float SnapX(float value) => Snap(value, OriginX);

    /// <inheritdoc cref="SnapX"/>
    public float SnapY(float value) => Snap(value, OriginY);

    /// <summary>
    /// The same grid, magnetic at this zoom.
    /// </summary>
    /// <remarks>
    /// Never more than half a step: past that every point is within reach of a line and the pull is
    /// the old rounding again — which is what a grid fine enough on screen to see wants anyway.
    /// </remarks>
    public SvgViewerGrid Pulling(double scale)
        => scale > 0d && Step > 0f
            ? this with { Pull = (float)Math.Min(PullPixels / scale, Step / 2d) }
            : this;

    /// <summary>
    /// The nearest line where the gesture has come near one, and where the pointer put it otherwise.
    /// </summary>
    /// <remarks>
    /// Rounding everything is what stops a drag following the pointer: one step is tens of pixels at
    /// a high zoom, so the shape stands still for half the gesture and then leaps a whole step, which
    /// reads as a drag that does nothing. Near a line it lands on the line; between two, it is where
    /// it was put.
    /// </remarks>
    public float PullX(float value) => Pulled(Snap(value, OriginX), value);

    /// <inheritdoc cref="PullX"/>
    public float PullY(float value) => Pulled(Snap(value, OriginY), value);

    private float Pulled(float landed, float value)
        => Pull > 0f && MathF.Abs(landed - value) > Pull ? value : landed;

    /// <summary>The nearest angle, in degrees.</summary>
    public float Turns(float degrees)
        => Turn > 0f ? MathF.Round(degrees / Turn) * Turn : degrees;

    private float Snap(float value, float origin)
        => Step > 0f ? (MathF.Round((value - origin) / Step) * Step) + origin : value;
}
