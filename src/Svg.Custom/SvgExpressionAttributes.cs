// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Svg.Expressions;

namespace Svg;

/// <summary>
/// Inline expressions in presentation attributes:
///
///   &lt;circle fill="{{ hsl(hue, 74%, 55%) }}" opacity="{{ fade }}" /&gt;
///
/// The parser lifts the text out of the braces into <see cref="SvgElement.CustomAttributes"/>
/// and substitutes a placeholder so the rest of the pipeline sees a well formed value. The
/// placeholder is never shown: code generators emit the expression in its place.
/// </summary>
public static class SvgExpressionAttributes
{
    /// <summary>Key namespace, so lifted values sit alongside foreign-namespace attributes.</summary>
    /// <remarks>
    /// The same constant the declarations use — the lifted attributes and the <c>&lt;e:code&gt;</c>
    /// block are one extension, and two spellings of its namespace could drift.
    /// </remarks>
    public const string Namespace = SvgExpressionDeclarations.Namespace;

    private const string Open = "{{";

    private const string Close = "}}";

    // The placeholder has to survive the model's own short circuits, which branch on the value:
    // "none" would drop the paint entirely and an opacity of 1 would skip creating a layer, and
    // in either case there would be nothing left for the expression to attach to.
    // The type is here rather than in a table of its own because all three answers are about the
    // same attributes, and another table would be another place to add the next one.
    //
    // Inherited is the property's own answer from SVG 1.1, not a choice: where the value travels
    // down the tree the expression has to travel with it, or a child would paint the placeholder
    // its parent's expression was standing in for.
    private static readonly Dictionary<string, (string Placeholder, ExprType Type, bool Inherited)> s_placeholders = new(StringComparer.Ordinal)
    {
        ["fill"] = ("#808080", ExprType.Color, true),
        ["stroke"] = ("#808080", ExprType.Color, true),
        ["stop-color"] = ("#808080", ExprType.Color, false),
        ["flood-color"] = ("#808080", ExprType.Color, false),
        ["lighting-color"] = ("#808080", ExprType.Color, false),
        // Not inherited: a group's opacity is applied to the group as a layer, and applying it
        // again per child would compound it.
        ["opacity"] = ("1", ExprType.Number, false),
        // Fully opaque, so the colour the expression scales is the one the author wrote.
        ["fill-opacity"] = ("1", ExprType.Number, true),
        ["stroke-opacity"] = ("1", ExprType.Number, true),
        ["stop-opacity"] = ("1", ExprType.Number, false),
        // A hidden element contributes no commands at all, so the placeholder has to be the
        // visible state or there would be nothing left to make conditional. For display that goes
        // further: a display:none container is not compiled at all, subtree included.
        //
        // Neither needs to inherit here: the conditional wraps everything the node contributes,
        // its subtree included, so a group's answer already covers its children.
        ["visibility"] = ("visible", ExprType.Boolean, false),
        ["display"] = ("inline", ExprType.Boolean, false)
    };

    /// <summary>Whether a value written in <paramref name="localName"/> is inherited by children.</summary>
    public static bool IsInherited(string localName)
        => s_placeholders.TryGetValue(localName, out var supported) && supported.Inherited;

    /// <summary>Attributes an expression can currently drive.</summary>
    public static bool IsSupported(string localName) => s_placeholders.ContainsKey(localName);

    /// <summary>The attributes an expression can drive.</summary>
    /// <remarks>
    /// Read off the table above rather than written out again, so adding a placeholder cannot leave
    /// a second list saying the extension lifts something else.
    /// </remarks>
    public static IReadOnlyList<string> Supported { get; } = new List<string>(s_placeholders.Keys);

    /// <summary>
    /// Why an expression written in <paramref name="localName"/> will do nothing, or null where it
    /// will work.
    /// </summary>
    /// <remarks>
    /// The extension's own rule, said out loud. An unlifted attribute keeps its braces, so the value
    /// reaching the parser is <c>{{ w }}</c> and every converter refuses it -- which reads as a
    /// malformed number rather than as the real answer, that this attribute takes no expression at
    /// all. Being told which attributes do is the useful half.
    ///
    /// The second sentence is why the list is that list, and answers the attribute people reach for
    /// next. Binding a value re-evaluates the recorded drawing rather than compiling it again, so an
    /// expression can only drive something the drawing still holds when it is done: a paint, an
    /// alpha, whether a node was drawn. A font or a layout property is read long before that -- the
    /// text is measured with it and the positions are baked -- so substituting one afterwards would
    /// draw the new value at the old value's positions.
    /// </remarks>
    public static string? WhyUnsupported(string localName)
        => IsSupported(localName)
            ? null
            : $"'{localName}' does not take an expression. The parser lifts {string.Join(", ", Supported)} -- what a drawing can still change once it has been recorded -- and reads a {Open} … {Close} written anywhere else as an ordinary value. A font or a layout property is read before the drawing is recorded, and text is measured with it, so it cannot vary.";

