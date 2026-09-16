// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Svg.CodeGen.Skia.Projects;

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
        var declarations = PaintCodeDeclarations.Of(document);
        var symbols = PaintCodeSymbols.Of(document);
        var files = new List<string>();
        var project = options.ProjectPath is { } path
            ? SvgcProjectDocument.Empty(Path.GetDirectoryName(Path.GetFullPath(path)) ?? options.Directory)
            : null;

        if (project is { })
        {
            project.Root.Namespace = options.Namespace ?? PaintCodeSlug.Pascal(document.Name);
        }

        foreach (var desk in document.Desks)
        {
            var slug = PaintCodeSlug.Of(desk.Name);
            var folder = Path.Combine(options.Directory, slug);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            SvgcProjectGroup? group = null;

            foreach (var canvas in desk.Canvases)
            {
                if (!canvas.IsExported && !options.IncludeSymbolOnlyCanvases)
                {
                    continue;
                }

                var name = Unique(taken, PaintCodeSlug.Of(canvas.Name));
                var file = Path.Combine(folder, name + ".svg");
                Directory.CreateDirectory(folder);
                Write(PaintCodeSvgWriter.Write(canvas, declarations, symbols, notes), file);
                files.Add(file);

                if (project is null)
                {
                    continue;
                }

                // A desk is only a group once something is in it: an empty one would generate a
                // namespace with nothing in it and show in Studio as a row that opens on nothing.
                group ??= Group(project, PaintCodeSlug.Pascal(desk.Name));

                var drawing = group.AddDrawing(slug + "/" + name + ".svg", group.Children.Count);
                drawing.Class = PaintCodeSlug.Pascal(canvas.Name);

                // A drawing needs somewhere for its C# to go, or the build stops on the first row.
                // Beside the drawing is the answer that needs no decision; a project meant to fold
                // into one file says so afterwards, by naming a singleFile and clearing these.
                drawing.Output = slug + "/" + name + ".cs";
            }
        }

        if (project is { } written)
        {
            written.Save(options.ProjectPath!);
        }

        return new PaintCodeImportResult(options.ProjectPath, files, notes);
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

        // Scoped so the handle is closed, not merely flushed, before the append: Windows refuses the
        // second open while the first is live, where POSIX allows it. Held open, this threw
        // IOException on every import on Windows and on no other platform.
        using (var writer = XmlWriter.Create(path, settings))
        {
            document.Save(writer);
        }

        File.AppendAllText(path, Environment.NewLine);
    }

    private static SvgcProjectGroup Group(SvgcProjectDocument project, string name)
    {
        var group = project.Root.AddGroup(project.Root.Children.Count);
        group.Namespace = project.Root.Namespace is { } root ? root + "." + name : name;

        return group;
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
