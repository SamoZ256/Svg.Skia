// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using ShimSkiaSharp;
using ShimSkiaSharp.Editing;
using Svg.Expressions;

namespace Svg.CodeGen.Skia;

/// <summary>
/// The drawings of one file, so a range marked as drawing one of them can be emitted as a call to
/// its class instead of as the copy it holds.
/// </summary>
/// <remarks>
/// The copy stays the authority: it is what every renderer draws, and a call is taken only where it
/// provably draws the same commands. A target that cannot be shown to — a stale copy, a resized
/// target, a layer the target's own shortcuts would ignore — leaves the copy where it is.
/// </remarks>
internal sealed class SkiaCSharpCalls
{
    private readonly Dictionary<string, List<SkiaCSharpDrawing>> _byClass = new(StringComparer.Ordinal);
    private readonly Dictionary<SkiaCSharpDrawing, Callee> _callees = new();
    private readonly bool _recorded;

    /// <param name="recorded">
    /// Whether a drawing draws by playing back a picture, which Skia drops whole where its cull rect
    /// misses the clip: only a use clipped to that rect cannot see anything it drops.
    /// </param>
    public SkiaCSharpCalls(IEnumerable<SkiaCSharpDrawing> drawings, bool recorded)
    {
        _recorded = recorded;

        foreach (var drawing in drawings)
        {
            if (!_byClass.TryGetValue(drawing.ClassName, out var named))
            {
                _byClass[drawing.ClassName] = named = new List<SkiaCSharpDrawing>();
            }

            named.Add(drawing);
        }
    }

    /// <summary>
    /// The statement calling what the range opened at <paramref name="begin"/> draws, or null where
    /// the copy has to be emitted instead.
    /// </summary>
    public string? TryCall(
        SkiaCSharpDrawing caller,
        IList<CanvasCommand> commands,
        int begin,
        CommandPlan plan,
        SaveLayerTracker layers,
        string canvas)
    {
        var call = (BeginCallCanvasCommand)commands[begin];

        if (End(commands, begin) is not { } end ||
            Resolve(call.Target) is not { } target ||
            Reaches(target, caller, new HashSet<SkiaCSharpDrawing>()) ||
            target.Frozen.Count > 0 ||
            !SameLets(caller.Declarations, target.Declarations))
        {
            return null;
        }

        if (_recorded && !ClippedTo(commands, begin, target.Picture.CullRect))
        {
            return null;
        }

        var callee = CalleeOf(target);

        if (!callee.Callable || (layers.SaveLayerDepth > 0 && callee.LonePrimitive))
        {
            return null;
        }

        // A layer the plan folds into a draw of the copy is a plain save in the caller, its alpha
        // carried by that draw: a call would draw it opaque.
        for (var i = begin + 1; i < end; i++)
        {
            if (plan.FoldedInto(i) is { } folded && folded.Any(layer => layer < begin || layer > end))
            {
                return null;
            }
        }

        var given = new Dictionary<string, SymNode>(StringComparer.Ordinal);

        foreach (var argument in call.Arguments)
        {
            if (given.ContainsKey(argument.Key))
            {
                return null;
            }

            given[argument.Key] = argument.Value;
        }

        if (!Same(commands, begin + 1, end, target.Picture.Commands, Reach(given.Keys, target.Declarations)))
        {
            return null;
        }

        var arguments = new List<string> { canvas };

        try
        {
            foreach (var parameter in target.Declarations.Parameters)
            {
                var value = given.TryGetValue(parameter.Name, out var node) ? node : SymNode.Source(parameter.Name);
                arguments.Add($"{parameter.Name}: {SymCSharpEmitter.Emit(value, parameter.Type)}");
            }
        }
        catch (ExprException)
        {
            // The caller declares no such name, or gives a value of another type: the copy, which
            // was checked against the caller's own declarations, is still right.
            return null;
        }

        return $"global::{target.NamespaceName}.{target.ClassName}.Draw({string.Join(", ", arguments)});";
    }

