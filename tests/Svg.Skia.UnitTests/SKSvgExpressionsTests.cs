// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ShimSkiaSharp;
using Svg.Expressions;
using Xunit;
using ShimColor = ShimSkiaSharp.SKColor;
using ShimPicture = ShimSkiaSharp.SKPicture;

namespace Svg.Skia.UnitTests;

/// <summary>
/// The <see cref="SKSvg"/> surface for expressions: what a host asks for, what it gets, and what
/// happens when it asks for something the document cannot give.
/// </summary>
/// <remarks>
/// Two rules do most of the work here and they pull in opposite directions. Loading never evaluates,
/// so a document with required parameters still renders — as the placeholders it was authored to fall
/// back to — and no existing consumer of SKSvg notices this feature exists. Supplying values is
/// strict, because that is a host asking for a specific rendering, and the same rule the generated
/// code enforces is better than a silent grey rectangle.
/// </remarks>
public class SKSvgExpressionsTests
{
    private const string Markup = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code>
              <e:param name="tint" type="color" />
              <e:param name="fade" type="number" default="1" />
            </e:code>
          </defs>
          <rect x="0" y="0" width="24" height="24" fill="{{ tint }}" opacity="{{ fade }}" />
        </svg>
        """;

    private static SKSvg Load(string markup = Markup)
    {
        var svg = new SKSvg();
        var picture = svg.FromSvg(markup);
        Assert.NotNull(picture);

        return svg;
    }

    private static Dictionary<string, ExprValue> Values(params (string Name, ExprValue Value)[] values)
    {
        var result = new Dictionary<string, ExprValue>(StringComparer.Ordinal);

        foreach (var (name, value) in values)
        {
            result[name] = value;
        }

        return result;
    }

    private static ShimColor FillOf(ShimPicture? model)
    {
        var color = model?.Commands?.OfType<DrawPathCanvasCommand>()
            .Select(c => c.Paint?.Color)
            .FirstOrDefault(c => c is { });

        Assert.NotNull(color);

        return color!.Value;
    }

    /// <summary>
    /// The channels alone. ShimSkiaSharp.SKColor includes Expression in its equality, so comparing
    /// whole colours would conflate "the placeholder" with "evaluated to the placeholder's value" --
    /// which is exactly the distinction some of these tests are about.
    /// </summary>
    private static (byte R, byte G, byte B, byte A) Channels(ShimPicture? model)
    {
        var color = FillOf(model);

        return (color.Red, color.Green, color.Blue, color.Alpha);
    }

    private static (byte R, byte G, byte B, byte A) Channels(byte r, byte g, byte b, byte a) => (r, g, b, a);

    [Fact]
    public void Loading_A_Document_With_A_Required_Parameter_Still_Renders()
    {
        // The rule that keeps this feature invisible to every existing consumer: `tint` has no
        // default, so evaluating would fail, and loading does not evaluate.
        using var svg = Load();

        Assert.NotNull(svg.Picture);
        Assert.Null(svg.ExpressionValues);

        // The placeholder the parser substituted, not a value.
        Assert.Equal(Channels(128, 128, 128, 255), Channels(svg.Model));
    }

    [Fact]
    public void The_Declared_Parameters_Are_Reported_In_Order()
    {
        using var svg = Load();

        Assert.Equal(new[] { "tint", "fade" }, svg.ExpressionParameters.Select(p => p.Name));
        Assert.Equal(ExprType.Color, svg.ExpressionParameters[0].Type);
        Assert.Null(svg.ExpressionParameters[0].DefaultExpression);
        Assert.Equal("1", svg.ExpressionParameters[1].DefaultExpression);
    }

    [Fact]
    public void A_Document_Without_Declarations_Reports_No_Parameters()
    {
        using var svg = Load("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect x="0" y="0" width="24" height="24" fill="#1e40af" />
            </svg>
            """);

        Assert.Empty(svg.ExpressionParameters);
    }

    [Fact]
    public void Supplying_Values_Changes_What_Is_Rendered()
    {
        using var svg = Load();

        var before = svg.Picture;
        var picture = svg.SetExpressionValues(Values(("tint", ExprValue.Color(255, 0, 0, 255))));

        Assert.NotNull(picture);
        Assert.NotSame(before, picture);
        Assert.Equal(Channels(255, 0, 0, 255), Channels(svg.Model));
    }

    [Fact]
    public void A_Parameter_With_A_Default_Does_Not_Have_To_Be_Supplied()
    {
        using var svg = Load();

        svg.SetExpressionValues(Values(("tint", ExprValue.Color(0, 0, 255, 255))));

        Assert.Equal(Channels(0, 0, 255, 255), Channels(svg.Model));
    }

    [Fact]
    public void A_Parameter_With_Neither_A_Value_Nor_A_Default_Is_An_Error()
    {
        using var svg = Load();

        var error = Assert.Throws<ExprException>(() => svg.SetExpressionValues(Values(("fade", ExprValue.Number(0.5f)))));

        Assert.Contains("No value was supplied for 'tint'", error.Message);
    }

    [Fact]
    public void A_Failed_Call_Leaves_The_Previous_Rendering_In_Place()
    {
        // Evaluated before anything is assigned, so a rejected set does not leave a half-applied
        // drawing behind.
        using var svg = Load();

        svg.SetExpressionValues(Values(("tint", ExprValue.Color(255, 0, 0, 255))));
        var applied = svg.Picture;

        Assert.Throws<ExprException>(() => svg.SetExpressionValues(Values(("fade", ExprValue.Number(0.5f)))));

        Assert.Same(applied, svg.Picture);
        Assert.Equal(Channels(255, 0, 0, 255), Channels(svg.Model));
    }

    [Fact]
    public void A_Value_Of_The_Wrong_Type_Is_An_Error()
    {
        using var svg = Load();

        var error = Assert.Throws<ExprException>(
            () => svg.SetExpressionValues(Values(("tint", ExprValue.Number(1f)))));

        Assert.Contains("'tint'", error.Message);
    }

    [Fact]
    public void Clearing_The_Values_Goes_Back_To_The_Placeholders()
    {
        using var svg = Load();

        svg.SetExpressionValues(Values(("tint", ExprValue.Color(255, 0, 0, 255))));
        Assert.Equal(Channels(255, 0, 0, 255), Channels(svg.Model));

        svg.ClearExpressionValues();

        Assert.Null(svg.ExpressionValues);
        Assert.Equal(Channels(128, 128, 128, 255), Channels(svg.Model));
    }

    [Fact]
    public void Re_Evaluating_Does_Not_Re_Parse_The_Document()
    {
        // What a viewer does on every frame of a slider drag. The document object has to be the same
        // one afterwards, or the cost per change is a parse and a scene compile rather than a walk.
        using var svg = Load();

        var document = svg.SourceDocument;
        Assert.NotNull(document);

        for (var step = 0; step < 4; step++)
        {
            svg.SetExpressionValues(Values(
                ("tint", ExprValue.Color((byte)(step * 60), 0, 0, 255)),
                ("fade", ExprValue.Number(1f))));
        }

        Assert.Same(document, svg.SourceDocument);
        Assert.Equal(Channels(180, 0, 0, 255), Channels(svg.Model));
    }

    [Fact]
    public void Values_Are_Copied_So_A_Caller_Cannot_Change_Them_Afterwards()
    {
        using var svg = Load();

        var values = Values(("tint", ExprValue.Color(255, 0, 0, 255)));
        svg.SetExpressionValues(values);

        values["tint"] = ExprValue.Color(0, 255, 0, 255);

        Assert.Equal(Channels(255, 0, 0, 255), Channels(svg.Model));
    }

    [Fact]
    public void Values_Supplied_For_Names_The_Document_Does_Not_Declare_Are_Ignored()
    {
        using var svg = Load();

        svg.SetExpressionValues(Values(
            ("tint", ExprValue.Color(255, 0, 0, 255)),
            ("gone", ExprValue.Number(9f))));

        Assert.Equal(Channels(255, 0, 0, 255), Channels(svg.Model));
    }

    [Fact]
    public void Refreshing_From_The_Source_Document_Keeps_The_Bound_Values()
    {
        // A DOM edit followed by a refresh recompiles the scene. Without re-applying, the drawing
        // would silently drop back to the placeholders.
        using var svg = Load();

        svg.SetExpressionValues(Values(("tint", ExprValue.Color(255, 0, 0, 255))));
        svg.RefreshFromSourceDocument();

        Assert.Equal(Channels(255, 0, 0, 255), Channels(svg.Model));
    }

    [Fact]
    public void A_Clone_Can_Be_Given_New_Values()
    {
        // The symbolic model travels with the clone, or it could render once and never respond again.
        using var svg = Load();
        svg.SetExpressionValues(Values(("tint", ExprValue.Color(255, 0, 0, 255))));

        using var clone = svg.Clone();

        Assert.Equal(Channels(255, 0, 0, 255), Channels(clone.Model));

        clone.SetExpressionValues(Values(("tint", ExprValue.Color(0, 255, 0, 255))));

        Assert.Equal(Channels(0, 255, 0, 255), Channels(clone.Model));

        // And the original is untouched by the clone's change.
        Assert.Equal(Channels(255, 0, 0, 255), Channels(svg.Model));
    }

    [Fact]
    public void A_Clone_Of_An_Unevaluated_Document_Can_Still_Be_Given_Values()
    {
        using var svg = Load();
        using var clone = svg.Clone();

        clone.SetExpressionValues(Values(("tint", ExprValue.Color(0, 255, 0, 255))));

        Assert.Equal(Channels(0, 255, 0, 255), Channels(clone.Model));
        Assert.Equal(Channels(128, 128, 128, 255), Channels(svg.Model));
    }

    [Fact]
    public void Loading_From_A_Stream_Supports_Values_Too()
    {
        // The route that has no source text to re-parse, which is why the declarations are read from
        // the tree.
        using var svg = new SKSvg();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Markup));

        Assert.NotNull(svg.Load(stream));

        svg.SetExpressionValues(Values(("tint", ExprValue.Color(255, 0, 0, 255))));

        Assert.Equal(Channels(255, 0, 0, 255), Channels(svg.Model));
    }

    [Fact]
    public void Values_Set_Before_A_Document_Is_Loaded_Apply_When_It_Arrives()
    {
        using var svg = new SKSvg();

        Assert.Null(svg.SetExpressionValues(Values(("tint", ExprValue.Color(255, 0, 0, 255)))));

        Assert.NotNull(svg.FromSvg(Markup));
        Assert.Equal(Channels(255, 0, 0, 255), Channels(svg.Model));
    }

    [Fact]
    public void A_Document_Without_Expressions_Is_Unaffected_By_Setting_Values()
    {
        using var svg = Load("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect x="0" y="0" width="24" height="24" fill="#1e40af" />
            </svg>
            """);

        var model = svg.Model;
        svg.SetExpressionValues(Values(("nothing", ExprValue.Number(1f))));

        // Same instance: nothing was symbolic, so the rewrite had nothing to do.
        Assert.Same(model, svg.Model);
    }

    [Fact]
    public void An_Opacity_Expression_Reaches_The_Layer()
    {
        using var svg = Load();

        svg.SetExpressionValues(Values(
            ("tint", ExprValue.Color(255, 0, 0, 255)),
            ("fade", ExprValue.Number(0.5f))));

        var layer = Assert.Single(svg.Model!.Commands!.OfType<SaveLayerCanvasCommand>());

        Assert.Equal(new ShimColor(255, 255, 255, 128), layer.Paint!.Color);
    }
}
