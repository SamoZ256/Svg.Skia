using System;
using System.IO;
using System.Linq;
using Avalonia;
using SkiaSharp;
using Svg.CodeGen.Skia.Expressions;
using Svg.Expressions;

namespace SvgRecipeDemo;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // `--render <dir>` runs the same chain the window runs and writes frames to PNG, so the
        // conversion can be checked on a machine with no display.
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--render")
            {
                var directory = i + 1 < args.Length ? args[i + 1] : "frames";
                return Render(directory);
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseSkia()
            .LogToTrace();

    private static int Render(string directory)
    {
        Directory.CreateDirectory(directory);

        var result = new RecipePipeline().Run(DemoFiles.Svg, DemoFiles.Recipe);

        foreach (var match in result.Matches)
        {
            Console.WriteLine($"  {match.Rule.ColorText} -> {{{{ {match.Rule.Expression} }}}} ({match.Count})");
        }

        if (!result.Success)
        {
            foreach (var error in result.AllErrors)
            {
                Console.WriteLine($"error: {error}");
            }

            return 1;
        }

        var parameters = result.Compiled!.Parameters;

        Console.WriteLine($"generated {result.Compiled.GeneratedCode?.Split('\n').Length ?? 0} lines of C#");
        Console.WriteLine(
            $"parameters: {(parameters.Count == 0 ? "(none)" : string.Join(", ", parameters.Select(p => $"{p.Name}:{p.Type}")))}");

        // A number parameter is swept over its range; without one, every boolean is written both
        // ways. Sweeping only the first would have said nothing about this recipe, whose first
        // two booleans turn out not to reach the drawing at all.
        var sweep = parameters
            .Select((parameter, index) => (parameter, index))
            .Where(p => p.parameter.Type is ExprType.Number or ExprType.Boolean)
            .ToList();

        if (sweep.Any(p => p.parameter.Type == ExprType.Number))
        {
            sweep = sweep.Where(p => p.parameter.Type == ExprType.Number).Take(1).ToList();
        }

        var frames = sweep.SelectMany(p => (p.parameter.Type == ExprType.Number
                ? new object?[] { 0f, 0.25f, 0.5f, 0.75f }
                : new object?[] { false, true })
            .Select(state => (p.parameter.Name, p.index, state)))
            .ToList();

        if (frames.Count == 0)
        {
            frames.Add((string.Empty, -1, null));
        }

        foreach (var (name, index, state) in frames)
        {
            var arguments = parameters
                .Select(object? (p, i) => i == index ? state : Fallback(p, i))
                .ToArray();

            using var picture = result.Compiled.Invoke(arguments);
            if (picture is null)
            {
                Console.WriteLine("error: Record returned null.");
                return 1;
            }

            var path = Path.Combine(
                directory,
                index < 0 ? "demo.png" : $"demo-{name}-{state!.ToString()!.ToLowerInvariant()}.png");

            Write(picture, path);
            Console.WriteLine($"wrote {path}");
        }

        return 0;
    }

    // Values for the parameters not being swept. Colours have no sensible neutral — the caller is
    // meant to supply them — so contrasting stand-ins are used, or a recipe that picks between a
    // light and a dark colour would render the same either way.
    private static object? Fallback(Svg.CodeGen.Skia.SvgCodeParameter parameter, int index) => parameter.Type switch
    {
        ExprType.Number => 0f,
        ExprType.Boolean => string.Equals(parameter.DefaultExpression, "true", StringComparison.Ordinal),
        _ => index % 2 == 0 ? new SKColor(0x00, 0x00, 0x00) : new SKColor(0xFF, 0xFF, 0xFF)
    };

    private static void Write(SKPicture picture, string path)
    {
        const int size = 256;

        var bounds = picture.CullRect;
        var scale = Math.Min(size / Math.Max(bounds.Width, 1f), size / Math.Max(bounds.Height, 1f));

        using var bitmap = new SKBitmap(size, size);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0x1A, 0x1A, 0x1E));
            canvas.Scale(scale);
            canvas.DrawPicture(picture);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }
}
