// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Svg.Highlighting;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// Draws a wavy underline beneath whatever is wrong with the drawing.
/// </summary>
/// <remarks>
/// <para>
/// Painted rather than decorated. <see cref="TextDecoration"/> offers a stroke, a thickness and a
/// dash array, so the closest it comes to an editor's squiggle is a dashed line, which under 12pt
/// type reads as a smudge — the pane drew a solid 2px line for exactly that reason. A background
/// renderer draws the geometry itself, so the mark can be the one people already recognise.
/// </para>
/// <para>
/// A diagnostic is a range into the same text the tokens point at, so nothing has to be translated:
/// the editor turns the range into rectangles and this draws a wave along the bottom of each.
/// </para>
/// </remarks>
internal sealed class SvgViewerSourceMarkers : IBackgroundRenderer
{
    /// <summary>How wide one full up-and-down of the wave is.</summary>
    private const double Period = 4d;

    private const double Amplitude = 2.5d;

    private readonly Func<IBrush?> _brush;

    private readonly Func<IBrush?> _warning;

    private IReadOnlyList<SvgSourceDiagnostic> _diagnostics = Array.Empty<SvgSourceDiagnostic>();

    public SvgViewerSourceMarkers(Func<IBrush?> brush, Func<IBrush?> warning)
    {
        _brush = brush ?? throw new ArgumentNullException(nameof(brush));
        _warning = warning ?? throw new ArgumentNullException(nameof(warning));
    }

    /// <summary>Above the text rather than behind it, so a mark is not lost under a selection.</summary>
    public KnownLayer Layer => KnownLayer.Selection;

    public void Show(IReadOnlyList<SvgSourceDiagnostic> diagnostics) => _diagnostics = diagnostics;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_diagnostics.Count == 0 || textView.Document is null || !textView.VisualLinesValid)
        {
            return;
        }

        // Resolved per draw rather than held, so a theme change is a repaint. A warning is drawn in
        // its own colour because it says something different: the drawing still opened.
        var pens = new Dictionary<SvgSourceSeverity, Pen>();

        foreach (var severity in new[] { SvgSourceSeverity.Error, SvgSourceSeverity.Warning })
        {
            if ((severity == SvgSourceSeverity.Warning ? _warning() : _brush()) is { } found)
            {
                pens[severity] = new Pen(found, 1.2d);
            }
        }

        if (pens.Count == 0)
        {
            return;
        }

        var length = textView.Document.TextLength;

        foreach (var diagnostic in _diagnostics)
        {
            var start = Math.Max(0, Math.Min(diagnostic.Start, length));
            var end = Math.Max(start, Math.Min(diagnostic.Start + diagnostic.Length, length));

            if (end <= start)
            {
                continue;
            }

            if (!pens.TryGetValue(diagnostic.Severity, out var pen))
            {
                continue;
            }

            var segment = new TextSegment { StartOffset = start, EndOffset = end };

            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                drawingContext.DrawGeometry(null, pen, Wave(rect));
            }
        }
    }

    /// <summary>A zig-zag along the bottom of one rectangle.</summary>
    internal static StreamGeometry Wave(Rect rect)
    {
        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            var bottom = rect.Bottom - 0.5d;

            context.BeginFigure(new Point(rect.Left, bottom), false);

            var up = true;

            for (var x = rect.Left; x < rect.Right; x += Period / 2d)
            {
                var to = Math.Min(x + (Period / 2d), rect.Right);

                context.LineTo(new Point(to, up ? bottom - Amplitude : bottom));

                up = !up;
            }

            context.EndFigure(false);
        }

        return geometry;
    }
}
