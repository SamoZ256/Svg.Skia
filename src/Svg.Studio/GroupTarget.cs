// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Svg.Expressions;
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

    /// <summary>What a tab is showing for a drawing, or null where no tab holds it.</summary>
    /// <remarks>
    /// Only a rename asks. A drawing open with edits nobody has saved is written twice — once here,
    /// into the project, and again when that tab is saved, out of a buffer that still says the old
    /// name. The second write is the one that lasts, so the rename would come back off that one
    /// drawing alone and say nothing. Rather than write into the buffer as well, which would put
    /// half the gesture on that tab's own history, the rename is refused while one is open.
    /// </remarks>
    public Func<ProjectDrawing, string?>? Held { get; set; }

    public string Text => _group.CodeText;

    /// <inheritdoc />
    /// <remarks>
    /// A rename is the one edit that does not land here alone. The block is where the declaration
    /// is, and the drawings under it are where the uses are, so both go in one gesture: renaming
    /// the block by itself leaves every drawing naming something that is gone, and leaves it
    /// silently — what a drawing reaches is what gets spliced into it, so the declaration is not
    /// even written out as the wrong name, it simply stops being written at all.
    /// </remarks>
    public string? Commit(string label, Func<SvgSourceDocument, string?> edit, SvgDeclarationRename? rename = null)
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
        // written back — the block is the only thing this edit itself touches.
        source.Inherited = ProjectDeclarations.Declared(_group);

        if (edit(source) is { } refusal)
        {
            return refusal;
        }

        var written = source.ToText();

        // Worked out before anything at all is written: a project where some drawings say the new
        // name and some the old is worse than one where the rename did not happen.
        if (Carried(rename, out var carried) is { } stopped)
        {
            return stopped;
        }

        if (string.Equals(written, text, StringComparison.Ordinal) && carried.Count == 0)
        {
            return null;
        }

        string? bad = null;

        _workspace.Do(
            label,
            () => ProjectSnapshot.All(
                new[] { ProjectSnapshot.Code(_group) }.Concat(carried.Select(one => Snapshot(one.Node))).ToArray()),
            () =>
            {
                bad = _group.SetCode(written);

                foreach (var one in carried)
                {
                    bad ??= Put(one.Node, one.Text);
                }
            });

        return bad;
    }

    /// <summary>
    /// Every document under this group with the rename already made, or the sentence stopping it.
    /// </summary>
    /// <remarks>
    /// The set <see cref="UsesElsewhere"/> counts over, rewritten rather than counted. Read and
    /// written into a list rather than applied as it goes, so that the refusal below can still be
    /// a refusal rather than half a rename to undo.
    /// </remarks>
    private string? Carried(SvgDeclarationRename? rename, out List<(ProjectNode Node, string Text)> carried)
    {
        carried = new List<(ProjectNode, string)>();

        if (rename is not { } renaming)
        {
            return null;
        }

        foreach (var drawing in _group.Drawings)
        {
            if (Held?.Invoke(drawing) is { } open
                && !string.Equals(open, drawing.Text, StringComparison.Ordinal))
            {
                return $"'{ProjectWorkspace.Label(drawing)}' has edits open in its tab that saving would "
                       + $"write over the rename, so '{renaming.From}' was not renamed. Save or close that tab first.";
            }

            if (Rewritten(drawing, drawing.Text, renaming, carried) is { } bad)
            {
                return bad;
            }
        }

        foreach (var below in Groups(_group))
        {
            if (Rewritten(below, below.CodeText, renaming, carried) is { } bad)
            {
                return bad;
            }
        }

        return null;
    }

    private static string? Rewritten(
        ProjectNode node,
        string text,
        SvgDeclarationRename renaming,
        List<(ProjectNode, string)> carried)
    {
        var where = ProjectWorkspace.Label(node);

        if (SvgSourceDocument.Read(text, out var unreadable) is not { } source)
        {
            return $"'{where}' could not be read, so '{renaming.From}' was not renamed anywhere, "
                   + $"the block included: {unreadable}";
        }

        // One set of names across params and lets, so renaming onto one a drawing declares itself
        // would leave that drawing refusing to build rather than merely saying something else.
        if (Declares(source, renaming.To))
        {
            return $"'{where}' declares '{renaming.To}' of its own, so renaming '{renaming.From}' to it "
                   + "would declare it twice. Nothing was renamed.";
        }

        // A document that declares the old name itself shadows this group's, so its uses are its
        // own. It is left to be rewritten with the rest rather than skipped: the chain refuses a
        // name declared twice, so a project in that state does not build either way, and a walk
        // that pruned around it would be a second answer to what is under a group.
        if (SvgDeclarationEditor.Rename(source, renaming.From, renaming.To) is { } trouble)
        {
            return $"'{where}' could not take the rename, so '{renaming.From}' was not renamed anywhere, "
                   + $"the block included: {trouble}";
        }

        // What SetText and SetCode check, asked here where a refusal is still only a refusal. Past
        // this, the write below cannot fail, which is what makes 'nothing was renamed' true.
        if (!Writable(node, source))
        {
            return $"'{where}' would not read back as itself after the rename, so nothing was renamed.";
        }

        var written = source.ToText();

        if (!string.Equals(written, text, StringComparison.Ordinal))
        {
            carried.Add((node, written));
        }

        return null;
    }

    /// <summary>Whether this document is still the kind of thing its node can be given back.</summary>
    private static bool Writable(ProjectNode node, SvgSourceDocument source)
        => source.Document.Root is { } root
           && (node is ProjectDrawing
               ? string.Equals(root.Name.LocalName, "svg", StringComparison.Ordinal)
               : root.Name == (XNamespace)SvgExpressionDeclarations.Namespace + "code");

    private static bool Declares(SvgSourceDocument source, string name)
        => source.Document
            .Descendants()
            .Any(element => element.Name.Namespace == (XNamespace)SvgExpressionDeclarations.Namespace
                            && string.Equals((string?)element.Attribute("name"), name, StringComparison.Ordinal));

    private static string? Put(ProjectNode node, string text)
        => node switch
        {
            ProjectDrawing drawing => drawing.SetText(text),
            ProjectGroup group => group.SetCode(text),
            _ => null
        };

    private static Action Snapshot(ProjectNode node)
        => node switch
        {
            ProjectDrawing drawing => ProjectSnapshot.Text(drawing),
            ProjectGroup group => ProjectSnapshot.Code(group),
            _ => () => { }
        };

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
