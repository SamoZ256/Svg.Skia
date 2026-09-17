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
        var files = new List<string>();
        var project = options.ProjectPath is { } path
            ? SvgcProjectDocument.Empty(Path.GetDirectoryName(Path.GetFullPath(path)) ?? options.Directory)
            : null;

        if (project is { })
        {
            project.Root.Namespace = NamespaceOf(document, options);
        }

        foreach (var desk in Desks(document, options, notes))
        {
            var folder = Path.Combine(options.Directory, desk.Folder);
            SvgcProjectGroup? group = null;

            foreach (var canvas in desk.Drawings)
            {
                var file = Path.Combine(folder, canvas.Name + ".svg");
                Directory.CreateDirectory(folder);
                Write(canvas.Document, file);
                files.Add(file);

                if (project is null)
                {
                    continue;
                }

                group ??= Group(project, desk.Name);

                var drawing = group.AddDrawing(desk.Folder + "/" + canvas.Name + ".svg", group.Children.Count);
                drawing.Class = canvas.Class;

                // A drawing needs somewhere for its C# to go, or the build stops on the first row.
                // Beside the drawing is the answer that needs no decision; a project meant to fold
                // into one file says so afterwards, by naming a singleFile and clearing these.
                drawing.Output = desk.Folder + "/" + canvas.Name + ".cs";
            }
        }

        if (project is { } written)
        {
            written.Save(options.ProjectPath!);
        }

        return new PaintCodeImportResult(options.ProjectPath, files, notes);
    }

    /// <summary>The namespace the generated code sits in: the one asked for, or the document's own.</summary>
    public static string NamespaceOf(PaintCodeDocument document, PaintCodeImportOptions options)
        => options?.Namespace ?? PaintCodeSlug.Pascal((document ?? throw new ArgumentNullException(nameof(document))).Name);

    /// <summary>
    /// The drawings a document holds, desk by desk, without writing any of them.
    /// </summary>
    /// <remarks>
    /// For a caller that keeps the drawings rather than filing them — a Svg.Studio project holds
    /// them inline, and writing a thousand files to read them straight back would be both slower and
    /// less faithful than the documents themselves.
    ///
    /// A desk with nothing in it is left out: it would be a namespace with nothing in it, and a row
    /// in Studio that opens on nothing.
    /// </remarks>
    public static IReadOnlyList<PaintCodeImportDesk> Desks(
        PaintCodeDocument document,
        PaintCodeImportOptions options,
        ICollection<PaintCodeImportNote> notes)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (notes is null)
        {
            throw new ArgumentNullException(nameof(notes));
        }

        var declarations = PaintCodeDeclarations.Of(document, options.Integers);
        var symbols = PaintCodeSymbols.Of(document);
        var desks = new List<PaintCodeImportDesk>();

        foreach (var desk in document.Desks)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var drawings = new List<PaintCodeImportDrawing>();

            foreach (var canvas in desk.Canvases)
            {
                if (!canvas.IsExported && !options.IncludeSymbolOnlyCanvases)
                {
                    continue;
                }

                drawings.Add(new PaintCodeImportDrawing(
                    Unique(taken, PaintCodeSlug.Of(canvas.Name)),
                    PaintCodeSlug.Pascal(canvas.Name),
                    PaintCodeSvgWriter.Write(canvas, declarations, symbols, notes)));
            }

            if (drawings.Count > 0)
            {
                desks.Add(new PaintCodeImportDesk(PaintCodeSlug.Pascal(desk.Name), PaintCodeSlug.Of(desk.Name), drawings));
            }
        }

        return desks;
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
