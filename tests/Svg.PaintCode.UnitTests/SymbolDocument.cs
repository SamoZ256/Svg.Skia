// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;

namespace Svg.PaintCode.UnitTests;

/// <summary>
/// A target canvas and three instances of it: one that passes everything along, one that rebinds a
/// variable, and one that names a canvas the document does not hold. Beside them, a colour handed
/// down two levels of symbol, the way sr_window reaches its thermometer.
/// </summary>
internal static class SymbolDocument
{
    internal static byte[] Bytes(bool cycle = false)
    {
        var archive = new KeyedArchiveBuilder();

        var purple = ScopeDocument.Color(archive, "colorPurple", "0.3725490196 0.0 0.7882352941 1", usage: 1);
        var accent = ScopeDocument.Color(archive, "accentColorOn", "0.9843137255 0.2431372549 0.447432841 1", usage: 1);

        // Nobody marked it used, so it is a constant: what an instance hands along is its bytes.
        var blue = ScopeDocument.Color(archive, "colorBlue", "0 0 1 1");
        var light = Variable(archive, "isLight", 4, archive.Value(false), 5, 1);
        var dark = Variable(archive, "isNotLight", 4, archive.Value(true), 13, 0, "!isLight");

        var target = Canvas(archive, "badge", archive.Array(
            Shape(archive, "Mark", purple, archive.Dictionary(("fill", Expression(archive, "isLight ? colorPurple : colorPurple", 5, purple))))));

        // A number that drives where a shape sits, and an instance that pins it to something other
        // than its default: what a level indicator is, and what the offset has to survive.
        var level = Variable(archive, "level", 2, archive.Value(1d), 2, 1);

        // Two whole numbers the integer guess would retype, and what tells them apart: the fan's
        // step is multiplied by a fraction, which the language refuses of an integer, and the phase
        // is only ever compared. Neither is drawn by any canvas here; what they test is whether
        // retyping one of them costs the slider its driven transform.
        var step = Variable(archive, "step", 2, archive.Value(2d), 0, 1);
        var animation = Variable(archive, "animation", 2, archive.Value(1d), 2, 1);
        var rotationSpeed = Variable(archive, "rotationSpeed", 2, archive.Value(-720d), 13, 0, "step * animation * -360");
        var phase = Variable(archive, "phase", 2, archive.Value(3d), 0, 1);
        var phase3 = Variable(archive, "phase3", 4, archive.Value(true), 13, 0, "phase == 3");

        var driven = Canvas(archive, "slider", archive.Array(
            Driven(archive, purple, archive.Dictionary(
                ("displayAnchorY", Expression(archive, "level * 10", 2, archive.Value(10d)))))));

        var host = Canvas(archive, "host", archive.Array(
            Symbol(archive, "Plain", "badge", archive.Dictionary(("VIRTUAL__isLight", Expression(archive, "isLight", 4, archive.Value(false))))),
            Symbol(archive, "Flipped", "badge", archive.Dictionary(("VIRTUAL__isLight", Expression(archive, "isNotLight", 4, archive.Value(true))))),
            Symbol(archive, "Missing", "nowhere", archive.Dictionary()),
            Symbol(archive, "Half", "slider", archive.Dictionary(("VIRTUAL__level", Expression(archive, "0.5", 2, archive.Value(0.5d))))),
            Moved(archive)));

        // glyph draws in accentColorOn and wrapper hands it that colour by its own name, so what
        // glyph draws inside a wrapper is whatever the wrapper was given: its own accent placed
        // directly, colorPurple through Outer, and blue's bytes through Pinned.
        var glyph = Canvas(archive, "glyph", archive.Array(Shape(archive, "Glyph", accent, archive.Dictionary())));
        var wrapper = Canvas(archive, "wrapper", archive.Array(
            Symbol(archive, "Inner", "glyph", archive.Dictionary(), archive.Dictionary(("VIRTUAL__accentColorOn", accent)))));
        var chain = Canvas(archive, "chain", archive.Array(
            Symbol(archive, "Direct", "glyph", archive.Dictionary(), archive.Dictionary(("VIRTUAL__accentColorOn", accent))),
            Symbol(archive, "Outer", "wrapper", archive.Dictionary(), archive.Dictionary(("VIRTUAL__accentColorOn", purple))),
            Symbol(archive, "Pinned", "wrapper", archive.Dictionary(), archive.Dictionary(("VIRTUAL__accentColorOn", blue)))));

        // A canvas that holds a symbol of itself, for the test that says so rather than recursing.
        var looping = cycle
            ? Canvas(archive, "loop", archive.Array(Symbol(archive, "Self", "loop", archive.Dictionary())))
            : (int?)null;

        var canvases = looping is { } self
            ? archive.Array(target, driven, host, glyph, wrapper, chain, self)
            : archive.Array(target, driven, host, glyph, wrapper, chain);

        return archive.ToBytes(
            ("styleKitName", archive.Text("Symbols")),
            ("desks", archive.Array(archive.Object("PPDesk", ("name", archive.Text("Desk")), ("canvases", canvases)))),
            ("library", archive.Object(
                "PPLibrary",
                ("colors", archive.Array(purple, accent, blue)),
                ("variables", archive.Array(light, dark, level, step, animation, rotationSpeed, phase, phase3)))));
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

    /// <summary>An instance, with the plain values it hands its target in <paramref name="values"/>.</summary>
    private static int Symbol(KeyedArchiveBuilder archive, string name, string target, int bindings, int? values = null)
    {
        var members = new List<(string, int)>
        {
            ("name", archive.Text(name)),
            ("propertyValueProviders", bindings),
            ("symbolProviderID", archive.Object(
                "PPSymbolProviderID",
                ("name", archive.Text(target)),
                ("identifier", archive.Text(target))))
        };

        if (values is { } given)
        {
            members.Add(("virtualProperties", given));
        }

        return archive.Object(
            "PPSymbol",
            members.ToArray(),
            ("anchorX", 0d), ("anchorY", -15d), ("x", 0d), ("y", -15d), ("width", 15d), ("height", 15d),
            ("alpha", 1d), ("visibilityMode", 1), ("isKeepingAnchorAtDefaultPosition", true));
    }

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
