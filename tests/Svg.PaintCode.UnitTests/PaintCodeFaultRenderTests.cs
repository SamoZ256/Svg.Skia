// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;
using Svg.Expressions;
using Svg.Skia;
using Svg.Skia.UnitTests.Common;
using Xunit;

namespace Svg.PaintCode.UnitTests;

/// <summary>
/// Each geometry fault the conversion has had, converted, rendered, and held against SkiaSharp
/// drawing what PaintCode draws.
/// </summary>
/// <remarks>
/// Every one of these passed the markup asserts in <see cref="PaintCodeSvgWriterTests"/> while
/// drawing the wrong picture, and was found only by comparing pixels against PaintCode. The
/// expected side is the geometry written out by hand rather than a stored raster, so a fixture says
/// what PaintCode does instead of what it once measured.
/// </remarks>
public class PaintCodeFaultRenderTests
{
    /// <summary>A 30×30 canvas at 180×180, where a misplaced shape is pixels, not rounding.</summary>
    private const int Size = 180;

    private const float Scale = Size / 30f;

    private static readonly SKColor s_red = new(220, 40, 40);

    /// <summary>
    /// Both sides draw through the same Skia and every fixture measures 0; a shape half a unit out
    /// measures about 0.065 and the wrong colour 0.062, so anything above noise is a fault.
    /// </summary>
    private const double Threshold = 0.005;

    /// <summary>PaintCode's inner radius is a percentage of the outer; read as a fraction, the star exploded.</summary>
    [Fact]
    public void A_Star_Reaches_Its_Inner_Points_At_A_Percentage_Of_Its_Outer_Ones()
    {
        var star = Filled(PaintCodeShapeKind.Star, Box(5, 5, 20, 20), new PaintCodeShapeMetrics(0, true, true, true, true, 0, 0, true, 5, 40));

        Compares(Written(star), canvas =>
        {
            using var builder = new SKPathBuilder();

            for (var point = 0; point < 10; point++)
            {
                var radius = point % 2 == 0 ? 10f : 4f;
                var angle = (-90f + (point * 36f)) * MathF.PI / 180f;
                var at = new SKPoint(15f + (radius * MathF.Cos(angle)), 15f + (radius * MathF.Sin(angle)));

                if (point == 0)
                {
                    builder.MoveTo(at);
                }
                else
                {
                    builder.LineTo(at);
                }
            }

            builder.Close();

            using var path = builder.Detach();

            canvas.DrawPath(path, Paint(s_red));
        });
    }

    /// <summary>PaintCode saves a whole oval with no sweep at all, which read as an arc drew nothing.</summary>
    [Fact]
    public void A_Whole_Oval_Draws_All_Of_Itself()
    {
        var oval = Filled(PaintCodeShapeKind.Oval, Box(3, 9, 24, 12), new PaintCodeShapeMetrics(0, true, true, true, true, 0, 0, true, 0, 0));

        Compares(Written(oval), canvas => canvas.DrawOval(SKRect.Create(3, 9, 24, 12), Paint(s_red)));
    }

    /// <summary>A group places its children at its anchor, however deep; dropping it moved 322 of 375 groups.</summary>
    [Fact]
    public void A_Nested_Group_Keeps_The_Anchor_Of_Every_Group_Above_It()
    {
        var square = Filled(PaintCodeShapeKind.Rectangle, Box(5, 5, 10, 10), PaintCodeShapeMetrics.Default);
        var inner = Group("Inner", new PaintCodePoint(3, -4), square);
        var outer = Group("Outer", new PaintCodePoint(2, -1), inner);

        Compares(Written(outer), canvas => canvas.DrawRect(SKRect.Create(10, 10, 10, 10), Paint(s_red)));
    }

    /// <summary>
    /// A symbol's canvas fills the box it was placed in, from the box's corner and not from the
    /// anchor, which an instance dragged by its anchor carries away from the corner.
    /// </summary>
    [Fact]
    public void A_Symbol_Is_Drawn_From_Its_Box_Corner_Rather_Than_Its_Anchor()
    {
        // Anchor (20, 25), box corner 5 back from it each way, a 15-unit box for a 30-unit canvas.
        Compares(Imported("moved"), canvas => canvas.DrawRect(SKRect.Create(15, 20, 10, 10), Paint(Accent)));
    }

