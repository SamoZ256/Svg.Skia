// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;

namespace ShimSkiaSharp.Editing;

/// <summary>How a back end draws a save-layer, as <see cref="CommandPlan.Layer"/> plans it.</summary>
public enum LayerPlan
{
    Layer,

    /// <summary>A plain save once the paint's alpha is 255, which generated code only learns at run time.</summary>
    SaveIfOpaque,

    /// <summary>A plain save whatever the alpha: the one draw inside takes it (<see cref="CommandPlan.FoldedInto"/>).</summary>
    Fold
}

/// <summary>
/// Which of a command list's clips cut nothing and which of its layers a plain save draws the same,
/// decided in one pass that every back end reads, so the interpreter and the C# export cannot part.
/// </summary>
/// <remarks>
/// Skia keeps an antialiased clip that is tilted or off the pixel grid as a coverage mask, and blends
/// every draw and every layer under it through that mask (blinds180ltr: 59.7 µs with its seven
/// layers, 8.0 µs without). The model stays as recorded: the editor and hit testing read its clips.
/// </remarks>
public sealed class CommandPlan
{
    private static readonly CommandPlan s_empty = new(0);

    private readonly bool[] _skips;
    private readonly LayerPlan[] _layers;
    private readonly List<int>?[] _folds;

    private CommandPlan(int count)
    {
        _skips = new bool[count];
        _layers = new LayerPlan[count];
        _folds = new List<int>?[count];
    }

    /// <summary>Whether the clip at <paramref name="index"/> contains everything drawn under it.</summary>
    public bool Skips(int index) => index < _skips.Length && _skips[index];

    public LayerPlan Layer(int index) => index < _layers.Length ? _layers[index] : LayerPlan.Layer;

    /// <summary>
    /// The folded layers whose alpha the draw at <paramref name="index"/> multiplies into its paint,
    /// innermost first, or null.
    /// </summary>
    public IReadOnlyList<int>? FoldedInto(int index) => index < _folds.Length ? _folds[index] : null;

    /// <param name="commands">One picture's commands; a nested picture is planned when it is drawn.</param>
    /// <param name="symbolic">
    /// Whether the plan has to hold for every value of the expressions in the list, as generated code
    /// needs; false where the list holds the values drawn, as the interpreter's does.
    /// </param>
    public static CommandPlan For(IList<CanvasCommand>? commands, bool symbolic)
        => commands is null || commands.Count == 0 ? s_empty : new Planner(commands, symbolic).Plan();

    private sealed class Frame
    {
        public int Layer = -1;
        public Affine Matrix;
        public int Clips;
        public int Draws;
        public int Draw = -1;
        public bool Foldable = true;
        public bool SourceOver = true;
        public bool Bounded;
        public bool Masks;
    }

    private sealed class Planner
    {
        private readonly IList<CanvasCommand> _commands;
        private readonly bool _symbolic;
        private readonly CommandPlan _plan;
        private readonly List<Frame> _frames = new();
        private readonly List<Region> _clips = new();
        private Affine _matrix = Affine.Identity;

        public Planner(IList<CanvasCommand> commands, bool symbolic)
        {
            _commands = commands;
            _symbolic = symbolic;
            _plan = new CommandPlan(commands.Count);
            _frames.Add(new Frame());
        }

