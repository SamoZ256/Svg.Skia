// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions;
using Svg.SourceEditing;

namespace Svg.Studio;

/// <summary>One node of a project: the project itself, a group or a drawing.</summary>
/// <remarks>
/// <para>
/// Every setting is read and written straight through to <see cref="Element"/>, so a node has no
/// state of its own to keep in step with the document and an edit is saved by saving the document.
/// </para>
/// <para>
/// Settings come in two flavours. <c>Namespace</c> and friends are what the node itself says, or
/// null; <c>Effective…</c> is what it comes to once the groups above it have had their say, which
/// is what actually decides the build.
/// </para>
/// </remarks>
public abstract class ProjectNode
{
    private static long s_made;

    private protected ProjectNode(XElement element, ProjectGroup? parent, ProjectDocument owner)
    {
        Element = element;
        Parent = parent;
        Owner = owner;
        Sequence = Interlocked.Increment(ref s_made);

        // Whatever renames it — a setter, an undo writing the attributes back — moves it among its
        // siblings, which are kept in name order.
        Element.Changed += (_, _) => Parent?.Unsort();
    }

    /// <summary>The XML this node's settings are read from and written to.</summary>
    /// <remarks>
    /// A group's is the root of its <c>group.xml</c>, the project's of <c>studio.xml</c>. A drawing's
    /// is held apart from the drawing, and written into its <c>&lt;metadata&gt;</c> as it is saved.
    /// </remarks>
    public XElement Element { get; }

    /// <summary>The group holding this node, or null for the project itself.</summary>
    /// <remarks>
    /// Settable because a move reparents the node it is given rather than building a new one: every
    /// open tab holds its node by reference, and a replacement would leave them editing something
    /// the document no longer contains.
    /// </remarks>
    public ProjectGroup? Parent { get; internal set; }

    /// <summary>The document this belongs to.</summary>
    public ProjectDocument Owner { get; }

    /// <summary>The file or folder name this node is saved under, once it has been read or saved.</summary>
    /// <remarks>
    /// Kept while it still fits the name, so saving does not rename what somebody else named, and
    /// the <c>~2</c> that keeps two drawings of one name apart does not move between them.
    /// </remarks>
    internal string? Segment { get; set; }

    /// <summary>When this node was made, which is what orders two rows nothing else tells apart.</summary>
    internal long Sequence { get; }

    /// <summary>What this is called: the row in the tree, the tab, and the class it falls back on.</summary>
    /// <remarks>
    /// A label rather than an identifier, so nothing here makes it unique — a drawing copied three
    /// times is three rows reading the same thing, and renaming one of them is the answer.
    /// </remarks>
    public string Name
    {
        get => ProjectDocument.Attribute(Element, "name") ?? string.Empty;
        set => Element.SetAttributeValue("name", Trimmed(value));
    }

    public string? Namespace
    {
        get => ProjectDocument.Attribute(Element, "namespace");
        set => Element.SetAttributeValue("namespace", Trimmed(value));
    }

    public string? Class
    {
        get => ProjectDocument.Attribute(Element, "class");
        set => Element.SetAttributeValue("class", Trimmed(value));
    }

    public float? Width
    {
        get => SvgcProject.ParseLength(ProjectDocument.Attribute(Element, "width"), "width");
        set => Element.SetAttributeValue("width", Number(value));
    }

    public float? Height
    {
        get => SvgcProject.ParseLength(ProjectDocument.Attribute(Element, "height"), "height");
        set => Element.SetAttributeValue("height", Number(value));
    }

    public float? Scale
    {
        get => SvgcProject.ParseScale(ProjectDocument.Attribute(Element, "scale"));
        set => Element.SetAttributeValue("scale", Number(value));
    }

    /// <summary>The room to leave, as it was written. <see cref="SvgcProjectItem.Padding"/> says why.</summary>
    public string? Padding
    {
        get => ProjectDocument.Attribute(Element, "padding");
        set => Element.SetAttributeValue("padding", Trimmed(value));
    }

    /// <summary>Whether this node asks for a size of its own. <see cref="SvgcProjectItem.HasSize"/>.</summary>
    public bool HasSize => Width is { } || Height is { } || Scale is { };

    /// <summary>Where this sits on the board of the group holding it, in drawing units.</summary>
    /// <remarks>
    /// Layout, and only layout: the build never sees it, and a group is still folded into its
    /// drawings as settings rather than composed out of them.
    /// </remarks>
    public float? X
    {
        get => SvgcProject.ParseLength(ProjectDocument.Attribute(Element, "x"), "position");
        set => Element.SetAttributeValue("x", Number(value));
    }

    /// <inheritdoc cref="X" />
    public float? Y
    {
        get => SvgcProject.ParseLength(ProjectDocument.Attribute(Element, "y"), "position");
        set => Element.SetAttributeValue("y", Number(value));
    }

    /// <summary>
    /// Whether this node names a place of its own.
    /// </summary>
    /// <remarks>
    /// Deliberately not part of <see cref="HasSize"/>: a node that names a place would otherwise
    /// become its own size owner and stop inheriting the scale its group builds it at, so every
    /// drawing would drop to its natural size the moment somebody moved it.
    /// </remarks>
    public bool HasPosition => X is { } && Y is { };

    public string? EffectiveNamespace => Nearest("namespace", true);

    public string? EffectiveClass => Nearest("class", true);

    public string? EffectivePadding => Nearest("padding", true);

    public float? EffectiveWidth => SizeOwner(true)?.Width;

    public float? EffectiveHeight => SizeOwner(true)?.Height;

    public float? EffectiveScale => SizeOwner(true)?.Scale;

