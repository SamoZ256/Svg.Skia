// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using Svg.Pathing;
using Svg.Transforms;

namespace Svg;

/// <summary>
/// The values an element holds, compared exactly and written back as text that reads back as them.
/// </summary>
/// <remarks>
/// SVG.NET's own writers cannot do either. Its numbers are G7, so a float written and read again is
/// another float; <c>ex</c>, <c>pc</c>, <c>defer</c>, a skew of both angles and an odd point are
/// dropped; and a colour is a name on one target and hex on another. Its <c>Equals</c> calls an empty
/// transform list equal to every list, ignores a paint's fallback, and compares colours by name.
/// One canonical writer answers both questions here, so "unchanged" means "would be written the same".
/// </remarks>
public static class SvgValueSpelling
{
    /// <summary>Whether <paramref name="before"/> and <paramref name="after"/> are the same value, to the bit.</summary>
    public static bool Precise(object? before, object? after)
    {
        if (ReferenceEquals(before, after))
        {
            return true;
        }

        if (before is null || after is null)
        {
            return false;
        }

        if (before is ValueType || after is ValueType)
        {
            return before.GetType() == after.GetType() && before switch
            {
                float number => number.Equals((float)after),
                SvgUnit unit => unit.Type == ((SvgUnit)after).Type
                                && unit.IsEmpty == ((SvgUnit)after).IsEmpty
                                && unit.Value.Equals(((SvgUnit)after).Value),
                _ => before.Equals(after)
            };
        }

        return before switch
        {
            string text => after is string other && string.Equals(text, other, StringComparison.Ordinal),
            Uri uri => after is Uri other && string.Equals(uri.OriginalString, other.OriginalString, StringComparison.Ordinal),

            // A value with no text is unequal to all but itself, which ReferenceEquals answered.
            _ => Text(before, exact: true) is { } text && string.Equals(text, Text(after, exact: true), StringComparison.Ordinal)
        };
    }

