// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;

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
    public const string Namespace = "https://svg.skia/expr/1.0";

    private const string Open = "{{";

    private const string Close = "}}";

    // The placeholder has to survive the model's own short circuits, which branch on the value:
    // "none" would drop the paint entirely and an opacity of 1 would skip creating a layer, and
    // in either case there would be nothing left for the expression to attach to.
    private static readonly Dictionary<string, string> s_placeholders = new(StringComparer.Ordinal)
    {
        ["fill"] = "#808080",
        ["stroke"] = "#808080",
        ["stop-color"] = "#808080",
        ["opacity"] = "1",
        // A hidden element contributes no commands at all, so the placeholder has to be the
        // visible state or there would be nothing left to make conditional.
        ["visibility"] = "visible"
    };

    /// <summary>Attributes an expression can currently drive.</summary>
    public static bool IsSupported(string localName) => s_placeholders.ContainsKey(localName);

    public static string KeyFor(string localName) => Namespace + ":" + localName;

    public static string PlaceholderFor(string localName)
        => s_placeholders.TryGetValue(localName, out var placeholder) ? placeholder : "#808080";

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
