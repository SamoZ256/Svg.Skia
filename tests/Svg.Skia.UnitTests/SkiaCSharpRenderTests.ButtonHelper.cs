using System;
using System.Linq;
using System.Text.RegularExpressions;
using SkiaSharp;
using Svg.CodeGen.Skia;
using Svg.Expressions;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// The four drawings TapHome's ButtonHelper.cs draws by hand with a sweep gradient, each recorded
/// with the SkiaSharp calls it makes at its native frame and compared with the SVG that replaces it.
/// </summary>
/// <remarks>
/// Each row's bound is at least what it measured on Apple Silicon and up to 1.3 times it, room for
/// the other raster pipelines CI's x64 machines run, and the same drawing a little off has to exceed it. <c>SKPathBuilder</c> stands in for <c>SKPath.AddArc</c>, which
/// SkiaSharp 4 marks obsolete.
/// </remarks>
public partial class SkiaCSharpRenderTests
{
    // TapHome's dark tile: a light thermostat's ring is white, and on the white Draw clears to it
    // would not be compared at all.
    private static readonly SKColor Tile = new(31, 33, 35);

    private static SKColor[] Hues => Enumerable.Range(0, 7).Select(i => SKColor.FromHsv(i * 60, 86, 100)).ToArray();

    private static readonly SKColor[] ThermostatColors =
    {
        new(237, 35, 18), new(199, 201, 203), new(78, 183, 226), new(194, 58, 144), new(237, 35, 18)
    };

    private const string HueWheel = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 30 30" width="30" height="30">
          <defs>
            <e:code><e:param name="picker" type="boolean" default="false" /></e:code>
            <sweepGradient id="hue" gradientUnits="userSpaceOnUse" cx="15" cy="15">
              <stop offset="0" stop-color="#FF2323" />
              <stop offset="0.16666667" stop-color="#FFFF23" />
              <stop offset="0.33333334" stop-color="#23FF23" />
              <stop offset="0.5" stop-color="#23FFFF" />
              <stop offset="0.6666667" stop-color="#2323FF" />
              <stop offset="0.8333333" stop-color="#FF23FF" />
              <stop offset="1" stop-color="#FF2323" />
            </sweepGradient>
            <radialGradient id="saturation" gradientUnits="userSpaceOnUse" cx="15" cy="15" r="13">
              <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.8627451" />
              <stop offset="1" stop-color="#FFFFFF" stop-opacity="0" />
            </radialGradient>
            <radialGradient id="shadow" gradientUnits="userSpaceOnUse" cx="15" cy="15" r="13">
              <stop offset="0.7" stop-color="#000000" stop-opacity="0" />
              <stop offset="1" stop-color="#000000" stop-opacity="0.11764706" />
            </radialGradient>
          </defs>
          <g transform="rotate(-90 15 15)">
            <circle cx="15" cy="15" r="13" fill="url(#hue) #FF2323" />
            <g display="{{ picker }}">
              <circle cx="15" cy="15" r="13" fill="url(#saturation)" />
              <circle cx="15" cy="15" r="13" fill="url(#shadow)" />
            </g>
          </g>
        </svg>
        """;

    private const string HueColourPicker = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 30 30" width="30" height="30">
          <defs>
            <e:code><e:param name="light" type="boolean" default="false" /></e:code>
            <sweepGradient id="hue" gradientUnits="userSpaceOnUse" cx="15" cy="15">
              <stop offset="0" stop-color="#FF2323" />
              <stop offset="0.16666667" stop-color="#FFFF23" />
              <stop offset="0.33333334" stop-color="#23FF23" />
              <stop offset="0.5" stop-color="#23FFFF" />
              <stop offset="0.6666667" stop-color="#2323FF" />
              <stop offset="0.8333333" stop-color="#FF23FF" />
              <stop offset="1" stop-color="#FF2323" />
            </sweepGradient>
            <radialGradient id="saturation" gradientUnits="userSpaceOnUse" cx="15" cy="15" r="14">
              <stop offset="0" stop-color="#FFFFFF" />
              <stop offset="1" stop-color="#FFFFFF" stop-opacity="0" />
            </radialGradient>
            <radialGradient id="saturation2" gradientUnits="userSpaceOnUse" cx="15" cy="15" r="15">
              <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.39215687" />
              <stop offset="1" stop-color="#FFFFFF" stop-opacity="0" />
            </radialGradient>
            <radialGradient id="shadow" gradientUnits="userSpaceOnUse" cx="15" cy="15" r="15">
              <stop offset="0" stop-color="#000000" stop-opacity="0.007843138" />
              <stop offset="0.16666667" stop-color="#000000" stop-opacity="0.007843138" />
              <stop offset="0.33333334" stop-color="#000000" stop-opacity="0.019607844" />
              <stop offset="0.5" stop-color="#000000" stop-opacity="0.039215688" />
              <stop offset="0.6666667" stop-color="#000000" stop-opacity="0.05882353" />
              <stop offset="0.8333333" stop-color="#000000" stop-opacity="0.10980392" />
              <stop offset="1" stop-color="#000000" stop-opacity="0.14117648" />
            </radialGradient>
            <radialGradient id="shadow2" gradientUnits="userSpaceOnUse" cx="15" cy="15" r="15">
              <stop offset="0.8" stop-color="#000000" stop-opacity="0" />
              <stop offset="0.96" stop-color="#000000" stop-opacity="0.039215688" />
              <stop offset="1" stop-color="#000000" stop-opacity="0.15686275" />
            </radialGradient>
          </defs>
          <g transform="rotate(-90 15 15)">
            <circle cx="15" cy="15" r="15" fill="url(#hue) #FF2323" />
            <circle cx="15" cy="15" r="15" fill="url(#saturation)" />
            <circle cx="15" cy="15" r="15" fill="url(#saturation2)" />
            <circle cx="15" cy="15" r="15" fill="url(#shadow)" />
            <circle cx="15" cy="15" r="15" fill="url(#shadow2)" />
            <circle cx="15" cy="15" r="14.61" fill="none" stroke="{{ light ? #CCCCCC : #FFFFFF }}" stroke-opacity="0.70588235" stroke-width="0.8" />
          </g>
        </svg>
        """;

