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

/// <summary>
/// TapHome's two colour-temperature drawings, which ButtonHelper.cs draws by hand from the stops
/// CctColorCache works out for a light's range, compared with SVGs taking those stops as lists.
/// </summary>
/// <remarks>
/// Each drawing's defaults are its overload that takes no lists, three colours of its own spaced evenly
/// rather than the full range's stops.
/// </remarks>
public partial class SkiaCSharpRenderTests
{
    // Every row measured 0 on Apple Silicon, but the outline is a path here and a circle there, and the
    // sweep rows met x64 rounding of 0.001 on CI. The nearest drawing a little off measures 0.00142.
    private const double CctBound = 0.0005d;

    private const string SymbolCct = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 30 30" width="30" height="30">
          <defs>
            <e:code>
              <e:param name="positions" type="number[]" default="[0, 0.5, 1]" />
              <e:param name="colors" type="color[]" default="[#F7DC74, #FFFFFF, #ACE3E3]" />
              <e:param name="isPicker" type="boolean" default="false" />
            </e:code>
            <linearGradient id="cct" gradientUnits="userSpaceOnUse" x1="0" y1="2" x2="0" y2="28" e:stop-colors="{{ colors }}" e:stop-offsets="{{ positions }}">
              <stop offset="0" stop-color="#F7DC74" />
              <stop offset="0.5" stop-color="#FFFFFF" />
              <stop offset="1" stop-color="#ACE3E3" />
            </linearGradient>
            <radialGradient id="shadow" gradientUnits="userSpaceOnUse" cx="15" cy="15" r="13">
              <stop offset="0.7" stop-color="#000000" stop-opacity="0" />
              <stop offset="1" stop-color="#000000" stop-opacity="0.11764706" />
            </radialGradient>
          </defs>
          <circle cx="15" cy="15" r="13" fill="url(#cct)" />
          <circle cx="15" cy="15" r="13" fill="url(#shadow)" display="{{ isPicker }}" />
        </svg>
        """;

    private const string CctColourPicker = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 30 30" width="30" height="30">
          <defs>
            <e:code>
              <e:param name="positions" type="number[]" default="[0, 0.5, 1]" />
              <e:param name="colors" type="color[]" default="[#F7DD74, #FFFFFF, #ACE2E2]" />
              <e:param name="isLight" type="boolean" default="false" />
            </e:code>
            <linearGradient id="cct" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="0" y2="30" e:stop-colors="{{ colors }}" e:stop-offsets="{{ positions }}">
              <stop offset="0" stop-color="#F7DD74" />
              <stop offset="0.5" stop-color="#FFFFFF" />
              <stop offset="1" stop-color="#ACE2E2" />
            </linearGradient>
            <radialGradient id="shadow" gradientUnits="userSpaceOnUse" cx="15" cy="15" r="15">
              <stop offset="0.6666667" stop-color="#000000" stop-opacity="0" />
              <stop offset="0.8333333" stop-color="#000000" stop-opacity="0.007843138" />
              <stop offset="1" stop-color="#000000" stop-opacity="0.019607844" />
            </radialGradient>
            <radialGradient id="shadow2" gradientUnits="userSpaceOnUse" cx="15" cy="15" r="15">
              <stop offset="0.8" stop-color="#000000" stop-opacity="0" />
              <stop offset="0.96" stop-color="#000000" stop-opacity="0.039215688" />
              <stop offset="1" stop-color="#000000" stop-opacity="0.15686275" />
            </radialGradient>
          </defs>
          <circle cx="15" cy="15" r="15" fill="url(#cct)" />
          <circle cx="15" cy="15" r="15" fill="url(#shadow)" />
          <circle cx="15" cy="15" r="15" fill="url(#shadow2)" />
          <circle cx="15" cy="15" r="14.61" fill="none" stroke="{{ isLight ? #CCCCCC : #FFFFFF }}" stroke-opacity="0.70588235" stroke-width="0.8" />
        </svg>
        """;

    private static readonly SKColor[] s_symbolCctColors = { new(247, 220, 116), new(255, 255, 255), new(172, 227, 227) };

    private static SKColor[] CctPickerColors => new[] { SKColor.FromHsv(48, 53, 97), SKColor.FromHsv(0, 0, 100), SKColor.FromHsv(180, 24, 89) };

    private static readonly SKColor s_cctMin = new(247, 222, 119);
    private static readonly SKColor s_cctMiddle = new(255, 255, 255);
    private static readonly SKColor s_cctMax = new(172, 227, 227);