    /// <summary>Whether this is <paramref name="node"/>, or sits anywhere under it.</summary>
    public bool DescendsFrom(ProjectNode node)
    {
        for (ProjectNode? at = this; at is { }; at = at.Parent)
        {
            if (ReferenceEquals(at, node))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Where an effective setting comes from, for a UI that wants to say so. Null when nothing sets it.</summary>
    public ProjectNode? OwnerOf(string setting) => setting switch
    {
        "width" or "height" or "scale" => SizeOwner(true),
        // Where a thing sits is its own. A group's place is on its parent's board, in its parent's
        // coordinates, so offered as a drawing's inherited x it would be a number about somewhere else.
        "x" or "y" => null,
        _ => Ancestry(true).FirstOrDefault(node => node.Setting(setting) is { })
    };

    /// <summary>The node's own value, or null. Named rather than typed so a UI can drive one editor.</summary>
    internal string? Setting(string name) => ProjectDocument.Attribute(Element, name);

    // What the groups alone settle, with the project's own settings left out — the shape the build
    // wants, where an item names only what it overrides.
    internal string? ScopedNamespace => Nearest("namespace", false);

    internal string? ScopedPadding => Nearest("padding", false);

    internal ProjectNode? ScopedSizeOwner => SizeOwner(false);

    /// <summary>This node, then every group above it, then the project unless it is skipped.</summary>
    private IEnumerable<ProjectNode> Ancestry(bool includeProject)
    {
        for (ProjectNode? node = this; node is { }; node = node.Parent)
        {
            if (node is ProjectRoot && !includeProject)
            {
                yield break;
            }

            yield return node;
        }
    }

    /// <summary>The nearest node that names <paramref name="name"/>, and what it says.</summary>
    /// <remarks>A namespace joins what the nodes above say; a padding can start with a dot too, and does not.</remarks>
    private string? Nearest(string name, bool includeProject)
    {
        var values = Ancestry(includeProject).Select(node => node.Setting(name));

        return name == "namespace" ? SvgcProject.ResolveNamespace(values) : values.FirstOrDefault(value => value is { });
    }

    /// <summary>
    /// The nearest node that names any of width, height or scale.
    /// </summary>
    /// <remarks>
    /// The three are one setting rather than three, so the nearest node naming any of them answers
    /// for all three. Taking them singly would let an inner width join an outer scale, which is a
    /// contradiction rather than a refinement — the rule <c>svgc</c> builds by.
    /// </remarks>
    private ProjectNode? SizeOwner(bool includeProject)
        => Ancestry(includeProject).FirstOrDefault(node => node.HasSize);

    private protected static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

    // Invariant, because a project describes the same build on every machine that reads it.
    private static string? Number(float? value)
        => value?.ToString(CultureInfo.InvariantCulture);

    /// <summary>A place as the file should carry it: near enough, and readable.</summary>
    /// <remarks>
    /// A hundredth of a drawing unit is invisible at every zoom, and a project is meant to be read
    /// and diffed by whoever owns it. Asked for by whatever computes a place rather than applied by
    /// <see cref="X"/>, which would round a number somebody typed.
    /// </remarks>
    internal static float Rounded(float value) => MathF.Round(value, 2);

    /// <summary>
    /// How far a place must move to mean on <paramref name="to"/>'s board what it meant on
    /// <paramref name="from"/>'s, or null where the two boards cannot say.
    /// </summary>
    /// <remarks>
    /// Between the two boards only. Whatever they share cancels out of the difference, so the walk
    /// stops at the nearest node they have in common — which is why the project itself is reached
    /// and never read, and it can name no place to read.
    ///
    /// A link with no place of its own is what makes it null. A group nobody has placed is not a
    /// board: everything under it is drawn in the grid beside the arrangement with its own numbers
    /// ignored, so there is no coordinate to carry across.
    /// </remarks>
    internal static (float X, float Y)? Shift(ProjectGroup from, ProjectGroup to)
    {
        var shared = to.Ancestry(true).First(from.DescendsFrom);

        return Sum(from) is { } left && Sum(to) is { } right
            ? (left.X - right.X, left.Y - right.Y)
            : null;

        (float X, float Y)? Sum(ProjectNode board)
        {
            var total = (X: 0f, Y: 0f);

            foreach (var at in board.Ancestry(true).TakeWhile(one => !ReferenceEquals(one, shared)))
            {
                if (at.X is not { } x || at.Y is not { } y)
                {
                    return null;
                }

                total = (total.X + x, total.Y + y);
            }

            return total;
        }
    }
}

/// <summary>A group — a folder with a <c>group.xml</c> — and by inheritance the project itself.</summary>
public class ProjectGroup : ProjectNode
{
    private readonly List<ProjectNode> _children = new();

    private bool _unsorted;

    private protected ProjectGroup(XElement element, ProjectGroup? parent, ProjectDocument owner, SvgSourceDocument file)
        : base(element, parent, owner)
    {
        File = file;
    }

    internal static ProjectGroup Group(SvgSourceDocument file, ProjectGroup parent, ProjectDocument owner)
        => new(file.Document.Root!, parent, owner, file);

    /// <summary>The group's own file, which is what writes its settings and its code back as they were found.</summary>
    internal SvgSourceDocument File { get; }

    /// <summary>What this group holds, by name.</summary>
    /// <remarks>
    /// The folder a group is keeps no order, and an order kept in a file of its own would be the one
    /// line two people adding a drawing each both change. So the rows go by name, as the tree shows
    /// them, and the board and the build follow the same order.
    /// </remarks>
    public IReadOnlyList<ProjectNode> Children
    {
        get
        {
            if (_unsorted)
            {
                _children.Sort(Order);
                _unsorted = false;
            }

            return _children;
        }
    }

    internal void Unsort() => _unsorted = true;

    private static int Order(ProjectNode left, ProjectNode right)
    {
        var by = NaturalOrder.Instance.Compare(left.Name, right.Name);

        if (by == 0)
        {
            by = string.CompareOrdinal(left.Segment, right.Segment);
        }

        return by != 0 ? by : left.Sequence.CompareTo(right.Sequence);
    }

    /// <summary>What this group declares for everything under it, or null where it declares nothing.</summary>
    /// <remarks>
    /// Not one of <see cref="Children"/>: a block is what the group says, not a row somebody can
    /// open, move or drop something into.
    /// </remarks>
    public XElement? Code => Block("code");

    /// <summary>The block as text, and an empty one where there is none.</summary>
    /// <remarks>
    /// Never null, because what reads this is an editor that has to be able to declare the first
    /// parameter into a group that has none. The empty block binds the prefix itself rather than
    /// leaning on the project root, so a block is the same document wherever it is read from.
    /// </remarks>
    public string CodeText
        => Code is { } code
            ? Standalone(code)
            : $"<e:code xmlns:e=\"{SvgExpressionDeclarations.Namespace}\" />";

    /// <summary>Puts an edited block back, or answers why it could not be read.</summary>
    /// <remarks>
    /// A block declaring nothing is taken out rather than left empty: a group that has had its last
    /// parameter removed is a group that declares nothing, and the file should say so.
    /// </remarks>
    public string? SetCode(string codeText)
        => SetBlock(codeText ?? throw new ArgumentNullException(nameof(codeText)), "code", "A group's declarations have to be an <e:code> block.");

    /// <summary>The one <c>e:</c> block named <paramref name="name"/> this group carries, or null.</summary>
    private protected XElement? Block(string name)
        => Element.Elements().FirstOrDefault(child => child.Name == Declarations + name);

    /// <summary><see cref="SetCode"/>, for whichever block <paramref name="name"/> says.</summary>
    private protected string? SetBlock(string text, string name, string misplaced)
    {
        var read = SvgSourceDocument.Read(text.Replace("\r\n", "\n"), out var refusal);

        if (read is null)
        {
            return refusal;
        }

        if (read.Document.Root is not { } block || block.Name != Declarations + name)
        {
            return misplaced;
        }

        // Detached first, for the reason ProjectDrawing.Inline is: adding a node that still has a
        // parent copies it, and the bytes each tag was read as are annotations a copy does not carry.
        block.Remove();

        PutBlock(name, block);

        return null;
    }

    /// <summary>
    /// Puts <paramref name="block"/> in the place of the block named <paramref name="name"/>, and
    /// takes that block out where this one is null or declares nothing.
    /// </summary>
    internal void PutBlock(string name, XElement? block)
    {
        var existing = Block(name);

        if (block is null || !block.Elements().Any())
        {
            if (existing is { })
            {
                if (existing.PreviousNode is XText separator && separator.Value.Trim().Length == 0)
                {
                    separator.Remove();
                }

                existing.Remove();
            }

            return;
        }

        if (existing is { })
        {
            existing.ReplaceWith(block);
        }
        else
        {
            PutFirst(block);

            // A block the editor made from CodeText's empty one closes at column zero; its lines
            // move to where it now opens. One already written at this depth has nothing to move.
            var closing = block.LastNode is XText last ? last.Value : string.Empty;

            Reindent(block, closing.Substring(closing.LastIndexOf('\n') + 1), ProjectDocument.Depth(block));
        }
    }

    /// <summary>Puts an element in front of everything this group's file holds, on a line of its own.</summary>
    private void PutFirst(XElement element)
    {
        if (Element.Elements().FirstOrDefault() is not { } first)
        {
            AddFirst(element);

            return;
        }

        first.AddBeforeSelf(element);

        // The break and indentation the displaced element was sitting on, given back to it: inserting
        // before an element lands after the whitespace in front of it, so without this the two share
        // a line.
        if (element.PreviousNode is XText indent)
        {
            element.AddAfterSelf(new XText(indent.Value));
        }
    }

    /// <summary>The first thing this group's file holds, so there is no sibling to take a line from.</summary>
    private void AddFirst(XElement element)
    {
        var inner = new XText(Indentation(Element));

        // Whatever closes the group already sits on the right line; the new element goes in front
        // of it rather than after, which is where Add would put it.
        if (Element.LastNode is XText closing && closing.Value.Trim().Length == 0)
        {
            closing.AddBeforeSelf(inner, element);
            return;
        }

        Element.Add(inner, element, new XText(ProjectDocument.Closing(Element)));
    }

    /// <summary>The break and indentation the children of <paramref name="element"/> sit on.</summary>
    /// <remarks>
    /// The whitespace before a closing tag is no guide when an element holds nothing: it is the
    /// element's own depth, and a child written on it would come out level with its parent.
    /// </remarks>
    internal string Indentation(XElement element)
    {
        var first = element.Elements().FirstOrDefault();

        if (first is { } && first.PreviousNode is XText inner && inner.Value.Contains("\n"))
        {
            return inner.Value;
        }

        // The depth alone and not the whole break: an element written after a blank line would
        // otherwise hand that blank line to its first child, inside the element rather than before it.
        return "\n" + ProjectDocument.Depth(element) + File.IndentUnit;
    }

    /// <summary>One element of this group's file as the file writes it, readable on its own.</summary>
    /// <remarks>
    /// A prefix it uses may be bound further up — a file may bind the expression prefix once on its
    /// root — and the text alone then does not read, so such a binding is written onto its opening tag.
    /// </remarks>
    internal string Standalone(XElement element)
    {
        var text = File.TextOf(element);
        var used = element.DescendantsAndSelf()
            .SelectMany(one => one.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Name.Namespace).Append(one.Name.Namespace))
            .ToHashSet();
        var bound = element.Attributes().Where(a => a.IsNamespaceDeclaration).Select(a => a.Name.LocalName).ToHashSet();
        var bindings = new System.Text.StringBuilder();

        // Nearest first, so a prefix bound twice takes the binding in scope.
        foreach (var binding in element.Ancestors().SelectMany(one => one.Attributes()))
        {
            if (binding.Name.Namespace == XNamespace.Xmlns && bound.Add(binding.Name.LocalName) && used.Contains(XNamespace.Get(binding.Value)))
            {
                bindings.Append($" xmlns:{binding.Name.LocalName}=\"{System.Security.SecurityElement.Escape(binding.Value)}\"");
            }
        }

        if (bindings.Length == 0)
        {
            return text;
        }

        var end = 1;

        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('/' or '>'))
        {
            end++;
        }

        return text.Insert(end, bindings.ToString());
    }

