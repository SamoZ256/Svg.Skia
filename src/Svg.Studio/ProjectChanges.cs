// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Svg.Studio;

/// <summary>What happened to one node between two versions of a project.</summary>
public enum ProjectChangeKind
{
    Added,
    Removed,

    /// <summary>The drawing's <c>&lt;svg&gt;</c> differs, whatever else does too.</summary>
    Changed,

    /// <summary>Only where it is differs: its <c>x</c>/<c>y</c>, or the group or name it is filed under.</summary>
    Moved,

    /// <summary>The node's own settings differ: how the project itself and its groups show up.</summary>
    Settings
}

/// <summary>One row of <see cref="ProjectChanges.Compare"/>: a node as it was and as it is.</summary>
public sealed record ProjectChange(string Key, ProjectChangeKind Kind, ProjectNode? Before, ProjectNode? After);

/// <summary>How two versions of one project differ, node by node.</summary>
/// <remarks>
/// A node is the file it is saved in, so the two versions are matched by path. A drawing whose path
/// changed with nothing else is the one thing matched otherwise: by what it draws, so a rename or a
/// move to another group reads as one row moved rather than one removed and one added.
/// </remarks>
public static class ProjectChanges
{
    /// <summary>
    /// Where <paramref name="node"/> is saved, inside the project: <c>Large/badge.svg</c> for a
    /// drawing, <c>Large</c> for a group. The project itself is the empty string.
    /// </summary>
    /// <remarks>The path a save would give it, so a row nobody has saved yet has one too.</remarks>
    public static string KeyOf(ProjectNode node)
    {
        if (node is ProjectRoot)
        {
            return string.Empty;
        }

        if (node.Segment is null)
        {
            node.Owner.Root.Place();
        }

        return node.Parent is { } parent && KeyOf(parent) is { Length: > 0 } above
            ? above + "/" + node.Segment
            : node.Segment ?? string.Empty;
    }

    /// <summary>
    /// Every node that differs between <paramref name="before"/> and <paramref name="after"/>, the
    /// project's own settings first, then <paramref name="after"/>'s rows, then the rows it lost.
    /// </summary>
    /// <remarks>With no <paramref name="before"/> everything is Added.</remarks>
    public static IReadOnlyList<ProjectChange> Compare(ProjectDocument? before, ProjectDocument after)
    {
        if (after is null)
        {
            throw new ArgumentNullException(nameof(after));
        }

        after.Root.Place();
        before?.Root.Place();

        var was = before is { } ? Nodes(before.Root).ToDictionary(KeyOf, StringComparer.Ordinal) : new Dictionary<string, ProjectNode>();
        var now = Nodes(after.Root).ToList();
        var keys = now.Select(KeyOf).ToHashSet(StringComparer.Ordinal);
        var changes = new List<ProjectChange>();

        if (before is { } && Settings(before.Root) != Settings(after.Root))
        {
            changes.Add(new ProjectChange(string.Empty, ProjectChangeKind.Settings, before.Root, after.Root));
        }

        // Drawings gone from where they were, by what they draw, for a row that turns up elsewhere.
        var gone = was.Where(pair => !keys.Contains(pair.Key) && pair.Value is ProjectDrawing)
            .Select(pair => (ProjectDrawing)pair.Value)
            .ToLookup(drawing => Drawn(drawing), StringComparer.Ordinal);
        var taken = new HashSet<ProjectNode>();

        foreach (var node in now)
        {
            var key = KeyOf(node);

            if (was.TryGetValue(key, out var old) && old.GetType() == node.GetType())
            {
                if (Kind(old, node) is { } kind)
                {
                    changes.Add(new ProjectChange(key, kind, old, node));
                }

                continue;
            }

            if (node is ProjectDrawing drawing && gone[Drawn(drawing)].FirstOrDefault(one => !taken.Contains(one)) is { } moved)
            {
                taken.Add(moved);
                changes.Add(new ProjectChange(key, ProjectChangeKind.Moved, moved, node));

                continue;
            }

            changes.Add(new ProjectChange(key, ProjectChangeKind.Added, null, node));
        }

        foreach (var (key, old) in was)
        {
            if (!keys.Contains(key) && !taken.Contains(old))
            {
                changes.Add(new ProjectChange(key, ProjectChangeKind.Removed, old, null));
            }
        }

        return changes;
    }

