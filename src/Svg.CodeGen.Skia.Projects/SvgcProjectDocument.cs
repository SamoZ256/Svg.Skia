// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Svg.CodeGen.Skia.Projects;

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
public abstract class SvgcProjectNode
{
    private protected SvgcProjectNode(XElement element, SvgcProjectGroup? parent, string baseDirectory)
    {
        Element = element;
        Parent = parent;
        BaseDirectory = baseDirectory;
    }

    /// <summary>The XML this node reads and writes. Editing it edits the project.</summary>
    public XElement Element { get; }

    public SvgcProjectGroup? Parent { get; }

    /// <summary>The directory the project was read from, which every relative path resolves against.</summary>
    public string BaseDirectory { get; }

    public string? Namespace
    {
        get => GetSetting("namespace");
        set => SetSetting("namespace", value);
    }

    public string? Class
    {
        get => GetSetting("class");
        set => SetSetting("class", value);
    }

    /// <summary>The recipe as it was written. <see cref="ResolvedRecipe"/> is the one to open.</summary>
    public string? Recipe
    {
        get => GetSetting("recipe");
        set => SetSetting("recipe", value);
    }

    public float? Width
    {
        get => SvgcProject.ParseLength(GetSetting("width"), "width");
        set => SetSetting("width", Number(value));
    }

    public float? Height
    {
        get => SvgcProject.ParseLength(GetSetting("height"), "height");
        set => SetSetting("height", Number(value));
    }

    public float? Scale
    {
        get => SvgcProject.ParseScale(GetSetting("scale"));
        set => SetSetting("scale", Number(value));
    }

    /// <summary>The room to leave, as it was written. <see cref="SvgcProjectItem.Padding"/> says why.</summary>
    public string? Padding
    {
        get => GetSetting("padding");
        set => SetSetting("padding", value);
    }

    /// <summary>Whether this node asks for a size of its own. <see cref="SvgcProjectItem.HasSize"/>.</summary>
    public bool HasSize => Width is { } || Height is { } || Scale is { };

    public string? ResolvedRecipe => SvgcProjectDocument.Resolve(Recipe, BaseDirectory);

    public string? EffectiveNamespace => Nearest("namespace", true);

    public string? EffectiveClass => Nearest("class", true);

    public string? EffectiveRecipe => Nearest("recipe", true);

    public string? EffectiveResolvedRecipe => SvgcProjectDocument.Resolve(EffectiveRecipe, BaseDirectory);

    public string? EffectivePadding => Nearest("padding", true);

    public float? EffectiveWidth => SizeOwner(true)?.Width;

    public float? EffectiveHeight => SizeOwner(true)?.Height;

    public float? EffectiveScale => SizeOwner(true)?.Scale;

    /// <summary>Where an effective setting comes from, for a UI that wants to say so. Null when nothing sets it.</summary>
    public SvgcProjectNode? OwnerOf(string setting) => setting switch
    {
        "width" or "height" or "scale" => SizeOwner(true),
        _ => Ancestry(true).FirstOrDefault(node => node.GetSetting(setting) is { })
    };

    /// <summary>The node's own value, or null. Named rather than typed so a UI can drive one editor.</summary>
    internal string? Setting(string name) => GetSetting(name);

    // What the groups alone settle, with the project's own settings left out. A drawing outside
    // every group inherits nothing here, and what stays null falls through to the project later —
    // which is what lets a command line flag override the file rather than tie with it.
    internal string? ScopedNamespace => Nearest("namespace", false);

    internal string? ScopedClass => Nearest("class", false);

    internal string? ScopedRecipe => Nearest("recipe", false);

    internal string? ScopedPadding => Nearest("padding", false);

    internal SvgcProjectNode? ScopedSizeOwner => SizeOwner(false);

    private protected virtual string? GetSetting(string name) => SvgcProjectDocument.Attribute(Element, name);

    private protected virtual void SetSetting(string name, string? value)
        => Element.SetAttributeValue(name, Trimmed(value));

    /// <summary>This node, then every group above it, then the project unless it is skipped.</summary>
    private IEnumerable<SvgcProjectNode> Ancestry(bool includeProject)
    {
        for (SvgcProjectNode? node = this; node is { }; node = node.Parent)
        {
            if (node is SvgcProjectRoot && !includeProject)
            {
                yield break;
            }

            yield return node;
        }
    }

    /// <summary>The nearest node that names <paramref name="name"/>, and what it says.</summary>
    private protected string? Nearest(string name, bool includeProject)
        => Ancestry(includeProject).Select(node => node.GetSetting(name)).FirstOrDefault(value => value is { });