    private static XNamespace Declarations => SvgExpressionDeclarations.Namespace;

    /// <summary>Adds an empty group, and hands it back to be filled in.</summary>
    public ProjectGroup AddGroup(string name)
    {
        var group = Group(ProjectDocument.ReadFile(ProjectDocument.BlankGroup, ProjectPackage.GroupFile), this, Owner);

        group.Name = Named(name);
        Attach(group);

        return group;
    }

    /// <summary>Adds a drawing holding <paramref name="svgText"/>, which inherits everything else.</summary>
    /// <remarks>
    /// The text becomes the drawing's file as it stands, prologue and all, with its lines ended the
    /// way git will check it out everywhere rather than the way the machine it was pasted on does.
    /// </remarks>
    public ProjectDrawing AddDrawing(string name, string svgText)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        var file = SvgSourceDocument.Read(svgText.Replace("\r\n", "\n"), out var refusal)
                   ?? throw new SvgcProjectException(refusal ?? "That drawing could not be read.");

        if (file.Document.Root is not { } root || root.Name.LocalName != "svg")
        {
            throw new SvgcProjectException("A drawing has to be an <svg> document.");
        }

        ProjectDrawing.Lift(root);

        var drawing = new ProjectDrawing(new XElement("drawing", new XAttribute("name", Named(name))), this, Owner, file);

        Attach(drawing);

