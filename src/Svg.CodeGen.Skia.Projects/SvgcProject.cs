// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Svg.CodeGen.Skia.Projects;

/// <summary>One drawing of a build, and whatever it overrides.</summary>
public sealed class SvgcProjectItem
{
    public SvgcProjectItem(
        string input,
        string? output,
        string? namespaceName,
        string? className,
        string? recipe,
        float? width = null,
        float? height = null,
        float? scale = null)
    {
        Input = input;
        Output = output;
        Namespace = namespaceName;
        Class = className;
        Recipe = recipe;
        Width = width;
        Height = height;
        Scale = scale;
    }

    public string Input { get; }

    public string? Output { get; }

    public string? Namespace { get; }

    public string? Class { get; }

    public string? Recipe { get; }

    /// <summary>The width to resize this drawing to, in pixels.</summary>
    public float? Width { get; }

    /// <summary>The height to resize this drawing to, in pixels.</summary>
    public float? Height { get; }

    /// <summary>The factor to resize this drawing by, against the size it already has.</summary>
    public float? Scale { get; }

    /// <summary>Whether the item asks for a size of its own, rather than taking the project's.</summary>
    /// <remarks>
    /// The three are one group: a scale and an explicit size contradict each other, so an item
    /// that names any of them replaces the project's sizing outright instead of merging with it.
    /// </remarks>
    public bool HasSize => Width is { } || Height is { } || Scale is { };
}

/// <summary>
/// A whole code generation build in one document:
///
/// <code>
///   &lt;svgc&gt;
///     &lt;recipe&gt;icons.recipe&lt;/recipe&gt;
///     &lt;namespace&gt;Icons&lt;/namespace&gt;
///     &lt;singleFile&gt;Icons.cs&lt;/singleFile&gt;
///
///     &lt;svg input="home.svg"   class="Home" /&gt;
///
///     &lt;group namespace="Icons.Brand" scale="2"&gt;
///       &lt;svg input="logo.svg" class="Logo" /&gt;
///     &lt;/group&gt;
///   &lt;/svgc&gt;
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// Every setting is optional and every one is nullable here, so a value the document did not
/// mention stays distinguishable from one it set — which is what lets a command line flag
/// override the file rather than merely tie with it.
/// </para>
/// <para>
/// A <c>&lt;group&gt;</c> is folded into its drawings as the project is read, so
/// <see cref="Items"/> is the same flat list either way. That also settles what a group beats: a
/// drawing carries what its groups said as if it had said it itself, and a drawing's own settings
/// already beat a command line flag.
/// </para>
/// </remarks>
public sealed class SvgcProject
{
    private static readonly string[] s_settings =
    {
        "recipe", "namespace", "class", "emit", "cache", "helperScope", "singleFile", "skiaSharp",
        "width", "height", "scale"
    };

    private static readonly string[] s_itemAttributes =
    {
        "input", "output", "namespace", "class", "recipe", "width", "height", "scale"
    };

    // Everything a drawing can name for itself, less the two that are about one file.
    private static readonly string[] s_groupAttributes =
    {
        "namespace", "class", "recipe", "width", "height", "scale"
    };

    private SvgcProject(
        string? recipe,
        string? namespaceName,
        string? className,
        SvgEmit? emit,
        SvgPictureCache? cache,
        SvgHelperScope? helperScope,
        SkiaSharpTarget? skiaSharp,
        string? singleFile,
        float? width,
        float? height,
        float? scale,
        IReadOnlyList<SvgcProjectItem> items)
    {
        Recipe = recipe;
        Namespace = namespaceName;
        Class = className;
        Emit = emit;
        Cache = cache;
        HelperScope = helperScope;
        SkiaSharp = skiaSharp;
        SingleFile = singleFile;
        Width = width;
        Height = height;
        Scale = scale;
        Items = items;
    }

    public string? Recipe { get; }

    public string? Namespace { get; }

    public string? Class { get; }

    public SvgEmit? Emit { get; }

    public SvgPictureCache? Cache { get; }

    public SvgHelperScope? HelperScope { get; }

    /// <summary>Which SkiaSharp the generated code has to compile against.</summary>
    public SkiaSharpTarget? SkiaSharp { get; }

    public string? SingleFile { get; }

    /// <summary>The width every drawing is resized to, in pixels.</summary>
    public float? Width { get; }

    /// <summary>The height every drawing is resized to, in pixels.</summary>
    public float? Height { get; }

