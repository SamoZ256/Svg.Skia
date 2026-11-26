// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Svg.Pathing;
using Svg.Transforms;

namespace Svg;

public abstract partial class SvgElement
{
    /// <summary>
    /// The declarations <see cref="FlushStyles(bool)"/> could not apply, each with every rule that set
    /// it in ascending specificity: what a <c>filter: blur()</c> or an unknown property is held as.
    /// </summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<KeyValuePair<int, string>>> StagedStyles()
        => _styles.ToDictionary(
            style => style.Key,
            style => (IReadOnlyList<KeyValuePair<int, string>>)style.Value.ToList(),
            StringComparer.Ordinal);
}

/// <summary>What one element holds of its own at one moment, copied so a later edit cannot reach it.</summary>
/// <remarks>
/// Captured before a command and again after it, the two are what a diff compares. An element's
/// <c>AttributeChanged</c> cannot be what says something changed: a value edited in place — a path
/// segment's end, a matrix's numbers — raises nothing, and a matrix's clone shares its numbers with
/// the original.
/// </remarks>
public sealed class SvgElementState
{
    private SvgElementState(
        SvgElement element,
        string key,
        IReadOnlyDictionary<string, object?> attributes,
        IReadOnlyDictionary<string, IReadOnlyList<KeyValuePair<int, string>>> styles,
        IReadOnlyDictionary<string, string> customAttributes,
        string? content,
        IReadOnlyList<SvgElement> children)
    {
        Element = element;
        Key = key;
        Attributes = attributes;
        Styles = styles;
        CustomAttributes = customAttributes;
        Content = content;
        Children = children;
    }

    /// <summary>The element itself, which is how a capture before is matched with one after.</summary>
    public SvgElement Element { get; }

    /// <summary>Its address key under the root it was captured from, as <see cref="SvgElementAddress"/> spells one.</summary>
    public string Key { get; }

    /// <summary>The typed values set on it, not inherited, each a copy.</summary>
    public IReadOnlyDictionary<string, object?> Attributes { get; }

    /// <summary>The declarations it holds that set no attribute, each with its rules in ascending specificity.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<KeyValuePair<int, string>>> Styles { get; }

    /// <summary>Its custom attributes: lifted expressions, where they came from, and what it was written with that it does not read.</summary>
    public IReadOnlyDictionary<string, string> CustomAttributes { get; }

    /// <summary>The text between its tags, or null where it holds none.</summary>
    public string? Content { get; }

    /// <summary>Its children, in order.</summary>
    public IReadOnlyList<SvgElement> Children { get; }

    /// <summary><paramref name="root"/> and everything under it, in document order.</summary>
    public static IReadOnlyList<SvgElementState> Capture(SvgElement root)
    {
        if (root is null)
        {
            throw new ArgumentNullException(nameof(root));
        }

        var states = new List<SvgElementState>();

        Walk(root, string.Empty);

        return states;

        void Walk(SvgElement element, string key)
        {
            states.Add(Of(element, key));

            for (var i = 0; i < element.Children.Count; i++)
            {
                var index = i.ToString(CultureInfo.InvariantCulture);

                Walk(element.Children[i], key.Length == 0 ? index : key + "/" + index);
            }
        }
    }

    /// <summary>What <paramref name="element"/> holds of its own now, under <paramref name="key"/>.</summary>
    public static SvgElementState Of(SvgElement element, string key)
    {
        if (element is null)
        {
            throw new ArgumentNullException(nameof(element));
        }

        // Enumerated rather than read through the indexer, which answers with a parent's value.
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var attribute in element.Attributes)
        {
            attributes[attribute.Key] = Freeze(attribute.Value);
        }

        var content = element.Nodes.OfType<SvgContentNode>().ToList();

        return new SvgElementState(
            element,
            key ?? string.Empty,
            attributes,
            element.StagedStyles(),
            new Dictionary<string, string>(element.CustomAttributes, StringComparer.Ordinal),
            content.Count > 0 ? string.Concat(content.Select(node => node.Content)) : string.IsNullOrEmpty(element.Content) ? null : element.Content,
            element.Children.ToList());
    }

    /// <summary>A copy of <paramref name="value"/> no edit of the original reaches.</summary>
    internal static object? Freeze(object? value)
    {
        switch (value)
        {
            case SvgTransformCollection transforms:
                // Its clone shares each matrix's list of numbers with the original.
                var copy = new SvgTransformCollection();

                foreach (var transform in transforms)
                {
                    copy.Add(transform is SvgMatrix matrix ? new SvgMatrix(new List<float>(matrix.Points)) : (SvgTransform)transform.Clone());
                }

                return copy;
            case SvgOrient orient:
                return orient.IsAuto ? new SvgOrient(true, orient.IsAutoStartReverse) : new SvgOrient(orient.Angle);
            case SvgColourServer colour when ReferenceEquals(colour, SvgPaintServer.None)
                                             || ReferenceEquals(colour, SvgPaintServer.Inherit)
                                             || ReferenceEquals(colour, SvgPaintServer.NotSet):
                return colour;
            case SvgColourServer colour:
                return new SvgColourServer(colour.Colour);
            case SvgDeferredPaintServer deferred:
                return deferred.DeepCopy();
            case SvgContextPaintServer context:
                return new SvgContextPaintServer(context.Kind);
            case SvgPaintServer:
                // A gradient or a pattern in the tree: which element it is, is the value.
                return value;
            case SvgPathSegmentList or SvgUnitCollection or SvgNumberCollection or SvgPointCollection or SvgAspectRatio:
                return ((ICloneable)value).Clone();
            default:
                return value;
        }
    }
}
