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
    public static ProjectDocument FromSvgc(SvgcProjectDocument source, ICollection<string> notes)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (notes is null)
        {
            throw new ArgumentNullException(nameof(notes));
        }

        // The old project's directory: what it carries over — an output, a single file — was written
        // relative to that, and stays true until somebody saves this one somewhere else.
        var document = ProjectDocument.Empty(source.BaseDirectory);

        Carry(source.Root, document.Root);

        document.Root.Cache = source.Root.Cache;
        document.Root.HelperScope = source.Root.HelperScope;
        document.Root.SkiaSharp = source.Root.SkiaSharp;
        document.Root.SingleFile = source.Root.SingleFile;

        Convert(source.Root, document.Root, notes);

        return document;
    }

    /// <summary>A PaintCode document as a project: a group per desk, and every canvas drawn into it.</summary>
    /// <param name="baseDirectory">Where the document came from, which its outputs resolve against.</param>
    public static ProjectDocument FromPaintCode(
        PaintCodeDocument source,
        PaintCodeImportOptions options,
        ICollection<PaintCodeImportNote> notes,
        string baseDirectory)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var document = ProjectDocument.Empty(baseDirectory);

        document.Root.Namespace = PaintCodeImport.NamespaceOf(source, options);

        var desks = PaintCodeImport.Desks(source, options, notes);
        var gap = Gap(desks);
        var below = 0f;

        foreach (var desk in desks)
        {
            var group = document.Root.AddGroup(desk.Name, document.Root.Children.Count);

            group.Namespace = document.Root.Namespace is { } root ? root + "." + desk.Name : desk.Name;

            var corner = Corner(desk);

            // Down the board in the order the document lists them, one column. A desk is a board of
            // its own and they say nothing about each other, so the only honest arrangement is the
            // one the tree already reads in — and a column leaves the room to the right of each
            // desk, which is where anything it could not place waits.
            group.X = 0f;
            group.Y = ProjectNode.Rounded(below);

            below += (corner is { } held ? (float)held.Height : gap) + gap;

            foreach (var canvas in desk.Drawings)
            {
                // The document itself rather than the file the folder import writes, which is the
                // same drawing indented by a writer on the way past.
                var drawing = group.AddDrawing(canvas.Name, canvas.Document.Root!.ToString(), group.Children.Count);

                drawing.Class = canvas.Class;

                if (canvas.Place is { } place && corner is { } from)
                {
                    (drawing.X, drawing.Y) = At(place, from);
                }
            }
        }

        return document;
    }

    /// <summary>
    /// The room to leave between two desks on the board.
    /// </summary>
    /// <remarks>
    /// Taken from what the board itself spaces things by rather than picked: a caption is five per
    /// cent of the largest drawing on the tab, and that is the unit every gap on a board is counted
    /// in. Six of them is the two frames' inflation, the room a spread leaves between its own, and
    /// the line a frame writes its name on above itself — which is what two desks need between them
    /// to read as two.
    ///
    /// It is also what an empty desk is given for a height, that being the box a frame round
    /// nothing is drawn at.
    /// </remarks>
    private static float Gap(IReadOnlyList<PaintCodeImportDesk> desks)
    {
        var largest = desks
            .SelectMany(desk => desk.Drawings)
            .Select(drawing => drawing.Place)
            .OfType<PaintCodeRect>()
            .SelectMany(place => new[] { place.Width, place.Height })
            .DefaultIfEmpty(0d)
            .Max();

        return MathF.Max((float)largest * 0.05f, 1f) * 6f;
    }

    /// <summary>
    /// Where a canvas goes on its desk's board, against the desk's own corner.
    /// </summary>
    /// <remarks>
    /// A subtraction on both axes, which assumes a canvas's bounds name its top left corner and
    /// that a desk's y grows downwards as a board's does. PaintCode is y-up inside a canvas — see
    /// <c>PaintCodeGeometry</c> — but that is about path points, which the writer has already turned
    /// over; the rect here is the same one whose width and height become a drawing measured from its
    /// top left, and the canvas carries isFlipped, which is a y-down view. Read the wrong way round
    /// this mirrors each desk vertically, and the second line below is the whole of the fix:
    /// (from.Y + from.Height) - (place.Y + place.Height).
    ///
    /// Board units are canvas points one for one: an imported drawing names no size, so what it is
    /// drawn at is the width and height of this same rect.
    /// </remarks>
    private static (float X, float Y) At(PaintCodeRect place, PaintCodeRect from)
        => (ProjectNode.Rounded((float)(place.X - from.X)),
            ProjectNode.Rounded((float)(place.Y - from.Y)));

    /// <summary>
    /// What a desk comes to: the union of what is placed on it, or null where nothing is.
    /// </summary>
    /// <remarks>
    /// Normalised against this rather than carried raw, because it is what the board would compute
    /// anyway: a group's place is the nearest corner of what it holds, so writing the desk's own
    /// numbers would have the first drag rewrite every row on the board to these. It also keeps the
    /// negatives a desk is free to use out of a file somebody has to read.
    /// </remarks>
    private static PaintCodeRect? Corner(PaintCodeImportDesk desk)
    {
        double? left = null, top = null, right = null, bottom = null;

        foreach (var place in desk.Drawings.Select(drawing => drawing.Place).OfType<PaintCodeRect>())
        {
            left = left is { } x ? Math.Min(x, place.X) : place.X;
            top = top is { } y ? Math.Min(y, place.Y) : place.Y;
            right = right is { } far ? Math.Max(far, place.X + place.Width) : place.X + place.Width;
            bottom = bottom is { } low ? Math.Max(low, place.Y + place.Height) : place.Y + place.Height;
        }

        return left is { } && top is { } && right is { } && bottom is { }
            ? new PaintCodeRect(left.Value, top.Value, right.Value - left.Value, bottom.Value - top.Value)
            : null;
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
