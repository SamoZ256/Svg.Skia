// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.Skia;
using Avalonia.Input;
using Avalonia.Interactivity;
using SkiaSharp;
using Svg.Skia;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// The drawing surface: one document, an absolute scale and an offset, and the gestures that move
/// them.
/// </summary>
/// <remarks>
/// On <see cref="SKCanvasControl"/> rather than the <c>Avalonia.Svg.Skia.Svg</c> control, which
/// sizes itself to the drawing it fits — a 100x100 document in a 400x200 pane arranges at 200x200 —
/// so it can never fill a viewport. The scale is absolute, not a factor on a stretch, so fit and
/// one-to-one are both expressible and the readout is a true percentage.
/// </remarks>
public class SvgViewerCanvas : SKCanvasControl
{
    /// <summary>Below this a drawing is a speck; above it, one path fills the pane.</summary>
    public const double MinimumScale = 0.02d;

    public const double MaximumScale = 64d;

    private static readonly Cursor s_grabCursor = new(StandardCursorType.SizeAll);

    private IReadOnlyList<SvgViewerPlacement> _placed = Array.Empty<SvgViewerPlacement>();
    private double _scale = 1d;
    private double _offsetX;
    private double _offsetY;

    private bool _hasFitted;
    private bool _userAdjusted;
    private Size _fittedTo;
    private Point _dragOrigin;
    private double _dragOffsetX;
    private double _dragOffsetY;
    private bool _dragging;
    private Cursor? _restoreCursor;
    private bool _showBounds = true;

    // Written on the UI thread, read on the render thread. Everything the draw needs, in one
    // reference assignment, so a frame can never see half of a change.
    private volatile Snapshot _snapshot = new(Array.Empty<SvgViewerPlacement>(), 1d, 0d, 0d, true);

    private sealed record Snapshot(
        IReadOnlyList<SvgViewerPlacement> Placed,
        double Scale,
        double OffsetX,
        double OffsetY,
        bool Bounds);