    // The opacity is on each ring rather than on a group around both: over two draws generated code
    // keeps the group's layer for every value, where the interpreter folds it into the one it draws,
    // and the two then part by a level.
    private const string ThermostatRing = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 30 30" width="30" height="30">
          <defs>
            <e:code>
              <e:param name="enabled" type="boolean" default="true" />
              <e:param name="light" type="boolean" default="false" />
              <e:let name="fade">enabled ? 1 : 0.2</e:let>
            </e:code>
            <sweepGradient id="thermostat" gradientUnits="userSpaceOnUse" cx="15" cy="15">
              <stop offset="0" stop-color="#ED2312" />
              <stop offset="0.25" stop-color="#C7C9CB" />
              <stop offset="0.5" stop-color="#4EB7E2" />
              <stop offset="0.75" stop-color="#C23A90" />
              <stop offset="1" stop-color="#ED2312" />
            </sweepGradient>
          </defs>
          <circle cx="15" cy="15" r="13" fill="none" stroke="url(#thermostat) #ED2312" stroke-width="1.2" opacity="{{ fade }}" display="{{ !light }}" />
          <circle cx="15" cy="15" r="13" fill="none" stroke="#FFFFFF" stroke-width="1.2" opacity="{{ fade }}" display="{{ light }}" />
        </svg>
        """;

    // A ring that never changes under a driven sector, since d is drivable only in a clip and a driven
    // dash would rebuild the paint and its gradient every draw; the discs are the round caps. At
    // exactly 3/306 they leave a dot where ButtonHelper draws nothing.
    private const string ThermostatDial = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 180 180" width="180" height="180">
          <defs>
            <e:code>
              <e:param name="level" type="number" default="0.5" />
              <e:let name="sweep">306 * level - 3</e:let>
              <e:let name="clockwise">sweep >= 0 ? 1 : 0</e:let>
              <e:let name="middle">(120 + sweep / 2) * pi / 180</e:let>
              <e:let name="end">(120 + sweep) * pi / 180</e:let>
            </e:code>
            <sweepGradient id="dial" gradientUnits="userSpaceOnUse" cx="89.79" cy="90.02">
              <stop offset="0" stop-color="#ED2312" />
              <stop offset="0.25" stop-color="#C7C9CB" />
              <stop offset="0.5" stop-color="#4EB7E2" />
              <stop offset="0.75" stop-color="#C23A90" />
              <stop offset="1" stop-color="#ED2312" />
            </sweepGradient>
            <clipPath id="reach">
              <path d="M89.79,90.02L49.79,159.30203A80 80 0 0 {{ clockwise }} {{ 89.79 + 80 * cos(middle) }},{{ 90.02 + 80 * sin(middle) }}A80 80 0 0 {{ clockwise }} {{ 89.79 + 80 * cos(end) }},{{ 90.02 + 80 * sin(end) }}Z" />
              <circle cx="51.54" cy="156.27094" r="1" />
              <circle cx="{{ 89.79 + 76.5 * cos(end) }}" cy="{{ 90.02 + 76.5 * sin(end) }}" r="1" />
            </clipPath>
          </defs>
          <circle cx="89.79" cy="90.02" r="76.5" fill="none" stroke="url(#dial) #ED2312" stroke-width="2" clip-path="url(#reach)" />
        </svg>
        """;

