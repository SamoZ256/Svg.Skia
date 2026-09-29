// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Svg.CodeGen.Skia.Projects;

namespace Svg.Studio;

/// <summary>What happened to one node between two versions of a project.</summary>
public enum ProjectChangeKind
{
    Added,
    Removed,

    /// <summary>The drawing's <c>&lt;svg&gt;</c> differs, whatever else does too.</summary>
    Changed,

    /// <summary>Only its place differs: the drawing's <c>x</c>/<c>y</c>, or where it sits among its siblings.</summary>
    Moved,

    /// <summary>The node's own attributes differ: how the project itself and its groups show up.</summary>
    Settings
}

/// <summary>One row of <see cref="ProjectChanges.Compare"/>: a node as it was and as it is.</summary>
public sealed record ProjectChange(string Key, ProjectChangeKind Kind, ProjectNode? Before, ProjectNode? After);

/// <summary>How two versions of one project file differ, node by node.</summary>
/// <remarks>
/// Nodes are matched by name and content (<see cref="Paired"/>), never by position alone: names are
/// not unique, and a drawing inserted above another would otherwise read as every row below it having
/// changed.
/// </remarks>
public static class ProjectChanges
{
    private const string SvgAspect = "<svg>";
    private const string CodeAspect = "<code>";
    private const string PlaceAspect = "<place>";

    /// <summary>
    /// The group path to <paramref name="node"/>, each step its name and its position among
    /// same-named siblings (<c>Large#0/badge#1</c>). The project itself is the empty string.
    /// </summary>
    public static string KeyOf(ProjectNode node)
    {
        if (node is ProjectRoot)
        {
            return string.Empty;
        }

        var local = LocalKey(node);

        return node.Parent is { } parent && KeyOf(parent) is { Length: > 0 } above ? above + "/" + local : local;
    }

    /// <summary>
    /// Every node that differs between <paramref name="before"/> and <paramref name="after"/>, in
    /// document order, removed rows where they used to be.
    /// </summary>
    /// <remarks>
    /// A group added or removed is listed with every node under it, so the drawings it brought or
    /// took are rows of their own. With no <paramref name="before"/> everything is Added.
    /// </remarks>
    public static IReadOnlyList<ProjectChange> Compare(ProjectDocument? before, ProjectDocument after)
    {
        if (after is null)
        {
            throw new ArgumentNullException(nameof(after));
        }

        var changes = new List<ProjectChange>();

        if (before is { } && !Same(Aspects(before.Root), Aspects(after.Root)))
        {
            changes.Add(new ProjectChange(string.Empty, ProjectChangeKind.Settings, before.Root, after.Root));
        }

        CompareChildren(before?.Root, after.Root, changes);

        return changes;
    }

    private static void CompareChildren(ProjectGroup? before, ProjectGroup after, List<ProjectChange> changes)
    {
        var pairs = before is { }
            ? Paired(before, after).Where(pair => pair.Key.GetType() == pair.Value.GetType()).ToDictionary(pair => pair.Key, pair => pair.Value)
            : new Dictionary<ProjectNode, ProjectNode>();
        var kept = pairs.Values.ToHashSet();
        var reordered = Reordered(after.Children.Where(pairs.ContainsKey).ToList(), now => IndexOf(before!, pairs[now]));
        var shown = 0;

        foreach (var now in after.Children)
        {
            if (!pairs.TryGetValue(now, out var was))
            {
                Listed(now, ProjectChangeKind.Added, changes);
                continue;
            }

            var at = IndexOf(before!, was);

            // The rows before this one that are gone, so a removal reads where it used to sit.
            for (; shown < at; shown++)
            {
                RemovedUnless(before!.Children[shown], kept, changes);
            }

            shown = Math.Max(shown, at + 1);

            if ((Kind(was, now) ?? (reordered.Contains(now) ? ProjectChangeKind.Moved : null)) is { } kind)
            {
                changes.Add(new ProjectChange(KeyOf(now), kind, was, now));
            }

            if (now is ProjectGroup group)
            {
                CompareChildren((ProjectGroup)was, group, changes);
            }
        }

        for (; before is { } && shown < before.Children.Count; shown++)
        {
            RemovedUnless(before.Children[shown], kept, changes);
        }
    }

