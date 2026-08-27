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
}