    [Theory]
    [InlineData(false, 0.0011)]
    [InlineData(true, 0.001)]
    public void A_Hue_Wheel_Draws_What_ButtonHelper_Draws(bool picker, double threshold)
    {
        Assert.Equal(
            new[] { "#FF2323", "#FFFF23", "#23FF23", "#23FFFF", "#2323FF", "#FF23FF", "#FF2323" },
            Hues.Select(hue => $"#{hue.Red:X2}{hue.Green:X2}{hue.Blue:X2}"));

        var arguments = new object?[] { picker };
        AssertDrawsAsButtonHelper(
            $"HueWheel_{picker}", HueWheel, arguments, 30, canvas => DrawSymbolHue(canvas, picker), threshold,
            WithGradientTransform(HueWheel, "rotate(2 15 15)"), arguments);
    }

    [Theory]
    [InlineData(false, 0.001)]
    [InlineData(true, 0.001)]
    public void A_Hue_Colour_Picker_Draws_What_ButtonHelper_Draws(bool light, double threshold)
    {
        var arguments = new object?[] { light };
        AssertDrawsAsButtonHelper(
            $"HueColourPicker_{light}", HueColourPicker, arguments, 30, canvas => DrawHueColorPicker(canvas, light), threshold,
            WithGradientTransform(HueColourPicker, "rotate(2 15 15)"), arguments);
    }

    // A disabled ring turned 2 degrees measures under its bound, so it is told apart from an opacity of
    // 0.21; an enabled white one, with no hue to turn, from a ring 0.02 narrower.
    [Theory]
    [InlineData(true, false, 0.001)]
    [InlineData(true, true, 0.001)]
    [InlineData(false, false, 0.001)]
    [InlineData(false, true, 0.0013)]
    public void A_Thermostat_Ring_Draws_What_ButtonHelper_Draws(bool enabled, bool light, double threshold)
    {
        var arguments = new object?[] { enabled, light };
        var off = (enabled, light) switch
        {
            (false, _) => ThermostatRing.Replace(": 0.2<", ": 0.21<"),
            (_, true) => ThermostatRing.Replace("#FFFFFF\" stroke-width=\"1.2\"", "#FFFFFF\" stroke-width=\"1.18\""),
            _ => WithGradientTransform(ThermostatRing, "rotate(2 15 15)")
        };

        Assert.NotEqual(ThermostatRing, off);
        AssertDrawsAsButtonHelper(
            $"ThermostatRing_{enabled}_{light}", ThermostatRing, arguments, 30, canvas => DrawThermostattemperaturelevel(canvas, enabled, light), threshold,
            off, arguments);
    }