    private static void RemovedUnless(ProjectNode was, HashSet<ProjectNode> kept, List<ProjectChange> changes)
    {
        if (!kept.Contains(was))
        {
            Listed(was, ProjectChangeKind.Removed, changes);
        }
    }

    private static void Listed(ProjectNode node, ProjectChangeKind kind, List<ProjectChange> changes)
    {
        changes.Add(kind == ProjectChangeKind.Added
            ? new ProjectChange(KeyOf(node), kind, null, node)
            : new ProjectChange(KeyOf(node), kind, node, null));

        if (node is ProjectGroup group)
        {
            foreach (var child in group.Children)
            {
                Listed(child, kind, changes);
            }
        }
    }

    private static ProjectChangeKind? Kind(ProjectNode was, ProjectNode now)
    {
        var before = Aspects(was);
        var after = Aspects(now);

        if (Same(before, after))
        {
            return null;
        }

        if (now is not ProjectDrawing)
        {
            return ProjectChangeKind.Settings;
        }

        if (Value(before, SvgAspect) != Value(after, SvgAspect))
        {
            return ProjectChangeKind.Changed;
        }

        return Same(before, after, PlaceAspect) ? ProjectChangeKind.Moved : ProjectChangeKind.Settings;
    }

    /// <summary>A node's name and its position among same-named siblings, which is its key within its group.</summary>
    internal static string LocalKey(ProjectNode node)
    {
        var name = node.Name;
        var siblings = node.Parent?.Children ?? Array.Empty<ProjectNode>();
        var occurrence = 0;

        foreach (var sibling in siblings)
        {
            if (ReferenceEquals(sibling, node))
            {
                break;
            }

            if (sibling.Name == name)
            {
                occurrence++;
            }
        }

        return name + "#" + occurrence;
    }

    internal static int IndexOf(ProjectGroup group, ProjectNode child)
    {
        for (var i = 0; i < group.Children.Count; i++)
        {
            if (ReferenceEquals(group.Children[i], child))
            {
                return i;
            }
        }

        return -1;
    }

    internal static Dictionary<string, ProjectNode> Keyed(ProjectGroup group)
        => group.Children.ToDictionary(LocalKey);

    /// <summary>Each child of <paramref name="side"/> that continues a child of <paramref name="base"/>, with that child.</summary>
    /// <remarks>
    /// Per name, the longest run of children written identically is paired first, and only what is
    /// left between two such pairs is paired by position. By position alone, a drawing inserted or
    /// deleted ahead of a same-named one shifts every one after it onto its neighbour.
    /// </remarks>
    internal static Dictionary<ProjectNode, ProjectNode> Paired(ProjectGroup @base, ProjectGroup side)
    {
        var pairs = new Dictionary<ProjectNode, ProjectNode>();
        var named = @base.Children.ToLookup(child => child.Name);

        foreach (var run in side.Children.GroupBy(child => child.Name))
        {
            var was = named[run.Key].ToList();
            var now = run.ToList();
            var before = was.Select(Written).ToList();
            var after = now.Select(Written).ToList();
            var longest = new int[was.Count + 1, now.Count + 1];

            for (var i = was.Count - 1; i >= 0; i--)
            {
                for (var j = now.Count - 1; j >= 0; j--)
                {
                    longest[i, j] = before[i] == after[j]
                        ? longest[i + 1, j + 1] + 1
                        : Math.Max(longest[i + 1, j], longest[i, j + 1]);
                }
            }

            int b = 0, s = 0, fromB = 0, fromS = 0;

            while (b < was.Count && s < now.Count)
            {
                if (before[b] == after[s] && longest[b, s] == longest[b + 1, s + 1] + 1)
                {
                    Gap();
                    pairs[now[s++]] = was[b++];
                    (fromB, fromS) = (b, s);
                }
                else if (longest[b + 1, s] >= longest[b, s + 1])
                {
                    b++;
                }
                else
                {
                    s++;
                }
            }

            (b, s) = (was.Count, now.Count);
            Gap();

            void Gap()
            {
                for (var k = 0; fromB + k < b && fromS + k < s; k++)
                {
                    pairs[now[fromS + k]] = was[fromB + k];
                }
            }
        }

        return pairs;

        static string Written(ProjectNode node) => node.GetType().Name + node.Owner.Source.TextOf(node.Element);
    }

