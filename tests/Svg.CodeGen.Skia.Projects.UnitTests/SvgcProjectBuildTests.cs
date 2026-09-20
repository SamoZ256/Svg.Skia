// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.IO;
using ShimSkiaSharp;
using Svg.CodeGen.Skia.Projects;
using Svg.Model;
using Svg.Skia;
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

    /// <summary>A drawing whose words the author drives, laid out the one way that can carry it.</summary>
    private const string Driven = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0"
             viewBox="0 0 64 24" width="64" height="24">
          <defs><e:code><e:param name="step" type="string" default="'1'" /></e:code></defs>
          <text x="32" y="18" text-anchor="middle" font-family="Arial" font-size="12" fill="#111827">{{ step }}</text>
        </svg>
        """;

    /// <summary>The same words, positioned per span, which is laid out before anything is drawn.</summary>
    private const string DrivenPerSpan = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0"
             viewBox="0 0 64 24" width="64" height="24">
          <defs><e:code><e:param name="step" type="string" default="'1'" /></e:code></defs>
          <text x="32" y="18" font-family="Arial" font-size="12" fill="#111827"><tspan x="4" y="18">{{ step }}</tspan></text>
        </svg>
        """;

    private string Build(string drawing, SvgTextLayout layout, ICollection<string>? log = null)
    {
        var target = Path("Out.cs");

        var project = new SvgcProject(
            null, "Demo", null, null, null, null, null, target, null, null, null, null,
            new[] { new SvgcProjectItem("Badge", null, null, "Badge", null, source: drawing) });

        var settings = SvgcBuildSettings.For(project);

        settings.SingleFile = target;
        settings.TextLayout = layout;

        SvgcProjectBuild.Run(project, settings, new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings())), line => log?.Add(line));

        return File.ReadAllText(target);
    }

    /// <summary>
    /// The one thing a driven text has to get right: the argument reaches the draw.
    /// </summary>
    /// <remarks>
    /// And the anchor goes with it rather than into the origin — 32 is where the element says it
    /// is, not where the default string happens to start, because a different string starts
    /// somewhere else.
    /// </remarks>
    [Fact]
    public void Text_The_Author_Drives_Is_Drawn_From_The_Argument()
    {
        var code = Build(Driven, SvgTextLayout.Baked);

        Assert.Contains("public static SKPicture Record(string step = null)", code);
        Assert.Contains("DrawText(step__default, 32f, 18f, SKTextAlign.Center", code);
    }

    /// <summary>
    /// Laid out per span, the positions are written down before the string is known, so the default
    /// is what the picture holds — said out loud, since the generated file compiles either way.
    /// </summary>
    [Fact]
    public void Text_A_Layout_Was_Measured_With_Is_Frozen_And_Said_So()
    {
        var log = new List<string>();
        var code = Build(DrivenPerSpan, SvgTextLayout.Baked, log);

        Assert.Contains("DrawText(\"1\", ", code);

        // Nothing reads it any more, so it is not offered: a signature that took the argument and
        // ignored it is the thing this generator has always refused to emit.
        Assert.DoesNotContain("string step", code);
        Assert.Contains(log, line => line.StartsWith("warning:", StringComparison.Ordinal) && line.Contains("the text of <tspan> is frozen"));
    }

    /// <summary>A parameter the author never used is theirs, and stays whatever else is frozen.</summary>
    [Fact]
    public void A_Parameter_Nothing_Froze_Is_Left_Alone()
    {
        var code = Build("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0"
                 viewBox="0 0 64 24" width="64" height="24">
              <defs><e:code>
                <e:param name="step" type="string" default="'1'" />
                <e:param name="spare" type="number" default="2" />
              </e:code></defs>
              <text x="32" y="18" font-family="Arial" font-size="12" fill="#111827"><tspan x="4" y="18">{{ step }}</tspan></text>
            </svg>
            """, SvgTextLayout.Baked);

        Assert.DoesNotContain("string step", code);
        Assert.Contains("float spare", code);
    }

    [Fact]
    public void Strict_Still_Refuses_What_It_Cannot_Vary()
    {
        var refusal = Assert.Throws<SvgcProjectException>(() => Build(DrivenPerSpan, SvgTextLayout.Strict));

        Assert.Contains("the text of <tspan> is resolved before the drawing is recorded", refusal.Message);
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
