// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Svg.Highlighting;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// A text box's presenter that paints each piece of an expression by what the language says it is.
/// </summary>
/// <remarks>
/// A <see cref="TextBox"/> has one foreground, and the only thing that can give it more is whatever
/// builds its layout. So this replaces that and nothing else: the caret, the selection, composition,
/// the clipboard and undo all stay the box's own. It is put in place by a control theme rather than
/// by replacing the control, so only the boxes that hold expressions get it.
/// </remarks>
public class SvgExpressionPresenter : TextPresenter
{
    /// <summary>Repaints when the theme changes, since the brushes are the theme's.</summary>
    public SvgExpressionPresenter()
        => ActualThemeVariantChanged += (_, _) => InvalidateTextLayout();

    protected override TextLayout CreateTextLayout()
    {
        var caret = CaretIndex;
        var preedit = PreeditText;
        var text = Combined(Text, caret, preedit);
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);

        return new TextLayout(
            text,
            typeface,
            FontSize,
            Foreground,
            TextAlignment,
            TextWrapping,
            textTrimming: null,
            textDecorations: null,
            FlowDirection,

            // Unbounded on purpose. The width only decides where to wrap and what to align against,
            // and a one-line box does neither — which is what keeps the presenter's own constraint,
            // a private field two layout passes maintain, out of this.
            maxWidth: double.PositiveInfinity,
            maxHeight: double.PositiveInfinity,
            LineHeight,
            LetterSpacing,
            maxLines: 0,
            FontFeatures,
            Spans(text, typeface, caret, preedit));
    }

    /// <summary>The text as it is being shown, which while composing is not the text.</summary>
    /// <remarks>
    /// An input method puts what is being composed at the caret before it is committed, and the box
    /// shows both at once. Laying out the committed text alone would drop the half being typed.
    /// </remarks>
    private static string Combined(string? text, int caret, string? preedit)
    {
        var body = text ?? string.Empty;

        if (string.IsNullOrEmpty(preedit))
        {
            return body;
        }

        var at = Math.Max(0, Math.Min(caret, body.Length));

        return body.Substring(0, at) + preedit + body.Substring(at);
    }

    /// <summary>A run of properties per piece the language named, or null where there is nothing to say.</summary>
    /// <remarks>
    /// Composition wins over colour while it lasts: the underline is what says which characters are
    /// not committed yet, and two sets of overlapping spans is not something to hand a layout.
    /// Selected text keeps its colours — the source pane does the same, since AvaloniaEdit leaves its
    /// selection foreground unset, and the two panes showing one expression should agree.
    /// </remarks>
    private IReadOnlyList<ValueSpan<TextRunProperties>>? Spans(
        string text,
        Typeface typeface,
        int caret,
        string? preedit)
    {
        if (!string.IsNullOrEmpty(preedit))
        {
            var at = Math.Max(0, Math.Min(caret, text.Length - preedit!.Length));

            return new[]
            {
                new ValueSpan<TextRunProperties>(
                    at,
                    preedit.Length,
                    new GenericTextRunProperties(
                        typeface,
                        FontSize,
                        TextDecorations.Underline,
                        Foreground,
                        fontFeatures: FontFeatures)),
            };
        }

        var spans = new List<ValueSpan<TextRunProperties>>();

        foreach (var token in SvgSourceHighlighter.Expression(text))
        {
            if (token.Length == 0 || Brush(token.Kind) is not { } brush)
            {
                continue;
            }

            spans.Add(new ValueSpan<TextRunProperties>(
                token.Start,
                token.Length,
                new GenericTextRunProperties(
                    typeface,
                    FontSize,
                    foregroundBrush: brush,
                    fontFeatures: FontFeatures)));
        }

        return spans.Count > 0 ? spans : null;
    }

    private IBrush? Brush(SvgSourceTokenKind kind)
        => this.TryFindResource(SvgViewer.SourceResourceKey(kind), ActualThemeVariant, out var found)
            ? found as IBrush
            : null;
}