    /// <summary>
    /// Which of <paramref name="nodes"/> are out of the order <paramref name="rank"/> gives them: all
    /// but the longest run already in it.
    /// </summary>
    internal static HashSet<ProjectNode> Reordered(IReadOnlyList<ProjectNode> nodes, Func<ProjectNode, int> rank)
    {
        var ranks = nodes.Select(rank).ToList();
        var length = new int[ranks.Count];
        var previous = new int[ranks.Count];
        var last = -1;

        for (var i = 0; i < ranks.Count; i++)
        {
            (length[i], previous[i]) = (1, -1);

            // The latest of the longest throughout, so a row dragged up reads as the one that moved
            // rather than the row it passed.
            for (var j = 0; j < i; j++)
            {
                if (ranks[j] < ranks[i] && length[j] + 1 >= length[i])
                {
                    (length[i], previous[i]) = (length[j] + 1, j);
                }
            }

            if (last < 0 || length[i] >= length[last])
            {
                last = i;
            }
        }

        var moved = nodes.ToHashSet();

        for (var i = last; i >= 0; i = previous[i])
        {
            moved.Remove(nodes[i]);
        }

        return moved;
    }

    /// <summary>
    /// Everything about a node that is its own, one entry per thing that can change separately: each
    /// attribute, its place as one, and its <c>&lt;svg&gt;</c> or its declarations.
    /// </summary>
    /// <remarks>
    /// x/y are one entry because a place is both or neither — merged one at a time they could come out
    /// half of each side. Text is compared without its indentation, which a move to another depth changes.
    /// </remarks>
    internal static Dictionary<string, string?> Aspects(ProjectNode? node)
    {
        var aspects = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (node is null)
        {
            return aspects;
        }

        foreach (var attribute in node.Element.Attributes())
        {
            var name = attribute.Name.ToString();

            if (!attribute.IsNamespaceDeclaration && name is not ("x" or "y"))
            {
                aspects[name] = attribute.Value;
            }
        }

        if (node is not ProjectRoot)
        {
            aspects[PlaceAspect] = (string?)node.Element.Attribute("x") is { } x
                ? x + "," + (string?)node.Element.Attribute("y")
                : null;
        }

        switch (node)
        {
            case ProjectDrawing drawing:
                aspects[SvgAspect] = Dedented(drawing.Text);
                break;
            case ProjectGroup group:
                aspects[CodeAspect] = group.Code is { } code ? Dedented(node.Owner.Source.TextOf(code)) : null;
                break;
        }

        return aspects;

        // Line endings too: with core.autocrlf, HEAD reads back CRLF while Studio writes the file LF.
        static string Dedented(string text) => string.Join('\n', text.Split('\n').Select(line => line.TrimStart().TrimEnd('\r')));
    }

    internal static string? Value(Dictionary<string, string?> aspects, string aspect)
        => aspects.TryGetValue(aspect, out var value) ? value : null;

    /// <summary>Whether two nodes' aspects agree, all of them or all but <paramref name="except"/>.</summary>
    internal static bool Same(Dictionary<string, string?> left, Dictionary<string, string?> right, string? except = null)
        => left.Keys.Union(right.Keys).All(aspect => aspect == except || Value(left, aspect) == Value(right, aspect));

    /// <summary>Whether a node and everything under it is written the same in both.</summary>
    internal static bool Identical(ProjectNode left, ProjectNode right)
        => left.GetType() == right.GetType()
           && left.Owner.Source.TextOf(left.Element) == right.Owner.Source.TextOf(right.Element);

