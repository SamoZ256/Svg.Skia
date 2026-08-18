// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ShimSkiaSharp;
using Svg.Expressions;

namespace Svg.SceneGraph;

/// <summary>
/// Replaces every expression in a picture with the value it evaluates to, producing a picture that
/// any renderer can draw without knowing the extension exists.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole seam. Rewriting the model rather than teaching a renderer to consult
/// <see cref="SKColor.Expression"/> means <c>SkiaModel</c> and <c>AvaloniaPicture</c> need no changes
/// at all — and it is the only way that works for the Skia path, where <c>SkiaModel</c> is a shared
/// static instance with nowhere to keep one document's parameter values.
/// </para>
/// <para>
/// Nothing is mutated. Paints are shared: two elements with equal values get one cached
/// <see cref="SKPaint"/>, and the symbolic picture has to survive intact for the next set of
/// parameter values. Untouched subtrees come back as the same instances, so a document with no
/// expressions costs one walk and no allocation.
/// </para>
/// </remarks>
public static class SvgSceneExpressionEvaluator
{
    /// <summary>
    /// Evaluates every expression in <paramref name="picture"/> against
    /// <paramref name="declarations"/> and <paramref name="values"/>.
    /// </summary>
    public static SKPicture? Evaluate(
        SKPicture? picture,
        SvgExpressionDeclarations declarations,
        IReadOnlyDictionary<string, ExprValue>? values = null)
        => Evaluate(picture, ExprEvaluator.Create(declarations, values));

    /// <summary>
    /// Evaluates every expression in <paramref name="picture"/> against an evaluator whose values are
    /// already bound.
    /// </summary>
    /// <returns>
    /// A new picture, or <paramref name="picture"/> itself when it holds no expressions.
    /// </returns>
    public static SKPicture? Evaluate(SKPicture? picture, ExprEvaluator evaluator)
    {
        if (evaluator is null)
        {
            throw new ArgumentNullException(nameof(evaluator));
        }

        return picture is null ? null : new Rewriter(evaluator).Rewrite(picture);
    }

