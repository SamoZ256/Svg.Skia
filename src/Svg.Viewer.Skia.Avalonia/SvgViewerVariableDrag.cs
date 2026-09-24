// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using Avalonia.Input;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>A variable being dragged from the variables panel onto an attribute.</summary>
/// <remarks>
/// Here rather than on either panel because both ends need the same answer and neither owns the
/// other: the variables are listed by <see cref="SvgViewerDeclarationPanel"/> and the attributes by
/// <see cref="SvgViewerElementPanel"/>, and they meet only in the strip that holds them both.
///
/// The name travels in the payload, where a row dragged inside one control travels as a bare marker
/// with the row itself kept in a field. A field on the panel that started this is not something the
/// panel that ends it can read.
/// </remarks>
public static class SvgViewerVariableDrag
{
    /// <summary>What a dragged variable carries: the name of it.</summary>
    public static readonly DataFormat<string> Format = DataFormat.CreateStringApplicationFormat("SvgViewerVariable");

    /// <summary>The variable a drag is carrying, or null where it is carrying something else.</summary>
    public static string? Carried(DragEventArgs e)
        => e?.DataTransfer is { } carried && carried.Contains(Format)
            ? carried.TryGetValue(Format) is { Length: > 0 } name ? name : null
            : null;

    /// <summary>The attribute value that binds it to <paramref name="name"/>.</summary>
    /// <remarks>
    /// Spaced inside the braces the way every drawing in the repository writes one, and the way the
    /// panel's own help text does. The parser trims, so the spaces are for whoever opens the file.
    /// </remarks>
    public static string Bound(string name) => "{{ " + name + " }}";
}