    /// <summary>A node and everything under it with its name, its place and its layout left out: what a move or a rename keeps.</summary>
    internal static string Shape(ProjectNode node)
    {
        var copy = new XElement(node.Element);

        copy.SetAttributeValue("name", null);
        copy.SetAttributeValue("x", null);
        copy.SetAttributeValue("y", null);

        foreach (var space in copy.DescendantNodes().OfType<XText>().Where(text => text is not XCData && text.Value.Trim().Length == 0).ToList())
        {
            space.Remove();
        }

        return node.GetType().Name + copy.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Makes <paramref name="target"/>'s <paramref name="aspect"/> what <paramref name="source"/>'s is.</summary>
    internal static void Take(ProjectNode target, ProjectNode source, string aspect)
    {
        switch (aspect)
        {
            case SvgAspect:
                Refused(((ProjectDrawing)target).SetText(((ProjectDrawing)source).Text));
                break;
            case CodeAspect:
                // CodeText is the empty block where there is none, which SetCode reads as taking it out.
                Refused(((ProjectGroup)target).SetCode(((ProjectGroup)source).CodeText));
                break;
            case PlaceAspect:
                target.Element.SetAttributeValue("x", (string?)source.Element.Attribute("x"));
                target.Element.SetAttributeValue("y", (string?)source.Element.Attribute("y"));
                break;
            default:
                target.Element.SetAttributeValue(XName.Get(aspect), (string?)source.Element.Attribute(XName.Get(aspect)));
                break;
        }

        static void Refused(string? refusal)
        {
            if (refusal is { })
            {
                throw new SvgcProjectException(refusal);
            }
        }
    }
}

/// <summary>Which side of a merge a conflict is settled for.</summary>
public enum ProjectSide
{
    Ours,
    Theirs
}

/// <summary>One node both sides of a merge changed, and could not both be kept.</summary>
public sealed class ProjectConflict
{
    internal ProjectConflict(string key, ProjectNode? @base, ProjectNode? ours, ProjectNode? theirs)
    {
        Key = key;
        Base = @base;
        Ours = ours;
        Theirs = theirs;
    }

    /// <summary>The node's <see cref="ProjectChanges.KeyOf"/>.</summary>
    public string Key { get; }

    /// <summary>The node as both sides started from, or null where both added it.</summary>
    public ProjectNode? Base { get; }

    /// <summary>Our node, or null where we deleted it.</summary>
    public ProjectNode? Ours { get; }

    /// <summary>Their node, or null where they deleted it.</summary>
    public ProjectNode? Theirs { get; }

    /// <summary>The side <see cref="ProjectMerge.ToXml"/> takes this node from.</summary>
    public ProjectSide Choice { get; set; } = ProjectSide.Ours;

    /// <summary>Whether one side deleted the node and the other changed it, so the choice is keep or delete.</summary>
    public bool IsDeletion => Ours is null || Theirs is null;
}

/// <summary>
/// A three-way merge of one project file: ours with their changes laid over it, node by node.
/// </summary>
/// <remarks>
/// <para>
/// Built on a fresh parse of ours, so everything the merge does not touch keeps our bytes. A node
/// changed on one side takes that side; changed the same way on both, it is kept; changed differently,
/// it is a <see cref="ProjectConflict"/>. Attributes, a place and a drawing's svg are merged
/// separately, so one side moving a drawing while the other redraws it is not a conflict.
/// </para>
/// <para>
/// A node they added is copied in after the sibling it follows on their side, past anything we
/// added there, so both sides appending to one group — what git's line merge cannot do — keeps both.
/// A node one side took out and put back elsewhere or under another name, unchanged otherwise, is
/// that node moved, and the other side's changes to it follow it there.
/// </para>
/// </remarks>
public sealed class ProjectMerge
{
    private readonly ProjectDocument? _base;
    private readonly ProjectDocument _ours;
    private readonly ProjectDocument _theirs;
    private readonly string _oursText;
    private readonly Side _mine;
    private readonly Side _their;
    private readonly List<ProjectConflict> _conflicts = new();

    // Counts conflicts as a run meets them. A run's walk and what it counts as a conflict depend on
    // the three inputs only, never on a choice, so the n-th one met is always the same conflict.
    private int _met;

    private ProjectMerge(ProjectDocument? @base, ProjectDocument ours, ProjectDocument theirs)
    {
        // Read again rather than held: the caller's documents may go on being edited, and Copy
        // reads their text from the node's own document.
        _base = @base is { } b ? ProjectDocument.Parse(b.ToXml(), b.Path) : null;
        _oursText = ours.ToXml();
        _ours = ProjectDocument.Parse(_oursText, ours.Path);
        _theirs = ProjectDocument.Parse(theirs.ToXml(), theirs.Path);
        _mine = new Side(_base, _ours);
        _their = new Side(_base, _theirs);
    }

