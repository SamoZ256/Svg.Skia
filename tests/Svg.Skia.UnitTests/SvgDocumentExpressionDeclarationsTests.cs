// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Svg;
using Svg.Expressions;
using Svg.Model.Services;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// Reading <c>&lt;e:code&gt;</c> declarations off a parsed document, and agreeing with the reader that
/// works from source text.
/// </summary>
/// <remarks>
/// A renderer needs the parameters before it can evaluate anything, and it does not always have the
/// source text: <c>Load(XmlReader)</c> and being handed an <see cref="SvgDocument"/> never had any.
/// Two readers means two chances to disagree, so most of what is here is parity rather than
/// behaviour — the behaviour is already covered by <c>SvgExpressionDeclarationsTests</c>.
/// </remarks>
public class SvgDocumentExpressionDeclarationsTests
{
    private const string Ns = SvgExpressionDeclarations.Namespace;

    private static SvgDocument Document(string markup)
    {
        var document = SvgService.FromSvg(markup);
        Assert.NotNull(document);

        return document!;
    }

    private static void AssertSame(SvgExpressionDeclarations expected, SvgExpressionDeclarations actual)
    {
        // The range attributes are projected too: without them the tree reader could drop all three
        // and every one of these tests would still pass.
        Assert.Equal(
            expected.Parameters.Select(p => (p.Name, p.Type, p.DefaultExpression, p.MinExpression, p.MaxExpression, p.StepExpression)),
            actual.Parameters.Select(p => (p.Name, p.Type, p.DefaultExpression, p.MinExpression, p.MaxExpression, p.StepExpression)));

        Assert.Equal(
            expected.Lets.Select(l => (l.Name, l.Expression)),
            actual.Lets.Select(l => (l.Name, l.Expression)));

        Assert.Equal(expected.IsEmpty, actual.IsEmpty);
    }

    private static void AssertAgreesWithParse(string markup)
        => AssertSame(SvgExpressionDeclarations.Parse(markup), Document(markup).ExpressionDeclarations);

    [Fact]
    public void Parameters_And_Lets_Match_What_Parse_Reads()
    {
        const string markup = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24">
              <defs>
                <e:code>
                  <e:param name="t" type="number" default="0.25" />
                  <e:param name="hue" type="number" default="217" min="0" max="360" step="1" />
                  <e:param name="tint" type="color" />
                  <e:param name="bold" type="boolean" default="false" />
                  <e:let name="wave">(sin(t * tau) + 1) / 2</e:let>
                  <e:let name="tone">hsl(200 + wave * 60, 0.6, 0.4)</e:let>
                </e:code>
              </defs>
              <rect x="0" y="0" width="24" height="24" fill="{{ tone }}" />
            </svg>
            """;

        AssertAgreesWithParse(markup);

        var declarations = Document(markup).ExpressionDeclarations;

        Assert.Equal(4, declarations.Parameters.Count);
        Assert.Equal(2, declarations.Lets.Count);
        Assert.Equal("(sin(t * tau) + 1) / 2", declarations.Lets[0].Expression);
        Assert.Equal(ExprType.Color, declarations.Parameters[2].Type);
        Assert.Null(declarations.Parameters[2].DefaultExpression);

        var ranged = declarations.Parameters[1];
        Assert.Equal("0", ranged.MinExpression);
        Assert.Equal("360", ranged.MaxExpression);
        Assert.Equal("1", ranged.StepExpression);
        Assert.True(ranged.HasRange);
        Assert.False(declarations.Parameters[0].HasRange);
    }

    [Fact]
    public void A_Document_Loaded_From_An_XmlReader_Has_Its_Declarations()
    {
        // The route with no source text at all, which is why reading the tree is what makes this
        // work everywhere rather than only where a string happened to be kept.
        const string markup = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24">
              <defs><e:code><e:param name="t" type="number" default="1" /></e:code></defs>
            </svg>
            """;

        using var reader = XmlReader.Create(new StringReader(markup));
        var document = SvgDocument.Open<SvgDocument>(reader);
        Assert.NotNull(document);

        var parameter = Assert.Single(document!.ExpressionDeclarations.Parameters);

        Assert.Equal("t", parameter.Name);
        Assert.Equal(ExprType.Number, parameter.Type);
        Assert.Equal("1", parameter.DefaultExpression);
    }

    [Fact]
    public void Declarations_Survive_A_Serialization_Round_Trip()
    {
        // The editor path: a document read, written back out and read again. The lifted attributes
        // are normalised on the way out, but the code block has to come back intact.
        const string markup = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24">
              <defs>
                <e:code>
                  <e:param name="tint" type="color" />
                  <e:let name="solid">withAlpha(tint, 1)</e:let>
                </e:code>
              </defs>
              <rect x="0" y="0" width="24" height="24" fill="{{ solid }}" />
            </svg>
            """;

        var before = Document(markup).ExpressionDeclarations;
        var roundTripped = Document(Document(markup).GetXML()).ExpressionDeclarations;

        AssertSame(before, roundTripped);
    }

    [Fact]
    public void A_Param_In_Somebody_Elses_Namespace_Is_Ignored()
    {
        // The case that made the text reader work from source in the first place: outside
        // Svg.Custom a foreign element's namespace is invisible, so an unqualified <param> could be
        // anyone's. In here it is visible, and this is the test that says so.
        const string markup = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" xmlns:other="https://example.invalid/other" width="24" height="24">
              <defs>
                <e:code>
                  <e:param name="mine" type="number" default="1" />
                  <other:param name="theirs" type="number" default="2" />
                </e:code>
                <other:code>
                  <other:param name="alsoTheirs" type="number" default="3" />
                </other:code>
              </defs>
            </svg>
            """;

        AssertAgreesWithParse(markup);

        var parameter = Assert.Single(Document(markup).ExpressionDeclarations.Parameters);
        Assert.Equal("mine", parameter.Name);
    }

