// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;
using System.Linq;

namespace Svg.PaintCode.UnitTests;

/// <summary>
/// Builds an <c>NSKeyedArchiver</c> file: a <c>$objects</c> table every value indexes into, and a
/// <c>$top</c> naming the roots. Slots are reserved before they are filled so an object can reference
/// one written after it, which is how the real archiver shares.
/// </summary>
internal sealed class KeyedArchiveBuilder
{
    private readonly Bplist _plist = new();
    private readonly List<int?> _slots = new();

    internal KeyedArchiveBuilder()
    {
        // Slot 0 is the archive's "$null", written wherever a value was nil.
        Fill(Reserve(), _plist.Ascii("$null"));
    }

    internal int Reserve()
    {
        _slots.Add(null);

        return _slots.Count - 1;
    }

    internal void Fill(int slot, int reference) => _slots[slot] = reference;

    internal int Text(string value)
    {
        var slot = Reserve();
        Fill(slot, _plist.Ascii(value));

        return slot;
    }

    /// <summary>A primitive in its own slot, which is how one reaches an array.</summary>
    internal int Value(object value)
    {
        var slot = Reserve();
        Fill(slot, value switch
        {
            bool flag => _plist.Boolean(flag),
            int integer => _plist.Integer(integer),
            double real => _plist.Real(real),
            _ => _plist.Ascii(value.ToString()!)
        });

        return slot;
    }

    internal int Data(byte[] value)
    {
        var slot = Reserve();
        Fill(slot, _plist.Data(value));

        return slot;
    }

    /// <summary>An instance of <paramref name="className"/> with the members given.</summary>
    internal int Object(string className, params (string Key, int Reference)[] members)
    {
        var slot = Reserve();
        var description = Reserve();
        Fill(description, _plist.Dictionary((_plist.Ascii("$classname"), _plist.Ascii(className))));

        var entries = new List<(int, int)> { (_plist.Ascii("$class"), _plist.Uid(description)) };

        foreach (var member in members)
        {
            entries.Add((_plist.Ascii(member.Key), _plist.Uid(member.Reference)));
        }

        Fill(slot, _plist.Dictionary(entries.ToArray()));

        return slot;
    }

    /// <summary>An instance with plain values beside its object members, the way primitives are stored.</summary>
    internal int Object(string className, (string Key, int Reference)[] members, params (string Key, object Value)[] values)
    {
        var slot = Reserve();
        var description = Reserve();
        Fill(description, _plist.Dictionary((_plist.Ascii("$classname"), _plist.Ascii(className))));

        var entries = new List<(int, int)> { (_plist.Ascii("$class"), _plist.Uid(description)) };

        foreach (var member in members)
        {
            entries.Add((_plist.Ascii(member.Key), _plist.Uid(member.Reference)));
        }

        foreach (var value in values)
        {
            entries.Add((_plist.Ascii(value.Key), value.Value switch
            {
                bool flag => _plist.Boolean(flag),
                int integer => _plist.Integer(integer),
                double real => _plist.Real(real),
                _ => _plist.Ascii(value.Value.ToString()!)
            }));
        }

        Fill(slot, _plist.Dictionary(entries.ToArray()));

        return slot;
    }

    internal int Array(params int[] slots)
    {
        var slot = Reserve();
        var description = Reserve();
        Fill(description, _plist.Dictionary((_plist.Ascii("$classname"), _plist.Ascii("NSMutableArray"))));

        Fill(slot, _plist.Dictionary(
            (_plist.Ascii("$class"), _plist.Uid(description)),
            (_plist.Ascii("NS.objects"), _plist.Array(slots.Select(_plist.Uid).ToArray()))));

        return slot;
    }

    internal int Dictionary(params (string Key, int Value)[] entries)
    {
        var slot = Reserve();
        var description = Reserve();
        Fill(description, _plist.Dictionary((_plist.Ascii("$classname"), _plist.Ascii("NSMutableDictionary"))));

        Fill(slot, _plist.Dictionary(
            (_plist.Ascii("$class"), _plist.Uid(description)),
            (_plist.Ascii("NS.keys"), _plist.Array(entries.Select(entry => _plist.Uid(Text(entry.Key))).ToArray())),
            (_plist.Ascii("NS.objects"), _plist.Array(entries.Select(entry => _plist.Uid(entry.Value)).ToArray()))));

        return slot;
    }

    internal byte[] ToBytes(params (string Key, int Slot)[] top)
    {
        var objects = _plist.Array(_slots.Select(slot => slot ?? _plist.Null()).ToArray());
        var root = _plist.Dictionary(
            (_plist.Ascii("$archiver"), _plist.Ascii("NSKeyedArchiver")),
            (_plist.Ascii("$version"), _plist.Integer(100000)),
            (_plist.Ascii("$objects"), objects),
            (_plist.Ascii("$top"), _plist.Dictionary(top.Select(entry => (_plist.Ascii(entry.Key), _plist.Uid(entry.Slot))).ToArray())));

        return _plist.ToBytes(root);
    }
}