        return drawing;
    }

    /// <summary>
    /// Adds a copy of <paramref name="source"/>, with everything under it, and hands it back.
    /// </summary>
    /// <remarks>
    /// Read back through the text it was written as rather than cloned: the bytes each tag was read
    /// as are annotations on the element, and a clone carries none of them — so a copied drawing
    /// would be the one file in the project that had been reformatted.
    /// </remarks>
    public ProjectNode Copy(ProjectNode source)
    {
        if (source is ProjectRoot)
        {
            throw new SvgcProjectException("The project itself cannot be copied.");
        }

        var copy = Copied(source, this);

        // The place came across with the settings and is about where the original sits. Two rows on
        // one spot, one of them under the other, is worse than a row waiting beside the arrangement.
        copy.X = null;
        copy.Y = null;

        return copy;

        static ProjectNode Copied(ProjectNode source, ProjectGroup into)
        {
            if (source is ProjectDrawing drawing)
            {
                var made = new ProjectDrawing(
                    new XElement(drawing.Element),
                    into,
                    into.Owner,
                    ProjectDocument.ReadFile(drawing.File.ToText(), drawing.Name));

                into.Attach(made);

                return made;
            }

            var group = (ProjectGroup)source;
            var copy = Group(ProjectDocument.ReadFile(group.File.ToText(), group.Name), into, into.Owner);

            into.Attach(copy);

            foreach (var child in group.Children.ToList())
            {
                Copied(child, copy);
            }

            return copy;
        }
    }

    /// <summary>
    /// What this group holds now, as something that puts it back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The capture every structural gesture wants — add, remove, paste, a row dragged in or out —
    /// and it is the group rather than the row because a row being added does not exist yet when the
    /// state before is taken. What was there is a list, and putting it back is putting that list
    /// back.
    /// </para>
    /// <para>
    /// The very nodes, re-attached rather than re-read, so identity survives: every open tab holds
    /// its node by reference, and a row put back as a copy would leave the tab that was open on it
    /// editing something the document no longer contains.
    /// </para>
    /// </remarks>
    internal Action Contents()
    {
        var children = _children.ToList();

        return () =>
        {
            // A row this takes out says it is nowhere, unless the group it went to has claimed it:
            // a row undone away still pointing here would read as in the project.
            foreach (var gone in _children.Except(children).Where(gone => ReferenceEquals(gone.Parent, this)))
            {
                gone.Parent = null;
            }

            _children.Clear();
            _children.AddRange(children);

            foreach (var child in children)
            {
                child.Parent = this;
            }

            Unsort();
        };
    }

    /// <summary>Takes a node out of this group, with everything under it.</summary>
    public void Remove(ProjectNode child)
    {
        if (!_children.Contains(child))
        {
            throw new SvgcProjectException("That node is not in this group.");
        }

        Detach(child);
    }

    /// <summary>Moves a node into this group.</summary>
    public void Move(ProjectNode child)
    {
        if (child is ProjectRoot || child.Parent is not { } parent)
        {
            throw new SvgcProjectException("The project itself cannot be moved.");
        }

        // Both at once: a group dropped on itself, and one dropped inside its own descendant, which
        // would take the whole branch out of the document and leave it holding itself.
        if (DescendsFrom(child))
        {
            throw new SvgcProjectException("A group cannot be moved into itself.");
        }

        if (ReferenceEquals(parent, this))
        {
            return;
        }

        Reboard(child, parent);

        parent.Detach(child);
        Attach(child);
    }

    /// <summary>Whether rows sit here and none names a place, so the board is the spread.</summary>
    internal bool IsSpread => _children.Count > 0 && !_children.Any(one => one.HasPosition);

    /// <summary>
    /// Writes a row's place in the coordinates of this board, or takes it away.
    /// </summary>
    /// <remarks>
    /// Where a row is dropped in the tree says which group holds it and nothing about where it
    /// should sit, so it keeps the spot it was already on — read in the coordinates of the board it
    /// has arrived on, which is the one thing about it that changed.
    ///
    /// Taken away in the two cases where those coordinates would say nothing. A board where rows sit
    /// and none of them names a place is a grid, and the grid begins at the board's own corner: a row
    /// arriving with a place would become the first placed row there and push every row nobody
    /// touched out to the right of it, which is a larger jump than the one this avoids, and on rows
    /// nobody dragged. And a row that has no place, or a board that cannot say where it is, has
    /// nothing to carry.
    /// </remarks>
    private void Reboard(ProjectNode child, ProjectGroup from)
    {
        if (!IsSpread && child.X is { } x && child.Y is { } y && Shift(from, this) is { } by)
        {
            child.X = Rounded(x + by.X);
            child.Y = Rounded(y + by.Y);

            return;
        }

        child.X = null;
        child.Y = null;
    }

    /// <summary>Every drawing under this node, in the order <see cref="Children"/> gives.</summary>
    public IEnumerable<ProjectDrawing> Drawings
        => Children.SelectMany(child => child switch
        {
            ProjectDrawing drawing => new[] { drawing },
            ProjectGroup group => group.Drawings,
            _ => Enumerable.Empty<ProjectDrawing>()
        });

    private static string Named(string name)
        => string.IsNullOrWhiteSpace(name) ? throw new SvgcProjectException("A drawing or group needs a name.") : name.Trim();

    /// <summary>
    /// Moves everything written inside <paramref name="element"/> from one depth to another.
    /// </summary>
    /// <remarks>
    /// The whitespace between an element's children lives inside it, so a block carried to a new
    /// depth arrives with its contents still indented for the old one. Only the lines that sat at the
    /// old depth are shifted; what was deeper keeps the extra, which is what makes a whole block move
    /// as one.
    ///
    /// Whitespace that is a line of its own, and nothing else: a text run can be the stylesheet in a
    /// <c>&lt;style&gt;</c> or the words in a <c>&lt;text&gt;</c>, and shifting those would change
    /// what a drawing says rather than where it sits.
    /// </remarks>
    internal static void Reindent(XElement element, string was, string now)
    {
        if (was == now)
        {
            return;
        }

        foreach (var text in element.DescendantNodes().OfType<XText>())
        {
            if (text is XCData || text.Value.Trim().Length > 0 || Preserved(text))
            {
                continue;
            }

            var lines = text.Value.Split('\n');

            for (var line = 1; line < lines.Length; line++)
            {
                if (lines[line].StartsWith(was, StringComparison.Ordinal))
                {
                    lines[line] = now + lines[line].Substring(was.Length);
                }
            }

            text.Value = string.Join("\n", lines);
        }
    }

    /// <summary>Whether a node sits under an <c>xml:space="preserve"</c>, where a space is content.</summary>
    private static bool Preserved(XNode node)
    {
        for (var at = node.Parent; at is { }; at = at.Parent)
        {
            if ((string?)at.Attribute(XNamespace.Xml + "space") is { } space)
            {
                return string.Equals(space, "preserve", StringComparison.Ordinal);
            }
        }

        return false;
    }

    internal void Attach(ProjectNode child)
    {
        _children.Add(child);
        child.Parent = this;
        Unsort();
    }

    private void Detach(ProjectNode child)
    {
        _children.Remove(child);
        child.Parent = null;
    }

    /// <summary>The group's file as it is saved: without a name its folder already says.</summary>
    internal string FileText()
    {
        if (this is ProjectRoot
            || Segment is not { } segment
            || Element.Attribute("name") is not { } name
            || name.Value != ProjectPackage.Named(segment, group: true))
        {
            return File.ToText();
        }

        var all = Element.Attributes().ToList();

        name.Remove();

        try
        {
            return File.ToText();
        }
        finally
        {
            Element.ReplaceAttributes(all.Select(attribute => new XAttribute(attribute)));
        }
    }

    /// <summary>Gives every row under this group the file or folder name it is saved under.</summary>
    /// <remarks>
    /// Kept names first, so a new row is the one that takes the <c>~2</c> and an old one is never
    /// renamed to make room for it.
    /// </remarks>
    internal void Place()
    {
        var children = Children.ToList();
        var taken = ProjectPackage.Reserved();

        foreach (var child in children)
        {
            if (child.Segment is { } kept && !(ProjectPackage.Fits(kept, child.Name, child is ProjectGroup) && taken.Add(kept)))
            {
                child.Segment = null;
            }
        }

        foreach (var child in children)
        {
            child.Segment ??= ProjectPackage.Segment(child.Name, taken, child is ProjectGroup);

            if (child is ProjectGroup group)
            {
                group.Place();
            }
        }

        Unsort();
    }

    /// <summary>Puts what this group holds into <paramref name="files"/>, under <paramref name="folder"/>.</summary>
    internal void Write(IDictionary<string, string> files, string folder)
    {
        foreach (var child in Children)
        {
            if (child is ProjectDrawing drawing)
            {
                files[folder + drawing.Segment] = drawing.FileText();
            }
            else if (child is ProjectGroup group)
            {
                var inner = folder + group.Segment + "/";

                files[inner + ProjectPackage.GroupFile] = group.FileText();
                group.Write(files, inner);
            }
        }
    }
}

