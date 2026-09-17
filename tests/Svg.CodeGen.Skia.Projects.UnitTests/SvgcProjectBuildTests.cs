// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.IO;
using ShimSkiaSharp;
using Svg.CodeGen.Skia.Projects;
using Svg.Model;
using Xunit;

namespace Svg.CodeGen.Skia.Projects.UnitTests;

/// <summary>Building a drawing a project carries rather than one it points at.</summary>
public class SvgcProjectBuildTests : IDisposable
{
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect x="0" y="0" width="24" height="24" fill="#3b82f6" />
        </svg>
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Path(string name) => System.IO.Path.Combine(_directory, name);

    [Fact]
    public void An_Item_Carrying_Its_Drawing_Builds_What_The_Same_Drawing_In_A_File_Builds()
    {
        var input = Path("badge.svg");

        File.WriteAllText(input, Drawing);

        var settings = new SvgcBuildSettings();
        var loader = new NoAssets();

        // The same drawing twice: once as a file svgc reads, once as the text Svg.Studio holds
        // inline, where the input is the drawing's name and there is no file to read at all.
        SvgcProjectBuild.Write(new SvgcProjectItem(input, Path("file.cs"), "Demo", "Badge", null), settings, loader);
        SvgcProjectBuild.Write(new SvgcProjectItem("Badge", Path("text.cs"), "Demo", "Badge", null, source: Drawing), settings, loader);

        Assert.Equal(File.ReadAllText(Path("file.cs")), File.ReadAllText(Path("text.cs")));
    }

    /// <summary>A loader for a drawing that asks for nothing: no image, no text.</summary>
    private sealed class NoAssets : ISvgAssetLoader
    {
        public bool EnableSvgFonts => false;

        public SKImage LoadImage(Stream stream) => throw new NotSupportedException();

        public List<TypefaceSpan> FindTypefaces(string? text, SKPaint paintPreferredTypeface) => new();

        public SKFontMetrics GetFontMetrics(SKPaint paint) => default;

        public float MeasureText(string? text, SKPaint paint, ref SKRect bounds) => 0f;

        public SKPath? GetTextPath(string? text, SKPaint paint, float x, float y) => null;
    }
}