    private static ProjectChangeKind? Kind(ProjectNode was, ProjectNode now)
    {
        if (now is ProjectDrawing drawing && Drawn(drawing) != Drawn((ProjectDrawing)was))
        {
            return ProjectChangeKind.Changed;
        }

        if (Settings(was) == Settings(now))
        {
            return null;
        }

        return now is ProjectDrawing && Settings(was, placed: false) == Settings(now, placed: false)
            ? ProjectChangeKind.Moved
            : ProjectChangeKind.Settings;
    }

    /// <summary>Every node under <paramref name="group"/>, each group before what it holds.</summary>
    private static IEnumerable<ProjectNode> Nodes(ProjectGroup group)
    {
        foreach (var child in group.Children)
        {
            yield return child;

            if (child is ProjectGroup inner)
            {
                foreach (var below in Nodes(inner))
                {
                    yield return below;
                }
            }
        }
    }

    /// <summary>What a drawing draws, with the line endings a checkout chose taken out of it.</summary>
    /// <remarks>
    /// Git for Windows checks a file out with CRLF by default, and a drawing compared as it reads
    /// on disk with one read back from a commit would otherwise differ on every line.
    /// </remarks>
    private static string Drawn(ProjectDrawing drawing) => drawing.Text.Replace("\r\n", "\n");

    /// <summary>A node's settings, and for a group what it declares, as one string to compare.</summary>
    private static string Settings(ProjectNode node, bool placed = true)
    {
        var attributes = node.Element.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration && (placed || attribute.Name.LocalName is not ("x" or "y")))
            .Select(attribute => attribute.Name.LocalName + "=" + attribute.Value)
            .OrderBy(text => text, StringComparer.Ordinal);

        var said = string.Join("\n", attributes);

        return node is ProjectGroup group
            ? said + "\n" + (group.Code is { } ? group.CodeText.Replace("\r\n", "\n") : string.Empty)
                   + (group is ProjectRoot root && root.TemplatesBlock is { } ? root.TemplatesText.Replace("\r\n", "\n") : string.Empty)
            : said;
    }
}

/// <summary>Which version of a conflicted file to keep.</summary>
public enum ProjectSide
{
    Ours,
    Theirs,

    /// <summary>Both, for a drawing added under one name on both sides: theirs is kept under a name of its own.</summary>
    Both
}

/// <summary>One file a merge left conflicted, with our and their version of it to keep one of.</summary>
public sealed class ProjectConflict
{
    public ProjectConflict(string path, string? ours, string? theirs, bool added)
    {
        Path = path;
        OursText = ours;
        TheirsText = theirs;
        Ours = Read(path, ours);
        Theirs = Read(path, theirs);
        IsAddition = added && ours is { } && theirs is { } && path.EndsWith(ProjectPackage.DrawingExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where the file is inside the project.</summary>
    public string Path { get; }

    /// <summary>The node our version is, or null where our side deleted it.</summary>
    public ProjectNode? Ours { get; }

    /// <summary>The node their version is, or null where their side deleted it.</summary>
    public ProjectNode? Theirs { get; }

    internal string? OursText { get; }

    internal string? TheirsText { get; }

    public ProjectSide Choice { get; set; } = ProjectSide.Ours;

    public bool IsDeletion => Ours is null || Theirs is null;

    /// <summary>Whether both sides added a drawing at this one path, so both can be kept.</summary>
    public bool IsAddition { get; }

    /// <summary>
    /// The node <paramref name="text"/> is, read as the one file of a project with nothing else in
    /// it, so a drawing is shown with no group above it to inherit from.
    /// </summary>
    private static ProjectNode? Read(string path, string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (path == ProjectPackage.Manifest)
        {
            return ProjectDocument.Read(new Dictionary<string, string> { [path] = text }, null).Root;
        }

        var leaf = path.EndsWith("/" + ProjectPackage.GroupFile, StringComparison.Ordinal)
            ? path.Substring(0, path.Length - ProjectPackage.GroupFile.Length - 1)
            : path;
        var lone = ProjectDocument.Read(
            new Dictionary<string, string>
            {
                [ProjectPackage.Manifest] = ProjectDocument.Blank,
                [path] = text
            },
            null);

        var node = (ProjectNode)lone.Root;

        foreach (var part in leaf.Split('/'))
        {
            node = ((ProjectGroup)node).Children.First(child => child.Segment == part);
        }

        return node;
    }
}

/// <summary>The files a merge left conflicted, each to be settled by keeping one side.</summary>
public sealed class ProjectMerge
{
    public ProjectMerge(IReadOnlyList<ProjectConflict> conflicts) => Conflicts = conflicts;

    public IReadOnlyList<ProjectConflict> Conflicts { get; }
}
