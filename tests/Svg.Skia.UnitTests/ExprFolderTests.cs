// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Expressions;
using Svg.Expressions;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// What folding leaves of an expression. Whether the folded answer is right is
/// <c>ExprEvaluatorDifferentialTests</c>' job; these pin what is folded at all.
/// </summary>
public class ExprFolderTests
{
    private static Dictionary<string, ExprType> Symbols() => new(StringComparer.Ordinal)
    {
        ["t"] = ExprType.Number,
        ["steps"] = ExprType.Integer,
        ["hot"] = ExprType.Boolean
    };

    private static string Code(string source) => new ExprCompiler(Symbols(), null, fold: true).Compile(source).Code;

    [Theory]
    [InlineData("!true", "false")]
    [InlineData("!(1 > 2)", "true")]
    [InlineData("1 + 2 * 3", "7f")]
    [InlineData("1 / 60", "0.016666668f")]
    [InlineData("-(2)", "(-2f)")]
    [InlineData("-0", "(-0f)")]
    [InlineData("0 / 0", "float.NaN")]
    [InlineData("pi * 2", "6.2831855f")]
    [InlineData("rgb(255, 0, 0)", "new SKColor(255, 0, 0, 255)")]
    [InlineData("'a' + str(len('bc'))", "\"a2\"")]
    [InlineData("int(7.9) / int(0.2)", "2147483647")]
    // C# refuses this one written out as arithmetic (CS0220), and wraps it at run time.
    [InlineData("-int(-3000000000)", "(-2147483648)")]
    public void A_Constant_Expression_Becomes_Its_Value(string source, string expected)
        => Assert.Equal(expected, Code(source));

    [Theory]
    [InlineData("true ? t : 1", "t")]
    [InlineData("1 > 2 ? t : t * 2", "(t * 2f)")]
    [InlineData("false and hot", "false")]
    [InlineData("true and hot", "hot")]
    [InlineData("true or hot", "true")]
    [InlineData("false or hot", "hot")]
    [InlineData("t * (1 + 1)", "(t * 2f)")]
    [InlineData("hot ? 1 + 1 : 3", "(hot ? 2f : 3f)")]
    public void Only_What_Is_Known_Folds(string source, string expected)
        => Assert.Equal(expected, Code(source));

    [Theory]
    // No identity that a NaN or a negative zero breaks.
    [InlineData("t * 0", "(t * 0f)")]
    [InlineData("t + 0", "(t + 0f)")]
    // The right operand decides nothing on its own: hot is still evaluated either way.
    [InlineData("hot and true", "(hot && true)")]
    [InlineData("hot and false", "(hot && false)")]
    // A bare constant reads better as its name.
    [InlineData("pi", "MathF.PI")]
    // The netstandard2.0 build computes these up to an ulp from MathF.
    [InlineData("sin(0)", "MathF.Sin(0f)")]
    [InlineData("pow(2, 1 + 1)", "MathF.Pow(2f, 2f)")]
    // Answers that depend on the runtime: casing tables, and .NET Framework's 7-digit float.
    [InlineData("upper('a')", "SvgUpper(\"a\")")]
    [InlineData("lower('A')", "SvgLower(\"A\")")]
    [InlineData("str(1 / 3)", "SvgStr(0.33333334f)")]
    // Math.Clamp throws on a reversed range; that stays the drawing's to throw.
    [InlineData("clamp(5, 3, 1)", "Math.Clamp(5f, 3f, 1f)")]
    public void What_Cannot_Fold_Safely_Stays_Written(string source, string expected)
        => Assert.Equal(expected, Code(source));

    [Fact]
    public void A_Compiler_That_Does_Not_Fold_Emits_What_Was_Written()
        => Assert.Equal("(!true)", new ExprCompiler(Symbols()).Compile("!true").Code);

    [Fact]
    public void A_Constant_Let_Folds_Into_Every_Use_And_Needs_No_Local()
    {
        var declarations = SvgExpressionDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{SvgExpressionDeclarations.Namespace}" width="10" height="10">
              <defs><e:code>
                <e:param name="t" type="number" default="0" />
                <e:let name="half">1 / 2</e:let>
                <e:let name="shown">!(half > 1)</e:let>
                <e:let name="wave">t * half</e:let>
              </e:code></defs>
            </svg>
            """);

        var (compiler, lets) = declarations.Resolve();

        var let = Assert.Single(lets);
        Assert.Equal("wave", let.Name);
        Assert.Equal("(t * 0.5f)", let.Code);

        Assert.True(compiler.TryConstant("shown", ExprType.Boolean, "The condition", out var shown));
        Assert.True(shown.AsBoolean);
        Assert.False(compiler.TryConstant("wave", out _));
    }

    [Fact]
    public void A_Typed_Let_Folds_At_Its_Declared_Type()
    {
        // A whole literal is a number unless something says otherwise, and the declaration does.
        var declarations = SvgExpressionDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{SvgExpressionDeclarations.Namespace}" width="10" height="10">
              <defs><e:code>
                <e:param name="steps" type="integer" default="4" />
                <e:let name="n" type="integer">3</e:let>
                <e:let name="half" type="integer">steps / 2</e:let>
              </e:code></defs>
            </svg>
            """);

        var (compiler, lets) = declarations.Resolve();

        Assert.Equal("0.3f", compiler.CompileTo("num(n) / 10", ExprType.Number, "The opacity"));
        var half = Assert.Single(lets);
        Assert.Equal(ExprType.Integer, half.Type);
    }
}
