// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Svg.SourceEditing;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// What the declaration panel's buttons do, for whatever is hosting it.
/// </summary>
/// <remarks>
/// Every one of them is a call into <see cref="SvgDeclarationEditor"/> over the document the
/// declarations live in. What differs between hosts is only which document that is and what
/// committing to it means, so both are handed in and the commands themselves are host-agnostic.
///
/// Here rather than in <see cref="SvgViewer"/> because the viewer is no longer the only thing that
/// shows a panel: a project group's tab shows the parameters of the recipe its drawings are built
/// through, and the commands there have to write into the same recipe in the same way. A second copy
/// would be a second set of answers about what a rename does.
/// </remarks>
public sealed class SvgViewerDeclarationCommands
{
    private readonly Func<string> _text;
    private readonly Func<string, Func<SvgSourceDocument, string?>, bool> _write;
    private readonly Func<IReadOnlyList<SvgViewerParameter>> _rows;
    private readonly Func<ISvgViewerParameterDialogService> _dialogs;

    /// <param name="text">The document the declarations are in, as it currently stands.</param>
    /// <param name="write">
    /// What to do with the edit a command comes to, and where a refusal is reported. It is handed
    /// what the person did, for a menu to name, and answers whether the document changed.
    /// </param>
    /// <param name="rows">
    /// The rows on show. They are what a name has to avoid clashing with and what a commit reads;
    /// a function rather than a list because they are rebuilt whenever the declarations change.
    /// </param>
    /// <param name="dialogs">
    /// How to ask what to declare. Read at the moment of asking, since a host may replace it — a
    /// test does exactly that.
    /// </param>
    public SvgViewerDeclarationCommands(
        Func<string> text,
        Func<string, Func<SvgSourceDocument, string?>, bool> write,
        Func<IReadOnlyList<SvgViewerParameter>> rows,
        Func<ISvgViewerParameterDialogService> dialogs)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _rows = rows ?? throw new ArgumentNullException(nameof(rows));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    }

    /// <summary>Asks for a parameter and writes it where the declarations live.</summary>
    public async Task<bool> AddAsync(TopLevel? owner)
    {
        var taken = _rows().Select(row => row.Name).ToList();

        var parameter = await _dialogs()
            .AskAsync(owner, taken)
            .ConfigureAwait(true);

        return parameter is { } declared && _write($"add {declared.Name}", source => SvgDeclarationEditor.Add(source, declared));
    }

    /// <summary>Asks what one parameter should declare, and writes the answer.</summary>
    public async Task<bool> EditAsync(TopLevel? owner, SvgViewerParameter parameter)
    {
        if (parameter is null)
        {
            throw new ArgumentNullException(nameof(parameter));
        }

        // Its own name is not one it clashes with.
        var taken = _rows()
            .Where(row => !ReferenceEquals(row, parameter))
            .Select(row => row.Name)
            .ToList();

        var replacement = await _dialogs()
            .EditAsync(owner, taken, parameter.Declaration)
            .ConfigureAwait(true);

        return replacement is { } wanted
            && _write($"change {parameter.Name}", source => SvgDeclarationEditor.Update(source, parameter.Name, wanted));
    }

    /// <summary>Takes one parameter out.</summary>
    public bool Remove(SvgViewerParameter parameter)
    {
        if (parameter is null)
        {
            throw new ArgumentNullException(nameof(parameter));
        }

        return _write($"remove {parameter.Name}", source => SvgDeclarationEditor.Remove(source, parameter.Name));
    }

    /// <summary>Writes every value somebody chose in as the declared default.</summary>
    /// <remarks>
    /// One call for the lot, so a session of moving sliders is one thing to take back. Only rows
    /// that differ are written, so committing twice does nothing the second time.
    /// </remarks>
    public bool SetDefaults()
    {
        var changed = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in _rows().Where(row => row.IsModified))
        {
            changed[row.Name] = row.ToExpression();
        }

        return changed.Count > 0 && _write("keep these values", source => SvgDeclarationEditor.SetDefaults(source, changed));
    }

    /// <summary>Writes what a let row says, declaring it if it is not there yet.</summary>
    public bool CommitLet(SvgViewerLet let)
    {
        if (let is null)
        {
            throw new ArgumentNullException(nameof(let));
        }

        var name = let.Name.Trim();
        var expression = let.Expression.Trim();

        return let.Declaration is { } declared
            ? _write($"change {declared.Name}", source => SvgDeclarationEditor.UpdateLet(source, declared.Name, name, expression))
            : _write($"add {name}", source => SvgDeclarationEditor.AddLet(source, name, expression));
    }

    /// <summary>Moves a let to <paramref name="to"/> among the lets.</summary>
    public bool MoveLet(SvgViewerLet let, int to)
    {
        if (let is null)
        {
            throw new ArgumentNullException(nameof(let));
        }

        return let.Declaration is { } declared
               && _write($"move {declared.Name}", source => SvgDeclarationEditor.MoveLet(source, declared.Name, to));
    }

    /// <summary>Takes one let out.</summary>
    public bool RemoveLet(SvgViewerLet let)
    {
        if (let is null)
        {
            throw new ArgumentNullException(nameof(let));
        }

        return let.Declaration is { } declared
               && _write($"remove {declared.Name}", source => SvgDeclarationEditor.RemoveLet(source, declared.Name));
    }

    /// <summary>Moves a parameter to <paramref name="to"/> among the parameters.</summary>
    public bool MoveParameter(SvgViewerParameter parameter, int to)
    {
        if (parameter is null)
        {
            throw new ArgumentNullException(nameof(parameter));
        }

        return _write($"move {parameter.Name}", source => SvgDeclarationEditor.MoveParameter(source, parameter.Name, to));
    }
}
