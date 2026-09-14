// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Svg.PaintCode;

/// <summary>
/// An <c>NSKeyedArchiver</c> graph: a flat <c>$objects</c> table that every value indexes into, and a
/// <c>$top</c> dictionary naming the roots.
/// </summary>
internal sealed class PaintCodeKeyedArchive
{
    private readonly List<object?> _objects;

    private PaintCodeKeyedArchive(List<object?> objects, Dictionary<string, object?> top)
    {
        _objects = objects;
        Top = new PaintCodeNode(this, top);
    }

    internal PaintCodeNode Top { get; }

    internal static PaintCodeKeyedArchive Parse(byte[] bytes)
    {
        if (PaintCodeArchive.Read(bytes) is not Dictionary<string, object?> root)
        {
            throw new PaintCodeException("The property list's root is not a dictionary, so it is not a keyed archive.");
        }

        if (root.TryGetValue("$archiver", out var archiver) && archiver is string named && named != "NSKeyedArchiver")
        {
            throw new PaintCodeException($"The property list was written by {named} rather than NSKeyedArchiver.");
        }

        if (root.TryGetValue("$objects", out var objects) && objects is not List<object?>)
        {
            throw new PaintCodeException("The keyed archive's object table is not an array.");
        }

        if (objects is not List<object?> table || root["$top"] is not Dictionary<string, object?> top)
        {
            throw new PaintCodeException("The keyed archive has no object table or no $top.");
        }

        return new PaintCodeKeyedArchive(table, top);
    }

    /// <summary>Follows a <c>CF$UID</c> to the object it indexes; anything else is already the value.</summary>
    internal object? Follow(object? value)
    {
        while (value is PaintCodeUid uid)
        {
            if (uid.Index < 0 || uid.Index >= _objects.Count)
            {
                throw new PaintCodeException($"The keyed archive references object {uid.Index}, which it does not hold.");
            }

            // Index 0 is the archive's "$null" placeholder, written where a value was nil.
            value = uid.Index == 0 ? null : _objects[uid.Index];
        }

        return value;
    }

    internal string? ClassNameOf(object? value)
    {
        if (Follow(value) is not Dictionary<string, object?> entries ||
            !entries.TryGetValue("$class", out var reference) ||
            Follow(reference) is not Dictionary<string, object?> description ||
            !description.TryGetValue("$classname", out var name))
        {
            return null;
        }

        return name as string;
    }
}

