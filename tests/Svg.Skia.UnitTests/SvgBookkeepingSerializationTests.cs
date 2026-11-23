// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// What the loader records beside an expression stays in memory and out of the file.
/// </summary>
public class SvgBookkeepingSerializationTests
{
    private const string Markup = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 40 20" width="40" height="20">
          <defs><e:code><e:param name="tint" type="color" default="#1e40af" /></e:code></defs>
          <rect id="literal" width="10" height="10" style="fill:#ff0000" />
          <rect id="driven" x="20" width="10" height="10" fill="{{ tint }}" />
        </svg>
        """;

    [Fact]
    public void A_Provenance_Key_Is_Kept_And_Cloned_But_Never_Written()
    {
        using var svg = new SKSvg();
        Assert.NotNull(svg.FromSvg(Markup));

        var document = svg.SourceDocument!;
        var literal = document.GetElementById("literal");
        var driven = document.GetElementById("driven");
        var fromKey = SvgExpressionAttributes.SourceKeyFor("fill");

        Assert.True(literal.CustomAttributes.ContainsKey(fromKey));
        Assert.True(driven.CustomAttributes.ContainsKey(fromKey));
        Assert.True(literal.DeepCopy().CustomAttributes.ContainsKey(fromKey));

        foreach (var xml in new[] { document.GetXML(), literal.GetXML(), driven.GetXML() })
        {
            Assert.DoesNotContain(":from=", xml);
            Assert.DoesNotContain("expr/1.0:fill", xml);
        }

        Assert.Contains("style=\"fill:#FF0000;\"", literal.GetXML());
    }
}