    public SvgViewerCanvas()
    {
        ClipToBounds = true;
        Focusable = true;

        Draw += OnDraw;

        // Tunnelling, because the pointer and wheel events are forwarded to the document's own
        // interaction dispatcher by anything hosting an SVG, and chrome gets first refusal.
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);

        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
    }

    /// <summary>Raised whenever the scale or offset changes, for a zoom readout.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>What is painted behind the drawing.</summary>
    public SKColor Background { get; set; } = new(0x1A, 0x1A, 0x1E);

    public bool IsZoomEnabled { get; set; } = true;

    public bool IsPanEnabled { get; set; } = true;

    /// <summary>
    /// Whether the drawing's own edges are outlined.
    /// </summary>
    /// <remarks>
    /// An icon with transparent margins ends somewhere the eye cannot see, and where it ends is what
    /// an export writes and what a project's sizing moves. On by default for that reason; a host
    /// wanting the drawing on its own turns it off.
    /// </remarks>
    public bool ShowBounds
    {
        get => _showBounds;
        set
        {
            if (_showBounds == value)
            {
                return;
            }

            _showBounds = value;

            // Through the snapshot like everything else the frame is drawn from: the render thread
            // may read nothing else, so a flag it could see change mid-frame is not an option.
            Publish();
        }
    }

    /// <summary>The drawing on show. Assigning a different one starts it fitted.</summary>
    /// <remarks>The one-drawing case of <see cref="Show"/>, which is what most hosts want.</remarks>
    public SKSvg? Svg
    {
        get => _placed.Count == 1 ? _placed[0].Svg : null;
        set
        {
            _hasFitted = false;
            _userAdjusted = false;

            Replace(value);
        }
    }

    /// <summary>What is on show, in the order it is drawn.</summary>
    public IReadOnlyList<SvgViewerPlacement> Placements => _placed;

    /// <summary>
    /// Shows several drawings at once, arranged by the caller.
    /// </summary>
    /// <remarks>
    /// One surface rather than one per drawing, so a set is zoomed, panned and outlined as the one
    /// thing it is. The arrangement is expected to start at the origin, as a single drawing's own
    /// picture does — the view is fitted to the size of what is placed, not to where it was put.
    /// </remarks>
    public void Show(IReadOnlyList<SvgViewerPlacement> placed)
    {
        _hasFitted = false;
        _userAdjusted = false;

        Place(placed ?? Array.Empty<SvgViewerPlacement>());
    }

    /// <summary>Swaps in a rebuild of the drawing already on show, keeping an adjusted view.</summary>
    /// <remarks>
    /// Assigning <see cref="Svg"/> re-fits, which is right for a file being opened and wrong for the
    /// open one being edited: re-fitting would throw away where the reader was looking, on every
    /// keystroke. A view nobody has adjusted still re-fits, since the size may be what was edited.
    /// </remarks>
    public void Replace(SKSvg? svg)
    {
        if (ReferenceEquals(Svg, svg))
        {
            return;
        }

        Place(svg is { } ? new[] { new SvgViewerPlacement(svg, default) } : Array.Empty<SvgViewerPlacement>());
    }

    private void Place(IReadOnlyList<SvgViewerPlacement> placed)
    {
        _placed = placed;

        // Published because the drawing changed, whatever the view does about it. The fit below
        // publishes only when it moves the view, so a drawing swapped for one that fits exactly as
        // the last did — a padding change inside the same frame — left the render thread holding
        // the picture that had just been replaced, and the old one stayed up until something else
        // moved the view.
        Publish();

        if (_userAdjusted)
        {
            return;
        }

        // When the control has no size yet the fit waits for one, and asking for a layout pass is
        // what makes that arrive — a repaint alone would leave the drawing unscaled in the corner.
        if (!TryFit())
        {
            InvalidateArrange();
        }
    }

    /// <summary>The scale actually applied, where 1 is one drawing unit per device pixel.</summary>
    public double Scale => _scale;

    public double OffsetX => _offsetX;

    public double OffsetY => _offsetY;

    /// <summary>Scales the drawing to fit the pane, centred.</summary>
    public void Fit()
    {
        _userAdjusted = false;

        if (!TryFit())
        {
            SetView(1d, 0d, 0d);
        }
    }

    /// <summary>One drawing unit per pixel, centred.</summary>
    public void ActualSize()
    {
        _userAdjusted = true;
        ScaleCentred(1d);
    }

    /// <summary>Back to the fitted view. Parameter values are untouched.</summary>
    public void ResetView() => Fit();

    public void ZoomIn() => ScaleCentred(_scale * 1.2d);

    public void ZoomOut() => ScaleCentred(_scale / 1.2d);

    /// <summary>Scales about a point in control coordinates, leaving what is under it in place.</summary>
    public void ZoomTo(double scale, Point anchor)
    {
        _userAdjusted = true;

        var clamped = Math.Clamp(scale, MinimumScale, MaximumScale);
        var factor = clamped / _scale;

        SetView(
            clamped,
            anchor.X - (anchor.X - _offsetX) * factor,
            anchor.Y - (anchor.Y - _offsetY) * factor);
    }

    /// <summary>Converts a point in control coordinates to one in the drawing.</summary>
    public bool TryGetDrawingPoint(Point point, out SKPoint drawingPoint)
    {
        drawingPoint = default;

        if (_scale <= 0d || !TryGetCullRect(out var bounds))
        {
            return false;
        }

        drawingPoint = new SKPoint(
            (float)((point.X - _offsetX) / _scale + bounds.Left),
            (float)((point.Y - _offsetY) / _scale + bounds.Top));

        return true;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);

        // Against the size being arranged, not Bounds, which is assigned once this returns. A
        // resize keeps the drawing fitted only until the view has been adjusted by hand.
        if (!_hasFitted || (!_userAdjusted && arranged != _fittedTo))
        {
            TryFit(arranged);
        }

        return arranged;
    }

    private void ScaleCentred(double scale)
        => ZoomTo(scale, new Point(Bounds.Width / 2d, Bounds.Height / 2d));

    private bool TryFit() => TryFit(Bounds.Size);

    private bool TryFit(Size size)
    {
        if (size.Width <= 0d || size.Height <= 0d || !TryGetCullRect(out var bounds))
        {
            return false;
        }

        var scale = Math.Clamp(
            Math.Min(size.Width / bounds.Width, size.Height / bounds.Height),
            MinimumScale,
            MaximumScale);

        _hasFitted = true;
        _fittedTo = size;

        SetView(
            scale,
            (size.Width - bounds.Width * scale) / 2d,
            (size.Height - bounds.Height * scale) / 2d);

        return true;
    }

    /// <summary>What is on show, taken together: one drawing's own edges, or all of their union.</summary>
    private bool TryGetCullRect(out SKRect bounds)
    {
        bounds = default;

        var found = false;

        foreach (var placed in _placed)
        {
            if (Frame(placed) is not { } frame)
            {
                continue;
            }

            frame.Offset(placed.At);

            bounds = found ? SKRect.Union(bounds, frame) : frame;
            found = true;
        }

        return found && bounds.Width > 0f && bounds.Height > 0f;
    }

    /// <summary>One placed drawing's own edges, in its own space, or null where it has none.</summary>
    private static SKRect? Frame(SvgViewerPlacement placed)
        => placed.Svg.Picture is { CullRect: { Width: > 0f, Height: > 0f } cull } ? cull : null;

    private void SetView(double scale, double offsetX, double offsetY)
    {
        var clamped = Math.Clamp(scale, MinimumScale, MaximumScale);

        if (_scale.Equals(clamped) && _offsetX.Equals(offsetX) && _offsetY.Equals(offsetY))
        {
            return;
        }

        _scale = clamped;
        _offsetX = offsetX;
        _offsetY = offsetY;

        Publish();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Hands the render thread a new frame's worth of state.</summary>
    internal void Publish()
    {
        _snapshot = new Snapshot(_placed, _scale, _offsetX, _offsetY, _showBounds);
        InvalidateVisual();
    }

    // ---- gestures ---------------------------------------------------------------------------

    /// <remarks>
    /// The trackpad path too: a two finger scroll arrives as a wheel event with a fractional delta.
    /// A pinch is a separate platform gesture, but Avalonia 12.0.0 keeps <c>Gestures</c> internal,
    /// so there is no public event for it.
    /// </remarks>
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!IsZoomEnabled || _placed.Count == 0)
        {
            return;
        }

        ZoomTo(_scale * Math.Pow(1.2d, e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Command on macOS, Control elsewhere.
        var accelerator = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (!accelerator)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                ZoomIn();
                break;

            case Key.OemMinus or Key.Subtract:
                ZoomOut();
                break;

            case Key.D0 or Key.NumPad0:
                Fit();
                break;

            case Key.D1 or Key.NumPad1:
                ActualSize();
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (!IsPanEnabled || _placed.Count == 0 || !(properties.IsLeftButtonPressed || properties.IsMiddleButtonPressed))
        {
            return;
        }

        Focus();

        _dragging = true;
        _dragOrigin = e.GetPosition(this);
        _dragOffsetX = _offsetX;
        _dragOffsetY = _offsetY;
        _restoreCursor = Cursor;
        Cursor = s_grabCursor;

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var position = e.GetPosition(this);
        _userAdjusted = true;

        // The offset is applied after the scale, so a drag is one for one in control pixels.
        SetView(
            _scale,
            _dragOffsetX + (position.X - _dragOrigin.X),
            _dragOffsetY + (position.Y - _dragOrigin.Y));

        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Cursor = _restoreCursor;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        _dragging = false;
        Cursor = _restoreCursor;
    }

    // ---- drawing ----------------------------------------------------------------------------

    private void OnDraw(object? sender, SKCanvasEventArgs e)
    {
        // Render thread. Only the snapshot and the canvas may be touched here.
        var state = _snapshot;
        var canvas = e.Canvas;

        canvas.Clear(Background);

        if (state.Placed.Count == 0)
        {
            return;
        }

        canvas.Save();
        canvas.Translate((float)state.OffsetX, (float)state.OffsetY);
        canvas.Scale((float)state.Scale);

        // One font for the frame rather than one per label: the sizes differ, and setting the size
        // on a font costs nothing next to building one.
        using var font = new SKFont(SKTypeface.Default, 1f);
        using var writing = new SKPaint { IsAntialias = true, Color = SKColors.Gray };

        foreach (var placed in state.Placed)
        {
            canvas.Save();
            canvas.Translate(placed.At.X, placed.At.Y);

            // SKSvg.Draw brackets itself with BeginDraw/EndDraw, so the picture cannot be disposed
            // underneath it by a value being bound on the UI thread.
            placed.Svg.Draw(canvas);

            if (Frame(placed) is { } frame)
            {
                if (state.Bounds)
                {
                    Outline(canvas, frame, state.Scale);
                }

                if (placed is { Label: { Length: > 0 } label, LabelSize: > 0f })
                {
                    font.Size = placed.LabelSize;

                    canvas.DrawText(
                        label,
                        frame.MidX,
                        frame.Bottom + placed.LabelSize * 1.2f,
                        SKTextAlign.Center,
                        font,
                        writing);
                }
            }

            canvas.Restore();
        }

        canvas.Restore();
    }

    /// <summary>
    /// Draws the drawing's own edges, inside the space the drawing was just drawn in.
    /// </summary>
    /// <remarks>
    /// Dashed, and not because it is prettier: a solid rectangle hugging an icon reads as part of
    /// the icon, and the one thing this must never be mistaken for is something the file draws.
    /// Grey rather than a theme brush, since it has to read on both the dark ground this paints by
    /// default and on whatever a host sets <see cref="Background"/> to.
    ///
    /// Every length is divided by the scale because the canvas is scaled around it, which is what
    /// keeps the line one pixel wide and the dashes one length at every zoom.
    /// </remarks>
    private static void Outline(SKCanvas canvas, SKRect frame, double scale)
    {
        var hairline = (float)(1d / scale);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            Color = SKColors.Gray,
            StrokeWidth = hairline,
            PathEffect = SKPathEffect.CreateDash(new[] { 4f * hairline, 4f * hairline }, 0f)
        };

        // A stroke straddles what it is drawn on, so half of it would fall outside the drawing.
        // Half a pixel in puts the whole line within the edges it is about.
        canvas.DrawRect(SKRect.Inflate(frame, -hairline / 2f, -hairline / 2f), paint);
    }
}
