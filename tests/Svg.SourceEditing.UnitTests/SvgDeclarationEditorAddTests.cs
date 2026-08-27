// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Linq;
using Svg.Expressions;
using Svg.SourceEditing;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// Declaring a parameter in a document that may or may not be ready for one.
/// </summary>
/// <remarks>
/// <para>
/// Two things are asserted throughout, and the second is the point of editing this way: that the
/// document reads the parameter back, and that everything else in it is unchanged. The second is
/// what a regenerated document cannot do, so it is worth asserting on the parts nothing else would
/// notice — the comments, the attribute order, the odd indentation somebody chose.
/// </para>
/// <para>
/// Nothing here asserts on wording. A refusal about a name comes from the language's own rules, and
/// pinning the sentence here would mean two places to change when it improves.
/// </para>
/// </remarks>
public class SvgDeclarationEditorAddTests
{
    private const string Ns = SvgExpressionDeclarations.Namespace;

    private static SvgExpressionParameter Number(string name = "radius", string? @default = "40")
        => new(name, ExprType.Number, @default);

    /// <summary>The document after the edit, which must have been allowed.</summary>
    private static string Add(string svgText, SvgExpressionParameter parameter)
    {
        var result = SvgDeclarationEditor.Add(svgText, parameter);

        Assert.True(result.Succeeded, result.Refusal);

        return SvgTextEdit.ApplyAll(svgText, result.Edits);
    }

    private static SvgExpressionParameter Declared(string svgText, string name)
        => Assert.Single(SvgExpressionDeclarations.Parse(svgText, out _).Parameters, p => p.Name == name);

    // ---- a block that is already there ----

    [Fact]
    public void A_Parameter_Joins_The_Block_A_Drawing_Already_Has()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="217" />
                </e:code>
              </defs>
            </svg>
            """;

        var edited = Add(source, Number());

        Assert.Equal(ExprType.Number, Declared(edited, "radius").Type);
        Assert.Equal("40", Declared(edited, "radius").DefaultExpression);

        // Beside the declaration above it, not at some indentation of this code's choosing.
        Assert.Contains("""
                  <e:param name="hue" type="number" default="217" />
                  <e:param name="radius" type="number" default="40" />
            """.TrimEnd(), edited);
    }

    [Fact]
    public void Everything_The_Edit_Did_Not_Touch_Is_Left_Alone()
    {
        // Not interpolated: a drawing holding {{ … }} and a raw string holding {…} cannot both have
        // the braces, so the namespace goes in afterwards.
        var source = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!-- the drawing this file is for -->
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="217" />
                </e:code>
              </defs>
              <!-- why this circle is here -->
              <circle cx="5" cy="5" r="4" fill="{{ hsl(hue, 90%, 60%) }}" />
            </svg>
            """.Replace("EXPR-NS", Ns);

        var edited = Add(source, Number());

        Assert.Contains("<!-- the drawing this file is for -->", edited);
        Assert.Contains("<!-- why this circle is here -->", edited);
        Assert.Contains("fill=\"{{ hsl(hue, 90%, 60%) }}\"", edited);
        Assert.Contains("""<?xml version="1.0" encoding="UTF-8"?>""", edited);

        // The only difference is the line that was added.
        var before = source.Split('\n');
        var after = edited.Split('\n');

