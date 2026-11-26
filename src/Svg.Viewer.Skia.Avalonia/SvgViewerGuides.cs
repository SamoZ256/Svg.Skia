// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using SkiaSharp;
using Svg.Editor.Skia;
using Svg.Skia;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// The lines a gesture lands on besides the grid's: the canvas's ruler guides, and, while something is
/// dragged and smart guides are on, the edges and centres of everything else on show.
/// </summary>
/// <remarks>
/// Handed over inside <see cref="SvgViewerGrid"/>, which every gesture already pulls through.
/// Upstream's <see cref="SvgEditorGuideCache"/> gathers the same lines by the same rules, but off its
/// own editor controller, which nothing here has.
/// </remarks>
public sealed class SvgViewerGuides
{
    private readonly SelectionService _selection = new();
    private readonly List<float> _xs = new();
    private readonly List<float> _ys = new();
    private SKRect? _box;
    private SKPoint _corner;
    private int _version;
    private (IReadOnlyList<SvgEditorGuide> Guides, bool Shown, int Version, float X, float Y)? _built;
    private SvgEditorGuideIndex? _uprights;
    private SvgEditorGuideIndex? _levels;

    /// <summary>Whether a drag lands on the other objects' edges and centres, as well as on the ruler guides.</summary>
    public bool IsOn { get; set; }

    /// <summary>Gathers what a gesture starting now may land on.</summary>
    /// <param name="moving">What the gesture moves, which it must not land on where it started.</param>
    /// <param name="box">
    /// The selection's box when the gesture carries it whole, which the gizmo pulls by its top-left
    /// corner: each line is then offered at that corner's distance from the box's left edge, centre
    /// and right edge, so whichever of them comes near lands.
    /// </param>
    /// <param name="page">The drawing whose own edges are being dragged, whose page is no line.</param>
    public void Begin(IReadOnlyList<SvgViewerPlacement> placed, IReadOnlyCollection<SvgElement> moving, BoundsInfo? box, SvgViewerPlacement? page)
    {
        End();

        if (box is { } carried)
        {
            _box = SelectionService.GetBoundsRect(carried);
            _corner = carried.TL;
        }

        if (!IsOn)
        {
            return;
        }

        var held = new HashSet<SvgElement>(moving);

        foreach (var placement in placed)
        {
            if (!ReferenceEquals(placement, page) && SvgViewerCanvas.Extent(placement) is { } sheet)
            {
                sheet.Offset(placement.At);
                Add(sheet);
            }

            if (!placement.Svg.TryEnsureRetainedSceneGraph(out var scene) || scene is null)
            {
                continue;
            }

            foreach (var node in scene.Traverse())
            {
                if (_xs.Count + _ys.Count >= SvgEditorGuideCache.MaximumSmartGuides)
                {
                    return;
                }

                if (node.Parent is null
                    || node.Element is not SvgVisualElement element
                    || element is SvgGroup
                    || !SvgViewerOutline.Drawn(node)
                    || Defined(element)
                    || Carried(node, held))
                {
                    continue;
                }

                var bounds = SelectionService.GetBoundsRect(_selection.GetBoundsInfo(node, static () => 1f));

                bounds.Offset(placement.At);
                Add(bounds);
            }
        }
    }

    /// <summary>Forgets what the last gesture could land on.</summary>
    public void End()
    {
        _xs.Clear();
        _ys.Clear();
        _box = null;
        _version++;
    }

