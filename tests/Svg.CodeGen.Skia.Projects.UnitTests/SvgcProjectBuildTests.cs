// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.IO;
using ShimSkiaSharp;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions;
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

    /// <summary>
    /// Boxes a PaintCode import marks: one under the drawing's own offsets, one a symbol's copy
    /// brought into <c>&lt;defs&gt;</c>, and one inside a clip, which is never drawn.
    /// </summary>
    private const string Boxed = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0"
             viewBox="0 0 30 30" width="30" height="30">
          <defs>
            <g id="sym"><rect x="0" y="0" width="5" height="5" fill="none" e:bounds="SymbolRect" /></g>
          </defs>
          <g transform="translate(1 2)">
            <rect x="0" y="0" width="26" height="18" fill="none" transform="translate(1 4)" e:bounds="LevelRect" />
          </g>
          <clipPath id="clip"><rect x="0" y="0" width="4" height="4" e:bounds="HiddenRect" /></clipPath>
          <use href="#sym" x="10" y="10" />
        </svg>
        """;

    [Fact]
    public void A_Marked_Box_Is_Reported_Where_Its_Transforms_Put_It()
    {
        var log = new List<string>();
        var code = Build(Boxed, SvgTextLayout.Baked, log);

        Assert.Contains("public static readonly SKRect @LevelRect = new SKRect(2f, 6f, 28f, 24f);", code);

        // The symbol's box belongs to the symbol's own class.
        Assert.DoesNotContain("SymbolRect", code);
        Assert.DoesNotContain("HiddenRect", code);
        Assert.Contains(log, line => line.StartsWith("warning:", StringComparison.Ordinal) && line.Contains("'HiddenRect' has no place of its own"));
    }

    /// <summary>
    /// A <c>&lt;use&gt;</c> compiles its target again under the target's own address, and is compiled
    /// first when it comes first, so the box has to be told from its copy.
    /// </summary>
    [Fact]
    public void A_Box_Is_Reported_Where_It_Stands_Rather_Than_Where_A_Copy_Of_It_Does()
    {
        const string copied = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0"
                 viewBox="0 0 80 30" width="80" height="30">
              <use href="#box" x="40" />
              <rect id="box" x="2" y="6" width="26" height="18" fill="none" e:bounds="LevelRect" />
            </svg>
            """;

        Assert.Contains("public static readonly SKRect @LevelRect = new SKRect(2f, 6f, 28f, 24f);", Build(copied, SvgTextLayout.Baked));
    }

    /// <summary>A parameter with no default leaves no defaults to measure at, and the warning says where the box is instead.</summary>
    [Fact]
    public void A_Box_Moved_By_A_Parameter_Without_A_Default_Says_It_Is_Where_It_Was_Compiled()
    {
        const string undefaulted = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0"
                 viewBox="0 0 30 30" width="30" height="30">
              <defs><e:code><e:param name="dy" type="number" default="4" /><e:param name="label" type="string" /></e:code></defs>
              <g transform="translate(0 {{ dy }})">
                <rect x="2" y="2" width="26" height="18" fill="none" e:bounds="LevelRect" />
              </g>
            </svg>
            """;

        var log = new List<string>();

        Build(undefaulted, SvgTextLayout.Baked, log);

        Assert.Contains(log, line => line.Contains("'LevelRect' moves with a parameter that has no default"));
    }

    /// <summary>A drawing built to a file of its own reports its boxes as the single file does.</summary>
    [Fact]
    public void A_Drawing_Written_On_Its_Own_Reports_Its_Boxes()
    {
        SvgcProjectBuild.Write(
            new SvgcProjectItem("Badge", Path("Badge.cs"), "Demo", "Badge", null, source: Boxed),
            new SvgcBuildSettings(),
            new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings())));

        Assert.Contains("public static readonly SKRect @LevelRect = new SKRect(2f, 6f, 28f, 24f);", File.ReadAllText(Path("Badge.cs")));
    }

    [Fact]
    public void A_Box_A_Parameter_Moves_Is_Reported_At_The_Defaults_And_Said_So()
    {
        const string moved = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0"
                 viewBox="0 0 30 30" width="30" height="30">
              <defs><e:code><e:param name="dy" type="number" default="4" /></e:code></defs>
              <g transform="translate(0 {{ dy }})">
                <rect x="2" y="2" width="26" height="18" fill="none" e:bounds="LevelRect" />
              </g>
            </svg>
            """;

        var log = new List<string>();
        var code = Build(moved, SvgTextLayout.Baked, log);

        Assert.Contains("public static readonly SKRect @LevelRect = new SKRect(2f, 6f, 28f, 24f);", code);
        Assert.Contains(log, line => line.StartsWith("warning:", StringComparison.Ordinal) && line.Contains("'LevelRect' moves with a parameter"));
    }

    [Theory]
    [InlineData("Draw")]
    [InlineData("Badge")]
    [InlineData("2Rect")]
    public void A_Box_The_Class_Cannot_Declare_Stops_The_Build(string name)
    {
        var drawing = Boxed.Replace("e:bounds=\"LevelRect\"", $"e:bounds=\"{name}\"", StringComparison.Ordinal);

        var failure = Assert.Throws<ExprException>(() => Build(drawing, SvgTextLayout.Baked));

        Assert.Contains($"'{name}'", failure.Message);
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

    /// <summary>
    /// Relaxed takes the words away from the layout that placed them, so the argument reaches the
    /// draw after all -- and says which rule it broke to get there.
    /// </summary>
    [Fact]
    public void Relaxed_Draws_From_The_Argument_And_Says_What_It_Gave_Up()
    {
        var log = new List<string>();
        var code = Build(DrivenPerSpan, SvgTextLayout.Relaxed, log);

        Assert.Contains("public static SKPicture Record(string step = null)", code);
        Assert.Contains("DrawText(step__default, ", code);

        Assert.Contains(log, line => line.Contains("relaxed text layout") && line.Contains("<tspan>"));

        // And nothing is reported frozen, because nothing was.
        Assert.DoesNotContain(log, line => line.Contains("is frozen at its default"));
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
