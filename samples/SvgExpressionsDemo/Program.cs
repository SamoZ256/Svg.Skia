using System;
using System.IO;
using System.Linq;
using Avalonia;
using SkiaSharp;
using SvgExpressionsDemo.Generated;

namespace SvgExpressionsDemo;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // `--render <dir>` writes a few frames to PNG instead of opening a window, so the
        // parametrisation can be checked on a machine with no display.
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--render")
            {
                var directory = i + 1 < args.Length ? args[i + 1] : "frames";
                return Render(directory);
            }

            // Exercises the same runtime pipeline the editor uses, without a display.
            if (args[i] == "--live")
            {
                var svgPath = i + 1 < args.Length ? args[i + 1] : null;
                var outPath = i + 2 < args.Length ? args[i + 2] : "live.png";

                return svgPath is null ? Usage() : Live(svgPath, outPath);
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

    private static int Usage()
    {
        Console.WriteLine("usage: --render <dir> | --live <file.svg> [out.png]");
        return 2;
    }

    private static int Live(string svgPath, string outPath)
    {
        var result = new LiveCompiler().Compile(File.ReadAllText(svgPath));

        if (result.GeneratedCode is { } code)
        {
            Console.WriteLine($"generated {code.Split('\n').Length} lines of C#");
        }

        if (!result.Success)
        {
            foreach (var error in result.Errors)
            {
                Console.WriteLine($"error: {error}");
            }

            return 1;
        }

        // Mid-range values, so a parameterised document shows something representative.
        var arguments = result.Parameters
            .Select(object? (p) => p.Type switch
            {
                Svg.CodeGen.Skia.Expressions.ExprType.Number => 0.5f,
                Svg.CodeGen.Skia.Expressions.ExprType.Boolean => true,
                _ => new SKColor(0x3F, 0xB5, 0xB5)
            })
            .ToArray();

        Console.WriteLine(
            $"parameters: {(result.Parameters.Count == 0 ? "(none)" : string.Join(", ", result.Parameters.Select(p => $"{p.Name}:{p.Type}")))}");

        using var picture = result.Invoke(arguments);
        if (picture is null)
        {
            Console.WriteLine("error: Record returned null.");
            return 1;
        }

        var bounds = picture.CullRect;
        var width = (int)Math.Max(1, bounds.Width);
        var height = (int)Math.Max(1, bounds.Height);

        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0x1A, 0x1A, 0x1E));
            canvas.DrawPicture(picture);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(outPath);
        data.SaveTo(stream);

        Console.WriteLine($"wrote {outPath} ({width}x{height})");
        return 0;
    }

    private static int Render(string directory)
    {
        Directory.CreateDirectory(directory);

        const int size = 256;
        var frames = new (float T, bool Bold)[]
        {
            (0f, false), (0.25f, false), (0.5f, false), (0.75f, false), (0.25f, true)
        };

        foreach (var (t, bold) in frames)
        {
            using var bitmap = new SKBitmap(size, size);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(new SKColor(0x1A, 0x1A, 0x1E));

                using var picture = Logo.Record(t, bold: bold);
                canvas.DrawPicture(picture);
            }

            var path = Path.Combine(directory, $"logo-t{t:0.00}{(bold ? "-bold" : string.Empty)}.png");
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(path);
            data.SaveTo(stream);

            Console.WriteLine($"wrote {path}");
        }

        return 0;
    }
}
