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

    private static SvgViewerIntegerParameter Integer(string param)
        => Assert.IsType<SvgViewerIntegerParameter>(SvgViewerParameterFactory.Create(Declare(param)));

    // ---- a row takes a value back -----------------------------------------------------------

    [Theory]
    // Every type, because the switch this replaced had three of the five and dropped a string or an
    // integer value on the floor wherever a row was asked to take one back.
    [InlineData("""<e:param name="p" type="number" default="0.25" />""")]
    [InlineData("""<e:param name="p" type="integer" default="4" />""")]
    [InlineData("""<e:param name="p" type="color" default="#22c55e" />""")]
    [InlineData("""<e:param name="p" type="boolean" default="true" />""")]
    [InlineData("""<e:param name="p" type="string" default="'dark'" />""")]
    public void A_Row_Takes_Back_What_It_Gave(string param)
    {
        var row = SvgViewerParameterFactory.Create(Declare(param));
        var carried = SvgViewerParameterFactory.Create(Declare(param));

        Assert.True(carried.TrySet(row.ToExprValue()));
        Assert.Equal(row.ToExprValue(), carried.ToExprValue());
    }

    [Theory]
    [InlineData("""<e:param name="p" type="number" default="0.25" />""")]
    [InlineData("""<e:param name="p" type="integer" default="4" />""")]
    [InlineData("""<e:param name="p" type="color" default="#22c55e" />""")]
    [InlineData("""<e:param name="p" type="boolean" default="true" />""")]
    [InlineData("""<e:param name="p" type="string" default="'dark'" />""")]
    public void A_Row_Refuses_A_Value_Of_Another_Type(string param)
    {
        var row = SvgViewerParameterFactory.Create(Declare(param));
        var was = row.ToExprValue();

        // Refused rather than coerced: rounding a number into an integer row would put a value into
        // the drawing that nobody chose, and the evaluator would refuse the lot anyway.
        foreach (var other in new[]
                 {
                     ExprValue.Number(9f),
                     ExprValue.Integer(9),
                     ExprValue.Color(1, 2, 3, 4),
                     ExprValue.Boolean(false),
                     ExprValue.String("other"),
                 })
        {
            if (other.Type == row.Type)
            {
                continue;
            }

            Assert.False(row.TrySet(other));
            Assert.Equal(was, row.ToExprValue());
        }
    }

    [Fact]
    public void A_Declared_Range_Is_Used_As_Declared()
    {
        var row = Number("""<e:param name="hue" type="number" default="217" min="0" max="360" step="1" />""");

        Assert.Equal(0m, row.Minimum);
        Assert.Equal(360m, row.Maximum);
        Assert.Equal(1d, row.Step);
        Assert.True(row.HasStep);
        Assert.True(row.HasSlider);
        Assert.Equal(217d, row.Value);
        Assert.False(row.IsModified);
    }

    [Fact]
    public void A_Default_Is_Evaluated_Rather_Than_Parsed()
    {
        // A default is an expression, so parsing it as a float would fail on anything but a literal.
        var row = Number("""<e:param name="t" type="number" default="tau / 4" min="0" max="tau" />""");

        Assert.Equal(MathF.PI / 2f, row.Value, 4);
        Assert.Equal(MathF.PI * 2f, (double)row.Maximum, 4);
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
        // What is wrong with the default is said on the declaration panel, where its row would be.
        var row = Number("""<e:param name="t" type="number" default="hsl(1, 2)" />""");

        Assert.Equal(0d, row.Value);
    }

    /// <summary>
    /// What replaced inferring the end nobody wrote: 217 used to come up on a 0..500 slider whose
    /// ends were this code's guess and whose drag wrote numbers the drawing had not been said to
    /// take. A default inside 0..1 is no different — the range a bare default resolves to is written
    /// down in the format, but it is still not something this parameter said.
    /// </summary>
    [Theory]
    [InlineData("217")]
    [InlineData("-3")]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("1")]
    public void An_Unranged_Default_Gets_No_Range_At_All(string @default)
    {
        var row = Number($"""<e:param name="hue" type="number" default="{@default}" />""");

        Assert.False(row.Declaration.HasRange);
        Assert.False(row.HasSlider);
        Assert.Equal(decimal.MinValue, row.Minimum);
        Assert.Equal(decimal.MaxValue, row.Maximum);
        Assert.Equal(double.Parse(@default, CultureInfo.InvariantCulture), row.Value);

        // No range to take a hundredth of, so the field steps by one.
        Assert.Equal(1d, row.TickFrequency);
    }

    /// <summary>
    /// One end is no range at all — not the end itself and not a slider.
    /// </summary>
    /// <remarks>
    /// Built by hand rather than declared, because a document cannot say this: a min without a max is
    /// refused as it is read, and the dialog holds one only while somebody is typing into it. Nor can
    /// the end that was written be kept — resolving a range fills the end nobody wrote with the 0 or
    /// the 1 of the default range, and then refuses the min above the max it has just made. So this
    /// pins what a host that builds one gets: the field, free, and a row rather than an exception.
    /// </remarks>
    [Theory]
    [InlineData("10", null)]
    [InlineData(null, "500")]
    public void One_Declared_End_Is_No_Range(string? min, string? max)
    {
        var row = Assert.IsType<SvgViewerNumberParameter>(SvgViewerParameterFactory.Create(
            new SvgExpressionParameter("t", ExprType.Number, "217", min, max, null)));

        Assert.False(row.HasSlider);
        Assert.Equal(decimal.MinValue, row.Minimum);
        Assert.Equal(decimal.MaxValue, row.Maximum);
        Assert.Equal(217d, row.Value);
    }

    /// <summary>
    /// A step is granularity and not an end, so it drives what the field increments by while leaving
    /// it as free as a row with no range at all.
    /// </summary>
    [Fact]
    public void A_Step_Alone_Is_Not_A_Range()
    {
        var row = Number("""<e:param name="t" type="number" default="217" step="5" />""");

        Assert.False(row.HasSlider);
        Assert.Equal(decimal.MinValue, row.Minimum);
        Assert.Equal(decimal.MaxValue, row.Maximum);
        Assert.Equal(5d, row.TickFrequency);
    }

    /// <summary>
    /// An end the field cannot hold is no end. The bounds are decimal and a bound is any float
    /// expression, so the two do not have the same reach -- and a document that renders must open.
    /// </summary>
    /// <remarks>
    /// Both reachable: the language has no exponent literal but it multiplies, and its sqrt hands
    /// back what MathF hands back, so <c>sqrt(0 - 1)</c> is a bound that is not a number. Neither
    /// used to survive the way to the control, which holds its bounds as decimal.
    /// </remarks>
    [Theory]
    [InlineData("100000 * 100000 * 100000 * 100000 * 100000 * 100000")]
    [InlineData("sqrt(0 - 1)")]
    public void An_End_Outside_What_The_Field_Holds_Is_No_End(string max)
    {
        var row = Number($"""<e:param name="t" type="number" default="2" min="0" max="{max}" />""");

        Assert.Equal(decimal.MaxValue, row.Maximum);
    }

    /// <summary>
    /// A fraction keeps its slider by saying so, which is how a PaintCode import writes one: the
    /// kind's 0..1 is written into the parameter, and a declared 0..1 is a range like any other.
    /// </summary>
    [Fact]
    public void A_Declared_Zero_To_One_Is_Still_A_Range()
    {
        var row = Number("""<e:param name="t" type="number" default="0.5" min="0" max="1" />""");

        Assert.True(row.HasSlider);
        Assert.Equal(0m, row.Minimum);
        Assert.Equal(1m, row.Maximum);

        // A hundredth of the range, which is what a fraction wants a drag to feel like.
        Assert.Equal(0.01d, row.TickFrequency, 6);
    }

    /// <summary>The number row's rule, on the type whose ends are whole.</summary>
    [Theory]
    [InlineData("4")]
    [InlineData("0")]
    [InlineData("1")]
    public void An_Unranged_Integer_Gets_No_Range(string @default)
    {
        var row = Integer($"""<e:param name="steps" type="integer" default="{@default}" />""");

        Assert.False(row.HasSlider);
        Assert.Equal(int.MinValue, row.Minimum);
        Assert.Equal(int.MaxValue, row.Maximum);
        Assert.Equal(int.Parse(@default, CultureInfo.InvariantCulture), row.Value);

        // Still one, which is the smallest an integer can move by.
        Assert.Equal(1, row.TickFrequency);
    }

    [Fact]
    public void An_Integer_Keeps_A_Declared_Range()
    {
        var row = Integer("""<e:param name="steps" type="integer" default="4" min="0" max="9" />""");

        Assert.True(row.HasSlider);
        Assert.Equal(0, row.Minimum);
        Assert.Equal(9, row.Maximum);
    }

    [Fact]
    public void A_Declared_Range_Is_Widened_To_Reach_Its_Own_Default()
    {
        // The range is advice, and the format allows a default outside it. The slider still has to
        // be able to get back to the value it started on.
        var row = Number("""<e:param name="t" type="number" default="5" min="0" max="1" />""");

        Assert.Equal(0m, row.Minimum);
        Assert.Equal(5m, row.Maximum);
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
        Assert.Equal(declared * 2d, (double)row.Minimum + (2d * row.TickFrequency));

        // And it is the same float either way, so what the evaluator computes with is untouched.
        // This is a widening said properly, not a rounding of the parameter.
        Assert.Equal((float)declared, (float)row.Step);
    }

    [Fact]
    public void A_Range_That_Does_Not_Resolve_Falls_Back()
    {
        // Reported on the declaration panel, in place of the row it would have had, rather than here.
        var row = Number("""<e:param name="t" type="number" min="1" max="0" />""");

        // Both ends were written, so there is still a slider; what they resolve to is the fallback.
        Assert.True(row.HasSlider);
        Assert.Equal(0m, row.Minimum);
        Assert.Equal(1m, row.Maximum);
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
