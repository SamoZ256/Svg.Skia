// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Svg.SourceEditing;

/// <summary>What one gesture writes into a drawing, as one undo step: its label, and its steps in order.</summary>
/// <remarks>
/// One document and one commit however many elements it carries: a commit hands the edit the whole
/// document and pushes one state, so a drag of six shapes is one thing to take back, and a refusal
/// on the fourth puts the first three back with it.
/// </remarks>
public readonly record struct SvgSourceEdits(string Label, IReadOnlyList<SvgSourceEdit> Steps)
{
    /// <summary>Every key the steps name, in order: what a host has to be able to place before anything is written.</summary>
    public IReadOnlyList<string> Keys => Steps.SelectMany(step => step.Keys).ToList();

    /// <inheritdoc cref="Apply(SvgSourceDocument, Func{string, string?}, out IReadOnlyList{string})"/>
    public string? Apply(SvgSourceDocument source, Func<string, string?> address) => Apply(source, address, out _);

    /// <summary>Applies every step to one document, once every key the steps name has been found in it.</summary>
    /// <remarks>
    /// Belongs inside a commit: a step's own refusal can come after an earlier step has written,
    /// and the commit is what puts that back. A key the host cannot place, or the file does not
    /// have, is refused before anything is written.
    ///
    /// Every key names the drawing as it was before the plan, however the steps before it shift
    /// what is where: a key is found once, and the steps act on what it found. A step naming what
    /// an earlier step took out is refused, except a removal, for which it is already done. An
    /// insert's <see cref="SvgSourceEdit.Insert.Name"/> is a key too, from that step on.
    /// </remarks>
    /// <param name="address">What the host calls each key, turned into an address in the file.</param>
    /// <param name="landed">
    /// The file keys, once every step is written, of what the steps wrote, moved or put in, in step
    /// order: what a host picks afterwards. Empty where the edit was refused.
    /// </param>
    /// <returns>The sentence refusing the edit, or null where it was made.</returns>
    public string? Apply(SvgSourceDocument source, Func<string, string?> address, out IReadOnlyList<string> landed)
    {
        landed = Array.Empty<string>();

        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (address is null)
        {
            throw new ArgumentNullException(nameof(address));
        }

        var found = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var addresses = new Dictionary<string, string>(StringComparer.Ordinal);
        var named = new HashSet<string>(Steps.OfType<SvgSourceEdit.Insert>().Select(insert => insert.Name).OfType<string>(), StringComparer.Ordinal);

        foreach (var key in Keys)
        {
            if (found.ContainsKey(key) || named.Contains(key))
            {
                continue;
            }

            if (address(key) is not { } at)
            {
                return Unwritten;
            }

            if (SvgAttributeEditor.Resolve(source.Document, at) is not { } element)
            {
                return SvgElementEditor.Missing(at);
            }

            found[key] = element;
            addresses[key] = at;
        }

        var touched = new List<XElement>();

        foreach (var step in Steps)
        {
            if (step.Keys.FirstOrDefault(key => !found.ContainsKey(key)) is { } unmade)
            {
                return $"A step names '{unmade}' before the step that puts it in.";
            }

            var gone = step.Keys.FirstOrDefault(key => found[key].Document is null);

            if (gone is { } && step is SvgSourceEdit.Remove)
            {
                continue;
            }

            if (gone is { })
            {
                return SvgElementEditor.Missing(addresses.TryGetValue(gone, out var at) ? at : gone);
            }

            var refusal = step switch
            {
                SvgSourceEdit.SetAttributes set => Write(found[set.Key], set.Writes),
                SvgSourceEdit.Insert insert => Put(source, found[insert.TargetKey], insert, touched, found),
                SvgSourceEdit.Move move => SvgElementEditor.Move(source, found[move.Key], found[move.TargetKey], move.Where),
                SvgSourceEdit.Remove remove => Cut(found[remove.Key]),
                SvgSourceEdit.SetContent content => SvgAttributeEditor.SetContent(found[content.Key], content.Value),
                SvgSourceEdit.Retag retag => SvgElementEditor.Retag(found[retag.Key], retag.Name),
                _ => throw new ArgumentException($"'{step.GetType().Name}' is not a step this applies.", nameof(Steps))
            };

            if (refusal is { })
            {
                return refusal;
            }

            if (step is not SvgSourceEdit.Insert and not SvgSourceEdit.Remove)
            {
                touched.Add(found[step.Keys.First()]);
            }
        }

        // Only once every step is in: each one can shift the addresses after it.
        landed = touched.Where(element => element.Document is { }).Distinct().Select(SvgAttributeEditor.Key).ToList();

        return null;
    }

    /// <remarks>A copy, so the plan stays a value: the element its caller built is never parented.</remarks>
    private static string? Put(SvgSourceDocument source, XElement target, SvgSourceEdit.Insert insert, List<XElement> touched, Dictionary<string, XElement> found)
    {
        var copy = SvgSourceDocument.Copy(insert.Element);

        if (SvgElementEditor.Insert(source, target, insert.Where, copy) is { } refusal)
        {
            return refusal;
        }

        touched.Add(copy);

        if (insert.Name is { } name)
        {
            found[name] = copy;
        }

        return null;
    }

    private static string? Cut(XElement element)
    {
        if (SvgElementEditor.Unchosen(element, "deleted") is { } refusal)
        {
            return refusal;
        }

        SvgElementEditor.Cut(element);

        return null;
    }

    private static string? Write(XElement element, IReadOnlyList<(string Name, string? Value)> writes)
    {
        foreach (var (name, value) in writes)
        {
            if (SvgAttributeEditor.SetAttribute(element, name, value) is { } refusal)
            {
                return refusal;
            }
        }

        return null;
    }

    private const string Unwritten = "That element is not written in this drawing's file, so it cannot be edited here.";
}

