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

    /// <summary>Applies every step to one document, once every key the steps name has been found in it.</summary>
    /// <remarks>
    /// Belongs inside a commit: a step's own refusal can come after an earlier step has written,
    /// and the commit is what puts that back. A key the host cannot place, or the file does not
    /// have, is refused before anything is written.
    /// </remarks>
    /// <param name="address">What the host calls each key, turned into an address in the file.</param>
    /// <returns>The sentence refusing the edit, or null where it was made.</returns>
    public string? Apply(SvgSourceDocument source, Func<string, string?> address)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (address is null)
        {
            throw new ArgumentNullException(nameof(address));
        }

        var found = new Dictionary<string, XElement>(StringComparer.Ordinal);

        foreach (var key in Keys)
        {
            if (found.ContainsKey(key))
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
        }

        foreach (var step in Steps)
        {
            var refusal = step switch
            {
                SvgSourceEdit.SetAttributes set => Write(found[set.Key], set.Writes),
                _ => throw new ArgumentException($"'{step.GetType().Name}' is not a step this applies.", nameof(Steps))
            };

            if (refusal is { })
            {
                return refusal;
            }
        }

        return null;
    }

    private static string? Write(XElement element, IReadOnlyList<(string Name, string Value)> writes)
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

    /// <summary>Writes attributes of one element, in order.</summary>
    public sealed record SetAttributes(string Key, IReadOnlyList<(string Name, string Value)> Writes) : SvgSourceEdit
    {
        internal override IEnumerable<string> Keys => new[] { Key };
    }
}