    /// <summary>The factor every drawing is resized by, against the size it already has.</summary>
    public float? Scale { get; }

    /// <summary>Whether the project asks for a size at all. <see cref="SvgcProjectItem.HasSize"/>.</summary>
    public bool HasSize => Width is { } || Height is { } || Scale is { };

    public IReadOnlyList<SvgcProjectItem> Items { get; }

    public static SvgcProject Load(string path)
        => Parse(File.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty);

    /// <summary>
    /// Reads a project from its text. Paths resolve against <paramref name="baseDirectory"/> —
    /// the directory of the file — so a project describes the same build wherever it is run from.
    /// </summary>
    public static SvgcProject Parse(string xml, string baseDirectory)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(
                new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new SvgcProjectException($"The project is not well formed XML: {ex.Message}", ex);
        }

        var root = document.Root
            ?? throw new SvgcProjectException("The project is empty.");

        if (root.Name != "svgc")
        {
            throw new SvgcProjectException($"The project root must be <svgc>, but was <{root.Name.LocalName}>.");
        }

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var items = new List<SvgcProjectItem>();

        foreach (var element in root.Elements())
        {
            var name = element.Name.LocalName;

            if (name == "svg")
            {
                items.Add(ReadItem(element, baseDirectory, Scoped.None));
                continue;
            }

            if (name == "group")
            {
                ReadGroup(element, baseDirectory, Scoped.None, items);
                continue;
            }

            // Rejected rather than ignored. The json batch this replaces bound nothing on a
            // mistyped key and still exited zero, which is a bad afternoon.
            if (Array.IndexOf(s_settings, name) < 0)
            {
                throw new SvgcProjectException(
                    $"<{name}> is not a project setting. Expected <svg>, <group> or one of: {string.Join(", ", s_settings)}.");
            }

            if (settings.ContainsKey(name))
            {
                throw new SvgcProjectException($"<{name}> is set more than once.");
            }

            settings[name] = element.Value.Trim();
        }

