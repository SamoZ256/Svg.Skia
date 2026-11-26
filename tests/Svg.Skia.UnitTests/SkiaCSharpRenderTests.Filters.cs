using Svg.CodeGen.Skia;
using Xunit;

namespace Svg.Skia.UnitTests;

public partial class SkiaCSharpRenderTests
{
    // No filter region, so no primitive has a crop rect: the emitter has to pick each call's overload without one.
    [Theory]
    [InlineData("Blur", """<feGaussianBlur stdDeviation="1" />""")]
    [InlineData("ColorMatrix", """<feColorMatrix type="hueRotate" values="90" />""")]
    [InlineData("ComponentTransfer", """<feComponentTransfer><feFuncR type="linear" slope="0.5" /></feComponentTransfer>""")]
    [InlineData("Morphology", """<feMorphology operator="dilate" radius="1" />""")]
    [InlineData("Offset", """<feOffset dx="2" dy="1" />""")]
    [InlineData("Merge", """<feOffset dx="2" result="o" /><feMerge><feMergeNode in="SourceGraphic" /><feMergeNode in="o" /></feMerge>""")]
    [InlineData("Blend", """<feFlood flood-color="#00f" flood-opacity="0.5" result="f" /><feBlend in="SourceGraphic" in2="f" mode="multiply" />""")]
    public void A_Filter_Without_A_Region_Compiles_And_Draws_As_The_Runtime_Does(string name, string primitives)
    {
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <defs><filter id="f">{primitives}</filter></defs>
              <rect x="4" y="4" width="12" height="12" fill="#f00" filter="url(#f)" />
            </svg>
            """;

        AssertRendersTheSame($"Filter{name}_V3", svg, SkiaSharpTarget.V3);
        AssertRendersTheSame($"Filter{name}_V4", svg, SkiaSharpTarget.V4);
    }
}