    /// <summary>
    /// The nearest node that names any of width, height or scale.
    /// </summary>
    /// <remarks>
    /// The three are one setting rather than three, so the nearest node naming any of them answers
    /// for all three. Taking them singly would let an inner width join an outer scale, which is a
    /// contradiction rather than a refinement — the rule <c>svgc</c> builds by.
    /// </remarks>
    private protected SvgcProjectNode? SizeOwner(bool includeProject)
        => Ancestry(includeProject).FirstOrDefault(node => node.HasSize);

    private protected static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

    // Invariant, because a project describes the same build on every machine that reads it.
    private static string? Number(float? value)
        => value?.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A <c>&lt;group&gt;</c>, and by inheritance the project itself.</summary>
public class SvgcProjectGroup : SvgcProjectNode
{
    private readonly List<SvgcProjectNode> _children = new();

    private protected SvgcProjectGroup(XElement element, SvgcProjectGroup? parent, string baseDirectory)
        : base(element, parent, baseDirectory)
    {
    }

    internal static SvgcProjectGroup Group(XElement element, SvgcProjectGroup parent, string baseDirectory)
        => new(element, parent, baseDirectory);

    public IReadOnlyList<SvgcProjectNode> Children => _children;

    internal void Add(SvgcProjectNode child) => _children.Add(child);

    /// <summary>Every drawing under this node, in document order.</summary>
    public IEnumerable<SvgcProjectDrawing> Drawings
        => _children.SelectMany(child => child switch
        {
            SvgcProjectDrawing drawing => new[] { drawing },
            SvgcProjectGroup group => group.Drawings,
            _ => Enumerable.Empty<SvgcProjectDrawing>()
        });
}

/// <summary>One <c>&lt;svg&gt;</c>: a drawing of the build, and whatever it overrides.</summary>
public sealed class SvgcProjectDrawing : SvgcProjectNode
{
    internal SvgcProjectDrawing(XElement element, SvgcProjectGroup parent, string baseDirectory)
        : base(element, parent, baseDirectory)
    {
    }

    public string Input
    {
        get => SvgcProjectDocument.Attribute(Element, "input") ?? string.Empty;
        set => Element.SetAttributeValue("input", Trimmed(value));
    }

    public string? Output
    {
        get => SvgcProjectDocument.Attribute(Element, "output");
        set => Element.SetAttributeValue("output", Trimmed(value));
    }

    public string ResolvedInput => SvgcProjectDocument.Resolve(Input, BaseDirectory)!;

    public string? ResolvedOutput => SvgcProjectDocument.Resolve(Output, BaseDirectory);
}

/// <summary>The <c>&lt;svgc&gt;</c> root: a group that also carries the settings for the whole build.</summary>
/// <remarks>
/// Its settings are child elements rather than attributes, which is the only way it differs from any
/// other group — hence the override rather than a second set of properties.
/// </remarks>
public sealed class SvgcProjectRoot : SvgcProjectGroup
{
    internal SvgcProjectRoot(XElement element, string baseDirectory)
        : base(element, null, baseDirectory)
    {
    }

    public SvgEmit? Emit
    {
        get => GetSetting("emit") is { } value ? SvgcProject.ParseEmit(value) : null;
        set => SetSetting("emit", value?.ToString().ToLowerInvariant());
    }

    public SvgPictureCache? Cache
    {
        get => GetSetting("cache") is { } value ? SvgcProject.ParseCache(value) : null;
        set => SetSetting("cache", value is { } cache ? CacheText(cache) : null);
    }

    public SvgHelperScope? HelperScope
    {
        get => GetSetting("helperScope") is { } value ? SvgcProject.ParseHelperScope(value) : null;
        set => SetSetting("helperScope", value is { } scope ? ScopeText(scope) : null);
    }

    public SkiaSharpTarget? SkiaSharp
    {
        get => GetSetting("skiaSharp") is { } value ? SvgcProject.ParseSkiaSharpTarget(value) : null;
        set => SetSetting("skiaSharp", value is { } target ? (target == SkiaSharpTarget.V3 ? "3" : "4") : null);
    }

    public string? SingleFile
    {
        get => GetSetting("singleFile");
        set => SetSetting("singleFile", value);
    }

    public string? ResolvedSingleFile => SvgcProjectDocument.Resolve(SingleFile, BaseDirectory);

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

    private protected override string? GetSetting(string name)
    {
        var value = Element.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value.Trim();

        return string.IsNullOrEmpty(value) ? null : value;
    }

