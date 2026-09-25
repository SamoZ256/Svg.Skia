// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Svg.Expressions;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>How the viewer asks what parameter to declare.</summary>
/// <remarks>
/// An interface for the reason the file picker is one: a modal is the other part of the viewer a
/// test cannot drive. A host that wants to ask some other way — its own form, a command line, a
/// value it already has — supplies this instead.
/// </remarks>
public interface ISvgViewerParameterDialogService
{
    /// <summary>Asks for a parameter to add, or null if nobody wanted one after all.</summary>
    /// <param name="owner">What the form is shown over.</param>
    /// <param name="taken">
    /// The names already spoken for, so the form can say so while it is being filled in rather than
    /// after it is submitted.
    /// </param>
    Task<SvgExpressionParameter?> AskAsync(TopLevel? owner, IReadOnlyCollection<string> taken);

    /// <summary>Asks what an existing parameter should say, or null if nothing should change.</summary>
    /// <remarks>
    /// Defaulted so that an implementation written before this existed still compiles, as
    /// <see cref="ISvgViewerFileDialogService.SaveSvgAsync"/> is. Refusing is the honest answer for
    /// one that cannot ask.
    /// </remarks>
    Task<SvgExpressionParameter?> EditAsync(
        TopLevel? owner,
        IReadOnlyCollection<string> taken,
        SvgExpressionParameter existing)
        => Task.FromResult<SvgExpressionParameter?>(null);

    /// <summary>Asks what an expression variable should say, or null if nothing should.</summary>
    /// <param name="scope">
    /// What a body may name where this one sits, by name and type. Handed in rather than worked out
    /// from <paramref name="taken"/>, which says only which names are spoken for: a checker told
    /// that everything is a number calls <c>mix(tint, #000000, 0.5)</c> wrong.
    /// </param>
    /// <param name="existing">What the drawing declares, or null to ask for one it does not.</param>
    /// <remarks>Defaulted for the reason <see cref="EditAsync"/> is.</remarks>
    Task<SvgExpressionLet?> AskLetAsync(
        TopLevel? owner,
        IReadOnlyCollection<string> taken,
        IReadOnlyDictionary<string, ExprType> scope,
        SvgExpressionLet? existing)
        => Task.FromResult<SvgExpressionLet?>(null);
}
