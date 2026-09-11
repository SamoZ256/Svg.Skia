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
/// element — as spans into the text it was read from.
/// </summary>
/// <remarks>
/// Three attributes are the whole of a resize, which is why this is an edit and not a rewrite: a
/// document regenerated from a parsed tree comes back without the author's formatting, attribute
/// order or comments, and a resize has no business touching any of them. What the three should say
/// is not decided here — <c>SvgSceneSizing</c> works that out from the document and the size asked
/// for, and this writes down its answer.
/// </remarks>
public static class SvgFrameEditor
{
    /// <summary>
    /// Sets the frame on the root element. A value of null leaves that attribute as it is.
    /// </summary>
    public static SvgSourceEditResult SetFrame(string svgText, string? width, string? height, string? viewBox)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        // The declarations are not this edit's business, so a fault in them does not stop it: a
        // drawing with a broken <e:code> block still has a size somebody may want to change.
        if (!SvgDeclarationEditor.Open(svgText, out var document, out var positions, out var refusal, declarationsMustBeValid: false))
        {
            return SvgSourceEditResult.Refuse(refusal!);
        }

        if (document!.Root is not { } root)
        {
            return SvgSourceEditResult.Refuse("This drawing has no root element to resize.");
        }

        var edits = new List<SvgTextEdit>();

        Write(svgText, root, positions, "width", width, edits);
        Write(svgText, root, positions, "height", height, edits);
        Write(svgText, root, positions, "viewBox", viewBox, edits);

        // Two inserts land at the same position, and applying them back to front would reverse the
        // order they were asked for; a stable sort keeps width before height.
        edits.Sort((left, right) => left.Position.CompareTo(right.Position));

        return edits.Count == 0 ? SvgSourceEditResult.Nothing : SvgSourceEditResult.From(edits);
    }

    /// <inheritdoc cref="SetFrame(string, string?, string?, string?)"/>
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

    private static void Write(
        string svgText,
        XElement root,
        SvgExpressionDeclarations.Positions positions,
        string attributeName,
        string? value,
        List<SvgTextEdit> edits)
    {
        if (value is null)
        {
            return;
        }

        if (SvgDeclarationEditor.Write(svgText, root, positions, attributeName, value) is { } edit)
        {
            edits.Add(edit);
        }
    }
}