    /// <summary>Reference identity, so a shared instance is rewritten once and stays shared.</summary>
    /// <remarks>
    /// Not <c>ReferenceEqualityComparer</c>, which is .NET 5 and up while this targets
    /// netstandard2.0. Most of the model is records, so the default comparer would compare by value
    /// and merge two distinct paints that happen to be equal — which would be harmless, but would
    /// also silently stop the sharing being observable.
    /// </remarks>
    private sealed class IdentityComparer : IEqualityComparer<object>
    {
        public static readonly IdentityComparer Instance = new();

        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);

        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }

    private sealed class Rewriter
    {
        private readonly ExprEvaluator _evaluator;
        private readonly Dictionary<object, object> _rewritten = new(IdentityComparer.Instance);

        public Rewriter(ExprEvaluator evaluator) => _evaluator = evaluator;

        public SKPicture Rewrite(SKPicture picture)
        {
            if (_rewritten.TryGetValue(picture, out var existing))
            {
                return (SKPicture)existing;
            }

            if (picture.Commands is null || picture.Commands.Count == 0)
            {
                return picture;
            }

            var target = new List<CanvasCommand>(picture.Commands.Count);
            var changed = RewriteRange(picture.Commands, 0, picture.Commands.Count, target, suppressed: false);

            var result = changed ? new SKPicture(picture.CullRect, target) : picture;
            _rewritten[picture] = result;

            return result;
        }

        /// <summary>
        /// Copies <c>[start, end)</c> into <paramref name="target"/>, resolving conditionals and
        /// rewriting paints. Returns whether anything differs from the source.
        /// </summary>
        private bool RewriteRange(
            IList<CanvasCommand> source,
            int start,
            int end,
            List<CanvasCommand> target,
            bool suppressed)
        {
            var changed = false;
            var index = start;

            while (index < end)
            {
                var command = source[index];

                if (command is BeginConditionalCanvasCommand begin)
                {
                    var close = FindMatchingEnd(source, index, end);

                    // Inside a range that is already dropped, the condition is not evaluated at all.
                    // Generated code nests the ifs, so at runtime an inner condition behind a false
                    // outer one never runs either — and evaluating it here could raise an error for
                    // code that will not execute.
                    var keep = suppressed || Condition(begin);

                    RewriteRange(source, index + 1, close, target, suppressed || !keep);

                    // The markers themselves never survive: a resolved conditional is not a
                    // conditional any more, and leaving them would make the picture look unevaluated.
                    changed = true;
                    index = close < end ? close + 1 : end;
                    continue;
                }

                if (command is EndConditionalCanvasCommand)
                {
                    // Only reachable from an End with no Begin, which the recorder cannot produce.
                    // Dropping it is still better than handing a renderer a marker it will ignore.
                    changed = true;
                    index++;
                    continue;
                }

                if (suppressed)
                {
                    if (IsCanvasState(command))
                    {
                        // Kept, and kept exactly as it is.
                        //
                        // Generated code drops a false range wholesale, which it can afford because
                        // it assigns SetMatrix(TotalMatrix) and whatever comes next restates its own
                        // absolute matrix. The runtime renderer applies Concat(DeltaMatrix), where a
                        // dropped delta would leave every later command transformed by the wrong
                        // matrix.
                        //
                        // In practice that difference does not arise: SvgSceneRenderer opens the
                        // range around everything a node contributes, so a matrix or clip inside it
                        // is always inside a Save that the range's own Restore pops, and deleting
                        // the range would be equally correct. This does not rely on that. The
                        // recorder guarantees the balance, not the model, and a picture that arrives
                        // any other way — hand-built, or from a recorder that changes — still has to
                        // render the same. ConditionalRangeTests pins the difference on a picture
                        // built by hand, because no document produces one.
                        //
                        // None of these draws, so leaving their paints unrewritten is safe and
                        // avoids evaluating expressions belonging to a range that is not drawn.
                        target.Add(command);
                    }

                    changed = true;
                    index++;
                    continue;
                }

                var rewritten = RewriteCommand(command);

                if (!ReferenceEquals(rewritten, command))
                {
                    changed = true;
                }

                target.Add(rewritten);
                index++;
            }

            return changed;
        }

        /// <summary>
        /// The index of the <c>End</c> closing the <c>Begin</c> at <paramref name="from"/>, or
        /// <paramref name="end"/> when the range is unterminated.
        /// </summary>
        private static int FindMatchingEnd(IList<CanvasCommand> source, int from, int end)
        {
            var depth = 0;

            for (var index = from; index < end; index++)
            {
                switch (source[index])
                {
                    case BeginConditionalCanvasCommand:
                        depth++;
                        break;
                    case EndConditionalCanvasCommand:
                        depth--;
                        if (depth == 0)
                        {
                            return index;
                        }

                        break;
                }
            }

            return end;
        }

        // Commands that change canvas state without drawing anything.
        private static bool IsCanvasState(CanvasCommand command)
            => command is SaveCanvasCommand
                or RestoreCanvasCommand
                or SaveLayerCanvasCommand
                or SetMatrixCanvasCommand
                or ClipPathCanvasCommand
                or ClipRectCanvasCommand;

        private bool Condition(BeginConditionalCanvasCommand begin)
            => SvgSceneSymEvaluator.Evaluate(begin.Condition, ExprType.Boolean, _evaluator).AsBoolean;

        private CanvasCommand RewriteCommand(CanvasCommand command)
        {
            switch (command)
            {
                case DrawPathCanvasCommand draw:
                    {
                        var paint = RewritePaint(draw.Paint);

                        return ReferenceEquals(paint, draw.Paint)
                            ? command
                            : Carry(command, draw with { Paint = paint });
                    }

                case SaveLayerCanvasCommand save:
                    {
                        var paint = RewritePaint(save.Paint);

                        return ReferenceEquals(paint, save.Paint)
                            ? command
                            : Carry(command, save with { Paint = paint });
                    }

                case DrawImageCanvasCommand draw:
                    {
                        var paint = RewritePaint(draw.Paint);

                        return ReferenceEquals(paint, draw.Paint)
                            ? command
                            : Carry(command, draw with { Paint = paint });
                    }

                case DrawTextCanvasCommand draw:
                    {
                        var paint = RewritePaint(draw.Paint);

                        return ReferenceEquals(paint, draw.Paint)
                            ? command
                            : Carry(command, draw with { Paint = paint });
                    }

                case DrawTextBlobCanvasCommand draw:
                    {
                        var paint = RewritePaint(draw.Paint);

                        return ReferenceEquals(paint, draw.Paint)
                            ? command
                            : Carry(command, draw with { Paint = paint });
                    }

                case DrawPositionedTextRunCanvasCommand draw:
                    {
                        var paint = RewritePaint(draw.Paint);

                        return ReferenceEquals(paint, draw.Paint)
                            ? command
                            : Carry(command, draw with { Paint = paint });
                    }

                case DrawTextOnPathCanvasCommand draw:
                    {
                        var paint = RewritePaint(draw.Paint);

                        return ReferenceEquals(paint, draw.Paint)
                            ? command
                            : Carry(command, draw with { Paint = paint });
                    }

                case DrawPictureCanvasCommand draw when draw.Picture is { } nested:
                    {
                        var picture = Rewrite(nested);

                        return ReferenceEquals(picture, nested)
                            ? command
                            : Carry(command, draw with { Picture = picture });
                    }

                default:
                    return command;
            }
        }

        // `with` copies the record's own members, not the source-element metadata on the base, and
        // that metadata is what hit testing and the editor look up an element by.
        private static CanvasCommand Carry(CanvasCommand original, CanvasCommand clone)
        {
            clone.SourceElementId = original.SourceElementId;
            clone.SourceElementAddress = original.SourceElementAddress;
            clone.SourceElementTypeName = original.SourceElementTypeName;

            return clone;
        }

        private SKPaint? RewritePaint(SKPaint? paint)
        {
            if (paint is null)
            {
                return null;
            }

            if (_rewritten.TryGetValue(paint, out var existing))
            {
                return (SKPaint)existing;
            }

            var color = RewriteColor(paint.Color);
            var shader = RewriteShader(paint.Shader);
            var colorFilter = RewriteColorFilter(paint.ColorFilter);

            if (color.Equals(paint.Color)
                && ReferenceEquals(shader, paint.Shader)
                && ReferenceEquals(colorFilter, paint.ColorFilter))
            {
                return paint;
            }

            // Cloned rather than built field by field, so a member added to SKPaint later cannot be
            // silently dropped here. The clone deep-copies a shader that is then replaced, which is
            // wasted work on a path that only runs for paints carrying an expression.
            var clone = paint.Clone();
            clone.Color = color;
            clone.Shader = shader;
            clone.ColorFilter = colorFilter;

            _rewritten[paint] = clone;

            return clone;
        }

        private SKColor? RewriteColor(SKColor? color)
            => color is { Expression: { } expression }
                ? ToColor(SvgSceneSymEvaluator.Evaluate(expression, ExprType.Color, _evaluator))
                : color;

        private SKShader? RewriteShader(SKShader? shader)
        {
            switch (shader)
            {
                case null:
                    return null;

                case ColorShader colorShader when colorShader.Color.Expression is { } expression:
                    return colorShader with
                    {
                        Color = ToColor(SvgSceneSymEvaluator.Evaluate(expression, ExprType.Color, _evaluator))
                    };

                case LinearGradientShader gradient when TryRewriteStops(gradient.Colors, out var colors):
                    return gradient with { Colors = colors };

                case RadialGradientShader gradient when TryRewriteStops(gradient.Colors, out var colors):
                    return gradient with { Colors = colors };

                case TwoPointConicalGradientShader gradient when TryRewriteStops(gradient.Colors, out var colors):
                    return gradient with { Colors = colors };

                // A pattern paints through a nested picture, which can hold expressions of its own.
                case PictureShader pictureShader when pictureShader.Src is { } src:
                    {
                        var rewrittenSrc = Rewrite(src);

                        return ReferenceEquals(rewrittenSrc, src)
                            ? shader
                            : pictureShader with { Src = rewrittenSrc };
                    }

                default:
                    return shader;
            }
        }

        private SKColorFilter? RewriteColorFilter(SKColorFilter? filter)
            => filter is BlendModeColorFilter blendMode && blendMode.Color.Expression is { } expression
                ? blendMode with
                {
                    Color = ToColor(SvgSceneSymEvaluator.Evaluate(expression, ExprType.Color, _evaluator))
                }
                : filter;

        /// <summary>
        /// Rewrites the stops of a gradient, reporting false when none of them carries an expression
        /// so the shader can be left alone.
        /// </summary>
        private bool TryRewriteStops(SKColorF[]? stops, out SKColorF[]? rewritten)
        {
            rewritten = null;

            if (stops is null)
            {
                return false;
            }

            for (var index = 0; index < stops.Length; index++)
            {
                if (stops[index].Expression is not { } expression)
                {
                    continue;
                }

                rewritten ??= (SKColorF[])stops.Clone();
                rewritten[index] = ToColor(SvgSceneSymEvaluator.Evaluate(expression, ExprType.Color, _evaluator));
            }

            return rewritten is { };
        }

        // The expression is gone from the result on purpose: it has been resolved, and a renderer
        // that still saw one would have no reason to trust the channels beside it.
        private static SKColor ToColor(ExprValue value)
            => new(value.Red, value.Green, value.Blue, value.Alpha);
    }
}
