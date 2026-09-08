// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;
using System.Globalization;
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

    internal const string FloodColor = "flood-color";

    internal const string LightingColor = "lighting-color";

    internal const string Opacity = "opacity";

    internal const string FillOpacity = "fill-opacity";

    internal const string StrokeOpacity = "stroke-opacity";

    internal const string StopOpacity = "stop-opacity";

    internal const string Visibility = "visibility";

    internal const string Display = "display";

    /// <summary>
    /// The expression driving <paramref name="localName"/> on <paramref name="element"/>, following
    /// inheritance where the property has it.
    /// </summary>
    /// <remarks>
    /// A value written on a group is resolved by walking up from the element that paints, so an
    /// expression standing in for it has to be found the same way — otherwise a child of
    /// <c>&lt;g fill="{{ tint }}"&gt;</c> paints the grey placeholder the expression was substituted
    /// with, which is what it did until this walk existed. The walk stops where the value would:
    /// at an element that declares the property itself, whose own answer is the one that reaches
    /// the child.
    /// </remarks>
    internal static SymNode? TryGet(SvgElement? element, string localName)
    {
        for (var current = element; current is { }; current = current.Parent)
        {
            if (Own(current, localName) is { } expression)
            {
                return expression;
            }

            if (!SvgExpressionAttributes.IsInherited(localName) || Declares(current, localName))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The expression this element carries itself, ignoring what it inherits.</summary>
    private static SymNode? Own(SvgElement element, string localName)
    {
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

    /// <summary>
    /// Whether this element settles <paramref name="localName"/> for itself, so nothing above it
    /// reaches the drawing.
    /// </summary>
    /// <remarks>
    /// The same test <c>SvgAttributeCollection.GetInheritedAttribute</c> makes before it walks up.
    /// An explicit <c>fill="inherit"</c> is not an exception to it: the compatibility layer resolves
    /// that keyword as the document loads and writes the computed value onto the element — the
    /// placeholder, where an expression is in play — so by the time a scene is compiled nothing is
    /// left to say the value came from above, and such a child paints the stand-in colour.
    /// </remarks>
    private static bool Declares(SvgElement element, string localName)
        => element.Attributes.ContainsKey(localName);

    /// <summary>The functions written in <c>transform</c>, or null where nothing drives it.</summary>
    /// <remarks>
    /// A direct lookup rather than the walk above: for this one name the lifted value is the whole
    /// author text rather than the code inside the braces. Anything that cannot be mapped — an
    /// argument count SVG does not allow, a literal that will not parse, a function the model has no
    /// case for — comes back null, and the element draws the stand-in rather than something invented
    /// here.
    /// </remarks>
    internal static SymMatrix? TryGetTransform(SvgElement? element)
    {
        if (element?.CustomAttributes is not { Count: > 0 } attributes ||
            SvgExpressionAttributes.Lifted(attributes, SvgTransformExpression.Name) is not { } value)
        {
            return null;
        }

        var written = SvgTransformExpression.Parse(value);

        if (!written.Any)
        {
            return null;
        }

        var transforms = new SymTransform[written.Functions.Count];

        for (var index = 0; index < transforms.Length; index++)
        {
            if (ToTransform(written.Functions[index]) is not { } transform)
            {
                return null;
            }

            transforms[index] = transform;
        }

        return new SymMatrix(transforms);
    }

    /// <summary>One function, filled out to the arity the model folds it at.</summary>
    /// <remarks>
    /// SVG's own defaults for the arguments the author left off, which are not all zero: an omitted
    /// scale factor repeats the one given, so <c>scale(2)</c> is <c>scale(2, 2)</c> and stays that
    /// way when the expression behind it is rebound.
    /// </remarks>
    private static SymTransform? ToTransform(SvgTransformExpressionFunction function)
    {
        if (!TryReadArguments(function.Arguments, out var read))
        {
            return null;
        }

        return function.Name switch
        {
            "translate" when read.Length == 1 => new SymTransform(SymTransformOp.Translate, new[] { read[0], SymNode.Zero }),
            "translate" when read.Length == 2 => new SymTransform(SymTransformOp.Translate, read),
            "scale" when read.Length == 1 => new SymTransform(SymTransformOp.Scale, new[] { read[0], read[0] }),
            "scale" when read.Length == 2 => new SymTransform(SymTransformOp.Scale, read),
            "rotate" when read.Length == 1 => new SymTransform(SymTransformOp.Rotate, new[] { read[0], SymNode.Zero, SymNode.Zero }),
            "rotate" when read.Length == 3 => new SymTransform(SymTransformOp.Rotate, read),
            "skewX" when read.Length == 1 => new SymTransform(SymTransformOp.Skew, new[] { read[0], SymNode.Zero }),
            "skewY" when read.Length == 1 => new SymTransform(SymTransformOp.Skew, new[] { SymNode.Zero, read[0] }),
            "matrix" when read.Length == 6 => new SymTransform(SymTransformOp.Matrix, read),
            _ => null
        };
    }

    private static bool TryReadArguments(IReadOnlyList<SvgTransformExpressionArgument> arguments, out SymNode[] read)
    {
        read = new SymNode[arguments.Count];

        for (var index = 0; index < read.Length; index++)
        {
            var argument = arguments[index];

            if (argument.Expression is { } expression)
            {
                read[index] = SymNode.Source(expression);

                continue;
            }

            if (!double.TryParse(argument.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var literal))
            {
                return false;
            }

            read[index] = SymNode.Literal(literal);
        }

        return true;
    }

    internal static SymNode? TryGetPaint(SvgElement? element, bool forStroke)
        => TryGet(element, forStroke ? Stroke : Fill);

    internal static SymNode? TryGetPaintOpacity(SvgElement? element, bool forStroke)
        => TryGet(element, forStroke ? StrokeOpacity : FillOpacity);
}
