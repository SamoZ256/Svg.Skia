// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Svg.Skia;

namespace Svg.Studio;

/// <summary>What the system clipboard is holding, as the project tree reads it.</summary>
/// <remarks>
/// Read when a paste is asked for and at no other time: a clipboard read with no paste behind it is
/// what recent macOS puts a permission panel in front of, so nothing here can be used to decide
/// whether to offer the command in the first place.
/// </remarks>
public sealed class ProjectClipboard
{
    private ProjectClipboard(IReadOnlyList<string> files, string? drawing, IReadOnlyList<string> formats)
    {
        Files = files;
        Drawing = drawing;
        Formats = formats;
    }

    /// <summary>A clipboard with nothing on it for a project.</summary>
    public static ProjectClipboard Nothing { get; }
        = new(Array.Empty<string>(), null, Array.Empty<string>());

    /// <summary>Files it named, which a project can point at where they already are.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>An SVG with no file behind it, which is what a drawing program copies.</summary>
    public string? Drawing { get; }

    /// <summary>What it offered, so a refusal can say what was there instead.</summary>
    public IReadOnlyList<string> Formats { get; }

    public static async Task<ProjectClipboard> ReadAsync(IClipboard? clipboard)
    {
        if (clipboard is null)
        {
            return Nothing;
        }

        using var carried = await clipboard.TryGetDataAsync().ConfigureAwait(true);

        if (carried is null)
        {
            return Nothing;
        }

        var formats = carried.Formats.Select(format => format.Identifier).ToList();

        // Files first: one already on disk is a row the project can carry as it stands, and writing
        // a second copy of it beside the project would be the paste inventing a file nobody asked for.
        var files = (await carried.TryGetFilesAsync().ConfigureAwait(true))
            ?.Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToList();

        return files is { Count: > 0 }
            ? new ProjectClipboard(files, null, formats)
            : new ProjectClipboard(
                Array.Empty<string>(),
                await DrawnAsync(carried).ConfigureAwait(true),
                formats);
    }

    /// <summary>The SVG on the clipboard, or null where none of it is one.</summary>
    /// <remarks>
    /// Text first, which is where Illustrator puts it with Preferences ▸ Clipboard Handling ▸
    /// Include SVG Code on, then any format whose name says SVG — it has also written the same art
    /// as <c>image/svg+xml</c>. Nothing else is read: the rest of a drawing program's clipboard is a
    /// PDF and a bitmap of the same picture, and pulling megabytes over to sniff them is a waste.
    /// </remarks>
    private static async Task<string?> DrawnAsync(IAsyncDataTransfer carried)
    {
        if (await carried.TryGetTextAsync().ConfigureAwait(true) is { } text && IsSvg(text))
        {
            return text;
        }

        foreach (var item in carried.Items)
        {
            foreach (var format in item.Formats.Where(Names))
            {
                var raw = await item.TryGetRawAsync(format).ConfigureAwait(true);

                if (Read(raw) is { } drawn && IsSvg(drawn))
                {
                    return drawn;
                }
            }
        }

        return null;
    }

    /// <summary>Whether a format's name says it carries an SVG.</summary>
    /// <remarks>
    /// By name rather than by a list of them: the identifier is a MIME type on some platforms and a
    /// uniform type identifier on others, and both spell it the same way in the middle.
    /// </remarks>
    private static bool Names(DataFormat format)
        => format.Identifier.Contains("svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>A format's value as text, whichever of the two shapes it arrived in.</summary>
    private static string? Read(object? raw) => raw switch
    {
        string text => text,
        byte[] bytes => Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'),
        _ => null
    };

    /// <summary>Whether text is a drawing, settled by building it.</summary>
    /// <remarks>
    /// Built rather than sniffed for a <c>&lt;svg</c>: a paste writes a file into the project's own
    /// directory, and one that turns out not to open would have to be found and deleted by hand.
    /// The parser refuses with an exception as readily as with null, and both mean the same here.
    /// </remarks>
    private static bool IsSvg(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var svg = new SKSvg();

            return svg.FromSvg(text) is { };
        }
        catch (Exception)
        {
            return false;
        }
    }
}
