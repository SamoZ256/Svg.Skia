// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Text;

namespace Svg.PaintCode;

/// <summary>
/// Reads Apple's <c>bplist00</c> binary property list into plain objects: <see cref="long"/>,
/// <see cref="double"/>, <see cref="bool"/>, <see cref="string"/>, <see cref="byte"/> arrays,
/// <see cref="List{T}"/>, <see cref="Dictionary{TKey,TValue}"/> and <see cref="PaintCodeUid"/>.
/// </summary>
/// <remarks>
/// Hand-rolled because no plist package is in <c>Directory.Packages.props</c> and the whole format is
/// byte-shifting: a header, a table of offsets, and one tagged object per offset.
/// </remarks>
internal static class PaintCodeArchive
{
    private const int TrailerLength = 32;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("bplist00");

    internal static object? Read(byte[] bytes)
    {
        if (bytes is null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

        if (bytes.Length < Magic.Length + TrailerLength)
        {
            throw new PaintCodeException("Not a binary property list: the file is too short to hold one.");
        }

        for (var index = 0; index < Magic.Length; index++)
        {
            if (bytes[index] != Magic[index])
            {
                throw new PaintCodeException("Not a binary property list: the bplist00 header is missing.");
            }
        }

        var trailer = bytes.Length - TrailerLength;
        var offsetSize = bytes[trailer + 6];
        var referenceSize = bytes[trailer + 7];
        var count = checked((int)ReadBigEndian(bytes, trailer + 8, 8));
        var root = checked((int)ReadBigEndian(bytes, trailer + 16, 8));
        var table = checked((int)ReadBigEndian(bytes, trailer + 24, 8));

        if (offsetSize is 0 or > 8 || referenceSize is 0 or > 8)
        {
            throw new PaintCodeException("Not a binary property list: the trailer declares an impossible word size.");
        }

        if (count < 0 || table < 0 || (long)table + (long)count * offsetSize > trailer)
        {
            throw new PaintCodeException("The property list's offset table runs past the end of the file.");
        }

        var offsets = new int[count];

        for (var index = 0; index < count; index++)
        {
            offsets[index] = checked((int)ReadBigEndian(bytes, table + index * offsetSize, offsetSize));
        }

        var reader = new Reader(bytes, offsets, referenceSize);

        return reader.Object(root);
    }

    private static long ReadBigEndian(byte[] bytes, int at, int length)
    {
        if (at < 0 || at + length > bytes.Length)
        {
            throw new PaintCodeException("The property list ends in the middle of a value.");
        }

        var value = 0L;

        for (var index = 0; index < length; index++)
        {
            value = (value << 8) | bytes[at + index];
        }

        return value;
    }

    // One reader per file: the object table is walked recursively, and a container holds references
    // rather than its children, so every read needs the offsets and the reference width to hand.
    private sealed class Reader
    {
        private readonly byte[] _bytes;
        private readonly int[] _offsets;
        private readonly int _referenceSize;
        private readonly object?[] _read;
        private readonly bool[] _reading;

        internal Reader(byte[] bytes, int[] offsets, int referenceSize)
        {
            _bytes = bytes;
            _offsets = offsets;
            _referenceSize = referenceSize;
            _read = new object?[offsets.Length];
            _reading = new bool[offsets.Length];
        }

        internal object? Object(int index)
        {
            if (index < 0 || index >= _offsets.Length)
            {
                throw new PaintCodeException($"The property list references object {index}, which it does not hold.");
            }

            if (_read[index] is { } already)
            {
                return already;
            }

            // A container that reached itself would recurse until the stack gave out. Archives are
            // written as a flat table precisely so they can share, and sharing admits cycles.
            if (_reading[index])
            {
                throw new PaintCodeException($"The property list's object {index} contains itself.");
            }

            _reading[index] = true;

            try
            {
                var value = Decode(_offsets[index]);
                _read[index] = value;

                return value;
            }
            finally
            {
                _reading[index] = false;
            }
        }

        private object? Decode(int at)
        {
            if (at < 0 || at >= _bytes.Length)
            {
                throw new PaintCodeException("The property list's offset table points past the end of the file.");
            }

