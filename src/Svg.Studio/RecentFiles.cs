// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Svg.Studio;

/// <summary>
/// The drawings and projects opened lately, newest first.
/// </summary>
/// <remarks>
/// One file of paths under the user's application data, read and written on the spot rather than
/// held in the window: the list is the whole of what one session hands the next, and a second
/// window opened over the same file sees what the first one added.
/// </remarks>
public static class RecentFiles
{
    /// <summary>How many are kept: a list long enough to scroll is one nobody reads.</summary>
    private const int Capacity = 10;

    /// <summary>
    /// Where the list is kept.
    /// </summary>
    /// <remarks>Settable so a test drives a file of its own instead of the one on this machine.</remarks>
    public static string Store { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Svg.Studio",
        "recent");

    /// <summary>What can be opened again, newest first.</summary>
    /// <remarks>
    /// Filtered by what is still there, so nothing offered can fail to open — a file moved or
    /// deleted between sessions is the ordinary case, not an error worth reporting.
    /// </remarks>
    public static IReadOnlyList<string> Paths => Read().Where(File.Exists).ToList();

    /// <summary>Puts a path at the front, wherever in the list it was.</summary>
    public static void Add(string path)
    {
        var full = Path.GetFullPath(path);

        var kept = new List<string> { full };

        kept.AddRange(Read()
            .Where(other => !string.Equals(other, full, StringComparison.Ordinal))
            .Take(Capacity - 1));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
            File.WriteAllLines(Store, kept);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A list of what was opened lately is not worth interrupting anyone over.
        }
    }

    private static IEnumerable<string> Read()
    {
        try
        {
            return File.Exists(Store) ? File.ReadAllLines(Store) : Array.Empty<string>();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
