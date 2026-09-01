// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Svg.Highlighting;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// Colours the source pane from <see cref="SvgSourceHighlighter"/>'s tokens.
/// </summary>
/// <remarks>
/// The editor is told what colour a range is rather than handed a grammar: no stock XML grammar
/// knows <c>{{ hsl(…) }}</c> is code. Only the lines on screen reach here — 340 lines of a 132KB
/// drawing colour in 18ms because 30 are asked about.
///
/// Public because nothing about it is the viewer's: it colours whatever
/// <see cref="SvgSourceHighlighter"/> can split, which is any file written in the extension's own
/// namespace. Svg.Studio paints an svgc recipe with it, and a second copy of this would be a second
/// place for the token kinds to fall out of step.
/// </remarks>
public sealed class SvgViewerSourceColorizer : DocumentColorizingTransformer
{
    private readonly Func<SvgSourceTokenKind, IBrush?> _brush;

    private IReadOnlyList<SvgSourceLine> _lines = Array.Empty<SvgSourceLine>();

    public SvgViewerSourceColorizer(Func<SvgSourceTokenKind, IBrush?> brush)
        => _brush = brush ?? throw new ArgumentNullException(nameof(brush));

    /// <summary>The lines to colour, which must be the ones the editor is showing.</summary>
    public void Show(IReadOnlyList<SvgSourceLine> lines) => _lines = lines;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.LineNumber > _lines.Count)
        {
            return;
        }

        var tokens = _lines[line.LineNumber - 1].Tokens;

        // A minified drawing is the whole file on one line: 132KB took 1.1s to colour whole, and
        // 39ms once the row stopped past this. What is not coloured is still there to read.
        var coloured = Math.Min(tokens.Count, SvgSourceHighlighter.RowTokenLimit);

        for (var index = 0; index < coloured; index++)
        {
            var token = tokens[index];

            if (_brush(token.Kind) is not { } brush)
            {
                continue;
            }

            // Two things split lines here, and a disagreement would otherwise throw out of a
            // render. Clamping makes it a missing colour rather than no pane at all.
            var start = Math.Max(token.Start, line.Offset);
            var end = Math.Min(token.Start + token.Length, line.EndOffset);

            if (end > start)
            {
                ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }
}
