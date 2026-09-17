// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Projects;
using Svg.SourceEditing;

namespace Svg.Studio;

/// <summary>One node of a project: the project itself, a <c>&lt;group&gt;</c> or a drawing.</summary>
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
    private protected ProjectNode(XElement element, ProjectGroup? parent, ProjectDocument owner)
    {
        Element = element;
        Parent = parent;
        Owner = owner;
    }

    /// <summary>The XML this node reads and writes. Editing it edits the project.</summary>
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
    private string? Nearest(string name, bool includeProject)
        => Ancestry(includeProject).Select(node => node.Setting(name)).FirstOrDefault(value => value is { });

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
}

/// <summary>A <c>&lt;group&gt;</c>, and by inheritance the project itself.</summary>
public class ProjectGroup : ProjectNode
{
    private readonly List<ProjectNode> _children = new();

    private protected ProjectGroup(XElement element, ProjectGroup? parent, ProjectDocument owner)
        : base(element, parent, owner)
    {
    }

    internal static ProjectGroup Group(XElement element, ProjectGroup parent, ProjectDocument owner)
        => new(element, parent, owner);

    public IReadOnlyList<ProjectNode> Children => _children;

    internal void Add(ProjectNode child) => _children.Add(child);

    /// <summary>Adds an empty group, and hands it back to be filled in.</summary>
    public ProjectGroup AddGroup(string name, int index)
    {
        var group = Group(new XElement("group", new XAttribute("name", Named(name))), this, Owner);

        Attach(index, group);

        return group;
    }

    /// <summary>Adds a drawing holding <paramref name="svgText"/>, which inherits everything else.</summary>
    /// <remarks>
    /// The drawing arrives written for a file of its own, so this is where it is shifted to the
    /// depth it now sits at. Once in, its whitespace is the project's own and is never touched
    /// again: shifting it back out on the way to an editor and in again on the way back is not a
    /// round trip, because a line that starts inside a text run is not indentation.
    /// </remarks>
    public ProjectDrawing AddDrawing(string name, string svgText, int index)
    {
        var drawing = new ProjectDrawing(
            new XElement("drawing", new XAttribute("name", Named(name))),
            this,
            Owner);

        Attach(index, drawing);

        if (drawing.Inline(svgText, indent: true) is { } refusal)
        {
            Detach(drawing);

            throw new SvgcProjectException(refusal);
        }

        return drawing;
    }

    /// <summary>
    /// Adds a copy of <paramref name="source"/>, with everything under it, and hands it back.
    /// </summary>
    /// <remarks>
    /// Read back through the text it was written as rather than cloned: the bytes each tag was read
    /// as are annotations on the element, and a clone carries none of them — so a copied drawing
    /// would be the one thing in the file that had been reformatted.
    /// </remarks>
    public ProjectNode Copy(ProjectNode source, int index)
    {
        if (source is ProjectRoot)
        {
            throw new SvgcProjectException("The project itself cannot be copied.");
        }

        // Read before attaching, for the reason Move reads before moving: the whitespace that says
        // what depth it was written at travels with the element.
        var was = ProjectDocument.Depth(source.Element);

        var element = Owner.Rooted(source.Owner.Source.TextOf(source.Element));

        var copy = element.Name.LocalName == "group"
            ? Owner.ReadGroup(element, this)
            : (ProjectNode)Owner.ReadDrawing(element, this);

        Attach(index, copy);

        // The place came across in the text and is about where the original sits. Two rows on one
        // spot, one of them under the other, is worse than a row waiting beside the arrangement.
        copy.X = null;
        copy.Y = null;

        Reindent(element, was, ProjectDocument.Depth(element));

        return copy;
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

    /// <summary>
    /// Moves a node into this group, at <paramref name="index"/>.
    /// </summary>
    /// <remarks>
    /// The index is read against the children as they are now, before the node has left wherever it
    /// is — so a drop after the second row is index 2 whether or not the node being dropped is
    /// already in this group.
    /// </remarks>
    public void Move(ProjectNode child, int index)
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

        if (ReferenceEquals(parent, this) && index > parent._children.IndexOf(child))
        {
            index--;
        }
        else if (!ReferenceEquals(parent, this))
        {
            // Its place was about the board it has left. A reorder inside one board keeps it:
            // document order decides nothing about where a row is drawn any more.
            child.X = null;
            child.Y = null;
        }

        // Read before the move, since detaching takes the whitespace that says it away.
        var was = ProjectDocument.Depth(child.Element);

        parent.Detach(child);
        Attach(index, child);

        Reindent(child.Element, was, ProjectDocument.Depth(child.Element));
    }

