// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Svg.Editor.Skia;
using Svg.Skia;
using Shim = ShimSkiaSharp;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// Rulers along the top and the left of a canvas, counting a drawing's own user units, and the
/// guides pressed out of them.
/// </summary>
/// <remarks>
/// Drawn by Avalonia rather than on the canvas, so following the pointer repaints a strip of ticks
/// and never the drawing.
/// </remarks>
public class SvgViewerRulers : Decorator
{
    /// <summary>How deep each ruler is.</summary>
    public const double Breadth = 18d;

    private static readonly IBrush s_band = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80));

    private readonly SvgRulerTickCache _across = new();
    private readonly SvgRulerTickCache _down = new();
    private SvgViewerCanvas? _canvas;
    private bool _shows;
    private Point? _pointer;

    static SvgViewerRulers()
    {
        AffectsRender<SvgViewerRulers>(TextElement.ForegroundProperty);
    }

    /// <summary>Whether the rulers take their room; off, the canvas has all of it.</summary>
    public bool ShowsRulers
    {
        get => _shows;
        set
        {
            if (_shows == value)
            {
                return;
            }

            _shows = value;
            Padding = value ? new Thickness(Breadth, Breadth, 0d, 0d) : default;

            InvalidateVisual();
        }
    }

    /// <summary>
    /// The drawing whose units are counted, or null for the arrangement's own. Unset, a canvas
    /// showing one drawing counts that one.
    /// </summary>
    public Func<SvgViewerPlacement?>? Ruled { get; set; }

    /// <summary>From the counted drawing's user units to the space the canvas arranges drawings in.</summary>
    /// <remarks>The root's matrix, which is where the viewBox is applied, moved to where the drawing is placed.</remarks>
    public Shim.SKMatrix Units
    {
        get
        {
            var placed = Ruled is { } ruled ? ruled() : _canvas?.Placements is { Count: 1 } one ? one[0] : null;

            if (placed is null)
            {
                return Shim.SKMatrix.CreateIdentity();
            }

            SvgViewerDraw.TryParent(placed.Svg, null, out var root);

            return Shim.SKMatrix.CreateTranslation(placed.At.X, placed.At.Y).PreConcat(root);
        }
    }

    /// <summary>The ticks along the top ruler, or with <paramref name="across"/> false the left one, in canvas pixels.</summary>
    public IReadOnlyList<SvgRulerTick> Ticks(bool across)
    {
        if (_canvas is not { } canvas)
        {
            return Array.Empty<SvgRulerTick>();
        }

        var units = Units;

        return across
            ? _across.GetTicks(canvas.OffsetX + units.TransX * canvas.Scale, units.ScaleX * canvas.Scale, canvas.Bounds.Width)
            : _down.GetTicks(canvas.OffsetY + units.TransY * canvas.Scale, units.ScaleY * canvas.Scale, canvas.Bounds.Height);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != ChildProperty)
        {
            return;
        }

        if (_canvas is { } was)
        {
            was.Published -= OnPublished;
            was.RemoveHandler(PointerMovedEvent, OnPointer);
            was.RemoveHandler(PointerExitedEvent, OnPointer);
        }

        _canvas = Child as SvgViewerCanvas;

        if (_canvas is { } canvas)
        {
            canvas.Published += OnPublished;

            // Tunnelling and handled or not: the canvas takes every move it is dragging.
            canvas.AddHandler(PointerMovedEvent, OnPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
            canvas.AddHandler(PointerExitedEvent, OnPointer, RoutingStrategies.Direct, handledEventsToo: true);
        }
    }

    private void OnPublished(object? sender, EventArgs e)
    {
        if (_shows)
        {
            InvalidateVisual();
        }
    }

    private void OnPointer(object? sender, PointerEventArgs e)
    {
        _pointer = e.RoutedEvent == PointerExitedEvent || _canvas is null ? null : e.GetPosition(_canvas);

        if (_shows)
        {
            InvalidateVisual();
        }
    }

    /// <summary>A press on a ruler hands the canvas a guide to drag: the top one makes a level guide, the left one an upright.</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!_shows || _canvas is not { } canvas || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var at = e.GetPosition(this);
        var top = at.Y < Breadth && at.X >= Breadth;
        var left = at.X < Breadth && at.Y >= Breadth;

        if (top || left)
        {
            canvas.HoldGuide(upright: left, e);
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (!_shows || _canvas is null)
        {
            return;
        }

        var size = Bounds.Size;
        var ink = GetValue(TextElement.ForegroundProperty) ?? Brushes.Gray;
        var pen = new Pen(ink, 1d);

        // Filled, which is also what makes the bands answer the pointer.
        context.FillRectangle(s_band, new Rect(0d, 0d, size.Width, Breadth));
        context.FillRectangle(s_band, new Rect(0d, Breadth, Breadth, Math.Max(0d, size.Height - Breadth)));

        using (context.PushClip(new Rect(Breadth, 0d, Math.Max(0d, size.Width - Breadth), Breadth)))
        {
            foreach (var tick in Ticks(across: true))
            {
                var x = Math.Round(Breadth + tick.Pixel) + 0.5d;

                context.DrawLine(pen, new Point(x, Breadth - (tick.IsMajor ? 8d : 4d)), new Point(x, Breadth));

                if (tick.IsMajor)
                {
                    context.DrawText(Label(tick.Label, ink), new Point(x + 2d, 1d));
                }
            }
        }

        using (context.PushClip(new Rect(0d, Breadth, Breadth, Math.Max(0d, size.Height - Breadth))))
        {
            foreach (var tick in Ticks(across: false))
            {
                var y = Math.Round(Breadth + tick.Pixel) + 0.5d;

                context.DrawLine(pen, new Point(Breadth - (tick.IsMajor ? 8d : 4d), y), new Point(Breadth, y));

                if (tick.IsMajor)
                {
                    // Turned to read upwards, starting just above the tick.
                    using (context.PushTransform(Matrix.CreateRotation(-Math.PI / 2d) * Matrix.CreateTranslation(1d, y - 2d)))
                    {
                        context.DrawText(Label(tick.Label, ink), default);
                    }
                }
            }
        }

        if (_pointer is { } at)
        {
            var marker = new Pen(ink, 1d, dashStyle: DashStyle.Dash);

            context.DrawLine(marker, new Point(Breadth + at.X, 0d), new Point(Breadth + at.X, Breadth));
            context.DrawLine(marker, new Point(0d, Breadth + at.Y), new Point(Breadth, Breadth + at.Y));
        }
    }

    private static FormattedText Label(string text, IBrush ink)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 9d, ink);
}
