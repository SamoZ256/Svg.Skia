// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.IO;
using System.Linq;
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
        SvgcProjectDrawing drawing => Drawn(drawing),
        SvgcProjectRoot root => Named(root) ?? "Project",
        _ => Named(node) ?? "group"
    };

    /// <summary>
    /// What a drawing is called: its file, and what that file becomes.
    /// </summary>
    /// <remarks>
    /// The class it ends up with rather than the one it sets, because a project usually builds the
    /// same file more than once and the entry that differs may name nothing itself — the third
    /// drawing of the sample project takes its class from the group holding it. By the file alone
    /// all three were rows reading "badge.svg".
    /// </remarks>
    private static string Drawn(SvgcProjectDrawing drawing)
    {
        var file = Path.GetFileName(drawing.Input);

        return drawing.EffectiveClass is { } becomes ? $"{file} - {becomes}" : file;
    }

    /// <summary>What a group calls itself: its namespace, its class, or both.</summary>
    /// <remarks>
    /// <para>
    /// Neither is a name — they are settings a group hands down to its drawings — but the format
    /// has nothing else to tell one group from another, and rows all reading "group" tell them
    /// apart no better than nothing would. Taking only the first meant two groups beside each other
    /// could be named off different attributes.
    /// </para>
    /// <para>
    /// Joined with a hyphen rather than the dash used elsewhere, because the window title puts this
    /// beside the project's name with a dash of its own.
    /// </para>
    /// </remarks>
    private static string? Named(SvgcProjectNode node)
        => string.Join(" - ", new[] { node.Namespace, node.Class }.Where(part => part is { })) is { Length: > 0 } name
            ? name
            : null;
}