    /// <summary>The grid with the guide lines added, magnetic at the canvas's zoom.</summary>
    public SvgViewerGrid Over(SvgViewerGrid grid, SvgViewerCanvas canvas)
    {
        var key = (canvas.Guides, canvas.ShowsGuides, _version, grid.OriginX, grid.OriginY);

        if (_built != key)
        {
            var xs = new List<float>(_xs);
            var ys = new List<float>(_ys);

            if (canvas.ShowsGuides)
            {
                foreach (var guide in canvas.Guides)
                {
                    if (Upright(guide))
                    {
                        xs.Add(guide.Origin.X);
                    }
                    else
                    {
                        ys.Add(guide.Origin.Y);
                    }
                }
            }

            // Relative to the grid's origin, which is how From keeps them where they are on a board.
            var (dx, dy) = _box is { } box ? (_corner.X - box.Left, _corner.Y - box.Top) : (0f, 0f);

            _uprights = xs.Count > 0 ? SvgViewerGrid.Lines(Offered(xs, dx - grid.OriginX, _box?.Width), upright: true) : null;
            _levels = ys.Count > 0 ? SvgViewerGrid.Lines(Offered(ys, dy - grid.OriginY, _box?.Height), upright: false) : null;
            _built = key;
        }

        return _uprights is null && _levels is null || canvas.Scale <= 0d
            ? grid
            : grid with { Uprights = _uprights, Levels = _levels, Reach = (float)(SvgViewerGrid.PullPixels / canvas.Scale) };
    }

    /// <summary>The object edges and centres the box now sits on, to draw while the drag lasts.</summary>
    public IReadOnlyList<SvgEditorGuide>? Aligned(BoundsInfo? box, double scale)
    {
        if (box is not { } at || scale <= 0d || _xs.Count + _ys.Count == 0)
        {
            return null;
        }

        var bounds = SelectionService.GetBoundsRect(at);
        var near = (float)(0.5d / scale);
        var found = new List<SvgEditorGuide>();

        foreach (var x in On(_xs, bounds.Left, bounds.MidX, bounds.Right, near))
        {
            found.Add(new SvgEditorGuide(string.Empty, new SKPoint(x, 0f), new SKPoint(0f, 1f)));
        }

        foreach (var y in On(_ys, bounds.Top, bounds.MidY, bounds.Bottom, near))
        {
            found.Add(new SvgEditorGuide(string.Empty, new SKPoint(0f, y), new SKPoint(1f, 0f)));
        }

        return found.Count > 0 ? found : null;
    }

    /// <summary>Whether a guide runs up and down, pulling x.</summary>
    public static bool Upright(SvgEditorGuide guide) => guide.Direction.X == 0f;

    private void Add(SKRect bounds)
    {
        if (!Finite(bounds.Left) || !Finite(bounds.Top) || !Finite(bounds.Right) || !Finite(bounds.Bottom))
        {
            return;
        }

        _xs.Add(bounds.Left);
        _xs.Add(bounds.MidX);
        _xs.Add(bounds.Right);
        _ys.Add(bounds.Top);
        _ys.Add(bounds.MidY);
        _ys.Add(bounds.Bottom);
    }

    /// <summary>Each line moved by <paramref name="by"/>, and for a carried box also at its centre's and far edge's distance.</summary>
    private static IEnumerable<float> Offered(List<float> lines, float by, float? across)
    {
        foreach (var line in lines)
        {
            yield return line + by;

            if (across is { } span)
            {
                yield return line - span / 2f + by;
                yield return line - span + by;
            }
        }
    }

    private static IEnumerable<float> On(List<float> lines, float low, float middle, float high, float near)
    {
        var seen = new HashSet<float>();

        foreach (var line in lines)
        {
            if ((Math.Abs(line - low) <= near || Math.Abs(line - middle) <= near || Math.Abs(line - high) <= near) && seen.Add(line))
            {
                yield return line;
            }
        }
    }

    /// <summary>Whether this is held in a definition, whose mask and pattern content has visible scene nodes of its own.</summary>
    /// <remarks>Upstream's list. Hidden elements are the scene's to say, which knows a child may be visible inside a hidden group.</remarks>
    private static bool Defined(SvgElement element)
    {
        for (var item = element; item is not null; item = item.Parent)
        {
            if (item is SvgDefinitionList or SvgSymbol or SvgClipPath or SvgMask or SvgPatternServer or SvgMarker)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the gesture carries this node, by the scene rather than the file: a &lt;use&gt;'s copy is under the use, not under what it copies.</summary>
    private static bool Carried(SvgSceneNode node, HashSet<SvgElement> moving)
    {
        for (var item = node; item is not null; item = item.Parent)
        {
            if (item.Element is { } element && moving.Contains(element))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