    // CctColorCache.GetGradientColorsWithPositions, the stops a light's range is drawn with.
    private static (List<float> Positions, List<SKColor> Colors) CctStops(int minValue, int maxValue)
    {
        if (minValue == 1800 && maxValue == 7000)
        {
            return (new List<float> { 0f, 0.42f, 1f }, new List<SKColor> { s_cctMin, s_cctMiddle, s_cctMax });
        }

        var positions = new List<float> { 0f };
        var colors = new List<SKColor> { Kelvin(minValue) };

        if (minValue < 4000 && maxValue > 4000)
        {
            positions.Add((float)(4000 - minValue) / (float)(maxValue - minValue));
            colors.Add(Kelvin(4000));
        }

        positions.Add(1f);
        colors.Add(Kelvin(maxValue));

        return (positions, colors);
    }

    // CctColorCache.GetSKColor for 1800 to 7000 K, whose hundredth of the range is a whole 52 K.
    private static SKColor Kelvin(double colorTemperature)
    {
        var percentage = (colorTemperature - 1800) / 52 / 100;
        var (startPercentage, startColor, endPercentage, endColor) = percentage < 0.42
            ? (0d, s_cctMin, 0.42, s_cctMiddle)
            : (0.42, s_cctMiddle, 1d, s_cctMax);
        var newPercentage = (percentage - startPercentage) / ((endPercentage - startPercentage) / 100) / 100;

        static byte Interpolated(int start, int end, double percentage) => (byte)(start + (int)Math.Round(percentage * (end - start)));

        return new SKColor(
            Interpolated(startColor.Red, endColor.Red, newPercentage),
            Interpolated(startColor.Green, endColor.Green, newPercentage),
            Interpolated(startColor.Blue, endColor.Blue, newPercentage),
            Interpolated(startColor.Alpha, endColor.Alpha, newPercentage));
    }

    // The second stop 0.02 earlier: the middle of three, or the end of two.
    private static List<float> Earlier(IReadOnlyList<float>? positions)
    {
        var moved = new List<float>(positions ?? new[] { 0f, 0.5f, 1f });
        moved[1] -= 0.02f;

        return moved;
    }

    [Theory]
    [InlineData(1800, 7000, 3, false)]
    [InlineData(1800, 7000, 3, true)]
    [InlineData(2700, 6500, 3, false)]
    [InlineData(2700, 6500, 3, true)]
    [InlineData(1800, 3000, 2, false)]
    [InlineData(1800, 3000, 2, true)]
    [InlineData(4500, 6500, 2, false)]
    [InlineData(4500, 6500, 2, true)]
    [InlineData(2000, 4000, 2, false)]
    [InlineData(2000, 4000, 2, true)]
    public void A_Cct_Symbol_Draws_What_ButtonHelper_Draws(int minValue, int maxValue, int stops, bool isPicker)
    {
        var (positions, colors) = CctStops(minValue, maxValue);
        Assert.Equal(stops, positions.Count);

        AssertDrawsAsButtonHelper(
            $"SymbolCct_{minValue}_{maxValue}_{isPicker}", SymbolCct, new object?[] { positions, colors, isPicker }, 30,
            canvas => DrawSymbolCct(canvas, positions, colors, isPicker), CctBound,
            SymbolCct, new object?[] { Earlier(positions), colors, isPicker });
    }

    [Theory]
    [InlineData(1800, 7000, 3, false)]
    [InlineData(1800, 7000, 3, true)]
    [InlineData(2700, 6500, 3, false)]
    [InlineData(2700, 6500, 3, true)]
    [InlineData(1800, 3000, 2, false)]
    [InlineData(1800, 3000, 2, true)]
    [InlineData(4500, 6500, 2, false)]
    [InlineData(4500, 6500, 2, true)]
    [InlineData(2000, 4000, 2, false)]
    [InlineData(2000, 4000, 2, true)]
    public void A_Cct_Colour_Picker_Draws_What_ButtonHelper_Draws(int minValue, int maxValue, int stops, bool isLight)
    {
        var (positions, colors) = CctStops(minValue, maxValue);
        Assert.Equal(stops, positions.Count);

        AssertDrawsAsButtonHelper(
            $"CctColourPicker_{minValue}_{maxValue}_{isLight}", CctColourPicker, new object?[] { positions, colors, isLight }, 30,
            canvas => DrawCctColorPicker(canvas, positions, colors, isLight), CctBound,
            CctColourPicker, new object?[] { Earlier(positions), colors, isLight });
    }