/// <summary>One drawing: an <c>.svg</c> file of the project, and whatever it overrides.</summary>
public sealed class ProjectDrawing : ProjectNode
{
    internal ProjectDrawing(XElement element, ProjectGroup parent, ProjectDocument owner, SvgSourceDocument file)
        : base(element, parent, owner)
    {
        File = file;
    }

    /// <summary>The drawing's own file, which is what writes it back as it was found.</summary>
    internal SvgSourceDocument File { get; }

    /// <summary>The drawing itself: the file's <c>&lt;svg&gt;</c>.</summary>
    public XElement Svg => File.Document.Root!;

    /// <summary>The drawing as text, written the way its file writes it.</summary>
    public string Text => File.TextOf(Svg);

    /// <summary>The whitespace its settings were written after, so they go back exactly where they were found.</summary>
    internal string? Lead { get; set; }

    /// <summary>
    /// Puts edited text back, or answers why it could not be read.
    /// </summary>
    public string? SetText(string svgText) => Inline(svgText);

    /// <summary>Its method on the project's API class, where not <c>draw</c> and its class name.</summary>
    public string? Method
    {
        get => ProjectDocument.Attribute(Element, "method");
        set => Element.SetAttributeValue("method", Trimmed(value));
    }

    /// <summary>The parameters its API method takes, in order, as names separated by spaces.</summary>
    public string? Parameters
    {
        get => ProjectDocument.Attribute(Element, "parameters");
        set => Element.SetAttributeValue("parameters", Trimmed(value));
    }

    /// <summary>Where the drawing was imported from, such as <c>streamline:&lt;hash&gt;</c>, or null.</summary>
    public string? Source
    {
        get => ProjectDocument.Attribute(Element, "source");
        set => Element.SetAttributeValue("source", Trimmed(value));
    }

    internal string? Inline(string svgText)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        // The file's own ending is put back when it is written, so text pasted from a machine that
        // ends its lines differently cannot decide it.
        var read = SvgSourceDocument.Read(svgText.Replace("\r\n", "\n"), out var refusal);

        if (read is null)
        {
            return refusal;
        }

        if (read.Document.Root is not { } root || root.Name.LocalName != "svg")
        {
            return "A drawing has to be an <svg> document.";
        }

        Lift(root);

        // Detached first: adding a node that still has a parent copies it, and the bytes each tag
        // was read as are annotations, which a copy does not carry.
        root.Remove();
        Svg.ReplaceWith(root);

        return null;
    }

    /// <summary>The drawing's file as it is saved: its settings put into a <c>&lt;metadata&gt;</c> of its own.</summary>
    /// <remarks>
    /// A child at the end, after a blank line, rather than attributes on <c>&lt;svg&gt;</c>, whose
    /// tag a resize rewrites and the first parameter declares a namespace on. Git takes changes to
    /// neighbouring lines as one conflict, so a drawing moved on one branch and resized or drawn on
    /// on another merges only with a line nobody edits between them. Nothing at all when there is
    /// nothing to say, so a drawing imported and never touched is the file it was.
    /// </remarks>
    internal string FileText()
    {
        var stem = ProjectPackage.Named(Segment!, group: false);
        var said = Element.Attributes()
            .Where(attribute => !(attribute.Name == "name" && attribute.Value == stem))
            .Select(attribute => new XAttribute(attribute.Name, attribute.Value))
            .ToList();

        if (said.Count == 0)
        {
            return File.ToText();
        }

        var settings = new XElement(
            ProjectDocument.Studio + "drawing",
            new XAttribute("xmlns", ProjectDocument.Studio.NamespaceName),
            said);
        var holder = new XElement(Svg.Name.Namespace + "metadata", settings);
        var lead = new XText(Lead ?? "\n\n" + File.IndentUnit);

        if (Svg.LastNode is XText closing && closing.Value.Trim().Length == 0)
        {
            closing.AddBeforeSelf(lead, holder);
        }
        else
        {
            Svg.Add(lead, holder);
        }

        try
        {
            return File.ToText();
        }
        finally
        {
            lead.Remove();
            holder.Remove();
        }
    }

    /// <summary>
    /// Takes the settings Studio wrote into a drawing back out of it, and hands them over, or null.
    /// </summary>
    /// <remarks>
    /// Out of the tree rather than read in place, so the drawing an editor sees, a build reads and
    /// a parameter is declared into is the drawing alone.
    /// </remarks>
    internal static XElement? Lift(XElement svg) => Lift(svg, out _);

    /// <inheritdoc cref="Lift(XElement)" />
    internal static XElement? Lift(XElement svg, out string? lead)
    {
        lead = null;

        var holder = svg.Elements().FirstOrDefault(element =>
            element.Name.LocalName == "metadata"
            && element.Elements().Count() == 1
            && element.Elements().Single().Name == ProjectDocument.Studio + "drawing"
            && element.Nodes().All(node => node is XElement || (node is XText text && text.Value.Trim().Length == 0)));

        if (holder is null)
        {
            return null;
        }

        var settings = holder.Elements().Single();

        if (holder.PreviousNode is XText before && before.Value.Trim().Length == 0)
        {
            lead = before.Value;
            before.Remove();
        }

        holder.Remove();

        return settings;
    }
}

/// <summary>The project itself, <c>studio.xml</c>: a group that also carries the settings for the whole build.</summary>
public sealed class ProjectRoot : ProjectGroup
{
    internal ProjectRoot(SvgSourceDocument file, ProjectDocument owner)
        : base(file.Document.Root!, null, owner, file)
    {
    }

    public SvgPictureCache? Cache
    {
        get => Setting("cache") is { } value ? SvgcProject.ParseCache(value) : null;
        set => Element.SetAttributeValue("cache", value is { } cache ? CacheText(cache) : null);
    }

    public SvgHelperScope? HelperScope
    {
        get => Setting("helperScope") is { } value ? SvgcProject.ParseHelperScope(value) : null;
        set => Element.SetAttributeValue("helperScope", value is { } scope ? ScopeText(scope) : null);
    }

