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
        float? scale = null,
        string? padding = null)
    {
        Input = input;
        Output = output;
        Namespace = namespaceName;
        Class = className;
        Recipe = recipe;
        Width = width;
        Height = height;
        Scale = scale;
        Padding = padding;
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

    /// <summary>The room to leave around this drawing as it was written, or null to take the project's.</summary>
    /// <remarks>
    /// Not part of <see cref="HasSize"/>: padding says how much room to leave rather than what size
    /// to be, so it overrides on its own and an item asking for it keeps the project's sizing. Left
    /// as it was written for the reason a width is left a bare number — what it means is decided
    /// where the whole group is seen, and that is not here.
    /// </remarks>
    public string? Padding { get; }

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
/// Every setting is nullable, so a value the document did not mention stays distinguishable from one
/// it set — which is what lets a command line flag override the file rather than tie with it.
/// </para>
/// <para>
/// A <c>&lt;group&gt;</c> is folded into its drawings as the project is read, so a drawing carries
/// what its groups said as if it had said it itself.
/// </para>
/// </remarks>
public sealed class SvgcProject
{
    internal SvgcProject(
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
        string? padding,
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
        Padding = padding;
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

    /// <summary>The room left around every drawing, inside the size it is given, as it was written.</summary>
    public string? Padding { get; }

    /// <summary>Whether the project asks for a size at all. <see cref="SvgcProjectItem.HasSize"/>.</summary>
    public bool HasSize => Width is { } || Height is { } || Scale is { };

    public IReadOnlyList<SvgcProjectItem> Items { get; }

    public static SvgcProject Load(string path) => SvgcProjectDocument.Load(path).Flatten();

    /// <summary>
    /// Reads a project from its text. Paths resolve against <paramref name="baseDirectory"/> —
    /// the directory of the file — so a project describes the same build wherever it is run from.
    /// </summary>
    public static SvgcProject Parse(string xml, string baseDirectory)
        => SvgcProjectDocument.Parse(xml, baseDirectory).Flatten();

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
}