/// <summary>One step of <see cref="SvgSourceEdits"/>.</summary>
public abstract record SvgSourceEdit
{
    private protected SvgSourceEdit()
    {
    }

    /// <summary>The keys of the elements the step acts on, as its host calls them.</summary>
    internal abstract IEnumerable<string> Keys { get; }

    /// <summary>Writes attributes of one element, in order, taking away each written as null.</summary>
    public sealed record SetAttributes(string Key, IReadOnlyList<(string Name, string? Value)> Writes) : SvgSourceEdit
    {
        internal override IEnumerable<string> Keys => new[] { Key };
    }

    /// <summary>Writes a copy of <paramref name="Element"/> where a drop on <paramref name="TargetKey"/> says, as <see cref="SvgElementEditor.Insert(SvgSourceDocument, string, SvgElementDrop, XElement, out string?)"/> does.</summary>
    /// <param name="Name">A key the steps after this one can name the copy by, or null.</param>
    public sealed record Insert(string TargetKey, SvgElementDrop Where, XElement Element, string? Name = null) : SvgSourceEdit
    {
        internal override IEnumerable<string> Keys => new[] { TargetKey };
    }

    /// <summary>Moves an element where a drop on <paramref name="TargetKey"/> says, as <see cref="SvgElementEditor.Move(SvgSourceDocument, string, string, SvgElementDrop, out string?)"/> does.</summary>
    public sealed record Move(string Key, string TargetKey, SvgElementDrop Where) : SvgSourceEdit
    {
        internal override IEnumerable<string> Keys => new[] { Key, TargetKey };
    }

    /// <summary>Takes an element out with the line that carried it, as <see cref="SvgElementEditor.Remove"/> does.</summary>
    public sealed record Remove(string Key) : SvgSourceEdit
    {
        internal override IEnumerable<string> Keys => new[] { Key };
    }

    /// <summary>Writes the text between an element's tags, or takes it away where null, as <see cref="SvgAttributeEditor.SetContent(SvgSourceDocument, string, string?)"/> does.</summary>
    public sealed record SetContent(string Key, string? Value) : SvgSourceEdit
    {
        internal override IEnumerable<string> Keys => new[] { Key };
    }

    /// <summary>Gives an element another name in its own namespace, keeping what it is written with and holds.</summary>
    public sealed record Retag(string Key, string Name) : SvgSourceEdit
    {
        internal override IEnumerable<string> Keys => new[] { Key };
    }
}
