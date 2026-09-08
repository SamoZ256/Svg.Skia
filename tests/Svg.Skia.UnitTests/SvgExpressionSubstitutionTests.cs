// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using Svg.Expressions;
using Svg.Model.Services;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// The kind of expression a drawing consumes while it is being built: text, and the properties the
/// text is measured with.
/// </summary>
/// <remarks>
/// The assertions here are about ink on a bitmap rather than about the model, and deliberately so.
/// The whole reason this kind exists is that its value changes where the glyphs land, and a model
/// comparison would pass just as well against an implementation that swapped the string and kept the
/// old positions -- which is the bug this feature had to avoid.
/// </remarks>
public class SvgExpressionSubstitutionTests
{
    private const string Ns = "https://svg.skia/expr/1.0";

    private static SKSvg Load(string markup)
    {
        var svg = new SKSvg();
        Assert.NotNull(svg.FromSvg(markup));

        return svg;
    }

    private static Dictionary<string, ExprValue> Values(params (string Name, ExprValue Value)[] values)
        => values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

    private static string Document(string body, string declarations)
        => $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" viewBox="0 0 200 60" width="200" height="60">
              <defs><e:code>{declarations}</e:code></defs>
              {body}
            </svg>
            """;

    /// <summary>Where the drawing put ink, in device pixels, or null for none.</summary>
    private static SKRectI? Ink(SKSvg svg)
    {
        using var bitmap = new SKBitmap(200, 60);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);

            if (svg.Picture is { } picture)
            {
                canvas.DrawPicture(picture);
            }
        }

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);

                // Anything that is not the white it was cleared to, so coloured ink counts too.
                if (pixel.Red > 200 && pixel.Green > 200 && pixel.Blue > 200)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return left == int.MaxValue ? null : new SKRectI(left, top, right, bottom);
    }

    [Fact]
    public void Text_Content_Renders_Its_Declared_Default_On_A_Plain_Load()
    {
        // No values bound, and no host involved: opening the file has to draw what the author
        // described, or a document would look empty in every viewer that does not know about this.
        using var described = Load(Document(
            """<text x="10" y="40" font-size="24" fill="#000000">{{ label }}</text>""",
            """<e:param name="label" type="string" default="'Hello'" />"""));

        using var literal = Load(Document(
            """<text x="10" y="40" font-size="24" fill="#000000">Hello</text>""",
            ""));

        Assert.NotNull(Ink(described));
        Assert.Equal(Ink(literal), Ink(described));
    }

    [Fact]
    public void Binding_A_Value_Re_Measures_The_Text()
    {
        // The point of the whole feature. text-anchor="middle" centres the run on the anchor, and
        // the offset is computed while the drawing is compiled. An implementation that swapped the
        // string into the recorded drawing would leave the ink starting where the old string
        // started; only a recompile puts a longer word evenly either side of x=100.
        using var svg = Load(Document(
            """<text x="100" y="40" font-size="24" text-anchor="middle" fill="#000000">{{ label }}</text>""",
            """<e:param name="label" type="string" default="'ii'" />"""));

        var narrow = Ink(svg);
        Assert.NotNull(narrow);

        svg.SetExpressionValues(Values(("label", ExprValue.String("mmmmmm"))));

        var wide = Ink(svg);
        Assert.NotNull(wide);

        // Wider, and still centred on the same anchor.
        Assert.True(wide!.Value.Width > narrow!.Value.Width, $"{wide} is not wider than {narrow}");
        Assert.True(wide.Value.Left < narrow!.Value.Left, "the longer word did not grow to the left");
        Assert.True(wide.Value.Right > narrow.Value.Right, "the longer word did not grow to the right");
    }

    [Fact]
    public void Font_Size_Reaches_The_Layout()
    {
        using var svg = Load(Document(
            """<text x="10" y="50" font-size="{{ size }}" fill="#000000">Hello</text>""",
            """<e:param name="size" type="number" default="12" />"""));

        var small = Ink(svg);
        Assert.NotNull(small);

        svg.SetExpressionValues(Values(("size", ExprValue.Number(36f))));

        var large = Ink(svg);
        Assert.NotNull(large);

        Assert.True(large!.Value.Height > small!.Value.Height, $"{large} is not taller than {small}");
    }

    [Fact]
    public void An_Expression_That_Will_Not_Evaluate_Still_Opens()
    {
        // Loading is documented never to fail on a declaration block, and this runs on the load
        // path. The element draws as though the attribute had not been written.
        using var svg = Load(Document(
            """<text x="10" y="40" font-size="24" fill="#000000">{{ nope }}</text>""",
            """<e:param name="label" type="string" default="'Hello'" />"""));

        Assert.Null(Ink(svg));
    }

    [Fact]
    public void The_Document_Is_Given_Back_As_It_Was()
    {
        // The document is the one the host keeps -- what GetXML writes and the next compile reads --
        // so a scope that left its values behind would put one binding's text into a saved file.
        using var svg = Load(Document(
            """<text x="10" y="40" font-size="24" font-family="{{ face }}" fill="#000000">{{ label }}</text>""",
            """
            <e:param name="label" type="string" default="'Hello'" />
            <e:param name="face" type="string" default="'serif'" />
            """));

        svg.SetExpressionValues(Values(
            ("label", ExprValue.String("Goodbye")),
            ("face", ExprValue.String("monospace"))));

        var text = svg.SourceDocument!.Descendants().OfType<SvgText>().Single();

        Assert.Equal(string.Empty, text.Content);
        Assert.Null(text.FontFamily);
        Assert.Equal("label", SvgExpressionAttributes.Lifted(text.CustomAttributes, SvgExpressionAttributes.ContentName));
        Assert.Equal("face", SvgExpressionAttributes.Lifted(text.CustomAttributes, "font-family"));
    }

    [Fact]
    public void The_Declarations_Are_Not_Disturbed_By_A_Binding()
    {
        // They are read off the live DOM on every access, so writing into the block would change the
        // declarations under the compile that is reading them.
        using var svg = Load(Document(
            """<text x="10" y="40" font-size="24" fill="#000000">{{ label }}</text>""",
            """<e:param name="label" type="string" default="'Hello'" />"""));

        svg.SetExpressionValues(Values(("label", ExprValue.String("Goodbye"))));

        var parameter = Assert.Single(svg.ExpressionDeclarations.Parameters);

        Assert.Equal("label", parameter.Name);
        Assert.Equal(ExprType.String, parameter.Type);
        Assert.Equal("'Hello'", parameter.DefaultExpression);
    }

    [Fact]
    public void A_Drawing_Whose_Text_Is_An_Expression_Cannot_Be_Generated()
    {
        // A generated picture is recorded at build time with the text already measured, so a
        // parameter driving it could never vary. Refusing says so where it can still be acted on;
        // generating would hand back a signature offering something it cannot do.
        var document = SvgService.FromSvg(Document(
            """<text x="10" y="40" font-size="24" fill="#000000">{{ label }}</text>""",
            """<e:param name="label" type="string" default="'Hello'" />"""));

        var refusal = SvgExpressionSubstitution.WhyNotGeneratable(document);

        Assert.NotNull(refusal);
        Assert.Contains("the text of <text>", refusal);
        Assert.Contains("SetExpressionValues", refusal);
    }

    [Fact]
    public void A_Drawing_Whose_Font_Is_An_Expression_Cannot_Be_Generated()
    {
        var document = SvgService.FromSvg(Document(
            """<text x="10" y="40" font-family="{{ face }}" fill="#000000">Hello</text>""",
            """<e:param name="face" type="string" default="'serif'" />"""));

        Assert.Contains("'font-family' on <text>", SvgExpressionSubstitution.WhyNotGeneratable(document));
    }

    [Fact]
    public void A_Drawing_Using_Only_The_Recorded_Kind_Still_Generates()
    {
        // The refusal has to be about this kind alone, or it would take the feature that does
        // generate down with it.
        var document = SvgService.FromSvg(Document(
            """<rect width="24" height="24" fill="{{ ink }}" />""",
            """<e:param name="ink" type="color" default="#000000" />"""));

        Assert.Null(SvgExpressionSubstitution.WhyNotGeneratable(document));
    }

    [Fact]
    public void Both_Kinds_Of_Expression_Work_In_One_Drawing()
    {
        // A recompile throws the recorded model away, so the recorded kind has to be re-applied on
        // the way out or a document mixing them would lose its colours the moment its text changed.
        using var svg = Load(Document(
            """<text x="10" y="40" font-size="24" fill="{{ ink }}">{{ label }}</text>""",
            """
            <e:param name="label" type="string" default="'Hello'" />
            <e:param name="ink" type="color" default="#000000" />
            """));

        svg.SetExpressionValues(Values(
            ("label", ExprValue.String("Goodbye")),
            ("ink", ExprValue.Color(255, 0, 0, 255))));

        using var bitmap = new SKBitmap(200, 60);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawPicture(svg.Picture!);
        }

        var ink = Ink(svg);
        Assert.NotNull(ink);

        var pixel = bitmap.GetPixel(ink!.Value.MidX, ink.Value.MidY);

        Assert.True(pixel.Red > pixel.Green && pixel.Red > pixel.Blue, $"{pixel} is not red");
    }
}
