// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Svg.Expressions;

namespace Svg.SourceEditing;

/// <summary>
/// Writes the frame of a drawing — the <c>width</c>, <c>height</c> and <c>viewBox</c> of its root
/// element.
/// </summary>
/// <remarks>
/// Three attributes are the whole of a resize. What they should say is not decided here —
/// <c>SvgSceneSizing</c> works that out from the document and the size asked for, and this writes
/// down its answer onto the root, which is the tag in a drawing most likely to have been laid out
/// by hand.
/// </remarks>
public static class SvgFrameEditor
{
    /// <summary>
    /// Sets the frame on the root element. A value of null leaves that attribute as it is.
    /// </summary>
    /// <returns>The sentence refusing the resize, or null where it was made.</returns>
    /// <remarks>
    /// The root is the tag in a drawing most likely to have been laid out by hand, one attribute to
    /// a line, and a resize writes three of its attributes at once — so this is the first place a
    /// writer that regenerated a tag instead of writing over its values would be noticed.
    /// </remarks>
    public static string? SetFrame(SvgSourceDocument source, string? width, string? height, string? viewBox)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (source.Document.Root is not { } root)
        {
            return "This drawing has no root element to resize.";
        }

        // Null leaves the attribute alone, which is not the same as SetAttributeValue's null.
        if (width is { })
        {
            root.SetAttributeValue("width", width);
        }

        if (height is { })
        {
            root.SetAttributeValue("height", height);
        }

        if (viewBox is { })
        {
            root.SetAttributeValue("viewBox", viewBox);
        }

        return null;
    }
}
