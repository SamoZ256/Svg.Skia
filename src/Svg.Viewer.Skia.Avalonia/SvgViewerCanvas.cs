// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls.Skia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using SkiaSharp;
using Svg.Editor.Skia;
using Svg.SceneGraph;
using Svg.Skia;
using Shim = ShimSkiaSharp;

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

    /// <summary>The box arithmetic the editor stack already draws selections with.</summary>
    private readonly SelectionService _selection = new();

    private Cursor? _restoreCursor;

    /// <summary>The ground a host pinned, or null while it is the theme's to say.</summary>
    private SKColor? _background;
    private SKPath? _highlight;
    private BoundsInfo? _gizmo;
    private bool _gizmoTurns = true;
    private bool _editing;
    private bool _editMoved;
    private IPointer? _editPointer;

    /// <summary>The rectangle being swept, in the space the drawings are arranged in.</summary>
    /// <remarks>
    /// Arranged space and not the control's, because that is the space the query answers in and the
    /// space it is drawn in. Safe to hold across the gesture because the view is held still for the
    /// length of one: neither corner can be moved under the hand.
    /// </remarks>
    private bool _marquee;
    private bool _marqueeMoved;
    private SKPoint _marqueeFrom;
    private SKPoint _marqueeTo;
    private IPointer? _marqueePointer;

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
        Array.Empty<SvgViewerPlacement>(), Array.Empty<SvgViewerFrame>(), 1d, 0d, 0d, new(0x1A, 0x1A, 0x1E), null,
        0d, null, null, true, DefaultCaptionSize, null);

    private sealed record Snapshot(
        IReadOnlyList<SvgViewerPlacement> Placed,
        IReadOnlyList<SvgViewerFrame> Frames,
        double Scale,
        double OffsetX,
        double OffsetY,
        SKColor Ground,
        SKPath? Highlight,
        double HighlightAge,
        (SKRect Bounds, SKPoint By, IReadOnlySet<SvgViewerPlacement> Carried)? Moving,
        BoundsInfo? Gizmo,
        bool GizmoTurns,
        double CaptionSize,
        SKRect? Marquee);

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

        // A machine set to turn dark at sunset changes this under an editor nobody has touched,
        // and the ground is read from the variant.
        ActualThemeVariantChanged += (_, _) => Publish();
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

    /// <summary>
    /// Whether a left press that missed the narrower claims sweeps a rectangle rather than pans.
    /// </summary>
    /// <remarks>
    /// A plain switch and not a question like <see cref="IsEditTarget"/>, which needs the point
    /// because its answer differs from pixel to pixel — is this handle mine? A sweep's answer is the
    /// same everywhere the handles are not, so asking per point would be a lie about what varies.
    ///
    /// The view is still reachable while this is on: a middle drag pans, as does a two finger
    /// scroll, neither of which a sweep ever claims.
    /// </remarks>
    public bool IsMarqueeEnabled { get; set; }

    /// <summary>Raised where a sweep came to rest, as <see cref="Picked"/> reports a click.</summary>
    /// <remarks>
    /// In the space the drawings are arranged in, unlike <see cref="Picked"/>, which is in control
    /// coordinates: a rectangle is a question about the drawings rather than a place on the screen,
    /// and <see cref="Enclosed"/> — the only thing there is to ask of it — answers in that space.
    ///
    /// Only for a sweep that travelled. One that did not is a click, and <see cref="Picked"/> has it.
    /// </remarks>
    public event EventHandler<SKRect>? Marqueed;

    /// <summary>
    /// Raised as a sweep is drawn, with the rectangle as it now stands.
    /// </summary>
    /// <remarks>
    /// So that a host can show what the rectangle is over before anybody lets go of it — the answer
    /// to <see cref="Enclosed"/> is the same one the release will give. Null says the sweep is over
    /// and chose nothing, which is a host's cue to put back whatever it was showing before.
    /// </remarks>
    public event EventHandler<SKRect?>? Marqueeing;

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

    /// <summary>Whether the box drawn by <see cref="Gizmo"/> is of something that can be turned.</summary>
    /// <remarks>
    /// The stalk and its circle, which a page has no use for: a drawing's own edges are what its
    /// width and height say, and there is nowhere for a turned one to be written. Set beside the
    /// box rather than read off it, because a box is only eight points and a centre — what can be
    /// done to the thing under it is the host's to say.
    /// </remarks>
    public bool GizmoTurns
    {
        get => _gizmoTurns;
        set
        {
            if (_gizmoTurns == value)
            {
                return;
            }

            _gizmoTurns = value;

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

    /// <summary>
    /// What the surface is cleared to, which follows the theme unless a host says otherwise.
    /// </summary>
    /// <remarks>
    /// The canvas paints its own ground rather than letting the control's background through, so
    /// without this a light window kept a near-black hole where the drawing goes. Assigning it pins
    /// it: a host showing a drawing against a ground of its own — a thumbnail, a contrast check —
    /// means that ground whatever the window around it is doing.
    /// </remarks>
    public SKColor Background
    {
        get => _background ?? Grounded(ActualThemeVariant);

        set
        {
            _background = value;

            Publish();
        }
    }

    /// <summary>
    /// The ground each variant is painted on.
    /// </summary>
    /// <remarks>
    /// Mirrors of one another, and the light one is deliberately not white: the gizmo's handles are
    /// filled white so they read on whatever they sit on, and on white they would be an outline
    /// round nothing.
    /// </remarks>
    private static SKColor Grounded(ThemeVariant variant)
        => variant == ThemeVariant.Light ? new SKColor(0xF2, 0xF2, 0xF4) : new SKColor(0x1A, 0x1A, 0x1E);

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

        // And a rectangle measured on an arrangement about to be replaced names nothing in the one
        // that follows it.
        EndMarquee(commit: false);

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
        if (_moving is { } || _editing || _marquee || size.Width <= 0d || size.Height <= 0d || !TryGetCullRect(out var bounds))
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

        // A frame reaching past the drawings it holds is part of what is on show, or the fit would
        // cut it off at the edge of the ink inside it. Its name needs nothing added for: it is
        // written inside.
        foreach (var framed in _frames)
        {
            bounds = found ? SKRect.Union(bounds, framed.Bounds) : framed.Bounds;
            found = true;
        }

        return found && bounds.Width > 0f && bounds.Height > 0f;
    }

    /// <summary>How far a pointer may travel between press and release and still be a pick.</summary>
    private const double PickSlack = 4d;

    private static bool Away(Point moved, Point from)
        => Math.Abs(moved.X - from.X) > PickSlack || Math.Abs(moved.Y - from.Y) > PickSlack;

    /// <summary>One placed drawing's own edges, in its own space, or null where it has none.</summary>
    /// <remarks>
    /// Its own space and not the board's: a host that wants the page where it is drawn offsets by
    /// the placement, which is nothing for the one drawing a viewer shows.
    /// </remarks>
    public static SKRect? Extent(SvgViewerPlacement placed)
        => placed.Svg.Picture is { CullRect: { Width: > 0f, Height: > 0f } cull } ? cull : null;

    /// <summary>How big a caption is drawn where nobody says otherwise, in control pixels.</summary>
    public const double DefaultCaptionSize = 13d;

    /// <summary>The smallest and largest a caption may be asked to be.</summary>
    /// <remarks>
    /// Below the first it cannot be read and above the second it covers what it names, and both are
    /// the sort of value a settings box will be handed by somebody finding out what it does.
    /// </remarks>
    public const double MinimumCaptionSize = 8d;

    /// <inheritdoc cref="MinimumCaptionSize"/>
    public const double MaximumCaptionSize = 32d;

    private double _captionSize = DefaultCaptionSize;

    /// <summary>How big a caption is drawn, in control pixels.</summary>
    /// <remarks>
    /// A caption names what it sits by; it is not part of what is drawn. Scaled with the drawings it
    /// was unreadable zoomed out and enormous zoomed in, and the strip a frame is taken hold of by
    /// went with it — a target that changed size as you approached it. The frame's own outline and
    /// its dashes are already held at one pixel the same way.
    ///
    /// Settable because how big is readable is about the screen somebody is at rather than about the
    /// drawings, which is the one thing this cannot work out for itself.
    /// </remarks>
    public double CaptionSize
    {
        get => _captionSize;
        set
        {
            var wanted = Math.Clamp(value, MinimumCaptionSize, MaximumCaptionSize);

            if (_captionSize.Equals(wanted))
            {
                return;
            }

            _captionSize = wanted;

            Publish();
        }
    }

    /// <summary>
    /// Where <paramref name="framed"/>'s name is written, in the space the drawings are arranged in.
    /// </summary>
    /// <remarks>
    /// So a host can take a frame hold of by its name rather than by anywhere inside it. What is
    /// inside it is mostly the room between the drawings it holds, and a host that grabbed that left
    /// nowhere to move the view from.
    ///
    /// The strip is a fixed size on the control, like the writing in it, so it comes back here in
    /// drawing units and changes as the view is zoomed. It runs down from the top edge and along as
    /// far as the name does, which is where the name is written.
    /// </remarks>
    /// <summary>How near an outline a press counts as being on it, in control pixels.</summary>
    /// <remarks>
    /// A line one pixel wide is not something a pointer can be asked to land on, and the slack is
    /// held in control pixels for the reason the line's width is: the target must not change size as
    /// the view is zoomed.
    /// </remarks>
    private const double EdgeSlack = 4d;

    /// <summary>
    /// Whether <paramref name="at"/> is on the line round <paramref name="bounds"/>.
    /// </summary>
    /// <remarks>
    /// On the line rather than within it: what a rectangle encloses is the drawing, or the room
    /// between the drawings a frame holds, and a host that answered for all of that would take every
    /// press that fell on a shape. A rectangle too small to have an inside is all edge, which is the
    /// right answer for one.
    /// </remarks>
    public bool Grabs(SKRect bounds, SKPoint at)
    {
        var slack = (float)(EdgeSlack / _scale);

        return SKRect.Inflate(bounds, slack, slack).Contains(at.X, at.Y)
               && !SKRect.Inflate(bounds, -slack, -slack).Contains(at.X, at.Y);
    }

    /// <summary>
    /// Whether <paramref name="at"/> is on the chrome <paramref name="framed"/> is taken hold of by.
    /// </summary>
    /// <remarks>Its name as well as its outline, which a drawing has no equivalent of.</remarks>
    public bool Grabs(SvgViewerFrame framed, SKPoint at)
    {
        if (framed is null)
        {
            throw new ArgumentNullException(nameof(framed));
        }

        return TitleOf(framed).Contains(at.X, at.Y) || Grabs(framed.Bounds, at);
    }

    public SKRect TitleOf(SvgViewerFrame framed)
    {
        if (framed is null)
        {
            throw new ArgumentNullException(nameof(framed));
        }

        if (framed is not { Label: { Length: > 0 } name })
        {
            return SKRect.Empty;
        }

        var tall = (float)(_captionSize / _scale);

        using var font = new SKFont(SKTypeface.Default, tall);

        // As wide as the name and no wider. The frame's full width would be the whole top of it,
        // and since this is answered before the drawings are, that would take the top strip off
        // every drawing standing under it.
        return new SKRect(
            framed.Bounds.Left,
            framed.Bounds.Top,
            framed.Bounds.Left + (tall * 0.7f) + font.MeasureText(name),
            framed.Bounds.Top + (tall * 1.6f));
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

            // Resolved here rather than in the draw: the variant is the UI thread's to read, and
            // the render thread may read nothing but this.
            Background,
            _highlight,
            _highlightAge.Elapsed.TotalSeconds,
            _moving is { } && _moved ? (_movingBounds, _movingBy, Carried()) : null,

            // Not while something is being carried: the box is measured from a drawing that is on
            // its way somewhere else, so it would be left hanging over the board it has left.
            _moving is { } && _moved ? null : _gizmo,
            _gizmoTurns,
            _captionSize,

            // Only once it has travelled, as the carried rectangle is: a press inside the slack has
            // no rectangle yet, and a zero-sized one would flash a dot under every click.
            _marquee && _marqueeMoved ? Spanned(_marqueeFrom, _marqueeTo) : null);

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
        if (_moving is { } || _marquee || _placed.Count == 0)
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
        if (_moving is { } || _marquee || !IsZoomEnabled || _placed.Count == 0)
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

        // A sweep taken back raises nothing: a rectangle let go of selects nothing.
        if (e.Key == Key.Escape && _marquee)
        {
            EndMarquee(commit: false);
            _pressed = false;
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

        // Last of the left button's meanings, and the widest: a press that was not a handle and not
        // something to carry is the start of a rectangle over whatever is there. It sits below the
        // grip because carrying a drawing is the narrower thing to have meant — a grip answers only
        // where a drawing is, and a sweep answers everywhere else.
        if (properties.IsLeftButtonPressed
            && IsMarqueeEnabled
            && TryGetDrawingPoint(_pressOrigin, out var swept))
        {
            Focus();

            _marquee = true;
            _marqueeMoved = false;
            _marqueeFrom = swept;
            _marqueeTo = swept;
            _marqueePointer = e.Pointer;

            e.Pointer.Capture(this);
            e.Handled = true;

            // _pressed is left standing, as the grip leaves it: a sweep that never travels is a
            // click, and with nothing selected yet a click is the only way to select anything.
            return;
        }

        // What is left is the view's own, and the left button is not among its gestures wherever a
        // sweep is offered: it was claimed above, and never reaches here.
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

        if (_marquee)
        {
            // Behind the same slack, so nothing is drawn under a click; and handled either way, so
            // a pan can never start underneath a sweep that has not travelled yet.
            if ((_marqueeMoved || Away(e.GetPosition(this), _pressOrigin))
                && TryGetDrawingPoint(e.GetPosition(this), out var to))
            {
                _marqueeMoved = true;
                _marqueeTo = to;

                Publish();

                Marqueeing?.Invoke(this, Spanned(_marqueeFrom, _marqueeTo));
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

        if (_marquee)
        {
            // Read before EndMarquee gives the pointer up, which is a capture lost and clears it.
            var picked = _pressed;

            EndMarquee(commit: true);

            _pressed = false;
            e.Handled = true;

            if (picked)
            {
                Picked?.Invoke(this, e.GetPosition(this));
            }

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
    /// Every element wholly inside <paramref name="marquee"/>, under the drawing it belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Geometry, which is all the canvas has, as <see cref="Carried"/> is: which of them a host
    /// then selects, and how it names them, is the host's arrangement to know.
    /// </para>
    /// <para>
    /// The rectangle arrives in the space the drawings are arranged in and each drawing is asked in
    /// its own, which is the whole of what lets one sweep catch elements of several drawings at
    /// once.
    /// </para>
    /// <para>
    /// Wholly inside, measured on the same box the handles are drawn on — so what a sweep catches
    /// is exactly what you can see a box would be put round. Asked at a scale of one because that
    /// box's corners do not depend on the scale; only the stalk does, and no corner is the stalk.
    /// </para>
    /// </remarks>
    public IReadOnlyList<(SvgViewerPlacement Placement, IReadOnlyList<SvgElement> Elements)> Enclosed(SKRect marquee)
        => Sweeping(marquee).ToList();

    /// <inheritdoc cref="Enclosed"/>
    /// <remarks>
    /// Drawing by drawing as they are asked for, so a caller that only wants to know whether a
    /// second one answers can stop there rather than paying for the whole board. The walk itself is
    /// the same one; this is where it lives and <see cref="Enclosed"/> is it, run to the end.
    /// </remarks>
    public IEnumerable<(SvgViewerPlacement Placement, IReadOnlyList<SvgElement> Elements)> Sweeping(SKRect marquee)
    {
        foreach (var placed in _placed)
        {
            var local = marquee;

            local.Offset(-placed.At.X, -placed.At.Y);

            if (Extent(placed) is not { } extent || !extent.IntersectsWith(local))
            {
                continue;
            }

            var caught = new List<SvgSceneNode>();
            var inside = new HashSet<SvgSceneNode>();

            // The scene graph speaks the recorded model's rectangle, not Skia's own.
            var asked = new Shim.SKRect(local.Left, local.Top, local.Right, local.Bottom);

            foreach (var node in placed.Svg.HitTestSceneNodes(asked))
            {
                // The page is not something anybody swept for: a rectangle round a whole drawing
                // would otherwise answer with the drawing and nothing that is on it.
                if (node.Parent is null || node.Element is null or SvgFragment)
                {
                    continue;
                }

                // The rectangle walk does not drop what is not drawn, though the point walk does.
                if (node.IsDisplayNone || !node.IsVisible)
                {
                    continue;
                }

                if (!SelectionService.ContainsRect(
                        local,
                        SelectionService.GetBoundsRect(_selection.GetBoundsInfo(node, Unscaled))))
                {
                    continue;
                }

                caught.Add(node);
                inside.Add(node);
            }

            var elements = new List<SvgElement>();
            var seen = new HashSet<SvgElement>();

            foreach (var node in caught)
            {
                // A group inside the rectangle brings everything under it, and the walk hands back
                // both. Answering with both would have a gesture applied to a shape and again to
                // the group holding it.
                if (!Outermost(node, inside))
                {
                    continue;
                }

                var element = node.HitTestTargetElement ?? node.Element!;

                if (seen.Add(element))
                {
                    elements.Add(element);
                }
            }

            if (elements.Count > 0)
            {
                yield return (placed, elements);
            }
        }
    }

    /// <summary>Whether nothing above <paramref name="node"/> was caught by the same rectangle.</summary>
    /// <remarks>
    /// Walked rather than read off the order the nodes arrived in: that order belongs to a service
    /// in another assembly, and this answer does not depend on it.
    /// </remarks>
    private static bool Outermost(SvgSceneNode node, HashSet<SvgSceneNode> caught)
    {
        for (var above = node.Parent; above is { }; above = above.Parent)
        {
            if (caught.Contains(above))
            {
                return false;
            }
        }

        return true;
    }

    private static float Unscaled() => 1f;

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

    /// <summary>Lets go of a swept rectangle, saying whether the host is to be told about it.</summary>
    /// <remarks>
    /// Shaped like <see cref="EndEdit"/>: the flag goes down before the capture is given back,
    /// because giving it back is a capture lost and that comes straight back in here; the rectangle
    /// leaves the screen before the host is told; and the host is told last, because it calls back
    /// in and a handler that throws must not leave a gesture stuck to the pointer.
    ///
    /// A sweep taken back raises nothing, as a carry taken back does: nothing was held.
    /// </remarks>
    private void EndMarquee(bool commit)
    {
        if (!_marquee)
        {
            return;
        }

        var swept = _marqueeMoved ? Spanned(_marqueeFrom, _marqueeTo) : (SKRect?)null;
        var showing = _marqueeMoved;

        _marquee = false;
        _marqueeMoved = false;
        _marqueePointer?.Capture(null);
        _marqueePointer = null;

        Publish();

        if (commit && swept is { } rectangle)
        {
            Marqueed?.Invoke(this, rectangle);

            return;
        }

        // Taken back, so whatever the host was showing along the way is about to be wrong.
        if (showing)
        {
            Marqueeing?.Invoke(this, null);
        }
    }

    /// <summary>The rectangle two corners span, whichever way round they were drawn.</summary>
    private static SKRect Spanned(SKPoint from, SKPoint to)
        => new(
            Math.Min(from.X, to.X),
            Math.Min(from.Y, to.Y),
            Math.Max(from.X, to.X),
            Math.Max(from.Y, to.Y));

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
        EndMarquee(commit: false);
    }

    // ---- drawing ----------------------------------------------------------------------------

    private void OnDraw(object? sender, SKCanvasEventArgs e)
    {
        // Render thread. Only the snapshot and the canvas may be touched here.
        var state = _snapshot;
        var canvas = e.Canvas;

        canvas.Clear(state.Ground);

        if (state.Placed.Count == 0 && state.Frames.Count == 0)
        {
            return;
        }

        canvas.Save();
        canvas.Translate((float)state.OffsetX, (float)state.OffsetY);
        canvas.Scale((float)state.Scale);

        // Built once for the frames rather than one per name: setting the size on a font costs
        // nothing next to building one.
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
                Outline(canvas, frame, state.Scale);
            }

            canvas.Restore();
        }

        // After the drawings rather than with the frames they belong to, which are drawn under. A
        // frame is a ground and a name is not: written under the drawings it went behind the ones
        // that reach into the corner it sits in, which since it moved inside is most of them.
        foreach (var framed in state.Frames)
        {
            if (framed is not { Label: { Length: > 0 } name })
            {
                continue;
            }

            var by = state.Moving is { } moving && moving.Bounds.Contains(framed.Bounds)
                ? moving.By
                : default;

            // Divided by the scale so it comes out the same size on the control however far the
            // view is zoomed, which is what the outline round it already does with its width.
            var tall = (float)(state.CaptionSize / state.Scale);

            font.Size = tall;

            // Inside its top corner, off both edges by a third of the writing's height so it does
            // not sit on the line it is naming.
            canvas.DrawText(
                name,
                framed.Bounds.Left + by.X + (tall * 0.35f),
                framed.Bounds.Top + by.Y + (tall * 1.15f),
                SKTextAlign.Left,
                font,
                writing);
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
        // carried rectangle above it, which suppresses the box: that one is measured from a drawing
        // on its way somewhere else. A swept rectangle does share the frame — nothing is moving, so
        // the handles are still telling the truth about what is selected.
        if (state.Gizmo is { } gizmo)
        {
            Handles(canvas, gizmo, state.Scale, state.GizmoTurns);
        }

        // Over the handles, because this is what the hand is doing now and the selection behind it
        // is what it is about to replace.
        if (state.Marquee is { } swept)
        {
            Marquee(canvas, swept, state.Scale);
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
    private static void Handles(SKCanvas canvas, BoundsInfo box, double scale, bool turns)
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

        if (turns)
        {
            // The stalk before the handle, so the line stops under the circle rather than through it.
            canvas.DrawLine(box.TopMid, box.RotHandle, line);
            canvas.DrawCircle(box.RotHandle, half, fill);
            canvas.DrawCircle(box.RotHandle, half, line);
        }

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

    /// <summary>The rectangle being swept, as the hand is drawing it.</summary>
    /// <remarks>
    /// Dashed like <see cref="Outline"/> and coloured like <see cref="Ring"/>, which is what it is:
    /// a selection that has not happened yet. The colour is what keeps the two dashes apart — grey
    /// dashes already mean a drawing's own edges, and a second grey rectangle over a board of them
    /// would read as one more page.
    ///
    /// Drawn on the line rather than half a hairline inside it, unlike the one above: this rectangle
    /// is the question being asked, and the only honest place to draw it is where it will be asked.
    /// </remarks>
    private static void Marquee(SKCanvas canvas, SKRect swept, double scale)
    {
        var hairline = (float)(1d / scale);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            Color = s_ringSettled,
            StrokeWidth = hairline,
            PathEffect = SKPathEffect.CreateDash(new[] { 4f * hairline, 4f * hairline }, 0f)
        };

        canvas.DrawRect(swept, paint);
    }
}
