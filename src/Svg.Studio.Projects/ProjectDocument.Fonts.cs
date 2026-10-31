// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ShimSkiaSharp;
using ShimSkiaSharp.Editing;
using Svg.CodeGen.Skia.Projects;
using Svg.Skia;
using Svg.Skia.TypefaceProviders;

namespace Svg.Studio;

/// <summary>One font file a project measures with.</summary>
/// <param name="File">Its path inside the project (<c>fonts/X.ttf</c>), or the fonts folder's setting and its name.</param>
/// <param name="Bytes">What the file holds.</param>
/// <param name="Hash">The bytes' SHA-256, which the face is shared by.</param>
/// <param name="Provider">The face, whose <see cref="CustomTypefaceProvider.Typeface"/> is null where Skia cannot read it.</param>
internal readonly record struct ProjectFace(string File, ReadOnlyMemory<byte> Bytes, string Hash, CustomTypefaceProvider Provider);

public sealed partial class ProjectDocument
{
    private static readonly ImmutableSortedDictionary<string, ReadOnlyMemory<byte>> s_noFonts
        = ImmutableSortedDictionary.Create<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);

    // Never freed: SkiaModel caches advances by SKTypeface.Handle, so a freed face's address taken by
    // a newer copy of the same file would be measured with the old one's advances.
    private static readonly ConcurrentDictionary<string, CustomTypefaceProvider> s_faces = new(StringComparer.Ordinal);

    // What a machine with no project fonts resolves, and the resolver's own style conversions.
    private static readonly Lazy<SkiaModel> s_machine = new(() => new SkiaModel(new SKSvgSettings()));

    private readonly object _fontsLock = new();

    private FontSet? _fonts;

    // Read as text alone, by the public Read: the folder's fonts are then not this document's to delete.
    private bool _fontsUnread;

    /// <summary>The fonts the project holds, by <c>/</c>-separated path inside it (<c>fonts/X.ttf</c>), in the order they are asked.</summary>
    /// <remarks>Read from its <c>fonts</c> folder, added and taken out in Studio as edits, and written by a save.</remarks>
    public ImmutableSortedDictionary<string, ReadOnlyMemory<byte>> Fonts { get; internal set; } = s_noFonts;

    /// <summary>
    /// What the project's text is measured and drawn with: the fonts it holds, then those of its
    /// <see cref="ProjectRoot.FontsFolder"/>, then the machine's.
    /// </summary>
    /// <remarks>The same list until the fonts or the folder change, so a host can tell when to draw again.</remarks>
    public IReadOnlyList<ITypefaceProvider> TypefaceProviders() => Set().Providers;

    /// <summary>What is wrong with the project's fonts themselves: a file Skia cannot read, a folder that is not there.</summary>
    public IReadOnlyList<string> FontProblems() => Set().Problems;

    /// <summary>
    /// One sentence for each typeface or character of <paramref name="picture"/>'s text that the
    /// project's fonts do not hold, or, where it has none, that this machine has no font for.
    /// </summary>
    /// <remarks>
    /// Where the project has fonts, a face it lacks is said even where the machine has one, since
    /// another machine would measure with another: what is said is the same on every machine.
    /// </remarks>
    public IReadOnlyList<string> MissingFonts(SKPicture? picture) => Missing(Set(), picture);

    /// <summary>Every font the project measures with, those it holds first.</summary>
    internal IReadOnlyList<ProjectFace> Faces() => Set().Faces;

    /// <summary>
    /// The settings and the loader an export of <paramref name="project"/> builds with, the fonts'
    /// problems said to <paramref name="log"/>.
    /// </summary>
    /// <remarks>
    /// A loader of its own each time: SceneGraph's text caches are keyed on the loader, not on its
    /// fonts. The set is taken once, so the loader and the check cannot disagree.
    /// </remarks>
    internal (SvgcBuildSettings Settings, SkiaSvgAssetLoader Loader) Prepare(SvgcProject project, Action<string>? log)
    {
        var set = Set();

        foreach (var problem in set.Problems)
        {
            log?.Invoke($"warning: {problem}");
        }

        var settings = SvgcBuildSettings.For(project);

        settings.Warnings = picture => Missing(set, picture);

        return (settings, new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings { TypefaceProviders = set.Providers.ToList() })));
    }

    /// <summary>The face <paramref name="bytes"/> hold, made once for every document that holds them.</summary>
    internal static (string Hash, CustomTypefaceProvider Provider) Face(ReadOnlyMemory<byte> bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes.Span));

        return (hash, s_faces.GetOrAdd(hash, static (_, held) => new CustomTypefaceProvider(SkiaSharp.SKData.CreateCopy(held.Span)), bytes));
    }

    /// <summary>A face as people say it, <c>Noto Sans 700 italic</c>.</summary>
    internal static string Spoken(SkiaSharp.SKTypeface? face)
        => face is null ? "not a font Skia can read" : $"{face.FamilyName} {Style(face.FontWeight, face.FontWidth, face.FontSlant)}";

    private static string Style(int weight, int width, SkiaSharp.SKFontStyleSlant slant)
    {
        var text = new StringBuilder(weight.ToString(CultureInfo.InvariantCulture));

        if (width != (int)SkiaSharp.SKFontStyleWidth.Normal)
        {
            text.Append(' ').Append(((SkiaSharp.SKFontStyleWidth)width).ToString().ToLowerInvariant());
        }

        if (slant != SkiaSharp.SKFontStyleSlant.Upright)
        {
            text.Append(' ').Append(slant == SkiaSharp.SKFontStyleSlant.Italic ? "italic" : "oblique");
        }

        return text.ToString();
    }

    private FontSet Set()
    {
        var package = Fonts;
        var setting = Root.FontsFolder;
        var path = Path;

        lock (_fontsLock)
        {
            // Where the project is counts only while it names a folder: the first save of a project
            // that holds its fonts itself would otherwise draw everything again for nothing.
            if (_fonts is { } held
                && ReferenceEquals(held.Package, package)
                && held.Setting == setting
                && (setting is null || held.Path == path))
            {
                return held;
            }

            return _fonts = FontSet.Read(package, setting, path, _fonts);
        }
    }

    private static IReadOnlyList<string> Missing(FontSet set, SKPicture? picture)
    {
        var said = new List<string>();

        if (picture is null)
        {
            return said;
        }

        var machine = s_machine.Value;
        var project = set.Package.Count > 0 || set.Setting is { };
        var faces = set.Providers.OfType<CustomTypefaceProvider>().ToList();

        foreach (var command in picture.FindCommands<CanvasCommand>())
        {
            var (paint, text) = command switch
            {
                DrawTextCanvasCommand draw => (draw.Paint, draw.Text),
                DrawTextOnPathCanvasCommand along => (along.Paint, along.Text),
                DrawPositionedTextRunCanvasCommand run => (run.Paint, string.Concat(run.Fragments?.Select(fragment => fragment.Text) ?? Array.Empty<string>())),
                DrawTextBlobCanvasCommand blob => (blob.Paint, blob.TextBlob?.Text),
                _ => (null, null)
            };

            if (paint?.RequestedTypeface is not { } requested)
            {
                continue;
            }

            if ((project ? Unheld(set, faces, machine, requested, text) : Absent(machine, requested)) is { } sentence && !said.Contains(sentence))
            {
                said.Add(sentence);
            }
        }

        return said;
    }

    /// <summary>Why the project's fonts do not serve <paramref name="requested"/>, or null where they do.</summary>
    /// <remarks>
    /// Asked of the first family as the resolver asks it, of the project's faces before any other:
    /// a face the first family finds there is the one the text is measured with on every machine.
    /// </remarks>
    private static string? Unheld(FontSet set, IReadOnlyList<CustomTypefaceProvider> faces, SkiaModel machine, SKTypeface requested, string? text)
    {
        var family = First(requested.FamilyName);
        var weight = machine.ToSKFontStyleWeight(requested.FontWeight);
        var width = machine.ToSKFontStyleWidth(requested.FontWidth);
        var slant = machine.ToSKFontStyleSlant(requested.FontSlant);
        var style = Style((int)weight, (int)width, slant);

        if (family is null)
        {
            return $"a text naming no font-family is not among the project's fonts ({set.Held}), so each machine measures it with a font of its own.";
        }

        if (faces.Select(face => face.FromFamilyName(family, weight, width, slant)).FirstOrDefault(found => found is { }) is not { } served)
        {
            return $"'{family}' {style} is not among the project's fonts ({set.Held}), so each machine measures it with a font of its own.";
        }

        using var font = new SkiaSharp.SKFont(served);

        // Marks, format characters and whitespace stay in the run's font, as the loader keeps them;
        // a space and a no-break space it asks the font for, so this does too.
        foreach (var rune in (text ?? string.Empty).EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) && rune.Value is not (0x20 or 0xA0)
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.EnclosingMark or UnicodeCategory.Format or UnicodeCategory.Control)
            {
                continue;
            }

            if (!font.ContainsGlyph(rune.Value))
            {
                return $"'{family}' {style} has no '{rune}' (U+{rune.Value:X4}), so each machine draws it from a font of its own.";
            }
        }

        return null;
    }

    /// <summary>Why this machine has no font of <paramref name="requested"/>'s family, or null where it has one.</summary>
    /// <remarks>Accepted as the resolver accepts a family, ignoring case and at the nearest weight.</remarks>
    private static string? Absent(SkiaModel machine, SKTypeface requested)
    {
        if (First(requested.FamilyName) is not { } family || SkiaModel.IsGenericFontFamilyName(family))
        {
            return null;
        }

        var found = machine.ToSKTypeface(requested)?.FamilyName;

        return string.Equals(found, family, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"'{family}' is not on this machine, so its text is measured with '{found}' instead; adding the font to the project measures it the same everywhere.";
    }

    /// <summary>The first family a <c>font-family</c> names, as the resolver trims it.</summary>
    private static string? First(string? families)
        => families?.Split(',').Select(name => name.Trim().Trim('\'', '"').Trim()).FirstOrDefault(name => name.Length > 0);

    /// <summary>The fonts a project measures with, as read for one set of the fonts it holds and one fonts folder.</summary>
    private sealed record FontSet(
        ImmutableSortedDictionary<string, ReadOnlyMemory<byte>> Package,
        string? Setting,
        string? Path,
        IReadOnlyList<ProjectFace> Faces,
        IReadOnlyList<ITypefaceProvider> Providers,
        IReadOnlyList<string> Problems,
        string Held)
    {
        /// <param name="was">The set this one follows, whose faces are kept for whatever bytes are still the same: an undo hashes nothing.</param>
        public static FontSet Read(ImmutableSortedDictionary<string, ReadOnlyMemory<byte>> package, string? setting, string? path, FontSet? was)
        {
            var faces = new List<ProjectFace>();
            var problems = new List<string>();

            foreach (var (file, bytes) in package)
            {
                faces.Add(Kept(file, bytes) ?? Made(file, bytes));
            }

            if (setting is { })
            {
                ReadFolder(setting, path, faces, problems);
            }

            foreach (var face in faces.Where(face => face.Provider.Typeface is null))
            {
                problems.Add($"{face.File} is not a font Skia can read; a Git LFS pointer that was never pulled reads like this.");
            }

            var held = faces
                .Where(face => face.Provider.Typeface is { })
                .Select(face => face.Provider.Typeface!)
                .GroupBy(typeface => typeface.FamilyName, StringComparer.Ordinal)
                .Select(family => $"{family.Key} {string.Join(", ", family
                    .OrderBy(typeface => typeface.FontWeight).ThenBy(typeface => typeface.FontWidth).ThenBy(typeface => typeface.FontSlant)
                    .Select(typeface => Style(typeface.FontWeight, typeface.FontWidth, typeface.FontSlant)))}")
                .ToList();

            var providers = faces
                .Where(face => face.Provider.Typeface is { })
                .Select(face => (ITypefaceProvider)face.Provider)
                .Distinct()
                .Concat(new ITypefaceProvider[] { new FontManagerTypefaceProvider(), new DefaultTypefaceProvider() })
                .ToList()
                .AsReadOnly();

            return new FontSet(package, setting, path, faces, providers, problems, held.Count > 0 ? string.Join("; ", held) : "none that load");

            ProjectFace? Kept(string file, ReadOnlyMemory<byte> bytes)
                => was?.Faces.FirstOrDefault(face => face.File == file && face.Bytes.Equals(bytes)) is { Provider: { } } kept ? kept : null;
        }

        private static ProjectFace Made(string file, ReadOnlyMemory<byte> bytes)
        {
            var (hash, provider) = Face(bytes);

            return new ProjectFace(file, bytes, hash, provider);
        }

        private static void ReadFolder(string setting, string? path, List<ProjectFace> faces, List<string> problems)
        {
            if (path is null)
            {
                problems.Add($"The fonts folder '{setting}' is found from the project's folder, which it has none of until it is saved; its text is measured without it.");
                return;
            }

            try
            {
                var folder = System.IO.Path.GetFullPath(System.IO.Path.Combine(path, setting));

                if (!Directory.Exists(folder))
                {
                    problems.Add($"The fonts folder '{setting}' is not there: {folder}.");
                    return;
                }

                var files = Directory.EnumerateFiles(folder)
                    .Select(file => (File: file, Leaf: System.IO.Path.GetFileName(file)))
                    .Where(file => !file.Leaf.StartsWith(".", StringComparison.Ordinal) && ProjectPackage.IsFont(file.Leaf))
                    .OrderBy(file => file.Leaf, StringComparer.Ordinal)
                    .ToList();

                if (files.Count == 0)
                {
                    problems.Add($"The fonts folder '{setting}' holds no .otf or .ttf file.");
                }

                foreach (var (file, leaf) in files)
                {
                    faces.Add(Made($"{setting.TrimEnd('/')}/{leaf}", File.ReadAllBytes(file)));
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                problems.Add($"The fonts folder '{setting}' cannot be read: {failure.Message}");
            }
        }
    }
}