/// <summary>One resolved value in the graph, with the archive it came from.</summary>
/// <remarks>
/// A struct over the pair rather than a materialised tree: the sample document is 174k objects of
/// which a conversion reads a fraction, and its symbols are shared by reference — inflating it into
/// objects would copy every shared subtree once per referent.
/// </remarks>
internal readonly struct PaintCodeNode
{
    private readonly PaintCodeKeyedArchive? _archive;
    private readonly object? _value;

    internal PaintCodeNode(PaintCodeKeyedArchive archive, object? value)
    {
        _archive = archive;
        _value = archive.Follow(value);
    }

    internal bool IsNull => _value is null;

    internal string? ClassName => _archive?.ClassNameOf(_value);

    /// <summary>The member named <paramref name="key"/>, or a null node where there is none.</summary>
    internal PaintCodeNode this[string key]
        => _archive is { } archive && _value is Dictionary<string, object?> entries && entries.TryGetValue(key, out var member)
            ? new PaintCodeNode(archive, member)
            : default;

    internal bool Has(string key) => _value is Dictionary<string, object?> entries && entries.ContainsKey(key);

    /// <summary>Whether this and <paramref name="other"/> are the same archived object.</summary>
    /// <remarks>
    /// The reader hands back one instance per table entry, so reference equality is object identity —
    /// which is the only way to tell a shared object from an equal-looking copy.
    /// </remarks>
    internal bool SameAs(PaintCodeNode other) => ReferenceEquals(_value, other._value);

    /// <summary>The text of a raw string or of an archived <c>NSString</c>.</summary>
    internal string? Text
        => _value switch
        {
            string text => text,
            Dictionary<string, object?> entries when entries.TryGetValue("NS.string", out var text) => _archive?.Follow(text) as string,
            _ => null
        };

    internal double? Number
        => _value switch
        {
            long integer => integer,
            double real => real,
            bool flag => flag ? 1d : 0d,
            _ => null
        };

    /// <summary>Whether the value is a boolean rather than a number that reads as one.</summary>
    /// <remarks>
    /// <see cref="Number"/> answers for a boolean too, so anything deciding a type from the value
    /// alone -- the values a symbol hands its target, which carry no type tag -- has to ask this first.
    /// </remarks>
    internal bool IsBoolean => _value is bool;

    internal bool? Flag
        => _value switch
        {
            bool flag => flag,
            long integer => integer != 0,
            _ => null
        };

    internal byte[]? Data
        => _value switch
        {
            byte[] data => data,
            Dictionary<string, object?> entries when entries.TryGetValue("NS.data", out var data) => _archive?.Follow(data) as byte[],
            _ => null
        };

    internal double NumberOr(double fallback) => Number ?? fallback;

    internal bool FlagOr(bool fallback) => Flag ?? fallback;

    /// <summary>The elements of an archived array or set, in order.</summary>
    internal IReadOnlyList<PaintCodeNode> Items
    {
        get
        {
            if (_archive is not { } archive)
            {
                return System.Array.Empty<PaintCodeNode>();
            }

            var objects = _value switch
            {
                List<object?> raw => raw,
                Dictionary<string, object?> entries when entries.TryGetValue("NS.objects", out var items) => archive.Follow(items) as List<object?>,
                _ => null
            };

            if (objects is null)
            {
                return System.Array.Empty<PaintCodeNode>();
            }

            var nodes = new PaintCodeNode[objects.Count];

            for (var index = 0; index < nodes.Length; index++)
            {
                nodes[index] = new PaintCodeNode(archive, objects[index]);
            }

            return nodes;
        }
    }

    /// <summary>The entries of an archived dictionary, keyed by the text of each key.</summary>
    internal IReadOnlyDictionary<string, PaintCodeNode> Entries
    {
        get
        {
            var entries = new Dictionary<string, PaintCodeNode>(StringComparer.Ordinal);

            if (_archive is not { } archive ||
                _value is not Dictionary<string, object?> dictionary ||
                !dictionary.TryGetValue("NS.keys", out var keys) ||
                !dictionary.TryGetValue("NS.objects", out var values) ||
                archive.Follow(keys) is not List<object?> keyList ||
                archive.Follow(values) is not List<object?> valueList)
            {
                return entries;
            }

            for (var index = 0; index < keyList.Count && index < valueList.Count; index++)
            {
                if (new PaintCodeNode(archive, keyList[index]).Text is { } key)
                {
                    entries[key] = new PaintCodeNode(archive, valueList[index]);
                }
            }

            return entries;
        }
    }

    /// <summary>A point, written either as a bare <c>{x, y}</c> string or wrapped in an NSValue.</summary>
    internal PaintCodePoint? Point => ParsePoint(Geometry("NS.pointval"));

    /// <summary>A rectangle, written either as a bare <c>{{x, y}, {w, h}}</c> string or in an NSValue.</summary>
    internal PaintCodeRect? Rect
    {
        get
        {
            if (Geometry("NS.rectval") is not { } text)
            {
                return null;
            }

            var split = text.IndexOf("},", StringComparison.Ordinal);

            if (split < 0 || ParsePoint(text.Substring(0, split + 1)) is not { } origin || ParsePoint(text.Substring(split + 2)) is not { } size)
            {
                return null;
            }

            return new PaintCodeRect(origin.X, origin.Y, size.X, size.Y);
        }
    }

    // PaintCode archives most geometry as the plain string AppKit's NSStringFrom… functions produce,
    // and only some of it inside an NSValue, so both spellings reach here.
    private string? Geometry(string key)
        => Text ?? this[key].Text;

    private static PaintCodePoint? ParsePoint(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var trimmed = text.Trim().Trim('{', '}');
        var comma = trimmed.IndexOf(',');

        if (comma < 0 ||
            !double.TryParse(trimmed.Substring(0, comma).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(trimmed.Substring(comma + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            return null;
        }

        return new PaintCodePoint(x, y);
    }
}
