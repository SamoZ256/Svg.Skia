// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using Svg.SourceEditing;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>A drawing in the project, as somewhere declarations can be written.</summary>
/// <remarks>
/// The last resort, for a drawing that is open in no tab. Everything else that writes into a drawing
/// in Studio writes the buffer behind its tab, and <see cref="ISvgViewerDeclarationTarget"/> says as
/// much: "a buffer and not a file: edits arrive one at a time as somebody works, and a host that
/// wrote each one to disk would save on every gesture and have nothing to take back."
///
/// This is that exception, deliberately, and it costs what the interface says it does. Every edit is
/// written into the project and saved the moment it is made: there is no undo, no unsaved mark, and
/// nothing to confirm on the way out because the file has already changed. It is the honest answer
/// for a drawing nothing else is holding — the alternative was refusing to edit it at all.
/// </remarks>
public sealed class DrawingTarget : ISvgViewerDeclarationTarget
{
    private readonly ProjectWorkspace _workspace;
    private readonly ProjectDrawing _drawing;

    public DrawingTarget(ProjectWorkspace workspace, ProjectDrawing drawing)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _drawing = drawing ?? throw new ArgumentNullException(nameof(drawing));
    }

    public string Text => _drawing.Text;

    /// <inheritdoc />
    /// <remarks>
    /// The tree is read afresh for each edit rather than held: there is no history here to keep one
    /// consistent with, and a document kept across writes would be one more thing that could come to
    /// disagree with the project it was read from.
    /// </remarks>
    public string? Commit(string label, Func<SvgSourceDocument, string?> edit)
    {
        if (edit is null)
        {
            throw new ArgumentNullException(nameof(edit));
        }

        var text = _drawing.Text;

        if (SvgSourceDocument.Read(text, out var unreadable) is not { } source)
        {
            return unreadable;
        }

        if (edit(source) is { } refusal)
        {
            return refusal;
        }

        var written = source.ToText();

        if (string.Equals(written, text, StringComparison.Ordinal))
        {
            return null;
        }

        if (_drawing.SetText(written) is { } bad)
        {
            return bad;
        }

        _workspace.Save();

        return null;
    }
}
