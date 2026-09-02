// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Svg.Expressions.Recipes;
using Svg.SourceEditing;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>
/// One open recipe: its text, and what the parser makes of it.
/// </summary>
/// <remarks>
/// The file the window is working on rather than one of the things it shows, the way
/// <see cref="ProjectWorkspace"/> is. A recipe is edited from more than one place — as text in a tab
/// of its own, and through the colours and parameters of a drawing under it — and two of those
/// holding their own copy would disagree about what the file says the moment one of them was typed
/// in. There is one buffer, and every view splices into it.
///
/// The buffer is AvaloniaEdit's own, so an edit made anywhere lands on the undo stack the editor
/// shows and can be taken back there.
/// </remarks>
public sealed class RecipeWorkspace : ISvgViewerDeclarationTarget
{
    /// <summary>Waits for typing to stop before saying the recipe has changed.</summary>
    /// <remarks>
    /// Every drawing under a recipe is rebuilt when it changes, so a keystroke is far too often.
    /// The same interval the viewer rebuilds a drawing from its source pane on.
    /// </remarks>
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(200d) };

    /// <summary>Whether the parse below is out of date. Read again when somebody asks, not on the edit.</summary>
    /// <remarks>
    /// Lazily, because both the tab showing the text and the window rebuilding the drawings ask
    /// during the same keystroke: parsing where the change arrives would make one of them right and
    /// the other a keystroke behind, depending on which subscribed first.
    /// </remarks>
    private bool _stale = true;

    private SvgRecipe? _recipe;
    private string? _fault;
    private bool _modified;

    public RecipeWorkspace(string path)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));

        string text;

        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Opened empty and saying why, rather than not opening: there is nowhere else to read
            // the reason, and a recipe named by a project that is not there is worth seeing.
            text = string.Empty;
            _fault = failure.Message;
            _stale = false;
        }

        Document = new TextDocument(text);
        Document.UndoStack.MarkAsOriginalFile();

        Document.TextChanged += (_, _) =>
        {
            _stale = true;

            // Posted, not called: AvaloniaEdit raises this before its undo stack has taken the edit,
            // so the file still reads as unmodified here and a tab would never get its mark.
            Dispatcher.UIThread.Post(Announce);

            _settle.Stop();
            _settle.Start();
        };

        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Edited?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>The file this is the text of.</summary>
    public string Path { get; }

    /// <summary>The one buffer. Every view onto this recipe shows and edits this.</summary>
    public TextDocument Document { get; }

    public string Text => Document.Text;

    /// <summary>Whether the text has edits that are not on disk.</summary>
    public bool IsModified => !Document.UndoStack.IsOriginalFile;

    /// <summary>What the text comes to, or null when it would not read.</summary>
    public SvgRecipe? Recipe
    {
        get
        {
            Parse();

            return _recipe;
        }
    }

    /// <summary>
    /// Why the recipe would not read, or null.
    /// </summary>
    /// <remarks>
    /// Said rather than refused: half a recipe is what one looks like while it is being written, and
    /// taking the text back between keystrokes would make it unwritable. The drawings under it go on
    /// showing what the last readable version made of them.
    /// </remarks>
    public string? Fault
    {
        get
        {
            Parse();

            return _fault;
        }
    }

    /// <summary>Raised once typing has stopped, for everything built from this recipe to follow.</summary>
    public event EventHandler? Edited;

    /// <summary>Raised when <see cref="IsModified"/> changes, for a host that marks its tabs.</summary>
    public event EventHandler<bool>? ModifiedChanged;

    /// <summary>
    /// Puts an edit into the buffer, wherever it was worked out.
    /// </summary>
    /// <remarks>
    /// The one way in for everything structured: the colours pane writes a rule this way and the
    /// viewer's parameter panel writes a declaration, and both land as one step on the stack the
    /// text tab shows. Nothing here decides what an edit means — it is spans by the time it arrives.
    /// </remarks>
    public bool Apply(IReadOnlyList<SvgTextEdit> edits)
    {
        if (edits is null)
        {
            throw new ArgumentNullException(nameof(edits));
        }

        if (edits.Count == 0)
        {
            return false;
        }

        Document.BeginUpdate();

        try
        {
            // Back to front, so an earlier edit does not move the ones after it.
            for (var index = edits.Count - 1; index >= 0; index--)
            {
                var edit = edits[index];

                Document.Replace(edit.Position, edit.Length, edit.Text);
            }
        }
        finally
        {
            Document.EndUpdate();
        }

        return true;
    }

    /// <summary>Takes back the last edit, or puts it back.</summary>
    /// <remarks>
    /// The stack the text tab shows, so an edit made from a drawing's panes and one typed into the
    /// recipe are taken back the same way and in the order they were made.
    /// </remarks>
    public bool Undo()
    {
        if (!Document.UndoStack.CanUndo)
        {
            return false;
        }

        Document.UndoStack.Undo();

        return true;
    }

    /// <inheritdoc cref="Undo"/>
    public bool Redo()
    {
        if (!Document.UndoStack.CanRedo)
        {
            return false;
        }

        Document.UndoStack.Redo();

        return true;
    }

    /// <summary>Writes the text to the file.</summary>
    public void Save()
    {
        File.WriteAllText(Path, Document.Text);

        Document.UndoStack.MarkAsOriginalFile();

        Announce();
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
            // The parser the build uses, not a second opinion about the format: a message here the
            // build did not agree with would be worse than no message at all.
            _recipe = SvgRecipe.Parse(Document.Text);
            _fault = null;
        }
        catch (SvgRecipeException failure)
        {
            _recipe = null;
            _fault = failure.Message;
        }
    }

    private void Announce()
    {
        var modified = IsModified;

        if (modified == _modified)
        {
            return;
        }

        _modified = modified;
        ModifiedChanged?.Invoke(this, modified);
    }
}
