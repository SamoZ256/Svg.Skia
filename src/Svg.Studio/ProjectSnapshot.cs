// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Svg.Studio;

/// <summary>
/// What a gesture is about to change, kept as something that puts it back.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these restores <em>in place</em>, over the nodes the document already holds. That is
/// the whole design: a project runs to a thousand drawings and sixteen megabytes, so keeping a copy
/// of its text per edit is not affordable — and re-reading one would build a new object graph, while
/// every open tab holds its node by reference and is matched with <c>ReferenceEquals</c>. An undo
/// that replaced the graph would leave every tab editing something the document no longer contains.
/// </para>
/// <para>
/// Each returns an <see cref="Action"/> rather than a value, so <see cref="ProjectWorkspace.Do"/> can
/// call the same capture on both sides of a gesture and get the way back and the way forward from
/// one closure.
/// </para>
/// </remarks>
public static class ProjectSnapshot
{
    /// <summary>The attributes <paramref name="node"/> carries now.</summary>
    /// <remarks>
    /// Every setting a node has is an attribute read and written straight through — there is no
    /// state of its own to keep — so this covers the whole settings panel, a root's code-generator
    /// options, and the <c>x</c> and <c>y</c> a move rewrites, without naming any of them.
    /// </remarks>
    public static Action Attributes(ProjectNode node)
    {
        if (node is null)
        {
            throw new ArgumentNullException(nameof(node));
        }

        return Attributes(node.Element);
    }

    /// <summary>Where every node under <paramref name="group"/> sits, as the file says.</summary>
    /// <remarks>
    /// The whole subtree and not the one row that was dragged: the first drag on a board nobody has
    /// arranged writes a place onto every row under it, so that is what there is to put back.
    /// </remarks>
    public static Action Places(ProjectGroup group)
    {
        if (group is null)
        {
            throw new ArgumentNullException(nameof(group));
        }

        var places = new List<(ProjectNode Node, float? X, float? Y)>();

        Walk(group, node => places.Add((node, node.X, node.Y)));

        return () =>
        {
            foreach (var (node, x, y) in places)
            {
                node.X = x;
                node.Y = y;
            }
        };
    }

    /// <inheritdoc cref="ProjectGroup.Contents"/>
    public static Action Contents(ProjectGroup group)
    {
        if (group is null)
        {
            throw new ArgumentNullException(nameof(group));
        }

        return group.Contents();
    }

    /// <summary>How far in <paramref name="node"/> is written, and everything under it with it.</summary>
    /// <remarks>
    /// A row dragged between groups is written at its new depth — that is what keeps the file laid
    /// out rather than left ragged — and putting the row back does not put its indentation back,
    /// because the whitespace was rewritten inside the element rather than around it.
    ///
    /// This has to be restored <em>before</em> the row is: the depth it is written at is read from
    /// where it sits, so once it is home again there is nothing left to say it was ever elsewhere.
    /// <see cref="All"/> runs its captures in order, and this goes first.
    /// </remarks>
    public static Action Indentation(ProjectNode node)
    {
        if (node is null)
        {
            throw new ArgumentNullException(nameof(node));
        }

        var element = node.Element;
        var was = ProjectDocument.Depth(element);

        return () => ProjectGroup.Reindent(element, ProjectDocument.Depth(element), was);
    }

    /// <summary>The drawing's own text, as the file holds it.</summary>
    public static Action Text(ProjectDrawing drawing)
    {
        if (drawing is null)
        {
            throw new ArgumentNullException(nameof(drawing));
        }

        var was = drawing.Text;

        // The refusal is dropped: what is being put back is what the document was reading a moment
        // ago, so there is nothing for it to refuse and nobody to tell.
        return () => drawing.SetText(was);
    }

    /// <summary>What the group declares, as the file holds it.</summary>
    /// <remarks>
    /// <see cref="ProjectGroup.CodeText"/> answers for a group that declares nothing with an empty
    /// block, and writing that back takes the block out again — so a first declaration is taken back
    /// to no block rather than to an empty one, which is what the file said.
    /// </remarks>
    public static Action Code(ProjectGroup group)
    {
        if (group is null)
        {
            throw new ArgumentNullException(nameof(group));
        }

        var was = group.CodeText;

        return () => group.SetCode(was);
    }

    /// <summary>Several captures as one, in the order they were given.</summary>
    /// <remarks>For a gesture that touches more than one thing — a paste is a node and a place.</remarks>
    public static Action All(params Action[] captured)
        => () =>
        {
            foreach (var one in captured)
            {
                one();
            }
        };

    private static Action Attributes(XElement element)
    {
        var was = element.Attributes().Select(attribute => (attribute.Name, attribute.Value)).ToList();

        return () =>
        {
            // Set before removed, so an attribute that is both kept and rewritten is never briefly
            // absent — and taken out afterwards by name, since SetAttributeValue(null) is how the
            // document drops one anywhere else.
            foreach (var (name, value) in was)
            {
                element.SetAttributeValue(name, value);
            }

            foreach (var attribute in element.Attributes()
                         .Where(attribute => !was.Any(kept => kept.Name == attribute.Name))
                         .ToList())
            {
                attribute.Remove();
            }
        };
    }

    private static void Walk(ProjectNode node, Action<ProjectNode> visit)
    {
        visit(node);

        if (node is ProjectGroup group)
        {
            foreach (var child in group.Children)
            {
                Walk(child, visit);
            }
        }
    }
}
