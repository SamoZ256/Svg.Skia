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
/// <para>
/// The editor is told what colour a range is rather than handed a grammar, which is the whole reason
/// a text editor can be used here at all: no stock XML grammar knows that <c>{{ hsl(…) }}</c> is
/// code or that an <c>&lt;e:let&gt;</c> body is a declaration, and the splitter that does knows
/// nothing about how any of it is drawn.
/// </para>
/// <para>
/// Only the lines on screen reach here, which is what makes a large drawing cost what a small one
/// does — 340 lines of a 132KB drawing colour in 18ms because 30 of them are asked about.
/// </para>
/// </remarks>
internal sealed class SvgViewerSourceColorizer : DocumentColorizingTransformer
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

        // Virtualising by line bounds a document but not a line, and a minified drawing is the whole
        // file on one of them: 132KB of it took 1.1s to colour whole, and 39ms once the row stopped
        // past this. Nothing is hidden — what is not coloured is still there to read.
        var coloured = Math.Min(tokens.Count, SvgSourceHighlighter.RowTokenLimit);

        for (var index = 0; index < coloured; index++)
        {
            var token = tokens[index];

            if (_brush(token.Kind) is not { } brush)
            {
                continue;
            }

            // Clamped to the line the editor is asking about. Two things split lines here — this
            // splitter and the editor's document — and a disagreement about, say, a line ending
            // would otherwise throw out of a render. A test pins that they agree; this makes a
            // disagreement show up as a colour that is missing rather than as no pane at all.
            var start = Math.Max(token.Start, line.Offset);
            var end = Math.Min(token.Start + token.Length, line.EndOffset);

            if (end > start)
            {
                ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }
}
