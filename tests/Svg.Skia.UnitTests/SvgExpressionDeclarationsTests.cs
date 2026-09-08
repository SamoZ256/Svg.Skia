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
                  <e:param name="theme" type="string" default="'dark'" />
                  <e:let name="wave">(sin(t * tau) + 1) / 2</e:let>
                  <e:let name="level">clamp(wave, 0, 1)</e:let>
                </e:code>
              </defs>
            </svg>
            """);

        Assert.False(declarations.IsEmpty);

        Assert.Equal(new[] { "t", "tint", "bold", "theme" }, declarations.Parameters.Select(p => p.Name));
        Assert.Equal(ExprType.Number, declarations.Parameters[0].Type);
        Assert.Equal(ExprType.Color, declarations.Parameters[1].Type);
        Assert.Equal(ExprType.Boolean, declarations.Parameters[2].Type);
        Assert.Equal(ExprType.String, declarations.Parameters[3].Type);
        Assert.Equal("'dark'", declarations.Parameters[3].DefaultExpression);
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

    // ---- what is wrong, and where it was written ------------------------------------------------

    [Fact]
    public void A_Document_That_Is_Right_Reports_Nothing()
    {
        SvgExpressionDeclarations.Parse(
            Markup("""<e:param name="t" type="number" min="0" max="1" /><e:let name="w">t * 2</e:let>"""),
            out var diagnostics);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Every_Bad_Declaration_Is_Reported_Rather_Than_Only_The_First()
    {
        // Three mistakes, three diagnostics. Throwing at the first is what hides the other two, and
        // a source view wants to underline all of them at once.
        var markup = Markup("""
            <e:param name="1t" type="number" /><e:param name="tint" type="color" min="0" max="1" /><e:let name="w"></e:let>
            """);

        SvgExpressionDeclarations.Parse(markup, out var diagnostics);

        Assert.Equal(3, diagnostics.Count);
        Assert.Contains("is not a valid name", diagnostics[0].Message);
        Assert.Contains("cannot carry min, max or step", diagnostics[1].Message);
        Assert.Contains("has no expression", diagnostics[2].Message);
    }

    [Fact]
    public void What_Could_Be_Read_Survives_A_Bad_Declaration()
    {
        // The parameters after a bad one are not lost with it: a pane that stopped showing the rest
        // of the document because of one typo would be worse than the typo.
        var declarations = SvgExpressionDeclarations.Parse(
            Markup("""<e:param name="t" type="number" /><e:param name="bad" /><e:param name="u" type="number" />"""),
            out var diagnostics);

        Assert.Single(diagnostics);
        Assert.Equal(new[] { "t", "u" }, declarations.Parameters.Select(p => p.Name));
    }

    [Fact]
    public void The_First_Mistake_Is_Still_What_The_Throwing_Reader_Throws()
    {
        // One decides, so the two cannot disagree about which mistake matters.
        var markup = Markup("""<e:param name="1t" type="number" /><e:let name="w"></e:let>""");

        SvgExpressionDeclarations.Parse(markup, out var diagnostics);

        Assert.Equal(
            Assert.Throws<ExprException>(() => SvgExpressionDeclarations.Parse(markup)).Message,
            diagnostics[0].Message);
    }

    [Theory]
    // A name that is wrong points at the name, not at the declaration carrying it.
    [InlineData("""<e:param name="1t" type="number" />""", "1t")]
    [InlineData("""<e:param name="sin" type="number" />""", "sin")]
    // A redeclaration is marked where it is redeclared, not where the name was first used. The name
    // rule runs before the type is read, which is the language's fixed visit order.
    [InlineData("""<e:param name="hue" type="number" /><e:param name="hue" type="colour" />""", "hue")]
    // The type is what is unknown, so the type is what is marked.
    [InlineData("""<e:param name="t" type="colour" />""", "colour")]
    // A range on something with no range: the one to delete is the first one written.
    [InlineData("""<e:param name="tint" type="color" min="0" max="1" />""", "0")]
    [InlineData("""<e:param name="on" type="boolean" step="2" />""", "2")]
    [InlineData("""<e:param name="theme" type="string" min="0" max="1" />""", "0")]
    // Half a range points at the half that is there.
    [InlineData("""<e:param name="t" type="number" min="3" />""", "3")]
    [InlineData("""<e:param name="t" type="number" max="7" />""", "7")]
    public void A_Mistake_Is_Reported_Where_It_Was_Written(string body, string offender)
    {
        var markup = Markup(body);

        SvgExpressionDeclarations.Parse(markup, out var diagnostics);

        var diagnostic = diagnostics[diagnostics.Count - 1];

        Assert.Equal(markup.LastIndexOf(offender, StringComparison.Ordinal), diagnostic.Position);
    }

    [Theory]
    // Nothing of its own to point at, so it points at the declaration that is missing it.
    [InlineData("""<e:param name="t" />""")]
    [InlineData("""<e:param type="number" />""")]
    public void A_Rule_About_Something_Left_Out_Points_At_The_Declaration(string body)
    {
        var markup = Markup(body);

        SvgExpressionDeclarations.Parse(markup, out var diagnostics);

        Assert.Equal(markup.IndexOf("e:param", StringComparison.Ordinal), Assert.Single(diagnostics).Position);
    }

    [Fact]
    public void A_Let_With_Nothing_In_It_Points_At_The_Let()
    {
        // Whitespace is not an expression, and underlining the whitespace would say nothing.
        var markup = Markup("""<e:let name="w">   </e:let>""");

        SvgExpressionDeclarations.Parse(markup, out var diagnostics);

        Assert.Equal(markup.IndexOf("e:let", StringComparison.Ordinal), Assert.Single(diagnostics).Position);
    }

    [Fact]
    public void A_Document_That_Is_Not_Well_Formed_Says_Where_It_Stopped()
    {
        // Reported here and nowhere else: the throwing reader contributes no declarations and says
        // nothing, because the SVG parser is the authority on well-formedness. Saying where is still
        // worth doing, and this is the one place holding the text.
        var markup = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}">
              <defs><e:code><e:param name="t" type="number" /></e:code>
            </svg>
            """;

        var declarations = SvgExpressionDeclarations.Parse(markup, out var diagnostics);

        Assert.True(declarations.IsEmpty);
        Assert.True(Assert.Single(diagnostics).Position > 0);

        // And still not a failure: reading a malformed document must not throw here.
        Assert.True(SvgExpressionDeclarations.Parse(markup).IsEmpty);
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
