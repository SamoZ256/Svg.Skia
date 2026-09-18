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
    internal PaintCodeImportDrawing(string name, string className, PaintCodeRect? place, XDocument document)
    {
        Name = name;
        Class = className;
        Place = place;
        Document = document;
    }

    /// <summary>The canvas's name as a file system takes it, made unique within its desk.</summary>
    public string Name { get; }

    /// <summary>The canvas's name as code spells it.</summary>
    public string Class { get; }

    /// <summary>
    /// Where the canvas sat on its desk, in PaintCode points, or null where the document did not say.
    /// </summary>
    /// <remarks>
    /// The whole rect and not the corner alone: what a desk comes to is the union of what is on it,
    /// so a consumer laying the desks out needs the sizes as well. As the document wrote it, with
    /// nothing normalised — where a desk's corner should be is a question for whoever is arranging
    /// them, and the answer differs between a folder of files and a board.
    /// </remarks>
    public PaintCodeRect? Place { get; }

    /// <summary>The drawing itself.</summary>
    public XDocument Document { get; }
}