    /// <summary>The text to write for <paramref name="name"/> on <paramref name="element"/>, holding <paramref name="value"/>.</summary>
    /// <param name="authored">What the file writes for it now, if anything: kept whole where it still reads as the value.</param>
    /// <param name="refusal">Why nothing can be written, naming the attribute and the element.</param>
    /// <returns>
    /// The authored text, or a fresh spelling, either one read back through the element's own
    /// converter and found equal; or null, where no spelling reads back as the value.
    /// </returns>
    public static string? Spell(SvgElement element, string name, object? value, string? authored, out string? refusal)
    {
        if (element is null)
        {
            throw new ArgumentNullException(nameof(element));
        }

        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        refusal = null;

        var about = $"'{name}' on <{SvgElementNames.NameOf(element)}>";

        if (authored?.IndexOf("{{", StringComparison.Ordinal) >= 0)
        {
            refusal = $"{about} is written with an expression, which only the expression's own rules may rewrite.";

            return null;
        }

        // Staged styles and custom keys have no converter: they are held as the text they were written.
        if (SvgElementFactory.FindProperty(element, name) is not { Converter: { } converter })
        {
            if (value is string held)
            {
                return held;
            }

            refusal = $"{about} is not an attribute this element reads.";

            return null;
        }

        // Not where the loader reads it otherwise than the converter does: a var() is resolved against
        // the element, and an attribute it finds malformed it ignores.
        if (authored is { } && authored.IndexOf("var(", StringComparison.OrdinalIgnoreCase) < 0
            && !SvgElementFactory.ShouldIgnoreInvalidPresentationStyleAttribute(name, authored) && ReadsBack(authored))
        {
            return authored;
        }

        var spelt = value is Enum
            ? converter.ConvertTo(element.OwnerDocument, CultureInfo.InvariantCulture, value, typeof(string)) as string
            : Text(value, exact: false, semicolons: converter is SvgSemicolonNumberCollectionConverter);

        if (spelt is { } && ReadsBack(spelt))
        {
            return spelt;
        }

        refusal = $"{about} would not read back as the value it holds.";

        return null;

        // Also what catches a converter that reads in the current culture: its answer is not the value.
        bool ReadsBack(string text)
        {
            try
            {
                return Precise(value, converter.ConvertFrom(element.OwnerDocument, CultureInfo.InvariantCulture, text));
            }
            catch (Exception failure) when (failure is not SvgProcessingLimitException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// <paramref name="value"/> in the fewest significant digits that read back as it, laid out as
    /// JavaScript writes a number; null for NaN or an infinity.
    /// </summary>
    /// <remarks>
    /// Not <c>"R"</c>, whose digits belong to the runtime rather than the target: on .NET Framework,
    /// which loads the netstandard2.0 build for Svg.Editor.Skia's net461, it is "G7, else G9". This
    /// loop gives what <c>"R"</c> gives on a modern runtime, and is still right where it is not.
    /// </remarks>
    public static string? Number(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return null;
        }

        if (value == 0f)
        {
            return "0";
        }

        var written = value.ToString("E8", CultureInfo.InvariantCulture);

        for (var digits = 1; digits < 9; digits++)
        {
            var shorter = value.ToString("E" + (digits - 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

            // TryParse: on .NET Framework a rounding past float.MaxValue throws rather than reading as infinity.
            if (float.TryParse(shorter, NumberStyles.Float, CultureInfo.InvariantCulture, out var read) && read == value)
            {
                written = shorter;

                break;
            }
        }

        return Laid(written);
    }

    /// <summary>The text of <paramref name="value"/>, or null where it has none this can write.</summary>
    /// <param name="exact">Floats as their bits, for <see cref="Precise"/>; otherwise as <see cref="Number"/> writes them.</param>
    /// <param name="semicolons">A number list as a semicolon list, which is how animation values are read.</param>
    internal static string? Text(object? value, bool exact, bool semicolons = false)
    {
        Func<float, string?> number = exact ? Bits : Number;

        return value switch
        {
            null => null,
            string text => text,
            Uri uri => uri.OriginalString,
            float single => number(single),
            int whole => whole.ToString(CultureInfo.InvariantCulture),
            bool flag => flag ? "true" : "false",
            SvgUnit unit => Unit(unit, number),
            SvgUnitCollection units => units.Count == 0
                ? units.StringForEmptyValue ?? string.Empty
                : Joined(units.Select(unit => Unit(unit, number)), " "),
            SvgNumberCollection numbers => Joined(numbers.Select(number), semicolons ? ";" : " "),
            SvgPointCollection points => Points(points, number),
            SvgViewBox box => Joined(new[] { number(box.MinX), number(box.MinY), number(box.Width), number(box.Height) }, " "),
            SvgAspectRatio ratio => (ratio.Defer ? "defer " : string.Empty) + ratio.Align + (ratio.Slice ? " slice" : string.Empty),
            SvgOrient orient => orient.IsAuto ? orient.IsAutoStartReverse ? "auto-start-reverse" : "auto" : number(orient.Angle),
            SvgTransformCollection transforms => Joined(transforms.Select(transform => Transform(transform, number)), " "),
            SvgPathSegmentList path => Joined(path.Select(segment => Segment(segment, number)), " "),
            SvgPaintServer paint => Paint(paint, number),
            _ => null
        };
    }

    private static string? Unit(SvgUnit unit, Func<float, string?> number)
    {
        if (unit.IsEmpty)
        {
            return null;
        }

        if (unit.Type == SvgUnitType.None)
        {
            return "none";
        }

        // The converter's own suffixes: SvgUnit.ToString has none for ex or pc.
        var suffix = unit.Type switch
        {
            SvgUnitType.Pixel => "px",
            SvgUnitType.Em => "em",
            SvgUnitType.Ex => "ex",
            SvgUnitType.Percentage => "%",
            SvgUnitType.Inch => "in",
            SvgUnitType.Centimeter => "cm",
            SvgUnitType.Millimeter => "mm",
            SvgUnitType.Pica => "pc",
            SvgUnitType.Point => "pt",
            _ => string.Empty
        };

        return number(unit.Value) is { } written ? written + suffix : null;
    }

    /// <remarks>An odd number at the end is kept, which ToString drops.</remarks>
    private static string? Points(SvgPointCollection points, Func<float, string?> number)
    {
        var pairs = new List<string?>();

        for (var i = 0; i < points.Count; i += 2)
        {
            pairs.Add(i + 1 < points.Count
                ? number(points[i].Value) is { } x && number(points[i + 1].Value) is { } y ? x + "," + y : null
                : number(points[i].Value));
        }

        return Joined(pairs, " ");
    }

    /// <remarks>The fewest arguments that say it, as the gizmo and the draw tools write a transform.</remarks>
    private static string? Transform(SvgTransform transform, Func<float, string?> number)
    {
        return transform switch
        {
            SvgTranslate move => move.Y == 0f ? Call("translate", move.X) : Call("translate", move.X, move.Y),
            SvgScale scale => scale.X.Equals(scale.Y) ? Call("scale", scale.X) : Call("scale", scale.X, scale.Y),
            SvgRotate turn => turn.CenterX == 0f && turn.CenterY == 0f
                ? Call("rotate", turn.Angle)
                : Call("rotate", turn.Angle, turn.CenterX, turn.CenterY),
            SvgMatrix matrix => matrix.Points is { Count: 6 } points ? Call("matrix", points.ToArray()) : null,

            // A skew of both angles has no function: WriteToString would keep one and drop the other.
            SvgSkew skew => skew.AngleY == 0f ? Call("skewX", skew.AngleX) : skew.AngleX == 0f ? Call("skewY", skew.AngleY) : null,
            SvgShear shear => Call("shear", shear.X, shear.Y),
            _ => null
        };

        string? Call(string name, params float[] arguments)
            => Joined(arguments.Select(number), " ") is { } written ? name + "(" + written + ")" : null;
    }

    /// <remarks>
    /// One command letter per segment, its relativity kept. A coordinate the command does not have
    /// is NaN — a line's Y for H, a curve's first control for S — and NaN anywhere else is no path.
    /// </remarks>
    private static string? Segment(SvgPathSegment segment, Func<float, string?> number)
    {
        var relative = segment.IsRelative;

        return segment switch
        {
            SvgMoveToSegment => Command('M', segment.End),
            SvgLineSegment when float.IsNaN(segment.End.Y) => Command('H', segment.End.X),
            SvgLineSegment when float.IsNaN(segment.End.X) => Command('V', segment.End.Y),
            SvgLineSegment => Command('L', segment.End),
            SvgCubicCurveSegment cubic when float.IsNaN(cubic.FirstControlPoint.X) || float.IsNaN(cubic.FirstControlPoint.Y)
                => Command('S', cubic.SecondControlPoint, cubic.End),
            SvgCubicCurveSegment cubic => Command('C', cubic.FirstControlPoint, cubic.SecondControlPoint, cubic.End),
            SvgQuadraticCurveSegment quadratic when float.IsNaN(quadratic.ControlPoint.X) || float.IsNaN(quadratic.ControlPoint.Y)
                => Command('T', quadratic.End),
            SvgQuadraticCurveSegment quadratic => Command('Q', quadratic.ControlPoint, quadratic.End),
            SvgArcSegment arc => Joined(
                new[]
                {
                    number(arc.RadiusX), number(arc.RadiusY), number(arc.Angle),
                    arc.Size == SvgArcSize.Large ? "1" : "0", arc.Sweep == SvgArcSweep.Positive ? "1" : "0",
                    number(arc.End.X), number(arc.End.Y)
                },
                " ") is { } written
                ? Letter('A') + " " + written
                : null,
            SvgClosePathSegment => Letter('Z').ToString(),
            _ => null
        };

        char Letter(char command) => relative ? char.ToLowerInvariant(command) : command;

        string? Command(char command, params object[] coordinates)
        {
            var numbers = coordinates.SelectMany(coordinate => coordinate is PointF point
                ? new[] { number(point.X), number(point.Y) }
                : new[] { number((float)coordinate) });

            return Joined(numbers, " ") is { } written ? Letter(command) + " " + written : null;
        }
    }

    /// <remarks>Never ToString, which writes a colour as <c>Red</c> on net6 and later and as hex on netstandard2.0.</remarks>
    private static string? Paint(SvgPaintServer paint, Func<float, string?> number)
    {
        if (ReferenceEquals(paint, SvgPaintServer.None))
        {
            return "none";
        }

        if (ReferenceEquals(paint, SvgPaintServer.Inherit))
        {
            return "inherit";
        }

        // Not a value to write but the absence of one: the attribute is taken away instead.
        if (ReferenceEquals(paint, SvgPaintServer.NotSet))
        {
            return null;
        }

        return paint switch
        {
            SvgDeferredPaintServer deferred => deferred.FallbackServer is { } fallback
                ? Paint(fallback, number) is { } written ? deferred.DeferredId + " " + written : null
                : deferred.DeferredId,
            SvgContextPaintServer context => context.Kind == SvgContextPaintKind.Stroke ? "context-stroke" : "context-fill",
            SvgColourServer colour => Hex(colour.Colour),

            // A gradient or a pattern a command set as the paint itself: the drawing names it by id.
            _ => paint.ID is { Length: > 0 } id ? "url(#" + id + ")" : null
        };
    }

    private static string Hex(Color colour)
    {
        var hex = new StringBuilder("#", 9)
            .Append(colour.R.ToString("x2", CultureInfo.InvariantCulture))
            .Append(colour.G.ToString("x2", CultureInfo.InvariantCulture))
            .Append(colour.B.ToString("x2", CultureInfo.InvariantCulture));

        return colour.A == 255 ? hex.ToString() : hex.Append(colour.A.ToString("x2", CultureInfo.InvariantCulture)).ToString();
    }

    private static string? Joined(IEnumerable<string?> parts, string separator)
    {
        var all = parts.ToList();

        return all.Any(part => part is null) ? null : string.Join(separator, all);
    }

    /// <remarks>0 and -0 alike, as a reader cannot tell them apart; netstandard2.0 has no SingleToInt32Bits.</remarks>
    private static string? Bits(float value)
        => value == 0f ? "0" : BitConverter.ToInt32(BitConverter.GetBytes(value), 0).ToString(CultureInfo.InvariantCulture);

    /// <summary>Exponent notation's digits laid out as JavaScript's <c>Number#toString</c> lays them.</summary>
    private static string Laid(string exponential)
    {
        var negative = exponential[0] == '-';
        var at = exponential.IndexOf('E');
        var digits = exponential.Substring(negative ? 1 : 0, at - (negative ? 1 : 0)).Replace(".", string.Empty).TrimEnd('0');
        var exponent = int.Parse(exponential.Substring(at + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        // Where the point goes, counted in digits from the first.
        var point = exponent + 1;
        string laid;

        if (point > 21 || point < -5)
        {
            var rest = digits.Length > 1 ? "." + digits.Substring(1) : string.Empty;

            laid = digits[0] + rest + "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
        }
        else if (point >= digits.Length)
        {
            laid = digits + new string('0', point - digits.Length);
        }
        else if (point > 0)
        {
            laid = digits.Substring(0, point) + "." + digits.Substring(point);
        }
        else
        {
            laid = "0." + new string('0', -point) + digits;
        }

        return negative ? "-" + laid : laid;
    }
}
