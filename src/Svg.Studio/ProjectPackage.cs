// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Svg.Studio;

/// <summary>
/// A project on disk: a <c>.svgstudio</c> folder holding <c>studio.xml</c>, one <c>.svg</c> per
/// drawing, and a folder with a <c>group.xml</c> per group.
/// </summary>
/// <remarks>
/// A folder rather than one file so that git merges a project the way it merges anything else: two
/// people adding a drawing each add a file each, and only a drawing both of them changed conflicts.
/// </remarks>
public static class ProjectPackage
{
    public const string Extension = ".svgstudio";

    public const string Manifest = "studio.xml";

    public const string GroupFile = "group.xml";

    public const string DrawingExtension = ".svg";

    private const int Longest = 64;

    private static readonly string[] s_devices =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>The project folder <paramref name="path"/> is, or is inside, or null.</summary>
    /// <remarks>
    /// Anything inside counts, so picking the <c>studio.xml</c> a folder picker shows opens the
    /// project, which is how one opens where the system does not know a project is a document.
    /// </remarks>
    public static string? Find(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        for (var at = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
             !string.IsNullOrEmpty(at);
             at = Path.GetDirectoryName(at))
        {
            if (at.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) && Directory.Exists(at))
            {
                return at;
            }
        }

        return null;
    }

    /// <summary>Whether the project at <paramref name="path"/> is there to open.</summary>
    public static bool Exists(string path) => File.Exists(Path.Combine(path, Manifest));

    /// <summary>Whether a path in a project, <c>/</c>-separated, is one of the files a project is made of.</summary>
    public static bool Holds(string relative)
    {
        if (relative.Split('/').Any(part => part.StartsWith(".", StringComparison.Ordinal)))
        {
            return false;
        }

        var leaf = relative.Substring(relative.LastIndexOf('/') + 1);

        return relative == Manifest
               || (leaf == GroupFile && relative.Length > leaf.Length)
               || leaf.EndsWith(DrawingExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every file the project at <paramref name="folder"/> is made of, by <c>/</c>-separated path.</summary>
    /// <remarks>
    /// Decoded rather than read as text, which strips a byte order mark: the mark is a character of
    /// the document here, and a file would lose three bytes on the first save. Anything else in the
    /// folder is left alone, and so never deleted.
    /// </remarks>
    public static Dictionary<string, string> Read(string folder)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var decoder = new UTF8Encoding(false);

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');

            if (Holds(relative))
            {
                files[relative] = decoder.GetString(File.ReadAllBytes(file));
            }
        }

        return files;
    }

    /// <summary>
    /// Writes <paramref name="files"/> into <paramref name="folder"/>, touching only what differs from
    /// <paramref name="disk"/> — what the folder held as last read or written, kept up to date here.
    /// </summary>
    /// <remarks>
    /// New and changed files first, each to a temporary beside it and then moved over, and only then
    /// what is gone: a save that stops half way leaves a drawing twice rather than not at all, and
    /// <paramref name="disk"/> says exactly how far it got, so the next save finishes the job.
    /// </remarks>
    public static void Write(string folder, IReadOnlyDictionary<string, string> files, Dictionary<string, string> disk)
    {
        Directory.CreateDirectory(folder);

        var spellings = Spellings(disk.Keys, StringComparer.OrdinalIgnoreCase);
        var spelled = Spellings(disk.Keys, StringComparer.Ordinal);

        foreach (var (relative, text) in files)
        {
            if (disk.TryGetValue(relative, out var had) && had == text)
            {
                continue;
            }

            var path = Located(folder, relative);

            if (Spellings(new[] { relative }, StringComparer.Ordinal).Any(part => spellings.Contains(part) && !spelled.Contains(part)))
            {
                Recase(folder, relative);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var temporary = path + ".~tmp";

            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);

            disk[relative] = text;
        }

        foreach (var stale in disk.Keys.Where(relative => !files.ContainsKey(relative)).ToList())
        {
            // Spelled the same but for case, it is the file just written on a file system that does
            // not tell them apart, and deleting it would delete that.
            if (!files.Keys.Any(relative => string.Equals(relative, stale, StringComparison.OrdinalIgnoreCase)))
            {
                File.Delete(Located(folder, stale));
            }

            disk.Remove(stale);
        }

        Prune(folder);
    }

    /// <summary>A file or folder name for a row called <paramref name="name"/>, free among <paramref name="taken"/>.</summary>
    /// <remarks>
    /// Free case-insensitively, because two names differing only in case are one file on macOS and
    /// Windows. Taken into <paramref name="taken"/> as it is handed out.
    /// </remarks>
    public static string Segment(string name, ISet<string> taken, bool group)
    {
        var stem = Clean(name);
        var extension = group ? string.Empty : DrawingExtension;
        var segment = stem + extension;

        for (var count = 2; !taken.Add(segment); count++)
        {
            segment = stem + "~" + count.ToString(CultureInfo.InvariantCulture) + extension;
        }

        return segment;
    }

    /// <summary>Whether <paramref name="segment"/> is still the name a row called <paramref name="name"/> would be given.</summary>
    public static bool Fits(string segment, string name, bool group)
        => Unsuffixed(Stem(segment, group)) == Clean(name);

    /// <summary>The name a row stored under <paramref name="segment"/> is called when nothing says otherwise.</summary>
    public static string Named(string segment, bool group) => Unsuffixed(Stem(segment, group));

    /// <summary>The names no row may take in any folder, because the folder's own files have them.</summary>
    public static ISet<string> Reserved()
        => new HashSet<string>(new[] { Manifest, GroupFile }, StringComparer.OrdinalIgnoreCase);

    private static string Stem(string segment, bool group)
        => group || !segment.EndsWith(DrawingExtension, StringComparison.OrdinalIgnoreCase)
            ? segment
            : segment.Substring(0, segment.Length - DrawingExtension.Length);

    // The ~2 a name is given to stay apart from a sibling, taken off again to say what it is called.
    private static string Unsuffixed(string stem)
    {
        var tilde = stem.LastIndexOf('~');

        return tilde > 0 && tilde < stem.Length - 1 && stem.Skip(tilde + 1).All(char.IsAsciiDigit)
            ? stem.Substring(0, tilde)
            : stem;
    }

    /// <summary>A name as every file system the project may be cloned onto can hold it.</summary>
    internal static string Clean(string name)
    {
        var text = new StringBuilder();

        foreach (var character in name.Normalize(NormalizationForm.FormC))
        {
            text.Append(character < ' ' || character == '\u007F' || "/\\:*?\"<>|".IndexOf(character) >= 0 ? '_' : character);
        }

        var clean = text.ToString().Trim(' ', '.');

        if (clean.Length > Longest)
        {
            clean = clean.Substring(0, Longest).TrimEnd(' ', '.');
        }

        if (clean.Length == 0)
        {
            return "_";
        }

        var device = clean.Split('.')[0];

        return s_devices.Contains(device, StringComparer.OrdinalIgnoreCase) ? clean + "_" : clean;
    }

    /// <summary>Every path in <paramref name="paths"/>, and every folder above one.</summary>
    private static HashSet<string> Spellings(IEnumerable<string> paths, StringComparer comparer)
    {
        var set = new HashSet<string>(comparer);

        foreach (var path in paths)
        {
            for (var end = path.Length; end > 0; end = path.LastIndexOf('/', end - 1))
            {
                set.Add(path.Substring(0, end));
            }
        }

        return set;
    }

    private static string Located(string folder, string relative)
        => Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Gives every part of <paramref name="relative"/> already on disk the case it is now spelled with.
    /// </summary>
    /// <remarks>
    /// A rename that changes only case finds the old name still there on a file system that does not
    /// tell them apart, and writing over it keeps the old spelling: the rename would be lost, and git
    /// would never hear of it. Moved through a temporary name, since a move to itself does nothing.
    /// </remarks>
    private static void Recase(string folder, string relative)
    {
        var at = folder;

        foreach (var part in relative.Split('/'))
        {
            var wanted = Path.Combine(at, part);

            if (Directory.Exists(at)
                && Directory.EnumerateFileSystemEntries(at).Select(Path.GetFileName).ToList() is var names
                && !names.Contains(part, StringComparer.Ordinal)
                && names.FirstOrDefault(one => string.Equals(one, part, StringComparison.OrdinalIgnoreCase)) is { } spelled)
            {
                var old = Path.Combine(at, spelled);
                var between = wanted + ".~case";

                if (Directory.Exists(old))
                {
                    Directory.Move(old, between);
                    Directory.Move(between, wanted);
                }
                else
                {
                    File.Move(old, between);
                    File.Move(between, wanted);
                }
            }

            at = wanted;
        }
    }

    /// <summary>Takes away the folders a save left empty, which is what a group removed leaves.</summary>
    private static void Prune(string folder)
    {
        foreach (var directory in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
                     .Where(path => !Path.GetRelativePath(folder, path).Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith(".", StringComparison.Ordinal)))
                     .OrderByDescending(path => path.Length)
                     .ToList())
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }
}
