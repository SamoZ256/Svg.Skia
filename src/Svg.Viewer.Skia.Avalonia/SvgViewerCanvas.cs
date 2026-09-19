// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.Skia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SkiaSharp;
using Svg.Editor.Skia;
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
    private IReadOnlyList<SvgViewerFrame> _frames = Array.Empty<SvgViewerFrame>();
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

    /// <summary>What a press took hold of, whatever the host said that was, or null.</summary>
    private object? _moving;
    private SKRect _movingBounds;
    private SKPoint _movingFrom;
    private SKPoint _movingBy;
    private bool _moved;
    private IPointer? _movingPointer;

    private Cursor? _restoreCursor;
    private bool _showBounds = true;
    private SKPath? _highlight;
    private BoundsInfo? _gizmo;
    private bool _editing;
    private bool _editMoved;
    private IPointer? _editPointer;

    /// <summary>How long the ring has been up, which is what the pulse is a function of.</summary>
    private readonly Stopwatch _highlightAge = new();

    /// <summary>
    /// Repaints while the ring is settling in.
    /// </summary>
    /// <remarks>
    /// A pulse that ran for ever would repaint the whole drawing thirty times a second for as long
    /// as anything was selected, which is a real cost to pay for decoration. It runs for
    /// <see cref="PulseSeconds"/> and stops itself: long enough for the eye to be pulled to the
    /// ring, after which the ring is still there and no longer costs anything.
    /// </remarks>
    private readonly DispatcherTimer _pulse = new() { Interval = TimeSpan.FromMilliseconds(33d) };
    private Point _pressOrigin;
    private bool _pressed;

    // Written on the UI thread, read on the render thread. Everything the draw needs, in one
    // reference assignment, so a frame can never see half of a change.
    private volatile Snapshot _snapshot = new(
        Array.Empty<SvgViewerPlacement>(), Array.Empty<SvgViewerFrame>(), 1d, 0d, 0d, true, null, 0d, null, null);

    private sealed record Snapshot(
        IReadOnlyList<SvgViewerPlacement> Placed,
        IReadOnlyList<SvgViewerFrame> Frames,
        double Scale,
        double OffsetX,
        double OffsetY,
        bool Bounds,
        SKPath? Highlight,
        double HighlightAge,
        (SKRect Bounds, SKPoint By, IReadOnlySet<SvgViewerPlacement> Carried)? Moving,
        BoundsInfo? Gizmo);

    public SvgViewerCanvas()
    {
        ClipToBounds = true;
        Focusable = true;

        Draw += OnDraw;

        _pulse.Tick += (_, _) =>
        {
            if (_highlight is null || _highlightAge.Elapsed.TotalSeconds > PulseSeconds)
            {
                _pulse.IsEnabled = false;
            }

            Publish();
        };

        // Tunnelling, because the pointer and wheel events are forwarded to the document's own
        // interaction dispatcher by anything hosting an SVG, and chrome gets first refusal.
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);

        // The trackpad's own zoom, which the platform reports as a gesture of its own rather than as
        // a wheel with a modifier held. Bubbling, unlike its neighbours here: that is the only way
        // this one is raised, and a tunnelling handler on it is never called at all.
        AddHandler(PointerTouchPadGestureMagnifyEvent, OnMagnify, RoutingStrategies.Bubble);

        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
    }

    /// <summary>Raised whenever the scale or offset changes, for a zoom readout.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>Raised where a click landed, in control coordinates.</summary>
    /// <remarks>
    /// Only for a press and release the pointer did not travel between: a drag is a pan, and what
    /// it panned over is not what the reader meant to point at.
    /// </remarks>
    public event EventHandler<Point>? Picked;

    /// <summary>
    /// Whether a press belongs to whatever is being edited rather than to a pan.
    /// </summary>
    /// <remarks>
    /// The canvas has to settle pan against edit on the press itself, before anything has moved, and
    /// only the host knows what is selected and where its handles are. Null, and every drag pans as
    /// it always did.
    /// </remarks>
    public Func<Point, bool>? IsEditTarget { get; set; }

    /// <summary>The edit gesture, in control coordinates, as <see cref="Picked"/> reports a click.</summary>
    public event EventHandler<Point>? EditBegun;

    /// <inheritdoc cref="EditBegun"/>
    public event EventHandler<Point>? EditMoved;

    /// <inheritdoc cref="EditBegun"/>
    public event EventHandler<Point>? EditEnded;

    /// <summary>Raised where the pointer was taken away mid-gesture, so nothing was let go of.</summary>
    public event EventHandler? EditCancelled;

    /// <summary>
    /// The box and handles drawn over what is being edited, or null while nothing is.
    /// </summary>
    /// <remarks>
    /// In the space the drawings are arranged in, like <see cref="Highlight"/>, and for the same
    /// reason: a host showing several offsets it by the placement it belongs to.
    /// </remarks>
    public BoundsInfo? Gizmo
    {
        get => _gizmo;
        set
        {
            _gizmo = value;

            Publish();
        }
    }

    /// <summary>What is painted behind the drawing.</summary>
    /// <summary>
    /// What a press at a point takes hold of, and the rectangle it covers. Null where nothing there moves.
    /// </summary>
    /// <remarks>
    /// Asked of the host, in the space the drawings are arranged in, because what counts as one
    /// thing is the arrangement's own business: several drawings and the frame round them can be
    /// one item to whoever laid them out. The item is handed back on release and means nothing here.
    ///
    /// Unset, a press pans as it always did, which is what every host but a board wants.
    /// </remarks>
    public Func<SKPoint, (object Item, SKRect Bounds)?>? Grip { get; set; }

    /// <summary>Raised when something taken hold of is let go, with how far it was carried.</summary>
    /// <remarks>
    /// Once, on release, and never for a press that did not travel — that is a pick, and
    /// <see cref="Picked"/> has it. Nothing has been committed: this is the request, and a host that
    /// does nothing about it has refused, leaving what it drew where its own arrangement says.
    /// </remarks>
    public event EventHandler<SvgViewerMove>? Moved;

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
    /// <summary>
    /// The silhouette to ring, in the space the drawings are arranged in.
    /// </summary>
    /// <remarks>
    /// One path holding every piece, so an element drawn several times through <c>&lt;use&gt;</c> is
    /// ringed at each of them and the whole thing is still one object to hand across to the render
    /// thread. Stroked, never filled, so the pieces need not be unioned.
    ///
    /// Not disposed when replaced: the render thread may still be drawing the snapshot holding it,
    /// and a path freed underneath it would take the process down. Left to the finalizer, which is
    /// affordable for something built only when somebody picks a row.
    ///
    /// In the arrangement's space rather than any one drawing's, and drawn once rather than once per
    /// placement. With a single drawing at the origin the two spaces are the same, so a viewer hands
    /// over what it traced; a host showing several — the preview of everything a project group
    /// builds — offsets the path by the placement it belongs to.
    /// </remarks>
    public SKPath? Highlight
    {
        get => _highlight;
        set
        {
            _highlight = value;

            _highlightAge.Restart();
            _pulse.IsEnabled = value is { };

            Publish();
        }
    }

    /// <summary>Replaces the ring's path, leaving the pulse that announced it alone.</summary>
    /// <remarks>
    /// A bound value moves what the ring traces, and dragging a slider moves it many times a second:
    /// going through <see cref="Highlight"/> would restart the pulse for every one of them and leave
    /// it flashing for as long as the drag lasted.
    /// </remarks>
    public void Retrace(SKPath? highlight)
    {
        if (ReferenceEquals(_highlight, highlight))
        {
            return;
        }

        _highlight = highlight;

        Publish();
    }

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

    /// <summary>The rectangles drawn under them, or nothing where the host asked for none.</summary>
    public IReadOnlyList<SvgViewerFrame> Frames => _frames;

    /// <summary>
    /// Shows several drawings at once, arranged by the caller.
    /// </summary>
    /// <remarks>
    /// One surface rather than one per drawing, so a set is zoomed, panned and outlined as the one
    /// thing it is. The arrangement is expected to start at the origin, as a single drawing's own
    /// picture does — the view is fitted to the size of what is placed, not to where it was put.
    /// </remarks>
    /// <param name="frames">
    /// Rectangles to draw under the drawings, or null for none. Handed in with them rather than set
    /// on their own, so the two can never be a frame apart.
    /// </param>
    public void Show(IReadOnlyList<SvgViewerPlacement> placed, IReadOnlyList<SvgViewerFrame>? frames = null)
    {
        _hasFitted = false;
        _userAdjusted = false;

        Place(placed ?? Array.Empty<SvgViewerPlacement>(), frames ?? Array.Empty<SvgViewerFrame>(), mayFit: true);
    }

    /// <summary>Lays out the arrangement already on show again, keeping the view on it.</summary>
    /// <remarks>
    /// What <see cref="Replace"/> is for one drawing, for several: <see cref="Show"/> means a board
    /// has arrived and re-fits, which is wrong for the same board being rearranged under the hand --
    /// a drop that re-fits moves the thing that was just dropped away from where it was let go.
    ///
    /// It holds the view whether or not anybody has zoomed, where <see cref="Replace"/> re-fits an
    /// untouched one. A single drawing has nothing beside it to hold still against and its own size
    /// is usually what the edit changed; a board has neighbours, and they must not move because one
    /// of them did. A board rearranged before the control has a size is still fitted once, since
    /// this leaves <c>_hasFitted</c> alone for the arrange pass to see.
    /// </remarks>
    public void Rearrange(IReadOnlyList<SvgViewerPlacement> placed, IReadOnlyList<SvgViewerFrame>? frames = null)
        => Place(placed ?? Array.Empty<SvgViewerPlacement>(), frames ?? Array.Empty<SvgViewerFrame>(), mayFit: false);

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

        Place(
            svg is { } ? new[] { new SvgViewerPlacement(svg, default) } : Array.Empty<SvgViewerPlacement>(),
            Array.Empty<SvgViewerFrame>(),
            mayFit: true);
    }

    private void Place(IReadOnlyList<SvgViewerPlacement> placed, IReadOnlyList<SvgViewerFrame> frames, bool mayFit)
    {
        // What is being carried may not be among these, and what is being edited holds references
        // into a document that is about to be thrown away.
        EndMove();
        EndEdit(commit: false);

        _placed = placed;
        _frames = frames;

        // Published because the drawing changed, whatever the view does about it. The fit below
        // publishes only when it moves the view, so a drawing swapped for one that fits exactly as
        // the last did — a padding change inside the same frame — left the render thread holding
        // the picture that had just been replaced, and the old one stayed up until something else
        // moved the view.
        Publish();

        if (!mayFit || _userAdjusted)
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

    /// <summary>
    /// Which drawing a control point fell on, and where on it.
    /// </summary>
    /// <remarks>
    /// <see cref="TryGetDrawingPoint"/> answers in the space the drawings are <em>arranged</em> in —
    /// the union of them all — which is a drawing's own space only when there is one of them at the
    /// origin. A host showing several needs to know which one was clicked before it can ask that one
    /// anything, and the arrangement is the host's own, so the canvas is the only thing that can say.
    ///
    /// Back to front, because that is the order they were drawn in and the last of them is the one
    /// on top. A drawing with nothing in it is not a candidate, so a click passes through it.
    /// </remarks>
    /// <returns>Whether the point fell on a drawing at all.</returns>
    public bool TryGetPlacementAt(Point point, out SvgViewerPlacement? placement, out SKPoint drawingPoint)
    {
        placement = null;
        drawingPoint = default;

        if (!TryGetDrawingPoint(point, out var arranged))
        {
            return false;
        }

        for (var index = _placed.Count - 1; index >= 0; index--)
        {
            var placed = _placed[index];

            if (Extent(placed) is not { } extent)
            {
                continue;
            }

            extent.Offset(placed.At);

            if (!extent.Contains(arranged.X, arranged.Y))
            {
                continue;
            }

            placement = placed;
            drawingPoint = new SKPoint(arranged.X - placed.At.X, arranged.Y - placed.At.Y);

            return true;
        }

        return false;
    }

    /// <summary>Converts a point in control coordinates to one in the space the drawings are arranged in.</summary>
    /// <remarks>
    /// Which is one drawing's own space only when there is one drawing, at the origin.
    /// <see cref="TryGetPlacementAt"/> is the one to ask otherwise.
    /// </remarks>
    public bool TryGetDrawingPoint(Point point, out SKPoint drawingPoint)
    {
        drawingPoint = default;

        if (_scale <= 0d || (_placed.Count == 0 && _frames.Count == 0))
        {
            return false;
        }

        drawingPoint = new SKPoint(
            (float)((point.X - _offsetX) / _scale),
            (float)((point.Y - _offsetY) / _scale));

        return true;
    }

    /// <summary>Converts a point in the space the drawings are arranged in back to control coordinates.</summary>
    /// <remarks>
    /// The inverse of <see cref="TryGetDrawingPoint"/>, for anything that has to put something on
    /// screen where the drawing says rather than read the drawing where the screen was clicked.
    /// </remarks>
    public bool TryGetControlPoint(SKPoint drawingPoint, out Point point)
    {
        point = default;

        if (_scale <= 0d || (_placed.Count == 0 && _frames.Count == 0))
        {
            return false;
        }

        // The plain inverse of TryGetDrawingPoint. It used to take the arrangement's corner off as
        // well, which was right while the view was anchored there and wrong since the fit folded
        // that corner into the offset — it would put every handle the width of the corner away from
        // the shape it belongs to.
        point = new Point(
            drawingPoint.X * _scale + _offsetX,
            drawingPoint.Y * _scale + _offsetY);

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
        // A pane resized while something is being carried or edited must not refit under it: the
        // gesture captured where it started from, and the ground would move under the hand.
        if (_moving is { } || _editing || size.Width <= 0d || size.Height <= 0d || !TryGetCullRect(out var bounds))
        {
            return false;
        }

        var scale = Math.Clamp(
            Math.Min(size.Width / bounds.Width, size.Height / bounds.Height),
            MinimumScale,
            MaximumScale);

        _hasFitted = true;
        _fittedTo = size;

        // The offset is where the arrangement's own origin goes, not where its ink begins, so the
        // fit takes the union's corner off here rather than the draw taking it off every frame.
        // That is what lets the view outlive a change to what is placed: an arrangement that grows
        // to the left moves its own corner, and anchoring on that would slide everything else.
        SetView(
            scale,
            (size.Width - bounds.Width * scale) / 2d - bounds.Left * scale,
            (size.Height - bounds.Height * scale) / 2d - bounds.Top * scale);

        return true;
    }

    /// <summary>What is on show, taken together: one drawing's own edges, or all of their union.</summary>
    private bool TryGetCullRect(out SKRect bounds)
    {
        bounds = default;

        var found = false;

        foreach (var placed in _placed)
        {
            if (Extent(placed) is not { } extent)
            {
                continue;
            }

            extent.Offset(placed.At);

            bounds = found ? SKRect.Union(bounds, extent) : extent;
            found = true;
        }

        // A frame reaching past the drawings it holds is part of what is on show, name and all, or
        // the fit would cut it off at the edge of the ink inside it.
        foreach (var framed in _frames)
        {
            var around = Named(framed);

            bounds = found ? SKRect.Union(bounds, around) : around;
            found = true;
        }

        return found && bounds.Width > 0f && bounds.Height > 0f;
    }

    /// <summary>How far a pointer may travel between press and release and still be a pick.</summary>
    private const double PickSlack = 4d;

    private static bool Away(Point moved, Point from)
        => Math.Abs(moved.X - from.X) > PickSlack || Math.Abs(moved.Y - from.Y) > PickSlack;

    /// <summary>One placed drawing's own edges, in its own space, or null where it has none.</summary>
    private static SKRect? Extent(SvgViewerPlacement placed)
        => placed.Svg.Picture is { CullRect: { Width: > 0f, Height: > 0f } cull } ? cull : null;

    /// <summary>A frame with the room its name needs above it.</summary>
    private static SKRect Named(SvgViewerFrame framed)
        => framed.Title.IsEmpty ? framed.Bounds : SKRect.Union(framed.Title, framed.Bounds);

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
    /// <remarks>
    /// Public because a host can change what the drawings say without changing which drawings they
    /// are: binding a parameter rewrites the recorded picture in place, and the canvas is holding
    /// the same placements it was, so nothing else would tell it to paint again.
    /// </remarks>
    public void Publish()
    {
        _snapshot = new Snapshot(
            _placed,
            _frames,
            _scale,
            _offsetX,
            _offsetY,
            _showBounds,
            _highlight,
            _highlightAge.Elapsed.TotalSeconds,
            _moving is { } && _moved ? (_movingBounds, _movingBy, Carried()) : null,

            // Not while something is being carried: the box is measured from a drawing that is on
            // its way somewhere else, so it would be left hanging over the board it has left.
            _moving is { } && _moved ? null : _gizmo);

        InvalidateVisual();
    }

    // ---- gestures ---------------------------------------------------------------------------

    /// <remarks>
    /// The trackpad path too: a two finger scroll arrives as a wheel event with a fractional delta.
    /// A pinch is a separate platform gesture, but Avalonia 12.0.0 keeps <c>Gestures</c> internal,
    /// so there is no public event for it.
    /// </remarks>
    /// <summary>How far one notch of the wheel moves the view, in pixels.</summary>
    /// <remarks>
    /// What a scrolling pane moves by, so a canvas inside an application full of them moves the same
    /// distance for the same gesture. A trackpad sends fractions of a notch and scales with it.
    /// </remarks>
    private const double WheelStep = 50d;

    /// <summary>
    /// The wheel moves the view, and moves it closer while the accelerator is held.
    /// </summary>
    /// <remarks>
    /// It used to zoom whatever was held, which left a trackpad with no way to pan at all: there is
    /// no middle button on one, and a press only pans where it lands on nothing — which on a board
    /// covered in drawings is the margins. Scrolling to pan and accelerator-scrolling to zoom is
    /// what every canvas this is next to on the same machine does.
    /// </remarks>
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        // The ground may not move under what is being carried, or the pointer and the thing under
        // it part company.
        if (_moving is { } || _placed.Count == 0)
        {
            return;
        }

        // Command on macOS, Control elsewhere, as the keyboard's own zoom is.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (!IsZoomEnabled)
            {
                return;
            }

            ZoomTo(_scale * Math.Pow(1.2d, e.Delta.Y), e.GetPosition(this));
            e.Handled = true;

            return;
        }

        if (!IsPanEnabled || e.Delta == default)
        {
            return;
        }

        // Added, so the view follows the fingers: a scroll downwards carries what is on the canvas
        // up, which is the direction the platform already means by it.
        SetView(_scale, _offsetX + (e.Delta.X * WheelStep), _offsetY + (e.Delta.Y * WheelStep));

        e.Handled = true;
    }

    /// <summary>
    /// A pinch on the trackpad zooms about the pointer.
    /// </summary>
    /// <remarks>
    /// The platform reports how much larger the gesture has just made whatever is under it, as a
    /// fraction: a twentieth means a twentieth bigger than a moment ago, so the scale is multiplied
    /// rather than added to and a pinch compounds the way the wheel's own notches do.
    ///
    /// Which way round the fraction arrives is the backend's business — macOS has one number and
    /// puts it in one of the two — so whichever is not zero is the one to read. Summing them would
    /// double the gesture on a backend that filled in both.
    /// </remarks>
    private void OnMagnify(object? sender, PointerDeltaEventArgs e)
    {
        // The ground may not move under what is being carried, as for the wheel.
        if (_moving is { } || !IsZoomEnabled || _placed.Count == 0)
        {
            return;
        }

        var magnification = e.Delta.Y != 0d ? e.Delta.Y : e.Delta.X;

        if (magnification == 0d)
        {
            return;
        }

        ZoomTo(_scale * (1d + magnification), e.GetPosition(this));

        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Both before the accelerators, since Escape carries no modifier, and one after the other
        // rather than in one condition: only one of the two gestures can be in flight, and each has
        // its own way back.
        if (e.Key == Key.Escape && _editing)
        {
            EndEdit(commit: false);

            e.Handled = true;

            return;
        }

        // A move taken back raises nothing: what was not let go of was not moved.
        if (e.Key == Key.Escape && _moving is { })
        {
            EndMove();
            _pressed = false;
            e.Handled = true;

            return;
        }

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

        // Recorded before the pan is decided on, so a host that has turned panning off can still be
        // clicked. Whether this becomes a pick is settled on release.
        _pressed = properties.IsLeftButtonPressed && _placed.Count > 0;
        _pressOrigin = e.GetPosition(this);

        // The narrowest claim first. This one answers only while a host has Edit mode on and only
        // over the element that host is already editing, so a miss falls straight through to the
        // grip. The other order cannot work: a grip answers for anywhere inside a whole drawing, so
        // it would swallow every handle sitting on top of one.
        if (properties.IsLeftButtonPressed && IsEditTarget is { } wanted && wanted(_pressOrigin))
        {
            // Not a pick either: the row is already selected, which is why it has handles.
            _pressed = false;
            _editing = true;
            _editMoved = false;
            _editPointer = e.Pointer;

            Focus();

            EditBegun?.Invoke(this, _pressOrigin);

            e.Pointer.Capture(this);
            e.Handled = true;

            return;
        }

        // Then the host's grip, before the pan, which used to claim every press before anything knew
        // what was under the pointer — so nothing on a canvas could ever be taken hold of. The
        // middle button still pans over an item, which is the way out when a board is covered in them.
        if (properties.IsLeftButtonPressed
            && Grip is { } grip
            && TryGetDrawingPoint(_pressOrigin, out var arranged)
            && grip(arranged) is { } held)
        {
            Focus();

            _moving = held.Item;
            _movingBounds = held.Bounds;
            _movingFrom = arranged;
            _movingBy = default;
            _moved = false;
            _movingPointer = e.Pointer;
            _restoreCursor = Cursor;

            e.Pointer.Capture(this);
            e.Handled = true;

            // _pressed is left standing: a press that never travels is still a pick.
            return;
        }

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
        if (_pressed && Away(e.GetPosition(this), _pressOrigin))
        {
            // Moved: the gesture is a drag, and a drag pans. Anything a hand does while clicking is
            // inside the slack and still a pick.
            _pressed = false;
        }

        if (_editing)
        {
            // Behind the same slack the pick is decided by, so the canvas has one idea of what a
            // click is. Without it the pointer creeping a pixel under a confirming click composes a
            // transform of very nearly nothing, and the host writes it to the file and spends an
            // undo step on it.
            if (_editMoved || Away(e.GetPosition(this), _pressOrigin))
            {
                _editMoved = true;

                EditMoved?.Invoke(this, e.GetPosition(this));
            }

            e.Handled = true;

            return;
        }

        if (_moving is { })
        {
            if (Away(e.GetPosition(this), _pressOrigin) && TryGetDrawingPoint(e.GetPosition(this), out var carried))
            {
                if (!_moved)
                {
                    // On the first travel rather than on the press, so a click never flashes a cursor.
                    _moved = true;
                    Cursor = s_grabCursor;
                }

                _movingBy = new SKPoint(carried.X - _movingFrom.X, carried.Y - _movingFrom.Y);

                Publish();
            }

            e.Handled = true;

            return;
        }

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
        if (_editing)
        {
            // A press that never travelled is taken back rather than committed: it is the click that
            // confirmed a selection, and the host has nothing to write.
            EndEdit(commit: true, at: e.GetPosition(this));

            e.Handled = true;

            return;
        }

        if (_moving is { } held)
        {
            var by = _movingBy;
            var travelled = _moved;

            // Read before the release below, which gives the pointer up — and giving it up is a
            // capture lost, which clears this on the way past.
            var picked = _pressed;

            // Let go of before the host is told, because the host calls straight back in — and a
            // handler that throws must not leave a drag stuck to the pointer.
            EndMove();

            _pressed = false;
            e.Handled = true;

            if (travelled)
            {
                Moved?.Invoke(this, new SvgViewerMove(held, by));
            }
            else if (picked)
            {
                Picked?.Invoke(this, e.GetPosition(this));
            }

            return;
        }

        if (_pressed)
        {
            _pressed = false;

            Picked?.Invoke(this, e.GetPosition(this));
        }

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Cursor = _restoreCursor;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>
    /// What travels with the rectangle being carried: everything drawn wholly inside it.
    /// </summary>
    /// <remarks>
    /// Geometry, which is all the canvas has — what belongs to what is the host's arrangement to
    /// know, and it said as much by handing back a rectangle round the lot.
    /// </remarks>
    private IReadOnlySet<SvgViewerPlacement> Carried()
    {
        // By reference: two placements of one drawing at one spot are equal as records, and only
        // the one under the pointer is being carried.
        var carried = new HashSet<SvgViewerPlacement>(ReferenceEqualityComparer.Instance);

        foreach (var placed in _placed)
        {
            if (Extent(placed) is not { } extent)
            {
                continue;
            }

            extent.Offset(placed.At);

            if (_movingBounds.Contains(extent))
            {
                carried.Add(placed);
            }
        }

        return carried;
    }

    /// <summary>Lets go of whatever was being carried, drawing it back where it was.</summary>
    private void EndMove()
    {
        if (_moving is null)
        {
            return;
        }

        _moving = null;
        _movingBy = default;
        _moved = false;
        _movingPointer?.Capture(null);
        _movingPointer = null;
        Cursor = _restoreCursor;

        Publish();
    }

    /// <summary>
    /// Ends an edit gesture, saying whether the host is to keep what the drag came to.
    /// </summary>
    /// <remarks>
    /// Shaped like <see cref="EndMove"/> and for the same reasons. The flag goes down before the
    /// capture is given back, because giving it back is a capture lost and that comes straight back
    /// in here. And the host is told last: it calls back in, and a handler that throws must not
    /// leave a gesture stuck to the pointer.
    /// </remarks>
    private void EndEdit(bool commit, Point at = default)
    {
        if (!_editing)
        {
            return;
        }

        var travelled = _editMoved;

        _editing = false;
        _editMoved = false;
        _editPointer?.Capture(null);
        _editPointer = null;

        if (commit && travelled)
        {
            EditEnded?.Invoke(this, at);
        }
        else
        {
            EditCancelled?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <remarks>
    /// The timer holds this control, so a canvas taken off the tree while its ring was still
    /// settling would go on repainting something nobody can see until the pulse ran out.
    /// </remarks>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _pulse.IsEnabled = false;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        _dragging = false;
        _pressed = false;

        // A drag the window took away writes nothing.
        EndMove();

        Cursor = _restoreCursor;

        // A gesture the window took away writes nothing either.
        EndEdit(commit: false);
    }

    // ---- drawing ----------------------------------------------------------------------------

    private void OnDraw(object? sender, SKCanvasEventArgs e)
    {
        // Render thread. Only the snapshot and the canvas may be touched here.
        var state = _snapshot;
        var canvas = e.Canvas;

        canvas.Clear(Background);

        if (state.Placed.Count == 0 && state.Frames.Count == 0)
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

        // Under the drawings, since a frame is a ground rather than an overlay, and outside their
        // transforms for the reason the ring is: a frame is in the arrangement's own space.
        foreach (var framed in state.Frames)
        {
            // A frame inside the one being carried goes with it, by the rule its drawings go by.
            var by = state.Moving is { } moving && moving.Bounds.Contains(framed.Bounds)
                ? moving.By
                : default;

            var bounds = framed.Bounds;

            bounds.Offset(by);

            Around(canvas, bounds, state.Scale);

            if (framed is { Label: { Length: > 0 } name, LabelSize: > 0f })
            {
                font.Size = framed.LabelSize;

                canvas.DrawText(
                    name,
                    bounds.Left,
                    bounds.Top - framed.LabelSize * 0.4f,
                    SKTextAlign.Left,
                    font,
                    writing);
            }
        }

        foreach (var placed in state.Placed)
        {
            canvas.Save();

            // Carried, so it follows the pointer while what is not carried holds still. Nothing the
            // host handed in is rewritten: the arrangement is still what it says it is until the
            // host is told where this was let go.
            if (state.Moving is { } moving && moving.Carried.Contains(placed))
            {
                canvas.Translate(moving.By.X, moving.By.Y);
            }

            canvas.Translate(placed.At.X, placed.At.Y);

            var page = Extent(placed);

            // A drawing is its page. Every other way this picture is rendered records at the cull
            // rect and so cuts off ink that has been moved beyond it — an export, the replay a
            // generated class does, a thumbnail. This draws into a surface the size of the control,
            // so without the clip it is the one renderer in the repository that shows what nothing
            // else will, painted over whatever is placed beside it and answering no hit test.
            //
            // Its own save, because the outline below is stroked ON that rectangle and a shared clip
            // would shave the outer half of the stroke off.
            if (page is { } sheet)
            {
                canvas.Save();
                canvas.ClipRect(sheet);
            }

            // SKSvg.Draw brackets itself with BeginDraw/EndDraw, so the picture cannot be disposed
            // underneath it by a value being bound on the UI thread.
            placed.Svg.Draw(canvas);

            if (page is { })
            {
                canvas.Restore();
            }

            if (page is { } frame)
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

        // Outside the loop, so it is drawn once wherever it was put rather than once per drawing on
        // top of each of them.
        if (state.Highlight is { } ringed)
        {
            // Carried with the drawing it is on. A ring is drawn round a shape of one drawing, so a
            // ring inside what is being carried is a ring on it — decided the way everything else
            // being carried is decided, by what the moving rectangle holds. Without this the ring
            // stays behind on the board while the drawing it belongs to moves out from under it.
            var on = state.Moving is { } move && move.Bounds.Contains(ringed.Bounds) ? move.By : default;

            canvas.Save();
            canvas.Translate(on.X, on.Y);

            Ring(canvas, ringed, state.Scale, state.HighlightAge);

            canvas.Restore();
        }

        // Over everything, since it is what the hand is on.
        if (state.Moving is { } carried)
        {
            var held = carried.Bounds;

            held.Offset(carried.By);

            Around(canvas, held, state.Scale);
        }

        // Last, so a handle is never drawn under the shape it is for. Never in the same frame as the
        // rectangle above it: a carry suppresses the box, which is measured from what is being
        // carried.
        if (state.Gizmo is { } gizmo)
        {
            Handles(canvas, gizmo, state.Scale);
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
    /// <summary>How long the ring's colour sweeps for after it appears.</summary>
    private const double PulseSeconds = 1.8d;

    /// <summary>How wide the ring is drawn, in screen pixels.</summary>
    private const float RingWidth = 3.5f;

    /// <summary>What the ring settles to, and what the sweep lifts it towards.</summary>
    private static readonly SKColor s_ringSettled = new(0xFF, 0x7A, 0x00);
    private static readonly SKColor s_ringLit = new(0xFF, 0xC8, 0x6E);

    /// <summary>
    /// Draws the selected element's silhouette.
    /// </summary>
    /// <remarks>
    /// One stroke, one width, and orange because a drawing is rarely orange: what says "this is the
    /// selection and not part of the picture" is a colour nothing else in the pane uses. The cost of
    /// a single line is that there is no fallback on a drawing that <em>is</em> orange, where it will
    /// be hard to pick out.
    ///
    /// The colour sweeps for the first <see cref="PulseSeconds"/> and settles. A ring around one
    /// shape among hundreds is easy to miss on a picture the eye is already reading, and a moment of
    /// movement is what finds it; the width is left alone, so nothing about the shape it is tracing
    /// appears to change. It dies away rather than beating on, because a pulse that ran for ever
    /// would repaint the whole drawing thirty times a second for as long as anything was selected.
    /// </remarks>
    private static void Ring(SKCanvas canvas, SKPath outline, double scale, double age)
    {
        // Fades to nothing across the window, so the colour comes to rest rather than stopping
        // wherever the sine happened to be.
        var settling = Math.Clamp(1d - age / PulseSeconds, 0d, 1d);
        var sweep = (float)(settling * (0.5d + 0.5d * Math.Sin(age * Math.PI * 2d / 0.6d)));

        using var line = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            Color = Between(s_ringSettled, s_ringLit, sweep),
            // In screen pixels whatever the zoom, so the ring reads the same on an icon filling the
            // window and on one at actual size.
            StrokeWidth = RingWidth * (float)(1d / scale),
            StrokeJoin = SKStrokeJoin.Round,
            StrokeCap = SKStrokeCap.Round
        };

        canvas.DrawPath(outline, line);
    }

    private static SKColor Between(SKColor from, SKColor to, float amount)
        => new(
            (byte)(from.Red + (to.Red - from.Red) * amount),
            (byte)(from.Green + (to.Green - from.Green) * amount),
            (byte)(from.Blue + (to.Blue - from.Blue) * amount));

    /// <summary>How wide a handle is drawn, in screen pixels.</summary>
    /// <remarks>
    /// <c>SelectionService.HandleSize</c>, which is what the press is hit-tested against. Drawing
    /// one size and answering to another is how a handle comes to be missed by a pixel.
    /// </remarks>
    private const float HandleWidth = SelectionService.HandleSize;

    /// <summary>
    /// Draws the box the drag acts on, and the handles that take hold of it.
    /// </summary>
    /// <remarks>
    /// The box is the element's own bounds mapped through everything above it, so it leans when an
    /// ancestor rotates — four corners drawn as a path, not a rectangle, because a rectangle would
    /// have to be axis-aligned and would then describe a shape nobody can grab.
    ///
    /// Filled white with the ring's own orange around them: white because a handle has to read on
    /// whatever it is sitting on, and orange because that is already what the selection is drawn in,
    /// so the box and the ring around the shape read as one thing rather than two.
    ///
    /// Every length is divided by the scale, which is what keeps a handle the same size on screen at
    /// every zoom. The rotate handle's own distance from the box is already in those units — it
    /// comes out of <c>SelectionService.GetBoundsInfo</c>, which was given the same scale.
    /// </remarks>
    private static void Handles(SKCanvas canvas, BoundsInfo box, double scale)
    {
        var hairline = (float)(1d / scale);
        var half = HandleWidth / 2f * hairline;

        using var line = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            Color = s_ringSettled,
            StrokeWidth = hairline
        };

        using var fill = new SKPaint { IsAntialias = true, Color = SKColors.White };

        using var corners = new SKPathBuilder();
        corners.MoveTo(box.TL);
        corners.LineTo(box.TR);
        corners.LineTo(box.BR);
        corners.LineTo(box.BL);
        corners.Close();

        using var frame = corners.Detach();

        canvas.DrawPath(frame, line);

        // The stalk before the handle, so the line stops under the circle rather than through it.
        canvas.DrawLine(box.TopMid, box.RotHandle, line);
        canvas.DrawCircle(box.RotHandle, half, fill);
        canvas.DrawCircle(box.RotHandle, half, line);

        foreach (var handle in new[] { box.TL, box.TopMid, box.TR, box.RightMid, box.BR, box.BottomMid, box.BL, box.LeftMid })
        {
            var square = new SKRect(handle.X - half, handle.Y - half, handle.X + half, handle.Y + half);

            canvas.DrawRect(square, fill);
            canvas.DrawRect(square, line);
        }
    }

    /// <summary>The line round a frame.</summary>
    /// <remarks>
    /// Solid where <see cref="Outline"/> is dashed, and drawn on the line rather than half a
    /// hairline inside it: the dash means "these are the drawing's own edges", which is the one
    /// thing a frame must not be read as.
    /// </remarks>
    private static void Around(SKCanvas canvas, SKRect frame, double scale)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            Color = SKColors.Gray.WithAlpha(0x99),
            StrokeWidth = (float)(1d / scale)
        };

        canvas.DrawRect(frame, paint);
    }

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
