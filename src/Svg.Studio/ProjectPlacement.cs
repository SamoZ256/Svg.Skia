// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Svg.Expressions;
using Svg.SourceEditing;

namespace Svg.Studio;

/// <summary>Moving each declaration up to where the drawings that hold it share it.</summary>
/// <remarks>
/// <para>
/// A converted document arrives with every drawing carrying its own copy of what it uses — that is
/// all a folder of files can express, and it is what PaintCode's importer writes. In a project there
/// is somewhere better to put it: a name two drawings in a group share belongs to the group, and one
/// two groups share belongs to the project. Then a slider moves the family rather than one drawing,
/// which is the whole reason a group can declare.
/// </para>
/// <para>
/// Nothing here is about PaintCode. It reads the blocks the drawings already carry and redistributes
/// them, so any project whose drawings declare the same thing over and over can be tidied the same
/// way.
/// </para>
/// </remarks>
public static class ProjectPlacement
{
    private static XNamespace Ns => SvgExpressionDeclarations.Namespace;

    /// <summary>What one name is declared as, and by which drawings.</summary>
    private sealed class Declared
    {
        private readonly List<ProjectDrawing> _drawings = new();

        internal Declared(XElement element)
        {
            Element = element;
        }

        /// <summary>The declaration as the first drawing to hold it wrote it.</summary>
        internal XElement Element { get; }

        internal IReadOnlyList<ProjectDrawing> Drawings => _drawings;

        /// <summary>Whether every drawing holding this name declares the same thing by it.</summary>
        /// <remarks>
        /// Two drawings that mean different things by one name are not one declaration, and hoisting
        /// either would silently change what the other draws. A converted document cannot produce
        /// that — every copy is narrowed from one library — but a project somebody has edited can.
        /// </remarks>
        internal bool Agree { get; private set; } = true;

        internal bool IsLet => Element.Name == Ns + "let";

        internal void Add(ProjectDrawing drawing, XElement element)
        {
            _drawings.Add(drawing);

            Agree &= Same(Element, element);
        }
    }

    /// <summary>
    /// Moves what the drawings share to the group or the project that holds them.
    /// </summary>
    /// <param name="organize">
    /// Whether a name is placed where it is shared, or all of them on the project. Off is the flat
    /// answer: everything in one place, which is where somebody who does not want this looks first.
    /// </param>
    public static void Place(ProjectRoot root, bool organize)
    {
        if (root is null)
        {
            throw new ArgumentNullException(nameof(root));
        }

        var drawings = root.Drawings.ToList();
        var found = Found(drawings);
        var targets = new Dictionary<string, ProjectNode>(StringComparer.Ordinal);

        foreach (var pair in found)
        {
            if (!pair.Value.Agree)
            {
                continue;
            }

            var target = organize ? Common(pair.Value.Drawings) : root;

            // A name one drawing holds alone has nowhere to go: the drawing is already where it is
            // shared. Off, the project is the answer even for that one.
            if (target is ProjectGroup)
            {
                targets[pair.Key] = target;
            }
        }

        Close(found, targets);

        if (targets.Count == 0)
        {
            return;
        }

        Write(found, targets);
        Strip(drawings, targets);
    }

    /// <summary>Every name the drawings declare, with what it says and who says it.</summary>
    private static Dictionary<string, Declared> Found(IReadOnlyList<ProjectDrawing> drawings)
    {
        var found = new Dictionary<string, Declared>(StringComparer.Ordinal);

        foreach (var drawing in drawings)
        {
            foreach (var element in Declarations(drawing))
            {
                if ((string?)element.Attribute("name") is not { } name)
                {
                    continue;
                }

                if (!found.TryGetValue(name, out var declared))
                {
                    found[name] = declared = new Declared(element);
                }

                declared.Add(drawing, element);
            }
        }

        return found;
    }