    [Fact]
    public void A_Code_Block_Is_Found_Outside_Defs_And_At_Any_Depth()
    {
        // Parse looks for descendants rather than a fixed location, and so does this.
        const string markup = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24">
              <g>
                <g>
                  <e:code><e:param name="deep" type="number" default="1" /></e:code>
                </g>
              </g>
            </svg>
            """;

        AssertAgreesWithParse(markup);
        Assert.Single(Document(markup).ExpressionDeclarations.Parameters);
    }

    [Fact]
    public void Several_Code_Blocks_Contribute_In_Document_Order()
    {
        const string markup = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24">
              <defs><e:code><e:param name="first" type="number" default="1" /></e:code></defs>
              <g><e:code><e:param name="second" type="number" default="2" /></e:code></g>
            </svg>
            """;

        AssertAgreesWithParse(markup);

        var names = Document(markup).ExpressionDeclarations.Parameters.Select(p => p.Name).ToList();
        Assert.Equal(new[] { "first", "second" }, names);
    }

    [Fact]
    public void A_Document_Without_A_Code_Block_Has_No_Declarations()
    {
        const string markup = """
            <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24">
              <rect x="0" y="0" width="24" height="24" fill="#1e40af" />
            </svg>
            """;

        Assert.True(Document(markup).ExpressionDeclarations.IsEmpty);
        AssertAgreesWithParse(markup);
    }

    [Fact]
    public void An_Empty_Code_Block_Has_No_Declarations()
    {
        const string markup = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24">
              <defs><e:code /></defs>
            </svg>
            """;

        Assert.True(Document(markup).ExpressionDeclarations.IsEmpty);
        AssertAgreesWithParse(markup);
    }

    [Theory]
    // Every rule the format has, asserted to fire from the tree reader with the message the text
    // reader gives. Shared validation is the reason these match; without it they would drift.
    [InlineData("""<e:param type="number" default="1" />""", "is missing a name")]
    [InlineData("""<e:param name="t" />""", "is missing a type")]
    [InlineData("""<e:param name="t" type="colour" />""", "Unknown type 'colour'")]
    [InlineData("""<e:param name="1t" type="number" />""", "is not a valid name")]
    [InlineData("""<e:param name="sin" type="number" />""", "built-in name")]
    [InlineData("""<e:param name="pi" type="number" />""", "built-in name")]
    [InlineData("""<e:param name="t" type="number" /><e:param name="t" type="number" />""", "declared more than once")]
    [InlineData("""<e:param name="t" type="number" /><e:let name="t">1</e:let>""", "declared more than once")]
    [InlineData("""<e:let name="empty"></e:let>""", "has no expression")]
    [InlineData("""<e:param name="tint" type="color" min="0" max="1" />""", "cannot carry min, max or step")]
    [InlineData("""<e:param name="on" type="boolean" step="1" />""", "cannot carry min, max or step")]
    [InlineData("""<e:param name="t" type="number" min="0" />""", "has a min but no max")]
    [InlineData("""<e:param name="t" type="number" max="1" />""", "has a max but no min")]
    public void A_Malformed_Declaration_Reports_The_Same_Way_As_Parse(string body, string expected)
    {
        var markup = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="24" height="24">
              <defs><e:code>{body}</e:code></defs>
            </svg>
            """;

        var fromText = Assert.Throws<ExprException>(() => SvgExpressionDeclarations.Parse(markup));
        var fromTree = Assert.Throws<ExprException>(() => Document(markup).ExpressionDeclarations);

        Assert.Contains(expected, fromText.Message);
        Assert.Equal(fromText.Message, fromTree.Message);
    }

    [Fact]
    public void The_Namespace_Constant_Is_Shared_With_The_Lifted_Attributes()
        // Two spellings of one namespace would let the lift and the declarations disagree about
        // which extension they belong to.
        => Assert.Equal(SvgExpressionDeclarations.Namespace, SvgExpressionAttributes.Namespace);

    [Fact]
    public void Whitespace_Around_A_Let_Expression_Is_Trimmed_As_Parse_Trims_It()
    {
        var markup = new StringBuilder()
            .AppendLine("""<svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24">""")
            .AppendLine("""  <defs><e:code><e:let name="padded">""")
            .AppendLine("""    1 + 2""")
            .AppendLine("""  </e:let></e:code></defs>""")
            .AppendLine("""</svg>""")
            .ToString();

        AssertAgreesWithParse(markup);
        Assert.Equal("1 + 2", Assert.Single(Document(markup).ExpressionDeclarations.Lets).Expression);
    }
}
