// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Linq;
using System.Text;

namespace Svg.PaintCode.UnitTests;

/// <summary>A library of names for the translator tests to resolve against, and nothing else.</summary>
internal static class ScopeDocument
{
    internal static byte[] Bytes(string? derived = null, int derivedType = 2)
    {
        var archive = new KeyedArchiveBuilder();
        var tint = Color(archive, "tint", "0 1 0 1", usage: 1);

        var library = archive.Object(
            "PPLibrary",
            ("colors", archive.Array(Color(archive, "navy", "0 0 0.2352941176 1"), tint)),
            // "Cool" with a capital, because PaintCode's own code -- and so its expressions -- name a
            // library item with its first letter lowered, and the library keeps what was typed.
            // "blend" runs from a parameter through a middle stop, saved as the halfway colour.
            ("gradients", archive.Array(
                Gradient(archive, "warm"),
                Gradient(archive, "Cool"),
                Gradient(
                    archive,
                    "blend",
                    (tint, 0d, false),
                    (Color(archive, "PPGradientMidColor", "0 0.5 0.5 1"), 0.5d, true),
                    (Color(archive, string.Empty, "0 0 1 1"), 1d, false)))),
            ("variables", archive.Array(
                Input(archive, "a", 4, archive.Value(true)),
                Input(archive, "b", 4, archive.Value(true)),
                Input(archive, "s", 3, archive.Text("")),
                Input(archive, "x", 2, archive.Value(1d)),
                Input(archive, "y", 2, archive.Value(1d)),
                Input(archive, "state", 4, archive.Value(true)),
                Input(archive, "enabled", 4, archive.Value(true)),
                // A whole number and a fraction, for where the integer guess has to say num().
                Input(archive, "n", 2, archive.Value(2d)),
                Input(archive, "f", 2, archive.Value(0.5d), kind: 2),
                Derived(archive, "bad", derived ?? "x", derivedType),
                // Declared after bad, so bad can read a local that has not been translated yet --
                // and one that the integer guess turns into an integer when it is.
                Derived(archive, "count", "n + 1", 2),
                Rect(archive, "area", "{{0, 0}, {117, 132}}"))));

        return archive.ToBytes(("styleKitName", archive.Text("Scope")), ("library", library));
    }

    /// <summary>A library gradient through <paramref name="steps"/>, or from red to blue where none are given.</summary>
    internal static int Gradient(KeyedArchiveBuilder archive, string name, params (int Color, double Location, bool Middle)[] steps)
    {
        if (steps.Length == 0)
        {
            steps = new[] { (Color(archive, string.Empty, "1 0 0 1"), 0d, false), (Color(archive, string.Empty, "0 0 1 1"), 1d, false) };
        }

        return archive.Object(
            "PPGradient",
            new[]
            {
                ("name", archive.Text(name)),
                ("colorSteps", archive.Array(steps
                    .Select(step => archive.Object(
                        "PPGradientColor",
                        new[] { ("color", step.Color) },
                        ("location", step.Location), ("interRatio", 0.5d), ("isMiddleColor", step.Middle)))
                    .ToArray()))
            },
            ("usage", 0));
    }

    /// <summary>A library colour: a parameter where <paramref name="usage"/> marks it used, a constant where not.</summary>
    internal static int Color(KeyedArchiveBuilder archive, string name, string components, int usage = 0)
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
            ("isDerived", false), ("operation", 0), ("usage", usage));

    /// <summary>A variable derived from the others, so a test can hand the declarations one that will not go.</summary>
    private static int Derived(KeyedArchiveBuilder archive, string name, string expression, int type)
        => archive.Object(
            "PPVariable",
            new[]
            {
                ("name", archive.Text(name)),
                ("valueProvider", archive.Object(
                    "PPValueProviderExpression",
                    new[] { ("expression", archive.Text(expression)), ("value", type == 3 ? archive.Text("1") : archive.Value(1d)) },
                    ("type", type)))
            },
            ("kind", 13), ("usage", 0));

    private static int Input(KeyedArchiveBuilder archive, string name, int type, int value, int? kind = null)
        => archive.Object(
            "PPVariable",
            new[]
            {
                ("name", archive.Text(name)),
                ("valueProvider", archive.Object("PPValueProviderConstant", new[] { ("value", value) }, ("type", type)))
            },
            // The kind PaintCode's menu gives a variable storing this type, rather than one constant
            // for all of them: 0 is Number, 4 is Text, 5 is Boolean. It used to be 2 throughout --
            // anything but 13 -- from when kind was read only to tell a derived variable from an
            // input, which left every one of these claiming to be a fraction.
            ("kind", kind ?? type switch { 3 => 4, 4 => 5, _ => 0 }), ("usage", 1));

    private static int Rect(KeyedArchiveBuilder archive, string name, string rectangle)
        => archive.Object(
            "PPVariable",
            new[]
            {
                ("name", archive.Text(name)),
                ("valueProvider", archive.Object("PPValueProviderConstant", new[] { ("value", archive.Text(rectangle)) }, ("type", 10)))
            },
            ("kind", 8), ("usage", 0));
}
