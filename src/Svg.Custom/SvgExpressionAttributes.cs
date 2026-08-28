// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
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
    // The type is here rather than in a table of its own because both answers are about the same
    // attributes, and two tables would be two places to add the next one.
    private static readonly Dictionary<string, (string Placeholder, ExprType Type)> s_placeholders = new(StringComparer.Ordinal)
    {
        ["fill"] = ("#808080", ExprType.Color),
        ["stroke"] = ("#808080", ExprType.Color),
        ["stop-color"] = ("#808080", ExprType.Color),
        ["opacity"] = ("1", ExprType.Number),
        // Fully opaque, so the colour the expression scales is the one the author wrote.
        ["fill-opacity"] = ("1", ExprType.Number),
        ["stroke-opacity"] = ("1", ExprType.Number),
        ["stop-opacity"] = ("1", ExprType.Number),
        // A hidden element contributes no commands at all, so the placeholder has to be the
        // visible state or there would be nothing left to make conditional. For display that goes
        // further: a display:none container is not compiled at all, subtree included.
        ["visibility"] = ("visible", ExprType.Boolean),
        ["display"] = ("inline", ExprType.Boolean)
    };

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
    /// </remarks>
    public static string? WhyUnsupported(string localName)
        => IsSupported(localName)
            ? null
            : $"'{localName}' does not take an expression. The parser lifts {string.Join(", ", Supported)}, and reads a {Open} … {Close} written anywhere else as an ordinary value.";

    public static string KeyFor(string localName) => Namespace + ":" + localName;

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