    /// <summary>Merges <paramref name="theirs"/> into <paramref name="ours"/>; a null base means both sides added the file.</summary>
    public static ProjectMerge Of(ProjectDocument? @base, ProjectDocument ours, ProjectDocument theirs)
    {
        var merge = new ProjectMerge(
            @base,
            ours ?? throw new ArgumentNullException(nameof(ours)),
            theirs ?? throw new ArgumentNullException(nameof(theirs)));

        merge.Run(recording: true);

        return merge;
    }

    /// <summary>What the two sides could not agree on, in document order.</summary>
    public IReadOnlyList<ProjectConflict> Conflicts => _conflicts;

    /// <summary>The merged file, with each conflict settled as its <see cref="ProjectConflict.Choice"/> says now.</summary>
    public string ToXml() => Run(recording: false).ToXml();

    private ProjectDocument Run(bool recording)
    {
        _met = 0;

        var result = ProjectDocument.Parse(_oursText, _ours.Path);

        MergeNode(_base?.Root, _ours.Root, _theirs.Root, result.Root, recording);
        MergeChildren(_base?.Root, _ours.Root, _theirs.Root, result.Root, recording);

        return result;
    }

    private ProjectConflict Conflict(ProjectNode? @base, ProjectNode? ours, ProjectNode? theirs, bool recording)
    {
        if (recording)
        {
            _conflicts.Add(new ProjectConflict(ProjectChanges.KeyOf((ours ?? theirs)!), @base, ours, theirs));
        }

        return _conflicts[_met++];
    }

    /// <summary>Merges one node's own aspects into <paramref name="result"/>, which is our node in the result.</summary>
    private void MergeNode(ProjectNode? @base, ProjectNode ours, ProjectNode theirs, ProjectNode result, bool recording)
    {
        var b = ProjectChanges.Aspects(@base);
        var o = ProjectChanges.Aspects(ours);
        var t = ProjectChanges.Aspects(theirs);
        var divergent = new List<string>();

        foreach (var aspect in o.Keys.Union(t.Keys).ToList())
        {
            var mine = ProjectChanges.Value(o, aspect);
            var their = ProjectChanges.Value(t, aspect);

            if (mine == their || (@base is { } && ProjectChanges.Value(b, aspect) == their))
            {
                continue;
            }

            if (@base is { } && ProjectChanges.Value(b, aspect) == mine)
            {
                ProjectChanges.Take(result, theirs, aspect);
            }
            else
            {
                divergent.Add(aspect);
            }
        }

        if (divergent.Count > 0 && Conflict(@base, ours, theirs, recording).Choice == ProjectSide.Theirs)
        {
            foreach (var aspect in divergent)
            {
                ProjectChanges.Take(result, theirs, aspect);
            }
        }
    }

    private void MergeChildren(ProjectGroup? @base, ProjectGroup ours, ProjectGroup theirs, ProjectGroup result, bool recording)
    {
        if (@base is null)
        {
            MergeAdditions(ours, theirs, result, recording);
            return;
        }

        var placed = Placed(ours, result);

        foreach (var mine in ours.Children)
        {
            Walk(mine, result, placed, recording);
        }

        Reorder(@base, ours, theirs, result, placed);

        var rows = new Dictionary<ProjectNode, ProjectNode>();
        var twins = new Dictionary<ProjectNode, ProjectNode>();

        foreach (var their in theirs.Children)
        {
            if (_their.BaseOf.TryGetValue(their, out var was))
            {
                _mine.Of.TryGetValue(was, out var mine);

                if (mine is { } && (!_their.Moved.Contains(their) || _mine.Moved.Contains(mine) || mine.GetType() != their.GetType()))
                {
                    // Merged where our walk met it: in place, where we moved it, or replaced whole.
                    continue;
                }

                if (mine is { })
                {
                    // Moved by them and left where it was by us: ours goes where they put it, with
                    // their changes laid over it.
                    rows[their] = Carried(was, mine, their, result, At(their), recording);
                }
                else if ((_their.Moved.Contains(their) || !ProjectChanges.Identical(was, their))
                         && Conflict(was, null, their, recording).Choice == ProjectSide.Theirs)
                {
                    // We deleted what they changed or moved.
                    rows[their] = Copied(their, At(their), result);
                }

                continue;
            }

            // Added by them, and kept once where we added the very same.
            if (ours.Children.FirstOrDefault(one => !_mine.BaseOf.ContainsKey(one) && !twins.ContainsValue(one)
                                                    && ProjectChanges.Identical(one, their)) is { } same)
            {
                twins[their] = same;
                continue;
            }

            rows[their] = Copied(their, At(their), result, carry: true, recording);
        }

        int At(ProjectNode their) => After(
            their,
            theirs,
            result,
            one => rows.TryGetValue(one, out var row) ? row
                : twins.TryGetValue(one, out var same) && placed.TryGetValue(same, out row) ? row
                : _their.BaseOf.TryGetValue(one, out var was) && _mine.Of.TryGetValue(was, out var mine)
                  && placed.TryGetValue(mine, out row) ? row
                : null,
            row => placed.Any(pair => ReferenceEquals(pair.Value, row)
                                      && (!_mine.BaseOf.ContainsKey(pair.Key) || _mine.Moved.Contains(pair.Key))
                                      && !twins.ContainsValue(pair.Key)));
    }

