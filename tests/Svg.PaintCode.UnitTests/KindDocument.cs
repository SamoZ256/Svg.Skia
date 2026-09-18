// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
namespace Svg.PaintCode.UnitTests;

/// <summary>One library holding one variable, declared as whichever kind a test is asking about.</summary>
/// <remarks>
/// The kinds are PaintCode's own, read off a document carrying one variable of each: the menu offers
/// Number, Fraction, Angle, Text, Boolean, Point, Size and Rectangle, and a value provider stores all
/// three numeric ones as the same double. Built rather than committed, so the numbers a reader is
/// being held to are written where somebody can see them.
/// </remarks>
internal static class KindDocument
{
    internal static byte[] Bytes(int kind, int type, bool limited = false)
    {
        var archive = new KeyedArchiveBuilder();

        // PaintCode stores a limit on the value provider, where nothing ties it to the kind -- so a
        // boolean or a text variable can carry one, and the importer has to decide not to pass it on.
        // The value follows the type, a declaration whose literal cannot be written being refused
        // before its range is ever looked at.
        var value = ("value", archive.Value(type switch { 3 => "on", 4 => true, _ => (object)1d }));
        var limit = ("limit", archive.Object("PPLimitInterval", System.Array.Empty<(string, int)>(), ("min", 0d), ("max", 1d)));

        var library = archive.Object(
            "PPLibrary",
            ("variables", archive.Array(
                archive.Object(
                    "PPVariable",
                    new[]
                    {
                        ("name", archive.Text("test")),
                        ("valueProvider", archive.Object(
                            "PPValueProviderConstant",
                            limited ? new[] { value, limit } : new[] { value },
                            ("type", type)))
                    },
                    ("kind", kind), ("usage", 1)))));

        return archive.ToBytes(("styleKitName", archive.Text("Kinds")), ("library", library));
    }
}
