// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Svg.PaintCode.UnitTests;

/// <summary>
/// Writes a <c>bplist00</c> file a byte at a time, so a reader test names the bytes it is about
/// rather than pointing at a committed binary nobody can review.
/// </summary>
internal sealed class Bplist
{
    private readonly List<byte[]> _objects = new();

    internal int Null() => Add(new byte[] { 0x00 });

    internal int Boolean(bool value) => Add(new byte[] { value ? (byte)0x09 : (byte)0x08 });

    internal int Integer(long value)
    {
        if (value is >= 0 and < 256)
        {
            return Add(new byte[] { 0x10, (byte)value });
        }

        var bytes = new byte[9];
        bytes[0] = 0x13;

        for (var index = 0; index < 8; index++)
        {
            bytes[8 - index] = (byte)(value >> (index * 8));
        }

        return Add(bytes);
    }

    internal int Real(double value)
    {
        var source = BitConverter.GetBytes(value);
        var bytes = new byte[9];
        bytes[0] = 0x23;

        for (var index = 0; index < 8; index++)
        {
            bytes[1 + index] = source[BitConverter.IsLittleEndian ? 7 - index : index];
        }

        return Add(bytes);
    }

    internal int Ascii(string value)
    {
        var text = Encoding.ASCII.GetBytes(value);

        return Add(Tagged(0x50, text.Length, text));
    }

    internal int Unicode(string value)
    {
        var text = Encoding.BigEndianUnicode.GetBytes(value);

        return Add(Tagged(0x60, value.Length, text));
    }

    internal int Data(byte[] value) => Add(Tagged(0x40, value.Length, value));

    // Two bytes for every reference and every UID: the sample archives run to more than 256 objects,
    // and a one-byte width truncates silently rather than failing.
    internal int Uid(int index) => Add(new byte[] { 0x81, (byte)(index >> 8), (byte)index });

    internal int Array(params int[] references)
    {
        var body = new byte[references.Length * 2];

        for (var index = 0; index < references.Length; index++)
        {
            body[index * 2] = (byte)(references[index] >> 8);
            body[index * 2 + 1] = (byte)references[index];
        }

        return Add(Tagged(0xA0, references.Length, body));
    }

    internal int Dictionary(params (int Key, int Value)[] entries)
    {
        var body = new byte[entries.Length * 4];

        for (var index = 0; index < entries.Length; index++)
        {
            body[index * 2] = (byte)(entries[index].Key >> 8);
            body[index * 2 + 1] = (byte)entries[index].Key;
            body[entries.Length * 2 + index * 2] = (byte)(entries[index].Value >> 8);
            body[entries.Length * 2 + index * 2 + 1] = (byte)entries[index].Value;
        }

        return Add(Tagged(0xD0, entries.Length, body));
    }

    /// <summary>The file, with <paramref name="root"/> as its top object.</summary>
    internal byte[] ToBytes(int root)
    {
        using var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("bplist00"));

        var offsets = new long[_objects.Count];

        for (var index = 0; index < _objects.Count; index++)
        {
            offsets[index] = stream.Position;
            writer.Write(_objects[index]);
        }

        var table = stream.Position;

        foreach (var offset in offsets)
        {
            writer.Write((byte)(offset >> 8));
            writer.Write((byte)offset);
        }

        writer.Write(new byte[6]);
        writer.Write((byte)2);
        writer.Write((byte)2);
        WriteBigEndian(writer, _objects.Count);
        WriteBigEndian(writer, root);
        WriteBigEndian(writer, table);

        return stream.ToArray();
    }

    private static void WriteBigEndian(BinaryWriter writer, long value)
    {
        for (var index = 7; index >= 0; index--)
        {
            writer.Write((byte)(value >> (index * 8)));
        }
    }

    // A low nibble of 15 says the count did not fit in it and an integer object follows inline.
    private static byte[] Tagged(int marker, int count, byte[] body)
    {
        using var stream = new MemoryStream();

        if (count < 15)
        {
            stream.WriteByte((byte)(marker | count));
        }
        else
        {
            stream.WriteByte((byte)(marker | 0x0F));
            stream.WriteByte(0x10);
            stream.WriteByte((byte)count);
        }

        stream.Write(body, 0, body.Length);

        return stream.ToArray();
    }

    private int Add(byte[] bytes)
    {
        _objects.Add(bytes);

        return _objects.Count - 1;
    }
}
