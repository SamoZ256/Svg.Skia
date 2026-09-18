// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions.Recipes;
using Svg.PaintCode;

namespace Svg.Studio;

/// <summary>Where a project comes from when it was not written as one.</summary>
/// <remarks>
/// Both converters answer the same question — what are the drawings, and what does each of them
/// build as — and neither is reversible: a .svgstudio holds the drawings, so what it is made from
/// stays where it is and is not written to again.
/// </remarks>
public static class ProjectImport
{
    /// <summary>
    /// An svgc project as a Svg.Studio one: every drawing it points at, read in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One way. A drawing named twice by the old project — one file, three sizes, which is what
    /// groups were for — becomes that many drawings, and editing one of them no longer edits the
    /// others.
    /// </para>
    /// <para>
    /// A recipe is baked in on the way through: the parameters it declared were what drove the
    /// drawings, and a conversion that dropped them would hand back a set of flat pictures. What
    /// each drawing ends up declaring is what the recipe put in it, which is also what makes a
    /// family of them share a slider again.
    /// </para>
    /// </remarks>
    /// <param name="path">The file the project is to be written to, which nothing writes here.</param>
    public static ProjectDocument FromSvgc(SvgcProjectDocument source, ICollection<string> notes, string path)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (notes is null)
        {
            throw new ArgumentNullException(nameof(notes));
        }

        var document = ProjectDocument.For(path);

        Carry(source.Root, document.Root);

        document.Root.Cache = source.Root.Cache;
        document.Root.HelperScope = source.Root.HelperScope;
        document.Root.SkiaSharp = source.Root.SkiaSharp;
        document.Root.SingleFile = source.Root.SingleFile;

        Convert(source.Root, document.Root, notes);

        return document;
    }

    /// <summary>A PaintCode document as a project: a group per desk, and every canvas drawn into it.</summary>
    /// <param name="path">The file the project is to be written to, which nothing writes here.</param>
    public static ProjectDocument FromPaintCode(
        PaintCodeDocument source,
        PaintCodeImportOptions options,
        ICollection<PaintCodeImportNote> notes,
        string path)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var document = ProjectDocument.For(path);

        document.Root.Namespace = PaintCodeImport.NamespaceOf(source, options);

        foreach (var desk in PaintCodeImport.Desks(source, options, notes))
        {
            var group = document.Root.AddGroup(desk.Name, document.Root.Children.Count);

            group.Namespace = document.Root.Namespace is { } root ? root + "." + desk.Name : desk.Name;

            foreach (var canvas in desk.Drawings)
            {
                // The document itself rather than the file the folder import writes, which is the
                // same drawing indented by a writer on the way past.
                var drawing = group.AddDrawing(canvas.Name, canvas.Document.Root!.ToString(), group.Children.Count);

                drawing.Class = canvas.Class;
            }
        }

        return document;
    }

    private static void Convert(SvgcProjectGroup source, ProjectGroup target, ICollection<string> notes)
    {
        foreach (var child in source.Children)
        {
            if (child is SvgcProjectGroup group)
            {
                var into = target.AddGroup(Named(group), target.Children.Count);

                Carry(group, into);
                Convert(group, into, notes);

                continue;
            }

            if (child is not SvgcProjectDrawing drawing)
            {
                continue;
            }

            if (Read(drawing, notes) is not { } svg)
            {
                continue;
            }

            var added = target.AddDrawing(
                Path.GetFileNameWithoutExtension(drawing.Input),
                svg,
                target.Children.Count);

            Carry(drawing, added);

            added.Output = drawing.Output;
        }
    }

    /// <summary>A drawing as the old project built it: the file, through whatever recipe covered it.</summary>
    private static string? Read(SvgcProjectDrawing drawing, ICollection<string> notes)
    {
        string text;

        try
        {
            text = File.ReadAllText(drawing.ResolvedInput);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            notes.Add($"{drawing.Input} could not be read, so it was left out: {failure.Message}");

            return null;
        }

        if (drawing.EffectiveResolvedRecipe is not { } recipe)
        {
            return text;
        }

        try
        {
            return SvgRecipeRewriter.Apply(text, SvgRecipe.Load(recipe)).Svg;
        }
        catch (Exception failure) when (failure is SvgRecipeException or IOException or UnauthorizedAccessException)
        {
            notes.Add($"{Path.GetFileName(recipe)} could not be applied to {drawing.Input}, which was carried across as it was written: {failure.Message}");

            return text;
        }
    }

    /// <summary>What a node said, less the two settings the format no longer has.</summary>
    private static void Carry(SvgcProjectNode source, ProjectNode target)
    {
        target.Namespace = source.Namespace;
        target.Class = source.Class;
        target.Padding = source.Padding;
        target.Width = source.Width;
        target.Height = source.Height;
        target.Scale = source.Scale;
    }

    /// <summary>
    /// What to call a group that was never named.
    /// </summary>
    /// <remarks>
    /// The old format had nothing to tell one group from another, so Studio labelled a row by the
    /// settings it handed down. Here a name is a name, and the last part of a namespace is the
    /// closest thing the old project said.
    /// </remarks>
    private static string Named(SvgcProjectGroup group)
        => new[] { group.Class, group.Namespace?.Split('.').Last() }
               .FirstOrDefault(part => !string.IsNullOrWhiteSpace(part))
           ?? "group";
}
