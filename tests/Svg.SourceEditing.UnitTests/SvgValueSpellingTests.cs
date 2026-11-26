using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Svg;
using Svg.Pathing;
using Svg.Transforms;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// A value the parser holds, compared exactly and written back as text that reads back as it: what a
/// command on the parsed drawing writes into the file.
/// </summary>
public class SvgValueSpellingTests
{
    private static SvgDocument Load(string body)
        => SvgDocumentCompatibilityLoader.FromSvg<SvgDocument>($"""<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">{body}</svg>""");

    private static SvgElement Element(string body, string id = "a") => Load(body).GetElementById(id);

    private static string? Spelt(SvgElement element, string name, object? value, string? authored = null)
        => SvgValueSpelling.Spell(element, name, value, authored, out _);

    private static string? Spelt(string body, string name, string? authored = null)
    {
        var element = Element(body);

        return Spelt(element, name, SvgElementState.Of(element, string.Empty).Attributes[name], authored);
    }

    [Fact]
    public void A_Float_Takes_The_Fewest_Digits_That_Read_Back()
    {
        Assert.Equal("0.12345679", SvgValueSpelling.Number(0.12345679f));
        Assert.Equal("0.1", SvgValueSpelling.Number(0.1f));
        Assert.Equal("16777216", SvgValueSpelling.Number(16777216f));
        Assert.Equal("1e-45", SvgValueSpelling.Number(float.Epsilon));

        // The same significant digits as the shortest round trip a modern runtime gives, and the same float back.
        static string Digits(string written)
        {
            var mantissa = written.TrimStart('-').Split('e', 'E')[0].Replace(".", string.Empty).TrimStart('0').TrimEnd('0');

            return mantissa.Length == 0 ? "0" : mantissa;
        }

        var random = new Random(7);

        for (var i = 0; i < 100_000; i++)
        {
            var value = BitConverter.ToSingle(BitConverter.GetBytes(random.Next(int.MinValue, int.MaxValue)), 0);

            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                continue;
            }

            var written = SvgValueSpelling.Number(value)!;

            Assert.Equal(value, float.Parse(written, NumberStyles.Float, CultureInfo.InvariantCulture));
            Assert.Equal(Digits(value.ToString("R", CultureInfo.InvariantCulture)), Digits(written));
        }
    }

    [Fact]
    public void A_Float_Is_Positional_Where_JavaScript_Writes_It_So()
    {
        Assert.Equal("0.000001", SvgValueSpelling.Number(1e-6f));
        Assert.Equal("1e-7", SvgValueSpelling.Number(1e-7f));
        Assert.Equal("3.4028235e+38", SvgValueSpelling.Number(float.MaxValue));
        Assert.Equal("123456790", SvgValueSpelling.Number(123456789f));
        Assert.Equal("-2.5", SvgValueSpelling.Number(-2.5f));
        Assert.Equal("0", SvgValueSpelling.Number(-0f));
        Assert.Null(SvgValueSpelling.Number(float.NaN));
        Assert.Null(SvgValueSpelling.Number(float.PositiveInfinity));
    }

    [Theory]
    [InlineData("2ex")]
    [InlineData("3pc")]
    [InlineData("1.5em")]
    [InlineData("25%")]
    [InlineData("12pt")]
    [InlineData("4mm")]
    public void A_Unit_Keeps_Its_Unit_Ex_And_Pc_Included(string width)
        => Assert.Equal(width, Spelt($"""<rect id="a" width="{width}" height="1" />""", "width"));

    [Fact]
    public void A_Colour_With_Alpha_Keeps_It_In_The_Hex()
    {
        var rect = Element("""<rect id="a" fill="rgba(220,40,70,0.5)" fill-opacity="0.25" width="1" height="1" />""");
        var state = SvgElementState.Of(rect, string.Empty);

        Assert.Equal("#dc284680", Spelt(rect, "fill", state.Attributes["fill"]));
        Assert.Equal("0.25", Spelt(rect, "fill-opacity", state.Attributes["fill-opacity"]));

        // Written as the author wrote it while it still says the same.
        Assert.Equal("red", Spelt("""<rect id="a" fill="red" />""", "fill", "red"));
        Assert.Equal("hsl(0, 100%, 50%)", Spelt("""<rect id="a" fill="hsl(0, 100%, 50%)" />""", "fill", "hsl(0, 100%, 50%)"));
        Assert.Equal("#ff0000", Spelt("""<rect id="a" fill="red" />""", "fill"));
    }

    [Fact]
    public void A_Fallback_Is_Part_Of_The_Paint()
    {
        var red = SvgElementState.Of(Element("""<rect id="a" fill="url(#g) red" />"""), string.Empty).Attributes["fill"];
        var blue = SvgElementState.Of(Element("""<rect id="a" fill="url(#g) blue" />"""), string.Empty).Attributes["fill"];

        Assert.False(SvgValueSpelling.Precise(red, blue));
        Assert.Equal("url(#g) #ff0000", Spelt(Element("""<rect id="a" />"""), "fill", red));
        Assert.Equal("none", Spelt("""<rect id="a" fill="none" />""", "fill"));
        Assert.Equal("currentColor", Spelt("""<rect id="a" fill="currentColor" />""", "fill"));
        Assert.Equal("context-stroke", Spelt("""<path id="a" fill="context-stroke" />""", "fill"));
        Assert.Null(Spelt(Element("""<rect id="a" />"""), "fill", SvgPaintServer.NotSet));
    }

    [Theory]
    [InlineData("M10 20h5v-5H0V0Z", "M 10 20 h 5 v -5 H 0 V 0 Z")]
    [InlineData("m1 1 2 2", "m 1 1 l 2 2")]
    [InlineData("M0 0S1 1 2 2T3 3", "M 0 0 S 1 1 2 2 T 3 3")]
    [InlineData("M0 0C1 2 3 4 5 6Q7 8 9 10z", "M 0 0 C 1 2 3 4 5 6 Q 7 8 9 10 z")]
    [InlineData("M0 0a1,1 0 01 2 2", "M 0 0 a 1 1 0 0 1 2 2")]
    [InlineData("M0 0A5 6 30 1 0 .5 .25", "M 0 0 A 5 6 30 1 0 0.5 0.25")]
    public void A_Path_Keeps_Every_Command_And_Its_Relativity(string d, string spelt)
    {
        Assert.Equal(spelt, Spelt($"""<path id="a" d="{d}" />""", "d"));

        // The author's own compact spelling reads back, so it is what stays.
        Assert.Equal(d, Spelt($"""<path id="a" d="{d}" />""", "d", d));
    }

    [Fact]
    public void A_Bearing_Path_Reads_Back_As_What_It_Drew()
        => Assert.NotNull(Spelt("""<path id="a" d="M0 0B90 l10 0" />""", "d"));

    [Theory]
    [InlineData("translate(10 0)", "translate(10)")]
    [InlineData("translate(10,20) scale(2 2)", "translate(10 20) scale(2)")]
    [InlineData("rotate(45 0 0) rotate(30, 1, 2)", "rotate(45) rotate(30 1 2)")]
    [InlineData("matrix(1 0 0 1 .5 -3)", "matrix(1 0 0 1 0.5 -3)")]
    [InlineData("skewX(10) skewY(20)", "skewX(10) skewY(20)")]
    public void A_Transform_Takes_The_Fewest_Arguments(string transform, string spelt)
        => Assert.Equal(spelt, Spelt($"""<rect id="a" transform="{transform}" />""", "transform"));

    [Fact]
    public void An_Empty_Transform_Is_Not_Every_Transform()
    {
        Assert.False(SvgValueSpelling.Precise(new SvgTransformCollection(), new SvgTransformCollection { new SvgTranslate(1) }));

        // A skew of both angles has no function to be written as.
        Assert.Null(Spelt(Element("""<rect id="a" />"""), "transform", new SvgTransformCollection { new SvgSkew(10, 20) }));
    }

    [Theory]
    [InlineData("defer xMidYMax slice")]
    [InlineData("xMinYMin")]
    [InlineData("none")]
    public void Defer_And_Slice_Survive(string ratio)
        => Assert.Equal(ratio, Spelt($"""<image id="a" preserveAspectRatio="{ratio}" />""", "preserveAspectRatio"));

    [Fact]
    public void Every_List_Reads_Back_With_Its_Own_Separator()
    {
        Assert.Equal("0;0.5;1", Spelt("""<rect><animate id="a" attributeName="x" keyTimes="0;.5;1" values="0;5;10" dur="1s" /></rect>""", "keyTimes"));
        Assert.Equal("1,2 3,4 5", Spelt("""<polyline id="a" points="1 2 3 4 5" />""", "points"));
        Assert.Equal("0 0 10 20", Spelt("""<svg id="a" viewBox="0,0,10,20" />""", "viewBox"));
        Assert.Equal("1 2.5 3", Spelt("""<rect id="a" stroke-dasharray="1,2.5,3" />""", "stroke-dasharray"));
        Assert.Equal("none", Spelt("""<rect id="a" stroke-dasharray="none" />""", "stroke-dasharray"));
    }

    [Fact]
    public void An_Enum_And_A_Flag_Spell_Through_Their_Converter()
    {
        Assert.Equal("round", Spelt("""<rect id="a" stroke-linecap="round" />""", "stroke-linecap"));
        Assert.Equal("evenodd", Spelt("""<rect id="a" fill-rule="evenodd" />""", "fill-rule"));
        Assert.Equal("true", Spelt("""<filter><feConvolveMatrix id="a" kernelMatrix="1" preserveAlpha="true" /></filter>""", "preserveAlpha"));
        Assert.Equal("3", Spelt("""<filter><feTurbulence id="a" numOctaves="3" /></filter>""", "numOctaves"));
    }

    /// <summary>A converter that reads in the current culture cannot be written for: what it reads back is not the value.</summary>
    [Fact]
    public void An_Orient_Under_A_Comma_Culture_Is_Refused()
    {
        var marker = Element("""<marker id="a" />""");
        var culture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Null(SvgValueSpelling.Spell(marker, "orient", new SvgOrient(45.5f), null, out var refusal));
            Assert.Equal("'orient' on <marker> would not read back as the value it holds.", refusal);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        Assert.Equal("45.5", Spelt(marker, "orient", new SvgOrient(45.5f)));
        Assert.Equal("auto-start-reverse", Spelt(marker, "orient", new SvgOrient(true, true)));
    }

    /// <summary>The author's text whole while it still says the value, and a fresh spelling of the whole once it does not.</summary>
    [Fact]
    public void The_Authored_Text_Stays_Until_The_Value_Changes()
    {
        var rect = Element("""<rect id="a" x="1e2" />""");

        Assert.Equal("1e2", Spelt(rect, "x", new SvgUnit(100f), "1e2"));
        Assert.Equal("25", Spelt(rect, "x", new SvgUnit(25f), "1e2"));

        var path = Element("""<path id="a" d="M 0 0 10 10 20 20" />""");
        var moved = (SvgPathSegmentList)((SvgPathSegmentList)SvgElementState.Of(path, string.Empty).Attributes["d"]!).Clone();

        moved[2].End = new PointF(25, 20);

        Assert.Equal("M 0 0 L 10 10 L 25 20", Spelt(path, "d", moved, "M 0 0 10 10 20 20"));

        Assert.Null(SvgValueSpelling.Spell(rect, "x", new SvgUnit(100f), "{{ w }}", out var refusal));
        Assert.Equal("'x' on <rect> is written with an expression, which only the expression's own rules may rewrite.", refusal);
    }

    /// <summary>The author's text is not kept where the loader reads it otherwise than the converter does.</summary>
    [Fact]
    public void Authored_Text_The_Loader_Reads_Otherwise_Is_Not_Kept()
    {
        var rect = Element("""<rect id="a" />""");

        Assert.Equal("#ff0000", Spelt(rect, "fill", new SvgColourServer(Color.Red), "var(--c, red)"));
        Assert.Equal("2px", Spelt(rect, "stroke-width", new SvgUnit(SvgUnitType.Pixel, 2), "2PX"));
    }

    [Fact]
    public void Precise_Sees_What_Equals_Does_Not()
    {
        Assert.False(SvgValueSpelling.Precise(SvgUnit.Empty, new SvgUnit(0f)));
        Assert.False(SvgValueSpelling.Precise(new SvgOrient(true), new SvgOrient(true, true)));
        Assert.False(SvgValueSpelling.Precise(new SvgAspectRatio(SvgPreserveAspectRatio.xMidYMid), new SvgAspectRatio(SvgPreserveAspectRatio.xMidYMid, true)));
        Assert.True(SvgValueSpelling.Precise(new SvgColourServer(Color.Red), new SvgColourServer(Color.FromArgb(255, 255, 0, 0))));
        Assert.False(SvgValueSpelling.Precise(0.1f, 0.100000001f + 1e-8f));
        Assert.True(SvgValueSpelling.Precise(0f, -0f));
        Assert.False(SvgValueSpelling.Precise(new object(), new object()));
    }

    /// <summary>
    /// Every type an attribute of the parser is held as is one the spelling was written for, so a type
    /// added upstream fails here until it is.
    /// </summary>
    [Fact]
    public void Every_Attribute_Type_Is_Spelled()
    {
        var spelt = new HashSet<Type>
        {
            typeof(string), typeof(float), typeof(int), typeof(bool), typeof(Uri), typeof(SvgUnit), typeof(SvgUnitCollection),
            typeof(SvgNumberCollection), typeof(SvgPointCollection), typeof(SvgViewBox), typeof(SvgAspectRatio), typeof(SvgOrient),
            typeof(SvgTransformCollection), typeof(SvgPathSegmentList), typeof(SvgPaintServer), typeof(SvgDeferredPaintServer)
        };

        var unspelt = typeof(SvgElement).Assembly.GetTypes()
            .Where(type => typeof(SvgElement).IsAssignableFrom(type))
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(property => property.GetCustomAttribute<SvgAttributeAttribute>() is { } && !property.PropertyType.IsEnum)
            .Select(property => property.PropertyType)
            .Where(type => !spelt.Contains(type) && !typeof(Delegate).IsAssignableFrom(type))
            .Distinct()
            .ToList();

        Assert.Empty(unspelt);
    }

    /// <summary>
    /// Every value the parser holds of its own, in every drawing of the suites, spelt and read back as
    /// itself — or refused, never guessed at. The refusals are pinned at what was measured.
    /// </summary>
    [Fact]
    public void Every_Own_Value_In_The_Suites_Reads_Back()
    {
        var (spelt, refused) = (0, new Dictionary<string, int>(StringComparer.Ordinal));

        foreach (var file in SvgSourceDocumentTests.Suites())
        {
            SvgDocument document;

            try
            {
                document = SvgDocumentCompatibilityLoader.FromSvg<SvgDocument>(File.ReadAllText(file));
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var state in SvgElementState.Capture(document))
            {
                foreach (var (name, value) in state.Attributes)
                {
                    if (value is null || ReferenceEquals(value, SvgPaintServer.NotSet))
                    {
                        continue;
                    }

                    if (SvgValueSpelling.Spell(state.Element, name, value, null, out _) is { })
                    {
                        spelt++;
                    }
                    else
                    {
                        var kind = $"{value.GetType().Name} {name}";

                        refused[kind] = refused.TryGetValue(kind, out var count) ? count + 1 : 1;
                    }
                }
            }
        }

        // 167,542 values in 2,938 drawings when measured, and not one refused.
        Assert.True(spelt > 160_000, $"Only {spelt} values were spelt.");
        Assert.True(refused.Count == 0, $"{refused.Values.Sum()} refused: " + string.Join(", ", refused.OrderByDescending(pair => pair.Value).Take(15).Select(pair => $"{pair.Key} ×{pair.Value}")));
    }
}
