using System.Collections.Generic;
using Svg.CodeGen.Skia.Expressions;
using Svg.Expressions;
using Xunit;

namespace Svg.Skia.UnitTests;

public class ExprCompilerTests
{
    private static readonly Dictionary<string, ExprType> Symbols = new()
    {
        ["t"] = ExprType.Number,
        ["tint"] = ExprType.Color,
        ["bold"] = ExprType.Boolean,
        ["theme"] = ExprType.String
    };

    private static string Code(string source) => new ExprCompiler(Symbols).Compile(source).Code;

    private static ExprType Type(string source) => new ExprCompiler(Symbols).Compile(source).Type;

    private static ExprException Error(string source)
        => Assert.Throws<ExprException>(() => new ExprCompiler(Symbols).Compile(source));

    // ---- literals -------------------------------------------------------------------------

    [Fact]
    public void Numbers_Emit_Float_Literals()
    {
        Assert.Equal("1f", Code("1"));
        Assert.Equal("1.5f", Code("1.5"));
        Assert.Equal("0.5f", Code(".5"));
    }

    [Fact]
    public void Percentages_Are_A_Suffix_Not_An_Operator()
    {
        Assert.Equal("0.55f", Code("55%"));
        Assert.Equal("0.074f", Code("7.4%"));
    }

    [Fact]
    public void A_Bare_Percent_Sign_Is_Rejected_With_A_Hint()
    {
        Assert.Contains("mod(a, b)", Error("t % 2").Message);
    }

    [Theory]
    [InlineData("#f80", "new SKColor(255, 136, 0, 255)")]
    [InlineData("#ff8800", "new SKColor(255, 136, 0, 255)")]
    [InlineData("#ff880080", "new SKColor(255, 136, 0, 128)")]
    [InlineData("#f808", "new SKColor(255, 136, 0, 136)")]
    public void Colour_Literals_Support_3_4_6_And_8_Digits(string source, string expected)
    {
        Assert.Equal(expected, Code(source));
        Assert.Equal(ExprType.Color, Type(source));
    }

    [Fact]
    public void A_Colour_With_The_Wrong_Digit_Count_Is_Rejected()
    {
        Assert.Contains("3, 4, 6 or 8 hex digits", Error("#ff880").Message);
        Assert.Contains("3, 4, 6 or 8 hex digits", Error("#ff").Message);
    }

    [Fact]
    public void Booleans_And_Constants_Resolve()
    {
        Assert.Equal("true", Code("true"));
        Assert.Equal("MathF.PI", Code("pi"));
        Assert.Equal("(MathF.PI * 2f)", Code("tau"));
    }

    // ---- operators ------------------------------------------------------------------------

    [Fact]
    public void Arithmetic_Follows_Precedence()
    {
        Assert.Equal("(1f + (2f * 3f))", Code("1 + 2 * 3"));
        Assert.Equal("((1f + 2f) * 3f)", Code("(1 + 2) * 3"));
    }

    [Fact]
    public void Comparison_Binds_Looser_Than_Arithmetic()
    {
        Assert.Equal("((t + 1f) > 2f)", Code("t + 1 > 2"));
        Assert.Equal(ExprType.Boolean, Type("t > 2"));
    }

    [Fact]
    public void Logical_Operators_Bind_Looser_Than_Comparison()
    {
        Assert.Equal("((t > 1f) && (t < 5f))", Code("t > 1 && t < 5"));
    }

    [Theory]
    [InlineData("t lt 1", "t < 1")]
    [InlineData("t le 1", "t <= 1")]
    [InlineData("t gt 1", "t > 1")]
    [InlineData("t ge 1", "t >= 1")]
    [InlineData("t eq 1", "t == 1")]
    [InlineData("t ne 1", "t != 1")]
    [InlineData("bold and bold", "bold && bold")]
    [InlineData("bold or bold", "bold || bold")]
    [InlineData("not bold", "!bold")]
    public void Word_Operators_Are_Aliases_For_The_Symbols(string words, string symbols)
    {
        // XML forces '<' and '&' to be escaped, so the word forms exist to keep expressions
        // readable inside an SVG. They must compile to exactly the same thing.
        Assert.Equal(Code(symbols), Code(words));
    }

    [Fact]
    public void Word_Operators_Mix_With_Symbols_And_Keep_Precedence()
    {
        Assert.Equal(Code("t > 1 && t < 5"), Code("t gt 1 and t lt 5"));
        Assert.Equal(Code("(t + 1) > 2"), Code("t + 1 gt 2"));
    }

