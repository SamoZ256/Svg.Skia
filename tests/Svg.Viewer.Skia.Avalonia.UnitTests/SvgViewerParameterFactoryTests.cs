using System;
using System.Globalization;
using System.Linq;
using Svg.Expressions;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// Seeding and range resolution, which are plain functions of a declaration and need no UI.
/// </summary>
public class SvgViewerParameterFactoryTests
{
    private const string Ns = SvgExpressionDeclarations.Namespace;

    private static SvgExpressionParameter Declare(string param)
        => SvgExpressionDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code>{param}</e:code></defs>
            </svg>
            """).Parameters.Single();

    private static SvgViewerNumberParameter Number(string param)
        => Assert.IsType<SvgViewerNumberParameter>(SvgViewerParameterFactory.Create(Declare(param)));

    [Fact]
    public void A_Declared_Range_Is_Used_As_Declared()
    {
        var row = Number("""<e:param name="hue" type="number" default="217" min="0" max="360" step="1" />""");

        Assert.Equal(0d, row.Minimum);
        Assert.Equal(360d, row.Maximum);
        Assert.Equal(1d, row.Step);
        Assert.True(row.HasStep);
        Assert.Equal(217d, row.Value);
        Assert.False(row.IsModified);
    }

    [Fact]
    public void A_Default_Is_Evaluated_Rather_Than_Parsed()
    {
        // A default is an expression, so parsing it as a float would fail on anything but a literal.
        var row = Number("""<e:param name="t" type="number" default="tau / 4" min="0" max="tau" />""");

        Assert.Equal(MathF.PI / 2f, row.Value, 4);
        Assert.Equal(MathF.PI * 2f, row.Maximum, 4);
    }

    [Fact]
    public void A_Colour_Default_Is_Evaluated_Too()
    {
        var row = Assert.IsType<SvgViewerColorParameter>(
            SvgViewerParameterFactory.Create(Declare("""<e:param name="tint" type="color" default="hsl(0, 100%, 50%)" />""")));

        Assert.Equal(255, row.Color.R);
        Assert.Equal(0, row.Color.G);
        Assert.Equal(0, row.Color.B);
    }

    [Fact]
    public void A_Colour_Without_A_Default_Starts_At_The_Placeholder()
    {
        // Where an unevaluated document already renders, rather than some other arbitrary grey.
        var row = Assert.IsType<SvgViewerColorParameter>(
            SvgViewerParameterFactory.Create(Declare("""<e:param name="tint" type="color" />""")));

        Assert.Equal(0x80, row.Color.R);
        Assert.Equal(0x80, row.Color.G);
        Assert.Equal(0x80, row.Color.B);
    }

    [Fact]
    public void A_Malformed_Default_Is_Swallowed_Rather_Than_Thrown()
    {
        // The parameter still has to be offered: the document renders, and the value is bindable.
        // What is wrong with the default is marked in the source pane, where the default is.
        var row = Number("""<e:param name="t" type="number" default="hsl(1, 2)" />""");

        Assert.Equal(0d, row.Value);
    }

    [Fact]
    public void An_Unranged_Default_Above_One_Widens_To_A_Round_Number()
    {
        // 0..1 would put a default of 217 hard against the end of the slider.
        var row = Number("""<e:param name="hue" type="number" default="217" />""");

        Assert.False(row.Declaration.HasRange);
        Assert.Equal(0d, row.Minimum);
        Assert.Equal(500d, row.Maximum);
        Assert.Equal(217d, row.Value);
    }

    [Theory]
    [InlineData("0", 0d, 1d)]
    [InlineData("0.5", 0d, 1d)]
    [InlineData("1", 0d, 1d)]
    public void An_Unranged_Default_Within_Zero_To_One_Keeps_That_Range(string @default, double min, double max)
    {
        var row = Number($"""<e:param name="t" type="number" default="{@default}" />""");

        Assert.Equal(min, row.Minimum);
        Assert.Equal(max, row.Maximum);
    }

    [Fact]
    public void An_Unranged_Negative_Default_Widens_Downwards()
    {
        var row = Number("""<e:param name="t" type="number" default="-3" />""");

        Assert.Equal(-10d, row.Minimum);
        Assert.Equal(1d, row.Maximum);
        Assert.Equal(-3d, row.Value);
    }

    [Fact]
    public void A_Declared_Range_Is_Widened_To_Reach_Its_Own_Default()
    {
        // The range is advice, and the format allows a default outside it. The slider still has to
        // be able to get back to the value it started on.
        var row = Number("""<e:param name="t" type="number" default="5" min="0" max="1" />""");

        Assert.Equal(0d, row.Minimum);
        Assert.Equal(5d, row.Maximum);
        Assert.Equal(5d, row.Value);
    }

    [Theory]
    [InlineData("0.1")]
    [InlineData("0.2")]
    [InlineData("0.3")]
    public void A_Step_Carries_No_More_Digits_Than_A_Float_Has(string step)
    {
        // A float widened to a double keeps its binary tail: 0.1 arrives as 0.10000000149011612,
        // and a slider two ticks along reads 0.200000002980232. Seventeen digits of a number that
        // has seven, in a box somebody has to read.
        var row = Number($"""<e:param name="t" type="number" default="0" min="0" max="1" step="{step}" />""");

        var declared = double.Parse(step, CultureInfo.InvariantCulture);

        Assert.Equal(declared, row.Step);
        Assert.Equal(declared * 2d, row.Minimum + (2d * row.TickFrequency));

        // And it is the same float either way, so what the evaluator computes with is untouched.
        // This is a widening said properly, not a rounding of the parameter.
        Assert.Equal((float)declared, (float)row.Step);
    }

    [Fact]
    public void A_Range_That_Does_Not_Resolve_Falls_Back()
    {
        // Reported in the source pane, at the min it is wrong in, rather than here.
        var row = Number("""<e:param name="t" type="number" min="1" max="0" />""");

        Assert.Equal(0d, row.Minimum);
        Assert.Equal(1d, row.Maximum);
    }

    [Fact]
    public void A_Continuous_Range_Ticks_By_A_Hundredth()
    {
        var row = Number("""<e:param name="hue" type="number" default="180" min="0" max="360" />""");

        Assert.False(row.HasStep);
        Assert.Equal(3.6d, row.TickFrequency, 6);
    }

    [Fact]
    public void A_Boolean_Seeds_From_Its_Default()
    {
        Assert.True(Assert.IsType<SvgViewerBooleanParameter>(
            SvgViewerParameterFactory.Create(Declare("""<e:param name="on" type="boolean" default="true" />"""))).Value);

        Assert.False(Assert.IsType<SvgViewerBooleanParameter>(
            SvgViewerParameterFactory.Create(Declare("""<e:param name="on" type="boolean" />"""))).Value);
    }

    [Fact]
    public void A_String_Seeds_From_Its_Default_And_Otherwise_Is_Empty()
    {
        Assert.Equal("dark", Assert.IsType<SvgViewerStringParameter>(
            SvgViewerParameterFactory.Create(Declare("""<e:param name="theme" type="string" default="'dark'" />"""))).Value);

        Assert.Equal("ICON", Assert.IsType<SvgViewerStringParameter>(
            SvgViewerParameterFactory.Create(Declare("""<e:param name="theme" type="string" default="upper('icon')" />"""))).Value);

        Assert.Equal(string.Empty, Assert.IsType<SvgViewerStringParameter>(
            SvgViewerParameterFactory.Create(Declare("""<e:param name="theme" type="string" />"""))).Value);
    }

    [Fact]
    public void A_String_Is_Committed_As_A_Literal_The_Language_Reads_Back()
    {
        var row = Assert.IsType<SvgViewerStringParameter>(
            SvgViewerParameterFactory.Create(Declare("""<e:param name="theme" type="string" default="'dark'" />""")));

        row.Value = "it's a\\ b";

        // The round trip that matters: what a commit writes has to declare the value it came from.
        Assert.Equal(@"'it\'s a\\ b'", row.ToExpression());
        Assert.Equal(
            row.ToExprValue(),
            ExprEvaluator.Create(SvgExpressionDeclarations.Empty, null).Evaluate(row.ToExpression()));
    }

    [Fact]
    public void A_Row_Reports_And_Resets_A_Change()
    {
        var row = Number("""<e:param name="t" type="number" default="0.25" />""");
        var raised = 0;
        row.ValueChanged += (_, _) => raised++;

        row.Value = 0.75d;

        Assert.Equal(1, raised);
        Assert.True(row.IsModified);
        Assert.Equal(0.75f, row.ToExprValue().AsNumber);

        row.ResetToDefault();

        Assert.False(row.IsModified);
        Assert.Equal(0.25f, row.ToExprValue().AsNumber, 4);
    }
}