        public CommandPlan Plan()
        {
            for (var i = 0; i < _commands.Count; i++)
            {
                switch (_commands[i])
                {
                    case SaveCanvasCommand:
                        Push(-1, null);
                        break;
                    case SaveLayerCanvasCommand saveLayer:
                        Layered(saveLayer.Paint);
                        Blend(saveLayer.Paint);
                        Push(i, saveLayer.Paint);
                        break;
                    case RestoreCanvasCommand:
                        if (_frames.Count > 1)
                        {
                            Pop(restored: true);
                        }
                        break;
                    case SetMatrixCanvasCommand setMatrix:
                        if (_symbolic && setMatrix.SymbolicDelta is { })
                        {
                            Uncontain();
                        }

                        _matrix = _matrix.PreConcat(Affine.From(setMatrix.DeltaMatrix));
                        break;
                    // Only an antialiased clip costs a coverage mask. An aliased one is all but free,
                    // and cuts whole pixels: dropping it would bring back the edge of a marker drawn
                    // to its viewport's edge (painting-marker-03-f, 192/255).
                    case ClipRectCanvasCommand { Operation: SKClipOperation.Intersect, Antialias: true } clipRect:
                        Open(Region.Rect(i, clipRect.Rect, _matrix));
                        break;
                    case ClipPathCanvasCommand { Operation: SKClipOperation.Intersect, Antialias: true, ClipPath: { } clipPath }:
                        Open(Region.From(i, clipPath, _matrix));
                        break;
                    case ClipRectCanvasCommand or ClipPathCanvasCommand or BeginConditionalCanvasCommand or EndConditionalCanvasCommand or BeginCallCanvasCommand or EndCallCanvasCommand:
                        break;
                    case DrawPictureCanvasCommand { Picture: { } picture }:
                        Picture(picture.Commands, _matrix);
                        Drawn(i, fades: false);
                        break;
                    case DrawPictureCanvasCommand:
                        break;
                    case var draw:
                        Blend(draw);
                        if (Measure(draw, out var bounds) is not { } extent)
                        {
                            break;
                        }

                        if (extent)
                        {
                            Contain(bounds, _matrix);
                        }
                        else
                        {
                            Uncontain();
                        }

                        Drawn(i, Fades(draw));
                        break;
                }
            }

            // An unrestored clip outlives the list: the interpreter draws a nested picture without a
            // save around it, so what follows the picture is under that clip too.
            while (_frames.Count > 1)
            {
                Pop(restored: false);
            }

            return _plan;
        }

        private void Push(int layer, SKPaint? paint)
            => _frames.Add(new Frame
            {
                Layer = layer,
                Matrix = _matrix,
                Clips = _clips.Count,
                Bounded = layer >= 0 && Bounded(paint),
                Masks = layer >= 0 && Masking(paint)
            });

        private void Pop(bool restored)
        {
            var frame = _frames[_frames.Count - 1];
            _frames.RemoveAt(_frames.Count - 1);

            for (var i = frame.Clips; i < _clips.Count; i++)
            {
                if (restored && _clips[i] is { CutsNothing: true } clip)
                {
                    _plan._skips[clip.Index] = true;
                }
            }

            _clips.RemoveRange(frame.Clips, _clips.Count - frame.Clips);
            _matrix = frame.Matrix;

            if (frame.Layer < 0)
            {
                return;
            }

            var plan = PlanLayer(frame);
            _plan._layers[frame.Layer] = plan;
            if (plan == LayerPlan.Fold)
            {
                (_plan._folds[frame.Draw] ??= new List<int>(1)).Add(frame.Layer);
                return;
            }

            // A layer that stays one is not a draw an enclosing layer could fold.
            foreach (var enclosing in _frames)
            {
                enclosing.Foldable = false;
            }
        }

        private LayerPlan PlanLayer(Frame frame)
        {
            if (((SaveLayerCanvasCommand)_commands[frame.Layer]).Paint is { } paint &&
                (paint.Color is null ||
                 paint.BlendMode != SKBlendMode.SrcOver ||
                 paint.Shader is { } ||
                 paint.ColorFilter is { } ||
                 paint.ImageFilter is { } ||
                 paint.PathEffect is { }))
            {
                return LayerPlan.Layer;
            }

            // One shape cannot overlap itself, so fading its paint fades the group. A mask or an isolated
            // group opens an opaque source-over layer too, and that layer is what keeps the DstIn or the
            // blend mode inside off the backdrop: neither folds, nor saves.
            if (frame.Draws == 1 && frame.Foldable)
            {
                return LayerPlan.Fold;
            }

            return frame.SourceOver ? LayerPlan.SaveIfOpaque : LayerPlan.Layer;
        }

        private void Open(Region? region)
        {
            if (region is { })
            {
                region.Depth = _frames.Count;
                _clips.Add(region);
            }
        }

