using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using SkiaSharp;
using Svg.CodeGen.Skia;
using Svg.Expressions;
using Xunit;

namespace Svg.Skia.UnitTests;

public partial class SkiaCSharpRenderTests
{
    // Every kind of gradient given its stops by e:stop-colors and e:stop-offsets: one sharing a
    // gradient's lists through href and dashed, so that its paint is not the shared one; a focal one
    // and one of no radius, neither given offsets; one under a driven fill-opacity; one in linearRGB;
    // and one of no radius given offsets, which choose its last stop.
    private const string ListGradients = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 60 32" width="60" height="32">
          <defs>
            <e:code>
              <e:param name="colors" type="color[]" default="[#f7de77, #ffffff, #ace3e3]" />
              <e:param name="positions" type="number[]" default="[0, 0.42, 1]" />
              <e:param name="fade" type="number" default="1" />
            </e:code>
            <linearGradient id="l" gradientUnits="userSpaceOnUse" x1="0" y1="2" x2="0" y2="14" e:stop-colors="{{ colors }}" e:stop-offsets="{{ positions }}">
              <stop offset="0" stop-color="#f7de77" /><stop offset="0.42" stop-color="#ffffff" /><stop offset="1" stop-color="#ace3e3" />
            </linearGradient>
            <linearGradient id="h" href="#l" x1="12" y1="0" x2="22" y2="0" />
            <radialGradient id="r" gradientUnits="userSpaceOnUse" cx="30" cy="8" r="6" e:stop-colors="{{ colors }}" e:stop-offsets="{{ positions }}">
              <stop offset="0" stop-color="#f7de77" /><stop offset="0.42" stop-color="#ffffff" /><stop offset="1" stop-color="#ace3e3" />
            </radialGradient>
            <radialGradient id="f" gradientUnits="userSpaceOnUse" cx="42" cy="8" r="6" fx="40" fy="6" e:stop-colors="{{ colors }}">
              <stop offset="0" stop-color="#f7de77" /><stop offset="0.5" stop-color="#ffffff" /><stop offset="1" stop-color="#ace3e3" />
            </radialGradient>
            <sweepGradient id="s" gradientUnits="userSpaceOnUse" cx="6" cy="23" startAngle="30" endAngle="300" e:stop-colors="{{ colors }}" e:stop-offsets="{{ positions }}">
              <stop offset="0" stop-color="#f7de77" /><stop offset="0.42" stop-color="#ffffff" /><stop offset="1" stop-color="#ace3e3" />
            </sweepGradient>
            <radialGradient id="z" gradientUnits="userSpaceOnUse" cx="18" cy="23" r="0" e:stop-colors="{{ colors }}">
              <stop offset="0" stop-color="#f7de77" /><stop offset="0.5" stop-color="#ffffff" /><stop offset="1" stop-color="#ace3e3" />
            </radialGradient>
            <linearGradient id="o" gradientUnits="userSpaceOnUse" x1="24" y1="0" x2="34" y2="0" e:stop-colors="{{ colors }}" e:stop-offsets="{{ positions }}">
              <stop offset="0" stop-color="#f7de77" /><stop offset="0.42" stop-color="#ffffff" /><stop offset="1" stop-color="#ace3e3" />
            </linearGradient>
            <linearGradient id="k" gradientUnits="userSpaceOnUse" x1="36" y1="0" x2="46" y2="0" color-interpolation="linearRGB" e:stop-colors="{{ colors }}" e:stop-offsets="{{ positions }}">
              <stop offset="0" stop-color="#f7de77" /><stop offset="0.42" stop-color="#ffffff" /><stop offset="1" stop-color="#ace3e3" />
            </linearGradient>
            <radialGradient id="y" gradientUnits="userSpaceOnUse" cx="53" cy="23" r="0" e:stop-colors="{{ colors }}" e:stop-offsets="{{ positions }}">
              <stop offset="0" stop-color="#f7de77" /><stop offset="0.42" stop-color="#ffffff" /><stop offset="1" stop-color="#ace3e3" />
            </radialGradient>
          </defs>
          <rect x="1" y="2" width="10" height="12" fill="url(#l)" />
          <rect x="13" y="3" width="8" height="10" fill="none" stroke="url(#h)" stroke-width="2" stroke-dasharray="3 1" />
          <circle cx="30" cy="8" r="6" fill="url(#r)" />
          <circle cx="42" cy="8" r="6" fill="url(#f)" />
          <circle cx="6" cy="23" r="6" fill="url(#s)" />
          <rect x="13" y="18" width="10" height="10" fill="url(#z)" />
          <rect x="24" y="18" width="10" height="10" fill="url(#o)" fill-opacity="{{ fade }}" />
          <rect x="36" y="18" width="10" height="10" fill="url(#k)" />
          <rect x="48" y="18" width="10" height="10" fill="url(#y)" />
        </svg>
        """;

    private static List<float> Offsets(params float[] offsets) => new(offsets);

    // In the order they are drawn, each against what the one before left kept: the same stops again,
    // fewer and more of them, one and none, the declared offsets, either list the shorter, offsets out
    // of order or of range, translucent colours under a fade, and back.
    private static readonly (string Label, object?[] Arguments)[] s_listedStops =
    {
        ("three", new object?[] { Stops(0xffdc2626, 0xfffacc15, 0xff1e40af), Offsets(0f, 0.3f, 1f), 1f }),
        ("again", new object?[] { Stops(0xffdc2626, 0xfffacc15, 0xff1e40af), Offsets(0f, 0.3f, 1f), 1f }),
        ("two", new object?[] { new[] { new SKColor(0xff0ea5e9), new SKColor(0xff7c3aed) }, new[] { 0.2f, 0.8f }, 0.6f }),
        ("five", new object?[] { Stops(0xffdc2626, 0xfff97316, 0xfffacc15, 0xff16a34a, 0xff1e40af), Offsets(0f, 0.1f, 0.45f, 0.5f, 1f), 1f }),
        ("one", new object?[] { Stops(0xff16a34a), Offsets(0.5f), 1f }),
        ("none", new object?[] { Stops(), Offsets(), 1f }),
        ("declared", new object?[] { Stops(0xffdc2626, 0xfffacc15, 0xff1e40af), null, 1f }),
        ("fewer-offsets", new object?[] { Stops(0xffdc2626, 0xfffacc15, 0xff1e40af, 0xff16a34a), Offsets(0.25f, 0.75f), 1f }),
        ("fewer-colours", new object?[] { Stops(0xffdc2626, 0xff1e40af), Offsets(0f, 0.4f, 0.6f, 1f), 1f }),
        ("unsorted", new object?[] { Stops(0xffdc2626, 0xfff97316, 0xfffacc15, 0xff16a34a, 0xff1e40af, 0xff7c3aed, 0xff0ea5e9), Offsets(0.6f, -0.5f, float.NaN, 2f, 0.3f, float.PositiveInfinity, float.NegativeInfinity), 1f }),
        ("translucent", new object?[] { Stops(0x80dc2626, 0x00facc15, 0xc01e40af), Offsets(0f, 0.5f, 1f), 0.35f }),
        ("back", new object?[] { Stops(0xffdc2626, 0xfffacc15, 0xff1e40af), Offsets(0f, 0.3f, 1f), 1f })
    };

    private static Type Drawing(string name, string svg, SvgBody body, SkiaSharpTarget skiaSharp = SkiaSharpTarget.V4)
        => Compile(Code(Model(svg), name, body, SvgExpressionDeclarations.Parse(svg), skiaSharp, bake: false), name).GetType($"{Namespace}.{name}")!;

    [Theory]
    [InlineData(SkiaSharpTarget.V4)]
    [InlineData(SkiaSharpTarget.V3)]
    public void Gradients_Given_Lists_Draw_As_The_Runtime_Binds_Them(SkiaSharpTarget skiaSharp)
    {
        var model = Model(ListGradients);
        var declarations = SvgExpressionDeclarations.Parse(ListGradients);

        // The direct body's Record draws through its Draw, and so through what each site kept.
        var recorded = Drawing($"ListGradients{skiaSharp}", ListGradients, SvgBody.Record, skiaSharp).GetMethod("Record")!;
        var direct = Drawing($"ListGradientsDirect{skiaSharp}", ListGradients, SvgBody.Direct, skiaSharp).GetMethod("Record")!;
        using var unbound = new SkiaModel(new SKSvgSettings()).ToSKPicture(model)!;

        foreach (var (label, arguments) in s_listedStops)
        {
            using var runtime = Bound(model, declarations, arguments);
            using var record = (SKPicture)recorded.Invoke(null, arguments)!;
            using var drawn = (SKPicture)direct.Invoke(null, arguments)!;

            // Each row's colours differ from the written stops, so a binding that never happened shows.
            Assert.True(Difference(unbound, runtime) > 0, $"ListGradients-{skiaSharp}-{label} drew the stops as written.");
            AssertSamePicture($"ListGradients-{skiaSharp}-{label}", runtime, record);
            AssertSamePicture($"ListGradients-{skiaSharp}-{label}-direct", runtime, drawn);
        }
    }

    [Fact]
    public void A_Gradient_Given_Lists_Draws_Its_Own_Stops_Until_They_Are_Bound()
    {
        var written = Regex.Replace(ListGradients, @" e:stop-(colors|offsets)=""[^""]*""", "");
        using var expected = new SkiaModel(new SKSvgSettings()).ToSKPicture(Model(written))!;
        using var unbound = new SkiaModel(new SKSvgSettings()).ToSKPicture(Model(ListGradients))!;

        AssertSamePicture("ListGradients-unbound", expected, unbound);

        // The declared lists are the stops as written, so a drawing left to them draws those as well.
        var record = Drawing("ListGradientsDeclared", ListGradients, SvgBody.Record).GetMethod("Record")!;
        using var declared = (SKPicture)record.Invoke(null, new object?[] { null, null, 1f })!;

        AssertSamePicture("ListGradients-declared", expected, declared);
    }

    // b's own stops are what it draws, bound or not: lists stand in for a's stops, which b's shadow.
    private const string ShadowedLists = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code><e:param name="colors" type="color[]" default="[#f7de77, #ffffff]" /></e:code>
            <linearGradient id="a" e:stop-colors="{{ colors }}">
              <stop offset="0" stop-color="#f00" /><stop offset="1" stop-color="#00f" />
            </linearGradient>
            <linearGradient id="b" href="#a">
              <stop offset="0" stop-color="#0f0" /><stop offset="1" stop-color="#0f0" />
            </linearGradient>
          </defs>
          <rect width="24" height="24" fill="url(#b)" />
        </svg>
        """;

    [Fact]
    public void A_Gradients_Own_Stops_Are_Not_Replaced_By_Lists_It_Inherits()
    {
        var model = Model(ShadowedLists);
        var arguments = new object?[] { Stops(0xffff0000, 0xff0000ff) };
        using var unbound = new SkiaModel(new SKSvgSettings()).ToSKPicture(model)!;
        using var runtime = Bound(model, SvgExpressionDeclarations.Parse(ShadowedLists), arguments);
        using var record = (SKPicture)Drawing("ShadowedLists", ShadowedLists, SvgBody.Record).GetMethod("Record")!.Invoke(null, arguments)!;

        AssertSamePicture("ShadowedLists-bound", unbound, runtime);
        AssertSamePicture("ShadowedLists-generated", unbound, record);
    }

    // The stops of the brief's own example, given as lists the host owns.
    private const string KeptGradient = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 32" width="24" height="32">
          <defs>
            <e:code>
              <e:param name="colors" type="color[]" />
              <e:param name="positions" type="number[]" />
              <e:param name="fade" type="number" default="1" />
            </e:code>
            <linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" y1="2" x2="0" y2="28" e:stop-colors="{{ colors }}" e:stop-offsets="{{ positions }}">
              <stop offset="0" stop-color="#F7DE77" /><stop offset="0.42" stop-color="#FFF" /><stop offset="1" stop-color="#ACE3E3" />
            </linearGradient>
          </defs>
          <rect x="2" y="2" width="20" height="26" fill="url(#g)" fill-opacity="{{ fade }}" />
        </svg>
        """;

    [Fact]
    public void A_Listed_Gradient_Is_Built_Again_Only_When_Its_Stops_Change()
    {
        var type = Drawing("KeptStops", KeptGradient, SvgBody.Direct);
        var draw = type.GetMethod("Draw")!.CreateDelegate<Action<SKCanvas, IReadOnlyList<SKColor>, IReadOnlyList<float>, float>>();
        var kept = Assert.Single(type.GetFields(BindingFlags.NonPublic | BindingFlags.Static), field => field.Name.StartsWith("s_skGradient", StringComparison.Ordinal));

        using var bitmap = new SKBitmap(24, 32);
        using var canvas = new SKCanvas(bitmap);

        object Drawn(IReadOnlyList<SKColor> colors, IReadOnlyList<float> positions, float fade = 1f)
        {
            draw(canvas, colors, positions, fade);
            return kept.GetValue(null)!;
        }

        var first = Drawn(Stops(0xfff7de77, 0xffffffff, 0xfface3e3), Offsets(0f, 0.42f, 1f));

        // Equal stops in other lists, and in arrays, are the same stops.
        Assert.Same(first, Drawn(new[] { new SKColor(0xfff7de77), new SKColor(0xffffffff), new SKColor(0xfface3e3) }, new[] { 0f, 0.42f, 1f }));

        var moved = Drawn(Stops(0xfff7de77, 0xffffffff, 0xfface3e3), Offsets(0f, 0.5f, 1f));
        Assert.NotSame(first, moved);

        var recoloured = Drawn(Stops(0xfff7de77, 0xffdc2626, 0xfface3e3), Offsets(0f, 0.5f, 1f));
        Assert.NotSame(moved, recoloured);

        // Clamped, an offset of 2 is the 1 already kept.
        Assert.Same(recoloured, Drawn(Stops(0xfff7de77, 0xffdc2626, 0xfface3e3), Offsets(0f, 0.5f, 2f)));

        var counted = Drawn(Stops(0xfff7de77, 0xffdc2626, 0xfface3e3, 0xff1e40af), Offsets(0f, 0.5f, 0.7f, 1f));
        Assert.NotSame(recoloured, counted);

        var faded = Drawn(Stops(0xfff7de77, 0xffdc2626, 0xfface3e3, 0xff1e40af), Offsets(0f, 0.5f, 0.7f, 1f), 0.5f);
        Assert.NotSame(counted, faded);
        Assert.Same(faded, Drawn(Stops(0xfff7de77, 0xffdc2626, 0xfface3e3, 0xff1e40af), Offsets(0f, 0.5f, 0.7f, 1f), 0.5f));
    }

    // A radial gradient of no radius is the colour of its last stop, which the shared paint keeps as a
    // gradient's stops are kept.
    private const string KeptPoint = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 32" width="24" height="32">
          <defs>
            <e:code>
              <e:param name="colors" type="color[]" />
              <e:param name="positions" type="number[]" />
              <e:param name="fade" type="number" default="1" />
            </e:code>
            <radialGradient id="g" gradientUnits="userSpaceOnUse" cx="12" cy="15" r="0" e:stop-colors="{{ colors }}" e:stop-offsets="{{ positions }}">
              <stop offset="0" stop-color="#F7DE77" /><stop offset="1" stop-color="#ACE3E3" />
            </radialGradient>
          </defs>
          <rect x="2" y="2" width="20" height="26" fill="url(#g)" fill-opacity="{{ fade }}" />
        </svg>
        """;

    [Fact]
    public void A_Listed_Radial_Gradient_Of_No_Radius_Is_Built_Again_Only_When_Its_Colour_Changes()
    {
        var type = Drawing("KeptPoint", KeptPoint, SvgBody.Direct);
        var draw = type.GetMethod("Draw")!.CreateDelegate<Action<SKCanvas, IReadOnlyList<SKColor>, IReadOnlyList<float>, float>>();
        var kept = Assert.Single(type.GetFields(BindingFlags.NonPublic | BindingFlags.Static), field => field.Name.StartsWith("s_skGradient", StringComparison.Ordinal));

        using var bitmap = new SKBitmap(24, 32);
        using var canvas = new SKCanvas(bitmap);

        object Drawn(IReadOnlyList<SKColor> colors, IReadOnlyList<float> positions, float fade = 1f)
        {
            draw(canvas, colors, positions, fade);
            return kept.GetValue(null)!;
        }

        var first = Drawn(Stops(0xfff7de77, 0xffffffff, 0xfface3e3), Offsets(0f, 0.42f, 1f));
        Assert.Same(first, Drawn(new[] { new SKColor(0xfff7de77), new SKColor(0xffffffff), new SKColor(0xfface3e3) }, new[] { 0f, 0.42f, 1f }));

        // Only the last stop's colour is drawn, so another before it changes nothing.
        Assert.Same(first, Drawn(Stops(0xffdc2626, 0xffffffff, 0xfface3e3), Offsets(0f, 0.42f, 1f)));

        var recoloured = Drawn(Stops(0xffdc2626, 0xffffffff, 0xff1e40af), Offsets(0f, 0.42f, 1f));
        Assert.NotSame(first, recoloured);

        // Two offsets make the second colour the last stop.
        var shortened = Drawn(Stops(0xffdc2626, 0xffffffff, 0xff1e40af), Offsets(0f, 1f));
        Assert.NotSame(recoloured, shortened);

        var faded = Drawn(Stops(0xffdc2626, 0xffffffff, 0xff1e40af), Offsets(0f, 1f), 0.5f);
        Assert.NotSame(shortened, faded);
        Assert.Same(faded, Drawn(Stops(0xffdc2626, 0xffffffff, 0xff1e40af), Offsets(0f, 1f), 0.5f));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_Direct_Draw_Of_A_Listed_Gradient_With_The_Same_Stops_Allocates_Nothing(bool noRadius)
    {
        using var bitmap = new SKBitmap(24, 32);
        using var canvas = new SKCanvas(bitmap);
        var colors = Stops(0xffdc2626, 0xfffacc15, 0xff1e40af);
        var positions = Offsets(0f, 0.3f, 1f);
        var colorArray = colors.ToArray();
        var positionArray = positions.ToArray();

        var draw = Warmed<Action<SKCanvas, IReadOnlyList<SKColor>, IReadOnlyList<float>, float>>(
            noRadius ? "UnallocatedPoint" : "UnallocatedStops", noRadius ? KeptPoint : KeptGradient, warm => warm(canvas, colors, positions, 0.8f));

        var before = GC.GetAllocatedBytesForCurrentThread();
        draw(canvas, colors, positions, 0.8f);
        draw(canvas, colorArray, positionArray, 0.8f);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData(SvgHelperScope.PerClass)]
    [InlineData(SvgHelperScope.FileLocal)]
    [InlineData(SvgHelperScope.Internal)]
    public void A_Helper_Brings_Along_The_Helpers_It_Calls(SvgHelperScope scope)
    {
        var code = SkiaCSharpCodeGen.GenerateFile(
            new[] { new SkiaCSharpDrawing(Model(KeptGradient), Namespace, $"Helped{scope}", SvgExpressionDeclarations.Parse(KeptGradient)) },
            scope,
            style: new SvgCodeStyle { Body = SvgBody.Direct });

        // The drawing calls SvgStops and SvgStop, and only they call these.
        Assert.Contains("SKColorF SvgToColorF(SKColor color)", code);
        Assert.Contains("SKColor SvgScaleAlpha(SKColor color, float factor)", code);
        Assert.Contains("SKColor SvgToLinearRgb(SKColor color)", code);
        Assert.Empty(Warnings(code));
    }

    [Theory]
    [InlineData(SvgBody.Record)]
    [InlineData(SvgBody.Direct)]
    public void Generated_List_Gradient_Code_Compiles_Without_A_Warning(SvgBody body)
        => Assert.Empty(Warnings(Code(Model(ListGradients), $"ListGradientsWarnings{body}", body, SvgExpressionDeclarations.Parse(ListGradients))));
}
