// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Svg.Expressions.Recipes;
using Svg.SourceEditing;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>
/// One open recipe: its tree, and what the recipe reader makes of it.
/// </summary>
/// <remarks>
/// <para>
/// The file the window is working on rather than one of the things it shows, the way
/// <see cref="ProjectWorkspace"/> is. A recipe is edited from more than one place — through the
/// colours and parameters of a drawing under it, and from its own tab — and two of those holding
/// their own copy would disagree about what the file says the moment one of them was written to.
/// There is one workspace, and every view commits into it.
/// </para>
/// <para>
/// The tree is the truth and the text is what it writes, so the tab showing a recipe shows it
/// rather than holding it. That is what makes a rule written from the colours pane and a parameter
/// written from a drawing one thing to take back, and what makes the file come back off a save as
/// the file it was, with the comments and the layout somebody gave it.
/// </para>
/// <para>
/// There are two ways a recipe can be wrong and they are not the same gate. A file that is not well
/// formed XML has no tree at all, and is refused by <see cref="Open"/> — a tab is not opened for it.
/// A file that is well formed and not a recipe — a <c>&lt;defs&gt;</c> where none belongs, a rule
/// with no expression — has a tree, opens, and says so through <see cref="Fault"/> while somebody
/// puts it right from the panes.
/// </para>
/// </remarks>
public sealed class RecipeWorkspace : ISvgViewerDeclarationTarget
{
    private readonly SvgSourceWorkspace _workspace;

    /// <summary>Whether the parse below is out of date. Read again when somebody asks, not on the edit.</summary>
    /// <remarks>
    /// Lazily, because both the tab showing the recipe and the window rebuilding the drawings ask
    /// during the same commit: parsing where the change arrives would make one of them right and the
    /// other a gesture behind, depending on which subscribed first.
    /// </remarks>
    private bool _stale = true;

    private SvgRecipe? _recipe;
    private string? _fault;

    private RecipeWorkspace(string path, SvgSourceWorkspace workspace, bool byteOrderMark)
    {
        Path = path;
        ByteOrderMark = byteOrderMark;
        _workspace = workspace;

        _workspace.Changed += (_, _) =>
        {
            _stale = true;

            // One gesture, one rebuild. The timer this replaces waited for typing to stop, which is
            // the right thing to wait for when a keystroke is the edit and the wrong one when a
            // button somebody just pressed is.
            Edited?.Invoke(this, EventArgs.Empty);
        };

        _workspace.ModifiedChanged += (_, modified) => ModifiedChanged?.Invoke(this, modified);
    }

    /// <summary>The file this is the recipe of.</summary>
    public string Path { get; }

    /// <summary>Whether the file began with a byte order mark, so a save can put it back.</summary>
    public bool ByteOrderMark { get; }

    /// <summary>The recipe as text: what the tree writes.</summary>
    public string Text => _workspace.Text;

    /// <summary>Whether the recipe has edits that are not on disk.</summary>
    public bool IsModified => _workspace.IsModified;

    /// <summary>What the text comes to, or null when the recipe reader would not read it.</summary>
    public SvgRecipe? Recipe
    {
        get
        {
            Parse();

            return _recipe;
        }
    }

    /// <summary>
    /// Why the recipe reader would not read this, or null.
    /// </summary>
    /// <remarks>
    /// Said rather than refused, because a recipe missing the thing it is about to be given is the
    /// ordinary state of one being written from the panes: a parameter arrives before the rule that
    /// names it. What is refused instead is text with no tree at all, which never gets this far.
    /// </remarks>
    public string? Fault
    {
        get
        {
            Parse();

            return _fault;
        }
    }

    /// <summary>Raised after a gesture, for everything built from this recipe to follow.</summary>
    public event EventHandler? Edited;

    /// <summary>Raised when <see cref="IsModified"/> changes, for a host that marks its tabs.</summary>
    public event EventHandler<bool>? ModifiedChanged;

    /// <summary>Opens a recipe, or refuses with a sentence saying why it could not be.</summary>
    /// <remarks>
    /// A file that will not read has no tree to edit and nothing to show but itself, so it is not
    /// opened and the reason is given to whoever asked. That is a change from holding it open and
    /// empty with the reason under it: the cost is that a recipe broken by hand has to be put right
    /// somewhere other than here.
    /// </remarks>
    public static RecipeWorkspace? Open(string path, out string? refusal)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        string text;

        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            refusal = failure.Message;

            return null;
        }

        if (SvgSourceWorkspace.Open(text, out refusal) is not { } workspace)
        {
            return null;
        }

        return new RecipeWorkspace(path, workspace, workspace.Document.ByteOrderMark);
    }

    /// <summary>
    /// Runs one edit against the recipe, or says why it could not be made.
    /// </summary>
    /// <remarks>
    /// The one way in for everything structured: the colours pane writes a rule this way and a
    /// drawing's parameter panel writes a declaration, and both are one thing to take back.
    /// </remarks>
    public string? Commit(string label, Func<SvgSourceDocument, string?> edit)
        => _workspace.Commit(label, edit);

    /// <inheritdoc cref="Commit(string, Func{SvgSourceDocument, string?})"/>
    public string? Commit(string label, Func<string, string> rewrite)
        => _workspace.Commit(label, rewrite);

    /// <inheritdoc />
    public bool Apply(IReadOnlyList<SvgTextEdit> edits)
    {
        if (edits is null)
        {
            throw new ArgumentNullException(nameof(edits));
        }

        return edits.Count > 0
               && _workspace.Commit("edit the recipe", text => SvgTextEdit.ApplyAll(text, edits)) is null;
    }

    /// <summary>Takes back the last gesture, or puts it back.</summary>
    /// <remarks>
    /// One history however it was written, so a rule from the colours pane and a parameter from a
    /// drawing are taken back in the order they were made.
    /// </remarks>
    public bool Undo() => _workspace.Undo();

    /// <inheritdoc cref="Undo"/>
    public bool Redo() => _workspace.Redo();

    /// <summary>Writes the recipe to its file.</summary>
    /// <remarks>
    /// In the encoding it arrived in, so a byte order mark survives — which it did not before, since
    /// the buffer was written out with a plain WriteAllText.
    /// </remarks>
    public void Save()
    {
        File.WriteAllText(Path, _workspace.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: ByteOrderMark));

        _workspace.MarkSaved();
    }

    private void Parse()
    {
        if (!_stale)
        {
            return;
        }

        _stale = false;

        try
        {
            // The reader the build uses, not a second opinion about the format: a message here the
            // build did not agree with would be worse than no message at all.
            _recipe = SvgRecipe.Parse(_workspace.Text);
            _fault = null;
        }
        catch (SvgRecipeException failure)
        {
            _recipe = null;
            _fault = failure.Message;
        }
    }
}
