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
        string? padding = null,
        string? source = null)
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
        Source = source;
    }

    /// <summary>The drawing's file, or what to call it where <see cref="Source"/> says there is none.</summary>
    public string Input { get; }

    /// <summary>The drawing itself, for a project that holds it rather than pointing at it.</summary>
    /// <remarks>
    /// Svg.Studio keeps its drawings inline in the project file, so there is no file to read and
    /// <see cref="Input"/> carries the drawing's name instead — which is what the build says it is
    /// reading and what a refusal names. Null is the ordinary case: the drawing is a file.
    /// </remarks>
    public string? Source { get; }

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

    /// <summary>Its method on <see cref="SvgCodeStyle.Api"/>, where not <c>draw</c> and the class name.</summary>
    public string? Method { get; set; }

    /// <summary>The parameters its API method takes, in order, in place of <see cref="SvgCodeStyle.Order"/>.</summary>
    public IReadOnlyList<string>? Parameters { get; set; }
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
    /// <summary>A build described in memory, for a host whose project is not an svgc document.</summary>
    public SvgcProject(
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

    /// <summary>The shape of the generated file beyond the drawings; nothing a flag overrides.</summary>
    public SvgCodeStyle Style { get; set; } = SvgCodeStyle.Default;

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

    /// <summary>How a build treats text an expression drives, as a flag spells it.</summary>
    /// <remarks>
    /// Here with the other build settings although the project file carries no such attribute: what
    /// a drawing gives up to be generated is a decision about one build, and a project that stated
    /// it would be answering for every machine that ever builds it.
    /// </remarks>
    public static SvgTextLayout ParseTextLayout(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "strict" => SvgTextLayout.Strict,
        "baked" => SvgTextLayout.Baked,
        "relaxed" => SvgTextLayout.Relaxed,
        _ => throw new SvgcProjectException($"'{value}' is not a text layout. Expected strict, baked or relaxed.")
    };

    public static SvgBody ParseBody(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "record" => SvgBody.Record,
        "direct" => SvgBody.Direct,
        _ => throw new SvgcProjectException($"'{value}' is not a body. Expected record or direct.")
    };

    /// <summary>Parameter names separated by white space, or null when there are none.</summary>
    public static IReadOnlyList<string>? ParseNames(string? value)
    {
        if (value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is not { Length: > 0 } names)
        {
            return null;
        }

        foreach (var name in names)
        {
            ParseName(name, "parameter name", qualified: false);
        }

        return names;
    }

    /// <summary>A C# name, dotted where <paramref name="qualified"/>, or null when it was not given.</summary>
    public static string? ParseName(string? value, string what, bool qualified)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var name = value!.Trim();
        var parts = qualified ? name.Split('.') : new[] { name };

        return parts.All(IsName)
            ? name
            : throw new SvgcProjectException($"'{value}' is not a {what}.");
    }

    /// <summary>
    /// A namespace, or null when it was not given. One that starts with a dot continues the namespace
    /// above it, which only <paramref name="relative"/> allows.
    /// </summary>
    public static string? ParseNamespace(string? value, bool relative)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var name = value!.Trim();
        var continues = name[0] == '.';

        if (continues && !relative)
        {
            throw new SvgcProjectException($"'{name}' continues the namespace above it, and there is nothing above it to continue. Write it in full.");
        }

        return (continues ? name.Substring(1) : name).Split('.').All(IsName)
            ? name
            : throw new SvgcProjectException($"'{name}' is not a namespace.");
    }

    /// <summary>
    /// The namespace a node is in, from what it and each node above it say, nearest first: each one
    /// that starts with a dot continues the next, up to the first written in full. Still starting
    /// with a dot where none above is, for the build to continue from the project's.
    /// </summary>
    public static string? ResolveNamespace(IEnumerable<string?> nearestFirst)
    {
        string? resolved = null;

        foreach (var value in nearestFirst)
        {
            if (value is null)
            {
                continue;
            }

            resolved = value + resolved;

            if (value[0] != '.')
            {
                break;
            }
        }

        return resolved;
    }

    private static bool IsName(string part)
        => part.Length > 0 && !char.IsDigit(part[0]) && part.All(c => c == '_' || char.IsLetterOrDigit(c));

    /// <summary>The settings that shape the file as a project spells them, each null where it is not given.</summary>
    public static SvgCodeStyle ParseStyle(string? api, string? resizing, string? context, string? order, string? body, string? typeface)
        => new()
        {
            Api = ParseName(api, "class name", qualified: true),
            Resizing = ParseName(resizing, "type name", qualified: true),
            Context = ParseName(context, "type name", qualified: true),
            Order = ParseNames(order) ?? Array.Empty<string>(),
            Body = ParseBody(body),
            Typeface = Blank(typeface)
        };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

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