    [Fact]
    public void A_Keyword_Cannot_Be_Used_As_A_Name()
    {
        // 'and' lexes as an operator, so a variable of that name could never be referenced.
        Assert.True(ExprCompiler.IsReservedName("and"));
        Assert.True(ExprCompiler.IsReservedName("lt"));
        Assert.False(ExprCompiler.IsReservedName("android"));
    }

    [Fact]
    public void Identifiers_Merely_Starting_With_A_Keyword_Are_Not_Keywords()
    {
        var compiler = new ExprCompiler(new Dictionary<string, ExprType> { ["andy"] = ExprType.Number });

        Assert.Equal("andy", compiler.Compile("andy").Code);
    }

    [Fact]
    public void Unary_Operators_Work()
    {
        Assert.Equal("(-t)", Code("-t"));
        Assert.Equal("(!bold)", Code("!bold"));
    }

    [Fact]
    public void Arithmetic_On_Colours_Is_Rejected_With_A_Hint()
    {
        Assert.Contains("mix(a, b, t)", Error("tint + tint").Message);
    }

    [Fact]
    public void Comparing_Different_Types_Is_Rejected()
    {
        Assert.Contains("compares a number with a boolean", Error("t == bold").Message);
    }

    [Fact]
    public void Ordering_Colours_Is_Rejected()
    {
        Assert.Contains("expects a number", Error("tint > tint").Message);
    }

    // ---- conditionals ---------------------------------------------------------------------

    [Fact]
    public void Ternary_Selects_On_A_Boolean()
    {
        Assert.Equal("(bold ? 1f : 2f)", Code("bold ? 1 : 2"));
        Assert.Equal(ExprType.Number, Type("bold ? 1 : 2"));
    }

    [Fact]
    public void Ternary_Chains_To_The_Right()
    {
        Assert.Equal(
            "((t > 2f) ? #ff0000 : ((t > 1f) ? #ffaa00 : #00aa44))"
                .Replace("#ff0000", "new SKColor(255, 0, 0, 255)")
                .Replace("#ffaa00", "new SKColor(255, 170, 0, 255)")
                .Replace("#00aa44", "new SKColor(0, 170, 68, 255)"),
            Code("t > 2 ? #ff0000 : t > 1 ? #ffaa00 : #00aa44"));
    }

    [Fact]
    public void Ternary_Requires_A_Boolean_Condition()
    {
        Assert.Contains("must be a boolean", Error("t ? 1 : 2").Message);
    }

    [Fact]
    public void Ternary_Branches_Must_Agree()
    {
        Assert.Contains("same type", Error("bold ? 1 : #ff0000").Message);
    }

    // ---- functions ------------------------------------------------------------------------

    [Fact]
    public void Math_Functions_Map_To_The_Bcl()
    {
        Assert.Equal("MathF.Sin(t)", Code("sin(t)"));
        Assert.Equal("Math.Clamp(t, 0f, 1f)", Code("clamp(t, 0, 1)"));
        Assert.Equal("(t % 2f)", Code("mod(t, 2)"));
    }

    [Fact]
    public void Colour_Functions_Map_To_Helpers()
    {
        Assert.Equal("SvgHsl(200f, 0.5f, 0.5f)", Code("hsl(200, 50%, 50%)"));
        Assert.Equal(ExprType.Color, Type("hsl(200, 50%, 50%)"));
        Assert.Equal("SvgMix(tint, new SKColor(0, 0, 0, 255), 0.5f)", Code("mix(tint, #000000, 0.5)"));
        Assert.Equal("SvgWithAlpha(tint, 0.5f)", Code("withAlpha(tint, 0.5)"));
    }

    [Fact]
    public void Wrong_Arity_Is_Rejected()
    {
        Assert.Contains("takes 1 argument(s), but 2 were given", Error("sin(1, 2)").Message);
    }

    [Fact]
    public void Wrong_Argument_Type_Is_Rejected()
    {
        Assert.Contains("Argument 1 of 'sin' must be a number", Error("sin(tint)").Message);
    }

    [Fact]
    public void Unknown_Function_Lists_What_Is_Available()
    {
        var message = Error("wobble(1)").Message;

        Assert.Contains("Unknown function 'wobble'", message);
        Assert.Contains("clamp", message);
    }