    /// <summary>Our node against what it was and what they made of it; <paramref name="placed"/> maps our nodes to their rows.</summary>
    private void Walk(ProjectNode mine, ProjectGroup result, Dictionary<ProjectNode, ProjectNode> placed, bool recording)
    {
        var row = placed[mine];

        if (!_mine.BaseOf.TryGetValue(mine, out var was))
        {
            // Ours alone, though what is in it may be something we moved there.
            if (mine is ProjectGroup group)
            {
                var inner = Placed(group, (ProjectGroup)row);

                foreach (var child in group.Children)
                {
                    Walk(child, (ProjectGroup)row, inner, recording);
                }
            }

            return;
        }

        _their.Of.TryGetValue(was, out var their);

        if (their is null)
        {
            // They deleted it: gone if we left it alone, a conflict if we did not.
            if ((!_mine.Moved.Contains(mine) && ProjectChanges.Identical(was, mine))
                || Conflict(was, mine, null, recording).Choice == ProjectSide.Theirs)
            {
                result.Remove(row);
                placed.Remove(mine);
            }
        }
        else if (_their.Moved.Contains(their) && !_mine.Moved.Contains(mine) && mine.GetType() == their.GetType())
        {
            // They moved it, so it goes where they put it, which their side's walk sees to.
            result.Remove(row);
            placed.Remove(mine);
        }
        else if (mine.GetType() != their.GetType())
        {
            Replaced(was, mine, their, row, result, placed, recording);
        }
        else
        {
            MergeNode(was, mine, their, row, recording);

            if (mine is ProjectGroup group)
            {
                MergeChildren(was as ProjectGroup, group, (ProjectGroup)their, (ProjectGroup)row, recording);
            }
        }
    }

    /// <summary>
    /// Puts siblings both sides kept in their order where only they reordered them; where both did,
    /// ours stands.
    /// </summary>
    private void Reorder(
        ProjectGroup @base,
        ProjectGroup ours,
        ProjectGroup theirs,
        ProjectGroup result,
        Dictionary<ProjectNode, ProjectNode> placed)
    {
        var kept = ours.Children
            .Where(mine => placed.ContainsKey(mine) && !_mine.Moved.Contains(mine)
                           && _mine.BaseOf.TryGetValue(mine, out var was) && _their.Of.TryGetValue(was, out var their)
                           && ReferenceEquals(their.Parent, theirs) && !_their.Moved.Contains(their))
            .ToList();

        var bases = kept.Select(mine => _mine.BaseOf[mine]).ToList();
        var mineOrder = bases.Select(one => ProjectChanges.IndexOf(@base, one)).ToList();
        var theirOrder = bases.Select(one => ProjectChanges.IndexOf(theirs, _their.Of[one])).ToList();

        if (!Ascending(mineOrder) || Ascending(theirOrder))
        {
            return;
        }

        var wanted = kept.Select((mine, i) => (Row: placed[mine], Rank: theirOrder[i])).OrderBy(one => one.Rank).Select(one => one.Row).ToList();

        for (var i = 0; i < wanted.Count; i++)
        {
            var slots = wanted.Select(row => ProjectChanges.IndexOf(result, row)).OrderBy(index => index).ToList();

            if (ProjectChanges.IndexOf(result, wanted[i]) != slots[i])
            {
                result.Move(wanted[i], slots[i]);
            }
        }

        static bool Ascending(List<int> order) => order.Zip(order.Skip(1)).All(pair => pair.First < pair.Second);
    }