    public SkiaSharpTarget? SkiaSharp
    {
        get => Setting("skiaSharp") is { } value ? SvgcProject.ParseSkiaSharpTarget(value) : null;
        set => Element.SetAttributeValue("skiaSharp", value is { } target ? SkiaSharpText(target) : null);
    }

    /// <summary>The class every drawing is called through PaintCode's way, or null for none.</summary>
    public string? Api
    {
        get => Setting("api");
        set => Element.SetAttributeValue("api", Trimmed(value));
    }

    /// <summary>The host's <c>ResizingBehavior</c> enum, which gives the API class its framed overloads.</summary>
    public string? Resizing
    {
        get => Setting("resizing");
        set => Element.SetAttributeValue("resizing", Trimmed(value));
    }

    /// <summary>The type of the argument the API class takes ahead of a text drawing's own.</summary>
    public string? Context
    {
        get => Setting("context");
        set => Element.SetAttributeValue("context", Trimmed(value));
    }

    /// <summary>The positional order of an API method's parameters, as names separated by spaces.</summary>
    public string? Order
    {
        get => Setting("order");
        set => Element.SetAttributeValue("order", Trimmed(value));
    }

    public SvgBody? Body
    {
        get => Setting("body") is { } value ? SvgcProject.ParseBody(value) : null;
        set => Element.SetAttributeValue("body", value is { } body ? BodyText(body) : null);
    }

    /// <summary>The C# expression generated code finds its typefaces by, or null for the font manager.</summary>
    public string? Typeface
    {
        get => Setting("typeface");
        set => Element.SetAttributeValue("typeface", Trimmed(value));
    }

    /// <summary>The settings that shape the generated file, as one value.</summary>
    public SvgCodeStyle Style => SvgcProject.ParseStyle(
        Setting("api"),
        Setting("resizing"),
        Setting("context"),
        Setting("order"),
        Setting("body"),
        Setting("typeface"));

    /// <summary>The project's import templates, or null where it keeps none.</summary>
    /// <remarks>Not one of <see cref="ProjectGroup.Children"/>, for the reason <see cref="ProjectGroup.Code"/> is not.</remarks>
    public XElement? TemplatesBlock => Block("templates");

    /// <summary>The block as text, and an empty one where there is none; see <see cref="ProjectGroup.CodeText"/>.</summary>
    public string TemplatesText
        => TemplatesBlock is { } block
            ? Standalone(block)
            : $"<e:templates xmlns:e=\"{SvgExpressionDeclarations.Namespace}\" />";

    /// <summary>Each template's name and its text, ready for <c>SvgRecipe.Parse</c>.</summary>
    /// <remarks>
    /// Unread, because a template that does not parse is reported by whatever lists it rather than
    /// failing the project that holds it.
    /// </remarks>
    public IReadOnlyList<(string Name, string Text)> Templates
        => TemplatesBlock?.Elements()
               .Select(template => (ProjectDocument.Attribute(template, "name") ?? string.Empty, Standalone(template)))
               .ToList()
           ?? new List<(string Name, string Text)>();

    /// <summary>Puts an edited block back, or answers why it could not be read; see <see cref="ProjectGroup.SetCode"/>.</summary>
    /// <remarks>
    /// The text handed out, handed back unedited, is left alone: it may bind a prefix the file
    /// binds on <c>&lt;studio&gt;</c>, and writing it would add that declaration to the block.
    /// </remarks>
    public string? SetTemplates(string templatesText)
        => templatesText == TemplatesText
            ? null
            : SetBlock(
                templatesText ?? throw new ArgumentNullException(nameof(templatesText)),
                "templates",
                "A project's templates have to be an <e:templates> block.");

    /// <summary>A cache mode as the file spells it, which is also what the export's picker writes.</summary>
    internal static string CacheText(SvgPictureCache cache) => cache switch
    {
        SvgPictureCache.LastValue => "lastValue",
        SvgPictureCache.LastValueLocked => "lastValueLocked",
        _ => "none"
    };

    /// <summary>A helper scope as the file spells it.</summary>
    internal static string ScopeText(SvgHelperScope scope) => scope switch
    {
        SvgHelperScope.Internal => "internal",
        SvgHelperScope.PerClass => "perClass",
        _ => "file"
    };

    /// <summary>A body as the file spells it.</summary>
    internal static string BodyText(SvgBody body) => body == SvgBody.Direct ? "direct" : "record";

    /// <summary>A SkiaSharp version as the file spells it.</summary>
    internal static string SkiaSharpText(SkiaSharpTarget target) => target == SkiaSharpTarget.V3 ? "3" : "4";
}

/// <summary>
/// A Svg.Studio project: the drawings themselves and the build they describe, in one folder.
/// </summary>
/// <remarks>
/// <para>
/// The format is <c>.svgstudio</c>, a folder: <c>studio.xml</c> for the project's own settings, an
/// <c>.svg</c> per drawing, and a folder with a <c>group.xml</c> per group. Settings are attributes
/// wherever they appear, and a group hands its own down to everything under it.
/// </para>
/// <para>
/// Each file is held as an <see cref="SvgSourceDocument"/> rather than as a plain tree, which is the
/// whole reason this is not <c>SvgcProjectDocument</c> with different element names: that type
/// re-serialises, and re-serialising somebody's drawing reformats it — measured over the two suites,
/// writing a tree back the ordinary way returned 28 drawings of 2,988 unchanged.
/// </para>
/// <para>
/// <see cref="Flatten"/> is the same project as a <see cref="SvgcProject"/>, so a build here is the
/// build <c>svgc</c> runs rather than a second implementation of it.
/// </para>
/// </remarks>
public sealed class ProjectDocument
{
    /// <summary>Where a drawing's settings are written, inside its own <c>&lt;metadata&gt;</c>.</summary>
    public static readonly XNamespace Studio = "https://svg.skia/studio/1.0";

    internal const string BlankGroup = "<group />\n";

    internal const string Blank = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<studio>\n</studio>\n";

    private static readonly string[] s_settings =
    {
        "namespace", "class", "cache", "helperScope", "skiaSharp",
        "width", "height", "scale", "padding", "api", "resizing", "context", "order", "body", "typeface"
    };

    /// <summary>The expression extension, as a namespace rather than as the string it is declared as.</summary>
    /// <remarks>
    /// Because <c>SvgExpressionDeclarations.Namespace + "code"</c> is two strings joined, and the
    /// XName that comes of it holds the colon in the URI — which the reader then refuses as a name.
    /// </remarks>
    private static XNamespace Declarations => SvgExpressionDeclarations.Namespace;

    // One list for both: a drawing names what a group does, since what it draws is its file's and
    // not an attribute's.
    private static readonly string[] s_nodeAttributes =
    {
        "name", "namespace", "class", "width", "height", "scale", "padding", "x", "y"
    };