    // ---- names ----------------------------------------------------------------------------

    [Fact]
    public void Unknown_Name_Lists_What_Is_In_Scope()
    {
        var message = Error("nope").Message;

        Assert.Contains("Unknown name 'nope'", message);
        Assert.Contains("tint", message);
    }

    [Fact]
    public void Using_A_Function_As_A_Value_Is_Explained()
    {
        Assert.Contains("is a function; call it with arguments", Error("sin").Message);
    }

    // ---- diagnostics ----------------------------------------------------------------------

    [Fact]
    public void Errors_Carry_The_Offset_Of_The_Offending_Token()
    {
        var error = Error("1 + nope");

        Assert.Equal(4, error.Position);
    }

    [Fact]
    public void Diagnostics_Render_The_Expression_With_A_Caret()
    {
        var lines = Error("1 + nope").ToDiagnostic().Split('\n');

        Assert.Contains("Unknown name 'nope'", lines[0]);
        Assert.Equal("    1 + nope", lines[1].TrimEnd('\r'));
        Assert.Equal("        ^", lines[2].TrimEnd('\r'));
    }

    [Fact]
    public void Unbalanced_Parentheses_Are_Reported()
    {
        Assert.Contains("Expected ')'", Error("(1 + 2").Message);
    }

    [Fact]
    public void Trailing_Junk_Is_Reported()
    {
        Assert.Contains("after the end of the expression", Error("1 2").Message);
    }

    [Fact]
    public void An_Empty_Expression_Is_Reported()
    {
        Assert.Contains("empty", Assert.Throws<ExprException>(() => new ExprCompiler(Symbols).Compile("   ")).Message);
    }

    // ---- strings --------------------------------------------------------------------------

    [Theory]
    [InlineData("'dark'", "\"dark\"")]
    [InlineData("\"dark\"", "\"dark\"")]
    [InlineData("''", "\"\"")]
    public void A_String_Is_Written_With_Either_Quote(string source, string expected)
    {
        Assert.Equal(expected, Code(source));
        Assert.Equal(ExprType.String, Type(source));
    }

    [Theory]
    // Only the quote that opened the literal has to be escaped inside it.
    [InlineData(@"'it\'s'", "\"it's\"")]
    [InlineData("'it\"s'", "\"it\\\"s\"")]
    [InlineData(@"'a\\b'", @"""a\\b""")]
    [InlineData(@"'a\nb'", @"""a\nb""")]
    [InlineData(@"'a\tb'", @"""a\tb""")]
    public void The_Escapes_Are_Resolved_Once_And_Re_Emitted(string source, string expected)
    {
        Assert.Equal(expected, Code(source));
    }

    [Fact]
    public void A_String_That_Is_Never_Closed_Is_Reported()
    {
        Assert.Contains("no closing '", Error("'dark").Message);
        Assert.Contains("no closing \"", Error("\"dark").Message);
    }

    [Fact]
    public void An_Escape_The_Language_Does_Not_Have_Is_Reported()
    {
        Assert.Contains("is not an escape", Error(@"'a\qb'").Message);
    }

    [Fact]
    public void Strings_Compare_And_Choose_Like_Any_Other_Type()
    {
        Assert.Equal(ExprType.Boolean, Type("theme == 'dark'"));
        Assert.Equal("(theme != \"dark\")", Code("theme != 'dark'"));
        Assert.Equal(ExprType.Color, Type("theme == 'dark' ? #fff : #000"));
        Assert.Equal(ExprType.String, Type("bold ? theme : 'plain'"));
    }

    [Fact]
    public void A_String_Is_Not_A_Number_And_Says_So()
    {
        Assert.Contains("expects a number", Error("theme - 1").Message);
        Assert.Contains("expects a number", Error("theme < 'a'").Message);
        Assert.Contains("expects a boolean", Error("!theme").Message);
        Assert.Contains("compares a string with a number", Error("theme == 1").Message);
        Assert.Contains("must be a number", Error("sin('a')").Message);
    }

    [Fact]
    public void CompileTo_Enforces_The_Expected_Type()
    {
        var compiler = new ExprCompiler(Symbols);

        Assert.Equal("tint", compiler.CompileTo("tint", ExprType.Color, "A paint expression"));
        Assert.Contains(
            "must be a colour expression",
            Assert.Throws<ExprException>(() => compiler.CompileTo("t", ExprType.Color, "A paint expression")).Message);
    }
}
