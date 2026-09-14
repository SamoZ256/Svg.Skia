// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
namespace Svg.PaintCode;

/// <summary>What an import writes, and where.</summary>
public sealed class PaintCodeImportOptions
{
    public PaintCodeImportOptions(string directory)
    {
        Directory = directory;
    }

    /// <summary>The directory the drawings are written under, a folder per desk.</summary>
    public string Directory { get; }

    /// <summary>
    /// Whether a canvas that exists only to be used as a symbol gets a drawing of its own.
    /// </summary>
    /// <remarks>
    /// Off, because a project row for one would have code generated for it that nothing calls; a
    /// symbol reaches the drawings that use it whether or not it is written out itself.
    /// </remarks>
    public bool IncludeSymbolOnlyCanvases { get; set; }
}
