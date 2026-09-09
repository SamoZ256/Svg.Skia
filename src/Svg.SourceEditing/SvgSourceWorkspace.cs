// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;

namespace Svg.SourceEditing;

/// <summary>
/// An open drawing and everything that has been done to it: one entry to take back per gesture.
/// </summary>
/// <remarks>
/// <para>
/// What replaces a text editor's undo stack for a host whose truth is the tree. Each gesture is one
/// <see cref="Commit(string, Func{SvgSourceDocument, string?})"/>, however many attributes or
/// elements it moves, so a resize that writes three attributes is one thing to take back — which is
/// what <c>BeginUpdate</c> and <c>EndUpdate</c> were doing around a batch of spans.
/// </para>
/// <para>
/// A step is reversed by reading the drawing's own text back, and that is worth saying plainly
/// because reversing by snapshot is usually the wrong answer. It is the right one here for two
/// reasons. <see cref="SvgSourceDocument.ToText"/> is byte-faithful, so the text is a lossless
/// record of the tree rather than an approximation of it — the annotations carrying the author's
/// bytes are rebuilt from the author's bytes. And an edit needs the text it started from anyway: a
/// declaration can only be checked against the language's rules after the tree has been changed,
/// and one of those checks needs the state before the change, so a refusal has to be able to put
/// the document back. Rollback and undo are then the same mechanism instead of two, and the
/// alternative — an inverse hand-written for each of the editors' operations — is that many
/// separate proofs, each of which fails silently and none of which a test can be written against
/// without already knowing the answer.
/// </para>
/// <para>
/// Nothing may hold an <c>XElement</c>, or <see cref="Document"/> itself, across a commit: the tree
/// is read afresh. That is the arrangement the rest of the code already assumes — an address key
/// is what survives a rebuild, and it exists because the model has always been disposable.
/// </para>
/// </remarks>
public sealed class SvgSourceWorkspace
{
    /// <summary>
    /// How many states to keep. Far past what anyone reaches for, and a bound is only here so a
    /// long session cannot grow without one.
    /// </summary>
    private const int Depth = 200;

    private readonly List<string> _states = new();
    private readonly List<string?> _labels = new();

    private int _at;
    private int _saved;

    private SvgSourceWorkspace(SvgSourceDocument document, string text)
    {
        Document = document;
        _states.Add(text);
        _labels.Add(null);
    }

    /// <summary>Raised after the document has been replaced, by a commit, an undo or a redo.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when <see cref="IsModified"/> changes, for a host that marks its chrome.</summary>
    public event EventHandler<bool>? ModifiedChanged;

    /// <summary>The drawing as it stands. Not to be held across a commit.</summary>
    public SvgSourceDocument Document { get; private set; }

    /// <summary>The drawing as text, without writing it again.</summary>
    public string Text => _states[_at];

    /// <summary>Whether there are edits that are not on disk.</summary>
    /// <remarks>
    /// The index rather than a flag, so taking every edit back to where the file was last written
    /// reports clean again — the same thing a text editor's original-file mark does.
    /// </remarks>
    public bool IsModified => _at != _saved;

    public bool CanUndo => _at > 0;

    public bool CanRedo => _at < _states.Count - 1;

    /// <summary>What taking one back would take back, for a menu to name it.</summary>
    public string? UndoLabel => CanUndo ? _labels[_at] : null;

    /// <inheritdoc cref="UndoLabel"/>
    public string? RedoLabel => CanRedo ? _labels[_at + 1] : null;

    /// <summary>Opens a drawing, or refuses with a sentence saying why it could not be read.</summary>
    public static SvgSourceWorkspace? Open(string svgText, out string? refusal)
    {
        var document = SvgSourceDocument.Read(svgText, out refusal);

        return document is null ? null : new SvgSourceWorkspace(document, document.ToText());
    }

    /// <summary>
    /// Runs one edit against the tree and keeps it, or puts the document back and says why not.
    /// </summary>
    /// <param name="label">
    /// What the person did, for a menu to say "Undo move &lt;rect&gt;". It comes from the gesture
    /// and not from the editor, which serves several of them and knows which of none.
    /// </param>
    /// <param name="edit">
    /// The mutation, answering null or the sentence refusing it. It may leave the tree half changed
    /// when it refuses; putting it back is this method's job, not the editor's.
    /// </param>
    /// <returns>The refusal, or null where the edit was made or would have changed nothing.</returns>
    public string? Commit(string label, Func<SvgSourceDocument, string?> edit)
        => Commit(label, edit, null);

