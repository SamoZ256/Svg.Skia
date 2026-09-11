// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using Svg.SourceEditing;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>Where a host keeps the declarations of a drawing that does not declare them itself.</summary>
/// <remarks>
/// A drawing built through <see cref="SvgViewer.Rewrite"/> shows parameters its file has never
/// heard of. They belong to whatever made them — an svgc recipe — and that is where the panel's
/// commands have to write, so this is the document they read and the one they change.
///
/// A held document and not a file: edits arrive one at a time as somebody works, and a host that
/// wrote each one to disk would save on every gesture and have nothing to take back.
/// </remarks>
public interface ISvgViewerDeclarationTarget
{
    /// <summary>The document the declarations are in, as it currently stands.</summary>
    string Text { get; }

    /// <summary>
    /// Runs one edit against that document, or answers why it could not be made.
    /// </summary>
    /// <param name="label">What the person did, for a menu to name what it would take back.</param>
    /// <param name="edit">The mutation, answering null or the sentence refusing it.</param>
    /// <returns>The refusal, or null where the edit was made or would have changed nothing.</returns>
    string? Commit(string label, Func<SvgSourceDocument, string?> edit);
}