    private protected override void SetSetting(string name, string? value)
    {
        var text = Trimmed(value);
        var existing = Element.Elements().FirstOrDefault(child => child.Name.LocalName == name);

        if (text is null)
        {
            existing?.Remove();
            return;
        }

        if (existing is { })
        {
            existing.Value = text;
            return;
        }

        // Before the drawings, so a setting added by a UI lands where the documented layout puts it
        // rather than after the build it is supposed to describe.
        var firstItem = Element.Elements()
            .FirstOrDefault(child => child.Name.LocalName is "svg" or "group");

        var added = new XElement(name, text);

        if (firstItem is { })
        {
            firstItem.AddBeforeSelf(added);

            // The break and indentation the displaced element was sitting on, given back to it.
            // Whitespace is a node of its own once it is preserved, and inserting before an element
            // lands after the whitespace in front of it — so without this the new setting and the
            // element it displaced share a line.
            if (added.PreviousNode is XText indent)
            {
                added.AddAfterSelf(new XText(indent.Value));
            }

            return;
        }

        // Nothing to sit in front of, so in front of whatever closes the document instead, on the
        // indentation the settings already there are using.
        if (Element.LastNode is XText closing)
        {
            closing.AddBeforeSelf(new XText(Indent()), added);
            return;
        }

        Element.Add(added);
    }

    /// <summary>The break and indentation this project writes its settings on.</summary>
    private string Indent()
        => Element.Nodes().OfType<XText>().FirstOrDefault(text => text.Value.Contains("\n"))?.Value ?? "\n  ";
}

/// <summary>
/// A project as its document: the tree the file describes, editable and saveable.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SvgcProject"/> is this flattened — the same build, with the groups folded into the
/// drawings and nothing left that a generator has to know about. Reading happens once, here, so the
/// rules about what a project may say and what a group settles have one home.
/// </para>
/// <para>
/// The <see cref="XDocument"/> is kept rather than re-emitted, so comments, attribute order and
/// indentation survive an edit. Only the attribute actually changed is rewritten.
/// </para>
/// </remarks>
public sealed class SvgcProjectDocument
{
    private static readonly string[] s_settings =
    {
        "recipe", "namespace", "class", "emit", "cache", "helperScope", "singleFile", "skiaSharp",
        "width", "height", "scale", "padding"
    };

    private static readonly string[] s_itemAttributes =
    {
        "input", "output", "namespace", "class", "recipe", "width", "height", "scale", "padding"
    };

    // Everything a drawing can name for itself, less the two that are about one file.
    private static readonly string[] s_groupAttributes =
    {
        "namespace", "class", "recipe", "width", "height", "scale", "padding"
    };

    private readonly XDocument _document;

    private SvgcProjectDocument(
        XDocument document,
        SvgcProjectRoot root,
        string? path,
        string baseDirectory,
        bool byteOrderMark,
        string newLine)
    {
        _document = document;
        Root = root;
        Path = path;
        BaseDirectory = baseDirectory;
        ByteOrderMark = byteOrderMark;
        NewLine = newLine;
    }

    public SvgcProjectRoot Root { get; }

    /// <summary>The file this was read from, or null when it was parsed from text.</summary>
    public string? Path { get; }

    public string BaseDirectory { get; }

    /// <summary>Whether the file began with a byte order mark, so a save does not drop three bytes.</summary>
    public bool ByteOrderMark { get; }

    /// <summary>
    /// The line ending the file used.
    /// </summary>
    /// <remarks>
    /// Recorded because <see cref="XmlReader"/> normalises CRLF to LF as the XML spec requires, so a
    /// document read and written back on Windows would otherwise change every line.
    /// </remarks>
    public string NewLine { get; }

    public static SvgcProjectDocument Load(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(full);
        var mark = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, mark ? 3 : 0, bytes.Length - (mark ? 3 : 0));

        return Parse(text, System.IO.Path.GetDirectoryName(full) ?? string.Empty, full, mark);
    }

    public static SvgcProjectDocument Parse(string xml, string baseDirectory)
        => Parse(xml, baseDirectory, null, false);

