// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Threading.Tasks;
using Avalonia.Controls;
using Svg.Skia;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>How the viewer asks what size a drawing should be.</summary>
/// <remarks>
/// An interface for the reason the file picker and the parameter form are: a modal is the part of
/// the viewer a test cannot drive. A host that already knows the size calls
/// <see cref="SvgViewer.Resize"/> and never comes here.
/// </remarks>
public interface ISvgViewerResizeDialogService
{
    /// <summary>
    /// Asks for the size to resize to, or null if nobody wanted one after all.
    /// </summary>
    /// <param name="owner">What the form is shown over.</param>
    /// <param name="resize">
    /// The drawing's size, to start from and to take every ratio against. The form fills it in as
    /// it is edited, and what it holds when the form closes is the answer.
    /// </param>
    Task<SvgSizeRequest?> AskAsync(TopLevel? owner, SvgViewerResize resize);
}