    private string? Commit(string label, Func<SvgSourceDocument, string?> edit, Func<string, string>? rewrite)
    {
        if (label is null)
        {
            throw new ArgumentNullException(nameof(label));
        }

        if (edit is null)
        {
            throw new ArgumentNullException(nameof(edit));
        }

        var before = Text;
        string? refusal;

        try
        {
            refusal = edit(Document);
        }
        catch
        {
            // A fault in an editor is not a person's problem, but a half-edited drawing would be.
            Adopt(before);

            throw;
        }

        if (refusal is { })
        {
            Adopt(before);

            return refusal;
        }

        var after = Document.ToText();

        if (rewrite is { })
        {
            after = rewrite(after);

            if (SvgSourceDocument.Read(after, out var unreadable) is null)
            {
                Adopt(before);

                return unreadable;
            }
        }

        // Whatever the editor believed it did. Setting a value to the one already there is not a
        // step to take back, and must not mark the drawing as holding edits.
        if (string.Equals(after, before, StringComparison.Ordinal))
        {
            return null;
        }

        var modified = IsModified;

        _states.RemoveRange(_at + 1, _states.Count - _at - 1);
        _labels.RemoveRange(_at + 1, _labels.Count - _at - 1);
        _states.Add(after);
        _labels.Add(label);
        _at++;

        // The tree is the truth, so a rewrite that produced the text has to become one.
        if (rewrite is { })
        {
            Adopt(after);
        }

        if (_states.Count > Depth)
        {
            _states.RemoveAt(0);
            _labels.RemoveAt(0);
            _at--;

            // The state the file was written at has gone, so it can never be reached again and the
            // drawing must not read as saved on the way past where it used to be.
            _saved = _saved > 0 ? _saved - 1 : -1;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        Told(modified);

        return null;
    }

    /// <summary>
    /// Runs one edit against the drawing's text and keeps it, or says why it could not be kept.
    /// </summary>
    /// <remarks>
    /// The other medium, and it is sound here for the same reason undo is: <see cref="Text"/> is the
    /// tree's own byte-faithful serialisation, so text rewritten and read back is the document the
    /// rewrite described and nothing is lost on the way through. It is what lets the editors that
    /// still produce spans -- the declarations, which may be written into a recipe instead and so
    /// cannot be tree-only -- land on the same stack as the ones that write the tree.
    /// </remarks>
    /// <returns>The refusal, or null where the edit was made or would have changed nothing.</returns>
    public string? Commit(string label, Func<string, string> rewrite)
    {
        if (rewrite is null)
        {
            throw new ArgumentNullException(nameof(rewrite));
        }

        return Commit(label, _ => null, rewrite);
    }

    /// <summary>Takes back the last gesture.</summary>
    /// <returns>Whether there was anything to take back.</returns>
    /// <remarks>
    /// The answer matters: a host with more than one thing behind it — a drawing, and the recipe
    /// that drawing is under — asks each in turn and stops at the first that says yes.
    /// </remarks>
    public bool Undo() => Step(_at - 1);

    /// <inheritdoc cref="Undo"/>
    public bool Redo() => Step(_at + 1);

    /// <summary>Says the drawing as it stands is what is on disk.</summary>
    public void MarkSaved()
    {
        var modified = IsModified;

        _saved = _at;
        Told(modified);
    }

    private bool Step(int to)
    {
        if (to < 0 || to >= _states.Count)
        {
            return false;
        }

        var modified = IsModified;
        var at = _at;

        _at = to;

        if (!Adopt(_states[to]))
        {
            _at = at;

            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        Told(modified);

        return true;
    }

    /// <summary>
    /// Reads a state back and makes it the document.
    /// </summary>
    /// <remarks>
    /// The text came from writing a tree that had been read, so a refusal here is a fault in the
    /// writer rather than anything a person did. Keeping the document there is then the least bad
    /// answer: an edit that would not go back is better than a drawing that has gone.
    /// </remarks>
    private bool Adopt(string text)
    {
        if (SvgSourceDocument.Read(text, out _) is not { } document)
        {
            return false;
        }

        Document = document;

        return true;
    }

    private void Told(bool was)
    {
        if (was != IsModified)
        {
            ModifiedChanged?.Invoke(this, IsModified);
        }
    }
}
