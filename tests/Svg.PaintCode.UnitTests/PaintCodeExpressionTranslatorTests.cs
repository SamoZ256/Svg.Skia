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
    public void An_Expression_Is_Rewritten_Into_One_The_Extension_Reads(string source, string expected)
    {
        Assert.True(PaintCodeExpressionTranslator.TryTranslate(source, Scope(), out var expression, out var refusal), refusal);
        Assert.Equal(expected, expression);
    }

    [Theory]
    // The four the sample document actually runs into, each refused by name rather than guessed at.
    [InlineData("stringFromNumber(x)", "stringFromNumber")]
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

    private static PaintCodeDeclarations Scope() => PaintCodeDeclarations.Of(PaintCodeDocument.Parse(ScopeDocument.Bytes()));
}
