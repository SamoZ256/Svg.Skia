// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using Svg.Editor.Skia;
using SK = SkiaSharp;
using Shim = ShimSkiaSharp;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// Dragging a drawing's own edges to change the size it is.
/// </summary>
/// <remarks>
/// <para>
/// The gesture, not the pane: the host owns the selection and the writing, and this owns the
/// arithmetic between a handle and a page. It draws nothing and writes nothing — <see cref="End"/>
/// hands back where the edges went, for the host to commit through whatever holds the drawing.
/// </para>
/// <para>
/// A page cannot be turned. Its edges are what its <c>width</c> and <c>height</c> say, and there is
/// nowhere in a document for a turned one to be written, so this answers for the eight handles and
/// never the stalk.
/// </para>
/// <para>
/// Dragging a page moves the drawing's edges and leaves what is drawn inside them alone, which is
/// the other thing from a resize: the room appears on the side that was dragged rather than the
/// picture growing into it. What it hands back is where each edge ended up as a fraction of where
/// it started, which means the same thing whatever size the drawing is being shown at.
/// </para>
/// </remarks>
public sealed class SvgViewerPage
{
    /// <summary>The least a page may be dragged down to, in the drawing's own units.</summary>
    /// <remarks>
    /// Not zero, which the size model refuses outright, and not a hairline either: a page dragged to
    /// nothing has no handles left to drag it back out by.
    /// </remarks>
    private const float Minimum = 1f;

    private readonly SelectionService _selection = new();

    /// <summary>Where the page is, in the space the drawings are arranged in.</summary>
    private SK.SKRect? _page;

    private int _handle = -1;
    private SK.SKRect _from;
    private SK.SKRect _to;

    /// <summary>Where inside the handle the press landed, which is not part of what it meant.</summary>
    private SK.SKPoint _slack;

    /// <inheritdoc cref="SvgViewerGizmo.Grid"/>
    /// <remarks>
    /// In the space this is tracked in, which is the board's — a page is where the canvas draws it,
    /// so the lines it lands on are the ones drawn under it.
    /// </remarks>
    public SvgViewerGrid Grid { get; set; } = SvgViewerGrid.None;

    /// <summary>Whether a drag is in flight.</summary>
    public bool IsDragging => _handle >= 0;

    /// <summary>
    /// The page as the drag has it, which is where the box is drawn while one is in flight.
    /// </summary>
    /// <remarks>
    /// The box moves and the drawing does not. Rebuilding a document at a new size costs a re-parse,
    /// which is the thing a board already refuses to do per frame — so what follows the pointer is
    /// the eight handles, and the picture catches up once on the drop.
    /// </remarks>
    public SK.SKRect? Shown => IsDragging ? _to : _page;

    /// <summary>Follows the selection: the page that is selected, or null for none.</summary>
    public void Track(SK.SKRect? page)
    {
        Cancel();

        _page = page is { Width: > 0f, Height: > 0f } ? page : null;
    }

    /// <summary>The box and its handles, or null when no page is selected.</summary>
    public BoundsInfo? Box(float scale)
        => Shown is { } page && scale > 0f
            ? _selection.GetBoundsInfo(
                new Shim.SKRect(page.Left, page.Top, page.Right, page.Bottom),
                Shim.SKMatrix.CreateIdentity(),
                () => scale)
            : null;

    /// <summary>Whether a press at <paramref name="at"/> is one of the handles.</summary>
    /// <remarks>
    /// The handles alone and never the inside: what is inside a page is the drawing, and a press
    /// there belongs to the shapes under it.
    /// </remarks>
    public bool Hits(SK.SKPoint at, float scale)
        => Box(scale) is { } box && _selection.HitHandle(box, at, scale, out _) >= 0;

    /// <summary>Takes hold of a handle, or answers false where the press was not on one.</summary>
    public bool Begin(SK.SKPoint at, float scale)
    {
        Cancel();

        if (_page is not { } page || Box(scale) is not { } box)
        {
            return false;
        }

        _handle = _selection.HitHandle(box, at, scale, out _);

        if (_handle < 0)
        {
            return false;
        }

        _from = page;
        _to = page;

        _slack = new SK.SKPoint(
            at.X - (_handle switch { 0 or 6 or 7 => page.Left, 2 or 3 or 4 => page.Right, _ => page.MidX }),
            at.Y - (_handle switch { 0 or 1 or 2 => page.Top, 4 or 5 or 6 => page.Bottom, _ => page.MidY }));

        return true;
    }

