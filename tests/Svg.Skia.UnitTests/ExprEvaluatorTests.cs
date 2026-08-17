// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using Svg.Expressions;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// The evaluator's own behaviour: binding values to declarations, and the errors that come out when
/// that cannot be done.
/// </summary>
/// <remarks>
/// What each expression evaluates <i>to</i> is covered by <c>ExprEvaluatorDifferentialTests</c>,
/// which compares against compiled generated code rather than against a number written here — a
/// literal expected value would only ever prove that the evaluator agrees with whoever typed it.
/// This file is about the surface around that.
/// </remarks>
public class ExprEvaluatorTests
{
    private const string Ns = SvgExpressionDeclarations.Namespace;

    private static SvgExpressionDeclarations Declarations(string body)
        => SvgExpressionDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code>{body}</e:code></defs>
            </svg>
            """);

    private static Dictionary<string, ExprValue> Values(params (string Name, ExprValue Value)[] values)
    {
        var result = new Dictionary<string, ExprValue>(StringComparer.Ordinal);

        foreach (var (name, value) in values)
        {
            result[name] = value;
        }

        return result;
    }

    [Fact]
    public void A_Supplied_Value_Is_Used()
    {
        var declarations = Declarations("""<e:param name="t" type="number" default="0" />""");
        var evaluator = ExprEvaluator.Create(declarations, Values(("t", ExprValue.Number(3f))));

        Assert.Equal(3f, evaluator.Evaluate("t").AsNumber);
    }

    [Fact]
    public void A_Declared_Default_Fills_In_For_An_Unsupplied_Value()
    {
        var declarations = Declarations("""<e:param name="t" type="number" default="0.25" />""");

        Assert.Equal(0.25f, ExprEvaluator.Create(declarations).Evaluate("t").AsNumber);
    }

    [Fact]
    public void A_Parameter_With_Neither_A_Value_Nor_A_Default_Is_An_Error()
    {
        // The same rule the generated code enforces, where a parameter without a default is simply
        // required. Falling back to the placeholder here would make a viewer disagree with svgc
        // about what the document means.
        var declarations = Declarations("""<e:param name="tint" type="color" />""");

        var error = Assert.Throws<ExprException>(() => ExprEvaluator.Create(declarations));

        Assert.Contains("No value was supplied for 'tint'", error.Message);
    }

    [Fact]
    public void A_Value_Of_The_Wrong_Type_Is_An_Error()
    {
        var declarations = Declarations("""<e:param name="tint" type="color" />""");

        var error = Assert.Throws<ExprException>(
            () => ExprEvaluator.Create(declarations, Values(("tint", ExprValue.Number(1f)))));

        Assert.Contains("'tint'", error.Message);
        Assert.Contains("number", error.Message);
        Assert.Contains("colour", error.Message);
    }

    [Fact]
    public void A_Value_For_An_Undeclared_Name_Is_Ignored()
    {
        // Not symmetric with the missing case on purpose: rendering with a value missing is
        // under-specified and worth failing, while a host holding a stale value after an edit
        // removed a parameter is ordinary and must not stop the drawing appearing.
        var declarations = Declarations("""<e:param name="t" type="number" default="1" />""");

        var evaluator = ExprEvaluator.Create(
            declarations,
            Values(("t", ExprValue.Number(2f)), ("gone", ExprValue.Number(9f))));

        Assert.Equal(2f, evaluator.Evaluate("t").AsNumber);
    }

    [Fact]
    public void A_Default_Cannot_See_Another_Parameter()
    {
        // Evaluated against an empty table, matching the code generator: in C# an argument default
        // is a compile-time constant, so an ordering dependency between two of them could not be
        // honoured, and the runtime keeps the same restriction rather than quietly allowing more.
        var declarations = Declarations("""
            <e:param name="a" type="number" default="1" />
            <e:param name="b" type="number" default="a" />
            """);

        var error = Assert.Throws<ExprException>(() => ExprEvaluator.Create(declarations));

        Assert.Contains("Unknown name 'a'", error.Message);
    }

    [Fact]
    public void A_Default_Must_Match_The_Declared_Type()
    {
        var declarations = Declarations("""<e:param name="t" type="number" default="#ff0000" />""");

        var error = Assert.Throws<ExprException>(() => ExprEvaluator.Create(declarations));

        Assert.Contains("must be a number", error.Message);
    }

    [Fact]
    public void Lets_Resolve_In_Order_And_See_The_Parameters()
    {
        var declarations = Declarations("""
            <e:param name="t" type="number" default="2" />
            <e:let name="doubled">t * 2</e:let>
            <e:let name="plusOne">doubled + 1</e:let>
            """);

        var evaluator = ExprEvaluator.Create(declarations);

        Assert.Equal(4f, evaluator.Evaluate("doubled").AsNumber);
        Assert.Equal(5f, evaluator.Evaluate("plusOne").AsNumber);
    }

    [Fact]
    public void A_Let_Cannot_See_One_Declared_Below_It()
    {
        var declarations = Declarations("""
            <e:let name="early">later + 1</e:let>
            <e:let name="later">1</e:let>
            """);

        var error = Assert.Throws<ExprException>(() => ExprEvaluator.Create(declarations));

        Assert.Contains("Unknown name 'later'", error.Message);
    }

    [Fact]
    public void A_Let_Takes_Its_Type_From_Its_Expression()
    {
        var declarations = Declarations("""<e:let name="tone">hsl(200, 0.5, 0.5)</e:let>""");

        Assert.Equal(ExprType.Color, ExprEvaluator.Create(declarations).Evaluate("tone").Type);
    }

    [Fact]
    public void Empty_Declarations_Still_Evaluate_A_Literal_Expression()
    {
        var evaluator = ExprEvaluator.Create(SvgExpressionDeclarations.Empty);

        Assert.Equal(ExprType.Color, evaluator.Evaluate("rgb(255, 0, 0)").Type);
    }

    [Fact]
    public void EvaluateTo_Rejects_The_Wrong_Result_Type()
    {
        var evaluator = ExprEvaluator.Create(SvgExpressionDeclarations.Empty);

        var error = Assert.Throws<ExprException>(
            () => evaluator.EvaluateTo("#ff0000", ExprType.Number, "An opacity expression"));

        Assert.Contains("An opacity expression must be a number expression", error.Message);
    }

    [Fact]
    public void An_Unknown_Name_Still_Reports_With_A_Caret()
    {
        // Position and expression text survive into the diagnostic, which is what makes an
        // authoring mistake findable rather than just refused.
        var evaluator = ExprEvaluator.Create(SvgExpressionDeclarations.Empty);

        var error = Assert.Throws<ExprException>(() => evaluator.Evaluate("1 + nope"));

        Assert.Equal(4, error.Position);
        Assert.Contains("1 + nope", error.ToDiagnostic());
        Assert.Contains("^", error.ToDiagnostic());
    }

    [Fact]
    public void A_Symbol_Declared_Without_A_Bound_Value_Is_A_Caller_Error()
    {
        // Reachable only by constructing the evaluator directly with a symbol table and value map
        // that disagree. Create() cannot produce this, which is the point of resolving every value
        // up front, but the message should still say which name it was.
        var symbols = new Dictionary<string, ExprType>(StringComparer.Ordinal) { ["t"] = ExprType.Number };
        var evaluator = new ExprEvaluator(symbols, new Dictionary<string, ExprValue>(StringComparer.Ordinal));

        var error = Assert.Throws<ExprException>(() => evaluator.Evaluate("t"));

        Assert.Contains("No value was bound for 't'", error.Message);
    }

    [Fact]
    public void An_ExprValue_Rejects_Being_Read_As_The_Wrong_Type()
    {
        var number = ExprValue.Number(1f);

        var error = Assert.Throws<InvalidOperationException>(() => number.Red);

        Assert.Contains("number", error.Message);
        Assert.Contains("colour", error.Message);
    }

    [Fact]
    public void ExprValue_Equality_Treats_NaN_As_Equal_To_Itself()
    {
        // Conventional .NET equality, deliberately unlike the language's == operator, which is C#
        // float comparison where NaN equals nothing. The evaluator does not route == through here;
        // that asymmetry is covered by the differential suite's NaN cases.
        Assert.Equal(ExprValue.Number(float.NaN), ExprValue.Number(float.NaN));
        Assert.NotEqual(ExprValue.Number(1f), ExprValue.Boolean(true));
        Assert.Equal(ExprValue.Color(1, 2, 3, 4), ExprValue.Color(1, 2, 3, 4));
        Assert.NotEqual(ExprValue.Color(1, 2, 3, 4), ExprValue.Color(1, 2, 3, 5));
    }
}
