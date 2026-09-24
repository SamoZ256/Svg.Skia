// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
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

    /// <summary>
    /// How many gestures to keep. The same bound a drawing's own history uses, and here for the same
    /// reason: far past what anyone reaches for, so a long session cannot grow without one.
    /// </summary>
    private const int Depth = 200;

    private readonly List<Gesture> _steps = new();

    /// <summary>How many of <see cref="_steps"/> have been done: the index of the next redo.</summary>
    private int _at;

    private bool _applying;

    private readonly record struct Gesture(string Label, Action Back, Action Again);

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

    /// <summary>Whether there is a gesture to take back.</summary>
    public bool CanUndo => _at > 0;

    /// <summary>Whether there is one to put again.</summary>
    public bool CanRedo => _at < _steps.Count;

    /// <summary>What taking one back would take back, for a menu to name it.</summary>
    public string? UndoLabel => CanUndo ? _steps[_at - 1].Label : null;

    /// <inheritdoc cref="UndoLabel"/>
    public string? RedoLabel => CanRedo ? _steps[_at].Label : null;

    /// <summary>
    /// Runs one gesture against the document and keeps what it takes to put it back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only way to edit a project, and that is the point of it: an edit that cannot be
    /// taken back cannot be recorded, so "everything is undoable" is a thing the compiler asks for
    /// rather than a thing anyone has to remember. What used to be here was a bare counter, and the
    /// half of the editor that wrote through it had no way back at all.
    /// </para>
    /// <para>
    /// One entry per gesture, not per attribute — settling a board writes a place on every row under
    /// a tab, and what somebody wants back is the drag.
    /// </para>
    /// </remarks>
    /// <param name="label">What the person did, for a menu to say "Undo remove Large".</param>
    /// <param name="capture">
    /// The state this gesture is about to change, as something that puts it back. Called twice, once
    /// on each side of <paramref name="edit"/>, so one closure gives both the way back and the way
    /// forward.
    /// </param>
    /// <param name="edit">The mutation, which has already happened by the time this returns.</param>
    public void Do(string label, Func<Action> capture, Action edit)
    {
        if (capture is null)
        {
            throw new ArgumentNullException(nameof(capture));
        }

        if (edit is null)
        {
            throw new ArgumentNullException(nameof(edit));
        }

        // A gesture inside a gesture is part of it. The one outside has already captured the whole
        // of what is about to change, so this has nothing to add — and a second entry for one thing
        // somebody did is an entry that takes half of it back. It arises where a gesture both writes
        // a drawing's text, through the seam that records it, and moves something about the row the
        // text belongs to: a page dragged wider on a board is both.
        if (_inside)
        {
            edit();

            return;
        }

        _inside = true;

        try
        {
            Step(label, capture, edit);
        }
        finally
        {
            _inside = false;
        }
    }

    /// <summary>Whether a gesture is already being recorded, and anything inside it is part of it.</summary>
    private bool _inside;

    /// <summary>Records one gesture, having settled that it is not inside another.</summary>
    private void Step(string label, Func<Action> capture, Action edit)
    {
        var undo = capture();

        edit();

        // Nothing is kept while one is being applied: an undo is itself a change to the document,
        // and recording it would put the way back onto the history as a step of its own.
        if (_applying)
        {
            return;
        }

        var redo = capture();

        // Whatever was taken back and not put again is gone the moment something else is done,
        // which is what every text editor does with a redo tail.
        _steps.RemoveRange(_at, _steps.Count - _at);
        _steps.Add(new Gesture(label, undo, redo));

        if (_steps.Count > Depth)
        {
            _steps.RemoveAt(0);
        }

        _at = _steps.Count;

        Edit();
    }

    /// <summary>Takes back the last gesture.</summary>
    /// <returns>Whether there was anything to take back.</returns>
    /// <remarks>
    /// The answer matters: a host with more than one history behind it — a drawing's text, and the
    /// project holding that drawing — asks each in turn and stops at the first that says yes.
    /// </remarks>
    public bool Undo()
    {
        if (!CanUndo)
        {
            return false;
        }

        Apply(_steps[_at - 1].Back);

        _at--;

        Edit();

        return true;
    }

    /// <inheritdoc cref="Undo"/>
    public bool Redo()
    {
        if (!CanRedo)
        {
            return false;
        }

        Apply(_steps[_at].Again);

        _at++;

        Edit();

        return true;
    }

    private void Apply(Action way)
    {
        _applying = true;

        try
        {
            way();
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>Records one edit to the document, which is left for somebody to save.</summary>
    private void Edit()
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
