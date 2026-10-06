using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Xml;
using Newtonsoft.Json;
using Svg.Model;
using Svg.Model.Services;

namespace Svg.Skia.Converter;

internal class SvgConverter
{
    private const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

    public static void Log(string message)
    {
        Console.WriteLine(message);
    }

    public static void Error(Exception ex)
    {
        Log($"{ex.Message}");
        Log($"{ex.StackTrace}");
        if (ex.InnerException is { })
        {
            Error(ex.InnerException);
        }
    }

    public static void GetFiles(System.IO.DirectoryInfo directory, string pattern, List<System.IO.FileInfo> paths)
    {
        var files = System.IO.Directory.EnumerateFiles(directory.FullName, pattern);
        if (files is { })
        {
            foreach (var path in files)
            {
                paths.Add(new System.IO.FileInfo(path));
            }
        }
    }

    private static void GetVectorDrawableFiles(System.IO.DirectoryInfo directory, List<System.IO.FileInfo> paths)
    {
        var files = System.IO.Directory.EnumerateFiles(directory.FullName, "*.xml");
        foreach (var path in files)
        {
            var fileInfo = new System.IO.FileInfo(path);
            if (IsVectorDrawableFile(fileInfo))
            {
                paths.Add(fileInfo);
            }
        }
    }

