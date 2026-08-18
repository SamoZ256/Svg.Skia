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
    /// <para>
    /// A renderer that wants to evaluate a document's expressions needs its parameters: the types are
    /// what let an expression be checked at all, and the defaults are what stands in for a value the
    /// host has not supplied. Reading them here means every route into a document has them, including
    /// <c>Load(XmlReader)</c> and being handed an already-parsed <see cref="SvgDocument"/>, neither of
    /// which ever had source text to re-parse.
    /// </para>
    /// <para>
    /// <see cref="SvgExpressionDeclarations.Parse"/> works from source text because a foreign
    /// element's namespace is not visible outside this assembly, so an unqualified <c>param</c> could
    /// belong to anyone. In here it is visible, so the tree is enough. Both go through
    /// <see cref="SvgExpressionDeclarations.Builder"/> and so enforce the same rules.
    /// </para>
    /// <para>
    /// Computed on each call rather than cached, because the tree is mutable and a cached answer
    /// would go stale the moment an editor added a parameter. Callers that need it per frame should
    /// hold the result.
    /// </para>
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