    /// <summary>Both sides' children where there is no base to tell a change from an addition, matched by key.</summary>
    private void MergeAdditions(ProjectGroup ours, ProjectGroup theirs, ProjectGroup result, bool recording)
    {
        var t = ProjectChanges.Keyed(theirs);
        var o = ProjectChanges.Keyed(ours);
        var placed = Placed(ours, result);
        var rows = new Dictionary<ProjectNode, ProjectNode>();

        foreach (var mine in ours.Children)
        {
            if (!t.TryGetValue(ProjectChanges.LocalKey(mine), out var their))
            {
                continue;
            }

            if (mine.GetType() != their.GetType())
            {
                Replaced(null, mine, their, placed[mine], result, placed, recording);
            }
            else
            {
                MergeNode(null, mine, their, placed[mine], recording);

                if (mine is ProjectGroup group)
                {
                    MergeAdditions(group, (ProjectGroup)their, (ProjectGroup)placed[mine], recording);
                }
            }
        }

        foreach (var their in theirs.Children)
        {
            if (!o.ContainsKey(ProjectChanges.LocalKey(their)))
            {
                rows[their] = Copied(their, After(
                    their,
                    theirs,
                    result,
                    one => rows.TryGetValue(one, out var row) ? row
                        : o.TryGetValue(ProjectChanges.LocalKey(one), out var mine) && placed.TryGetValue(mine, out row) ? row
                        : null,
                    row => placed.Any(pair => ReferenceEquals(pair.Value, row) && !t.ContainsKey(ProjectChanges.LocalKey(pair.Key)))), result);
            }
        }
    }

    /// <summary>Our children and their rows in the result, which starts as a parse of ours and so matches it child for child.</summary>
    private static Dictionary<ProjectNode, ProjectNode> Placed(ProjectGroup ours, ProjectGroup result)
        => ours.Children.Zip(result.Children).ToDictionary(pair => pair.First, pair => pair.Second);

    /// <summary>A node that is a drawing on one side and a group on the other: one replaces the other whole.</summary>
    private void Replaced(
        ProjectNode? was,
        ProjectNode mine,
        ProjectNode their,
        ProjectNode row,
        ProjectGroup result,
        Dictionary<ProjectNode, ProjectNode> placed,
        bool recording)
    {
        if (was is { } && ProjectChanges.Identical(was, their))
        {
            return;
        }

        if ((was is { } && ProjectChanges.Identical(was, mine))
            || Conflict(was, mine, their, recording).Choice == ProjectSide.Theirs)
        {
            var index = ProjectChanges.IndexOf(result, row);

            result.Remove(row);
            placed[mine] = Copied(their, index, result);
        }
    }

    /// <summary>Our version of a node they moved, put where they moved it and merged with theirs there.</summary>
    private ProjectNode Carried(ProjectNode was, ProjectNode mine, ProjectNode their, ProjectGroup into, int index, bool recording)
    {
        var row = Copied(mine, index, into);

        MergeNode(was, mine, their, row, recording);

        if (row is ProjectGroup group)
        {
            MergeChildren(was as ProjectGroup, (ProjectGroup)mine, (ProjectGroup)their, group, recording);
        }

        return row;
    }

    /// <summary>
    /// Where a node they added goes: after the nearest sibling before it on their side that the result
    /// still holds, and past whatever we added right there; at the start if it was their first, else at the end.
    /// </summary>
    private static int After(
        ProjectNode their,
        ProjectGroup theirs,
        ProjectGroup result,
        Func<ProjectNode, ProjectNode?> rowOf,
        Func<ProjectNode, bool> ours)
    {
        var at = ProjectChanges.IndexOf(theirs, their);

        if (at == 0)
        {
            return 0;
        }

        for (var before = at - 1; before >= 0; before--)
        {
            if (rowOf(theirs.Children[before]) is not { } anchor || !ReferenceEquals(anchor.Parent, result))
            {
                continue;
            }

            var index = ProjectChanges.IndexOf(result, anchor) + 1;

            // Ours first, then theirs, the order git itself writes two additions in.
            while (index < result.Children.Count && ours(result.Children[index]))
            {
                index++;
            }

            return index;
        }

        return result.Children.Count;
    }

