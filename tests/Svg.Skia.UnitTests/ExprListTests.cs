// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Linq;
using Svg.Expressions;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// <c>number[]</c> and <c>color[]</c>: how a list is written, what the checker makes of one, and
/// what the evaluator answers for it.
/// </summary>
/// <remarks>
/// That the generated C# answers the same is <c>ExprEvaluatorDifferentialTests</c>' job; this is the
/// language on its own, with no back end that emits anything.
/// </remarks>
public class ExprListTests
{
    private const string Ns = SvgExpressionDeclarations.Namespace;

    private static Dictionary<string, ExprType> Symbols() => new(StringComparer.Ordinal)
    {
        ["t"] = ExprType.Number,
        ["steps"] = ExprType.Integer,
        ["tint"] = ExprType.Color,
        ["on"] = ExprType.Boolean,
        ["label"] = ExprType.String,
        ["xs"] = ExprType.NumberList,
        ["ys"] = ExprType.NumberList,
        ["cs"] = ExprType.ColorList
    };

    private static TypedExpr Check(string text) => new ExprChecker(Symbols()).Check(text);

    private static ExprType Type(string text) => Check(text).Type;

    private static ExprException Error(string text) => Assert.Throws<ExprException>(() => Check(text));

    private static ExprValue Numbers(params float[] values) => ExprValue.NumberList(values);

