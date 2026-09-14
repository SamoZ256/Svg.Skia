// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Svg.PaintCode;

/// <summary>Converts a PaintCode document into a folder of Svg drawings.</summary>
public static class PaintCodeImport
{
    public static PaintCodeImportResult Run(string path, PaintCodeImportOptions options)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        return Run(PaintCodeDocument.Load(path), options);
    }

    public static PaintCodeImportResult Run(PaintCodeDocument document, PaintCodeImportOptions options)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var notes = new List<PaintCodeImportNote>();
        var files = new List<string>();

        foreach (var desk in document.Desks)
        {
            var folder = Path.Combine(options.Directory, PaintCodeSlug.Of(desk.Name));
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var canvas in desk.Canvases)
            {
                if (!canvas.IsExported && !options.IncludeSymbolOnlyCanvases)
                {
                    continue;
                }

                var file = Path.Combine(folder, Unique(taken, PaintCodeSlug.Of(canvas.Name)) + ".svg");
                Directory.CreateDirectory(folder);
                Write(PaintCodeSvgWriter.Write(canvas, notes), file);
                files.Add(file);
            }
        }

        return new PaintCodeImportResult(null, files, notes);
    }

    /// <summary>The drawing as text: two-space indent, no declaration, a newline at the end.</summary>
    /// <remarks>
    /// Written through <see cref="XDocument"/> rather than through the SVG writer in Svg.Custom, which
    /// adds a doctype, reorders attributes and rebuilds <c>style</c> from a dictionary — none of which
    /// a file someone is going to edit by hand wants.
    /// </remarks>
    internal static void Write(XDocument document, string path)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = true,
            Encoding = new UTF8Encoding(false)
        };

        using var writer = XmlWriter.Create(path, settings);
        document.Save(writer);
        writer.Flush();
        File.AppendAllText(path, Environment.NewLine);
    }

    private static string Unique(HashSet<string> taken, string name)
    {
        if (taken.Add(name))
        {
            return name;
        }

        for (var index = 2; ; index++)
        {
            var candidate = name + "-" + index;

            if (taken.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
