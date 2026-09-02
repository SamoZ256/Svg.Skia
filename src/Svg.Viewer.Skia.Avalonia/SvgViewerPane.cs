// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using Avalonia.Controls;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>One of a host's panels in the viewer's right pane, and what its tab is called.</summary>
/// <remarks>
/// What belongs there is something about the drawing the viewer has no business knowing:
/// <c>Svg.Studio</c> puts a project's say over the file in one, and the colours its recipe can
/// paint in another, beside what the file says about itself.
/// </remarks>
public sealed class SvgViewerPane
{
    public SvgViewerPane(string header, Control content)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Content = content ?? throw new ArgumentNullException(nameof(content));
    }

    public string Header { get; }

    public Control Content { get; }
}