    private static ExprValue Colours(params uint[] packed)
        => ExprValue.ColorList(packed.Select(c => ExprValue.Color((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c)));

    private static ExprValue Evaluate(string text, params (string Name, ExprValue Value)[] values)
    {
        var bound = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

        return new ExprEvaluator(bound.ToDictionary(v => v.Key, v => v.Value.Type, StringComparer.Ordinal), bound).Evaluate(text);
    }

    private static SvgExpressionDeclarations Declarations(string body)
        => SvgExpressionDeclarations.Parse($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code>{body}</e:code></defs>
            </svg>
            """);

    // ---- lexing and parsing ---------------------------------------------------------------

    [Fact]
    public void Brackets_Are_Tokens_Of_Their_Own()
    {
        var tokens = ExprLexer.Tokenize("xs[ 1 ]");

        Assert.Equal(
            new[] { ExprTokenKind.Identifier, ExprTokenKind.OpenBracket, ExprTokenKind.Number, ExprTokenKind.CloseBracket, ExprTokenKind.End },
            tokens.Select(token => token.Kind));
        Assert.Equal(2, tokens[1].Position);
        Assert.Equal(6, tokens[3].Position);
    }

    [Fact]
    public void A_List_Literal_Parses_With_Its_Elements()
    {
        var list = Assert.IsType<ListExpr>(ExprParser.Parse("[1, t, 2 + 3]"));

        Assert.Equal(0, list.Position);
        Assert.Equal(3, list.Elements.Count);
        Assert.IsType<BinaryExpr>(list.Elements[2]);
        Assert.Empty(Assert.IsType<ListExpr>(ExprParser.Parse("[ ]")).Elements);
    }

    [Fact]
    public void Indexing_Follows_Any_Primary_And_Chains()
    {
        Assert.IsType<IdentifierExpr>(Assert.IsType<IndexExpr>(ExprParser.Parse("xs[0]")).Target);
        Assert.IsType<ListExpr>(Assert.IsType<IndexExpr>(ExprParser.Parse("[1, 2][0]")).Target);
        Assert.IsType<ConditionalExpr>(Assert.IsType<IndexExpr>(ExprParser.Parse("(on ? xs : ys)[1]")).Target);
        Assert.IsType<CallExpr>(Assert.IsType<IndexExpr>(ExprParser.Parse("abs(t)[0]")).Target);

        // Two deep, the outer one indexing the inner; the checker is what refuses it.
        var outer = Assert.IsType<IndexExpr>(ExprParser.Parse("xs[0][1]"));
        Assert.Equal(5, outer.Position);
        Assert.IsType<IndexExpr>(outer.Target);
    }

    [Fact]
    public void Indexing_Binds_Tighter_Than_Negation()
    {
        var negated = Assert.IsType<UnaryExpr>(ExprParser.Parse("-xs[0]"));

        Assert.IsType<IndexExpr>(negated.Operand);
    }

    [Theory]
    [InlineData("[1, 2", "Expected ']' to close the list.", 5)]
    [InlineData("[1 2]", "Expected ']' to close the list.", 3)]
    [InlineData("xs[0", "Expected ']' to close the index.", 4)]
    [InlineData("xs[0, 1]", "Expected ']' to close the index.", 4)]
    [InlineData("[1, ]", "Unexpected ']'.", 4)]
    [InlineData("xs[]", "Unexpected ']'.", 3)]
    [InlineData("]", "Unexpected ']'.", 0)]
    [InlineData("xs]", "Unexpected ']' after the end of the expression.", 2)]
    public void A_Malformed_List_Or_Index_Says_Where(string text, string message, int position)
    {
        var error = Assert.Throws<ExprException>(() => ExprParser.Parse(text));

        Assert.Equal(message, error.Message);
        Assert.Equal(position, error.Position);
    }

    // ---- what the checker makes of a list --------------------------------------------------

    [Theory]
    [InlineData("[0, 0.5, 1]", ExprType.NumberList)]
    [InlineData("[1, 2]", ExprType.NumberList)]
    [InlineData("[t, 1 + 2, -3]", ExprType.NumberList)]
    [InlineData("[#fff, tint, rgb(1, 2, 3)]", ExprType.ColorList)]
    [InlineData("on ? xs : [1]", ExprType.NumberList)]
    [InlineData("xs", ExprType.NumberList)]
    [InlineData("cs", ExprType.ColorList)]
    public void A_List_Is_The_List_Of_Its_Elements(string text, ExprType type)
        => Assert.Equal(type, Type(text));

    [Fact]
    public void A_Whole_Literal_In_A_List_Is_A_Number()
    {
        var list = Assert.IsType<TypedList>(Check("[1, 2]"));

        Assert.All(list.Elements, element => Assert.IsType<TypedNumber>(element));
    }

    [Fact]
    public void Numbers_And_Colours_Do_Not_Mix()
    {
        var error = Error("[1, #fff]");

        Assert.Equal("A list's elements must all be numbers or all be colours, but element 1 is a number and element 2 is a colour.", error.Message);
        Assert.Equal(1, error.Position);

        Assert.Equal(
            "A list's elements must all be numbers or all be colours, but element 1 is a colour and element 3 is a number.",
            Error("[#fff, tint, t]").Message);
    }

    [Fact]
    public void A_List_Holds_Neither_Integers_Nor_Anything_Else()
    {
        Assert.Equal(
            "Element 2 of the list is an integer, and a list holds numbers or colours. Use num() to make it a number.",
            Error("[t, steps]").Message);
        Assert.Equal("Element 1 of the list is a string, and a list holds numbers or colours.", Error("['a']").Message);
        Assert.Equal("Element 2 of the list is a boolean, and a list holds numbers or colours.", Error("[1, on]").Message);

        var nested = Error("[[1], [2]]");
        Assert.Equal("A list cannot hold another list.", nested.Message);
        Assert.Equal(1, nested.Position);
        Assert.Equal("A list cannot hold another list.", Error("[xs]").Message);
    }

    [Fact]
    public void An_Empty_List_Needs_A_Declared_Type()
    {
        Assert.StartsWith("An empty list needs a type.", Error("[]").Message);
        Assert.StartsWith("An empty list needs a type.", Error("on ? [] : []").Message);
        Assert.StartsWith("An empty list needs a type.", Error("len([])").Message);
        Assert.StartsWith("An empty list needs a type.", Error("[][0]").Message);
        Assert.Equal(4, Error("len([])").Position);

        var checker = new ExprChecker(Symbols());

        Assert.Equal(ExprType.NumberList, checker.CheckAs("[]", ExprType.NumberList, "The default").Type);
        Assert.Equal(ExprType.ColorList, checker.CheckAs("[]", ExprType.ColorList, "The default").Type);
        Assert.Equal(ExprType.ColorList, checker.CheckAs("on ? [] : []", ExprType.ColorList, "The default").Type);
    }

    [Fact]
    public void The_Other_Branch_Gives_An_Empty_List_Its_Type()
    {
        Assert.Equal(ExprType.NumberList, Type("on ? xs : []"));
        Assert.Equal(ExprType.ColorList, Type("on ? [] : cs"));
        Assert.Equal(ExprType.ColorList, Type("on ? [] : [#000]"));

        Assert.Equal("An empty list cannot be a number.", Error("on ? [] : 1").Message);
        Assert.Equal("An empty list cannot be a number.", Error("on ? 1 : []").Message);
        Assert.Equal("An empty list cannot be a string.", Error("on ? [] : label").Message);
    }

    [Fact]
    public void An_Empty_List_Is_Not_Another_Type()
    {
        var checker = new ExprChecker(Symbols());

        Assert.Equal("An empty list cannot be a number.", Assert.Throws<ExprException>(() => checker.CheckAs("[]", ExprType.Number, "x")).Message);
        Assert.Equal("An empty list cannot be a colour.", Assert.Throws<ExprException>(() => checker.CheckAs("[]", ExprType.Color, "x")).Message);
        Assert.Equal("An empty list cannot be an integer.", Error("xs[[]]").Message);

        // Passed where no list is taken, it is the wrong type rather than one without a type.
        Assert.Equal("An empty list cannot be a number.", Error("sin([])").Message);
        Assert.Equal("An empty list cannot be a string.", Error("upper([])").Message);
    }

    [Fact]
    public void A_List_Of_The_Wrong_Kind_Is_Refused_Where_A_Type_Is_Declared()
    {
        var error = Assert.Throws<ExprException>(
            () => new ExprChecker(Symbols()).CheckAs("[1, 2]", ExprType.ColorList, "The default for 'stops'"));

        Assert.Equal("The default for 'stops' must be a list of colours, but this one is a list of numbers.", error.Message);
    }

    [Theory]
    [InlineData("xs[0]", ExprType.Number)]
    [InlineData("xs[steps]", ExprType.Number)]
    [InlineData("xs[steps + 1]", ExprType.Number)]
    [InlineData("xs[len(xs) - 1]", ExprType.Number)]
    [InlineData("cs[1]", ExprType.Color)]
    [InlineData("[#fff, tint][int(t)]", ExprType.Color)]
    [InlineData("(on ? xs : ys)[0]", ExprType.Number)]
    [InlineData("-xs[0] * 2", ExprType.Number)]
    public void An_Element_Is_What_The_List_Holds(string text, ExprType type)
        => Assert.Equal(type, Type(text));

    [Fact]
    public void An_Index_Is_Whole()
    {
        var fractional = Error("xs[0.5]");
        Assert.Equal("A list's index must be an integer, but this one is a number. Use int() to make it one.", fractional.Message);
        Assert.Equal(3, fractional.Position);

        Assert.Contains("int()", Error("xs[t]").Message);
        Assert.Equal("A list's index must be an integer, but this one is a string.", Error("xs['a']").Message);
        Assert.Equal("A list's index must be an integer, but this one is a colour.", Error("cs[tint]").Message);
    }

    [Fact]
    public void Only_A_List_Can_Be_Indexed()
    {
        var error = Error("t[0]");

        Assert.Equal("Only a list can be indexed, but this is a number.", error.Message);
        Assert.Equal(1, error.Position);
        Assert.Equal("Only a list can be indexed, but this is a number.", Error("xs[0][1]").Message);
        Assert.Equal("Only a list can be indexed, but this is a string.", Error("label[0]").Message);
    }

    [Fact]
    public void Length_Has_An_Overload_For_Each_List()
    {
        Assert.Equal(ExprType.Integer, Type("len(xs)"));
        Assert.Equal(ExprType.Integer, Type("len(cs)"));
        Assert.Equal(ExprType.Integer, Type("len([1, 2, 3])"));
        Assert.Equal(ExprType.Integer, Type("len(label)"));

        var call = Assert.IsType<TypedCall>(Check("len(cs)"));
        Assert.Equal(ExprFunction.Len, call.Function);
        Assert.Equal(ExprType.ColorList, call.Arguments[0].Type);

        Assert.Equal("Argument 1 of 'len' must be a string, but it is a number.", Error("len(t)").Message);
    }

    [Theory]
    [InlineData("xs == ys", "Lists cannot be compared with '=='. Compare their elements instead, as in xs[0] == ys[0].")]
    [InlineData("cs != cs", "Lists cannot be compared with '!='. Compare their elements instead, as in xs[0] != ys[0].")]
    [InlineData("xs < ys", "Lists cannot be compared with '<'. Compare their elements instead, as in xs[0] < ys[0].")]
    [InlineData("xs >= 1", "Lists cannot be compared with '>='. Compare their elements instead, as in xs[0] >= ys[0].")]
    [InlineData("[] == []", "Lists cannot be compared with '=='. Compare their elements instead, as in xs[0] == ys[0].")]
    [InlineData("xs + 1", "'+' does not apply to lists. Index one to use an element, as in xs[0] + 1.")]
    [InlineData("1 - xs", "'-' does not apply to lists. Index one to use an element, as in xs[0] - 1.")]
    [InlineData("xs * ys", "'*' does not apply to lists. Index one to use an element, as in xs[0] * 1.")]
    [InlineData("[] / 2", "'/' does not apply to lists. Index one to use an element, as in xs[0] / 1.")]
    [InlineData("xs && on", "'&&' takes booleans, and a list is not one. Test its length instead, as in len(xs) > 0.")]
    [InlineData("on || cs", "'||' takes booleans, and a list is not one. Test its length instead, as in len(xs) > 0.")]
    [InlineData("-xs", "'-' does not apply to lists. Index one to negate an element, as in -xs[0].")]
    [InlineData("!cs", "'!' takes a boolean, and a list is not one. Test its length instead, as in len(xs) > 0.")]
    [InlineData("xs ? 1 : 2", "A list cannot be a condition. Test its length instead, as in len(xs) > 0.")]
    [InlineData("on ? xs : cs", "Both branches of '?:' must have the same type, but they are list of numbers and list of colours.")]
    [InlineData("on ? xs : 1", "Both branches of '?:' must have the same type, but they are list of numbers and number.")]
    [InlineData("abs(xs)", "Argument 1 of 'abs' must be a number, but it is a list of numbers.")]
    public void What_A_List_Cannot_Do_Is_Said_In_So_Many_Words(string text, string message)
        => Assert.Equal(message, Error(text).Message);

    [Fact]
    public void A_Bad_Name_Inside_A_List_Reports_The_Name()
    {
        Assert.StartsWith("Unknown name 'nope'", Error("[1, nope]").Message);
        Assert.StartsWith("Unknown name 'nope'", Error("nope[0]").Message);
        Assert.StartsWith("Unknown name 'nope'", Error("xs[nope]").Message);
    }

    // ---- how a list type is written ---------------------------------------------------------

    [Theory]
    [InlineData("number", ExprType.Number)]
    [InlineData("integer", ExprType.Integer)]
    [InlineData("color", ExprType.Color)]
    [InlineData("boolean", ExprType.Boolean)]
    [InlineData("string", ExprType.String)]
    [InlineData("number[]", ExprType.NumberList)]
    [InlineData("color[]", ExprType.ColorList)]
    public void A_Type_Reads_Back_As_It_Is_Written(string text, ExprType type)
    {
        Assert.Equal(type, ExprFunctions.ParseType(text, 0));
        Assert.Equal(text, ExprFunctions.NameOf(type));
    }

    [Theory]
    [InlineData("integer[]")]
    [InlineData("string[]")]
    [InlineData("boolean[]")]
    [InlineData("number[][]")]
    [InlineData("colour[]")]
    [InlineData("[]")]
    [InlineData("number []")]
    [InlineData("list")]
    public void Only_Numbers_And_Colours_Have_A_List(string text)
    {
        var error = Assert.Throws<ExprException>(() => ExprFunctions.ParseType(text, 7, SvgDeclarationPart.Type));

        Assert.Equal($"Unknown type '{text}'. Expected number, integer, color, boolean, string, number[] or color[].", error.Message);
        Assert.Equal(7, error.Position);
        Assert.Equal(SvgDeclarationPart.Type, error.Part);
    }

    [Fact]
    public void A_List_Is_Named_In_A_Sentence_As_One()
    {
        Assert.Equal("list of numbers", ExprFunctions.Describe(ExprType.NumberList));
        Assert.Equal("list of colours", ExprFunctions.Describe(ExprType.ColorList));
        Assert.Equal("A number list expression", ExprFunctions.DescribeUse(ExprType.NumberList));
        Assert.Equal("A colour list expression", ExprFunctions.DescribeUse(ExprType.ColorList));
        Assert.Equal("An integer expression", ExprFunctions.DescribeUse(ExprType.Integer));
    }

    // ---- the value -------------------------------------------------------------------------

    [Fact]
    public void A_List_Value_Is_Copied_On_The_Way_In()
    {
        var numbers = new[] { 0f, 0.5f, 1f };
        var colours = new List<ExprValue> { ExprValue.Color(255, 0, 0, 255) };

        var numberList = ExprValue.NumberList(numbers);
        var colourList = ExprValue.ColorList(colours);

        numbers[0] = 9f;
        colours.Add(ExprValue.Color(0, 0, 0, 0));

        Assert.Equal(new[] { 0f, 0.5f, 1f }, numberList.AsNumberList);
        Assert.Single(colourList.AsColorList);
    }

    [Fact]
    public void A_List_Value_Cannot_Be_Changed_Through_What_It_Hands_Out()
    {
        var value = Numbers(1f, 2f);

        Assert.False(value.AsNumberList is float[]);
        Assert.Throws<NotSupportedException>(() => ((IList<float>)value.AsNumberList)[0] = 5f);
        Assert.Throws<NotSupportedException>(() => ((IList<ExprValue>)Colours(0xff0000ff).AsColorList)[0] = default);
        Assert.Equal(1f, value.AsNumberList[0]);
    }

    [Fact]
    public void A_Colour_List_Holds_Colours()
    {
        var error = Assert.Throws<ArgumentException>(() => ExprValue.ColorList(new[] { ExprValue.Number(1f) }));

        Assert.Contains("cannot hold a number", error.Message);
        Assert.Throws<ArgumentNullException>(() => ExprValue.NumberList(null!));
        Assert.Throws<ArgumentNullException>(() => ExprValue.ColorList(null!));
    }

    [Fact]
    public void A_List_Value_Hands_Out_Its_Elements()
    {
        var colours = Colours(0x11223344, 0xffffff00).AsColorList;

        Assert.Equal(ExprType.Color, colours[0].Type);
        Assert.Equal((0x11, 0x22, 0x33, 0x44), (colours[0].Red, colours[0].Green, colours[0].Blue, colours[0].Alpha));
        Assert.Equal(0, colours[1].Alpha);

        var error = Assert.Throws<InvalidOperationException>(() => Numbers(1f).AsNumber);
        Assert.Equal("This value is a list of numbers, not a number.", error.Message);
        Assert.Throws<InvalidOperationException>(() => Numbers(1f).AsColorList);
        Assert.Throws<InvalidOperationException>(() => Colours().AsNumberList);
    }

    [Fact]
    public void Two_Lists_Are_Equal_Element_By_Element()
    {
        Assert.Equal(Numbers(0f, 0.5f), Numbers(0f, 0.5f));
        Assert.Equal(Numbers(0f, 0.5f).GetHashCode(), Numbers(0f, 0.5f).GetHashCode());
        Assert.Equal(Numbers(float.NaN), Numbers(float.NaN));
        Assert.Equal(Colours(0xff0000ff, 0x00000000), Colours(0xff0000ff, 0x00000000));
        Assert.Equal(Colours(0xff0000ff).GetHashCode(), Colours(0xff0000ff).GetHashCode());
        Assert.Equal(Numbers(), Numbers());

        Assert.NotEqual(Numbers(0f, 0.5f), Numbers(0.5f, 0f));
        Assert.NotEqual(Numbers(0f), Numbers(0f, 0f));
        Assert.NotEqual(Colours(0xff0000ff), Colours(0xff0000fe));
        Assert.NotEqual(Numbers(), Colours());
        Assert.NotEqual(Numbers(1f), ExprValue.Number(1f));
    }

    [Theory]
    [InlineData(new[] { 0f, 0.5f, 1f }, "[0, 0.5, 1]")]
    [InlineData(new[] { -2.25f, 0.1f }, "[-2.25, 0.1]")]
    [InlineData(new float[0], "[]")]
    public void A_Number_List_Is_Written_As_Its_Literal(float[] values, string text)
        => Assert.Equal(text, Numbers(values).ToString());

    [Fact]
    public void A_Colour_List_Is_Written_As_Its_Literal()
    {
        Assert.Equal("[#ff0000ff, #00000000]", Colours(0xff0000ff, 0x00000000).ToString());
        Assert.Equal("[]", Colours().ToString());
    }

    [Fact]
    public void A_List_Reads_Back_As_What_It_Was_Written_From()
    {
        foreach (var value in new[] { Numbers(0f, 0.5f, 1f), Numbers(-2.25f, 0.1f, 16777216f), Numbers(), Colours(0x1e40afff, 0x00000000), Colours() })
        {
            Assert.Equal(value, ExprEvaluator.Isolated.EvaluateTo(value.ToString(), value.Type, "The value"));
        }
    }

    // ---- evaluating ------------------------------------------------------------------------

    [Theory]
    [InlineData(-1, 10f)]
    [InlineData(-2147483648, 10f)]
    [InlineData(0, 10f)]
    [InlineData(1, 20f)]
    [InlineData(2, 30f)]
    [InlineData(3, 30f)]
    [InlineData(2147483647, 30f)]
    public void An_Index_Is_Clamped_Into_The_List(int index, float expected)
    {
        var value = Evaluate("xs[i]", ("xs", Numbers(10f, 20f, 30f)), ("i", ExprValue.Integer(index)));

        Assert.Equal(expected, value.AsNumber);
    }

    [Fact]
    public void An_Empty_List_Indexes_To_Nothing()
    {
        Assert.Equal(ExprValue.Number(0f), Evaluate("xs[0]", ("xs", Numbers())));
        Assert.Equal(ExprValue.Number(0f), Evaluate("xs[-3]", ("xs", Numbers())));
        Assert.Equal(ExprValue.Color(0, 0, 0, 0), Evaluate("cs[2]", ("cs", Colours())));
        Assert.Equal(ExprValue.Integer(0), Evaluate("len(xs)", ("xs", Numbers())));
    }

    [Fact]
    public void A_Literal_List_Evaluates_Element_By_Element()
    {
        Assert.Equal(Numbers(1f, 2.5f, 4f), Evaluate("[1, t, t + 1.5]", ("t", ExprValue.Number(2.5f))));
        Assert.Equal(Colours(0xffffffff, 0x1e40afff), Evaluate("[#fff, tint]", ("tint", ExprValue.Color(0x1e, 0x40, 0xaf, 0xff))));
        Assert.Equal(ExprValue.Color(0x1e, 0x40, 0xaf, 0xff), Evaluate("[#fff, tint][1]", ("tint", ExprValue.Color(0x1e, 0x40, 0xaf, 0xff))));
        Assert.Equal(ExprValue.Integer(3), Evaluate("len([1, 2, 3])"));
        Assert.Equal(ExprValue.Integer(2), Evaluate("len(cs)", ("cs", Colours(0, 1))));
        Assert.Equal(ExprValue.Number(20f), Evaluate("(on ? xs : [1])[1]", ("on", ExprValue.Boolean(true)), ("xs", Numbers(10f, 20f))));
        Assert.Equal(ExprValue.Number(1f), Evaluate("(on ? xs : [1])[1]", ("on", ExprValue.Boolean(false)), ("xs", Numbers(10f, 20f))));
    }

    [Fact]
    public void A_Default_Is_Written_As_A_List_Literal()
    {
        var evaluator = ExprEvaluator.Create(Declarations("""
            <e:param name="stops" type="number[]" default="[0, 0.5, 1]" />
            <e:param name="colors" type="color[]" default="[]" />
            <e:param name="i" type="integer" default="1" />
            """));

        Assert.Equal(Numbers(0f, 0.5f, 1f), evaluator.Evaluate("stops"));
        Assert.Equal(Colours(), evaluator.Evaluate("colors"));
        Assert.Equal(ExprValue.Number(0.5f), evaluator.Evaluate("stops[i]"));
        Assert.Equal(ExprValue.Color(0, 0, 0, 0), evaluator.Evaluate("colors[i]"));
    }

    [Fact]
    public void A_Supplied_List_Is_Bound_And_One_Of_The_Wrong_Kind_Refused()
    {
        var declarations = Declarations("""<e:param name="stops" type="number[]" default="[0, 1]" />""");
        var supplied = new Dictionary<string, ExprValue>(StringComparer.Ordinal) { ["stops"] = Numbers(0.25f, 0.75f) };

        Assert.Equal(ExprValue.Number(0.75f), ExprEvaluator.Create(declarations, supplied).Evaluate("stops[1]"));

        foreach (var wrong in new[] { Colours(0xff0000ff), ExprValue.Number(0.5f) })
        {
            var error = Assert.Throws<ExprException>(
                () => ExprEvaluator.Create(declarations, new Dictionary<string, ExprValue> { ["stops"] = wrong }));

            Assert.Equal(
                $"The value supplied for 'stops' is a {ExprFunctions.Describe(wrong.Type)}, but it is declared as a list of numbers.",
                error.Message);
        }

        var notAList = Declarations("""<e:param name="t" type="number" default="0" />""");

        Assert.Contains(
            "is a list of numbers, but it is declared as a number",
            Assert.Throws<ExprException>(() => ExprEvaluator.Create(notAList, new Dictionary<string, ExprValue> { ["t"] = Numbers(1f) })).Message);
    }

    [Fact]
    public void A_List_With_No_Default_Has_To_Be_Supplied()
    {
        var error = Assert.Throws<ExprException>(
            () => ExprEvaluator.Create(Declarations("""<e:param name="stops" type="number[]" />""")));

        Assert.StartsWith("No value was supplied for 'stops'", error.Message);
    }

    [Fact]
    public void A_Let_Can_Be_A_List()
    {
        var declarations = Declarations("""
            <e:param name="tint" type="color" default="#1e40af" />
            <e:param name="on" type="boolean" default="true" />
            <e:let name="stops">[0, 0.5, 1]</e:let>
            <e:let name="ramp">[#ffffff, tint]</e:let>
            <e:let name="none" type="color[]">[]</e:let>
            <e:let name="chosen">on ? ramp : none</e:let>
            <e:let name="last">stops[len(stops) - 1]</e:let>
            """);
        var evaluator = ExprEvaluator.Create(declarations);

        Assert.Equal(Numbers(0f, 0.5f, 1f), evaluator.Evaluate("stops"));
        Assert.Equal(Colours(0xffffffff, 0x1e40afff), evaluator.Evaluate("chosen"));
        Assert.Equal(Colours(), evaluator.Evaluate("none"));
        Assert.Equal(ExprValue.Number(1f), evaluator.Evaluate("last"));

        // The checker the editors ask sees the same table.
        Assert.Equal(ExprType.ColorList, ExprChecker.For(declarations).Check("chosen").Type);
        Assert.Equal(ExprType.ColorList, ExprChecker.For(declarations).Check("none").Type);
    }

    [Fact]
    public void An_Untyped_Let_Cannot_Be_Empty()
    {
        var declarations = Declarations("""<e:let name="none">[]</e:let>""");

        Assert.StartsWith("An empty list needs a type.", Assert.Throws<ExprException>(() => ExprEvaluator.Create(declarations)).Message);
    }

    [Fact]
    public void A_List_Has_No_Attribute_Spelling()
    {
        // No attribute an expression substitutes into takes a list, so one reaching here is a fault.
        var error = Assert.Throws<NotSupportedException>(() => SvgExpressionSubstitution.Written(Numbers(1f)));

        Assert.Equal("A list of numbers has no spelling as an attribute value.", error.Message);
    }

    // ---- declaring one ----------------------------------------------------------------------

    [Fact]
    public void A_List_Parameter_Is_Declared_By_Its_Type()
    {
        var declarations = Declarations("""
            <e:param name="positions" type="number[]" default="[0, 1]" />
            <e:param name="colors" type="color[]" />
            <e:let name="ramp" type="color[]">colors</e:let>
            """);

        Assert.Equal(ExprType.NumberList, declarations.Parameters[0].Type);
        Assert.Equal(ExprType.ColorList, declarations.Parameters[1].Type);
        Assert.Equal(ExprType.ColorList, declarations.Lets[0].DeclaredType);
        Assert.Equal(ExprType.NumberList, declarations.CreateSymbolTable()["positions"]);
    }

    [Theory]
    [InlineData("min=\"0\" max=\"1\"", SvgDeclarationPart.Min)]
    [InlineData("step=\"1\"", SvgDeclarationPart.Step)]
    public void A_List_Has_No_Range(string range, SvgDeclarationPart part)
    {
        var error = Assert.Throws<ExprException>(() => Declarations($"""<e:param name="stops" type="number[]" {range} />"""));

        Assert.Equal(
            "<e:param name=\"stops\"> is a list of numbers, so it cannot carry min, max or step. Those describe the range of a number or an integer.",
            error.Message);
        Assert.Equal(part, error.Part);
    }

    [Fact]
    public void A_Mistyped_List_Type_Is_Placed_At_The_Type()
    {
        SvgExpressionDeclarations.Parse(
            $"""<svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}"><defs><e:code><e:param name="stops" type="integer[]" /></e:code></defs></svg>""",
            out var diagnostics);

        Assert.Equal("Unknown type 'integer[]'. Expected number, integer, color, boolean, string, number[] or color[].", Assert.Single(diagnostics).Message);
    }

