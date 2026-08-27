// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace Svg.SourceEditing;

/// <summary>Replaces one span of a document with text, in the document's own coordinates.</summary>
/// <remarks>
/// <para>
/// A span rather than a rewritten document, because the point of editing this way is that everything
/// outside the span is left alone — the author's formatting, their attribute order, and their
/// comments, none of which survive being regenerated from a parsed tree.
/// </para>
/// <para>
/// It is also what a text editor wants. Handing back a whole document forces a host to assign the
/// text wholesale, which resets the caret, the scroll and the undo stack; a span goes through the
/// editor's own replace and lands on the undo stack as a step the reader can take back.
/// </para>
/// </remarks>
public readonly record struct SvgTextEdit(int Position, int Length, string Text)
{
    /// <summary>Applies edits to the text they were produced for.</summary>
    /// <remarks>
    /// Back to front, so that an edit's position still means what it meant when it was produced: an
    /// insertion near the top of a document moves everything after it. A host with a real text
    /// editor should hand the spans to that instead, and gets undo for it; this is for one that has
    /// only a string.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">An edit falls outside <paramref name="text"/>.</exception>
    public static string ApplyAll(string text, IReadOnlyList<SvgTextEdit> edits)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (edits is null)
        {
            throw new ArgumentNullException(nameof(edits));
        }

        var builder = new StringBuilder(text);

        for (var index = edits.Count - 1; index >= 0; index--)
        {
            var edit = edits[index];

            if (edit.Position < 0 || edit.Length < 0 || edit.Position + edit.Length > text.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(edits),
                    $"An edit at {edit.Position} for {edit.Length} does not fit a document of {text.Length}.");
            }

            builder.Remove(edit.Position, edit.Length);
            builder.Insert(edit.Position, edit.Text);
        }

        return builder.ToString();
    }
}
