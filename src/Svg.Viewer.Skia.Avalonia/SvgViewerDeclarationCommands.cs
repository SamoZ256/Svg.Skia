// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Svg.Expressions;
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
/// shows a panel: a project group's tab shows what the group declares for the drawings under it, and
/// the commands there have to write into the group in the same way. A second copy would be a second
/// set of answers about what a rename does.
///
/// One instance per document, so a panel whose rows come from several — a drawing and the groups it
/// inherits from — keeps one of these per owner and picks by the row.
/// </remarks>
public sealed class SvgViewerDeclarationCommands
{
    private readonly Func<string, string, Func<SvgSourceDocument, string?>, bool> _write;
    private readonly Func<IReadOnlyList<SvgViewerParameter>> _rows;
    private readonly Func<ISvgViewerParameterDialogService> _dialogs;

    /// <param name="write">
    /// What to do with the edit a command comes to, and where a refusal is reported. It is handed
    /// the declaration the edit is about, so a host whose rows come from several documents knows
    /// which of them to write; what the person did, for a menu to name; and the edit. It answers
    /// whether the document changed.
    /// </param>
    /// <param name="rows">
    /// The rows on show — every one of them, inherited or not. They are what a name has to avoid
    /// clashing with and what a commit reads; a function rather than a list because they are rebuilt
    /// whenever the declarations change.
    /// </param>
    /// <param name="dialogs">
    /// How to ask what to declare. Read at the moment of asking, since a host may replace it — a
    /// test does exactly that.
    /// </param>
    public SvgViewerDeclarationCommands(
        Func<string, string, Func<SvgSourceDocument, string?>, bool> write,
        Func<IReadOnlyList<SvgViewerParameter>> rows,
        Func<ISvgViewerParameterDialogService> dialogs)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _rows = rows ?? throw new ArgumentNullException(nameof(rows));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    }

    /// <summary>How often a name is used outside the document that declares it, or null.</summary>
    /// <remarks>
    /// Set by a host whose documents declare on one another's behalf — a Svg.Studio group, whose
    /// drawings are where its parameters are used. Removal is the only command that asks.
    /// </remarks>
    public Func<string, int>? UsedElsewhere { get; set; }

    /// <summary>The expression rows on show, for a name that has to clear theirs as well.</summary>
    /// <remarks>
    /// Beside the parameters the constructor takes rather than among them: those are what a commit
    /// reads and what <see cref="SetDefaults"/> groups by document, and only a name has to clear
    /// both kinds at once — the language keeps one set across them. Null answers that there are
    /// none, which is a host showing no expressions.
    /// </remarks>
    public Func<IReadOnlyList<SvgViewerLet>>? Lets { get; set; }

    /// <summary>What a let's body may name where it sits, or at the end of the list for null.</summary>
    /// <remarks>
    /// Asked of the panel rather than worked out here: it is the walk the rows are already checked
    /// by, and a second copy would be a second answer about what a body may name.
    /// </remarks>
    public Func<SvgViewerLet?, IReadOnlyDictionary<string, ExprType>>? Scope { get; set; }

    /// <summary>What holds a declaration, for a host whose rows come from several documents.</summary>
    /// <remarks>
    /// Only <see cref="SetDefaults"/> asks, because it is the one command about several rows at
    /// once and has to make one write per document rather than one for the lot. Null answers that
    /// every row is held in the same place, which is the ordinary case.
    /// </remarks>
    public Func<string, object?>? Holder { get; set; }

    /// <summary>Asks for a parameter and writes it where the declarations live.</summary>
    public async Task<bool> AddAsync(TopLevel? owner)
    {
        var taken = _rows().Select(row => row.Name).ToList();

        var parameter = await _dialogs()
            .AskAsync(owner, taken)
            .ConfigureAwait(true);

        return parameter is { } declared
               && _write(declared.Name, $"add {declared.Name}", source => SvgDeclarationEditor.Add(source, declared));
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
            && _write(parameter.Name, $"change {parameter.Name}", source => SvgDeclarationEditor.Update(source, parameter.Name, wanted));
    }

    /// <summary>Asks for an expression variable to declare, and answers what was asked for.</summary>
    /// <remarks>
    /// Asking only, unlike <see cref="AddAsync"/>. What follows differs by host: a body given with
    /// the name is written, one left out starts the row that will write it — and a host that has to
    /// ask where a declaration goes asks that of the row rather than of this.
    /// </remarks>
    /// <returns>What was asked for, or null where nobody wanted one.</returns>
    public Task<SvgExpressionLet?> AskLetAsync(TopLevel? owner)
        => _dialogs().AskLetAsync(owner, Taken(null), Scope?.Invoke(null) ?? Nothing, null);

    /// <summary>Asks what one expression variable should say, and writes the answer.</summary>
    /// <remarks>
    /// A rename is an edit everywhere the drawing names it, which <see cref="CommitLet"/> already
    /// carries out through <c>UpdateLet</c>. Refused, the row goes back to what the document says:
    /// a row saying something the drawing does not is worse than an edit that did not land, which
    /// is the answer a refused reorder already gets.
    /// </remarks>
    public async Task<bool> EditLetAsync(TopLevel? owner, SvgViewerLet let)
    {
        if (let is null)
        {
            throw new ArgumentNullException(nameof(let));
        }

        // A row nobody has written yet declares nothing to change, and holds everything it has in
        // the open. The panel offers no button on one for the same reason.
        if (let.Declaration is not { } declared)
        {
            return false;
        }

        var replacement = await _dialogs()
            .AskLetAsync(owner, Taken(let), Scope?.Invoke(let) ?? Nothing, declared)
            .ConfigureAwait(true);

        if (replacement is not { } wanted)
        {
            return false;
        }

        let.Name = wanted.Name;
        let.Expression = wanted.Expression;

        if (CommitLet(let))
        {
            return true;
        }

        let.Revert();

        return false;
    }

    /// <summary>The names already spoken for, <paramref name="except"/>'s own not among them.</summary>
    private IReadOnlyCollection<string> Taken(SvgViewerLet? except)
        => _rows()
            .Select(row => row.Name)
            .Concat((Lets?.Invoke() ?? Array.Empty<SvgViewerLet>())
                .Where(row => !ReferenceEquals(row, except))
                .Select(row => row.Name.Trim()))
            .Where(name => name.Length > 0)
            .ToList();

    /// <summary>Nothing in scope, for a host that does not say what is.</summary>
    private static IReadOnlyDictionary<string, ExprType> Nothing { get; } = new Dictionary<string, ExprType>();

    /// <summary>Takes one parameter out.</summary>
    public bool Remove(SvgViewerParameter parameter)
    {
        if (parameter is null)
        {
            throw new ArgumentNullException(nameof(parameter));
        }

        return _write(parameter.Name, $"remove {parameter.Name}", source => SvgDeclarationEditor.Remove(source, parameter.Name, UsedElsewhere));
    }

    /// <summary>Writes every value somebody chose in as the declared default.</summary>
    /// <remarks>
    /// One call for the lot, so a session of moving sliders is one thing to take back. Only rows
    /// that differ are written, so committing twice does nothing the second time.
    /// </remarks>
    public bool SetDefaults()
    {
        var wrote = false;

        // One write per document rather than one for the lot: a panel showing what a drawing
        // declares beside what it inherits has rows from several, and SetDefaults refuses a name the
        // document it is given does not declare.
        foreach (var held in _rows().Where(row => row.IsModified).GroupBy(row => Holder?.Invoke(row.Name)))
        {
            var changed = held.ToDictionary(row => row.Name, row => row.ToExpression(), StringComparer.Ordinal);

            wrote |= _write(
                held.First().Name,
                "keep these values",
                source => SvgDeclarationEditor.SetDefaults(source, changed));
        }

        return wrote;
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
            ? _write(declared.Name, $"change {declared.Name}", source => SvgDeclarationEditor.UpdateLet(source, declared.Name, name, expression))
            : _write(name, $"add {name}", source => SvgDeclarationEditor.AddLet(source, name, expression));
    }

    /// <summary>Moves a let to <paramref name="to"/> among the lets.</summary>
    public bool MoveLet(SvgViewerLet let, int to)
    {
        if (let is null)
        {
            throw new ArgumentNullException(nameof(let));
        }

        return let.Declaration is { } declared
               && _write(declared.Name, $"move {declared.Name}", source => SvgDeclarationEditor.MoveLet(source, declared.Name, to));
    }

    /// <summary>Takes one let out.</summary>
    public bool RemoveLet(SvgViewerLet let)
    {
        if (let is null)
        {
            throw new ArgumentNullException(nameof(let));
        }

        return let.Declaration is { } declared
               && _write(declared.Name, $"remove {declared.Name}", source => SvgDeclarationEditor.RemoveLet(source, declared.Name, UsedElsewhere));
    }

    /// <summary>Moves a parameter to <paramref name="to"/> among the parameters.</summary>
    public bool MoveParameter(SvgViewerParameter parameter, int to)
    {
        if (parameter is null)
        {
            throw new ArgumentNullException(nameof(parameter));
        }

        return _write(parameter.Name, $"move {parameter.Name}", source => SvgDeclarationEditor.MoveParameter(source, parameter.Name, to));
    }
}
