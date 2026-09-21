using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

#nullable enable

namespace Svg;

/// <summary>
/// Where an element sits in its document, as the child indexes leading down to it.
/// </summary>
/// <remarks>
/// The identity that survives a rebuild. A host that reloads a drawing from edited text gets a new
/// <see cref="SvgDocument"/> and new elements every time, so a reference taken a keystroke ago names
/// nothing; this names the same place in whatever document is current. It is also what
/// <c>SvgSceneNode.ElementAddressKey</c> spells, so a scene node and an element agree on it.
/// </remarks>
public sealed class SvgElementAddress
{
    public SvgElementAddress(int[] childIndexes)
    {
        ChildIndexes = childIndexes;
        Key = CreateKey(childIndexes);
    }

    public int[] ChildIndexes { get; }

    public string Key { get; }

    public static SvgElementAddress Create(SvgElement element)
    {
        var indexes = new Stack<int>();
        var current = element;

        while (current.Parent is { } parent)
        {
            indexes.Push(parent.Children.IndexOf(current));
            current = parent;
        }

        return new SvgElementAddress(indexes.ToArray());
    }

    /// <summary>The address a key spells, or null where it spells no address at all.</summary>
    /// <remarks>
    /// The inverse of the key this type writes, and here rather than wherever a key is read back so
    /// that the convention has one reader. An empty key is the root, which is an address with no
    /// steps in it rather than no address.
    /// </remarks>
    public static SvgElementAddress? Parse(string? key)
    {
        if (key is null)
        {
            return null;
        }

        if (key.Length == 0)
        {
            return new SvgElementAddress(Array.Empty<int>());
        }

        var steps = key.Split('/');
        var indexes = new int[steps.Length];

        for (var i = 0; i < steps.Length; i++)
        {
            if (!int.TryParse(steps[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index < 0)
            {
                return null;
            }

            indexes[i] = index;
        }

        return new SvgElementAddress(indexes);
    }

    public SvgElement? Resolve(SvgDocument document)
    {
        SvgElement current = document;

        foreach (var childIndex in ChildIndexes)
        {
            if (childIndex < 0 || childIndex >= current.Children.Count)
            {
                return null;
            }

            current = current.Children[childIndex];
        }

        return current;
    }

    private static string CreateKey(IReadOnlyList<int> childIndexes)
    {
        if (childIndexes.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(childIndexes.Count * 2);
        for (var i = 0; i < childIndexes.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('/');
            }

            builder.Append(childIndexes[i].ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
