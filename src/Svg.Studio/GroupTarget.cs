// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Linq;
using Svg.SourceEditing;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>A group in the project, as somewhere declarations can be written.</summary>
/// <remarks>
/// What is handed to the editor is the group's <c>&lt;e:code&gt;</c> block on its own, rather than a
/// document with a block in it. There is nothing else in a group to write — no tree, no
/// <c>&lt;defs&gt;</c>, no drawing — and a block that is its own document is a case
/// <see cref="SvgDeclarationEditor"/> already knows, since an svgc recipe is written the same way.
///
/// Like <see cref="DrawingTarget"/>, the edit goes straight into the project rather than into a
/// buffer, so there is nothing to take back with ⌘Z and no mark of its own; the project carries it,
/// and the title says the project is unsaved.
/// </remarks>
public sealed class GroupTarget : ISvgViewerDeclarationTarget, IEquatable<GroupTarget>
{
    private readonly ProjectWorkspace _workspace;
    private readonly ProjectGroup _group;

    public GroupTarget(ProjectWorkspace workspace, ProjectGroup group)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _group = group ?? throw new ArgumentNullException(nameof(group));
    }

    public ProjectGroup Group => _group;

    public string Text => _group.CodeText;

    /// <inheritdoc />
    public string? Commit(string label, Func<SvgSourceDocument, string?> edit)
    {
        if (edit is null)
        {
            throw new ArgumentNullException(nameof(edit));
        }

        var text = _group.CodeText;

        if (SvgSourceDocument.Read(text, out var unreadable) is not { } source)
        {
            return unreadable;
        }

        // What the groups above declare, so a let written here can name it. Nothing of it is
        // written back — the edit lands in this group's own text and nowhere else.
        source.Inherited = ProjectDeclarations.Declared(_group);

        if (edit(source) is { } refusal)
        {
            return refusal;
        }

        var written = source.ToText();

        if (string.Equals(written, text, StringComparison.Ordinal))
        {
            return null;
        }

        string? bad = null;

        _workspace.Do(label, () => ProjectSnapshot.Code(_group), () => bad = _group.SetCode(written));

        return bad;
    }

    /// <summary>The group is the identity: two targets over one group are one place to write.</summary>
    /// <remarks>
    /// So that a caller grouping rows by where they are written — <c>SetDefaults</c> does — does not
    /// make one write per row because it was handed a fresh target for each.
    /// </remarks>
    public bool Equals(GroupTarget? other) => other is { } && ReferenceEquals(_group, other._group);

    public override bool Equals(object? obj) => Equals(obj as GroupTarget);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_group);

    /// <inheritdoc />
    /// <remarks>
    /// The whole branch, not only the drawings: a group nested under this one may name an inherited
    /// parameter in a let of its own, and taking the parameter away would leave that let naming
    /// nothing.
    /// </remarks>
    public int UsesElsewhere(string name)
    {
        var used = 0;

        foreach (var drawing in _group.Drawings)
        {
            used += Counted(drawing.Text, name);
        }

        foreach (var below in Groups(_group))
        {
            used += Counted(below.CodeText, name);
        }

        return used;
    }

    private static System.Collections.Generic.IEnumerable<ProjectGroup> Groups(ProjectGroup group)
        => group.Children.OfType<ProjectGroup>().SelectMany(child => new[] { child }.Concat(Groups(child)));

    private static int Counted(string text, string name)
        => SvgSourceDocument.Read(text, out _) is { } source ? SvgDeclarationEditor.Uses(source, name) : 0;
}