        private void Contain(SKRect bounds, Affine matrix)
        {
            foreach (var clip in _clips)
            {
                if (clip.CutsNothing && !Masked(clip) && !clip.Contains(bounds, matrix))
                {
                    clip.CutsNothing = false;
                }
            }
        }

        private void Uncontain()
        {
            foreach (var clip in _clips)
            {
                if (!Masked(clip))
                {
                    clip.CutsNothing = false;
                }
            }
        }

        // A blend mode reaches past what is drawn only into its backdrop. Inside a layer opened under
        // the clip that backdrop is the layer's own pixels, transparent wherever nothing was drawn,
        // so a mask's DstIn there cuts the clip's content and nothing beyond it, whatever filter the
        // mask carries (a luminance mask's colour filter). A filter on another mode can make pixels.
        private void Layered(SKPaint? paint)
        {
            if (Bounded(paint))
            {
                return;
            }

            foreach (var clip in _clips)
            {
                if (!(Masking(paint) || BlendsOnly(paint!)) || !Isolated(clip))
                {
                    clip.CutsNothing = false;
                }
            }
        }

        private bool Isolated(Region clip)
        {
            for (var i = clip.Depth; i < _frames.Count; i++)
            {
                if (_frames[i] is { Layer: >= 0, Bounded: true })
                {
                    return true;
                }
            }

            return false;
        }

        // Inside a mask nothing drawn can add coverage, only take it from what the mask composites onto.
        private bool Masked(Region clip)
        {
            for (var i = clip.Depth; i < _frames.Count; i++)
            {
                if (_frames[i].Masks)
                {
                    return true;
                }
            }

            return false;
        }

        private void Drawn(int index, bool fades)
        {
            foreach (var frame in _frames)
            {
                if (frame.Layer >= 0)
                {
                    frame.Draws++;
                    frame.Draw = index;
                    frame.Foldable &= fades;
                }
            }
        }

        private void Blend(CanvasCommand command)
        {
            if (!IsSourceOver(command))
            {
                Blended();
            }
        }

        private void Blend(SKPaint? paint)
        {
            if (paint is { BlendMode: not SKBlendMode.SrcOver })
            {
                Blended();
            }
        }

        private void Blended()
        {
            foreach (var frame in _frames)
            {
                frame.SourceOver = false;
            }
        }