    /// <summary>
    /// Whether every let the target computes reads the same where the caller declares that name
    /// too: the copy reads the caller's, a call computes the target's.
    /// </summary>
    private static bool SameLets(SvgExpressionDeclarations caller, SvgExpressionDeclarations target)
    {
        foreach (var let in target.Lets)
        {
            if (caller.Parameters.Any(parameter => parameter.Name == let.Name))
            {
                return false;
            }

            if (caller.Lets.FirstOrDefault(mine => mine.Name == let.Name) is { } mine &&
                (mine.Expression != let.Expression || mine.DeclaredType != let.DeclaredType))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The names a value given by the use can change: the parameters given, and every let of the
    /// target built from one of them.
    /// </summary>
    private static HashSet<string> Reach(IEnumerable<string> given, SvgExpressionDeclarations target)
    {
        var reach = new HashSet<string>(given, StringComparer.Ordinal);

        for (var grown = reach.Count > 0; grown;)
        {
            grown = false;

            foreach (var let in target.Lets)
            {
                if (!reach.Contains(let.Name) && Reads(let.Expression, reach))
                {
                    reach.Add(let.Name);
                    grown = true;
                }
            }
        }

        return reach;
    }

    /// <summary>Whether <paramref name="expression"/> names any of <paramref name="names"/>.</summary>
    private static bool Reads(string expression, HashSet<string> names)
    {
        try
        {
            return ExprLexer.Tokenize(expression).Any(token => token.Kind == ExprTokenKind.Identifier && names.Contains(token.Text));
        }
        catch (ExprException)
        {
            return true;
        }
    }

    /// <summary>Whether the use clips what the range draws to <paramref name="cull"/>, or tighter.</summary>
    private static bool ClippedTo(IList<CanvasCommand> commands, int begin, SKRect cull)
    {
        var clip = begin > 0 ? commands[begin - 1] : null;
        var rect = clip switch
        {
            ClipRectCanvasCommand { Operation: SKClipOperation.Intersect } clipRect => clipRect.Rect,
            ClipPathCanvasCommand { Operation: SKClipOperation.Intersect, ClipPath: { Clips: { Count: 1 } clips } path }
                when Plain(path.Transform) && None(path.Clip) && clips[0] is { Path.Commands: { Count: 1 } shape } && Plain(clips[0].Transform) && None(clips[0].Clip) && shape[0] is AddRectPathCommand addRect => addRect.Rect,
            _ => (SKRect?)null
        };

        return rect is { } bounds &&
               bounds.Left >= cull.Left && bounds.Top >= cull.Top && bounds.Right <= cull.Right && bounds.Bottom <= cull.Bottom;
    }

    // The scene gives a clip with nothing nested in it an empty one rather than none.
    private static bool None(ClipPath? clip) => clip is null || (clip.IsEmpty && None(clip.Clip));

    private static bool Plain(SKMatrix? matrix)
        => matrix is not { } m || m is { ScaleX: 1f, SkewX: 0f, TransX: 0f, SkewY: 0f, ScaleY: 1f, TransY: 0f, Persp0: 0f, Persp1: 0f, Persp2: 1f };

    private SkiaCSharpDrawing? Resolve(string target)
        => _byClass.TryGetValue(target, out var named) && named.Count == 1 ? named[0] : null;

    /// <summary>The matching end, where everything between is balanced, or null.</summary>
    private static int? End(IList<CanvasCommand> commands, int begin)
    {
        var calls = 0;
        var saves = 0;
        var conditions = 0;

        for (var i = begin + 1; i < commands.Count; i++)
        {
            switch (commands[i])
            {
                case BeginCallCanvasCommand:
                    calls++;
                    break;
                case EndCallCanvasCommand when calls == 0:
                    return saves == 0 && conditions == 0 ? i : null;
                case EndCallCanvasCommand:
                    calls--;
                    break;
                case SaveCanvasCommand or SaveLayerCanvasCommand:
                    saves++;
                    break;
                case RestoreCanvasCommand when --saves < 0:
                    return null;
                case BeginConditionalCanvasCommand:
                    conditions++;
                    break;
                case EndConditionalCanvasCommand when --conditions < 0:
                    return null;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="from"/>, or anything it calls, calls <paramref name="to"/>.</summary>
    private bool Reaches(SkiaCSharpDrawing from, SkiaCSharpDrawing to, HashSet<SkiaCSharpDrawing> seen)
    {
        if (ReferenceEquals(from, to))
        {
            return true;
        }

        if (!seen.Add(from))
        {
            return false;
        }

        foreach (var target in CalleeOf(from).Calls)
        {
            if (Resolve(target) is { } next && Reaches(next, to, seen))
            {
                return true;
            }
        }

        return false;
    }

    private Callee CalleeOf(SkiaCSharpDrawing drawing)
    {
        if (!_callees.TryGetValue(drawing, out var callee))
        {
            _callees[drawing] = callee = new Callee();
            callee.Walk(drawing.Picture, new SaveLayerTracker());
        }

        return callee;
    }

    /// <summary>What decides whether a drawing can be called in place of its copy.</summary>
    private sealed class Callee
    {
        /// <summary>
        /// False where a call would draw something else: text is laid out at compile time from the
        /// values substituted into each document, a non-scaling stroke is measured against the
        /// canvas it was drawn on, and so is an image filter's region.
        /// </summary>
        public bool Callable { get; private set; } = true;

        /// <summary>
        /// Whether it draws a fill the renderer would draw as a primitive with no layer open, which
        /// under a layer the copy draws as a path.
        /// </summary>
        public bool LonePrimitive { get; private set; }

        public List<string> Calls { get; } = new();

        public void Walk(SKPicture picture, SaveLayerTracker layers)
        {
            foreach (var command in picture.Commands ?? Enumerable.Empty<CanvasCommand>())
            {
                switch (command)
                {
                    case DrawTextCanvasCommand or DrawTextBlobCanvasCommand or DrawPositionedTextRunCanvasCommand or DrawTextOnPathCanvasCommand:
                        Callable = false;
                        break;
                    case SaveCanvasCommand:
                        layers.Save(isLayer: false);
                        break;
                    case SaveLayerCanvasCommand saveLayer:
                        layers.Save(isLayer: true);
                        Callable &= saveLayer.Paint?.ImageFilter is null;
                        break;
                    case RestoreCanvasCommand:
                        layers.Restore();
                        break;
                    case DrawPathCanvasCommand draw:
                        LonePrimitive |= layers.Primitive(draw) is { };
                        Callable &= draw.Paint is not { ImageFilter: { } } and not { IsStrokeNonScaling: true, Style: SKPaintStyle.Stroke };
                        break;
                    case DrawImageCanvasCommand { Paint: { ImageFilter: { } } }:
                        Callable = false;
                        break;
                    case DrawPictureCanvasCommand { Picture: { } nested }:
                        Walk(nested, layers);
                        break;
                    case BeginCallCanvasCommand call:
                        Calls.Add(call.Target);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="range"/> from <paramref name="start"/> to <paramref name="end"/> holds
    /// the commands of <paramref name="target"/>, wherever each is placed and whatever value each
    /// expression takes.
    /// </summary>
    /// <param name="reach">
    /// What the use gives, and what that changes: an expression of the target naming none of it
    /// has to read in the copy as it does in the target, and one naming any has the value
    /// substituted in, so it is held only to being there.
    /// </param>
    private static bool Same(IList<CanvasCommand> range, int start, int end, IList<CanvasCommand>? target, HashSet<string> reach)
    {
        if (target is null || target.Count != end - start)
        {
            return false;
        }

        var comparison = new Comparison(reach);

        for (var i = 0; i < target.Count; i++)
        {
            if (!comparison.Equal(range[start + i], target[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A walk over the model's own types, comparing what draws and ignoring where it was placed.</summary>
    private sealed class Comparison
    {
        private static readonly Assembly s_model = typeof(CanvasCommand).Assembly;
        private static readonly Dictionary<Type, PropertyInfo[]> s_properties = new();

        // The totals and the save depths are the copy's own place in the caller, and the metadata
        // where it was written.
        private static readonly HashSet<(Type, string)> s_placement = new()
        {
            (typeof(CanvasCommand), nameof(CanvasCommand.SourceElementId)),
            (typeof(CanvasCommand), nameof(CanvasCommand.SourceElementAddress)),
            (typeof(CanvasCommand), nameof(CanvasCommand.SourceElementTypeName)),
            (typeof(SetMatrixCanvasCommand), nameof(SetMatrixCanvasCommand.TotalMatrix)),
            (typeof(SetMatrixCanvasCommand), nameof(SetMatrixCanvasCommand.SymbolicTotal)),
            (typeof(SaveCanvasCommand), nameof(SaveCanvasCommand.Count)),
            (typeof(SaveLayerCanvasCommand), nameof(SaveLayerCanvasCommand.Count)),
            (typeof(RestoreCanvasCommand), nameof(RestoreCanvasCommand.Count))
        };

        private readonly HashSet<string> _reach;
        private readonly HashSet<(object, object)> _open = new(new PairComparer());
        private int _loose;

        public Comparison(HashSet<string> reach) => _reach = reach;

        public bool Equal(object? a, object? b)
        {
            if (a is null || b is null)
            {
                return a is null && b is null;
            }

            var type = a.GetType();

            if (type != b.GetType())
            {
                return false;
            }

            if (type.IsEnum)
            {
                return a.Equals(b);
            }

            // Judged on the target's side, which names what the use gives where the copy has it
            // substituted.
            var loosen = _loose == 0 && a is SymNode or SymMatrix && Sources(b).Any(text => Reads(text, _reach));
            _loose += loosen ? 1 : 0;

            try
            {
                return a switch
                {
                    SymSource source => _loose > 0 || source.Text == ((SymSource)b).Text,
                    BeginCallCanvasCommand call => Calls(call, (BeginCallCanvasCommand)b),
                    _ => Walk(a, b, type)
                };
            }
            finally
            {
                _loose -= loosen ? 1 : 0;
            }
        }

        /// <summary>
        /// A call the copy makes against the one the target makes: the copy's also gives what the
        /// target passes along by name where the use gives that name a value.
        /// </summary>
        private bool Calls(BeginCallCanvasCommand copy, BeginCallCanvasCommand target)
        {
            if (copy.Target != target.Target)
            {
                return false;
            }

            var given = new Dictionary<string, SymNode>(StringComparer.Ordinal);

            foreach (var argument in copy.Arguments)
            {
                if (given.ContainsKey(argument.Key))
                {
                    return false;
                }

                given[argument.Key] = argument.Value;
            }

            foreach (var argument in target.Arguments)
            {
                if (!given.TryGetValue(argument.Key, out var value) || !Equal(value, argument.Value))
                {
                    return false;
                }

                given.Remove(argument.Key);
            }

            return given.Keys.All(_reach.Contains);
        }

        private bool Walk(object a, object b, Type type)
        {
            // A call's arguments, whose values are expressions like any other.
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                var key = type.GetProperty(nameof(KeyValuePair<object, object>.Key))!;
                var value = type.GetProperty(nameof(KeyValuePair<object, object>.Value))!;

                return Equal(key.GetValue(a), key.GetValue(b)) && Equal(value.GetValue(a), value.GetValue(b));
            }

            if (type.Assembly != s_model)
            {
                return a is IEnumerable sequence and not string
                    ? Sequence(sequence, (IEnumerable)b)
                    : a.Equals(b);
            }

            // A path lists itself among its own properties.
            if (!_open.Add((a, b)))
            {
                return true;
            }

            try
            {
                if (a is IEnumerable items && !Sequence(items, (IEnumerable)b))
                {
                    return false;
                }

                foreach (var property in Properties(type))
                {
                    if (!Equal(property.GetValue(a), property.GetValue(b)))
                    {
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                _open.Remove((a, b));
            }
        }

        /// <summary>The expression text anywhere in a symbolic value.</summary>
        private static IEnumerable<string> Sources(object node)
            => node switch
            {
                SymSource source => new[] { source.Text },
                SymUnary unary => Sources(unary.Operand),
                SymBinary binary => Sources(binary.Left).Concat(Sources(binary.Right)),
                SymMatrix matrix => matrix.Transforms.SelectMany(transform => transform.Arguments).SelectMany(Sources),
                _ => Enumerable.Empty<string>()
            };

        private bool Sequence(IEnumerable a, IEnumerable b)
        {
            var left = a.GetEnumerator();
            var right = b.GetEnumerator();

            while (true)
            {
                var more = left.MoveNext();

                if (more != right.MoveNext())
                {
                    return false;
                }

                if (!more)
                {
                    return true;
                }

                if (!Equal(left.Current, right.Current))
                {
                    return false;
                }
            }
        }

        private static PropertyInfo[] Properties(Type type)
        {
            lock (s_properties)
            {
                if (!s_properties.TryGetValue(type, out var properties))
                {
                    s_properties[type] = properties = type
                        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(property => property.CanRead && property.GetIndexParameters().Length == 0 && !s_placement.Contains((property.DeclaringType!, property.Name)))
                        .ToArray();
                }

                return properties;
            }
        }

        private sealed class PairComparer : IEqualityComparer<(object, object)>
        {
            public bool Equals((object, object) x, (object, object) y)
                => ReferenceEquals(x.Item1, y.Item1) && ReferenceEquals(x.Item2, y.Item2);

            public int GetHashCode((object, object) pair)
                => (RuntimeHelpers.GetHashCode(pair.Item1) * 397) ^ RuntimeHelpers.GetHashCode(pair.Item2);
        }
    }
}
