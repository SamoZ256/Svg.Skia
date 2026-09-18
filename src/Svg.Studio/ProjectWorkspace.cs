// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.IO;
using Svg.Skia;

namespace Svg.Studio;

/// <summary>
/// The open project: the tree in the pane, and the edits made to it that are not on disk yet.
/// </summary>
/// <remarks>
/// Not a control and not a tab. A project is the thing the window is working on rather than one of
/// the things it is showing, so nothing about it belongs to a tab that can be closed — which is what
/// lets a group and a drawing both open as ordinary tabs over it.
///
/// It is also where unsaved work that belongs to no tab is counted. A row added, moved or removed,
/// and a board arranged, edit the document itself rather than a buffer behind some tab, so there is
/// nowhere else for them to be remembered between the gesture and the save.
/// </remarks>
public sealed class ProjectWorkspace
{
    /// <param name="edited">
    /// Whether what is being opened is already unsaved — an import, a conversion or a recovered copy,
    /// none of which exist anywhere but in memory. Only the caller knows which of those it is doing.
    /// </param>
    /// <param name="suggested">
    /// What to call the file when somebody is asked where to put it. A conversion is named after the
    /// document it came from; a project made from nothing has nothing to suggest.
    /// </param>
    public ProjectWorkspace(ProjectDocument document, bool edited = false, string? suggested = null)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Edits = edited ? 1 : 0;
        Suggested = suggested;
    }

    public ProjectDocument Document { get; }

    /// <summary>Raised when the document has changed, so every view of it can follow.</summary>
    public event EventHandler? Edited;

    /// <summary>Raised when the file has caught up with the document.</summary>
    /// <remarks>
    /// Its own event rather than another <see cref="Edited"/>: nothing the document says has changed,
    /// so a save must not cost a tree rebuild and a re-lay of every open board. What follows this is
    /// the chrome that reports whether there is anything left to write.
    /// </remarks>
    public event EventHandler? Saved;

    /// <summary>What the project is called: its file, or that it has not got one.</summary>
    /// <remarks>
    /// Not the name it is going to be saved under — a project that reads as a file it is not would
    /// be a worse lie than an honest Untitled, since that name is exactly what has not been decided.
    /// </remarks>
    public string Name => Document.Path is { } path ? Path.GetFileName(path) : "Untitled";

    /// <summary>The name to offer when somebody is asked where to put this, or null for none.</summary>
    public string? Suggested { get; }

    /// <summary>Gestures made since the document was last written.</summary>
    /// <remarks>
    /// One per gesture and not per attribute: settling a board writes a place on every row under it,
    /// and what a crash would cost is the one drag, not the rows. Counted rather than flagged because
    /// how much is at stake is what decides how soon a recovery copy is worth writing.
    /// </remarks>
    public int Edits { get; private set; }

    /// <summary>Whether the document holds work its file does not.</summary>
    /// <remarks>
    /// A project edited back to what it was still counts as edited. Answering otherwise means
    /// comparing the whole document against the bytes last written, on a file an import can size in
    /// the tens of megabytes, to spare somebody a save they were about to make anyway.
    /// </remarks>
    public bool IsEdited => Edits > 0;

    /// <summary>Records one edit to the document, which is left for somebody to save.</summary>
    public void Edit()
    {
        Edits++;
        Edited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes the document to the file it is, and says so.</summary>
    /// <remarks>
    /// Counted down only once the write has returned: a project that reported itself saved when the
    /// write threw would drop its mark, stop offering Save and let the window close over the lot.
    /// </remarks>
    public void Save()
    {
        Document.Save();

        Settle();
    }

    /// <summary>Writes the document to <paramref name="path"/>, which it becomes.</summary>
    /// <remarks>Where a project with no file of its own gets one, having been asked about.</remarks>
    public void Save(string path)
    {
        Document.Save(path);

        Settle();
    }

    private void Settle()
    {
        Edits = 0;

        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The size a drawing is built at once every group above it has had its say.</summary>
    public static SvgSizeRequest SizeOf(ProjectNode node)
        => new(node.EffectiveWidth, node.EffectiveHeight, node.EffectiveScale, SvgPadding.Parse(node.EffectivePadding));

    /// <summary>How a node is named, in the tree and on its tab.</summary>
    /// <remarks>
    /// Its own name, which is what the format asks every drawing and group for. The project has
    /// none: it is named by the file it is, and that is the window title's business.
    /// </remarks>
    public static string Label(ProjectNode node) => node switch
    {
        ProjectRoot => "Project",
        _ => node.Name is { Length: > 0 } name ? name : "unnamed"
    };
}
