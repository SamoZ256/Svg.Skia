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
/// in Studio writes the buffer behind its tab; there is no buffer here, so the edit goes straight
/// into the project, where the tree and every other view read it.
///
/// What that costs is the buffer's two services: there is nothing to take back with ⌘Z, and no mark
/// of its own, since no tab is holding it. The project carries it as it carries a row dragged in the
/// tree, so it is not on disk and not silent — the title says the project is unsaved.
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

        // What the groups above declare, so a let written here can name it. Nothing of it is
        // written back — the edit lands in this drawing's own text and nowhere else.
        source.Inherited = ProjectDeclarations.Declared(_drawing);

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

        _workspace.Edit();

        return null;
    }
}