    // A drawing's alone: where it was imported from is a fact about one drawing's text, and a group
    // has no text of its own to have come from anywhere.
    private static readonly string[] s_drawingAttributes = s_nodeAttributes.Concat(new[] { "source", "method", "parameters" }).ToArray();

    /// <summary>What the folder held as last read or written, so a save writes only what changed.</summary>
    private Dictionary<string, string>? _disk;

    private ProjectDocument(SvgSourceDocument manifest, string? path)
    {
        Path = path;
        Root = new ProjectRoot(manifest, this);
    }

    public ProjectRoot Root { get; }

    /// <summary>The folder this project is, or null while it is not one yet.</summary>
    /// <remarks>
    /// Null is the ordinary state of a project nobody has saved: a conversion and a new project are
    /// both made in the window and are not named until somebody is asked where to put them.
    /// </remarks>
    public string? Path { get; private set; }

    /// <summary>The project at <paramref name="path"/>: its folder, or anything inside it.</summary>
    public static ProjectDocument Load(string path)
    {
        var folder = ProjectPackage.Find(path)
                     ?? throw new SvgcProjectException($"'{path}' is not a project. A project is a folder ending in {ProjectPackage.Extension}.");

        var files = ProjectPackage.Read(folder);
        var document = Read(files, folder);

        document._disk = files;

        return document;
    }

    /// <summary>A project holding nothing, not yet a folder.</summary>
    /// <remarks>
    /// No namespace, because the build already defaults one and a guess written into the file would
    /// have to be found and corrected rather than simply typed.
    /// </remarks>
    public static ProjectDocument Empty()
        => Read(new Dictionary<string, string> { [ProjectPackage.Manifest] = Blank }, null);

    /// <summary>The project <paramref name="files"/> hold, read as the folder at <paramref name="path"/>.</summary>
    /// <remarks>
    /// For files that are a project's but are not the project's folder — a copy of unsaved work, a
    /// commit — which is read as the project it is a copy of rather than as where it was kept.
    /// </remarks>
    public static ProjectDocument Read(IReadOnlyDictionary<string, string> files, string? path)
    {
        if (!files.TryGetValue(ProjectPackage.Manifest, out var text))
        {
            throw new SvgcProjectException($"This is not a project: it has no {ProjectPackage.Manifest}.");
        }

        var manifest = ReadFile(text, ProjectPackage.Manifest);

        if (manifest.Document.Root!.Name != "studio")
        {
            throw new SvgcProjectException(
                $"{ProjectPackage.Manifest}: the root must be <studio>, but was <{manifest.Document.Root.Name.LocalName}>.");
        }

        var document = new ProjectDocument(manifest, path);

        RequireKnownAttributes(document.Root.Element, s_settings, "studio", ProjectPackage.Manifest);
        ReadBlocks(document.Root, ProjectPackage.Manifest);

        var held = files.Keys.Where(relative => relative != ProjectPackage.Manifest && ProjectPackage.Holds(relative)).ToList();
        var groups = new Dictionary<string, ProjectGroup>(StringComparer.Ordinal) { [string.Empty] = document.Root };

        foreach (var folder in held.SelectMany(Folders).Distinct()
                     .OrderBy(folder => folder.Count(character => character == '/'))
                     .ThenBy(folder => folder, StringComparer.Ordinal))
        {
            var holder = groups[Above(folder)];
            var relative = folder + "/" + ProjectPackage.GroupFile;

            groups[folder] = document.ReadGroup(
                relative,
                files.TryGetValue(relative, out var settings) ? settings : BlankGroup,
                Leaf(folder),
                holder);
        }

        foreach (var relative in held
                     .Where(one => one.EndsWith(ProjectPackage.DrawingExtension, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(one => one, StringComparer.Ordinal))
        {
            document.ReadDrawing(relative, files[relative], groups[Above(relative)]);
        }

        // Touched here so a bad value is a read error rather than a surprise at build time.
        _ = document.Root.Cache;
        _ = document.Root.HelperScope;
        _ = document.Root.SkiaSharp;
        Validate(document.Root);

        return document;

        static IEnumerable<string> Folders(string relative)
        {
            for (var end = relative.LastIndexOf('/'); end > 0; end = relative.LastIndexOf('/', end - 1))
            {
                yield return relative.Substring(0, end);
            }
        }

        static string Above(string relative)
            => relative.LastIndexOf('/') is var slash and > 0 ? relative.Substring(0, slash) : string.Empty;

        static string Leaf(string relative) => relative.Substring(relative.LastIndexOf('/') + 1);
    }

    /// <summary>The same project as a build: the groups folded away, each drawing carrying itself.</summary>
    /// <remarks>
    /// Where the build writes is not among what this carries: the project names no file, so the
    /// caller that asked for one lays it over these settings.
    /// </remarks>
    public SvgcProject Flatten()
    {
        var items = new List<SvgcProjectItem>();

        foreach (var drawing in Root.Drawings)
        {
            var size = drawing.ScopedSizeOwner;

            items.Add(new SvgcProjectItem(
                // The name, since there is no file: it is what the build says it is reading and
                // what a refusal names.
                drawing.Name,
                null,
                drawing.ScopedNamespace,
                // Its own name where nothing above claims one, rather than the build's default of
                // "Generated" — which every drawing of a project would share.
                drawing.EffectiveClass ?? SvgExport.Identifier(drawing.Name),
                null,
                size?.Width,
                size?.Height,
                size?.Scale,
                drawing.ScopedPadding,
                // What it is built from rather than what it is: the groups above it declare into it
                // on the way, and a build that read the drawing alone would generate a Draw with
                // none of those parameters on it.
                ProjectDeclarations.Built(drawing, drawing.Text))
            {
                Method = drawing.Method,
                Parameters = SvgcProject.ParseNames(drawing.Parameters)
            });
        }

        return new SvgcProject(
            null,
            Root.Namespace,
            Root.Class,
            null,
            Root.Cache,
            Root.HelperScope,
            Root.SkiaSharp,
            null,
            Root.Width,
            Root.Height,
            Root.Scale,
            Root.Padding,
            items)
        {
            Style = Root.Style
        };
    }

    /// <summary>Every file of the project as it would be written, by <c>/</c>-separated path.</summary>
    /// <remarks>Names whatever has no file yet, which is why a save and this agree on where things go.</remarks>
    public IReadOnlyDictionary<string, string> Files()
    {
        Root.Place();

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProjectPackage.Manifest] = Root.FileText()
        };

        Root.Write(files, string.Empty);

        return files;
    }

    public void Save() => Save(Path ?? throw new InvalidOperationException("This project has no folder to save to."));

