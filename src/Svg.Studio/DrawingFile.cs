// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using Svg.SourceEditing;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>
/// A drawing's file, as somewhere declarations can be written.
/// </summary>
/// <remarks>
/// The last resort, for a drawing that is under no recipe and open in no tab. Everything else that
/// writes declarations in Studio writes a buffer — a recipe's, or the one behind a drawing's source
/// pane — and <see cref="ISvgViewerDeclarationTarget"/> says as much: "a buffer and not a file:
/// edits arrive one at a time as somebody works, and a host that wrote each one to disk would save
/// on every keystroke and have nothing to take back."
///
/// This is that exception, deliberately, and it costs what the interface says it does. Every edit is
/// written and saved the moment it is made: there is no undo, no unsaved mark, and nothing to
/// confirm on the way out because the file has already changed. It is the honest answer for a
/// drawing nothing else is holding — the alternative was refusing to edit it at all.
///
/// Written through the document it was read from, so the file keeps the byte order mark it had.
/// </remarks>
public sealed class DrawingFile : ISvgViewerDeclarationTarget
{
    private readonly SvgViewerDocument _document;
    private readonly string _path;
    private string _text;

    public DrawingFile(SvgViewerDocument document, string path, string text)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public string Text => _text;

    /// <inheritdoc />
    public bool Apply(IReadOnlyList<SvgTextEdit> edits)
    {
        if (edits is null || edits.Count == 0)
        {
            return false;
        }

        var written = SvgTextEdit.ApplyAll(_text, edits);

        if (string.Equals(written, _text, StringComparison.Ordinal))
        {
            return false;
        }

        _document.Write(written, _path);
        _text = written;

        return true;
    }
}
