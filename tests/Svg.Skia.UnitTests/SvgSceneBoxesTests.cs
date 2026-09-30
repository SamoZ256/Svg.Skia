// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;
using System.Linq;
using Svg.Expressions;
using Svg.Model;
using Svg.Model.Services;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>A drawing's <c>e:bounds</c> boxes, measured the way a build reports them and an editor shows them.</summary>
public class SvgSceneBoxesTests
{
    private const string Moved = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 30 30" width="30" height="30">
          <defs><e:code><e:param name="dy" type="number" default="4" /></e:code></defs>
          <g transform="translate(0 {{ dy }})">
            <rect x="2" y="2" width="26" height="18" fill="none" e:bounds="LevelRect" />
          </g>
        </svg>
        """;

    /// <summary>
    /// An editor's scene is bound to whatever the sliders say, and the class has only the defaults:
    /// the box is reported where the defaults put it, and the scene is left where it was bound.
    /// </summary>
    [Fact]
    public void A_Driven_Box_Is_Measured_At_The_Defaults_Without_Moving_The_Scene()
    {
        var document = SvgService.FromSvg(Moved)!;

        Assert.True(SvgSceneRuntime.TryCompile(document, new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings())), DrawAttributes.None, out var scene));

        scene!.ApplyExpressionTransforms(ExprEvaluator.Create(
            document.ExpressionDeclarations,
            new Dictionary<string, ExprValue>(System.StringComparer.Ordinal) { ["dy"] = ExprValue.Number(10f) }));

        var box = Assert.Single(SvgSceneBoxes.Measure(document, scene));

        Assert.True(box.Driven);
        Assert.Equal((2f, 6f, 28f, 24f), (box.Rect!.Value.Left, box.Rect.Value.Top, box.Rect.Value.Right, box.Rect.Value.Bottom));

        // Still where the slider put it.
        Assert.Equal(12f, box.Node!.TransformedBounds.Top);
    }

    [Fact]
    public void A_Box_Under_Defs_Is_Not_The_Drawings()
    {
        var document = SvgService.FromSvg("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 30 30" width="30" height="30">
              <defs><rect id="copy" width="5" height="5" fill="none" e:bounds="SymbolRect" /></defs>
              <rect x="2" y="6" width="26" height="18" fill="none" e:bounds="LevelRect" />
            </svg>
            """)!;

        Assert.Equal(new[] { "LevelRect" }, SvgSceneBoxes.Marked(document).Select(marked => marked.Name));
    }
}