        // What a nested picture draws, for the clips around it: it starts at the matrix it is drawn at.
        private void Picture(IList<CanvasCommand>? commands, Affine matrix)
        {
            if (commands is null)
            {
                return;
            }

            var saved = new Stack<Affine>();
            foreach (var command in commands)
            {
                switch (command)
                {
                    case SaveCanvasCommand:
                        saved.Push(matrix);
                        break;
                    case SaveLayerCanvasCommand saveLayer:
                        Layered(saveLayer.Paint);
                        Blend(saveLayer.Paint);
                        saved.Push(matrix);
                        break;
                    case RestoreCanvasCommand:
                        if (saved.Count > 0)
                        {
                            matrix = saved.Pop();
                        }
                        break;
                    case SetMatrixCanvasCommand setMatrix:
                        if (_symbolic && setMatrix.SymbolicDelta is { })
                        {
                            Uncontain();
                        }

                        matrix = matrix.PreConcat(Affine.From(setMatrix.DeltaMatrix));
                        break;
                    case ClipRectCanvasCommand or ClipPathCanvasCommand or BeginConditionalCanvasCommand or EndConditionalCanvasCommand or BeginCallCanvasCommand or EndCallCanvasCommand:
                        break;
                    case DrawPictureCanvasCommand { Picture: { } picture }:
                        Picture(picture.Commands, matrix);
                        break;
                    case DrawPictureCanvasCommand:
                        break;
                    case var draw:
                        Blend(draw);
                        if (Measure(draw, out var bounds) is { } extent)
                        {
                            if (extent)
                            {
                                Contain(bounds, matrix);
                            }
                            else
                            {
                                Uncontain();
                            }
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Where <paramref name="command"/> can touch pixels, in its own space: true with the bounds, false
        /// when that is unknown or unbounded, null when it draws nothing.
        /// </summary>
        private bool? Measure(CanvasCommand command, out SKRect bounds)
        {
            bounds = default;
            switch (command)
            {
                case DrawPathCanvasCommand { Path: { } path, Paint: { } paint }:
                    {
                        if (path.IsEmpty)
                        {
                            return null;
                        }

                        // A dash, the only path effect, draws a part of the path.
                        if (paint.ImageFilter is { } ||
                            paint.BlendMode != SKBlendMode.SrcOver ||
                            HasTurnedArc(path))
                        {
                            return false;
                        }

                        bounds = path.Bounds;
                        if (paint.Style == SKPaintStyle.Fill)
                        {
                            // A line's fill, which an SVG path with a stroke records beside it, covers nothing.
                            return bounds.Width > 0 && bounds.Height > 0 ? true : null;
                        }

                        if (paint.StrokeWidth <= 0 ||
                            paint is { IsStrokeNonScaling: true, Style: SKPaintStyle.Stroke } ||
                            _symbolic && paint.StrokeWidthExpression is { })
                        {
                            return false;
                        }

                        bounds = StrokeReach.Bounds(path, paint);
                        return true;
                    }
                case DrawPathCanvasCommand:
                    return null;
                case DrawImageCanvasCommand { Image: { } } drawImage:
                    if (drawImage.Paint is { } imagePaint &&
                        (imagePaint.ImageFilter is { } || imagePaint.BlendMode != SKBlendMode.SrcOver))
                    {
                        return false;
                    }

                    bounds = drawImage.Dest;
                    return true;
                case DrawImageCanvasCommand:
                    return null;
                default:
                    // Text: the shim cannot measure glyphs.
                    return false;
            }
        }

        // SKPathBoundsHelper takes an arc's extremes at the ellipse's own axes, which miss them once
        // the ellipse is turned.
        private static bool HasTurnedArc(SKPath path)
        {
            foreach (var command in path)
            {
                if (command is ArcToPathCommand { XAxisRotate: not 0 })
                {
                    return true;
                }
            }

            return false;
        }

        // A layer bounds what its paint composites to what was drawn inside it, unless the paint
        // reaches past it: a filter spreads it, a colour filter or a mode such as DstIn changes pixels
        // nothing inside covers.
        private static bool Bounded(SKPaint? paint)
            => paint is null || paint.BlendMode == SKBlendMode.SrcOver && BlendsOnly(paint);

        private static bool Masking(SKPaint? paint)
            => paint is { BlendMode: SKBlendMode.DstIn or SKBlendMode.DstOut or SKBlendMode.SrcIn };

        private static bool BlendsOnly(SKPaint paint)
            => paint.ImageFilter is null && paint.ColorFilter is null && paint.Shader is null;

        private static bool Fades(CanvasCommand command)
            => command is DrawPathCanvasCommand { Path: { }, Paint: { } paint } &&
               paint.BlendMode == SKBlendMode.SrcOver &&
               paint.ImageFilter is null &&
               paint.ColorFilter is null &&
               (paint.Style == SKPaintStyle.Fill || paint.StrokeWidth > 0);

        private static bool IsSourceOver(CanvasCommand command)
            => !SKPictureEditingExtensions.TryGetPaint(command, out var paint) || paint is null || paint.BlendMode == SKBlendMode.SrcOver;
    }

    // A clip's shape in its own space, when it is one whose containment of a draw is exact: a rect,
    // round rect, oval or convex polygon. Each is convex, so a draw's bounds are inside it when their
    // four corners are.
    private sealed class Region
    {
        private readonly int _kind;
        private readonly double _left, _top, _right, _bottom, _rx, _ry, _tolerance;
        private readonly double[]? _xs, _ys;
        private readonly Affine _inverse;

        private Region(int index, int kind, double left, double top, double right, double bottom, double rx, double ry, double[]? xs, double[]? ys, Affine matrix)
        {
            Index = index;
            _kind = kind;
            _left = Math.Min(left, right);
            _top = Math.Min(top, bottom);
            _right = Math.Max(left, right);
            _bottom = Math.Max(top, bottom);
            _rx = rx;
            _ry = ry;
            _xs = xs;
            _ys = ys;

            // Float noise from mapping through one matrix and back, far below a pixel.
            _tolerance = 1e-5 * Math.Max(_right - _left, _bottom - _top);
            _inverse = matrix.Invert();
        }

        public int Index { get; }

        /// <summary>How many frames were open when it was recorded: those after are inside its scope.</summary>
        public int Depth { get; set; }

        public bool CutsNothing { get; set; } = true;

        public static Region Rect(int index, SKRect rect, Affine matrix)
            => new(index, Box, rect.Left, rect.Top, rect.Right, rect.Bottom, 0, 0, null, null, matrix);

        public static Region? From(int index, ClipPath clipPath, Affine matrix)
        {
            // SkiaModel.ToSKPath(ClipPath): each PathClip's path, cut by its own clip and turned by its
            // transform, united, cut by the outer clip, turned by the outer transform.
            if (clipPath.Clips is not { Count: 1 } clips ||
                clips[0] is not { Path: { } path } pathClip ||
                !IsNone(clipPath.Clip) ||
                !IsNone(pathClip.Clip))
            {
                return null;
            }

            matrix = matrix
                .PreConcat(Affine.From(clipPath.Transform ?? SKMatrix.Identity))
                .PreConcat(Affine.From(pathClip.Transform ?? SKMatrix.Identity));

            if (path.Count == 1)
            {
                switch (path[0])
                {
                    case AddRectPathCommand addRect:
                        return Rect(index, addRect.Rect, matrix);
                    case AddOvalPathCommand { Rect: var oval }:
                        return new Region(index, Oval, oval.Left, oval.Top, oval.Right, oval.Bottom, 0, 0, null, null, matrix);
                    case AddCirclePathCommand circle:
                        return new Region(index, Oval, circle.X - circle.Radius, circle.Y - circle.Radius, circle.X + circle.Radius, circle.Y + circle.Radius, 0, 0, null, null, matrix);
                    case AddRoundRectPathCommand { Rect: var rect } roundRect:
                        return RoundRect(index, rect, roundRect.Rx, roundRect.Ry, matrix);
                    case AddPolyPathCommand poly:
                        return Polygon(index, poly, matrix);
                }

                return null;
            }

            var points = new List<SKPoint>(path.Count);
            for (var i = 0; i < path.Count; i++)
            {
                switch (path[i])
                {
                    case MoveToPathCommand moveTo when i == 0:
                        points.Add(new SKPoint(moveTo.X, moveTo.Y));
                        break;
                    case LineToPathCommand lineTo when i > 0:
                        points.Add(new SKPoint(lineTo.X, lineTo.Y));
                        break;
                    case ClosePathCommand when i == path.Count - 1:
                        break;
                    default:
                        return null;
                }
            }

            return Polygon(index, points, matrix);
        }

        public bool Contains(SKRect bounds, Affine matrix)
        {
            var toClip = _inverse.PreConcat(matrix);
            return Contains(toClip, bounds.Left, bounds.Top) &&
                   Contains(toClip, bounds.Right, bounds.Top) &&
                   Contains(toClip, bounds.Right, bounds.Bottom) &&
                   Contains(toClip, bounds.Left, bounds.Bottom);
        }

        private const int Box = 0;
        private const int Oval = 1;
        private const int Rounded = 2;
        private const int Convex = 3;

        private static bool IsNone(ClipPath? clip)
            => clip?.Clips is null || clip.Clips.Count == 0 && IsNone(clip.Clip);

        // Radii as SkRRect::setRectXY takes them: none makes a rect, too large scales both down.
        private static Region RoundRect(int index, SKRect rect, float rx, float ry, Affine matrix)
        {
            double width = Math.Abs(rect.Width), height = Math.Abs(rect.Height);
            if (rx <= 0 || ry <= 0)
            {
                return Rect(index, rect, matrix);
            }

            var scale = Math.Min(1, Math.Min(width / (2.0 * rx), height / (2.0 * ry)));
            return new Region(index, Rounded, rect.Left, rect.Top, rect.Right, rect.Bottom, rx * scale, ry * scale, null, null, matrix);
        }

        private static Region? Polygon(int index, IList<SKPoint> points, Affine matrix)
        {
            var xs = new List<double>(points.Count);
            var ys = new List<double>(points.Count);
            foreach (var point in points)
            {
                if (xs.Count == 0 || point.X != xs[xs.Count - 1] || point.Y != ys[ys.Count - 1])
                {
                    xs.Add(point.X);
                    ys.Add(point.Y);
                }
            }

            while (xs.Count > 1 && xs[0] == xs[xs.Count - 1] && ys[0] == ys[ys.Count - 1])
            {
                xs.RemoveAt(xs.Count - 1);
                ys.RemoveAt(ys.Count - 1);
            }

            if (xs.Count < 3)
            {
                return null;
            }

            // Convex: every turn the same way, and once round in all, which a star's turns are not.
            var n = xs.Count;
            var sign = 0;
            var turned = 0.0;
            for (var i = 0; i < n; i++)
            {
                double ax = xs[(i + 1) % n] - xs[i], ay = ys[(i + 1) % n] - ys[i];
                double bx = xs[(i + 2) % n] - xs[(i + 1) % n], by = ys[(i + 2) % n] - ys[(i + 1) % n];
                var cross = ax * by - ay * bx;
                var turn = Math.Sign(cross);
                if (turn != 0)
                {
                    if (sign != 0 && turn != sign)
                    {
                        return null;
                    }

                    sign = turn;
                }

                turned += Math.Atan2(cross, ax * bx + ay * by);
            }

            if (sign == 0 || Math.Abs(Math.Abs(turned) - 2 * Math.PI) > 1e-6)
            {
                return null;
            }

            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            for (var i = 0; i < n; i++)
            {
                left = Math.Min(left, xs[i]);
                top = Math.Min(top, ys[i]);
                right = Math.Max(right, xs[i]);
                bottom = Math.Max(bottom, ys[i]);
            }

            if (sign < 0)
            {
                xs.Reverse();
                ys.Reverse();
            }

            return new Region(index, Convex, left, top, right, bottom, 0, 0, xs.ToArray(), ys.ToArray(), matrix);
        }

        // Written so that NaN, from a matrix that cannot be inverted, answers false.
        private bool Contains(Affine toClip, double localX, double localY)
        {
            toClip.Map(localX, localY, out var x, out var y);
            var e = _tolerance;
            if (!(x >= _left - e && x <= _right + e && y >= _top - e && y <= _bottom + e))
            {
                return false;
            }

            switch (_kind)
            {
                case Oval:
                    return InEllipse(x, y, (_left + _right) / 2, (_top + _bottom) / 2, (_right - _left) / 2, (_bottom - _top) / 2);
                case Rounded:
                    {
                        var cx = x < _left + _rx ? _left + _rx : x > _right - _rx ? _right - _rx : x;
                        var cy = y < _top + _ry ? _top + _ry : y > _bottom - _ry ? _bottom - _ry : y;
                        return cx == x || cy == y || InEllipse(x, y, cx, cy, _rx, _ry);
                    }
                case Convex:
                    {
                        var xs = _xs!;
                        var ys = _ys!;
                        for (var i = 0; i < xs.Length; i++)
                        {
                            var j = (i + 1) % xs.Length;
                            double ex = xs[j] - xs[i], ey = ys[j] - ys[i];
                            if (!(ex * (y - ys[i]) - ey * (x - xs[i]) >= -e * Math.Sqrt(ex * ex + ey * ey)))
                            {
                                return false;
                            }
                        }

                        return true;
                    }
                default:
                    return true;
            }
        }

        private bool InEllipse(double x, double y, double cx, double cy, double rx, double ry)
        {
            if (!(rx > 0 && ry > 0))
            {
                return false;
            }

            double dx = (x - cx) / (rx + _tolerance), dy = (y - cy) / (ry + _tolerance);
            return dx * dx + dy * dy <= 1;
        }
    }

    // Where a stroke reaches: half the width around its path, and further only at a vertex whose
    // miter tip Skia actually draws, or at a square cap. SkStrokeRec::GetInflationRadius assumes the
    // full miter limit at every vertex, which put circle-chart's polyline, miter limit 10, outside
    // the frame its joins of a few degrees never leave.
    private sealed class StrokeReach
    {
        private readonly float _half;
        private readonly float _miter;
        private readonly bool _mitered;
        private readonly bool _square;
        private SKRect _bounds;

        private SKPoint _start;
        private SKPoint _current;
        private SKPoint? _first;
        private SKPoint? _last;
        private bool _segments;
        private bool _unknownFirst;
        private bool _unknownLast;

        private StrokeReach(SKPath path, SKPaint paint)
        {
            _half = paint.StrokeWidth / 2;
            _miter = paint.StrokeMiter;
            _mitered = paint.StrokeJoin == SKStrokeJoin.Miter;
            _square = paint.StrokeCap == SKStrokeCap.Square;
            var bounds = path.Bounds;
            _bounds = new SKRect(bounds.Left - _half, bounds.Top - _half, bounds.Right + _half, bounds.Bottom + _half);
        }

        public static SKRect Bounds(SKPath path, SKPaint paint)
        {
            var reach = new StrokeReach(path, paint);
            foreach (var command in path)
            {
                reach.Add(command);
            }

            reach.End();
            return reach._bounds;
        }

        private void Add(PathCommand command)
        {
            switch (command)
            {
                case MoveToPathCommand moveTo:
                    End();
                    _start = _current = new SKPoint(moveTo.X, moveTo.Y);
                    break;
                case LineToPathCommand lineTo:
                    Segment(new SKPoint(lineTo.X, lineTo.Y), null, null);
                    break;
                case QuadToPathCommand quad:
                    {
                        var end = new SKPoint(quad.X1, quad.Y1);
                        var control = new SKPoint(quad.X0, quad.Y0);
                        Segment(end, Toward(_current, control, end), Arriving(end, control, _current));
                        break;
                    }
                case CubicToPathCommand cubic:
                    {
                        var c0 = new SKPoint(cubic.X0, cubic.Y0);
                        var c1 = new SKPoint(cubic.X1, cubic.Y1);
                        var end = new SKPoint(cubic.X2, cubic.Y2);
                        Segment(end, Toward(_current, c0, c1, end), Arriving(end, c1, c0, _current));
                        break;
                    }
                case ArcToPathCommand arcTo:
                    Segment(new SKPoint(arcTo.X, arcTo.Y), null, null, unknown: true);
                    break;
                case ClosePathCommand:
                    Close();
                    break;
                case AddRectPathCommand addRect:
                    Polygon(new[] { addRect.Rect.TopLeft, addRect.Rect.TopRight, addRect.Rect.BottomRight, addRect.Rect.BottomLeft }, close: true);
                    break;
                case AddRoundRectPathCommand { Rect: var corners } roundRect when roundRect.Rx <= 0 || roundRect.Ry <= 0:
                    Polygon(new[] { corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft }, close: true);
                    break;
                case AddPolyPathCommand poly:
                    Polygon(poly, poly.Close);
                    break;
                default:
                    // A round rect, oval or circle: one smooth closed contour, inside half the width.
                    End();
                    break;
            }
        }

        private void Polygon(IList<SKPoint> points, bool close)
        {
            End();
            if (points.Count == 0)
            {
                return;
            }

            _start = _current = points[0];
            for (var i = 1; i < points.Count; i++)
            {
                Segment(points[i], null, null);
            }

            if (close)
            {
                Close();
            }

            End();
        }

        // A line passes null tangents and takes its own direction.
        private void Segment(SKPoint end, SKPoint? startTangent, SKPoint? endTangent, bool unknown = false)
        {
            startTangent ??= unknown ? null : Toward(_current, end);
            endTangent ??= unknown ? null : startTangent;
            if (!unknown && startTangent is null)
            {
                // A zero-length line joins nothing; Skia draws it as a cap.
                _current = end;
                return;
            }

            if (_segments)
            {
                Join(_current, _last, _unknownLast, startTangent, unknown);
            }
            else
            {
                _first = startTangent;
                _unknownFirst = unknown;
            }

            _segments = true;
            _last = endTangent;
            _unknownLast = unknown;
            _current = end;
        }

        private void Close()
        {
            if (!_segments)
            {
                return;
            }

            Segment(_start, null, null);
            Join(_start, _last, _unknownLast, _first, _unknownFirst);
            _segments = false;
            _current = _start;
        }

        private void End()
        {
            if (_segments && _square)
            {
                Reach(_start, _half * 1.41421356f);
                Reach(_current, _half * 1.41421356f);
            }

            _segments = false;
        }

        private void Join(SKPoint vertex, SKPoint? incoming, bool unknownIn, SKPoint? outgoing, bool unknownOut)
        {
            if (!_mitered)
            {
                return;
            }

            if (unknownIn || unknownOut || incoming is not { } a || outgoing is not { } b)
            {
                Reach(vertex, _half * Math.Max(1, _miter));
                return;
            }

            // SkMiterJoiner: beyond the limit, where the half angle's sine is under its inverse, a bevel.
            var sinHalf = Math.Sqrt(Math.Max(0, (1 + (a.X * b.X + a.Y * b.Y)) / 2));
            if (sinHalf * _miter >= 1)
            {
                Reach(vertex, (float)(_half / sinHalf));
            }
        }

        private void Reach(SKPoint vertex, float radius)
        {
            _bounds = SKRect.Union(_bounds, new SKRect(vertex.X - radius, vertex.Y - radius, vertex.X + radius, vertex.Y + radius));
        }

        // The unit direction a segment leaves `from` in: towards the first of its later points apart from it.
        private static SKPoint? Toward(SKPoint from, params SKPoint[] later)
        {
            foreach (var point in later)
            {
                if (Unit(point.X - from.X, point.Y - from.Y) is { } direction)
                {
                    return direction;
                }
            }

            return null;
        }

        // The unit direction a segment arrives at `at` in: from the last of its earlier points apart from it.
        private static SKPoint? Arriving(SKPoint at, params SKPoint[] earlier)
        {
            foreach (var point in earlier)
            {
                if (Unit(at.X - point.X, at.Y - point.Y) is { } direction)
                {
                    return direction;
                }
            }

            return null;
        }

        private static SKPoint? Unit(double dx, double dy)
        {
            var length = Math.Sqrt(dx * dx + dy * dy);
            return length > 0 ? new SKPoint((float)(dx / length), (float)(dy / length)) : null;
        }
    }

    // An affine matrix in double, so a clip's inverse composed with a draw's matrix stays exact enough
    // to tell an edge from just past it. A perspective one is NaN, which contains nothing.
    private readonly struct Affine
    {
        public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

        private static readonly Affine s_none = new(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);

        private readonly double _a, _b, _c, _d, _e, _f;

        private Affine(double a, double b, double c, double d, double e, double f)
        {
            _a = a;
            _b = b;
            _c = c;
            _d = d;
            _e = e;
            _f = f;
        }

        public static Affine From(SKMatrix m)
            => m.Persp0 != 0 || m.Persp1 != 0 || m.Persp2 != 1
                ? s_none
                : new Affine(m.ScaleX, m.SkewY, m.SkewX, m.ScaleY, m.TransX, m.TransY);

        // this × other: other applies first, as SKMatrix.PreConcat.
        public Affine PreConcat(Affine o)
            => new(
                _a * o._a + _c * o._b,
                _b * o._a + _d * o._b,
                _a * o._c + _c * o._d,
                _b * o._c + _d * o._d,
                _a * o._e + _c * o._f + _e,
                _b * o._e + _d * o._f + _f);

        public Affine Invert()
        {
            var det = _a * _d - _b * _c;
            if (!(det != 0) || double.IsInfinity(det))
            {
                return s_none;
            }

            return new Affine(
                _d / det,
                -_b / det,
                -_c / det,
                _a / det,
                (_c * _f - _d * _e) / det,
                (_b * _e - _a * _f) / det);
        }

        public void Map(double x, double y, out double mappedX, out double mappedY)
        {
            mappedX = _a * x + _c * y + _e;
            mappedY = _b * x + _d * y + _f;
        }
    }
}
