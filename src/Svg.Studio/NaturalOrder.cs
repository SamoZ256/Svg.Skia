// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;

namespace Svg.Studio;

/// <summary>
/// Names in the order somebody reading a numbered list expects: <c>icon2</c> before <c>icon10</c>.
/// </summary>
/// <remarks>
/// <para>
/// Compared a run at a time rather than a character at a time: a run of digits compares as a
/// number, everything else as text. Character by character, <c>icon10</c> and <c>icon2</c> are
/// decided by '1' against '2', which puts every icon below ten at the bottom of an import that
/// numbers its drawings.
/// </para>
/// <para>
/// Digits are compared as digits rather than parsed. A name may carry more of them than any
/// integer holds, and a comparer that overflowed or threw on one would take the pane down with it.
/// Leading zeros do not count towards the number, so <c>icon02</c> and <c>icon2</c> are the same
/// number and settle on the ordinal comparison at the end — which is what keeps the order total,
/// so rows do not swap about between rebuilds.
/// </para>
/// <para>
/// ASCII digits only. A name written with Eastern Arabic numerals sorts as text, which is the same
/// answer as before this existed, rather than a half-applied rule nobody can predict.
/// </para>
/// </remarks>
public sealed class NaturalOrder : IComparer<string?>
{
    public static readonly NaturalOrder Instance = new();

    private NaturalOrder()
    {
    }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0, j = 0;

        while (i < x.Length && j < y.Length)
        {
            var digits = Digit(x[i]);

            // A number has met a letter. Neither run says anything about the other, so the tail
            // comparison below settles it as text, which is where the two names differ anyway.
            if (digits != Digit(y[j]))
            {
                break;
            }

            var endX = Run(x, i, digits);
            var endY = Run(y, j, digits);

            var compared = digits
                ? Numbers(x, i, endX, y, j, endY)
                : string.Compare(x[i..endX], y[j..endY], StringComparison.InvariantCultureIgnoreCase);

            if (compared != 0)
            {
                return compared;
            }

            i = endX;
            j = endY;
        }

        // Whatever is left of either: one name ran out, or a run of one kind met a run of the
        // other. Ordinal last so two names that read the same still hold a settled order.
        var tail = string.Compare(x[i..], y[j..], StringComparison.InvariantCultureIgnoreCase);

        return tail != 0 ? tail : string.CompareOrdinal(x, y);
    }

    private static bool Digit(char c) => c is >= '0' and <= '9';

    /// <summary>Where the run of the same kind starting at <paramref name="from"/> ends.</summary>
    private static int Run(string text, int from, bool digits)
    {
        while (from < text.Length && Digit(text[from]) == digits)
        {
            from++;
        }

        return from;
    }

    /// <summary>Two runs of digits, as the numbers they spell.</summary>
    private static int Numbers(string x, int from, int to, string y, int at, int end)
    {
        // One zero is kept, so a run that is nothing but zeros is still a number.
        while (from < to - 1 && x[from] == '0')
        {
            from++;
        }

        while (at < end - 1 && y[at] == '0')
        {
            at++;
        }

        // More digits is a larger number, once the zeros in front are gone.
        if ((to - from) != (end - at))
        {
            return (to - from) - (end - at);
        }

        for (; from < to; from++, at++)
        {
            if (x[from] != y[at])
            {
                return x[from] - y[at];
            }
        }

        return 0;
    }
}
