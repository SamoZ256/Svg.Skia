// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using Xunit;

namespace Svg.PaintCode.UnitTests;

public class PaintCodeExpressionTranslatorTests
{
    [Theory]
    // The four operators XML would otherwise need escaped, said as words instead.
    [InlineData("a && b", "a and b")]
    [InlineData("a || b", "a or b")]
    [InlineData("!a", "not a")]
    [InlineData("x < y", "x lt y")]
    [InlineData("x <= y", "x le y")]
    // These need none, so they are left alone.
    [InlineData("x > y", "x > y")]
    [InlineData("x >= y", "x >= y")]
    [InlineData("x == y", "x == y")]
    [InlineData("x != y", "x != y")]
    // A remainder is an operator there and a function here, because % is a suffix on a number.
    [InlineData("x % y", "mod(x, y)")]
    [InlineData("x * y % 10", "mod(x * y, 10)")]
    // Degrees to radians.
    [InlineData("cos(x)", "cos((x) * pi / 180)")]
    [InlineData("sin(x * 300 - 240) * 76 + 90", "sin((x * 300 - 240) * pi / 180) * 76 + 90")]
    // Fractions of one to bytes, with the alpha left as it is.
    [InlineData("makeColor(x, y, 1, 0.5)", "rgba((x) * 255, (y) * 255, (1) * 255, 0.5)")]
    // Shape, precedence and the author's own brackets, all kept.
    [InlineData("(enabled && state) ? a : b", "(enabled and state) ? a : b")]
    [InlineData("abs(x - 0.5) * 4 + 0.5", "abs(x - 0.5) * 4 + 0.5")]
    [InlineData("-x * 360", "-x * 360")]
    [InlineData("x ? true : false", "x ? true : false")]
    [InlineData("',' + s", "',' + s")]
    // PaintCode's own name for the only crossing from a number to the words of a label.
    [InlineData("stringFromNumber(x * 100)", "str(x * 100)")]
    // The joins that do nothing, which is how its editor spells a property that follows a variable.
    [InlineData("x + 0", "x")]
    [InlineData("x * 1", "x")]
    public void An_Expression_Is_Rewritten_Into_One_The_Extension_Reads(string source, string expected)
    {
        Assert.True(PaintCodeExpressionTranslator.TryTranslate(source, Scope(), out var expression, out var refusal), refusal);
        Assert.Equal(expected, expression);
    }

    [Theory]
    // Each refused by name rather than guessed at.
    [InlineData("hypot(x, y)", "hypot")]
    [InlineData("bound.size.height", "reads part of a value")]
    [InlineData("nowhere", "is not a name this document declares")]
    [InlineData("x +", "a value was expected")]
    public void An_Expression_It_Cannot_Say_Refuses_By_Name(string source, string reason)
    {
        Assert.False(PaintCodeExpressionTranslator.TryTranslate(source, Scope(), out _, out var refusal));
        Assert.Contains(reason, refusal);
    }

    [Fact]
    public void A_Constant_Is_Written_Into_The_Expression_That_Names_It()
    {
        Assert.True(PaintCodeExpressionTranslator.TryTranslate("state ? navy : navy", Scope(), out var expression, out _));
        Assert.Equal("state ? #00003cff : #00003cff", expression);
    }

    [Fact]
    public void A_Rectangle_Nothing_Drives_Is_Folded_To_The_Number_It_Already_Is()
    {
        Assert.True(PaintCodeExpressionTranslator.TryTranslate("x * area.size.width", Scope(), out var expression, out var refusal), refusal);
        Assert.Equal("x * 117", expression);
    }

    /// <summary>
    /// Where an integer meets a number, the translation says num(), since the language never
    /// converts on its own and PaintCode has only the one numeric type.
    /// </summary>
    /// <remarks>
    /// n is a whole number the integer guess retypes and f is a fraction it leaves alone. A whole
    /// literal needs nothing, the checker settling it to whichever type is beside it; a division
    /// of integers is real in PaintCode, so it is written as one.
    /// </remarks>
    [Theory]
    [InlineData("n * f", "num(n) * f")]
    [InlineData("f * n", "f * num(n)")]
    [InlineData("n * 2", "n * 2")]
    [InlineData("n * 2 * f", "num(n * 2) * f")]
    [InlineData("n / 2", "num(n) / 2")]
    [InlineData("n == 2", "n == 2")]
    [InlineData("n < f", "num(n) lt f")]
    [InlineData("n % 3", "mod(n, 3)")]
    [InlineData("state ? n : f", "state ? num(n) : f")]
    [InlineData("state ? n : 3", "state ? n : 3")]
    [InlineData("abs(n)", "abs(n)")]
    [InlineData("max(n, f)", "max(num(n), f)")]
    [InlineData("floor(n)", "floor(num(n))")]
    [InlineData("sin(n)", "sin((num(n)) * pi / 180)")]
    [InlineData("stringFromNumber(n)", "str(n)")]
    [InlineData("-n * f", "num(-n) * f")]
    public void An_Integer_Meets_A_Number_Through_Num(string source, string expected)
    {
        Assert.True(PaintCodeExpressionTranslator.TryTranslate(source, Scope(integers: true), out var expression, out var refusal), refusal);
        Assert.Equal(expected, expression);
    }

    [Fact]
    public void A_Rebound_Name_Keeps_The_Sort_Of_What_Was_Put_In_Its_Place()
    {
        var scope = Scope(integers: true);

        // What an instance pinned n to: a translation the same declarations produced, and so one
        // they remember the sort of.
        Assert.True(PaintCodeExpressionTranslator.TryTranslate("f * 4", scope, out var pinned, out _));

        var overrides = new System.Collections.Generic.Dictionary<string, string> { ["n"] = pinned };

        Assert.True(PaintCodeExpressionTranslator.TryTranslate("n * f", scope, out var expression, out var refusal, overrides), refusal);
        Assert.Equal("(f * 4) * f", expression);
    }

    private static PaintCodeDeclarations Scope(bool integers = false)
        => PaintCodeDeclarations.Of(PaintCodeDocument.Parse(ScopeDocument.Bytes()), integers);
}
