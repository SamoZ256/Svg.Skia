using System;
using System.IO;
using System.Linq;
using Avalonia;
using SkiaSharp;
using Svg.CodeGen.Skia.Expressions;

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

        // One frame per value of the first number parameter, which is what a recipe normally
        // drives the palette from.
        foreach (var value in new[] { 0f, 0.25f, 0.5f, 0.75f })
        {
            var used = false;

            var arguments = parameters
                .Select(object? (p) =>
                {
                    if (p.Type == ExprType.Number && !used)
                    {
                        used = true;
                        return value;
                    }

                    return p.Type switch
                    {
                        ExprType.Number => 0f,
                        ExprType.Boolean => false,
                        _ => (object?)new SKColor(0x3F, 0xB5, 0xB5)
                    };
                })
                .ToArray();

            using var picture = result.Compiled.Invoke(arguments);
            if (picture is null)
            {
                Console.WriteLine("error: Record returned null.");
                return 1;
            }

            var path = Path.Combine(directory, $"demo-{value:0.00}.png");
            Write(picture, path);
            Console.WriteLine($"wrote {path}");
        }

        return 0;
    }

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