    private static SvgcProjectDocument Parse(string xml, string baseDirectory, string? path, bool byteOrderMark)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(
                new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });

            // Preserved, so the indentation an author chose is not a casualty of editing one attribute.
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new SvgcProjectException($"The project is not well formed XML: {ex.Message}", ex);
        }

        var element = document.Root
            ?? throw new SvgcProjectException("The project is empty.");

        if (element.Name != "svgc")
        {
            throw new SvgcProjectException($"The project root must be <svgc>, but was <{element.Name.LocalName}>.");
        }

        var root = new SvgcProjectRoot(element, baseDirectory);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var child in element.Elements())
        {
            var name = child.Name.LocalName;

            if (name == "svg")
            {
                root.Add(ReadDrawing(child, root, baseDirectory));
                continue;
            }

            if (name == "group")
            {
                root.Add(ReadGroup(child, root, baseDirectory));
                continue;
            }

            // Rejected rather than ignored. The json batch this replaces bound nothing on a
            // mistyped key and still exited zero, which is a bad afternoon.
            if (Array.IndexOf(s_settings, name) < 0)
            {
                throw new SvgcProjectException(
                    $"<{name}> is not a project setting. Expected <svg>, <group> or one of: {string.Join(", ", s_settings)}.");
            }

            if (!seen.Add(name))
            {
                throw new SvgcProjectException($"<{name}> is set more than once.");
            }
        }

        // Touched here so a bad value is a parse error, as it was when every setting was read eagerly.
        _ = root.Emit;
        _ = root.Cache;
        _ = root.HelperScope;
        _ = root.SkiaSharp;
        ValidateSize(root);

        var newLine = xml.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";

        return new SvgcProjectDocument(document, root, path, baseDirectory, byteOrderMark, newLine);
    }

    /// <summary>The same build with the groups folded away, which is all a generator needs.</summary>
    public SvgcProject Flatten()
    {
        var items = new List<SvgcProjectItem>();

        foreach (var drawing in Root.Drawings)
        {
            var size = drawing.ScopedSizeOwner;

            items.Add(new SvgcProjectItem(
                drawing.ResolvedInput,
                drawing.ResolvedOutput,
                drawing.ScopedNamespace,
                drawing.ScopedClass,
                Resolve(drawing.ScopedRecipe, BaseDirectory),
                size?.Width,
                size?.Height,
                size?.Scale,
                drawing.ScopedPadding));
        }

        return new SvgcProject(
            Resolve(Root.Recipe, BaseDirectory),
            Root.Namespace,
            Root.Class,
            Root.Emit,
            Root.Cache,
            Root.HelperScope,
            Root.SkiaSharp,
            Resolve(Root.SingleFile, BaseDirectory),
            Root.Width,
            Root.Height,
            Root.Scale,
            Root.Padding,
            items);
    }

    /// <summary>The document as text, as it would be written.</summary>
    public string ToXml()
    {
        var builder = new StringBuilder();

        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false,
            NewLineHandling = NewLineHandling.None,
            Encoding = new UTF8Encoding(false)
        };

        using (var writer = XmlWriter.Create(builder, settings))
        {
            _document.Save(writer);
        }

        var body = builder.ToString();

        // Written by hand rather than by the writer, which reports the encoding of what it is
        // writing to — a StringBuilder is utf-16, and the file would claim to be too. The break
        // after it is already a preserved whitespace node, so one is added only if there is none.
        var xml = _document.Declaration is { } declaration
            ? declaration + (body.Length > 0 && body[0] == '\n' ? string.Empty : "\n") + body
            : body;

        return NewLine == "\n" ? xml : xml.Replace("\n", NewLine);
    }

    public void Save() => Save(Path ?? throw new InvalidOperationException("This project has no file to save to."));

    public void Save(string path)
        => File.WriteAllText(path, ToXml(), new UTF8Encoding(ByteOrderMark));

    private static SvgcProjectGroup ReadGroup(XElement element, SvgcProjectGroup parent, string baseDirectory)
    {
        RequireKnownAttributes(element, s_groupAttributes, "group");

        var group = SvgcProjectGroup.Group(element, parent, baseDirectory);

        ValidateSize(group);

        foreach (var child in element.Elements())
        {
            var name = child.Name.LocalName;

            if (name == "svg")
            {
                group.Add(ReadDrawing(child, group, baseDirectory));
                continue;
            }

            if (name == "group")
            {
                group.Add(ReadGroup(child, group, baseDirectory));
                continue;
            }

            // A setting element here would look like it scopes to the group and would in fact be
            // ignored, so it is worth saying where it should have gone.
            throw new SvgcProjectException(
                $"<{name}> is not allowed in a <group>. A group holds <svg> and <group>; its own settings are attributes on it.");
        }

        return group;
    }

    private static SvgcProjectDrawing ReadDrawing(XElement element, SvgcProjectGroup parent, string baseDirectory)
    {
        RequireKnownAttributes(element, s_itemAttributes, "svg");

        if (Attribute(element, "input") is null)
        {
            throw new SvgcProjectException("<svg> is missing an input.");
        }

        var drawing = new SvgcProjectDrawing(element, parent, baseDirectory);

        ValidateSize(drawing);

        return drawing;
    }

    private static void ValidateSize(SvgcProjectNode node)
    {
        _ = node.Width;
        _ = node.Height;
        _ = node.Scale;
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

    internal static string? Resolve(string? path, string baseDirectory)
        => path is null || System.IO.Path.IsPathRooted(path) || baseDirectory.Length == 0
            ? path
            : System.IO.Path.Combine(baseDirectory, path);
}