    // The overloads that take no lists are each drawing's defaults, and its written stops too, which
    // are what a browser draws.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_Cct_Symbol_Left_To_Its_Defaults_Draws_What_ButtonHelper_Draws_Without_Lists(bool isPicker)
    {
        var arguments = new object?[] { null, null, isPicker };
        AssertDrawsAsButtonHelper(
            $"SymbolCct_{isPicker}", SymbolCct, arguments, 30, canvas => DrawSymbolCct(canvas, null, null, isPicker), CctBound,
            SymbolCct, new object?[] { Earlier(null), null, isPicker });
        AssertWrittenStopsDrawAsButtonHelper($"SymbolCct_{isPicker}", SymbolCct, arguments, canvas => DrawSymbolCct(canvas, null, null, isPicker));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_Cct_Colour_Picker_Left_To_Its_Defaults_Draws_What_ButtonHelper_Draws_Without_Lists(bool isLight)
    {
        Assert.Equal(new[] { "#F7DD74", "#FFFFFF", "#ACE2E2" }, CctPickerColors.Select(color => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}"));

        var arguments = new object?[] { null, null, isLight };
        AssertDrawsAsButtonHelper(
            $"CctColourPicker_{isLight}", CctColourPicker, arguments, 30, canvas => DrawCctColorPicker(canvas, null, null, isLight), CctBound,
            CctColourPicker, new object?[] { Earlier(null), null, isLight });
        AssertWrittenStopsDrawAsButtonHelper($"CctColourPicker_{isLight}", CctColourPicker, arguments, canvas => DrawCctColorPicker(canvas, null, null, isLight));
    }

    private static void AssertWrittenStopsDrawAsButtonHelper(string name, string svgMarkup, object?[] arguments, Action<SKCanvas> buttonHelper)
    {
        using var bound = Bound(Regex.Replace(svgMarkup, @" e:stop-(colors|offsets)=""[^""]*""", ""), arguments);
        using var reference = OnTile(30, buttonHelper);
        using var svg = OnTile(30, canvas => canvas.DrawPicture(bound));

        AssertSamePicture($"{name}-written", reference, svg, CctBound);
    }

    /// <summary>What TapHome keeps beside the API: its calls, with the lists CctColorCache hands it.</summary>
    private const string CctCaller = """
        using System.Collections.Generic;
        using SkiaSharp;

        namespace TapHome
        {
            public static class Caller
            {
                public static void Symbol(SKCanvas canvas, List<float> positions, List<SKColor> colors, bool isPicker)
                    => PaintCodeResources.ButtonHelper.DrawSymbolCct(canvas, positions, colors, isPicker);

                public static void StaticSymbol(SKCanvas canvas, bool isPicker)
                    => PaintCodeResources.ButtonHelper.DrawSymbolCct(canvas, null, null, isPicker);

                public static void Picker(SKCanvas canvas, List<float> positions, List<SKColor> colors, SKRect targetFrame, bool isLight)
                    => PaintCodeResources.ButtonHelper.DrawCctColorPicker(canvas, targetFrame, PaintCode.ResizingBehavior.AspectFit, positions, colors, isLight);
            }
        }
        """;

    [Fact]
    public void TapHomes_Cct_Calls_Compile_Against_The_Api_And_Draw_Again_Without_Allocating()
    {
        SkiaCSharpDrawing Drawing(string svg, string name, string method)
            => new(Model(svg), Namespace, name, SvgExpressionDeclarations.Parse(svg)) { Method = method };

        var code = SkiaCSharpCodeGen.GenerateFile(
            new[] { Drawing(SymbolCct, "ApiSymbolCct", "DrawSymbolCct"), Drawing(CctColourPicker, "ApiCctColorPicker", "DrawCctColorPicker") },
            SvgHelperScope.PerClass,
            style: new SvgCodeStyle { Api = "PaintCodeResources.ButtonHelper", Resizing = "PaintCode.ResizingBehavior", Body = SvgBody.Direct });

        Assert.Contains("public static void DrawSymbolCct(SKCanvas canvas, IReadOnlyList<float> positions, IReadOnlyList<SKColor> colors, bool isPicker)", code);
        Assert.Contains("public static void DrawCctColorPicker(SKCanvas canvas, SKRect targetFrame, global::PaintCode.ResizingBehavior resizing, IReadOnlyList<float> positions, IReadOnlyList<SKColor> colors, bool isLight)", code);

        var assembly = Compile(code, "CctApi", PaintCodeHost, CctCaller);
        var caller = assembly.GetType("TapHome.Caller")!;
        var symbol = caller.GetMethod("Symbol")!.CreateDelegate<Action<SKCanvas, List<float>, List<SKColor>, bool>>();
        var picker = caller.GetMethod("Picker")!.CreateDelegate<Action<SKCanvas, List<float>, List<SKColor>, SKRect, bool>>();

        FieldInfo Kept(string name) => Assert.Single(
            assembly.GetType($"{Namespace}.{name}")!.GetFields(BindingFlags.NonPublic | BindingFlags.Static),
            field => field.Name.StartsWith("s_skGradient", StringComparison.Ordinal));

        var keptSymbol = Kept("ApiSymbolCct");
        var keptPicker = Kept("ApiCctColorPicker");
        var (positions, colors) = CctStops(2700, 6500);
        var frame = new SKRect(0, 0, 60, 60);

        using var bitmap = new SKBitmap(30, 30);
        using var canvas = new SKCanvas(bitmap);
        symbol(canvas, positions, colors, true);
        picker(canvas, positions, colors, frame, true);
        var symbolGradient = keptSymbol.GetValue(null);
        var pickerGradient = keptPicker.GetValue(null);

        var before = GC.GetAllocatedBytesForCurrentThread();
        symbol(canvas, positions, colors, true);
        picker(canvas, positions, colors, frame, true);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.NotNull(symbolGradient);
        Assert.Same(symbolGradient, keptSymbol.GetValue(null));
        Assert.NotNull(pickerGradient);
        Assert.Same(pickerGradient, keptPicker.GetValue(null));
    }

    // ButtonHelper.DrawSymbolCct, the overload that takes no lists where they are null.
    private static void DrawSymbolCct(SKCanvas canvas, List<float>? positions, List<SKColor>? colors, bool isPicker)
    {
        using var paint = new SKPaint();
        using var cctShader = positions is null
            ? SKShader.CreateLinearGradient(new SKPoint(0f, 2f), new SKPoint(0f, 28f), s_symbolCctColors, SKShaderTileMode.Clamp)
            : SKShader.CreateLinearGradient(new SKPoint(0f, 2f), new SKPoint(0f, 28f), colors!.ToArray(), positions.ToArray(), SKShaderTileMode.Clamp);
        using var shadowShader = SKShader.CreateRadialGradient(
            new SKPoint(15f, 15f), 13f, new[] { SKColor.FromHsv(0, 0, 0, 0), SKColor.FromHsv(0, 0, 0, 30) }, new[] { 0.7f, 1f }, SKShaderTileMode.Clamp);

        paint.Reset();
        paint.IsAntialias = true;
        paint.Style = SKPaintStyle.Fill;
        paint.Shader = cctShader;
        canvas.DrawCircle(new SKPoint(15f, 15f), 13f, paint);

        if (!isPicker)
        {
            return;
        }

        paint.Reset();
        paint.IsAntialias = true;
        paint.Style = SKPaintStyle.Fill;
        paint.Shader = shadowShader;
        canvas.DrawCircle(new SKPoint(15f, 15f), 13f, paint);
    }

    // ButtonHelper.DrawCctColorPicker at its 30 by 30 frame, which leaves the resize as no transform,
    // and the overload that takes no lists where they are null.
    private static void DrawCctColorPicker(SKCanvas canvas, List<float>? positions, List<SKColor>? colors, bool isLight)
    {
        using var paint = new SKPaint();
        using var cctShader = positions is null
            ? SKShader.CreateLinearGradient(new SKPoint(0f, 0f), new SKPoint(0f, 30f), CctPickerColors, SKShaderTileMode.Clamp)
            : SKShader.CreateLinearGradient(new SKPoint(0f, 0f), new SKPoint(0f, 30f), colors!.ToArray(), positions.ToArray(), SKShaderTileMode.Clamp);
        using var shadowShader = SKShader.CreateRadialGradient(
            new SKPoint(15f, 15f),
            15f,
            new[]
            {
                SKColor.FromHsv(0, 0, 0, 0), SKColor.FromHsv(0, 0, 0, 0), SKColor.FromHsv(0, 0, 0, 0), SKColor.FromHsv(0, 0, 0, 0),
                SKColor.FromHsv(0, 0, 0, 0), SKColor.FromHsv(0, 0, 0, 2), SKColor.FromHsv(0, 0, 0, 5)
            },
            SKShaderTileMode.Clamp);
        using var shadowShader2 = SKShader.CreateRadialGradient(
            new SKPoint(15f, 15f),
            15f,
            new[] { SKColor.FromHsv(0, 0, 0, 0), SKColor.FromHsv(0, 0, 0, 10), SKColor.FromHsv(0, 0, 0, 40) },
            new[] { 0.80f, 0.96f, 1f },
            SKShaderTileMode.Clamp);

        foreach (var shader in new[] { cctShader, shadowShader, shadowShader2 })
        {
            paint.Reset();
            paint.IsAntialias = true;
            paint.Style = SKPaintStyle.Fill;
            paint.Shader = shader;
            canvas.DrawCircle(new SKPoint(15f, 15f), 15f, paint);
        }

        var strokeWidth = 0.8f;

        paint.Reset();
        paint.IsAntialias = true;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = strokeWidth;
        paint.Color = isLight ? SKColor.FromHsv(0, 0, 80, 180) : SKColor.FromHsv(0, 0, 100, 180);
        canvas.DrawCircle(new SKPoint(15f, 15f), 15.01f - (strokeWidth / 2), paint);
    }
}