    /// <summary>
    /// Raises every name a hoisted let reads to at least where the let landed.
    /// </summary>
    /// <remarks>
    /// The blocks merge outermost first, so a declaration has to sit at or above everything that
    /// names it. A let moved to a group whose input stayed on a drawing would be a let reading a
    /// name declared after it, which the extension refuses — so the input comes up with it.
    ///
    /// A let whose input cannot be raised at all, because the drawings declare that input
    /// differently, does not move either. Iterated because raising one input can raise a let that
    /// reads it, and so on up.
    /// </remarks>
    private static void Close(Dictionary<string, Declared> found, Dictionary<string, ProjectNode> targets)
    {
        for (var settling = true; settling;)
        {
            settling = false;

            foreach (var name in targets.Keys.ToList())
            {
                if (!found.TryGetValue(name, out var declared) || !declared.IsLet)
                {
                    continue;
                }

                if (!targets.TryGetValue(name, out var landed))
                {
                    continue;
                }

                foreach (var read in SvgDeclarationEditor.Names(declared.Element.Value) ?? Array.Empty<string>())
                {
                    if (!found.ContainsKey(read))
                    {
                        // A function, a constant, or something the drawings do not declare at all.
                        continue;
                    }

                    if (!targets.TryGetValue(read, out var at))
                    {
                        targets.Remove(name);
                        settling = true;

                        break;
                    }

                    if (Common(at, landed) is { } raised && !ReferenceEquals(raised, at))
                    {
                        targets[read] = raised;
                        settling = true;
                    }
                }
            }
        }
    }

    /// <summary>Writes each target's block, keeping whatever it already declared.</summary>
    private static void Write(Dictionary<string, Declared> found, Dictionary<string, ProjectNode> targets)
    {
        foreach (var target in targets.Values.Distinct())
        {
            var group = (ProjectGroup)target;
            var names = targets.Where(pair => ReferenceEquals(pair.Value, group)).Select(pair => pair.Key).ToList();

            var block = new XElement(Ns + "code", new XAttribute(XNamespace.Xmlns + "e", Ns.NamespaceName));

            foreach (var element in Held(group))
            {
                block.Add(new XElement(element));
            }

            foreach (var name in names.Where(name => !found[name].IsLet).OrderBy(name => name, StringComparer.Ordinal))
            {
                block.Add(Bare(found[name].Element));
            }

            // Depth first over what each reads, because a let may only name what is declared above
            // it and the order they were written in is not necessarily that order.
            var written = new HashSet<string>(StringComparer.Ordinal);

            foreach (var name in names.Where(name => found[name].IsLet).OrderBy(name => name, StringComparer.Ordinal))
            {
                Let(block, name, found, targets, group, written);
            }

            group.SetCode(Rendered(block, group));
        }
    }

    /// <summary>The block as text, written at the depth the group sits at.</summary>
    /// <remarks>
    /// Not <c>XElement.ToString</c>, which indents two spaces from column zero: the block goes into
    /// a file whose depth it knows nothing about, and <see cref="ProjectGroup.SetCode"/> takes what
    /// it is given as written. One declaration per line, which is the shape a converted document
    /// already has.
    /// </remarks>
    private static string Rendered(XElement block, ProjectGroup group)
    {
        // The block's own depth, which is one in from the group that holds it.
        var unit = group.Owner.Source.IndentUnit;
        var depth = ProjectDocument.Depth(group.Element) + unit;

        var lines = block.ToString().Split('\n');
        var text = new StringBuilder(lines[0]);

        // Serialised whole and then shifted, rather than a declaration at a time: an element written
        // on its own carries the namespace binding it needs, so every one of them came out with an
        // xmlns:e of its own beside the block's.
        foreach (var line in lines.Skip(1))
        {
            var spaces = line.Length - line.TrimStart(' ').Length;

            text.Append('\n').Append(depth);

            // XLinq indents in twos; the file may not.
            for (var level = 0; level < spaces / 2; level++)
            {
                text.Append(unit);
            }

            text.Append(line, spaces, line.Length - spaces);
        }

        return text.ToString();
    }

    private static void Let(
        XElement block,
        string name,
        Dictionary<string, Declared> found,
        Dictionary<string, ProjectNode> targets,
        ProjectGroup group,
        HashSet<string> written)
    {
        if (!written.Add(name))
        {
            return;
        }

        foreach (var read in SvgDeclarationEditor.Names(found[name].Element.Value) ?? Array.Empty<string>())
        {
            if (found.TryGetValue(read, out var needed)
                && needed.IsLet
                && targets.TryGetValue(read, out var at)
                && ReferenceEquals(at, group))
            {
                Let(block, read, found, targets, group, written);
            }
        }

        block.Add(Bare(found[name].Element));
    }

