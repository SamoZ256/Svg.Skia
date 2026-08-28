// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Threading.Tasks;
using Avalonia.Controls;
using Svg.Skia;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>Asks with a modal window, which is what a desktop host wants and a test does not.</summary>
public class SvgViewerResizeDialogService : ISvgViewerResizeDialogService
{
    public async Task<SvgSizeRequest?> AskAsync(TopLevel? owner, SvgViewerResize resize)
    {
        // Without a window to own it there is nowhere to show a modal, and refusing is better than
        // a dialog nothing can dismiss.
        if (owner is not Window window)
        {
            return null;
        }

        return await new SvgResizeWindow(resize).ShowDialog<SvgSizeRequest?>(window);
    }
}
