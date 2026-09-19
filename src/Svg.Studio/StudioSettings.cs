// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>
/// What the editor has been told to do differently, kept between sessions.
/// </summary>
/// <remarks>
/// One file of <c>key=value</c> lines beside the list of what was opened lately, read and written on
/// the spot the way <see cref="RecentFiles"/> is — a second window sees what the first one changed.
/// Shaped on that class rather than sharing it: a capped list of paths has nothing in it to widen.
/// </remarks>
public static class StudioSettings
{
    private const string AutosaveKey = "autosave";

    private const string CaptionSizeKey = "captionSize";

    /// <summary>
    /// Where the settings are kept.
    /// </summary>
    /// <remarks>Settable so a test drives a file of its own instead of the one on this machine.</remarks>
    public static string Store { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Svg.Studio",
        "settings");

    /// <summary>Whether a recovery copy is kept of the project being edited.</summary>
    /// <remarks>
    /// On unless the file says otherwise, so a missing file, a missing line, a value nobody can read
    /// and a file that cannot be read at all all leave it on. Nothing switches it off by accident:
    /// the only way there is the line the toggle writes.
    /// </remarks>
    public static bool Autosave
    {
        get => !string.Equals(Read(AutosaveKey), "off", StringComparison.Ordinal);
        set => Write(AutosaveKey, value ? "on" : "off");
    }

    /// <summary>How big a caption on a board is drawn, in control pixels.</summary>
    /// <remarks>
    /// A caption is chrome rather than part of a drawing, so how big is readable is about the screen
    /// somebody is at — which is the one thing neither the canvas nor the project can work out.
    ///
    /// Anything the file cannot be read as, and anything outside what the canvas will take, comes
    /// back as the default rather than as a refusal: a settings file is not something to fail over.
    /// </remarks>
    public static double CaptionSize
    {
        get => double.TryParse(Read(CaptionSizeKey), NumberStyles.Float, CultureInfo.InvariantCulture, out var size)
               && size >= SvgViewerCanvas.MinimumCaptionSize
               && size <= SvgViewerCanvas.MaximumCaptionSize
            ? size
            : SvgViewerCanvas.DefaultCaptionSize;

        set => Write(CaptionSizeKey, value.ToString(CultureInfo.InvariantCulture));
    }

    private static string? Read(string key)
    {
        try
        {
            if (!File.Exists(Store))
            {
                return null;
            }

            return File.ReadAllLines(Store)
                .Select(line => line.Split('=', 2))
                .Where(pair => pair.Length == 2 && string.Equals(pair[0], key, StringComparison.Ordinal))
                .Select(pair => pair[1])
                .LastOrDefault();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Write(string key, string value)
    {
        try
        {
            var lines = (File.Exists(Store) ? File.ReadAllLines(Store) : Array.Empty<string>())
                .Where(line => !line.StartsWith(key + "=", StringComparison.Ordinal))
                .Append($"{key}={value}");

            Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
            File.WriteAllLines(Store, lines);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A preference that could not be written comes back as what it is by default, which is
            // where it was before anybody touched it.
        }
    }
}
