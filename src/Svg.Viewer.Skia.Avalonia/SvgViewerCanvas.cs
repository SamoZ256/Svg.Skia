// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
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

    private SKSvg? _svg;
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

    // Written on the UI thread, read on the render thread. Everything the draw needs, in one
    // reference assignment, so a frame can never see half of a change.
    private volatile Snapshot _snapshot = new(null, 1d, 0d, 0d);

    private sealed record Snapshot(SKSvg? Svg, double Scale, double OffsetX, double OffsetY);

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

    /// <summary>The drawing on show. Assigning a different one starts it fitted.</summary>
    public SKSvg? Svg
    {
        get => _svg;
        set
        {
            _hasFitted = false;
            _userAdjusted = false;

            Replace(value);
        }
    }

    /// <summary>Swaps in a rebuild of the drawing already on show, keeping an adjusted view.</summary>
    /// <remarks>
    /// Assigning <see cref="Svg"/> re-fits, which is right for a file being opened and wrong for the
    /// open one being edited: re-fitting would throw away where the reader was looking, on every
    /// keystroke. A view nobody has adjusted still re-fits, since the size may be what was edited.
    /// </remarks>
    public void Replace(SKSvg? svg)
    {
        if (ReferenceEquals(_svg, svg))
        {
            return;
        }

        _svg = svg;

        if (_userAdjusted)
        {
            Publish();

            return;
        }

        // When the control has no size yet the fit waits for one, and asking for a layout pass is
        // what makes that arrive — a repaint alone would leave the drawing unscaled in the corner.
        if (!TryFit())
        {
            Publish();
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

    private bool TryGetCullRect(out SKRect bounds)
    {
        bounds = default;

        var picture = _svg?.Picture;
        if (picture is null || picture.CullRect.Width <= 0f || picture.CullRect.Height <= 0f)
        {
            return false;
        }

        bounds = picture.CullRect;

        return true;
    }

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
        _snapshot = new Snapshot(_svg, _scale, _offsetX, _offsetY);
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
        if (!IsZoomEnabled || _svg is null)
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
        if (!IsPanEnabled || _svg is null || !(properties.IsLeftButtonPressed || properties.IsMiddleButtonPressed))
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

        if (state.Svg is not { } svg)
        {
            return;
        }

        canvas.Save();
        canvas.Translate((float)state.OffsetX, (float)state.OffsetY);
        canvas.Scale((float)state.Scale);

        // SKSvg.Draw brackets itself with BeginDraw/EndDraw, so the picture cannot be disposed
        // underneath it by a value being bound on the UI thread.
        svg.Draw(canvas);

        canvas.Restore();
    }
}