        return new SvgcProject(
            Resolve(Setting(settings, "recipe"), baseDirectory),
            Setting(settings, "namespace"),
            Setting(settings, "class"),
            Setting(settings, "emit") is { } emit ? ParseEmit(emit) : null,
            Setting(settings, "cache") is { } cache ? ParseCache(cache) : null,
            Setting(settings, "helperScope") is { } scope ? ParseHelperScope(scope) : null,
            Setting(settings, "skiaSharp") is { } skia ? ParseSkiaSharpTarget(skia) : null,
            Resolve(Setting(settings, "singleFile"), baseDirectory),
            ParseLength(Setting(settings, "width"), "width"),
            ParseLength(Setting(settings, "height"), "height"),
            ParseScale(Setting(settings, "scale")),
            items);
    }

    public static SvgEmit ParseEmit(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "csharp" => SvgEmit.CSharp,
        "svg" => SvgEmit.Svg,
        _ => throw new SvgcProjectException($"'{value}' is not an output format. Expected csharp or svg.")
    };

    public static SvgPictureCache ParseCache(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "none" => SvgPictureCache.None,
        "lastvalue" => SvgPictureCache.LastValue,
        "lastvaluelocked" => SvgPictureCache.LastValueLocked,
        _ => throw new SvgcProjectException($"'{value}' is not a cache mode. Expected none, lastValue or lastValueLocked.")
    };

    public static SvgHelperScope ParseHelperScope(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "file" => SvgHelperScope.FileLocal,
        "internal" => SvgHelperScope.Internal,
        "perclass" => SvgHelperScope.PerClass,
        _ => throw new SvgcProjectException($"'{value}' is not a helper scope. Expected file, internal or perClass.")
    };

    public static SkiaSharpTarget ParseSkiaSharpTarget(string? value) => value?.Trim() switch
    {
        null or "" or "4" => SkiaSharpTarget.V4,
        "3" => SkiaSharpTarget.V3,
        _ => throw new SvgcProjectException($"'{value}' is not a SkiaSharp version. Expected 3 or 4.")
    };

    /// <summary>
    /// A width or a height in pixels, or null when it was not given. Whether the number makes
    /// sense as a size is not decided here — <c>SvgSizeRequest</c> owns that, and it is the only
    /// place that sees the whole group.
    /// </summary>
    public static float? ParseLength(string? value, string name)
        => ParseNumber(value, $"is not a {name}. Expected a number of pixels.");

    /// <summary>A factor of the size a drawing already has, or null when it was not given.</summary>
    public static float? ParseScale(string? value)
        => ParseNumber(value, "is not a scale. Expected a number.");

    private static float? ParseNumber(string? value, string complaint)
    {
        if (value is null)
        {
            return null;
        }

        // Invariant, because a project describes the same build on every machine that reads it.
        return float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new SvgcProjectException($"'{value}' {complaint}");
    }

    /// <summary>
    /// Reads the drawings of one <c>&lt;group&gt;</c>, and of the groups inside it, appending them
    /// to <paramref name="items"/> in document order.
    /// </summary>
    /// <remarks>
    /// Nothing of the group survives the parse. Its settings are folded into every drawing it
    /// holds, so the result is the same flat list of fully resolved items a project without groups
    /// produces, and nothing downstream has to know groups exist.
    /// </remarks>
    private static void ReadGroup(XElement element, string baseDirectory, Scoped inherited, List<SvgcProjectItem> items)
    {
        RequireKnownAttributes(element, s_groupAttributes, "group");

        var scoped = inherited.OverlaidWith(element, baseDirectory);

        foreach (var child in element.Elements())
        {
            var name = child.Name.LocalName;

            if (name == "svg")
            {
                items.Add(ReadItem(child, baseDirectory, scoped));
                continue;
            }

            if (name == "group")
            {
                ReadGroup(child, baseDirectory, scoped, items);
                continue;
            }

            // A setting element here would look like it scopes to the group and would in fact be
            // ignored, so it is worth saying where it should have gone.
            throw new SvgcProjectException(
                $"<{name}> is not allowed in a <group>. A group holds <svg> and <group>; its own settings are attributes on it.");
        }
    }

    private static SvgcProjectItem ReadItem(XElement element, string baseDirectory, Scoped inherited)
    {
        RequireKnownAttributes(element, s_itemAttributes, "svg");

        var input = Attribute(element, "input")
            ?? throw new SvgcProjectException("<svg> is missing an input.");

        var scoped = inherited.OverlaidWith(element, baseDirectory);

        return new SvgcProjectItem(
            Resolve(input, baseDirectory)!,
            Resolve(Attribute(element, "output"), baseDirectory),
            scoped.Namespace,
            scoped.Class,
            scoped.Recipe,
            scoped.Width,
            scoped.Height,
            scoped.Scale);
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

    /// <summary>
    /// What the enclosing groups have said so far. A drawing outside every group starts from
    /// <see cref="None"/>, and whatever is still null there falls through to the project settings
    /// — which is what lets a command line flag override the file at all.
    /// </summary>
    private readonly struct Scoped
    {
        private Scoped(string? namespaceName, string? className, string? recipe, float? width, float? height, float? scale)
        {
            Namespace = namespaceName;
            Class = className;
            Recipe = recipe;
            Width = width;
            Height = height;
            Scale = scale;
        }

        public static Scoped None => default;

        public string? Namespace { get; }

        public string? Class { get; }

        public string? Recipe { get; }

        public float? Width { get; }

        public float? Height { get; }

        public float? Scale { get; }

        /// <summary>This, with whatever <paramref name="element"/> names of its own on top.</summary>
        public Scoped OverlaidWith(XElement element, string baseDirectory)
        {
            var width = ParseLength(Attribute(element, "width"), "width");
            var height = ParseLength(Attribute(element, "height"), "height");
            var scale = ParseScale(Attribute(element, "scale"));

            // The three are one setting, so an element that names any of them replaces all three.
            // Overlaying them singly would let an inner width join an outer scale, which is a
            // contradiction rather than a refinement.
            var resizes = width is { } || height is { } || scale is { };

            return new Scoped(
                Attribute(element, "namespace") ?? Namespace,
                Attribute(element, "class") ?? Class,
                Resolve(Attribute(element, "recipe"), baseDirectory) ?? Recipe,
                resizes ? width : Width,
                resizes ? height : Height,
                resizes ? scale : Scale);
        }
    }

    private static string? Attribute(XElement element, string name)
    {
        var value = ((string?)element.Attribute(name))?.Trim();

        return value is null || value.Length == 0 ? null : value;
    }

    private static string? Setting(Dictionary<string, string> settings, string name)
        => settings.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    private static string? Resolve(string? path, string baseDirectory)
        => path is null || Path.IsPathRooted(path) || baseDirectory.Length == 0
            ? path
            : Path.Combine(baseDirectory, path);
}