    // The bounds are wide because ButtonHelper's arc starts at 120 degrees and the ring here is a whole
    // circle, which Skia strokes up to a fifth of a pixel apart even drawing both itself; the clip
    // adds nothing. An arc a degree short is the level a 306th nearer 3/306; at level 0 three would
    // leave no arc at all.
    [Theory]
    [InlineData(0f, 1, 0.0012)]
    [InlineData(0.25f, 3, 0.0061)]
    [InlineData(0.5f, 3, 0.0047)]
    [InlineData(1f, 3, 0.007)]
    public void A_Thermostat_Dial_Draws_What_ButtonHelper_Draws(float level, int degreesShort, double threshold)
        => AssertDrawsAsButtonHelper(
            Named("ThermostatDial", level), ThermostatDial, new object?[] { level }, 180, canvas => DrawDetailthermostatlevelangle(canvas, level), threshold,
            ThermostatDial, new object?[] { level - degreesShort * MathF.Sign(306 * level - 3) / 306f });

    [Fact]
    public void A_Hue_Wheel_Turned_By_Its_Gradient_Transform_Draws_As_The_Turned_Group()
    {
        var arguments = new object?[] { false };
        using var group = Bound(HueWheel, arguments);
        using var gradient = Bound(WithGradientTransform(HueWheel.Replace("""<g transform="rotate(-90 15 15)">""", "<g>"), "rotate(-90 15 15)"), arguments);

        AssertSamePicture("HueWheelTurnedByGradient", group, gradient, 0.001);
    }

    // The V3 body edits each SKPath in place, which on SkiaSharp 3 allocates nothing. SkiaSharp 4,
    // which runs here, edits an SKPath through a builder it makes again once the path has been read,
    // so each path the clip is built into may cost that builder and nothing else.
    [Fact]
    public void A_Direct_Draw_Of_The_Thermostat_Dial_Allocates_Nothing_On_SkiaSharp3()
    {
        var code = SkiaCSharpCodeGen.GenerateFile(
            new[] { new SkiaCSharpDrawing(Model(ThermostatDial), Namespace, "UnallocatedDial", SvgExpressionDeclarations.Parse(ThermostatDial)) },
            SvgHelperScope.PerClass,
            skiaSharp: SkiaSharpTarget.V3,
            style: new SvgCodeStyle { Body = SvgBody.Direct });
        var draw = Compile(code, "UnallocatedDial").GetType($"{Namespace}.UnallocatedDial")!
            .GetMethod("Draw")!
            .CreateDelegate<Action<SKCanvas, float>>();
        var paths = Regex.Matches(code, @"= SvgPathSlot\(\d+\);").Count;

        using var bitmap = new SKBitmap(180, 180);
        using var canvas = new SKCanvas(bitmap);
        draw(canvas, 0.25f);

        var before = GC.GetAllocatedBytesForCurrentThread();
        draw(canvas, 0.5f);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(3, paths);
        Assert.Equal(paths * EditedPathCost(), allocated);
    }

