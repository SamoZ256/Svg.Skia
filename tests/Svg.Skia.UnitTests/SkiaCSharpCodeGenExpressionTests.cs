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
    public void Document_Without_Declarations_Keeps_The_Cached_Shape()
    {
        var code = Generate(Plain);

        Assert.Contains("public static SKPicture Picture { get; }", code);
        Assert.Contains("private static SKPicture Record()", code);
        Assert.Contains("public static void Draw(SKCanvas skCanvas)", code);
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
    public void Parameters_Become_The_Record_Signature_And_Drop_The_Static_Cache()
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

        // A picture built from arguments cannot be cached in a static field.
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
    public void A_Required_Parameter_Cannot_Follow_An_Optional_One()
    {
        // C# puts the parameters with defaults last, and reordering the list silently would
        // change what every positional call site means.
        var error = Assert.Throws<ExprException>(() => Generate("""
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
            """));

        Assert.Contains("'tint' has no default but follows 't'", error.Message);
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
}