    // ---- folding ----------------------------------------------------------------------------

    private static TypedExpr Folded(string text, IReadOnlyDictionary<string, ExprValue>? constants = null)
        => ExprFolder.Fold(Check(text), constants ?? new Dictionary<string, ExprValue>(StringComparer.Ordinal));

    [Fact]
    public void A_Constant_List_Folds_To_A_List_Of_Literals()
    {
        var list = Assert.IsType<TypedList>(Folded("[1 + 1, pi, -0.5]"));

        Assert.Equal(ExprType.NumberList, list.Type);
        Assert.Equal(new[] { 2d, (double)MathF.PI, -0.5d }, list.Elements.Select(element => Assert.IsType<TypedNumber>(element).Value));

        var colours = Assert.IsType<TypedList>(Folded("[rgb(255, 0, 0), #00f]"));
        Assert.All(colours.Elements, element => Assert.IsType<TypedColor>(element));

        Assert.True(ExprFolder.TryValue(Folded("[1, 2]"), out var value));
        Assert.Equal(Numbers(1f, 2f), value);
    }

    [Fact]
    public void A_Known_Index_Into_A_Known_List_Folds_To_The_Element()
    {
        Assert.Equal(2d, Assert.IsType<TypedNumber>(Folded("[1, 2, 3][1]")).Value);
        Assert.Equal(3d, Assert.IsType<TypedNumber>(Folded("[1, 2, 3][7]")).Value);
        Assert.Equal(0d, Assert.IsType<TypedNumber>(Folded("(true ? [] : [1])[0]")).Value);
        Assert.Equal(3, Assert.IsType<TypedInteger>(Folded("len([4, 5, 6])")).Value);
        Assert.IsType<TypedColor>(Folded("[#fff, #000][1]"));
    }

