using System;
using System.Linq;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Expressions;
using Svg.Expressions;
using Xunit;

namespace Svg.Skia.UnitTests;

public class SvgExpressionDeclarationsTests
{
    private const string Ns = SvgExpressionDeclarations.Namespace;

    [Fact]
    public void Plain_Svg_Yields_Empty_Declarations()
    {
        var declarations = SvgExpressionDeclarations.Parse("""
            <svg xmlns="http://www.w3.org/2000/svg" width="10" height="10" />
            """);

        Assert.True(declarations.IsEmpty);
    }

    [Fact]
    public void Params_And_Lets_Are_Read_In_Document_Order()
    {
        var declarations = SvgExpressionDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="t" type="number" default="0" />
                  <e:param name="tint" type="color" />
                  <e:param name="bold" type="boolean" default="true" />
                  <e:let name="wave">(sin(t * tau) + 1) / 2</e:let>
                  <e:let name="level">clamp(wave, 0, 1)</e:let>
                </e:code>
              </defs>
            </svg>
            """);

        Assert.False(declarations.IsEmpty);

        Assert.Equal(new[] { "t", "tint", "bold" }, declarations.Parameters.Select(p => p.Name));
        Assert.Equal(ExprType.Number, declarations.Parameters[0].Type);
        Assert.Equal(ExprType.Color, declarations.Parameters[1].Type);
        Assert.Equal(ExprType.Boolean, declarations.Parameters[2].Type);
        Assert.Equal("0", declarations.Parameters[0].DefaultExpression);
        Assert.Null(declarations.Parameters[1].DefaultExpression);

        // Order matters: a let may reference an earlier one.
        Assert.Equal(new[] { "wave", "level" }, declarations.Lets.Select(l => l.Name));
    }

    [Fact]
    public void Any_Prefix_Works_Because_Matching_Is_By_Namespace()
    {
        var declarations = SvgExpressionDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:whatever="{Ns}" width="10" height="10">
              <defs>
                <whatever:code>
                  <whatever:param name="t" type="number" default="0" />
                </whatever:code>
              </defs>
            </svg>
            """);