    public static string KeyFor(string localName) => Namespace + ":" + localName;

    /// <summary>Where a lifted expression came from, so a weaker declaration cannot overwrite it.</summary>
    /// <remarks>
    /// Beside the expression rather than encoded into it, and in the same collection, so it travels
    /// wherever the lifted value does — a clone, a style snapshot, the JavaScript DOM state.
    /// </remarks>
    public static string SourceKeyFor(string localName) => KeyFor(localName) + ":from";

    /// <summary>
    /// Records what a declaration of <paramref name="localName"/> carries — an expression, or a
    /// literal that leaves nothing to evaluate — for a declaration of <paramref name="specificity"/>.
    /// </summary>
    /// <remarks>
    /// The cascade decides, not the order attributes happen to be read in: a <c>style</c>
    /// declaration beats a presentation attribute whichever comes first in the file, and a literal
    /// that wins has to take a weaker expression down with it or the drawing would paint an
    /// expression CSS says is not in play. Equal strength lets the later one through, as CSS does.
    /// </remarks>
    public static void Lift(
        IDictionary<string, string> customAttributes,
        string localName,
        string? expression,
        int specificity)
    {
        if (customAttributes is null)
        {
            throw new ArgumentNullException(nameof(customAttributes));
        }

        var sourceKey = SourceKeyFor(localName);

        if (customAttributes.TryGetValue(sourceKey, out var applied)
            && int.TryParse(applied, NumberStyles.Integer, CultureInfo.InvariantCulture, out var strength)
            && strength > specificity)
        {
            return;
        }

        var key = KeyFor(localName);

        if (expression is null)
        {
            customAttributes.Remove(key);
        }
        else
        {
            customAttributes[key] = expression;
        }

        customAttributes[sourceKey] = specificity.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Drops what declarations of <paramref name="specificity"/> lifted, before they are applied
    /// again.
    /// </summary>
    /// <remarks>
    /// A style attribute is re-applied whole when script or the dynamic-style pass rewrites it, and
    /// a property it no longer mentions has to lose the expression it used to carry — nothing else
    /// would ever clear it.
    /// </remarks>
    public static void Forget(IDictionary<string, string> customAttributes, int specificity)
    {
        if (customAttributes is null)
        {
            throw new ArgumentNullException(nameof(customAttributes));
        }

        var strength = specificity.ToString(CultureInfo.InvariantCulture);

        foreach (var localName in Supported)
        {
            var sourceKey = SourceKeyFor(localName);

            if (customAttributes.TryGetValue(sourceKey, out var applied) && applied == strength)
            {
                customAttributes.Remove(KeyFor(localName));
                customAttributes.Remove(sourceKey);
            }
        }
    }

    public static string PlaceholderFor(string localName)
        => s_placeholders.TryGetValue(localName, out var supported) ? supported.Placeholder : "#808080";

    /// <summary>What an expression in <paramref name="localName"/> has to evaluate to, or null.</summary>
    /// <remarks>
    /// Not a rule of the language but of where the language is used: an <c>opacity</c> is a number
    /// because it scales an alpha, a <c>fill</c> is a colour because it is one. Both back ends
    /// already check this as they read the document -- the emitter through
    /// <c>SymCSharpEmitter</c> and the renderer through <c>SvgSceneSymEvaluator</c> -- so saying it
    /// here as well only moves the same answer earlier.
    /// </remarks>
    public static ExprType? TypeFor(string localName)
        => s_placeholders.TryGetValue(localName, out var supported) ? supported.Type : null;

    /// <summary>Returns true when <paramref name="value"/> is <c>{{ ... }}</c>, yielding the inside.</summary>
    public static bool TryUnwrap(string? value, out string expression)
    {
        expression = string.Empty;

        if (value is null)
        {
            return false;
        }

        var trimmed = value.Trim();

        if (trimmed.Length < Open.Length + Close.Length ||
            !trimmed.StartsWith(Open, StringComparison.Ordinal) ||
            !trimmed.EndsWith(Close, StringComparison.Ordinal))
        {
            return false;
        }

        var inner = trimmed.Substring(Open.Length, trimmed.Length - Open.Length - Close.Length).Trim();

        if (inner.Length == 0)
        {
            return false;
        }

        expression = inner;
        return true;
    }
}
