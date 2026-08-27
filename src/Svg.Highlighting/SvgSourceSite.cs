// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable

namespace Svg.Highlighting;

/// <summary>Where in a document a piece of expression code was written.</summary>
/// <remarks>
/// The language scopes them differently, so what a name may refer to depends on which this is.
/// </remarks>
internal enum SvgSourceSiteKind
{
    /// <summary>A <c>{{ … }}</c> in an attribute: everything the document declares is in scope.</summary>
    Placeholder,

    /// <summary>An <c>&lt;e:let&gt;</c> body: the parameters, and the lets declared before it.</summary>
    Let,

    /// <summary>
    /// A declaration's <c>default</c>, <c>min</c>, <c>max</c> or <c>step</c>, where nothing the
    /// document declares is in scope.
    /// </summary>
    /// <remarks>
    /// A default may not name another parameter — see <see cref="Svg.Expressions.ExprEvaluator"/> —
    /// so checking one against the full table would accept what the code generator rejects.
    /// </remarks>
    Declaration,
}

/// <summary>One piece of expression code, and what it is written in.</summary>
/// <remarks>
/// <see cref="Owner"/> and <see cref="Attribute"/> are filled in for a declaration, which is what
/// places a rule about a named parameter at the attribute it is about.
/// </remarks>
internal readonly record struct SvgSourceSite(
    int Start,
    int Length,
    SvgSourceSiteKind Kind,
    string? Owner = null,
    string? Attribute = null);
