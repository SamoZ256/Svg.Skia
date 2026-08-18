// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>The ordinary picker, over the platform storage provider.</summary>
public class SvgViewerFileDialogService : ISvgViewerFileDialogService
{
    private static readonly FilePickerFileType SvgFileType = new("Svg Files")
    {
        Patterns = new[] { "*.svg", "*.svgz" },
        AppleUniformTypeIdentifiers = new[] { "public.svg-image" },
        MimeTypes = new[] { "image/svg+xml", "application/gzip" }
    };

    // Declared here rather than using FilePickerFileTypes.All, which also carries the
    // `public.item` uniform type identifier. This mirrors samples/TestApp, whose picker is the one
    // shape known to work in this repository.
    private static readonly FilePickerFileType AllFileType = new("All")
    {
        Patterns = new[] { "*.*" },
        MimeTypes = new[] { "*/*" }
    };

    public async Task<string?> OpenSvgAsync(TopLevel? owner)
    {
        var storage = owner?.StorageProvider;
        if (storage is null || !storage.CanOpen)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open svg file",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType> { SvgFileType, AllFileType }
        }).ConfigureAwait(true);

        return files?.Select(file => file.TryGetLocalPath()).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
    }
}
