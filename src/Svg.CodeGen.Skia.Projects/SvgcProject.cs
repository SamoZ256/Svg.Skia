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
///     &lt;svg input="search.svg" class="Search" /&gt;
///   &lt;/svgc&gt;
/// </code>
/// </summary>
/// <remarks>
/// Every setting is optional and every one is nullable here, so a value the document did not
/// mention stays distinguishable from one it set — which is what lets a command line flag
/// override the file rather than merely tie with it.
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
                items.Add(ReadItem(element, baseDirectory));
                continue;
            }

            // Rejected rather than ignored. The json batch this replaces bound nothing on a
            // mistyped key and still exited zero, which is a bad afternoon.
            if (Array.IndexOf(s_settings, name) < 0)
            {
                throw new SvgcProjectException(
                    $"<{name}> is not a project setting. Expected <svg> or one of: {string.Join(", ", s_settings)}.");
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

    private static SvgcProjectItem ReadItem(XElement element, string baseDirectory)
    {
        foreach (var attribute in element.Attributes())
        {
            if (Array.IndexOf(s_itemAttributes, attribute.Name.LocalName) < 0)
            {
                throw new SvgcProjectException(
                    $"'{attribute.Name.LocalName}' is not a <svg> attribute. Expected one of: {string.Join(", ", s_itemAttributes)}.");
            }
        }

        var input = Attribute(element, "input")
            ?? throw new SvgcProjectException("<svg> is missing an input.");

        return new SvgcProjectItem(
            Resolve(input, baseDirectory)!,
            Resolve(Attribute(element, "output"), baseDirectory),
            Attribute(element, "namespace"),
            Attribute(element, "class"),
            Resolve(Attribute(element, "recipe"), baseDirectory),
            ParseLength(Attribute(element, "width"), "width"),
            ParseLength(Attribute(element, "height"), "height"),
            ParseScale(Attribute(element, "scale")));
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
