// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Svg.Skia;
using Svg.SourceEditing;
using Svg.Transforms;
using Shim = ShimSkiaSharp;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>Edits worked out on a drawing as it is drawn, for <see cref="SvgDomEdit.Apply"/> to write.</summary>
/// <remarks>
/// Each reads the drawing a host has built, with its expressions bound, and hands back a command
/// for the parsed copy. What it reads there is the same on both of the edit's runs, so the edit
/// cannot tell it was worked out from an expression's value: a command that would freeze one has
/// to refuse it itself.
/// </remarks>
public static class SvgViewerCommands
{
    /// <summary>Mirrors each element across the middle of all of them, by a term after its transform.</summary>
    /// <param name="bound">The drawing as the host built and drew it.</param>
    /// <param name="elements">Each element's address in the file, and in <paramref name="bound"/>.</param>
    public static Func<SvgDomEditContext, string?> Flip(SKSvg bound, IReadOnlyList<(string File, string Built)> elements, bool horizontal)
        => Turned(bound, elements, about => Affine.Scale(horizontal ? -1d : 1d, horizontal ? 1d : -1d, about));

    /// <summary>Turns each element about the middle of all of them, clockwise, by a term after its transform.</summary>
    /// <inheritdoc cref="Flip"/>
    public static Func<SvgDomEditContext, string?> Rotate(SKSvg bound, IReadOnlyList<(string File, string Built)> elements, float degrees)
        => Turned(bound, elements, about => Affine.Rotation(degrees, about));

    /// <remarks>
    /// The middle is of everything picked, in user units, so a quarter turn stays one however a
    /// viewBox stretches the page. Each element is given the turn in its own space, after what it is
    /// written with — the gizmo's tail — so an expression in its transform is kept and goes on
    /// applying first. Worked out once, before either run.
    /// </remarks>
    private static Func<SvgDomEditContext, string?> Turned(SKSvg bound, IReadOnlyList<(string File, string Built)> elements, Func<(double X, double Y), Affine> turn)
    {
        if (!SvgViewerDraw.TryParent(bound, null, out var root) || !root.TryInvert(out _))
        {
            return Refuse("The drawing has no user units to turn it in.");
        }

        var picked = new List<(string File, Shim.SKMatrix Total, Shim.SKRect Box)>();

        // What is inside another picked element turns with it.
        foreach (var (file, built) in elements.Where(each => !elements.Any(other => Inside(each.File, other.File))))
        {
            if (file.Length == 0)
            {
                return Refuse("The drawing itself has nothing around it to turn in.");
            }

            var nodes = bound.TryGetRetainedSceneNodes(built, out var found) ? found : Array.Empty<SvgSceneNode>();
            var tag = SvgElementAddress.Parse(built)?.Resolve(bound.SourceDocument!) is { } element ? SvgElementNames.NameOf(element) : "element";

            if (nodes.FirstOrDefault(node => !SvgViewerOutline.InMask(node) && SvgViewerOutline.Drawn(node)) is not { } node)
            {
                return Refuse($"The <{tag}> at {file} draws nothing, so it has no middle to turn about.");
            }

            picked.Add((file, node.TotalTransform, node.TransformedBounds));
        }

        var page = Affine.Of(root);
        var box = picked.Select(each => each.Box).Aggregate((sum, next) => new Shim.SKRect(
            Math.Min(sum.Left, next.Left), Math.Min(sum.Top, next.Top), Math.Max(sum.Right, next.Right), Math.Max(sum.Bottom, next.Bottom)));
        var middle = page.Inverse().Map(((box.Left + (double)box.Right) / 2d, (box.Top + (double)box.Bottom) / 2d));
        var drawing = page * turn(middle) * page.Inverse();
        var terms = picked.Select(each =>
        {
            var total = Affine.Of(each.Total);

            // inverse(total) · turn · total: the same move, in the element's own space.
            return (each.File, Terms: Spelt(total.Inverse() * drawing * total, total.Inverse().Map(page.Map(middle))));
        }).ToList();

        return context =>
        {
            foreach (var (file, written) in terms)
            {
                if (context.Element(file) is not { } element)
                {
                    return $"There is no element at {file} in this drawing.";
                }

                var transforms = element.Transforms ?? new SvgTransformCollection();

                transforms.AddRange(written);
                element.Transforms = transforms;
            }

            return null;
        };
    }