    /// <summary>Takes the moved declarations out of the drawings that held them.</summary>
    private static void Strip(IReadOnlyList<ProjectDrawing> drawings, Dictionary<string, ProjectNode> targets)
    {
        foreach (var drawing in drawings)
        {
            var text = drawing.Text;

            if (SvgSourceDocument.Read(text, out _) is not { } source)
            {
                continue;
            }

            var moved = false;

            foreach (var block in source.Document.Descendants(Ns + "code").ToList())
            {
                foreach (var element in block.Elements().ToList())
                {
                    if ((string?)element.Attribute("name") is { } name && targets.ContainsKey(name))
                    {
                        element.Remove();
                        moved = true;
                    }
                }

                if (block.Elements().Any())
                {
                    continue;
                }

                var defs = block.Parent;

                Cut(block);

                // The <defs> the importer made to hold it goes too, where it held nothing else: a
                // drawing that declares nothing should read as one.
                if (defs is { } held && held.Name.LocalName == "defs" && !held.Elements().Any())
                {
                    Cut(held);
                }
            }

            if (moved)
            {
                drawing.SetText(source.ToText());
            }
        }
    }

    /// <summary>Takes an element out, and the line it sat on with it.</summary>
    /// <remarks>
    /// Left behind, the whitespace in front of it meets the whitespace behind and the pair reads as
    /// a blank line — which is what a drawing that had its declarations taken away came out with.
    /// </remarks>
    private static void Cut(XElement element)
    {
        if (element.PreviousNode is XText before && before.Value.Trim().Length == 0)
        {
            before.Remove();
        }

        element.Remove();
    }

    /// <summary>The declarations one drawing's blocks hold.</summary>
    private static IReadOnlyList<XElement> Declarations(ProjectDrawing drawing)
        => SvgSourceDocument.Read(drawing.Text, out _) is { } source
            ? source.Document.Descendants(Ns + "code").SelectMany(block => block.Elements()).ToList()
            : Array.Empty<XElement>();

    /// <summary>What a group already declares, so writing a block does not take it away.</summary>
    private static IEnumerable<XElement> Held(ProjectGroup group)
        => group.Code is { } code ? code.Elements() : Enumerable.Empty<XElement>();

    /// <summary>The declaration without the namespace binding its own document gave it.</summary>
    private static XElement Bare(XElement element)
    {
        var copy = new XElement(element);

        foreach (var declaration in copy.DescendantsAndSelf().Attributes().Where(a => a.IsNamespaceDeclaration).ToList())
        {
            declaration.Remove();
        }

        return copy;
    }

    /// <summary>Whether two elements declare the same thing.</summary>
    private static bool Same(XElement one, XElement other)
        => one.Name == other.Name
           && string.Equals(one.Value.Trim(), other.Value.Trim(), StringComparison.Ordinal)
           && Attributes(one).SequenceEqual(Attributes(other));

    private static IEnumerable<(string Name, string Value)> Attributes(XElement element)
        => element.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration)
            .Select(attribute => (attribute.Name.LocalName, attribute.Value))
            .OrderBy(pair => pair.LocalName, StringComparer.Ordinal);

    /// <summary>The nearest node every one of <paramref name="drawings"/> sits under.</summary>
    private static ProjectNode Common(IReadOnlyList<ProjectDrawing> drawings)
    {
        ProjectNode common = drawings[0];

        for (var index = 1; index < drawings.Count; index++)
        {
            common = Common(common, drawings[index])!;
        }

        return common;
    }

    /// <summary>The nearest node both sit under, or null where they share none.</summary>
    private static ProjectNode? Common(ProjectNode one, ProjectNode other)
    {
        var above = new HashSet<ProjectNode>();

        for (var node = one; node is { }; node = node.Parent)
        {
            above.Add(node);
        }

        for (var node = other; node is { }; node = node.Parent)
        {
            if (above.Contains(node))
            {
                return node;
            }
        }

        return null;
    }
}
