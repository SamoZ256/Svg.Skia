using System.Linq;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Expressions;
using Svg.Expressions;
using Xunit;

namespace Svg.Skia.UnitTests;

public class SvgCodeDeclarationsTests
{
    private const string Ns = SvgCodeDeclarations.Namespace;

    [Fact]
    public void Plain_Svg_Yields_Empty_Declarations()
    {
        var declarations = SvgCodeDeclarations.Parse("""
            <svg xmlns="http://www.w3.org/2000/svg" width="10" height="10" />
            """);

        Assert.True(declarations.IsEmpty);
    }

    [Fact]
    public void Params_And_Lets_Are_Read_In_Document_Order()
    {
        var declarations = SvgCodeDeclarations.Parse($"""
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
        var declarations = SvgCodeDeclarations.Parse($"""
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
        var declarations = SvgCodeDeclarations.Parse("""
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
        var error = Assert.Throws<ExprException>(() => SvgCodeDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code><e:param name="t" type="float" /></e:code></defs>
            </svg>
            """));

        Assert.Contains("Unknown type 'float'", error.Message);
    }

    [Fact]
    public void Redeclaring_A_Builtin_Is_Rejected()
    {
        var error = Assert.Throws<ExprException>(() => SvgCodeDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code><e:param name="tau" type="number" /></e:code></defs>
            </svg>
            """));

        Assert.Contains("built-in", error.Message);
    }

    [Fact]
    public void Duplicate_Names_Are_Rejected()
    {
        var error = Assert.Throws<ExprException>(() => SvgCodeDeclarations.Parse($"""
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
        var forward = SvgCodeDeclarations.Parse($"""
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
        var declarations = SvgCodeDeclarations.Parse($"""
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
        var declarations = SvgCodeDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code>
                <e:param name="a" type="number" default="1" />
                <e:param name="b" type="number" default="a" />
              </e:code></defs>
            </svg>
            """);

        var error = Assert.Throws<ExprException>(() => declarations.DefaultCodeFor(declarations.Parameters[1]));
        Assert.Contains("Unknown name 'a'", error.Message);
    }

    [Fact]
    public void A_Default_Must_Match_The_Declared_Type()
    {
        // Declared on a number: a colour parameter is rejected for carrying any default at all,
        // which would mask the type check being tested here.
        var declarations = SvgCodeDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code><e:param name="t" type="number" default="#ff0000" /></e:code></defs>
            </svg>
            """);

        var error = Assert.Throws<ExprException>(() => declarations.DefaultCodeFor(declarations.Parameters[0]));
        Assert.Contains("must be a number", error.Message);
    }

    [Fact]
    public void A_Colour_Parameter_Cannot_Have_A_Default()
    {
        // `new SKColor(...)` is not a compile-time constant, so it cannot be a C# argument
        // default; the parameter is required instead.
        var declarations = SvgCodeDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code><e:param name="c" type="color" default="#ff0000" /></e:code></defs>
            </svg>
            """);

        var error = Assert.Throws<ExprException>(() => declarations.DefaultCodeFor(declarations.Parameters[0]));
        Assert.Contains("not a compile-time constant", error.Message);
    }

    [Fact]
    public void A_Parameter_Without_A_Default_Has_No_Default_Code()
    {
        var declarations = SvgCodeDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code><e:param name="c" type="color" /></e:code></defs>
            </svg>
            """);

        Assert.Null(declarations.DefaultCodeFor(declarations.Parameters[0]));
    }

    [Fact]
    public void Malformed_Xml_Yields_Empty_Rather_Than_Throwing()
    {
        // The SVG parser is the authority on well-formedness and reports it; this must not be a
        // second, competing failure path.
        var declarations = SvgCodeDeclarations.Parse($"""<svg xmlns:e="{Ns}"><defs><e:code>""");

        Assert.True(declarations.IsEmpty);
    }

    [Fact]
    public void Null_And_Empty_Input_Are_Safe()
    {
        Assert.True(SvgCodeDeclarations.Parse(null).IsEmpty);
        Assert.True(SvgCodeDeclarations.Parse("").IsEmpty);
        Assert.True(SvgCodeDeclarations.Parse("   ").IsEmpty);
    }
}