    private static long EditedPathCost()
    {
#pragma warning disable CS0618 // SkiaSharp 3's SKPath, edited as the V3 body edits it.
        using var path = new SKPath();
        path.Rewind();
        path.AddCircle(0, 0, 1);
        _ = path.Handle;

        var before = GC.GetAllocatedBytesForCurrentThread();
        path.Rewind();
        path.AddCircle(0, 0, 1);
        _ = path.Handle;
#pragma warning restore CS0618

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void AssertDrawsAsButtonHelper(
        string name,
        string svgMarkup,
        object?[] arguments,
        float size,
        Action<SKCanvas> buttonHelper,
        double threshold,
        string offMarkup,
        object?[] offArguments)
    {
        using var bound = Bound(WithSweep(svgMarkup), arguments);
        using var boundOff = Bound(offMarkup, offArguments);
        using var reference = OnTile(size, buttonHelper);
        using var svg = OnTile(size, canvas => canvas.DrawPicture(bound));
        using var off = OnTile(size, canvas => canvas.DrawPicture(boundOff));

        AssertSamePicture($"{name}-ButtonHelper", reference, svg, threshold);
        Assert.True(Difference(reference, off) > threshold, $"{name}: the drawing a little off is within {threshold}.");

        AssertBindsTheSame(name, svgMarkup, arguments, SkiaSharpTarget.V3);
    }

    private static SKPicture Bound(string svgMarkup, object?[] arguments)
        => Bound(Model(svgMarkup), SvgExpressionDeclarations.Parse(svgMarkup), arguments);

    // Each 30 by 30 drawing has one gradient centred at 15,15, and no transform on it.
    private static string WithGradientTransform(string svgMarkup, string transform)
    {
        var turned = svgMarkup.Replace("""cx="15" cy="15">""", $"""cx="15" cy="15" gradientTransform="{transform}">""");
        Assert.NotNull(Assert.Single(Sweeps(turned)).LocalMatrix);

        return turned;
    }

    private static SKPicture OnTile(float size, Action<SKCanvas> draw)
        => Recorded(SKRect.Create(size, size), canvas =>
        {
            canvas.DrawColor(Tile);
            draw(canvas);
        });

    // ButtonHelper.DrawSymbolHue.
    private static void DrawSymbolHue(SKCanvas canvas, bool isPicker)
    {
        using var paint = new SKPaint();
        using var hueShader = SKShader.CreateSweepGradient(new SKPoint(15f, 15f), Hues, SKShaderTileMode.Clamp, 0, 360);
        using var saturationShader = SKShader.CreateRadialGradient(
            new SKPoint(15f, 15f), 13f, new[] { SKColor.FromHsv(0, 0, 100, 220), SKColor.FromHsv(0, 0, 100, 0) }, SKShaderTileMode.Clamp);
        using var shadowShader = SKShader.CreateRadialGradient(
            new SKPoint(15f, 15f), 13f, new[] { SKColor.FromHsv(0, 0, 0, 0), SKColor.FromHsv(0, 0, 0, 30) }, new[] { 0.7f, 1f }, SKShaderTileMode.Clamp);

        canvas.Save();
        canvas.RotateDegrees(-90, 15f, 15f);

        paint.Reset();
        paint.IsAntialias = true;
        paint.Style = SKPaintStyle.Fill;
        paint.Shader = hueShader;
        canvas.DrawCircle(new SKPoint(15f, 15f), 13f, paint);

        if (!isPicker)
        {
            canvas.Restore();
            return;
        }

        paint.Reset();
        paint.IsAntialias = true;
        paint.Style = SKPaintStyle.Fill;
        paint.Shader = saturationShader;
        canvas.DrawCircle(new SKPoint(15f, 15f), 13f, paint);

        paint.Reset();
        paint.IsAntialias = true;
        paint.Style = SKPaintStyle.Fill;
        paint.Shader = shadowShader;
        canvas.DrawCircle(new SKPoint(15f, 15f), 13f, paint);

        canvas.Restore();
    }

    // ButtonHelper.DrawHueColorPicker at its 30 by 30 frame, which leaves the resize as no transform.
    private static void DrawHueColorPicker(SKCanvas canvas, bool isLight)
    {
        using var paint = new SKPaint();
        using var hueShader = SKShader.CreateSweepGradient(new SKPoint(15f, 15f), Hues, SKShaderTileMode.Clamp, 0, 360);
        using var saturationShader = SKShader.CreateRadialGradient(
            new SKPoint(15f, 15f), 14f, new[] { SKColor.FromHsv(0, 0, 100, 255), SKColor.FromHsv(0, 0, 100, 0) }, SKShaderTileMode.Clamp);
        using var saturationShader2 = SKShader.CreateRadialGradient(
            new SKPoint(15f, 15f), 15f, new[] { SKColor.FromHsv(0, 0, 100, 100), SKColor.FromHsv(0, 0, 100, 0) }, SKShaderTileMode.Clamp);
        using var shadowShader = SKShader.CreateRadialGradient(
            new SKPoint(15f, 15f),
            15f,
            new[]
            {
                SKColor.FromHsv(0, 0, 0, 2), SKColor.FromHsv(0, 0, 0, 2), SKColor.FromHsv(0, 0, 0, 5), SKColor.FromHsv(0, 0, 0, 10),
                SKColor.FromHsv(0, 0, 0, 15), SKColor.FromHsv(0, 0, 0, 28), SKColor.FromHsv(0, 0, 0, 36)
            },
            SKShaderTileMode.Clamp);
        using var shadowShader2 = SKShader.CreateRadialGradient(
            new SKPoint(15f, 15f),
            15f,
            new[] { SKColor.FromHsv(0, 0, 0, 0), SKColor.FromHsv(0, 0, 0, 10), SKColor.FromHsv(0, 0, 0, 40) },
            new[] { 0.80f, 0.96f, 1f },
            SKShaderTileMode.Clamp);

        canvas.Save();
        canvas.RotateDegrees(-90, 15f, 15f);

        foreach (var shader in new[] { hueShader, saturationShader, saturationShader2, shadowShader, shadowShader2 })
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

        canvas.Restore();
    }

    // ButtonHelper.DrawThermostattemperaturelevel, which ignores its level.
    private static void DrawThermostattemperaturelevel(SKCanvas canvas, bool enabled, bool light)
    {
        using var paint = new SKPaint();
        float enabledTransparency = enabled ? 1f : 0.2f;

        using var tempPaint1 = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(255, 255, 255, (byte)(enabledTransparency * 255f)) };
        canvas.SaveLayer(tempPaint1);
        {
            var rBRect = new SKRect(2.00f, 2.00f, 28.00f, 28.00f);
            using var rBPath = new SKPathBuilder();

            var strokeWidth = 1.2f;

            rBPath.AddArc(rBRect, 0f, 360f);

            paint.IsAntialias = true;
            paint.StrokeWidth = strokeWidth;
            paint.StrokeCap = SKStrokeCap.Round;
            paint.Style = SKPaintStyle.Stroke;

            var center = new SKPoint(rBRect.MidX, rBRect.MidY);
            using var gradientShader = SKShader.CreateSweepGradient(center, ThermostatColors, null, SKShaderTileMode.Clamp, 0f, 360f);

            if (light)
            {
                paint.Color = SKColors.White;
            }
            else
            {
                paint.Shader = gradientShader;
            }

            using var path = rBPath.Detach();
            canvas.DrawPath(path, paint);
        }
        canvas.Restore();
    }

