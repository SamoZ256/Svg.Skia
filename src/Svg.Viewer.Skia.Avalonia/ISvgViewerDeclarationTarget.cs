// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Collections.Generic;
using Svg.SourceEditing;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>Where a host keeps the declarations of a drawing that does not declare them itself.</summary>
/// <remarks>
/// A drawing built through <see cref="SvgViewer.Rewrite"/> shows parameters its file has never
/// heard of. They belong to whatever made them — an svgc recipe — and that is where the panel's
/// commands have to write, so this is the text they read and the buffer they land in.
///
/// A buffer and not a file: edits arrive one at a time as somebody works, and a host that wrote each
/// one to disk would save on every keystroke and have nothing to take back.
/// </remarks>
public interface ISvgViewerDeclarationTarget
{
    /// <summary>The document the declarations are in, as it currently stands.</summary>
    string Text { get; }

    /// <summary>Applies the spans an edit came to. Ascending, and non-overlapping.</summary>
    /// <returns>Whether the document changed.</returns>
    bool Apply(IReadOnlyList<SvgTextEdit> edits);
}
