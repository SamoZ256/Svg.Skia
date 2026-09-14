// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Text;

namespace Svg.PaintCode.UnitTests;

/// <summary>A library of names for the translator tests to resolve against, and nothing else.</summary>
internal static class ScopeDocument
{
    internal static byte[] Bytes()
    {
        var archive = new KeyedArchiveBuilder();

        var library = archive.Object(
            "PPLibrary",
            ("colors", archive.Array(Color(archive, "navy", "0 0 0.2352941176 1"))),
            ("gradients", archive.Array(Gradient(archive, "warm"))),
            ("variables", archive.Array(
                Input(archive, "a", 4, archive.Value(true)),
                Input(archive, "b", 4, archive.Value(true)),
                Input(archive, "s", 3, archive.Text("")),
                Input(archive, "x", 2, archive.Value(1d)),
                Input(archive, "y", 2, archive.Value(1d)),
                Input(archive, "state", 4, archive.Value(true)),
                Input(archive, "enabled", 4, archive.Value(true)),
                Rect(archive, "area", "{{0, 0}, {117, 132}}"))));

        return archive.ToBytes(("styleKitName", archive.Text("Scope")), ("library", library));
    }

    private static int Gradient(KeyedArchiveBuilder archive, string name)
        => archive.Object(
            "PPGradient",
            new[]
            {
                ("name", archive.Text(name)),
                ("colorSteps", archive.Array(
                    archive.Object("PPGradientColor", new[] { ("color", Color(archive, string.Empty, "1 0 0 1")) }, ("location", 0d), ("interRatio", 0.5d)),
                    archive.Object("PPGradientColor", new[] { ("color", Color(archive, string.Empty, "0 0 1 1")) }, ("location", 1d), ("interRatio", 0.5d))))
            },
            ("usage", 0));

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
            ("isDerived", false), ("operation", 0), ("usage", 0));

    private static int Input(KeyedArchiveBuilder archive, string name, int type, int value)
        => archive.Object(
            "PPVariable",
            new[]
            {
                ("name", archive.Text(name)),
                ("valueProvider", archive.Object("PPValueProviderConstant", new[] { ("value", value) }, ("type", type)))
            },
            ("kind", 2), ("usage", 1));

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