    private static bool IsVectorDrawableFile(System.IO.FileInfo file)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };

        try
        {
            using var stream = System.IO.File.OpenRead(file.FullName);
            using var reader = XmlReader.Create(stream, settings);
            return reader.MoveToContent() == XmlNodeType.Element
                && string.Equals(reader.LocalName, "vector", StringComparison.Ordinal)
                && string.Equals(reader.LookupNamespace("android"), AndroidNamespace, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static bool Save(System.IO.FileInfo inputPath, string outputPath, string format, int quality, string background, float scale, float scaleX, float scaleY, bool quiet, int i)
    {
        if (quiet == false)
        {
            Log($"[{i}] File: {inputPath}");
        }

        if (string.Compare(format, "svg", StringComparison.OrdinalIgnoreCase) == 0)
        {
            var svgDocument = SvgService.Open(inputPath.FullName);
            if (svgDocument is null)
            {
                Log($"Error: Failed to load input file: {inputPath.FullName}");
                return false;
            }

            svgDocument.Write(outputPath);

            if (quiet == false)
            {
                Log($"[{i}] Success: {outputPath}");
            }

            return true;
        }

        using var svg = new SKSvg();

        if (svg.Load(inputPath.FullName) == null)
        {
            Log($"Error: Failed to load input file: {inputPath.FullName}");
            return false;
        }

        if (string.Compare(format, "pdf", StringComparison.OrdinalIgnoreCase) == 0)
        {
            if (SkiaSharp.SKColor.TryParse(background, out var skBackgroundColor))
            {
                if (scale != 1f)
                {
                    svg.Picture?.ToPdf(outputPath, skBackgroundColor, scale, scale);
                }
                else
                {
                    svg.Picture?.ToPdf(outputPath, skBackgroundColor, scaleX, scaleY);
                }
            }
            else
            {
                throw new ArgumentException($"Invalid output image background.", nameof(background));
            }
        }
        else if (string.Compare(format, "xps", StringComparison.OrdinalIgnoreCase) == 0)
        {
            if (SkiaSharp.SKColor.TryParse(background, out var skBackgroundColor))
            {
                if (scale != 1f)
                {
                    svg.Picture?.ToXps(outputPath, skBackgroundColor, scale, scale);
                }
                else
                {
                    svg.Picture?.ToXps(outputPath, skBackgroundColor, scaleX, scaleY);
                }
            }
            else
            {
                throw new ArgumentException($"Invalid output image background.", nameof(background));
            }
        }
        else
        {
            if (Enum.TryParse<SkiaSharp.SKEncodedImageFormat>(format, true, out var skEncodedImageFormat))
            {
                if (SkiaSharp.SKColor.TryParse(background, out var skBackgroundColor))
                {
                    if (scale != 1f)
                    {
                        svg.Save(outputPath, skBackgroundColor, skEncodedImageFormat, quality, scale, scale);
                    }
                    else
                    {
                        svg.Save(outputPath, skBackgroundColor, skEncodedImageFormat, quality, scaleX, scaleY);
                    }
                }
                else
                {
                    throw new ArgumentException($"Invalid output image background.", nameof(background));
                }
            }
            else
            {
                throw new ArgumentException($"Invalid output image format.", nameof(format));
            }
        }

        if (quiet == false)
        {
            Log($"[{i}] Success: {outputPath}");
        }

        return true;
    }

    public static void Run(Settings settings)
    {
        var paths = new List<System.IO.FileInfo>();

        if (settings.InputFiles is { })
        {
            foreach (var file in settings.InputFiles)
            {
                paths.Add(file);
            }
        }

        if (settings.InputDirectory is { })
        {
            var directory = settings.InputDirectory;
            var pattern = settings.Pattern;
            if (pattern == null)
            {
                GetFiles(directory, "*.svg", paths);
                GetFiles(directory, "*.svgz", paths);
                GetVectorDrawableFiles(directory, paths);
            }
            else
            {
                GetFiles(directory, pattern, paths);
            }
        }

        if (settings.OutputFiles is { })
        {
            if (paths.Count > 0 && paths.Count != settings.OutputFiles.Length)
            {
                Log($"Error: The number of the output files must match the number of the input files.");
                return;
            }
        }

        if (settings.OutputDirectory is { } && !string.IsNullOrEmpty(settings.OutputDirectory.FullName))
        {
            if (!System.IO.Directory.Exists(settings.OutputDirectory.FullName))
            {
                System.IO.Directory.CreateDirectory(settings.OutputDirectory.FullName);
            }
        }

        using var language = settings.SystemLanguage is { } systemLanguage
            ? SvgService.PushSystemLanguage(CultureInfo.CreateSpecificCulture(systemLanguage))
            : null;

        var sw = Stopwatch.StartNew();

        int processed = 0;

        for (int i = 0; i < paths.Count; i++)
        {
            var inputPath = paths[i];
            var outputFile = settings.OutputFiles is { } ? settings.OutputFiles[i] : null;
            try
            {
                var outputPath = string.Empty;

                if (outputFile is { })
                {
                    outputPath = outputFile.FullName;
                }
                else
                {
                    var inputExtension = inputPath.Extension;
                    outputPath = System.IO.Path.ChangeExtension(inputPath.FullName, "." + settings.Format.ToLower());
                    if (settings.OutputDirectory is { } && !string.IsNullOrEmpty(settings.OutputDirectory.FullName))
                    {
                        outputPath = System.IO.Path.Combine(settings.OutputDirectory.FullName, System.IO.Path.GetFileName(outputPath));
                    }
                }

                var currentDirectoryName = System.IO.Path.GetDirectoryName(inputPath.FullName);
                if (currentDirectoryName is { })
                {
                    System.IO.Directory.SetCurrentDirectory(currentDirectoryName);
                }

                if (Save(inputPath, outputPath, settings.Format, settings.Quality, settings.Background, settings.Scale, settings.ScaleX, settings.ScaleY, settings.Quiet, i))
                {
                    processed++;
                }
            }
            catch (Exception ex)
            {
                if (settings.Quiet == false)
                {
                    Log($"[{i}] Error: {inputPath}");
                    Error(ex);
                }
            }
        }

        sw.Stop();

        if (paths.Count > 0)
        {
            Log($"Done: {sw.Elapsed} ({processed}/{paths.Count})");
        }
    }

    public static void Execute(System.IO.FileInfo? loadConfig, System.IO.FileInfo? saveConfig, Settings settings)
    {
        if (loadConfig is { })
        {
            var jsonSerializerSettings = new JsonSerializerSettings
            {
                Formatting = Newtonsoft.Json.Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                Converters =
                {
                    new FileInfoJsonConverter(),
                    new DirectoryInfoJsonConverter()
                }
            };
            var json = System.IO.File.ReadAllText(loadConfig.FullName);
            var loadedSettings = JsonConvert.DeserializeObject<Settings>(json, jsonSerializerSettings);
            if (loadedSettings is { })
            {
                try
                {
                    Run(loadedSettings);
                }
                catch (Exception ex)
                {
                    if (loadedSettings.Quiet == false)
                    {
                        Error(ex);
                    }
                }
            }
        }
        else
        {
            if (saveConfig is { })
            {
                var jsonSerializerSettings = new JsonSerializerSettings
                {
                    Formatting = Newtonsoft.Json.Formatting.Indented,
                    NullValueHandling = NullValueHandling.Ignore,
                    Converters =
                    {
                        new FileInfoJsonConverter(),
                        new DirectoryInfoJsonConverter()
                    }
                };
                string json = JsonConvert.SerializeObject(settings, jsonSerializerSettings);
                System.IO.File.WriteAllText(saveConfig.FullName, json);
            }

            try
            {
                Run(settings);
            }
            catch (Exception ex)
            {
                if (settings.Quiet == false)
                {
                    Error(ex);
                }
            }
        }
    }
}