        Assert.Equal("t", Assert.Single(declarations.Parameters).Name);
    }

    [Fact]
    public void A_Different_Namespace_Is_Not_Claimed()
    {
        var declarations = SvgExpressionDeclarations.Parse("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://example.com/other" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="t" type="number" default="0" />
                </e:code>
              </defs>
            </svg>
            """);

        Assert.True(declarations.IsEmpty);
    }

    [Fact]
    public void Unknown_Type_Is_Rejected()
    {
        var error = Assert.Throws<ExprException>(() => SvgExpressionDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code><e:param name="t" type="float" /></e:code></defs>
            </svg>
            """));

        Assert.Contains("Unknown type 'float'", error.Message);
    }

    [Fact]
    public void Redeclaring_A_Builtin_Is_Rejected()
    {
        var error = Assert.Throws<ExprException>(() => SvgExpressionDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code><e:param name="tau" type="number" /></e:code></defs>
            </svg>
            """));

        Assert.Contains("built-in", error.Message);
    }

    [Fact]
    public void Duplicate_Names_Are_Rejected()
    {
        var error = Assert.Throws<ExprException>(() => SvgExpressionDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code>
                <e:param name="t" type="number" />
                <e:let name="t">1</e:let>
              </e:code></defs>
            </svg>
            """));

        Assert.Contains("declared more than once", error.Message);
    }

    [Fact]
    public void A_Let_Sees_Earlier_Lets_But_Not_Later_Ones()
    {
        var forward = SvgExpressionDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code>
                <e:let name="a">b + 1</e:let>
                <e:let name="b">2</e:let>
              </e:code></defs>
            </svg>
            """);

        var error = Assert.Throws<ExprException>(() => forward.Resolve());
        Assert.Contains("Unknown name 'b'", error.Message);
    }

    [Fact]
    public void Resolve_Infers_Let_Types()
    {
        var declarations = SvgExpressionDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code>
                <e:param name="t" type="number" default="0" />
                <e:let name="wave">sin(t)</e:let>
                <e:let name="c">hsl(200, 50%, 50%)</e:let>
                <e:let name="hot">wave &gt; 0.5</e:let>
              </e:code></defs>
            </svg>
            """);

        var (_, lets) = declarations.Resolve();

        Assert.Equal(ExprType.Number, lets[0].Type);
        Assert.Equal(ExprType.Color, lets[1].Type);
        Assert.Equal(ExprType.Boolean, lets[2].Type);
    }

    [Fact]
    public void A_Parameter_Default_Cannot_Reference_Another_Parameter()
    {
        // C# argument defaults are compile time constants, so an ordering dependency between
        // them could not be honoured.
        var declarations = SvgExpressionDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code>
                <e:param name="a" type="number" default="1" />
                <e:param name="b" type="number" default="a" />
              </e:code></defs>
            </svg>
            """);

        var error = Assert.Throws<ExprException>(() => declarations.Parameters[1].DefaultCode());
        Assert.Contains("Unknown name 'a'", error.Message);
    }

    [Fact]
    public void A_Default_Must_Match_The_Declared_Type()
    {
        // Declared on a number: a colour parameter is rejected for carrying any default at all,
        // which would mask the type check being tested here.
        var declarations = SvgExpressionDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code><e:param name="t" type="number" default="#ff0000" /></e:code></defs>
            </svg>
            """);

        var error = Assert.Throws<ExprException>(() => declarations.Parameters[0].DefaultCode());
        Assert.Contains("must be a number", error.Message);
    }

    [Fact]
    public void A_Colour_Parameter_May_Have_A_Default()
    {
        // `new SKColor(...)` is not a compile-time constant, so this cannot be a C# argument
        // default — but that is the code generator's problem to solve, not a limit on the format.
        // It emits a nullable parameter and coalesces, and the evaluator needed no change at all.
        var declarations = SvgExpressionDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code><e:param name="c" type="color" default="#ff0000" /></e:code></defs>
            </svg>
            """);

        var parameter = Assert.Single(declarations.Parameters);

        Assert.Equal(ExprType.Color, parameter.Type);
        Assert.Equal("#ff0000", parameter.DefaultExpression);
    }

    [Fact]
    public void A_Parameter_Without_A_Default_Has_No_Default_Code()
    {
        var declarations = SvgExpressionDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code><e:param name="c" type="color" /></e:code></defs>
            </svg>
            """);

        Assert.Null(declarations.Parameters[0].DefaultCode());
    }

    [Fact]
    public void Malformed_Xml_Yields_Empty_Rather_Than_Throwing()
    {
        // The SVG parser is the authority on well-formedness and reports it; this must not be a
        // second, competing failure path.
        var declarations = SvgExpressionDeclarations.Parse($"""<svg xmlns:e="{Ns}"><defs><e:code>""");

        Assert.True(declarations.IsEmpty);
    }

    [Fact]
    public void Null_And_Empty_Input_Are_Safe()
    {
        Assert.True(SvgExpressionDeclarations.Parse(null).IsEmpty);
        Assert.True(SvgExpressionDeclarations.Parse("").IsEmpty);
        Assert.True(SvgExpressionDeclarations.Parse("   ").IsEmpty);
    }

    [Fact]
    public void Range_Attributes_Are_Read_As_Expression_Text()
    {
        var declarations = Declare("""<e:param name="t" type="number" min=" 0 " max="tau" step="1/60" />""");

        var parameter = Assert.Single(declarations.Parameters);
        Assert.Equal("0", parameter.MinExpression);
        Assert.Equal("tau", parameter.MaxExpression);
        Assert.Equal("1/60", parameter.StepExpression);
        Assert.True(parameter.HasRange);
    }

    [Fact]
    public void A_Parameter_With_No_Range_Falls_Back_To_Zero_To_One()
    {
        var parameter = Assert.Single(Declare("""<e:param name="t" type="number" default="0.25" />""").Parameters);

        Assert.False(parameter.HasRange);
        Assert.Equal(SvgExpressionRange.Default, parameter.ResolveRange());
        Assert.Equal(0f, parameter.ResolveRange().Minimum);
        Assert.Equal(1f, parameter.ResolveRange().Maximum);
        Assert.False(parameter.ResolveRange().HasStep);
    }

    [Fact]
    public void A_Range_Is_Evaluated_As_An_Expression()
    {
        // The whole point of storing text rather than parsed numbers: a bound is written in the same
        // language the default is.
        var parameter = Assert.Single(Declare("""<e:param name="t" type="number" min="-tau" max="tau" step="100% / 8" />""").Parameters);

        var range = parameter.ResolveRange();

        Assert.Equal(-MathF.PI * 2f, range.Minimum, 5);
        Assert.Equal(MathF.PI * 2f, range.Maximum, 5);
        Assert.Equal(0.125f, range.Step, 5);
        Assert.True(range.HasStep);
    }

    [Fact]
    public void A_Step_May_Stand_Alone()
    {
        // Quantisation, not an end point, and well defined against the documented 0..1 fallback.
        var parameter = Assert.Single(Declare("""<e:param name="t" type="number" step="0.25" />""").Parameters);

        Assert.Equal(new SvgExpressionRange(0f, 1f, 0.25f), parameter.ResolveRange());
    }

    [Fact]
    public void An_Empty_Range_Attribute_Is_The_Same_As_An_Absent_One()
    {
        var parameter = Assert.Single(Declare("""<e:param name="t" type="number" min="" max="" />""").Parameters);

        Assert.False(parameter.HasRange);
        Assert.Equal(SvgExpressionRange.Default, parameter.ResolveRange());

        // The consequence of that, spelled out: one empty end is one declared end.
        var error = Assert.Throws<ExprException>(
            () => Declare("""<e:param name="t" type="number" min="" max="1" />"""));

        Assert.Contains("has a max but no min", error.Message);
    }

    [Fact]
    public void A_Range_Bound_Must_Be_A_Number()
    {
        var colour = Assert.Single(Declare("""<e:param name="t" type="number" min="#ff0000" max="1" />""").Parameters);
        Assert.Contains("must be a number", Assert.Throws<ExprException>(() => colour.ResolveRange()).Message);

        var boolean = Assert.Single(Declare("""<e:param name="t" type="number" step="true" />""").Parameters);
        Assert.Contains("must be a number", Assert.Throws<ExprException>(() => boolean.ResolveRange()).Message);
    }

    [Fact]
    public void A_Range_Bound_Cannot_Reference_Another_Parameter()
    {
        // Resolved against nothing at all, exactly as a default is.
        var declarations = Declare("""
            <e:param name="a" type="number" default="1" />
            <e:param name="b" type="number" min="0" max="a" />
            """);

        var error = Assert.Throws<ExprException>(() => declarations.Parameters[1].ResolveRange());

        Assert.Contains("Unknown name 'a'", error.Message);
    }

    [Fact]
    public void A_Reversed_Range_Is_Rejected_When_Resolved_Rather_Than_When_Read()
    {
        // The eager/lazy split, pinned: reading a document evaluates nothing, so Parse succeeds and
        // only ResolveRange complains. SKSvg.Load depends on this staying true.
        var declarations = Declare("""<e:param name="t" type="number" min="1" max="0" />""");

        var error = Assert.Throws<ExprException>(() => declarations.Parameters[0].ResolveRange());

        Assert.Contains("is greater than its max", error.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void A_Step_Must_Be_Positive(string step)
    {
        var parameter = Assert.Single(Declare($"""<e:param name="t" type="number" step="{step}" />""").Parameters);

        Assert.Contains("must be greater than zero", Assert.Throws<ExprException>(() => parameter.ResolveRange()).Message);
    }

    [Fact]
    public void A_Default_Outside_Its_Range_Is_Allowed()
    {
        // The range is advice to a host, never a constraint on a value. Nothing clamps.
        var parameter = Assert.Single(Declare("""<e:param name="t" type="number" default="5" min="0" max="1" />""").Parameters);

        Assert.Equal(new SvgExpressionRange(0f, 1f, 0f), parameter.ResolveRange());
        Assert.Equal(5f, ExprEvaluator.Create(SvgExpressionDeclarations.Parse(Markup("""<e:param name="t" type="number" default="5" min="0" max="1" />"""))).Evaluate("t").AsNumber);
    }

    private static string Markup(string body)
        => $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code>{body}</e:code></defs>
            </svg>
            """;

    private static SvgExpressionDeclarations Declare(string body)
        => SvgExpressionDeclarations.Parse(Markup(body));
}
