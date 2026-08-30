// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.IO;
using Svg.CodeGen.Skia.Projects;
using Svg.Skia;

namespace Svg.Studio;

/// <summary>
/// The open project: the tree in the pane, and whether it has edits that are not on disk.
/// </summary>
/// <remarks>
/// Not a control and not a tab. A project is the thing the window is working on rather than one of
/// the things it is showing, so nothing about it belongs to a tab that can be closed — which is what
/// lets a group and a drawing both open as ordinary tabs over it.
/// </remarks>
public sealed class ProjectWorkspace
{
    public ProjectWorkspace(SvgcProjectDocument document)
        => Document = document ?? throw new ArgumentNullException(nameof(document));

    public SvgcProjectDocument Document { get; }

    /// <summary>Raised when the document has changed on disk, so every view of it can follow.</summary>
    /// <remarks>
    /// Unsaved work belongs to the tab it was typed in, not here — a project is one file, but each
    /// tab saves only what was typed in it, so there is no single dirty state to keep.
    /// </remarks>
    public event EventHandler? Edited;

    public string Name => Document.Path is { } path ? Path.GetFileName(path) : "A project";

    public void Save()
    {
        Document.Save();
        Edited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The size a drawing is built at once every group above it has had its say.</summary>
    public static SvgSizeRequest SizeOf(SvgcProjectNode node)
        => new(node.EffectiveWidth, node.EffectiveHeight, node.EffectiveScale, SvgPadding.Parse(node.EffectivePadding));

    /// <summary>How a node is named, in the tree and on its tab.</summary>
    public static string Label(SvgcProjectNode node) => node switch
    {
        SvgcProjectDrawing drawing => Path.GetFileName(drawing.Input),
        SvgcProjectRoot root => root.Namespace ?? "Project",
        _ => node.Namespace ?? node.Class ?? "group"
    };
}
