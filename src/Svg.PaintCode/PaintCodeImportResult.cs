// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;

namespace Svg.PaintCode;

/// <summary>What an import wrote, and what it could not carry across.</summary>
public sealed class PaintCodeImportResult
{
    internal PaintCodeImportResult(string? projectPath, IReadOnlyList<string> files, IReadOnlyList<PaintCodeImportNote> notes)
    {
        ProjectPath = projectPath;
        Files = files;
        Notes = notes;
    }

    /// <summary>The project written beside the drawings, or null where none was.</summary>
    public string? ProjectPath { get; }

    public IReadOnlyList<string> Files { get; }

    public IReadOnlyList<PaintCodeImportNote> Notes { get; }
}
