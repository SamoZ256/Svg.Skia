// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Collections.Generic;
using Svg.Expressions;

namespace Svg;

public partial class SvgDocument
{
    /// <summary>
    /// The <c>&lt;e:code&gt;</c> declarations this document carries, read from the parsed tree.
    /// </summary>
    /// <remarks>
    /// Read from the tree rather than the text, which every route into a document has —
    /// <c>Load(XmlReader)</c> and an already-parsed <see cref="SvgDocument"/> never had source to
    /// re-parse. Both readers go through <see cref="SvgExpressionDeclarations.Builder"/>. Computed
    /// on each call, since the tree is mutable and an editor can add a parameter to it.
    /// </remarks>
    /// <exception cref="ExprException">
    /// A declaration is malformed — no name, no type, a name that is reserved or declared twice, or a
    /// min, max or step on a parameter that is not a number.
    /// </exception>
    public SvgExpressionDeclarations ExpressionDeclarations => ReadExpressionDeclarations(this);

    private static SvgExpressionDeclarations ReadExpressionDeclarations(SvgElement root)
    {
        var builder = new SvgExpressionDeclarations.Builder();
        var found = false;

        foreach (var block in Blocks(root))
        {
            found = true;

            foreach (var child in block.Children)
            {
                if (child is not NonSvgElement declaration
                    || declaration.ElementNamespace != SvgExpressionAttributes.Namespace)
                {
                    continue;
                }

                switch (declaration.ElementName)
                {
                    case "param":
                        builder.AddParameter(
                            Attribute(declaration, "name"),
                            Attribute(declaration, "type"),
                            Attribute(declaration, "default"),
                            minExpression: Attribute(declaration, "min"),
                            maxExpression: Attribute(declaration, "max"),
                            stepExpression: Attribute(declaration, "step"));
                        break;

                    case "let":
                        // The expression is element content, which arrives both as Content and as a
                        // content node; Content is the one that is set for a single text child.
                        builder.AddLet(Attribute(declaration, "name"), declaration.Content);
                        break;
                }
            }
        }

        return found ? builder.Build() : SvgExpressionDeclarations.Empty;
    }

    /// <summary>Every <c>&lt;e:code&gt;</c> in the tree, at any depth, in document order.</summary>
    private static IEnumerable<NonSvgElement> Blocks(SvgElement element)
    {
        if (element is NonSvgElement block
            && block.ElementNamespace == SvgExpressionAttributes.Namespace
            && block.ElementName == "code")
        {
            yield return block;

            // Nothing nests inside a code block, so its children are declarations rather than more
            // blocks.
            yield break;
        }

        foreach (var child in element.Children)
        {
            foreach (var nested in Blocks(child))
            {
                yield return nested;
            }
        }
    }

    // An unprefixed attribute on a foreign element lands in CustomAttributes under its bare local
    // name — SvgElementFactory keys it as `ns.Length == 0 ? name : $"{ns}:{name}"`.
    private static string? Attribute(NonSvgElement element, string name)
        => element.CustomAttributes.TryGetValue(name, out var value) ? value : null;
}