    /// <summary>A copy of <paramref name="source"/> in <paramref name="result"/>.</summary>
    /// <param name="carry">
    /// Whether what they moved into it takes our version, merged. Only where the copy is certain: a copy
    /// made for a choice would meet conflicts that the other choice does not.
    /// </param>
    private ProjectNode Copied(ProjectNode source, int index, ProjectGroup result, bool carry = false, bool recording = false)
    {
        var copy = result.Copy(source, index);

        // Copy clears a place, since pasting beside the original would stack the two; here the
        // original is not in this document and its place is exactly what they chose.
        copy.Element.SetAttributeValue("x", (string?)source.Element.Attribute("x"));
        copy.Element.SetAttributeValue("y", (string?)source.Element.Attribute("y"));

        if (carry && source is ProjectGroup group)
        {
            Moved(group, (ProjectGroup)copy, recording);
        }

        return copy;
    }

    /// <summary>Swaps each node they moved into a group of theirs being copied in for our version of it, merged.</summary>
    private void Moved(ProjectGroup theirs, ProjectGroup copy, bool recording)
    {
        foreach (var (their, row) in theirs.Children.Zip(copy.Children).ToList())
        {
            if (_their.Moved.Contains(their) && _their.BaseOf[their] is var was && _mine.Of.TryGetValue(was, out var mine)
                && !_mine.Moved.Contains(mine) && mine.GetType() == their.GetType())
            {
                var index = ProjectChanges.IndexOf(copy, row);

                copy.Remove(row);
                Carried(was, mine, their, copy, index, recording);
            }
            else if (their is ProjectGroup group)
            {
                Moved(group, (ProjectGroup)row, recording);
            }
        }
    }

    /// <summary>Which base node each node of one side continues, where it stayed or where it was moved.</summary>
    private sealed class Side
    {
        /// <summary>Each node of the side that continues a base node, with that node.</summary>
        public readonly Dictionary<ProjectNode, ProjectNode> BaseOf = new();

        /// <summary>Each base node the side kept, with the side's node.</summary>
        public readonly Dictionary<ProjectNode, ProjectNode> Of = new();

        /// <summary>The side's nodes that are a base node moved to another group, or renamed.</summary>
        public readonly HashSet<ProjectNode> Moved = new();

        public Side(ProjectDocument? @base, ProjectDocument side)
        {
            if (@base is null)
            {
                return;
            }

            var added = new List<ProjectNode>();
            var removed = new List<(ProjectNode Node, string Shape)>();

            Align(@base.Root, side.Root, added, removed);

            for (var i = 0; i < added.Count; i++)
            {
                var node = added[i];

                // Paired already as the child of a group that moved.
                if (BaseOf.ContainsKey(node))
                {
                    continue;
                }

                var shape = ProjectChanges.Shape(node);

                if (removed.FirstOrDefault(one => !Of.ContainsKey(one.Node) && one.Shape == shape).Node is not { } was)
                {
                    continue;
                }

                Pair(was, node);
                Moved.Add(node);

                if (was is ProjectGroup from && node is ProjectGroup to)
                {
                    Align(from, to, added, removed);
                }
            }
        }

        private void Align(ProjectGroup @base, ProjectGroup side, List<ProjectNode> added, List<(ProjectNode, string)> removed)
        {
            var pairs = ProjectChanges.Paired(@base, side);

            foreach (var child in side.Children)
            {
                if (!pairs.TryGetValue(child, out var was))
                {
                    Every(child, node => added.Add(node));
                    continue;
                }

                Pair(was, child);

                if (was is ProjectGroup from && child is ProjectGroup to)
                {
                    Align(from, to, added, removed);
                }
            }

            foreach (var was in @base.Children.Where(one => !Of.ContainsKey(one)))
            {
                Every(was, node => removed.Add((node, ProjectChanges.Shape(node))));
            }
        }

        private void Pair(ProjectNode was, ProjectNode node)
        {
            BaseOf[node] = was;
            Of[was] = node;
        }

        private static void Every(ProjectNode node, Action<ProjectNode> each)
        {
            each(node);

            if (node is ProjectGroup group)
            {
                foreach (var child in group.Children)
                {
                    Every(child, each);
                }
            }
        }
    }
}
