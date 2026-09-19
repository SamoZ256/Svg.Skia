// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Svg.Expressions;
using Svg.SourceEditing;

namespace Svg.Studio;

/// <summary>What a drawing is built from, once the groups above it have declared their part.</summary>
/// <remarks>
/// <para>
/// A group carries an <c>&lt;e:code&gt;</c> block and everything under it inherits it. That is done
/// by writing the blocks into the drawing on its way to being drawn, rather than by handing a
/// symbol table to the renderer: declarations reach a picture through the document alone —
/// <see cref="Svg.Skia.SKSvg.ExpressionDeclarations"/> reads the tree it compiled — so the document
/// is where they have to be.
/// </para>
/// <para>
/// The blocks are spliced in whole, outermost first, in front of whatever the drawing declares
/// itself. Both halves of the rule then come from the extension rather than from here:
/// <see cref="SvgExpressionDeclarations"/> merges every block in document order, so outermost first
/// is what the order means, and its builder refuses a name declared twice, so a drawing redeclaring
/// what its group already declares is refused rather than quietly shadowing it.
/// </para>
/// <para>
/// The drawing's own text is never touched. It is what a tab shows, edits and saves; this is what is
/// drawn, generated and exported — the split <see cref="Svg.Viewer.Skia.Avalonia.SvgViewer.Rewrite"/>
/// exists for.
/// </para>
/// </remarks>
public static class ProjectDeclarations
{
    private static XNamespace Ns => SvgExpressionDeclarations.Namespace;

    /// <summary>The blocks <paramref name="node"/> inherits, outermost first.</summary>
    /// <remarks>
    /// The node's own block is not among them, whether it is a group's or a drawing's: a group's is
    /// what it declares rather than what it is given, and a drawing's is already in its text.
    /// </remarks>
    public static IReadOnlyList<ProjectGroup> Chain(ProjectNode node)
    {
        if (node is null)
        {
            throw new ArgumentNullException(nameof(node));
        }

        var above = new List<ProjectGroup>();

        for (var parent = node.Parent; parent is { }; parent = parent.Parent)
        {
            if (parent.Code is { })
            {
                above.Add(parent);
            }
        }

        above.Reverse();

        return above;
    }

    /// <summary>What <paramref name="node"/> inherits, as text, and empty where it inherits nothing.</summary>
    /// <remarks>
    /// For deciding whether a drawing has to be built again. The blocks are small and the drawings
    /// are not, so asking this is what lets a board reuse forty pictures on a drag while still
    /// noticing the one edit that changes all of them.
    /// </remarks>
    public static string Declared(ProjectNode node)
    {
        var chain = Chain(node);

        return chain.Count == 0 ? string.Empty : string.Concat(chain.Select(group => group.CodeText));
    }

    /// <summary>What <paramref name="drawing"/> is drawn, generated and exported from.</summary>
    /// <param name="ownText">
    /// The drawing as it stands — its own text from the project, or the buffer of a tab holding it.
    /// </param>
    /// <returns>
    /// The text with the inherited blocks written in, or <paramref name="ownText"/> itself where
    /// there are none or it could not be read. Unreadable text is handed back rather than refused
    /// here: what reads it next says so far better than this could.
    /// </returns>
    public static string Built(ProjectDrawing drawing, string ownText)
    {
        if (drawing is null)
        {
            throw new ArgumentNullException(nameof(drawing));
        }

        if (ownText is null)
        {
            throw new ArgumentNullException(nameof(ownText));
        }

        var chain = Chain(drawing);

        if (chain.Count == 0)
        {
            return ownText;
        }

        if (SvgSourceDocument.Read(ownText, out _) is not { } source
            || source.Document.Root is not { } root)
        {
            return ownText;
        }

        var prefix = SvgExpressionDeclarations.NamespacePrefixFor(root, out var declared);

        if (!declared && prefix.Length > 0)
        {
            root.SetAttributeValue(XNamespace.Xmlns + prefix, Ns.NamespaceName);
        }

        var defs = Defs(source, root);
        XElement? after = null;

        foreach (var group in chain)
        {
            var block = Copied(group.Code!);

            if (after is null)
            {
                PutFirst(source, defs, block);
            }
            else
            {
                after.AddAfterSelf(new XText(Indentation(source, defs)), block);
            }

            after = block;
        }

        return source.ToText();
    }

    /// <summary>The block as it goes into somebody else's document.</summary>
    /// <remarks>
    /// The copy's own namespace declarations are dropped, because the document it is going into has
    /// just been given one. Left on, a block written under a different prefix would arrive carrying
    /// it, and the drawing would bind the extension twice — which reads back as one namespace under
    /// two names, the thing <see cref="SvgExpressionDeclarations.NamespacePrefixFor"/> exists to
    /// prevent.
    /// </remarks>
    private static XElement Copied(XElement block)
    {
        var copy = new XElement(block);

        foreach (var declaration in copy.DescendantsAndSelf().Attributes().Where(a => a.IsNamespaceDeclaration).ToList())
        {
            declaration.Remove();
        }

        return copy;
    }

    /// <summary>The document's <c>&lt;defs&gt;</c>, made at the top where it has none.</summary>
    /// <remarks>Where <c>SvgDeclarationEditor</c> and <c>SvgRecipeRewriter</c> both put a block.</remarks>
    private static XElement Defs(SvgSourceDocument source, XElement root)
    {
        if (root.Elements().FirstOrDefault(element => element.Name == root.Name.Namespace + "defs") is { } existing)
        {
            return existing;
        }

        var defs = new XElement(root.Name.Namespace + "defs");

        PutFirst(source, root, defs);

        return defs;
    }

    /// <summary>Puts an element in front of everything <paramref name="parent"/> holds.</summary>
    private static void PutFirst(SvgSourceDocument source, XElement parent, XElement placed)
    {
        var inner = Indentation(source, parent);

        if (parent.FirstNode is null)
        {
            parent.Add(new XText(inner), placed, new XText("\n" + Depth(parent)));

            return;
        }

        parent.AddFirst(new XText(inner), placed);
    }

    /// <summary>The break and indentation the children of <paramref name="element"/> sit on.</summary>
    private static string Indentation(SvgSourceDocument source, XElement element)
    {
        if (element.Elements().FirstOrDefault() is { } first
            && first.PreviousNode is XText inner
            && inner.Value.Contains("\n"))
        {
            return inner.Value;
        }

        return "\n" + Depth(element) + source.IndentUnit;
    }

    /// <summary>How far in <paramref name="element"/> itself is written, without the break.</summary>
    private static string Depth(XElement element)
    {
        var leading = element.PreviousNode is XText text && text.Value.Contains("\n") ? text.Value : "\n";

        return leading.Substring(leading.LastIndexOf('\n') + 1);
    }
}
