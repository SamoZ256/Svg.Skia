// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// How the viewer asks for a file to open.
/// </summary>
/// <remarks>
/// An interface because a picker is the one part of the viewer a test cannot drive: everything else
/// is reachable by setting a property or raising an event.
/// </remarks>
public interface ISvgViewerFileDialogService
{
    Task<string?> OpenSvgAsync(TopLevel? owner);

    /// <summary>Where to write a drawing that has no file of its own, or null to abandon it.</summary>
    /// <remarks>
    /// Defaulted so an implementation written before the pane could be edited still compiles, and
    /// refuses by answering null.
    /// </remarks>
    Task<string?> SaveSvgAsync(TopLevel? owner, string? suggested) => Task.FromResult<string?>(null);
}