            var marker = _bytes[at];
            var kind = marker >> 4;
            var info = marker & 0x0F;

            return kind switch
            {
                0x0 => Singleton(info),
                0x1 => ReadBigEndian(_bytes, at + 1, 1 << info),
                0x2 => Real(at + 1, 1 << info),
                0x3 => Real(at + 1, 8),
                0x4 => Data(at, info),
                0x5 => Text(at, info, Encoding.ASCII, 1),
                0x6 => Text(at, info, Encoding.BigEndianUnicode, 2),
                0x8 => new PaintCodeUid(checked((int)ReadBigEndian(_bytes, at + 1, info + 1))),
                0xA or 0xC => Array(at, info),
                0xD => Dictionary(at, info),
                _ => throw new PaintCodeException($"The property list holds an object of an unknown kind (0x{marker:X2}).")
            };
        }

        private static object? Singleton(int info)
            => info switch
            {
                0x0 => null,
                0x8 => false,
                0x9 => true,
                _ => throw new PaintCodeException($"The property list holds a singleton this reader has no case for (0x0{info:X1}).")
            };

        private object Real(int at, int length)
            => length switch
            {
                4 => (double)BitConverter.ToSingle(Reversed(at, 4), 0),
                8 => BitConverter.ToDouble(Reversed(at, 8), 0),
                _ => throw new PaintCodeException($"The property list holds a {length}-byte real, which has no meaning.")
            };

        // Plists are big-endian and BitConverter follows the machine, so the bytes are turned round
        // rather than assumed.
        private byte[] Reversed(int at, int length)
        {
            if (at + length > _bytes.Length)
            {
                throw new PaintCodeException("The property list ends in the middle of a real.");
            }

            var bytes = new byte[length];

            for (var index = 0; index < length; index++)
            {
                bytes[index] = _bytes[at + length - 1 - index];
            }

            if (!BitConverter.IsLittleEndian)
            {
                System.Array.Reverse(bytes);
            }

            return bytes;
        }

        // A low nibble of 15 means the count did not fit in it and an integer object follows.
        private int Count(int at, int info, out int body)
        {
            if (info != 0x0F)
            {
                body = at + 1;

                return info;
            }

            var marker = _bytes[at + 1];

            if (marker >> 4 != 0x1)
            {
                throw new PaintCodeException("The property list's long-form count is not an integer.");
            }

            var length = 1 << (marker & 0x0F);
            body = at + 2 + length;

            return checked((int)ReadBigEndian(_bytes, at + 2, length));
        }

        private byte[] Data(int at, int info)
        {
            var count = Count(at, info, out var body);

            if (body + count > _bytes.Length)
            {
                throw new PaintCodeException("The property list ends in the middle of a data value.");
            }

            var bytes = new byte[count];
            System.Array.Copy(_bytes, body, bytes, 0, count);

            return bytes;
        }

        private string Text(int at, int info, Encoding encoding, int width)
        {
            var count = Count(at, info, out var body) * width;

            if (body + count > _bytes.Length)
            {
                throw new PaintCodeException("The property list ends in the middle of a string.");
            }

            return encoding.GetString(_bytes, body, count);
        }

        private List<object?> Array(int at, int info)
        {
            var count = Count(at, info, out var body);
            var items = new List<object?>(count);

            for (var index = 0; index < count; index++)
            {
                items.Add(Object(Reference(body + index * _referenceSize)));
            }

            return items;
        }

        private Dictionary<string, object?> Dictionary(int at, int info)
        {
            var count = Count(at, info, out var body);
            var values = body + count * _referenceSize;
            var entries = new Dictionary<string, object?>(count, StringComparer.Ordinal);

            for (var index = 0; index < count; index++)
            {
                if (Object(Reference(body + index * _referenceSize)) is not string key)
                {
                    throw new PaintCodeException("The property list holds a dictionary key that is not a string.");
                }

                entries[key] = Object(Reference(values + index * _referenceSize));
            }

            return entries;
        }

        private int Reference(int at) => checked((int)ReadBigEndian(_bytes, at, _referenceSize));
    }
}
