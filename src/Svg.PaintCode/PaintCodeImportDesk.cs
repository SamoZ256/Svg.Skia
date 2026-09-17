// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;
using System.Xml.Linq;

namespace Svg.PaintCode;

/// <summary>One desk of a PaintCode document, as an import reads it.</summary>
public sealed class PaintCodeImportDesk
{
    internal PaintCodeImportDesk(string name, string folder, IReadOnlyList<PaintCodeImportDrawing> drawings)
    {
        Name = name;
        Folder = folder;
        Drawings = drawings;
    }

    /// <summary>The desk's name as code spells it, which is the namespace its drawings sit in.</summary>
    public string Name { get; }

    /// <summary>The desk's name as a file system takes it.</summary>
    public string Folder { get; }

    public IReadOnlyList<PaintCodeImportDrawing> Drawings { get; }
}

/// <summary>One canvas, converted.</summary>
public sealed class PaintCodeImportDrawing
{
    internal PaintCodeImportDrawing(string name, string className, XDocument document)
    {
        Name = name;
        Class = className;
        Document = document;
    }

    /// <summary>The canvas's name as a file system takes it, made unique within its desk.</summary>
    public string Name { get; }

    /// <summary>The canvas's name as code spells it.</summary>
    public string Class { get; }

    /// <summary>The drawing itself.</summary>
    public XDocument Document { get; }
}
