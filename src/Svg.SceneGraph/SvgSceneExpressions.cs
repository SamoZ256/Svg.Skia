// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using ShimSkiaSharp;
using Svg;

namespace Svg.Skia;

// Reads inline expressions authored in a presentation attribute:
//
//   <circle fill="{{ hsl(hue, 74%, 55%) }}" opacity="{{ fade }}" />
//
// The SVG parser has already lifted the text out of the braces and replaced the attribute with
// a placeholder, so by this point the expression is a plain entry in CustomAttributes and the
// element paints normally. The text is not parsed here: type checking needs the symbol table
// declared in <e:code>, which only the back end sees, so the expression stays whole until then.
internal static class SvgSceneExpressions
{
    internal const string Fill = "fill";

    internal const string Stroke = "stroke";

    internal const string StopColor = "stop-color";

    internal const string Opacity = "opacity";

    internal const string FillOpacity = "fill-opacity";

    internal const string StrokeOpacity = "stroke-opacity";

    internal const string StopOpacity = "stop-opacity";

    internal const string Visibility = "visibility";

    internal static SymNode? TryGet(SvgElement? element, string localName)
    {
        if (element is null)
        {
            return null;
        }

        var attributes = element.CustomAttributes;
        if (attributes is null || attributes.Count == 0)
        {
            return null;
        }

        if (!attributes.TryGetValue(SvgExpressionAttributes.KeyFor(localName), out var text))
        {
            return null;
        }

        text = text?.Trim();

        return string.IsNullOrEmpty(text) ? null : SymNode.Source(text!);
    }

    internal static SymNode? TryGetPaint(SvgElement? element, bool forStroke)
        => TryGet(element, forStroke ? Stroke : Fill);

    internal static SymNode? TryGetPaintOpacity(SvgElement? element, bool forStroke)
        => TryGet(element, forStroke ? StrokeOpacity : FillOpacity);
}