    /// <summary>The move as the fewest terms that say it: a quarter turn about a point, an upright mirror, or a matrix.</summary>
    private static IReadOnlyList<SvgTransform> Spelt(Affine map, (double X, double Y) about)
    {
        if (Near(map.B, 0d) && Near(map.C, 0d) && Near(Math.Abs(map.A), 1d) && Near(Math.Abs(map.D), 1d) && map.A * map.D < 0d)
        {
            return map.A < 0d
                ? new SvgTransform[] { new SvgTranslate(Zero(map.E), 0f), new SvgScale(-1f, 1f) }
                : new SvgTransform[] { new SvgTranslate(0f, Zero(map.F)), new SvgScale(1f, -1f) };
        }

        if (Near(map.A, 0d) && Near(map.D, 0d) && Near(Math.Abs(map.B), 1d) && Near(map.B, -map.C))
        {
            return new SvgTransform[] { new SvgRotate(map.B > 0d ? 90f : -90f, Zero(about.X), Zero(about.Y)) };
        }

        return new SvgTransform[] { SvgViewerGizmo.Spelt(map.Shim()) };
    }

    private static bool Inside(string key, string other) => other.Length == 0 ? key.Length > 0 : key.StartsWith(other + "/", StringComparison.Ordinal);

    private static bool Near(double value, double to) => Math.Abs(value - to) < 1e-6d;

    /// <summary>A value as it is written: a float, whose precision sweeps the double's specks, and nought never negative.</summary>
    private static float Zero(double value) => value == 0d ? 0f : (float)value;

    private static Func<SvgDomEditContext, string?> Refuse(string why) => _ => why;

    /// <summary>A plane map in doubles, since a float's inverse leaves 49.999996 where 50 was meant.</summary>
    private readonly record struct Affine(double A, double B, double C, double D, double E, double F)
    {
        public static Affine Of(Shim.SKMatrix map) => new(map.ScaleX, map.SkewY, map.SkewX, map.ScaleY, map.TransX, map.TransY);

        public static Affine Scale(double x, double y, (double X, double Y) about) => new(x, 0d, 0d, y, about.X * (1d - x), about.Y * (1d - y));

        public static Affine Rotation(double degrees, (double X, double Y) about)
        {
            var (sin, cos) = (Math.Sin(degrees * Math.PI / 180d), Math.Cos(degrees * Math.PI / 180d));

            return new(cos, sin, -sin, cos, about.X - (cos * about.X) + (sin * about.Y), about.Y - (sin * about.X) - (cos * about.Y));
        }

        /// <summary>The two maps as one, the right-hand one applying first, as SkiaSharp's do.</summary>
        public static Affine operator *(Affine x, Affine y)
            => new(
                (x.A * y.A) + (x.C * y.B),
                (x.B * y.A) + (x.D * y.B),
                (x.A * y.C) + (x.C * y.D),
                (x.B * y.C) + (x.D * y.D),
                (x.A * y.E) + (x.C * y.F) + x.E,
                (x.B * y.E) + (x.D * y.F) + x.F);

        public Affine Inverse()
        {
            var det = (A * D) - (B * C);

            return new(D / det, -B / det, -C / det, A / det, ((C * F) - (D * E)) / det, ((B * E) - (A * F)) / det);
        }

        public (double X, double Y) Map((double X, double Y) point) => ((A * point.X) + (C * point.Y) + E, (B * point.X) + (D * point.Y) + F);

        public Shim.SKMatrix Shim() => new()
        {
            ScaleX = (float)A,
            SkewY = (float)B,
            SkewX = (float)C,
            ScaleY = (float)D,
            TransX = (float)E,
            TransY = (float)F,
            Persp2 = 1f
        };
    }
}
