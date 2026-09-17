// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Text;

namespace Svg.PaintCode.UnitTests;

/// <summary>
/// A target canvas and three instances of it: one that passes everything along, one that rebinds a
/// variable, and one that names a canvas the document does not hold.
/// </summary>
internal static class SymbolDocument
{
    internal static byte[] Bytes(bool cycle = false)
    {
        var archive = new KeyedArchiveBuilder();

        var purple = Color(archive, "colorPurple", "0.3725490196 0.0 0.7882352941 1");
        var light = Variable(archive, "isLight", 4, archive.Value(false), 5, 1);
        var dark = Variable(archive, "isNotLight", 4, archive.Value(true), 13, 0, "!isLight");

        var target = Canvas(archive, "badge", archive.Array(
            Shape(archive, "Mark", purple, archive.Dictionary(("fill", Expression(archive, "isLight ? colorPurple : colorPurple", 5, purple))))));

        // A number that drives where a shape sits, and an instance that pins it to something other
        // than its default: what a level indicator is, and what the offset has to survive.
        var level = Variable(archive, "level", 2, archive.Value(1d), 2, 1);

        var driven = Canvas(archive, "slider", archive.Array(
            Driven(archive, purple, archive.Dictionary(
                ("displayAnchorY", Expression(archive, "level * 10", 2, archive.Value(10d)))))));

        var host = Canvas(archive, "host", archive.Array(
            Symbol(archive, "Plain", "badge", archive.Dictionary(("VIRTUAL__isLight", Expression(archive, "isLight", 4, archive.Value(false))))),
            Symbol(archive, "Flipped", "badge", archive.Dictionary(("VIRTUAL__isLight", Expression(archive, "isNotLight", 4, archive.Value(true))))),
            Symbol(archive, "Missing", "nowhere", archive.Dictionary()),
            Symbol(archive, "Half", "slider", archive.Dictionary(("VIRTUAL__level", Expression(archive, "0.5", 2, archive.Value(0.5d))))),
            Moved(archive)));

        // A canvas that holds a symbol of itself, for the test that says so rather than recursing.
        var looping = cycle
            ? Canvas(archive, "loop", archive.Array(Symbol(archive, "Self", "loop", archive.Dictionary())))
            : (int?)null;

        var canvases = looping is { } self ? archive.Array(target, driven, host, self) : archive.Array(target, driven, host);

        return archive.ToBytes(
            ("styleKitName", archive.Text("Symbols")),
            ("desks", archive.Array(archive.Object("PPDesk", ("name", archive.Text("Desk")), ("canvases", canvases)))),
            ("library", archive.Object(
                "PPLibrary",
                ("colors", archive.Array(purple)),
                ("variables", archive.Array(light, dark, level)))));
    }

    private static int Canvas(KeyedArchiveBuilder archive, string name, int children)
        => archive.Object(
            "PPCanvas",
            new[]
            {
                ("name", archive.Text(name)),
                ("bounds", archive.Text("{{0, 0}, {30, 30}}")),
                ("rootGroup", archive.Object(
                    "PPGroup",
                    new[] { ("name", archive.Text("Canvas Group")), ("shapesAndGroups", children) },
                    ("alpha", 1d), ("visibilityMode", 1)))
            },
            ("isExported", true), ("isAvailableAsSymbol", true));

    private static int Shape(KeyedArchiveBuilder archive, string name, int fill, int bindings)
        => archive.Object(
            "PPBezier",
            new[]
            {
                ("name", archive.Text(name)),
                ("fill", fill),
                ("propertyValueProviders", bindings),
                ("path", archive.Object(
                    "PPPath",
                    ("contours", archive.Array(archive.Array(
                        archive.Object("PPPathPoint", ("position", archive.Text("{0, 0}"))),
                        archive.Object("PPPathPoint", ("position", archive.Text("{10, -10}")))))),
                    ("contoursClosedStatus", archive.Array(archive.Value(true)))))
            },
            ("anchorX", 0d), ("anchorY", 0d), ("alpha", 1d), ("visibilityMode", 1));

    /// <summary>A shape whose anchor an expression moves, and which sits 3 above where it says.</summary>
    private static int Driven(KeyedArchiveBuilder archive, int fill, int bindings)
        => archive.Object(
            "PPBezier",
            new[]
            {
                ("name", archive.Text("Bar")),
                ("fill", fill),
                ("propertyValueProviders", bindings),
                ("path", archive.Object(
                    "PPPath",
                    ("contours", archive.Array(archive.Array(
                        archive.Object("PPPathPoint", ("position", archive.Text("{0, 0}"))),
                        archive.Object("PPPathPoint", ("position", archive.Text("{10, -10}")))))),
                    ("contoursClosedStatus", archive.Array(archive.Value(true)))))
            },
            ("anchorX", 0d), ("anchorY", -13d), ("alpha", 1d), ("visibilityMode", 1));

    private static int Symbol(KeyedArchiveBuilder archive, string name, string target, int bindings)
        => archive.Object(
            "PPSymbol",
            new[]
            {
                ("name", archive.Text(name)),
                ("propertyValueProviders", bindings),
                ("symbolProviderID", archive.Object(
                    "PPSymbolProviderID",
                    ("name", archive.Text(target)),
                    ("identifier", archive.Text(target))))
            },
            ("anchorX", 0d), ("anchorY", -15d), ("x", 0d), ("y", -15d), ("width", 15d), ("height", 15d),
            ("alpha", 1d), ("visibilityMode", 1), ("isKeepingAnchorAtDefaultPosition", true));

    /// <summary>
    /// An instance whose anchor was dragged off the corner of its box, so x and y carry the rest.
    /// </summary>
    /// <remarks>
    /// 221 of the sample document's 761 instances are like this, and a use has no path data to carry
    /// the offset in the way a shape does.
    /// </remarks>
    private static int Moved(KeyedArchiveBuilder archive)
        => archive.Object(
            "PPSymbol",
            new[]
            {
                ("name", archive.Text("Moved")),
                ("propertyValueProviders", archive.Dictionary()),
                ("symbolProviderID", archive.Object(
                    "PPSymbolProviderID",
                    ("name", archive.Text("badge")),
                    ("identifier", archive.Text("badge"))))
            },
            ("anchorX", 20d), ("anchorY", -25d), ("x", -5d), ("y", -10d), ("width", 15d), ("height", 15d),
            ("alpha", 1d), ("visibilityMode", 1), ("isKeepingAnchorAtDefaultPosition", false));

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

    private static int Expression(KeyedArchiveBuilder archive, string expression, int type, int value)
        => archive.Object(
            "PPValueProviderExpression",
            new[] { ("expression", archive.Text(expression)), ("value", value) },
            ("type", type));

    private static int Variable(KeyedArchiveBuilder archive, string name, int type, int value, int kind, int usage, string? expression = null)
    {
        var provider = expression is { }
            ? Expression(archive, expression, type, value)
            : archive.Object("PPValueProviderConstant", new[] { ("value", value) }, ("type", type));

        return archive.Object("PPVariable", new[] { ("name", archive.Text(name)), ("valueProvider", provider) }, ("kind", kind), ("usage", usage));
    }
}