        Assert.Equal(before.Length + 1, after.Length);
        Assert.Equal(
            before.Select(line => line.TrimEnd()),
            after.Select(line => line.TrimEnd()).Where(line => !line.Contains("radius")));
    }

    [Fact]
    public void A_Block_Written_Outside_Defs_Is_Still_The_One_Used()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <e:code>
                <e:param name="hue" type="number" default="217" />
              </e:code>
            </svg>
            """;

        var edited = Add(source, Number());

        Assert.Equal(2, SvgExpressionDeclarations.Parse(edited, out _).Parameters.Count);
        Assert.DoesNotContain("<defs>", edited);
    }

    [Fact]
    public void An_Empty_Block_Takes_The_First_Declaration()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs>
                <e:code></e:code>
              </defs>
            </svg>
            """;

        Assert.Equal("40", Declared(Add(source, Number()), "radius").DefaultExpression);
    }

    [Fact]
    public void A_Block_That_Closes_Itself_Becomes_One_That_Can_Hold_A_Declaration()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs>
                <e:code />
              </defs>
            </svg>
            """;

        var edited = Add(source, Number());

        Assert.Equal("40", Declared(edited, "radius").DefaultExpression);
        Assert.DoesNotContain("<e:code />", edited);
    }

    [Fact]
    public void A_Parameter_Joins_The_Parameters_Rather_Than_The_End_Of_The_Block()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="217" />

                  <e:let name="primary">hsl(hue, 91%, 60%)</e:let>
                  <e:let name="deep">hsl(hue + 5, 71%, 40%)</e:let>
                </e:code>
              </defs>
            </svg>
            """;

        var edited = Add(source, Number());

        // The two groups are how a block is written and how it is read back. Landing after the lets
        // would put a declaration below things that use it, which parses but does not read.
        Assert.True(
            edited.IndexOf("name=\"radius\"", System.StringComparison.Ordinal)
                < edited.IndexOf("<e:let", System.StringComparison.Ordinal),
            "A parameter should be written with the parameters, above the lets.");

        Assert.Contains("""
                  <e:param name="hue" type="number" default="217" />
                  <e:param name="radius" type="number" default="40" />
            """.TrimEnd(), edited);
    }

    [Fact]
    public void A_Block_Of_Only_Lets_Takes_The_Parameter_Above_Them()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs>
                <e:code>
                  <e:let name="primary">hsl(200, 91%, 60%)</e:let>
                </e:code>
              </defs>
            </svg>
            """;

        var edited = Add(source, Number());

        Assert.True(
            edited.IndexOf("<e:param", System.StringComparison.Ordinal)
                < edited.IndexOf("<e:let", System.StringComparison.Ordinal),
            "With no parameters to join, one goes above the lets rather than below them.");

        Assert.Equal("40", Declared(edited, "radius").DefaultExpression);
    }

    // ---- a drawing with nothing declared yet ----

    [Fact]
    public void A_Drawing_With_No_Block_Gets_One_First_In_Its_Defs()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs>
                <linearGradient id="plate" />
              </defs>
              <circle cx="5" cy="5" r="4" />
            </svg>
            """;

        var edited = Add(source, Number());

        Assert.Equal("40", Declared(edited, "radius").DefaultExpression);

        // Where SvgRecipeRewriter puts it: the preamble, ahead of the gradients.
        Assert.True(
            edited.IndexOf("<e:code>", System.StringComparison.Ordinal)
                < edited.IndexOf("<linearGradient", System.StringComparison.Ordinal),
            "The block should come before what <defs> already held.");
    }

    [Fact]
    public void A_Drawing_With_No_Defs_Gets_Both()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <circle cx="5" cy="5" r="4" />
            </svg>
            """;

        var edited = Add(source, Number());

        Assert.Equal("40", Declared(edited, "radius").DefaultExpression);
        Assert.Contains("<defs>", edited);
        Assert.Contains("<circle", edited);
    }

    [Fact]
    public void A_Drawing_That_Never_Declared_The_Namespace_Has_It_Declared()
    {
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" width="10" height="10">
              <circle cx="5" cy="5" r="4" />
            </svg>
            """;

        var edited = Add(source, Number());

        Assert.Contains($"xmlns:e=\"{Ns}\"", edited);
        Assert.Equal("40", Declared(edited, "radius").DefaultExpression);
    }

    [Fact]
    public void A_Prefix_The_Drawing_Already_Uses_Is_The_One_Written()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:x="{Ns}" width="10" height="10">
              <defs>
                <x:code>
                  <x:param name="hue" type="number" default="217" />
                </x:code>
              </defs>
            </svg>
            """;

        var edited = Add(source, Number());

        Assert.Contains("<x:param name=\"radius\"", edited);
        Assert.DoesNotContain("<e:param", edited);
    }

    [Fact]
    public void A_Prefix_Spoken_For_By_Something_Else_Is_Not_Taken()
    {
        var source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="http://example.com/other" width="10" height="10">
              <circle cx="5" cy="5" r="4" />
            </svg>
            """;

        var edited = Add(source, Number());

        // The other namespace keeps its prefix, and the extension takes the next one going.
        Assert.Contains("xmlns:e=\"http://example.com/other\"", edited);
        Assert.Contains("<e2:param", edited);
        Assert.Equal("40", Declared(edited, "radius").DefaultExpression);
    }

    // ---- the shape of what was written ----

    [Fact]
    public void The_Line_Endings_Are_The_Ones_The_Document_Uses()
    {
        var source = $"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:e=\"{Ns}\" width=\"10\" height=\"10\">\r\n"
            + "  <defs>\r\n    <e:code>\r\n      <e:param name=\"hue\" type=\"number\" default=\"217\" />\r\n"
            + "    </e:code>\r\n  </defs>\r\n</svg>";

        var edited = Add(source, Number());

        Assert.DoesNotContain("\n", edited.Replace("\r\n", string.Empty));
        Assert.Equal("40", Declared(edited, "radius").DefaultExpression);
    }

    [Fact]
    public void The_Indentation_Is_The_One_The_Document_Uses()
    {
        var source = $"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:e=\"{Ns}\" width=\"10\" height=\"10\">\n"
            + "\t<defs>\n\t\t<e:code>\n\t\t</e:code>\n\t</defs>\n</svg>";

        var edited = Add(source, Number());

        Assert.Contains("\t\t\t<e:param name=\"radius\"", edited);
        Assert.DoesNotContain("  <e:param", edited);
    }

    [Fact]
    public void A_Range_And_A_Step_Are_Written_When_Declared()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code></e:code></defs>
            </svg>
            """;

        var edited = Add(source, new SvgExpressionParameter("hue", ExprType.Number, "217", "0", "360", "1"));

        var declared = Declared(edited, "hue");

        Assert.Equal("0", declared.MinExpression);
        Assert.Equal("360", declared.MaxExpression);
        Assert.Equal("1", declared.StepExpression);
    }

    [Fact]
    public void A_Colour_Is_Written_As_The_Type_The_Reader_Takes_Back()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code></e:code></defs>
            </svg>
            """;

        // Not "colour": that spelling is for a sentence somebody reads, and the parser refuses it.
        var edited = Add(source, new SvgExpressionParameter("tint", ExprType.Color, "#3fb5b5"));

        Assert.Contains("type=\"color\"", edited);
        Assert.Equal(ExprType.Color, Declared(edited, "tint").Type);
    }

    // ---- what it will not do ----

    [Fact]
    public void A_Name_Already_Declared_Is_Refused()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code><e:param name="hue" type="number" default="217" /></e:code></defs>
            </svg>
            """;

        Assert.False(SvgDeclarationEditor.Add(source, Number("hue")).Succeeded);
    }

    [Fact]
    public void A_Name_A_Let_Has_Taken_Is_Refused()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code><e:let name="primary">1</e:let></e:code></defs>
            </svg>
            """;

        Assert.False(SvgDeclarationEditor.Add(source, Number("primary")).Succeeded);
    }

    [Theory]
    [InlineData("tau")]
    [InlineData("1st")]
    [InlineData("has space")]
    public void A_Name_The_Language_Will_Not_Have_Is_Refused(string name)
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code></e:code></defs>
            </svg>
            """;

        Assert.False(SvgDeclarationEditor.Add(source, Number(name)).Succeeded);
    }

    [Fact]
    public void A_Range_With_One_End_Is_Refused()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code></e:code></defs>
            </svg>
            """;

        var result = SvgDeclarationEditor.Add(
            source,
            new SvgExpressionParameter("hue", ExprType.Number, "217", "0", null, null));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void A_Range_On_Something_That_Has_No_Range_Is_Refused()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code></e:code></defs>
            </svg>
            """;

        var result = SvgDeclarationEditor.Add(
            source,
            new SvgExpressionParameter("on", ExprType.Boolean, "true", "0", "1", null));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void A_Document_That_Is_Not_Well_Formed_Yet_Is_Refused_Rather_Than_Guessed_At()
    {
        // What a drawing looks like halfway through being typed, which is when a panel beside the
        // text is most likely to be asked to change it.
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code></e:code>
            """;

        var result = SvgDeclarationEditor.Add(source, Number());

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Refusal);
        Assert.Empty(result.Edits);
    }

    [Fact]
    public void A_Block_That_Is_Already_Wrong_Is_Fixed_By_Hand_First()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs><e:code><e:param name="hue" /></e:code></defs>
            </svg>
            """;

        Assert.False(SvgDeclarationEditor.Add(source, Number()).Succeeded);
    }
}