    /// <summary>Every drawing under this node, in document order.</summary>
    public IEnumerable<ProjectDrawing> Drawings
        => _children.SelectMany(child => child switch
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
    /// The whitespace between a group's children lives inside it, so a group carried to a new depth
    /// arrives with its contents still indented for the old one. Only the lines that sat at the old
    /// depth are shifted; what was deeper keeps the extra, which is what makes a whole branch move
    /// as one.
    ///
    /// Whitespace that is a line of its own, and nothing else: a project holds whole drawings, so a
    /// text run here can be the stylesheet in a <c>&lt;style&gt;</c> or the words in a
    /// <c>&lt;text&gt;</c>, and shifting those would change what the drawing says rather than where
    /// it sits.
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

    /// <summary>Puts a node into the children and into the XML, on a line of its own.</summary>
    private void Attach(int index, ProjectNode child)
    {
        index = Math.Max(0, Math.Min(index, _children.Count));

        var element = child.Element;

        if (_children.Count == 0)
        {
            AddFirst(element);
        }
        else if (index == _children.Count)
        {
            var last = _children[index - 1].Element;

            last.AddAfterSelf(new XText(Owner.Indentation(Element)), element);
        }
        else
        {
            var next = _children[index].Element;

            next.AddBeforeSelf(element);

            // The break and indentation the displaced element was sitting on, given back to it.
            // Whitespace is a node of its own once it is preserved, and inserting before an element
            // lands after the whitespace in front of it — so without this the two share a line.
            if (element.PreviousNode is XText indent)
            {
                element.AddAfterSelf(new XText(indent.Value));
            }
        }

        _children.Insert(index, child);
        child.Parent = this;
    }

    /// <summary>The first thing this group has held, so there is no sibling to take a line from.</summary>
    private void AddFirst(XElement element)
    {
        var inner = new XText(Owner.Indentation(Element));

        // Whatever closes the group already sits on the right line; the new element goes in front
        // of it rather than after, which is where Add would put it.
        if (Element.LastNode is XText closing && closing.Value.Trim().Length == 0)
        {
            closing.AddBeforeSelf(inner, element);
            return;
        }

        Element.Add(inner, element, new XText(ProjectDocument.Closing(Element)));
    }

    /// <summary>Takes a node out of the children and out of the XML, and the line with it.</summary>
    private void Detach(ProjectNode child)
    {
        var element = child.Element;

        // What goes with it is the separator, not simply the whitespace in front. In front of the
        // first of several children is the group's own opening indentation, which has to stay — take
        // that and the break behind the element is promoted to opening the group, blank line and
        // all. In front of an only child it is the opening indentation again, but there the break
        // behind is what closes the group, so this time the one in front is the one to go.
        var separator = ReferenceEquals(element, Element.Elements().FirstOrDefault())
                        && Element.Elements().Skip(1).Any()
            ? element.NextNode as XText
            : element.PreviousNode as XText;

        // Left behind, it meets the whitespace on the other side and the pair reads as a blank line.
        if (separator is { } text && text.Value.Trim().Length == 0)
        {
            text.Remove();
        }

        element.Remove();

        _children.Remove(child);
        child.Parent = null;
    }
}

/// <summary>One <c>&lt;drawing&gt;</c>: an SVG the project holds, and whatever it overrides.</summary>
public sealed class ProjectDrawing : ProjectNode
{
    internal ProjectDrawing(XElement element, ProjectGroup parent, ProjectDocument owner)
        : base(element, parent, owner)
    {
    }

