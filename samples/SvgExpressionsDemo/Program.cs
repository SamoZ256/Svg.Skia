using System;
using System.IO;
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