    public void Save(string path)
    {
        var full = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

        // Not replaced, though a save panel asked: the likeliest file of that name is a project an
        // older Studio wrote as one file, which this one cannot read, so it would be the only copy.
        if (File.Exists(full))
        {
            throw new SvgcProjectException(
                $"There is already a file called {System.IO.Path.GetFileName(full)} there, and a project is now a folder. "
                + "Save under another name, or move that file somewhere else first.");
        }

        // A folder somebody keeps something else in is not written into: a save deletes what the
        // project no longer holds, and in somebody's folder that could be anything.
        if (Directory.Exists(full) && !ProjectPackage.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
        {
            throw new SvgcProjectException($"'{full}' is a folder that is not a project, so the project is not saved into it.");
        }

        var disk = string.Equals(full, Path, StringComparison.Ordinal) && _disk is { } kept
            ? kept
            : Directory.Exists(full)
                ? ProjectPackage.Read(full)
                : new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            ProjectPackage.Write(full, Files(), disk);
        }
        finally
        {
            // How far it got, even when it stopped: the next save then writes only what is left.
            _disk = disk;
        }

        Path = full;
    }

    internal static SvgSourceDocument ReadFile(string text, string relative)
        => SvgSourceDocument.Read(text, out var refusal)
           ?? throw new SvgcProjectException($"{relative}: {refusal ?? "it could not be read."}");

    private ProjectGroup ReadGroup(string relative, string text, string segment, ProjectGroup parent)
    {
        var file = ReadFile(text, relative);
        var element = file.Document.Root!;

        if (element.Name != "group")
        {
            throw new SvgcProjectException($"{relative}: the root must be <group>, but was <{element.Name.LocalName}>.");
        }

        RequireKnownAttributes(element, s_nodeAttributes, "group", relative);

        var group = ProjectGroup.Group(file, parent, this);

        group.Segment = segment;

        // What the folder says, where the file does not say otherwise; taken back out as it is saved.
        if (Attribute(element, "name") is null)
        {
            element.SetAttributeValue("name", ProjectPackage.Named(segment, group: true));
        }

        Validate(group);
        ReadBlocks(group, relative);
        parent.Attach(group);

        return group;
    }

    private void ReadDrawing(string relative, string text, ProjectGroup parent)
    {
        var file = ReadFile(text, relative);

        if (file.Document.Root!.Name.LocalName != "svg")
        {
            throw new SvgcProjectException($"{relative}: a drawing has to be an <svg> document.");
        }

        var settings = ProjectDrawing.Lift(file.Document.Root, out var lead);
        var element = new XElement(
            "drawing",
            settings?.Attributes()
                .Where(attribute => !attribute.IsNamespaceDeclaration)
                .Select(attribute => new XAttribute(attribute.Name.LocalName, attribute.Value)));

        RequireKnownAttributes(element, s_drawingAttributes, "drawing", relative);

        var segment = relative.Substring(relative.LastIndexOf('/') + 1);

        if (Attribute(element, "name") is null)
        {
            element.SetAttributeValue("name", ProjectPackage.Named(segment, group: false));
        }

        var drawing = new ProjectDrawing(element, parent, this, file) { Segment = segment, Lead = lead };

        Validate(drawing);
        parent.Attach(drawing);
    }

    private static void ReadBlocks(ProjectGroup group, string relative)
    {
        var blocks = 0;
        var templates = 0;
        var element = group.Element;

        foreach (var child in element.Elements())
        {
            // Not a row: what the group declares is part of what the group is, like its attributes.
            // Malformed declarations are deliberately not read here — SKSvg does not fail a load
            // over a bad block either, and the panel is where one is reported.
            if (child.Name == Declarations + "code")
            {
                if (++blocks > 1)
                {
                    throw new SvgcProjectException($"{relative} holds more than one <e:code>. A group declares in one block.");
                }

                continue;
            }

            // The same, for the same reason: what the templates say is read when they are listed.
            // The project's alone, because they are what an import into any of its groups picks from.
            if (child.Name == Declarations + "templates")
            {
                if (group is not ProjectRoot)
                {
                    throw new SvgcProjectException($"{relative} holds an <e:templates>. Templates belong to the project, in {ProjectPackage.Manifest}.");
                }

                if (++templates > 1)
                {
                    throw new SvgcProjectException($"{relative} holds more than one <e:templates>. A project keeps its templates in one block.");
                }

                continue;
            }

            // Rejected rather than ignored: a mistyped name that bound nothing and still saved
            // would be a project quietly building something else.
            throw new SvgcProjectException(
                child.Name.LocalName is "drawing" or "group"
                    ? $"{relative} holds a <{child.Name.LocalName}>. A drawing is an .svg file of its own, and a group a folder."
                    : $"<{child.Name.LocalName}> is not allowed in {relative}. Expected <e:code>"
                      + (group is ProjectRoot ? ", or one <e:templates>." : "."));
        }
    }

    /// <summary>Touches every parsed setting, so a bad value is a read error rather than a surprise later.</summary>
    private static void Validate(ProjectNode node)
    {
        _ = node.Width;
        _ = node.Height;
        _ = node.Scale;

        // Half a point is not a place. The size trio allows a lone width because a width alone is a
        // request the build understands; a lone x is a board with no rule for where the item goes.
        if (node.X is { } != node.Y is { })
        {
            throw new SvgcProjectException(
                $"<{node.Element.Name.LocalName} name=\"{node.Name}\"> names one of x and y. A place needs both, or neither.");
        }

        // Only the new form: a namespace in full that a file has always held loads as it did.
        if (node.Namespace is { } continued && continued.StartsWith(".", StringComparison.Ordinal))
        {
            SvgcProject.ParseNamespace(continued, node is not ProjectRoot);
        }
    }

    private static void RequireKnownAttributes(XElement element, string[] allowed, string elementName, string relative)
    {
        foreach (var attribute in element.Attributes())
        {
            // A namespace declaration is not a setting. One binding the expression prefix is what a
            // file holding an <e:code> may well carry on its root, and refusing it read as a
            // complaint about a misspelled attribute.
            if (attribute.IsNamespaceDeclaration)
            {
                continue;
            }

            if (Array.IndexOf(allowed, attribute.Name.LocalName) < 0)
            {
                throw new SvgcProjectException(
                    $"{relative}: '{attribute.Name.LocalName}' is not a <{elementName}> setting. Expected one of: {string.Join(", ", allowed)}.");
            }
        }
    }

    internal static string? Attribute(XElement element, string name)
    {
        var value = ((string?)element.Attribute(name))?.Trim();

        return value is null || value.Length == 0 ? null : value;
    }

    /// <summary>How far in <paramref name="element"/> itself is written, without the break.</summary>
    internal static string Depth(XElement element)
    {
        var leading = Leading(element);

        return leading.Substring(leading.LastIndexOf('\n') + 1);
    }

    /// <summary>The break and indentation whatever closes <paramref name="element"/> sits on.</summary>
    internal static string Closing(XElement element) => "\n" + Depth(element);

    /// <summary>The break and indentation <paramref name="element"/> itself sits on.</summary>
    internal static string Leading(XElement element)
        => element.PreviousNode is XText text && text.Value.Contains("\n") ? text.Value : "\n";
}