    public string? Output
    {
        get => ProjectDocument.Attribute(Element, "output");
        set => Element.SetAttributeValue("output", Trimmed(value));
    }

    public string? ResolvedOutput => Owner.Resolve(Output);

    /// <summary>The drawing itself: the one <c>&lt;svg&gt;</c> this holds.</summary>
    public XElement Svg => Element.Elements().First();

    /// <summary>The drawing as text, written the way the project file writes it.</summary>
    public string Text => Owner.Source.TextOf(Svg);

    /// <summary>
    /// Puts edited text back, or answers why it could not be read.
    /// </summary>
    /// <remarks>
    /// What comes in is what <see cref="Text"/> handed out, so it already carries the project's
    /// indentation and is written back as it stands.
    /// </remarks>
    public string? SetText(string svgText) => Inline(svgText, indent: false);

    internal string? Inline(string svgText, bool indent)
    {
        if (svgText is null)
        {
            throw new ArgumentNullException(nameof(svgText));
        }

        // The project's own ending is put back when the file is written, so one drawing pasted from
        // a machine that ends its lines differently cannot decide it for the whole file.
        var read = SvgSourceDocument.Read(svgText.Replace("\r\n", "\n"), out var refusal);

        if (read is null)
        {
            return refusal;
        }

        if (read.Document.Root is not { } root || root.Name.LocalName != "svg")
        {
            return "A drawing has to be an <svg> document.";
        }

        // Detached first: adding a node that still has a parent copies it, and the bytes each tag
        // was read as are annotations, which a copy does not carry.
        root.Remove();

        if (indent)
        {
            var indentation = Owner.Indentation(Element);

            ProjectGroup.Reindent(root, string.Empty, indentation.Substring(indentation.LastIndexOf('\n') + 1));
        }

        if (Element.Elements().FirstOrDefault() is { } existing)
        {
            existing.ReplaceWith(root);
        }
        else
        {
            Element.Add(new XText(Owner.Indentation(Element)), root, new XText(ProjectDocument.Closing(Element)));
        }

        return null;
    }
}

/// <summary>The <c>&lt;studio&gt;</c> root: a group that also carries the settings for the whole build.</summary>
public sealed class ProjectRoot : ProjectGroup
{
    internal ProjectRoot(XElement element, ProjectDocument owner)
        : base(element, null, owner)
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
        set => Element.SetAttributeValue("skiaSharp", value is { } target ? (target == SkiaSharpTarget.V3 ? "3" : "4") : null);
    }

    public string? SingleFile
    {
        get => Setting("singleFile");
        set => Element.SetAttributeValue("singleFile", Trimmed(value));
    }

    public string? ResolvedSingleFile => Owner.Resolve(SingleFile);

    private static string CacheText(SvgPictureCache cache) => cache switch
    {
        SvgPictureCache.LastValue => "lastValue",
        SvgPictureCache.LastValueLocked => "lastValueLocked",
        _ => "none"
    };

    private static string ScopeText(SvgHelperScope scope) => scope switch
    {
        SvgHelperScope.Internal => "internal",
        SvgHelperScope.PerClass => "perClass",
        _ => "file"
    };
}

