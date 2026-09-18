#if PAINTCODE_ORACLE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using PaintCode;
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

    /// <summary>
    /// Every class in the compiled reference that draws, with the methods each of them draws by.
    /// </summary>
    /// <remarks>
    /// Anchored on PaintCode's own runtime shim rather than on any style kit's name: PaintCode2Skia
    /// emits <c>PaintCode.Context</c> beside whatever it generates, so the assembly can be found
    /// without knowing what is in it.
    ///
    /// Non-public too, because PaintCode emits a canvas it does not export as a private method --
    /// three of them in the document this was written against.
    /// </remarks>
    private static IReadOnlyDictionary<Type, MethodInfo[]> Drawing()
        => typeof(Context).Assembly
            .GetTypes()
            .Select(type => (type, methods: type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name.StartsWith("draw", StringComparison.Ordinal))
                .Where(m => m.GetParameters().FirstOrDefault()?.ParameterType == typeof(SKCanvas))
                .ToArray()))
            .Where(candidate => candidate.methods.Length > 0)
            .ToDictionary(candidate => candidate.type, candidate => candidate.methods);

    private static MethodInfo[] s_methods = Array.Empty<MethodInfo>();

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
    internal static IReadOnlyDictionary<string, MethodInfo> Methods { get; private set; } =
        new Dictionary<string, MethodInfo>(StringComparer.Ordinal);

    internal static int MethodCount => s_methods.Length;

    /// <summary>What was chosen and what it beat, so the choice is never silent.</summary>
    internal static string Scoreboard { get; private set; } = string.Empty;

    /// <summary>
    /// Picks the class that draws this document, by how much of it each candidate covers.
    /// </summary>
    /// <remarks>
    /// A folder holds a class per style kit, so which one draws a given document is a question the
    /// document itself answers: the class generated from it accounts for its canvases and any other
    /// accounts for almost none. Counted in canvases rather than methods, since PaintCode emits two
    /// overloads for most of them.
    ///
    /// Hand-written helpers alongside are not in the running at all -- PaintCode2Skia names what it
    /// generates <c>drawSomething</c>, carried over from the Java it transliterates, and the two
    /// beside this document's are <c>DrawSomething</c>. That is luck rather than a rule, which is why
    /// coverage decides rather than the mere presence of drawing methods, and why
    /// SVG_PAINTCODE_ORACLE_TYPE can name one outright.
    ///
    /// A wrong pick cannot pass quietly either way: the map is asserted total and injective after.
    /// </remarks>
    internal static void Use(IEnumerable<string> slugs)
    {
        var wanted = new HashSet<string>(slugs.Select(Normalise), StringComparer.Ordinal);
        var named = Environment.GetEnvironmentVariable("SVG_PAINTCODE_ORACLE_TYPE");

        var ranked = Drawing()
            .Select(candidate => (
                candidate.Key,
                candidate.Value,
                Covered: candidate.Value
                    .Select(m => Normalise(m.Name.Substring("draw".Length)))
                    .Distinct(StringComparer.Ordinal)
                    .Count(wanted.Contains)))
            .OrderByDescending(candidate => candidate.Covered)
            .ToList();

        Scoreboard = string.Join(
            Environment.NewLine,
            ranked.Select(candidate => $"  {candidate.Covered,5} of {wanted.Count}  {candidate.Key.FullName}"));

        var chosen = named is { Length: > 0 }
            ? ranked.FirstOrDefault(candidate => candidate.Key.FullName == named || candidate.Key.Name == named)
            : ranked.FirstOrDefault();

        if (chosen.Value is null)
        {
            throw new InvalidOperationException(
                $"Nothing in the compiled reference draws this document.{Environment.NewLine}{Scoreboard}");
        }

        s_methods = chosen.Value;
        Methods = s_methods
            .GroupBy(m => Normalise(m.Name.Substring("draw".Length)))
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.GetParameters().Length).First());
    }

    /// <summary>The boolean parameters a drawing method varies on, in PaintCode's own order.</summary>
    internal static IReadOnlyList<string> Switches(MethodInfo method) => method
        .GetParameters()
        .Where(p => p.ParameterType == typeof(bool))
        .Select(p => p.Name!)
        .ToArray();

    /// <summary>The numbers a drawing method varies on, likewise.</summary>
    internal static IReadOnlyList<string> Dials(MethodInfo method) => method
        .GetParameters()
        .Where(p => p.ParameterType == typeof(float))
        .Select(p => p.Name!)
        .ToArray();

    /// <summary>
    /// The values to try a number at: the ends of what it was declared to take, and the middle.
    /// </summary>
    /// <remarks>
    /// Any value probes honestly, since both sides are handed the same one -- the range only decides
    /// how representative the probe is. Where nothing was declared this is 0 to 1, which is what
    /// <c>level</c> means and what most of these are; a step enum carried as a number is weakly
    /// probed by it, and is the thing to improve if one turns out to hide a fault. An angle declares
    /// its own turn, so it is probed at 0, 180 and 360.
    /// </remarks>
    internal static IReadOnlyList<float> Turns(SKSvg svg, string name)
    {
        var parameter = svg.ExpressionParameters.FirstOrDefault(p => p.Name == name);

        if (parameter is null)
        {
            return new[] { 0f, 0.5f, 1f };
        }

        var range = parameter.ResolveRange();

        return new[] { range.Minimum, (range.Minimum + range.Maximum) / 2, range.Maximum };
    }

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
