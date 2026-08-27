// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Svg.Expressions;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>Asks with a modal window, which is what a desktop host wants and a test does not.</summary>
public class SvgViewerParameterDialogService : ISvgViewerParameterDialogService
{
    public async Task<SvgExpressionParameter?> AskAsync(TopLevel? owner, IReadOnlyCollection<string> taken)
    {
        // Without a window to own it there is nowhere to show a modal, and refusing is better than
        // a dialog nothing can dismiss.
        if (owner is not Window window)
        {
            return null;
        }

        return await new SvgParameterWindow(taken).ShowDialog<SvgExpressionParameter?>(window);
    }
}
