// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Text;

namespace Svg.PaintCode.UnitTests;

/// <summary>A document built in code, so a test can name the numbers it is about.</summary>
internal static class SampleDocument
{
    // One desk, one canvas, one two-point bezier filled with a bound colour, and four variables.
    // The numbers are the ones verified against the C# PaintCode generates for the same document.
    internal static byte[] Bytes()
    {
        var archive = new KeyedArchiveBuilder();

        var purple = Color(archive, "colorPurple", "0.3725490196 0.0 0.7882352941 1");
        var purple70 = archive.Object(
            "PPColor",
            new[] { ("name", archive.Text("purple70")), ("parentColor", purple) },
            ("isDerived", true), ("operation", 2), ("operationAmount", 0.7d));

        // The other two operations PaintCode derives a colour by, so a chain of all three is in
        // here: the sample's own accentColorOff is desaturated, then shadowed, then given an alpha.
        var purpleFade = archive.Object(
            "PPColor",
            new[] { ("name", archive.Text("purpleFade")), ("parentColor", purple) },
            ("isDerived", true), ("operation", 2), ("operationAmount", 0.7d));

        var purpleGrey = archive.Object(
            "PPColor",
            new[] { ("name", archive.Text("purpleGrey")), ("parentColor", purple) },
            ("isDerived", true), ("operation", 3), ("operationAmount", 0.2d));

        var purpleShade = archive.Object(
            "PPColor",
            new[] { ("name", archive.Text("purpleShade")), ("parentColor", purple) },
            ("isDerived", true), ("operation", 1), ("operationAmount", 0.2d));

        var point = archive.Object(
            "PPPathPoint",
            ("position", archive.Text("{9.8047, -1.5499}")),
            ("enteringControlPoint", archive.Text("{-2.6863, -4.9265}")),
            ("exitingControlPoint", archive.Text("{0.521, 0.9554}")));

        var second = archive.Object(
            "PPPathPoint",
            ("position", archive.Text("{12.4152, 0}")),
            ("enteringControlPoint", archive.Text("{-1.0882, 0}")),
            ("exitingControlPoint", archive.Text("{1.0882, 0}")));

        var path = archive.Object(
            "PPPath",
            ("contours", archive.Array(archive.Array(point, second))),
            ("contoursClosedStatus", archive.Array(archive.Value(true))));

        var bezier = archive.Object(
            "PPBezier",
            new[]
            {
                ("name", archive.Text("Bezier")),
                ("path", path),
                ("fill", purple),
                ("propertyValueProviders", archive.Dictionary(
                    ("fill", Expression(archive, "state ? colorPurple : colorPurple", 5, purple))))
            },
            ("anchorX", 3.0844d), ("anchorY", -3.6379d), ("windingRule", 1), ("alpha", 1d), ("visibilityMode", 1));

        // A second shape, filled with the library's own colour and driven by nothing, so a test can
        // tell a bound fill from one that is simply a name.
        var plain = archive.Object(
            "PPBezier",
            new[]
            {
                ("name", archive.Text("Bezier 2")),
                ("path", archive.Object(
                    "PPPath",
                    ("contours", archive.Array(archive.Array(second))),
                    ("contoursClosedStatus", archive.Array(archive.Value(false))))),
                ("fill", purple)
            },
            ("anchorX", 0d), ("anchorY", 0d), ("alpha", 1d), ("visibilityMode", 1));

        var group = archive.Object(
            "PPGroup",
            new[] { ("name", archive.Text("Canvas Group")), ("shapesAndGroups", archive.Array(bezier, plain)) },
            ("alpha", 1d), ("visibilityMode", 1));

        var canvas = archive.Object(
            "PPCanvas",
            new[] { ("name", archive.Text("overlay-error")), ("bounds", archive.Text("{{54, 136}, {30, 30}}")), ("rootGroup", group) },
            ("isExported", true), ("isAvailableAsSymbol", true));

        var desk = archive.Object("PPDesk", ("name", archive.Text("Overlays")), ("canvases", archive.Array(canvas)));

        var library = archive.Object("PPLibrary", ("colors", archive.Array(purple, purpleFade, purpleGrey, purpleShade)), ("variables", archive.Array(
            // The kinds PaintCode's own menu gives these: 5 is Boolean, 0 is Number. They used to be
            // 0 and 2 -- anything but 13 -- from when kind was read only to tell a derived variable
            // from an input, which made a boolean claim to be a number and a number a fraction.
            Variable(archive, "state", 5, Constant(archive, 4, archive.Value(true))),
            Variable(archive, "level", 0, Constant(archive, 2, archive.Value(1d), Interval(archive, 0, 1))),
            Variable(archive, "off", 13, Expression(archive, "!state", 4, archive.Value(false))),
            Variable(archive, "purple70", 13, Expression(archive, "withAlpha(colorPurple, 0.7)", 5, purple70)))));

        return archive.ToBytes(
            ("styleKitName", archive.Text("Icons")),
            ("desks", archive.Array(desk)),
            ("library", library));
    }

    private static int Color(KeyedArchiveBuilder archive, string name, string components)
        => archive.Object(
            "PPColor",
            new[]
            {
                ("name", archive.Text(name)),
                ("basicNSColor", archive.Object(
                    "NSColor",
                    new[] { ("NSComponents", archive.Data(Encoding.ASCII.GetBytes(components))) },
                    ("NSColorSpace", 1)))
            },
            ("isDerived", false), ("operation", 0), ("usage", 1));

    private static int Interval(KeyedArchiveBuilder archive, double minimum, double maximum)
        => archive.Object("PPLimitInterval", System.Array.Empty<(string, int)>(), ("min", minimum), ("max", maximum));

    private static int Constant(KeyedArchiveBuilder archive, int type, int value, int? limit = null)
        => archive.Object(
            "PPValueProviderConstant",
            limit is { } bounded ? new[] { ("value", value), ("limit", bounded) } : new[] { ("value", value) },
            ("type", type));

    private static int Expression(KeyedArchiveBuilder archive, string expression, int type, int value)
        => archive.Object(
            "PPValueProviderExpression",
            new[] { ("expression", archive.Text(expression)), ("value", value) },
            ("type", type));

    private static int Variable(KeyedArchiveBuilder archive, string name, int kind, int provider)
        => archive.Object(
            "PPVariable",
            new[] { ("name", archive.Text(name)), ("valueProvider", provider) },
            ("kind", kind), ("usage", kind == 13 ? 0 : 1));
}
