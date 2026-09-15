#if PAINTCODE_ORACLE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using PaintCode;
using PaintCodeResources;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;
using Svg.Expressions;
using Svg.Skia;
using Svg.Skia.TypefaceProviders;
using Svg.Skia.UnitTests.Common;

namespace Svg.PaintCode.UnitTests.Oracle;

/// <summary>
/// Draws a canvas both ways — through the imported SVG and through PaintCode's own generated code —
/// and measures how far apart they are.
/// </summary>
/// <remarks>
/// The oracle is PaintCode's drawing commands for the same document, exported to Android and
/// transliterated to SkiaSharp by PaintCode2Skia. It is worth comparing against because the
/// transliteration is literal: the numbers, their order and the calls are PaintCode's own.
///
/// Both sides raster through the identical bitmap, clear, scale and read-back, so a difference can
/// only be the picture. Everything is measured in <see cref="ImageHelper"/>'s metric, the one the
/// W3C and resvg rows are calibrated in.
/// </remarks>
internal static class PaintCodeOracle
{
    /// <summary>A 30×30 canvas rasters to 180×180, where a misplaced shape is pixels, not rounding.</summary>
    internal const float Scale = 6f;

    // Non-public too: PaintCode emits a canvas it does not export as a private method, which is
    // three of them here (symbol-a, symbol-m, symbol-safe).
    private static readonly MethodInfo[] s_methods = typeof(VectorIconsResource)
        .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        .Where(m => m.Name.StartsWith("draw", StringComparison.Ordinal))
        .ToArray();

    /// <summary>
    /// PaintCode's method names and our file slugs punctuate differently, so both are reduced to
    /// their letters and digits before matching.
    /// </summary>
    internal static string Normalise(string name)
        => new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// Every drawing method, keyed by normalised canvas name, keeping the shorter of the two
    /// overloads PaintCode emits — the one without <c>(SKRect, ResizingBehavior)</c>, which is the
    /// framing PaintCode itself defaults to.
    /// </summary>
    internal static IReadOnlyDictionary<string, MethodInfo> Methods { get; } = s_methods
        .GroupBy(m => Normalise(m.Name.Substring("draw".Length)))
        .ToDictionary(g => g.Key, g => g.OrderBy(m => m.GetParameters().Length).First());

    internal static int MethodCount => s_methods.Length;

    /// <summary>The boolean parameters a drawing method varies on, in PaintCode's own order.</summary>
    internal static IReadOnlyList<string> Switches(MethodInfo method) => method
        .GetParameters()
        .Where(p => p.ParameterType == typeof(bool))
        .Select(p => p.Name!)
        .ToArray();

    /// <summary>
    /// The faces PaintCode asks for, or null when they were not pointed at.
    /// </summary>
    /// <remarks>
    /// PaintCode names four SF-UI-Display faces by filename and its own lookup falls through to
    /// whatever family the system lists first without saying so, which makes a text canvas compare
    /// against an arbitrary font. They are not in this repository either, so the canvases that draw
    /// words are only comparable once SVG_PAINTCODE_FONTS points at them.
    /// </remarks>
    internal static string? Fonts
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("SVG_PAINTCODE_FONTS");