    // The gradient arc of ButtonHelper.DrawDetailthermostatlevelangle at its 180 by 180 frame, with
    // its sweep computed as it computes it: 306 * level - 3, from 120 degrees.
    private static void DrawDetailthermostatlevelangle(SKCanvas canvas, float desiredTemp)
    {
        using var paint = new SKPaint();
        float analogOutputAngle = -(desiredTemp * 0.85f + 0.075f) * 360f;

        var strokeWidth = 2.0f;
        var group = new SKRect(13.29f, 13.52f, 166.29f, 166.52f);

        using var selectedPath2 = new SKPathBuilder();
        selectedPath2.AddArc(group, 120f, -(analogOutputAngle - 120f) - 120f + (-(analogOutputAngle - 120f) < 120f ? 360f * MathF.Ceiling((120f + (analogOutputAngle - 120f)) / 360f) : -30f));

        paint.IsAntialias = true;
        paint.StrokeWidth = strokeWidth;
        paint.StrokeCap = SKStrokeCap.Round;
        paint.Style = SKPaintStyle.Stroke;

        var center = new SKPoint(group.MidX, group.MidY);
        using var gradientShader = SKShader.CreateSweepGradient(center, ThermostatColors, null, SKShaderTileMode.Clamp, 0f, 360f);

        paint.Shader = gradientShader;

        using var path = selectedPath2.Detach();
        canvas.DrawPath(path, paint);
    }
}