/// <summary>
/// A Svg.Studio project: the drawings themselves and the build they describe, in one file.
/// </summary>
/// <remarks>
/// <para>
/// The format is <c>.svgstudio</c>: a <c>&lt;studio&gt;</c> holding <c>&lt;group&gt;</c>s and
/// <c>&lt;drawing&gt;</c>s, each drawing holding the <c>&lt;svg&gt;</c> it draws. Settings are
/// attributes wherever they appear, and a group hands its own down to everything under it.
/// </para>
/// <para>
/// Held as an <see cref="SvgSourceDocument"/> rather than as a plain tree, which is the whole reason
/// this is not <c>SvgcProjectDocument</c> with different element names: that type re-serialises, and
/// re-serialising somebody's drawing reformats it — measured over the two suites, writing a tree
/// back the ordinary way returned 28 drawings of 2,988 unchanged. The settings and the structural
/// editing below are that type's, rewritten around a holder that can keep a drawing's own bytes.
/// </para>
/// <para>
/// <see cref="Flatten"/> is the same project as a <see cref="SvgcProject"/>, so a build here is the
/// build <c>svgc</c> runs rather than a second implementation of it.
/// </para>
/// </remarks>
public sealed class ProjectDocument
{
    private static readonly string[] s_settings =
    {
        "namespace", "class", "cache", "helperScope", "singleFile", "skiaSharp",
        "width", "height", "scale", "padding"
    };

    private static readonly string[] s_groupAttributes =
    {
        "name", "namespace", "class", "width", "height", "scale", "padding", "x", "y"
    };

    // Everything a group can name, and where the generated file goes, which is about one drawing.
    private static readonly string[] s_drawingAttributes =
    {
        "name", "output", "namespace", "class", "width", "height", "scale", "padding", "x", "y"
    };

    private ProjectDocument(SvgSourceDocument source, string? path, string baseDirectory)
    {
        Source = source;
        Path = path;
        BaseDirectory = baseDirectory;
        Root = new ProjectRoot(source.Document.Root!, this);
    }

    /// <summary>The file as it was read, which is what writes it back unchanged.</summary>
    public SvgSourceDocument Source { get; }

    public ProjectRoot Root { get; }

    /// <summary>The file this was read from, or null when it was parsed from text.</summary>
    public string? Path { get; private set; }

    public string BaseDirectory { get; private set; }

    public static ProjectDocument Load(string path)
    {
        var full = System.IO.Path.GetFullPath(path);

        // Decoded rather than read as text, which strips a byte order mark: the mark is a character
        // of the document here, and the file would lose three bytes on the first save.
        var text = new System.Text.UTF8Encoding(false).GetString(File.ReadAllBytes(full));

        return Parse(text, System.IO.Path.GetDirectoryName(full) ?? string.Empty, full);
    }

    public static ProjectDocument Parse(string xml, string baseDirectory) => Parse(xml, baseDirectory, null);

    /// <summary>A project holding nothing, on the two lines a first drawing is written between.</summary>
    /// <remarks>
    /// No namespace, because the build already defaults one and a guess written into the file would
    /// have to be found and corrected rather than simply typed.
    /// </remarks>
    public static ProjectDocument Empty(string baseDirectory)
        => Parse("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<studio>\n</studio>\n", baseDirectory);

    private static ProjectDocument Parse(string xml, string baseDirectory, string? path)
    {
        if (SvgSourceDocument.Read(xml, out var refusal) is not { } source)
        {
            throw new SvgcProjectException(refusal ?? "The project could not be read.");
        }

        if (source.Document.Root is not { } element)
        {
            throw new SvgcProjectException("The project is empty.");
        }

        if (element.Name != "studio")
        {
            throw new SvgcProjectException($"The project root must be <studio>, but was <{element.Name.LocalName}>.");
        }

        var document = new ProjectDocument(source, path, baseDirectory);

        RequireKnownAttributes(element, s_settings, "studio");

        document.ReadChildren(element, document.Root);

        // Touched here so a bad value is a parse error rather than a surprise at build time.
        _ = document.Root.Cache;
        _ = document.Root.HelperScope;
        _ = document.Root.SkiaSharp;
        Validate(document.Root);

        return document;
    }

