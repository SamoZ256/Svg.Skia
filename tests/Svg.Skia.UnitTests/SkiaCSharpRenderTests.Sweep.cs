using System;
using System.Linq;
using SkiaSharp;
using Svg.CodeGen.Skia;
using Xunit;

namespace Svg.Skia.UnitTests;

public partial class SkiaCSharpRenderTests
{
    // Two blank pictures agree as well as two sweeps do, so each row first finds a sweep in the model it compares.
    private static ShimSkiaSharp.SweepGradientShader[] Sweeps(string svgMarkup)
        => SkiaCSharpCodeGenTests.EnumerateShaders(Model(svgMarkup)).OfType<ShimSkiaSharp.SweepGradientShader>().ToArray();

    private static string WithSweep(string svgMarkup)
    {
        Assert.NotEmpty(Sweeps(svgMarkup));
        return svgMarkup;
    }

    private const string SweepFill = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <sweepGradient id="s" gradientUnits="userSpaceOnUse" cx="12" cy="12">
              <stop offset="0" stop-color="#ff2323" />
              <stop offset="0.1667" stop-color="#ffff23" />
              <stop offset="0.3333" stop-color="#23ff23" stop-opacity="0.5" />
              <stop offset="0.5" stop-color="#23ffff" />
              <stop offset="0.6667" stop-color="#2323ff" />
              <stop offset="0.8333" stop-color="#ff23ff" />
              <stop offset="1" stop-color="#ff2323" />
            </sweepGradient>
          </defs>
          <rect x="1" y="1" width="22" height="22" fill="url(#s)" />
        </svg>
        """;

    [Theory]
    [InlineData(SkiaSharpTarget.V3)]
    [InlineData(SkiaSharpTarget.V4)]
    public void A_Sweep_Gradient_Fills_A_Shape(SkiaSharpTarget skiaSharp)
        => AssertRendersTheSame($"SweepFill_{skiaSharp}", WithSweep(SweepFill), skiaSharp);

    [Fact]
    public void A_Written_Sweep_Gradient_Is_A_Static()
    {
        var code = Code(Model(SweepFill), "WrittenSweep", SvgBody.Direct);
        var created = code.IndexOf("SKShader.CreateSweepGradient(", StringComparison.Ordinal);

        Assert.Equal(1, code.Split("SKShader.CreateSweepGradient(").Length - 1);
        Assert.InRange(created, 0, code.IndexOf("public static void Draw(", StringComparison.Ordinal));
        Assert.DoesNotContain("s_skGradient", code);
    }

    [Fact]
    public void A_Sweep_Gradient_Strokes_An_Arc()
        => AssertRendersTheSame("SweepStroke", WithSweep("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <sweepGradient id="s" gradientUnits="userSpaceOnUse" cx="12" cy="12">
                  <stop offset="0" stop-color="#1e40af" />
                  <stop offset="0.5" stop-color="#facc15" />
                  <stop offset="1" stop-color="#dc2626" />
                </sweepGradient>
              </defs>
              <path d="M 4 16 A 9 9 0 1 1 20 16" fill="none" stroke="url(#s)" stroke-width="3" stroke-linecap="round" />
            </svg>
            """));

    // The last row is degenerate and goes to Skia as written, which draws the first colour up to 90
    // degrees and the last beyond.
    [Theory]
    [InlineData(45f, 270f, "pad")]
    [InlineData(45f, 270f, "reflect")]
    [InlineData(45f, 270f, "repeat")]
    [InlineData(90f, 90f, "pad")]
    public void A_Sweep_Gradient_Covers_Only_Its_Angles(float start, float end, string spread)
    {
        var svgMarkup = $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <sweepGradient id="s" gradientUnits="userSpaceOnUse" cx="12" cy="12" startAngle="{Written(start)}" endAngle="{Written(end)}" spreadMethod="{spread}">
                  <stop offset="0" stop-color="#1e40af" />
                  <stop offset="0.5" stop-color="#facc15" />
                  <stop offset="1" stop-color="#dc2626" />
                </sweepGradient>
              </defs>
              <rect x="0" y="0" width="24" height="24" fill="url(#s)" />
            </svg>
            """;

        var sweep = Assert.Single(Sweeps(svgMarkup));
        Assert.Equal((start, end), (sweep.StartAngle, sweep.EndAngle));
        AssertRendersTheSame(Named("SweepAngles", start, end) + "_" + spread, svgMarkup);
    }

    [Fact]
    public void A_Sweep_Gradient_Turns_With_Its_Gradient_Transform()
        => AssertRendersTheSame("SweepTurned", WithSweep("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <sweepGradient id="s" gradientUnits="userSpaceOnUse" cx="12" cy="12" gradientTransform="rotate(-90 12 12)">
                  <stop offset="0" stop-color="#1e40af" />
                  <stop offset="0.5" stop-color="#facc15" />
                  <stop offset="1" stop-color="#dc2626" />
                </sweepGradient>
              </defs>
              <rect x="0" y="0" width="24" height="24" fill="url(#s)" />
            </svg>
            """));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_Sweep_Gradient_In_Bounding_Box_Units(bool turned)
        => AssertRendersTheSame(turned ? "SweepBoxTurned" : "SweepBox", WithSweep($"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <defs>
                <sweepGradient id="s"{(turned ? " gradientTransform=\"rotate(30 .5 .5)\"" : "")}>
                  <stop offset="0" stop-color="#1e40af" />
                  <stop offset="0.5" stop-color="#facc15" />
                  <stop offset="1" stop-color="#dc2626" />
                </sweepGradient>
              </defs>
              <rect x="2" y="2" width="20" height="8" fill="url(#s)" />
              <ellipse cx="12" cy="17" rx="8" ry="4" fill="none" stroke="url(#s)" stroke-width="2" />
            </svg>
            """));

    private static string DrivenSweep(string parameter, string stop) => $"""
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code>{parameter}</e:code>
            <sweepGradient id="s" gradientUnits="userSpaceOnUse" cx="12" cy="12">
              <stop offset="0" stop-color="#facc15" />
              {stop}
              <stop offset="1" stop-color="#facc15" />
            </sweepGradient>
          </defs>
          <rect x="0" y="0" width="24" height="24" fill="url(#s)" />
        </svg>
        """;

    [Fact]
    public void A_Sweep_Gradient_Stop_Takes_An_Expression()
        => AssertExpressionsRenderTheSame(
            "ExprSweepStop",
            WithSweep(DrivenSweep("""<e:param name="tint" type="color" default="#1e40af" />""", """<stop offset="0.5" stop-color="{{ tint }}" />""")),
            arguments: new object?[] { new SKColor(0xdc, 0x26, 0x26) },
            expectedMarkup: DrivenSweep("", """<stop offset="0.5" stop-color="#dc2626" />"""));

    [Fact]
    public void A_Sweep_Gradient_Stop_Opacity_Takes_An_Expression()
        => AssertExpressionsRenderTheSame(
            "ExprSweepStopOpacity",
            WithSweep(DrivenSweep("""<e:param name="fade" type="number" default="1" />""", """<stop offset="0.5" stop-color="#1e40af" stop-opacity="{{ fade }}" />""")),
            arguments: new object?[] { 0.4f },
            expectedMarkup: DrivenSweep("", """<stop offset="0.5" stop-color="#1e40af" stop-opacity="0.4" />"""));

    private const string LevelSweep = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code><e:param name="level" type="number" default="0.5" /></e:code>
            <sweepGradient id="s" gradientUnits="userSpaceOnUse" cx="12" cy="12" gradientTransform="rotate(-90 12 12)">
              <stop offset="0" stop-color="#ed2312" />
              <stop offset="0.5" stop-color="{{ level > 0.5 ? #c23a90 : #4eb7e2 }}" stop-opacity="{{ level }}" />
              <stop offset="1" stop-color="#ed2312" />
            </sweepGradient>
          </defs>
          <circle cx="12" cy="12" r="9" fill="none" stroke="url(#s)" stroke-width="3" />
        </svg>
        """;

    [Theory]
    [InlineData(0.3f)]
    [InlineData(1f)]
    public void SkiaSharp3_Builds_A_Driven_Sweep_Gradient_As_SkiaSharp4_Does(float level)
        => AssertBindsTheSame(Named("LevelSweepV3", level), WithSweep(LevelSweep), new object?[] { level }, SkiaSharpTarget.V3);
}