    /// <summary>
    /// What each instance hands its symbol is what the symbol draws in, through a second symbol
    /// and when what is handed is a constant's bytes rather than a parameter.
    /// </summary>
    [Fact]
    public void A_Symbol_Draws_In_The_Colour_Handed_Down_To_It()
    {
        Compares(Imported("chain"), canvas =>
        {
            // Direct: glyph at half size; Outer and Pinned: wrapper at half size, glyph at half again.
            canvas.DrawRect(SKRect.Create(0, 15, 10, 10), Paint(Accent));
            canvas.DrawRect(SKRect.Create(10, 22.5f, 5, 5), Paint(new SKColor(95, 0, 201)));
            canvas.DrawRect(SKRect.Create(20, 22.5f, 5, 5), Paint(new SKColor(0, 0, 255)));
        });
    }

    private static SKColor Accent => new(251, 62, 114);

    private static void Compares(string svg, Action<SKCanvas> expected)
    {
        using var loaded = new SKSvg();

        loaded.FromSvg(svg);

        var picture = loaded.SetExpressionValues(new Dictionary<string, ExprValue>()) ?? loaded.Picture;

        Assert.NotNull(picture);

        using var actual = PaintCodeSliceTests.Raster(picture!, Size, Size);
        using var drawn = Drawn(expected);

        var difference = ImageHelper.CompareImages(actual, drawn);

        Assert.True(
            difference <= Threshold,
            $"{difference.ToString("F5", CultureInfo.InvariantCulture)} from what PaintCode draws, allowed {Threshold.ToString("F3", CultureInfo.InvariantCulture)}.\n{svg}");
    }

    private static Image<Rgba32> Drawn(Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(Size, Size);

        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(Scale);
            draw(canvas);
        }

        var pixels = bitmap.Pixels;
        var image = new Image<Rgba32>(Size, Size);

        for (var i = 0; i < pixels.Length; i++)
        {
            image[i % Size, i / Size] = new Rgba32(pixels[i].Red, pixels[i].Green, pixels[i].Blue, pixels[i].Alpha);
        }

        return image;
    }

    private static SKPaint Paint(SKColor colour) => new() { Color = colour, IsAntialias = true, Style = SKPaintStyle.Fill };

    /// <summary>One canvas holding <paramref name="item"/>, written the way the import writes it.</summary>
    private static string Written(PaintCodeItem item)
    {
        var scope = PaintCodeDocument.Parse(ScopeDocument.Bytes());
        var canvas = new PaintCodeCanvas("canvas", "canvas", new PaintCodeRect(0, 0, 30, 30), true, false, Group("Canvas", default, item));

        return PaintCodeSvgWriter.Write(canvas, PaintCodeDeclarations.Of(scope), PaintCodeSymbols.Of(scope), new List<PaintCodeImportNote>()).ToString();
    }

    /// <summary>One canvas of <see cref="SymbolDocument.Painted"/>, through the whole import.</summary>
    private static string Imported(string canvas)
    {
        var directory = Directory.CreateTempSubdirectory("paintcode-fault");

        try
        {
            var result = PaintCodeImport.Run(PaintCodeDocument.Parse(SymbolDocument.Painted()), new PaintCodeImportOptions(directory.FullName));

            Assert.DoesNotContain(result.Notes, note => note.Severity is PaintCodeImportSeverity.Missing);

            return File.ReadAllText(result.Files.Single(file => Path.GetFileNameWithoutExtension(file) == canvas));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>A box with its top left at (<paramref name="left"/>, <paramref name="top"/>), in PaintCode's flipped frame.</summary>
    private static PaintCodeFrame Box(double left, double top, double width, double height)
        => new(left, -(top + height), width, height, default, 0, 1, 1, 1, false, true);

    private static PaintCodeGroup Group(string name, PaintCodePoint anchor, params PaintCodeItem[] children)
        => new(name, new PaintCodeFrame(0, 0, 0, 0, anchor, 0, 1, 1, 1, false, true), new Dictionary<string, PaintCodeBinding>(), children, null);

    private static PaintCodeShape Filled(PaintCodeShapeKind kind, PaintCodeFrame frame, PaintCodeShapeMetrics metrics)
        => new(
            kind.ToString(),
            kind,
            frame,
            new Dictionary<string, PaintCodeBinding>(),
            null,
            new PaintCodePaint(PaintCodePaintKind.Color, new PaintCodeColor(string.Empty, s_red.Red, s_red.Green, s_red.Blue, 1), null),
            PaintCodePaint.None,
            PaintCodeStroke.None,
            false,
            null,
            metrics);
}