    /// <summary>The same project as a build: the groups folded away, each drawing carrying itself.</summary>
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
                drawing.ResolvedOutput,
                drawing.ScopedNamespace,
                // Its own name where nothing above claims one, rather than the build's default of
                // "Generated" — which every drawing of a project would share.
                drawing.EffectiveClass ?? SvgExport.Identifier(drawing.Name),
                null,
                size?.Width,
                size?.Height,
                size?.Scale,
                drawing.ScopedPadding,
                drawing.Text));
        }

        return new SvgcProject(
            null,
            Root.Namespace,
            Root.Class,
            null,
            Root.Cache,
            Root.HelperScope,
            Root.SkiaSharp,
            Root.ResolvedSingleFile,
            Root.Width,
            Root.Height,
            Root.Scale,
            Root.Padding,
            items);
    }

    /// <summary>The document as text, as it would be written.</summary>
    public string ToXml() => Source.ToText();

    public void Save() => Save(Path ?? throw new InvalidOperationException("This project has no file to save to."));

    public void Save(string path)
    {
        // Written without a mark of its own: ToText carries the file's own, where it had one.
        File.WriteAllText(path, ToXml(), new System.Text.UTF8Encoding(false));

        Path = System.IO.Path.GetFullPath(path);
        BaseDirectory = System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
    }

    /// <summary>The root of <paramref name="xml"/>, detached and ready to be put somewhere.</summary>
    internal XElement Rooted(string xml)
    {
        if (SvgSourceDocument.Read(xml, out var refusal)?.Document.Root is not { } root)
        {
            throw new SvgcProjectException(refusal ?? "That is not well formed XML.");
        }

        root.Remove();

        return root;
    }

    internal ProjectGroup ReadGroup(XElement element, ProjectGroup parent)
    {
        RequireKnownAttributes(element, s_groupAttributes, "group");
        RequireName(element, "group");

        var group = ProjectGroup.Group(element, parent, this);

        Validate(group);
        ReadChildren(element, group);

        return group;
    }

    internal ProjectDrawing ReadDrawing(XElement element, ProjectGroup parent)
    {
        RequireKnownAttributes(element, s_drawingAttributes, "drawing");

        var name = RequireName(element, "drawing");
        var children = element.Elements().ToList();

        // One drawing, and the drawing itself rather than a path to it: that is what the format is
        // for. A <drawing> holding two would be a row that draws one of them and saves the other.
        if (children.Count != 1 || children[0].Name.LocalName != "svg")
        {
            throw new SvgcProjectException($"<drawing name=\"{name}\"> must hold one <svg> and nothing else.");
        }

        var drawing = new ProjectDrawing(element, parent, this);

        Validate(drawing);

        return drawing;
    }

    private void ReadChildren(XElement element, ProjectGroup group)
    {
        foreach (var child in element.Elements())
        {
            var name = child.Name.LocalName;

            // Rejected rather than ignored: a mistyped name that bound nothing and still saved
            // would be a project quietly building something else.
            group.Add(name switch
            {
                "drawing" => ReadDrawing(child, group),
                "group" => ReadGroup(child, group),
                _ => throw new SvgcProjectException(
                    $"<{name}> is not allowed in <{element.Name.LocalName}>. Expected <drawing> or <group>.")
            });
        }
    }

    private static string RequireName(XElement element, string elementName)
        => Attribute(element, "name")
           ?? throw new SvgcProjectException($"<{elementName}> is missing a name.");

    /// <summary>Touches every parsed setting, so a bad value is a parse error rather than a surprise later.</summary>
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
    }

    private static void RequireKnownAttributes(XElement element, string[] allowed, string elementName)
    {
        foreach (var attribute in element.Attributes())
        {
            if (Array.IndexOf(allowed, attribute.Name.LocalName) < 0)
            {
                throw new SvgcProjectException(
                    $"'{attribute.Name.LocalName}' is not a <{elementName}> attribute. Expected one of: {string.Join(", ", allowed)}.");
            }
        }
    }

    internal static string? Attribute(XElement element, string name)
    {
        var value = ((string?)element.Attribute(name))?.Trim();

        return value is null || value.Length == 0 ? null : value;
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
        return "\n" + Depth(element) + Source.IndentUnit;
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

    internal string? Resolve(string? path)
        => path is null || System.IO.Path.IsPathRooted(path) || BaseDirectory.Length == 0
            ? path
            : System.IO.Path.Combine(BaseDirectory, path);
}