            return string.IsNullOrEmpty(path) || !Directory.Exists(path) ? null : path;
        }
    }

    /// <summary>Hands PaintCode's own lookup the folder the faces are in.</summary>
    /// <remarks>
    /// Its fallback reads <c>FontNamePrefix + name</c> straight off disk, so a prefix ending in a
    /// separator is all it needs; nothing sets one otherwise, which is why it was matching an
    /// arbitrary installed family instead.
    /// </remarks>
    static PaintCodeOracle()
    {
        if (Fonts is { } fonts)
        {
            TypefaceManager.FontNamePrefix = fonts + Path.DirectorySeparatorChar;
        }
    }

    /// <summary>Loads a drawing with the same faces lent to PaintCode.</summary>
    internal static SKSvg Load(string path)
    {
        var svg = new SKSvg();

        if (Fonts is { } fonts && svg.Settings.TypefaceProviders is { } providers)
        {
            foreach (var face in Directory.GetFiles(fonts, "*.otf").OrderBy(f => f, StringComparer.Ordinal))
            {
                providers.Insert(0, new CustomTypefaceProvider(face));
            }
        }

        if (svg.Load(path) is null)
        {
            throw new InvalidOperationException($"'{path}' did not load.");
        }

        return svg;
    }

    internal static SKBitmap Ours(SKSvg svg, IReadOnlyDictionary<string, ExprValue> values)
    {
        var picture = svg.SetExpressionValues(values) ?? svg.Picture
            ?? throw new InvalidOperationException("The drawing loaded no picture.");

        return Raster(picture.CullRect.Width, picture.CullRect.Height, canvas => canvas.DrawPicture(picture));
    }

    internal static SKBitmap Theirs(MethodInfo method, IReadOnlyDictionary<string, ExprValue> values, float width, float height)
    {
        var arguments = Arguments(method, values, width, height);

        return Raster(width, height, canvas =>
        {
            arguments[0] = canvas;
            method.Invoke(null, arguments);
        });
    }

    /// <summary>
    /// Maps our declared parameters onto PaintCode's positional ones by name — they agree, because
    /// both come from the same PaintCode variables.
    /// </summary>
    private static object?[] Arguments(MethodInfo method, IReadOnlyDictionary<string, ExprValue> values, float width, float height)
    {
        var parameters = method.GetParameters();
        var arguments = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];

            if (i == 0 && parameter.ParameterType == typeof(SKCanvas))
            {
                continue;
            }

            if (parameter.IsOut)
            {
                // Rects a caller would read back for hit testing. Nothing here draws with them.
                continue;
            }

            if (parameter.ParameterType == typeof(Context))
            {
                arguments[i] = new Context();
                continue;
            }

            if (parameter.ParameterType == typeof(ResizingBehavior))
            {
                arguments[i] = ResizingBehavior.Stretch;
                continue;
            }

            if (parameter.ParameterType == typeof(SKRect))
            {
                arguments[i] = new SKRect(0f, 0f, width, height);
                continue;
            }

            if (!values.TryGetValue(parameter.Name!, out var value))
            {
                throw new InvalidOperationException(
                    $"'{method.Name}' takes '{parameter.Name}', which the drawing does not declare and no other drawing defaults.");
            }

            arguments[i] = parameter.ParameterType switch
            {
                var t when t == typeof(SKColor) => new SKColor(value.Red, value.Green, value.Blue, value.Alpha),
                var t when t == typeof(bool) => value.AsBoolean,
                var t when t == typeof(float) => value.AsNumber,
                var t when t == typeof(string) => value.AsString,
                _ => throw new InvalidOperationException($"'{method.Name}' takes '{parameter.Name}' as {parameter.ParameterType.Name}, which has no expression type.")
            };
        }

        return arguments;
    }

    private static SKBitmap Raster(float width, float height, Action<SKCanvas> draw)
    {
        var bitmap = new SKBitmap((int)MathF.Round(width * Scale), (int)MathF.Round(height * Scale));

        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(Scale);
            draw(canvas);
        }

        return bitmap;
    }

    /// <summary>Root mean square difference, 0 when the two rasters are identical.</summary>
    internal static double Difference(SKBitmap ours, SKBitmap theirs)
    {
        using var a = ToImage(ours);
        using var b = ToImage(theirs);

        return ImageHelper.CompareImages(a, b);
    }

    /// <summary>
    /// Reads through <see cref="SKBitmap.Pixels"/>, which unpremultiplies whatever the bitmap's own
    /// colour type is, so neither side's storage format can colour the comparison.
    /// </summary>
    private static Image<Rgba32> ToImage(SKBitmap bitmap)
    {
        var pixels = bitmap.Pixels;
        var image = new Image<Rgba32>(bitmap.Width, bitmap.Height);

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = pixels[(y * bitmap.Width) + x];

                image[x, y] = new Rgba32(pixel.Red, pixel.Green, pixel.Blue, pixel.Alpha);
            }
        }

        return image;
    }

    internal static string Describe(IReadOnlyList<string> switches, int combination)
    {
        if (switches.Count == 0)
        {
            return "defaults";
        }

        return string.Join(
            " ",
            switches.Select((name, bit) => $"{name}={((combination >> bit) & 1) == 1}"));
    }

    internal static string Number(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
#endif