    /// <summary>Drags the handle to <paramref name="at"/>, and answers where the page now is.</summary>
    /// <remarks>
    /// The corner opposite the one being dragged stays put, which is what a handle means everywhere
    /// else. A side handle moves one edge and a corner moves two — free of each other, because the
    /// two numbers a page is are free of each other.
    /// </remarks>
    public SK.SKRect? Drag(SK.SKPoint at)
    {
        if (!IsDragging)
        {
            return null;
        }

        var left = _from.Left;
        var top = _from.Top;
        var right = _from.Right;
        var bottom = _from.Bottom;

        // Where the edge has reached rather than where the hand is: the press is anywhere within
        // half a handle of the edge it took hold of, and a line the hand lands on is not the line
        // the edge lands on. Only under a grid, since without one the two come to the same page.
        var to = Grid.IsOn
            ? new SK.SKPoint(Grid.SnapX(at.X - _slack.X), Grid.SnapY(at.Y - _slack.Y))
            : at;

        // The order SelectionService hands them out: TL, top, TR, right, BR, bottom, BL, left.
        switch (_handle)
        {
            case 0: left = to.X; top = to.Y; break;
            case 1: top = to.Y; break;
            case 2: right = to.X; top = to.Y; break;
            case 3: right = to.X; break;
            case 4: right = to.X; bottom = to.Y; break;
            case 5: bottom = to.Y; break;
            case 6: left = to.X; bottom = to.Y; break;
            case 7: left = to.X; break;
        }

        // Clamped rather than allowed to cross: a page turned inside out is a negative width, which
        // the size model refuses, and a page of nothing has no handle left to drag it back by.
        if (right - left < Minimum)
        {
            if (left != _from.Left)
            {
                left = right - Minimum;
            }
            else
            {
                right = left + Minimum;
            }
        }

        if (bottom - top < Minimum)
        {
            if (top != _from.Top)
            {
                top = bottom - Minimum;
            }
            else
            {
                bottom = top + Minimum;
            }
        }

        _to = new SK.SKRect(left, top, right, bottom);

        return _to;
    }

    /// <summary>
    /// Ends the drag and says where each edge of the page ended up.
    /// </summary>
    /// <remarks>
    /// As fractions of the page as the drag found it: 0 and 1 are where each edge was, so dragging
    /// the right edge half as wide again is a right of 1.5 and dragging the left edge out by a
    /// quarter is a left of -0.25. Which edges moved is the whole of it — the page grows on the side
    /// that was dragged and the opposite one stays where it is.
    ///
    /// Fractions and not the numbers under the pointer, because the page on screen is the drawing
    /// built at whatever size its host asked for: a project scaling a 24-unit icon draws it at 48,
    /// and 72 written into a drawing that is 24 would be squared by the next build. A fraction means
    /// the same thing whatever the drawing is being shown at.
    ///
    /// A fraction has no grid, which is why the snap is upstream of this: what lands on a line is
    /// the edge while it is being dragged, and this is only how far that got.
    ///
    /// Null for a drag that ended where it began, so a press that wandered inside the slack and came
    /// back is not an edit and costs no step in the history.
    /// </remarks>
    public (float Left, float Top, float Right, float Bottom)? End()
    {
        if (!IsDragging)
        {
            return null;
        }

        var to = _to;
        var from = _from;

        Cancel();

        if (to == from || from.Width <= 0f || from.Height <= 0f)
        {
            return null;
        }

        return (
            (to.Left - from.Left) / from.Width,
            (to.Top - from.Top) / from.Height,
            (to.Right - from.Left) / from.Width,
            (to.Bottom - from.Top) / from.Height);
    }

    /// <summary>Lets go without writing anything.</summary>
    public void Cancel()
    {
        _handle = -1;
        _from = default;
        _to = default;
        _slack = default;
    }
}
