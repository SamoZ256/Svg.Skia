// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Svg.PaintCode.UnitTests;

/// <summary>
/// Reads a real PaintCode document, named by the <c>SVG_PAINTCODE_SAMPLE</c> environment variable.
/// </summary>
/// <remarks>
/// Opt-in because the document these numbers were taken from is 16 MB and belongs to a product rather
/// than to this repository. The counts are pinned so that a PaintCode release which changes the
/// archived graph fails here, loudly, rather than quietly converting less than it used to.
/// </remarks>
public class PaintCodeSampleTests
{
    private readonly ITestOutputHelper _output;

    public PaintCodeSampleTests(ITestOutputHelper output) => _output = output;

    [SampleFact]
    public void The_Sample_Document_Reads_As_The_Census_Recorded_It()
    {
        var document = PaintCodeDocument.Load(SampleFactAttribute.Path!);
        var census = Census.Of(document);

        _output.WriteLine(census.ToString());

        Assert.Equal(10, document.Desks.Count);
        Assert.Equal(1014, document.Canvases.Count());
        Assert.Equal(149, document.Variables.Count);
        Assert.Equal(1389, census.Groups);
        Assert.Equal(761, census.Symbols);
        Assert.Equal(67, census.Clips);

        // The archive holds 3747 shape objects, 2331 of them beziers; the 67 that clip are named as a
        // group's clip and as one of its children, and the model keeps each only once, as the clip.
        Assert.Equal(3747 - 67, census.Shapes);
        Assert.Equal(2331 - 31, census.Beziers);
        Assert.Equal(1340 - 8, census.Stroked);
        Assert.Equal(94 - 6, census.GradientFilled);
        Assert.Equal(42, census.Texts);
        Assert.Equal(253, census.Rotated);
        Assert.Equal(0, census.Shadows);
    }

    [SampleFact]
    public void Every_Canvas_Has_A_Name_No_Other_Canvas_Shares()
    {
        var document = PaintCodeDocument.Load(SampleFactAttribute.Path!);
        var names = document.Canvases.Select(canvas => canvas.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [SampleFact]
    public void Every_Canvas_Converts_To_A_Drawing_Svg_Can_Build()
    {
        var document = PaintCodeDocument.Load(SampleFactAttribute.Path!);
        var directory = Directory.CreateTempSubdirectory("paintcode");

        try
        {
            var result = PaintCodeImport.Run(document, new PaintCodeImportOptions(directory.FullName) { IncludeSymbolOnlyCanvases = true });

            _output.WriteLine($"{result.Files.Count} drawings, {result.Notes.Count} notes");

            Assert.Equal(1014, result.Files.Count);

            foreach (var file in result.Files)
            {
                using var svg = new Svg.Skia.SKSvg();

                Assert.True(svg.Load(file) is { }, file);

                // Binding with no values supplied makes every parameter fall back to its default and
                // every expression type check, which is the half that loading on its own never reaches.
                Assert.True(svg.SetExpressionValues(new Dictionary<string, Svg.Expressions.ExprValue>()) is { }, file);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class Census
    {
        internal int Groups;
        internal int Symbols;
        internal int Shapes;
        internal int Beziers;
        internal int Clips;
        internal int Stroked;
        internal int GradientFilled;
        internal int Texts;
        internal int Rotated;
        internal int Shadows;

        internal static Census Of(PaintCodeDocument document)
        {
            var census = new Census();

            foreach (var canvas in document.Canvases)
            {
                census.Walk(canvas.Root);
            }

            return census;
        }

        private void Walk(PaintCodeItem item)
        {
            if (item.Frame.Rotation != 0)
            {
                Rotated++;
            }

            switch (item)
            {
                case PaintCodeGroup group:
                    Groups++;

                    if (group.Clip is { })
                    {
                        Clips++;
                    }

                    foreach (var child in group.Children)
                    {
                        Walk(child);
                    }

                    break;

                case PaintCodeSymbolItem:
                    Symbols++;

                    break;

                case PaintCodeShape shape:
                    Shapes++;

                    if (shape.Kind is PaintCodeShapeKind.Bezier)
                    {
                        Beziers++;
                    }

                    if (shape.Stroke.Kind is PaintCodePaintKind.Color)
                    {
                        Stroked++;
                    }

                    if (shape.Fill.Kind is PaintCodePaintKind.Gradient)
                    {
                        GradientFilled++;
                    }

                    if (shape.Text is { })
                    {
                        Texts++;
                    }

                    break;
            }
        }

        public override string ToString()
            => string.Join(", ", new List<string>
            {
                $"groups {Groups}",
                $"symbols {Symbols}",
                $"shapes {Shapes}",
                $"beziers {Beziers}",
                $"clips {Clips}",
                $"stroked {Stroked}",
                $"gradients {GradientFilled}",
                $"texts {Texts}",
                $"rotated {Rotated}",
                $"shadows {Shadows}"
            });
    }
}
