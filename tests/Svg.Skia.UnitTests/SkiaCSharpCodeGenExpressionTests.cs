using System.Linq;
using ShimSkiaSharp;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Expressions;
using Svg.Expressions;
using Svg.Model.Services;
using Xunit;

namespace Svg.Skia.UnitTests;

public class SkiaCSharpCodeGenExpressionTests
{
    private const string Ns = SvgExpressionDeclarations.Namespace;

    private static string Generate(string svgMarkup)
    {
        var document = SvgService.FromSvg(svgMarkup);
        Assert.NotNull(document);
        var assetLoader = new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings()));
        var picture = SvgSceneRuntime.CreateModel(document!, assetLoader);
        Assert.NotNull(picture);

        return SkiaCSharpCodeGen.Generate(picture!, "Svg", "Generated", SvgExpressionDeclarations.Parse(svgMarkup));
    }

    private const string Plain = """
        <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
          <rect x="0" y="0" width="10" height="10" fill="#808080" />
        </svg>
        """;

    [Fact]
    public void Document_Without_Declarations_Keeps_No_Picture()
    {
        var code = Generate(Plain);

        Assert.Contains("public static SKPicture Record()", code);
        Assert.Contains("public static void Draw(SKCanvas skCanvas)", code);
        Assert.Contains("using (var skPicture = Record())", code);
        Assert.DoesNotContain("SKPicture Picture", code);
        Assert.DoesNotContain("static Generated()", code);
        Assert.Contains("new SKColor(128, 128, 128, 255)", code);
    }

    [Fact]
    public void Old_And_New_Generate_Overloads_Agree_When_There_Are_No_Declarations()
    {
        var document = SvgService.FromSvg(Plain);
        var assetLoader = new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings()));
        var picture = SvgSceneRuntime.CreateModel(document!, assetLoader);

        Assert.Equal(
            SkiaCSharpCodeGen.Generate(picture!, "Svg", "Generated"),
            SkiaCSharpCodeGen.Generate(picture!, "Svg", "Generated", SvgExpressionDeclarations.Empty));
    }

    [Fact]
    public void No_Helpers_Are_Emitted_When_Unused()
    {
        var code = Generate(Plain);

        Assert.DoesNotContain("SvgScaleAlpha", code);
        Assert.DoesNotContain("SvgToLinearRgb", code);
        Assert.DoesNotContain("SvgToColorF", code);
        Assert.DoesNotContain("SvgHsl", code);
    }

    [Fact]
    public void Parameters_Become_The_Record_Signature()
    {
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs>
                <e:code>
                  <e:param name="t" type="number" default="0" />
                  <e:param name="bold" type="boolean" default="false" />
                  <e:let name="c">hsl(200, 50%, bold ? 60% : 40%)</e:let>
                </e:code>
              </defs>
              <rect x="0" y="0" width="10" height="10" fill="{{ c }}" />
            </svg>
            """);

        Assert.Contains("public static SKPicture Record(float t = 0f, bool bold = false)", code);
        Assert.Contains("public static void Draw(SKCanvas skCanvas, float t = 0f, bool bold = false)", code);
        // The picture is built for this call alone, so Draw owns it and disposes it.
        Assert.Contains("using (var skPicture = Record(t, bold))", code);
        Assert.Contains("skCanvas.DrawPicture(skPicture);", code);

        Assert.DoesNotContain("public static SKPicture Picture { get; }", code);
        Assert.DoesNotContain("static Generated()", code);
    }

    [Fact]
    public void A_Parameter_Without_A_Default_Is_Required()
    {
        // Inventing a default would make every argument skippable and put a value in the
        // signature that appears nowhere in the document.
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code><e:param name="tint" type="color" /></e:code></defs>
              <rect x="0" y="0" width="10" height="10" fill="{{ tint }}" />
            </svg>
            """);

        Assert.Contains("public static SKPicture Record(SKColor tint)", code);
        Assert.Contains("public static void Draw(SKCanvas skCanvas, SKColor tint)", code);
    }

    [Fact]
    public void A_String_Parameter_With_A_Default_Reaches_The_Body_Through_A_Local()
    {
        // A string default is a C# constant only while it stays a literal -- `upper('a')` compiles
        // to a call -- so every one takes the local rather than the generator deciding constness of
        // its own output. No `?` on the parameter: string is already a reference type, and the
        // annotation would warn (CS8632) in a project with nullable annotations off.
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code>
                <e:param name="theme" type="string" default="'dark'" />
                <e:let name="ink">theme == 'dark' ? #ffffff : #101010</e:let>
              </e:code></defs>
              <rect x="0" y="0" width="10" height="10" fill="{{ ink }}" />
            </svg>
            """);

        Assert.Contains("public static SKPicture Record(string theme = null)", code);
        Assert.Contains("string theme__default = theme ?? \"dark\";", code);
        Assert.Contains("theme__default ==", code);
        Assert.Contains("public static void Draw(SKCanvas skCanvas, string theme = null)", code);
    }

    [Fact]
    public void A_String_Function_Pulls_Its_Helper_Into_The_Generated_Class()
    {
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code>
                <e:param name="theme" type="string" default="'DARK'" />
                <e:let name="ink">lower(theme) == 'dark' ? #ffffff : #101010</e:let>
              </e:code></defs>
              <rect x="0" y="0" width="10" height="10" fill="{{ ink }}" />
            </svg>
            """);

        Assert.Contains("private static string SvgLower(string value) => value.ToLowerInvariant();", code);
        Assert.Contains("SvgLower(theme__default)", code);
        Assert.DoesNotContain("SvgUpper", code);
    }

    [Fact]
    public void A_Colour_Parameter_With_A_Default_Becomes_Nullable()
    {
        // `new SKColor(...)` is not a compile-time constant and cannot be a C# argument
        // default (CS1736), so the parameter goes nullable and the real default is coalesced into a
        // local. The body reads that local rather than the parameter, because C# will not let a
        // local shadow the parameter it is derived from.
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code><e:param name="tint" type="color" default="#ff0000" /></e:code></defs>
              <rect x="0" y="0" width="10" height="10" fill="{{ tint }}" />
            </svg>
            """);

        Assert.Contains("public static SKPicture Record(SKColor? tint = null)", code);
        Assert.Contains("SKColor tint__default = tint ?? new SKColor(255, 0, 0, 255);", code);
        Assert.Contains("skPaint0.Color = tint__default;", code);

        // Draw mirrors the signature and forwards the nullable, not the local.
        Assert.Contains("public static void Draw(SKCanvas skCanvas, SKColor? tint = null)", code);
    }

    [Fact]
    public void A_Declared_Range_Does_Not_Change_The_Generated_Signature()
    {
        // min/max/step are advice to a host about a slider. The generator has no use for them and
        // must emit exactly what it emits without them, or adding a range to a document would
        // silently move every generated file.
        const string body = """
              <rect x="0" y="0" width="10" height="10" fill="{{ hsl(hue, 74%, 55%) }}" />
            """;

        var ranged = Generate($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code><e:param name="hue" type="number" default="217" min="0" max="360" step="1" /></e:code></defs>
            {body}
            </svg>
            """);

        var plain = Generate($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code><e:param name="hue" type="number" default="217" /></e:code></defs>
            {body}
            </svg>
            """);

        Assert.Contains("public static SKPicture Record(float hue = 217f)", ranged);
        Assert.Equal(plain, ranged);
    }

    [Fact]
    public void A_Colour_Parameter_Without_A_Default_Is_Unchanged()
    {
        // The nullable shape is only for the ones carrying a default. Everything else keeps the
        // signature it has always had, so no existing generated output moves.
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code><e:param name="tint" type="color" /></e:code></defs>
              <rect x="0" y="0" width="10" height="10" fill="{{ tint }}" />
            </svg>
            """);

        Assert.Contains("public static SKPicture Record(SKColor tint)", code);
        Assert.DoesNotContain("SKColor?", code);
        Assert.DoesNotContain("__default", code);
        Assert.Contains("skPaint0.Color = tint;", code);
    }

    [Fact]
    public void A_Required_Parameter_After_An_Optional_One_Makes_Every_Argument_Required()
    {
        // C# puts the parameters with defaults last, and the document's order is the signature's --
        // reordering would change what a positional call means. So the order is kept and the
        // defaults are given up: `t` loses its 0 rather than `tint` moving in front of it.
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs>
                <e:code>
                  <e:param name="t" type="number" default="0" />
                  <e:param name="tint" type="color" />
                  <e:let name="c">withAlpha(tint, t)</e:let>
                </e:code>
              </defs>
              <rect x="0" y="0" width="10" height="10" fill="{{ c }}" />
            </svg>
            """);

        Assert.Contains("public static SKPicture Record(float t, SKColor tint)", code);
        Assert.DoesNotContain("float t = ", code);
        Assert.Contains("Every argument is required", code);
    }

    [Fact]
    public void A_Colour_Default_Loses_Its_Local_Along_With_Its_Default()
    {
        // The one that would break quietly. A colour default is normally emitted as `SKColor?` and
        // coalesced into a local, because `new SKColor(...)` cannot be an argument default (CS1736).
        // Once the colour is required that local has nothing to coalesce -- `tint ?? ...` on a
        // non-nullable is CS0019 -- and the body has to name the parameter instead, or it names a
        // local that was never declared (CS0103). Both faults would land in the generated file.
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs>
                <e:code>
                  <e:param name="tint" type="color" default="#ff0000" />
                  <e:param name="t" type="number" />
                  <e:let name="c">withAlpha(tint, t)</e:let>
                </e:code>
              </defs>
              <rect x="0" y="0" width="10" height="10" fill="{{ c }}" />
            </svg>
            """);

        Assert.Contains("public static SKPicture Record(SKColor tint, float t)", code);
        Assert.DoesNotContain("SKColor?", code);
        Assert.DoesNotContain("__default", code);
    }

    [Fact]
    public void A_Document_That_Fits_Keeps_Its_Defaults_And_Its_Colour_Local()
    {
        // The same parameters the other way round, which C# can take: nothing about this changes.
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs>
                <e:code>
                  <e:param name="t" type="number" />
                  <e:param name="tint" type="color" default="#ff0000" />
                  <e:let name="c">withAlpha(tint, t)</e:let>
                </e:code>
              </defs>
              <rect x="0" y="0" width="10" height="10" fill="{{ c }}" />
            </svg>
            """);

        Assert.Contains("SKColor? tint = null", code);
        Assert.Contains("__default", code);
        Assert.DoesNotContain("Every argument is required", code);
    }

    [Fact]
    public void Required_Parameters_May_Precede_Optional_Ones()
    {
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs>
                <e:code>
                  <e:param name="tint" type="color" />
                  <e:param name="t" type="number" default="0" />
                  <e:let name="c">withAlpha(tint, t)</e:let>
                </e:code>
              </defs>
              <rect x="0" y="0" width="10" height="10" fill="{{ c }}" />
            </svg>
            """);

        Assert.Contains("public static SKPicture Record(SKColor tint, float t = 0f)", code);
    }

    [Fact]
    public void Lets_Are_Emitted_As_Typed_Locals_In_Declaration_Order()
    {
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs>
                <e:code>
                  <e:param name="t" type="number" default="0" />
                  <e:let name="wave">sin(t)</e:let>
                  <e:let name="c">hsl(wave * 360, 50%, 50%)</e:let>
                </e:code>
              </defs>
              <rect x="0" y="0" width="10" height="10" fill="{{ c }}" />
            </svg>
            """);

        var wave = code.IndexOf("float wave = MathF.Sin(t);", System.StringComparison.Ordinal);
        var c = code.IndexOf("SKColor c = SvgHsl(", System.StringComparison.Ordinal);

        Assert.True(wave >= 0, "Expected a typed local for the number let.");
        Assert.True(c > wave, "A let may reference an earlier one, so order must be preserved.");
    }

    [Fact]
    public void Fill_Expression_Replaces_The_Literal_Color()
    {
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs>
                <e:code><e:param name="tint" type="color" /></e:code>
              </defs>
              <rect x="0" y="0" width="10" height="10" fill="{{ tint }}" />
            </svg>
            """);

        Assert.Contains("Color = tint;", code);
        Assert.DoesNotContain("new SKColor(128, 128, 128, 255)", code);
    }

    [Fact]
    public void A_Paint_Expression_Must_Be_A_Colour()
    {
        var error = Assert.Throws<ExprException>(() => Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code><e:param name="t" type="number" default="0" /></e:code></defs>
              <rect x="0" y="0" width="10" height="10" fill="{{ t }}" />
            </svg>
            """));

        Assert.Contains("must be a colour expression", error.Message);
    }

    [Fact]
    public void Opacity_Emits_The_ScaleAlpha_Helper_Once()
    {
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code><e:param name="tint" type="color" /></e:code></defs>
              <rect x="0" y="0" width="10" height="10" fill="{{ tint }}" fill-opacity="0.5" />
              <rect x="20" y="0" width="10" height="10" fill="{{ tint }}" fill-opacity="0.25" />
            </svg>
            """);

        Assert.Contains("SvgScaleAlpha(tint, 0.5f)", code);
        Assert.Contains("SvgScaleAlpha(tint, 0.25f)", code);
        Assert.Equal(1, CountOccurrences(code, "private static SKColor SvgScaleAlpha"));
    }

    [Fact]
    public void A_Fill_Opacity_Expression_Scales_The_Literal_Colour()
    {
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code><e:param name="fade" type="number" default="1" /></e:code></defs>
              <rect x="0" y="0" width="10" height="10" fill="#3366cc" fill-opacity="{{ fade }}" />
            </svg>
            """);

        // The colour the author wrote is emitted as a literal and scaled by the argument, rather
        // than the fade being folded into a constant alpha at generation time.
        Assert.Contains("SvgScaleAlpha(new SKColor(51, 102, 204, 255), fade)", code);
        Assert.Contains("private static SKColor SvgScaleAlpha", code);
    }

    [Fact]
    public void A_Stop_Opacity_Expression_Reaches_The_Gradient_Stop()
    {
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs>
                <e:code><e:param name="fade" type="number" default="1" /></e:code>
                <linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="100" y2="0">
                  <stop offset="0%" stop-color="#3366cc" stop-opacity="{{ fade }}" />
                  <stop offset="100%" stop-color="#000000" />
                </linearGradient>
              </defs>
              <rect x="0" y="0" width="100" height="100" fill="url(#g)" />
            </svg>
            """);

        Assert.Contains("SvgToColorF(SvgScaleAlpha(new SKColor(51, 102, 204, 255), fade))", code);
    }

    [Fact]
    public void Gradient_Stop_Expression_Is_Converted_To_ColorF()
    {
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs>
                <e:code><e:param name="tint" type="color" /></e:code>
                <linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="100" y2="0">
                  <stop offset="0%" stop-color="{{ tint }}" />
                  <stop offset="100%" stop-color="#000000" />
                </linearGradient>
              </defs>
              <rect x="0" y="0" width="100" height="100" fill="url(#g)" />
            </svg>
            """);

        Assert.Contains("SvgToColorF(tint)", code);
        Assert.Contains("private static SKColorF SvgToColorF", code);
    }

    [Fact]
    public void Filter_Colours_Take_An_Expression()
    {
        // Both already emit through ToSKColor, so what this pins is that the expression survives
        // the compile into a filter rather than being folded to a literal on the way.
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs>
                <e:code>
                  <e:param name="wash" type="color" />
                  <e:param name="lamp" type="color" />
                </e:code>
                <filter id="f" x="0" y="0" width="100" height="100">
                  <feFlood flood-color="{{ wash }}" result="flooded" />
                  <feDiffuseLighting lighting-color="{{ lamp }}" surfaceScale="2" in="flooded">
                    <feDistantLight azimuth="45" elevation="60" />
                  </feDiffuseLighting>
                </filter>
              </defs>
              <rect x="0" y="0" width="100" height="100" fill="#808080" filter="url(#f)" />
            </svg>
            """);

        Assert.Contains("Color = wash", code);
        Assert.Contains("CreateDistantLitDiffuse", code);
        Assert.Contains("    lamp,", code);
    }

    [Fact]
    public void A_Helper_Used_Only_By_A_Let_Is_Still_Emitted()
    {
        // The helper scan has to cover the lets, not just the recorded picture body.
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs>
                <e:code>
                  <e:param name="t" type="number" default="0" />
                  <e:let name="c">hsl(t, 50%, 50%)</e:let>
                </e:code>
              </defs>
              <rect x="0" y="0" width="10" height="10" fill="{{ c }}" />
            </svg>
            """);

        Assert.Contains("private static SKColor SvgHsl", code);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    [Fact]
    public void A_Range_That_Folds_To_False_Is_Not_Emitted()
    {
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <rect x="0" y="0" width="10" height="10" fill="#123456" visibility="{{ !true }}" />
              <rect x="20" y="0" width="10" height="10" fill="#808080" />
            </svg>
            """);

        Assert.DoesNotContain("if (", code);
        Assert.DoesNotContain("new SKColor(18, 52, 86, 255)", code);
        Assert.Contains("new SKColor(128, 128, 128, 255)", code);
    }

    [Fact]
    public void A_Range_That_Folds_To_True_Loses_Its_If_And_Its_Let()
    {
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code><e:let name="shown">1 &lt; 2</e:let></e:code></defs>
              <rect x="0" y="0" width="10" height="10" fill="#123456" visibility="{{ shown }}" />
            </svg>
            """);

        Assert.DoesNotContain("if (", code);
        Assert.DoesNotContain("bool shown", code);
        Assert.Contains("new SKColor(18, 52, 86, 255)", code);
    }

    [Fact]
    public void Only_The_Folded_Range_Of_A_Nested_Pair_Goes()
    {
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code><e:param name="hot" type="boolean" default="true" /></e:code></defs>
              <g visibility="{{ hot }}">
                <rect x="0" y="0" width="10" height="10" fill="#123456" visibility="{{ 1 > 2 }}" />
                <rect x="20" y="0" width="10" height="10" fill="#654321" visibility="{{ 2 > 1 }}" />
              </g>
              <g visibility="{{ false }}">
                <rect x="40" y="0" width="10" height="10" fill="#abcdef" visibility="{{ hot }}" />
              </g>
            </svg>
            """);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(code, @"if \(hot\)"));
        Assert.DoesNotContain("new SKColor(18, 52, 86, 255)", code);
        Assert.Contains("new SKColor(101, 67, 33, 255)", code);
        Assert.DoesNotContain("new SKColor(171, 205, 239, 255)", code);
    }

    [Theory]
    // Dropped from the output, but still checked: an `if (false)` around these failed the build, and
    // a typo behind a debug flag must not wait for the flag to flip.
    [InlineData("""<rect width="10" height="10" fill="{{ nosuch }}" visibility="{{ false }}" />""", "Unknown name 'nosuch'")]
    [InlineData("""<g display="{{ 1 > 2 }}"><rect width="10" height="10" visibility="{{ 3 }}" /></g>""", "must be a boolean")]
    public void A_Range_That_Folds_To_False_Is_Still_Checked(string content, string error)
    {
        var exception = Assert.Throws<ExprException>(() => Generate($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              {content}
            </svg>
            """));

        Assert.Contains(error, exception.Message);
    }

    // ---- lists -------------------------------------------------------------------------------

    [Fact]
    public void A_List_Parameter_With_A_Default_Reads_A_Field_Built_Once()
    {
        // No `?`: a list is a reference, and like a string's the annotation would warn (CS8632)
        // where nullable annotations are off. A host's List<float> passes as it is.
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code>
                <e:param name="positions" type="number[]" default="[0, 0.5, 1]" />
                <e:param name="i" type="integer" default="1" />
              </e:code></defs>
              <path d="M 0 0 L 10 0" stroke="#000" stroke-width="{{ positions[i] * 4 }}" />
            </svg>
            """);

        Assert.Contains("using System.Collections.Generic;", code);
        Assert.Contains("public static SKPicture Record(IReadOnlyList<float> positions = null, int i = 1)", code);
        Assert.Contains("public static void Draw(SKCanvas skCanvas, IReadOnlyList<float> positions = null, int i = 1)", code);
        Assert.Contains("private static readonly IReadOnlyList<float> s_skList0 = new float[] { 0f, 0.5f, 1f };", code);
        Assert.Contains("IReadOnlyList<float> positions__default = positions ?? s_skList0;", code);
        Assert.Contains("(SvgAt(positions__default, i) * 4f)", code);
        Assert.Contains("private static T SvgAt<T>(IReadOnlyList<T> list, int index)", code);
        Assert.DoesNotContain("SvgList", code);

        // A field initialised where it is declared needs no static constructor to set it.
        Assert.DoesNotContain("static Generated()", code);
    }

    [Fact]
    public void A_List_Parameter_Without_A_Default_Is_Required_And_Null_Reads_As_Empty()
    {
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code><e:param name="colors" type="color[]" /></e:code></defs>
              <path d="M 0 0 H 10 V 10 Z" fill="{{ colors[len(colors) - 1] }}" />
            </svg>
            """);

        Assert.Contains("public static SKPicture Record(IReadOnlyList<SKColor> colors)", code);
        Assert.Contains("IReadOnlyList<SKColor> colors__default = colors ?? Array.Empty<SKColor>();", code);
        Assert.Contains("SvgAt(colors__default, (colors__default.Count - 1))", code);
    }

    [Fact]
    public void A_Required_List_Keeps_Its_Default_For_A_Null()
    {
        // The defaults leave the signature, since t has none and follows; a null list still falls
        // back to what the document declared.
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code>
                <e:param name="positions" type="number[]" default="[0, 1]" />
                <e:param name="t" type="number" />
              </e:code></defs>
              <path d="M 0 0 L 10 0" stroke="#000" stroke-width="{{ positions[1] + t }}" />
            </svg>
            """);

        Assert.Contains("public static SKPicture Record(IReadOnlyList<float> positions, float t)", code);
        Assert.Contains("private static readonly IReadOnlyList<float> s_skList0 = new float[] { 0f, 1f };", code);
        Assert.Contains("IReadOnlyList<float> positions__default = positions ?? s_skList0;", code);
    }

    [Fact]
    public void A_List_Let_Is_A_Typed_Local()
    {
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code>
                <e:param name="tint" type="color" />
                <e:param name="i" type="integer" />
                <e:let name="ramp">[tint, mix(tint, #fff, 0.5)]</e:let>
                <e:let name="none" type="color[]">[]</e:let>
                <e:let name="chosen">i > 0 ? ramp : none</e:let>
              </e:code></defs>
              <path d="M 0 0 H 10 V 10 Z" fill="{{ chosen[i] }}" />
            </svg>
            """);

        Assert.Contains("IReadOnlyList<SKColor> ramp = SvgList(ref s_skList0, stackalloc SKColor[] { tint, SvgMix(tint, new SKColor(255, 255, 255, 255), 0.5f) });", code);
        Assert.Contains("IReadOnlyList<SKColor> chosen = ((i > 0) ? ramp : s_skList1);", code);
        Assert.Contains("private static readonly IReadOnlyList<SKColor> s_skList1 = Array.Empty<SKColor>();", code);
        Assert.Contains("[ThreadStatic]", code);
        Assert.Contains("private static SKColor[] s_skList0;", code);
        Assert.Contains("private static IReadOnlyList<T> SvgList<T>(ref T[] list, Span<T> elements)", code);
    }

    [Fact]
    public void A_Constant_List_Is_One_Field_However_Often_It_Is_Written()
    {
        var code = Generate("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code>
                <e:param name="i" type="integer" default="0" />
                <e:let name="stops">[1, 2, 3]</e:let>
              </e:code></defs>
              <path d="M 0 0 L 10 0" stroke="#000" stroke-width="{{ stops[i] }}" opacity="{{ [1, 2, 3][i] / 3 }}" />
              <path d="M 0 5 L 10 5" stroke="#000" stroke-width="{{ stops[1] }}" />
            </svg>
            """);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(code, @"new float\[\] \{ 1f, 2f, 3f \}"));
        Assert.Contains("private static readonly IReadOnlyList<float> s_skList0 = new float[] { 1f, 2f, 3f };", code);
        Assert.Contains("SvgAt(s_skList0, i)", code);

        // A known index into a known list is its element.
        Assert.Contains("StrokeWidth = 2f;", code);
        Assert.DoesNotContain("stops", code);
    }

    [Fact]
    public void A_Document_With_No_List_Needs_No_Collections()
        => Assert.DoesNotContain("System.Collections.Generic", Generate(Plain));

    [Fact]
    public void A_Drawing_Named_Like_A_Generic_Helper_It_Calls_Is_Refused()
    {
        // The helper's name is followed by its type parameter rather than its arguments, which the
        // reader of helper members has to see past.
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
              <defs><e:code><e:param name="xs" type="number[]" /></e:code></defs>
              <path d="M 0 0 L 10 0" stroke="#000" stroke-width="{{ xs[0] }}" />
            </svg>
            """;
        var document = SvgService.FromSvg(svg)!;
        var picture = SvgSceneRuntime.CreateModel(document, new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings())))!;

        var error = Assert.Throws<ExprException>(
            () => SkiaCSharpCodeGen.GenerateFile(new[] { new SkiaCSharpDrawing(picture, "Icons", "SvgAt", SvgExpressionDeclarations.Parse(svg)) }));

        Assert.Contains("'SvgAt' is a helper", error.Message);
        Assert.Equal(new[] { "SvgList" }, ExprHelpers.Members(ExprHelpers.All.First(helper => helper.Key == ExprHelpers.ListOf).Value));
    }
}
