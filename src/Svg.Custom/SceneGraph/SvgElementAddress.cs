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