    [Fact]
    public void A_List_That_Reads_Something_Unknown_Keeps_What_It_Reads()
    {
        var list = Assert.IsType<TypedList>(Folded("[t, 1 + 1]"));

        Assert.IsType<TypedSymbol>(list.Elements[0]);
        Assert.Equal(2d, Assert.IsType<TypedNumber>(list.Elements[1]).Value);

        var index = Assert.IsType<TypedIndex>(Folded("xs[1 + 1]"));
        Assert.Equal(2L, Assert.IsType<TypedInteger>(index.Index).Value);

        Assert.IsType<TypedIndex>(Folded("[1, 2][steps]"));
        Assert.False(ExprFolder.TryValue(Folded("[t]"), out _));
    }

    [Fact]
    public void A_List_Let_Known_To_Be_Constant_Folds_Where_It_Is_Read()
    {
        var constants = new Dictionary<string, ExprValue>(StringComparer.Ordinal) { ["xs"] = Numbers(4f, 5f) };

        Assert.Equal(5d, Assert.IsType<TypedNumber>(Folded("xs[1]", constants)).Value);
        Assert.Equal(2, Assert.IsType<TypedInteger>(Folded("len(xs)", constants)).Value);
        Assert.IsType<TypedList>(Folded("xs", constants));
    }
}
