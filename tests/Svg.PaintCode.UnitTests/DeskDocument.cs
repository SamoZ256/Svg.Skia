// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
namespace Svg.PaintCode.UnitTests;

/// <summary>Two desks of canvases that sit somewhere, so a test can name where they end up.</summary>
/// <remarks>
/// Nothing is drawn on them: what these are for is the placing, and a canvas with an empty group
/// still writes a drawing of its own size. The numbers are chosen so every step of the arithmetic
/// reads differently — a desk that does not start at the origin, one that starts above it, a row
/// and a column, and a canvas the document forgot to place.
/// </remarks>
internal static class DeskDocument
{
    internal static byte[] Bytes()
    {
        var archive = new KeyedArchiveBuilder();

        // Overlays: three 30×30 canvases in an L, the desk's own corner at (54, 136).
        var one = Canvas(archive, "one", "{{54, 136}, {30, 30}}");
        var two = Canvas(archive, "two", "{{94, 136}, {30, 30}}");
        var three = Canvas(archive, "three", "{{54, 186}, {30, 30}}");

        // The document not saying where a canvas sat, which is the one case there is nothing to
        // carry across. It still becomes a drawing.
        var nowhere = Canvas(archive, "nowhere", null);

        // Controls: one canvas below the desk's own origin — a desk's y points up — and a smaller
        // one above it, so a desk whose numbers go negative is in here too.
        var four = Canvas(archive, "four", "{{200, -40}, {60, 60}}");
        var five = Canvas(archive, "five", "{{200, 40}, {30, 30}}");

        var overlays = archive.Object(
            "PPDesk",
            ("name", archive.Text("Overlays")),
            ("canvases", archive.Array(one, two, three, nowhere)));

        var controls = archive.Object(
            "PPDesk",
            ("name", archive.Text("Controls")),
            ("canvases", archive.Array(four, five)));

        return archive.ToBytes(
            ("styleKitName", archive.Text("Icons")),
            ("desks", archive.Array(overlays, controls)));
    }

    /// <param name="bounds">Where it sat and how big it was, or null for a canvas that says neither.</param>
    private static int Canvas(KeyedArchiveBuilder archive, string name, string? bounds)
    {
        var group = archive.Object(
            "PPGroup",
            new[] { ("name", archive.Text(name + " Group")), ("shapesAndGroups", archive.Array()) },
            ("alpha", 1d), ("visibilityMode", 1));

        var members = bounds is { } rect
            ? new[] { ("name", archive.Text(name)), ("bounds", archive.Text(rect)), ("rootGroup", group) }
            : new[] { ("name", archive.Text(name)), ("rootGroup", group) };

        return archive.Object(
            "PPCanvas",
            members,
            ("isExported", true), ("isAvailableAsSymbol", false));
    }
}
