// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Linq;
using Svg.Expressions;
using Xunit;

namespace Svg.SourceEditing.UnitTests;

/// <summary>
/// Taking a parameter out of a drawing.
/// </summary>
/// <remarks>
/// Removal is the one edit that can leave a document parsing perfectly and drawing nothing, so most
/// of what is asserted here is what it refuses. The rest is that the line goes with the declaration:
/// a blank where an <c>&lt;e:param&gt;</c> used to be is not what anybody means by removing it.
/// </remarks>
public class SvgDeclarationEditorRemoveTests
{
    private const string Ns = SvgExpressionDeclarations.Namespace;

    private const string Two = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="EXPR-NS" width="10" height="10">
          <defs>
            <e:code>
              <e:param name="hue" type="number" default="217" />
              <e:param name="spare" type="number" default="1" />
            </e:code>
          </defs>
          <rect width="10" height="10" fill="{{ hsl(hue, 1, 0.5) }}" />
        </svg>
        """;

    private static string Source() => Two.Replace("EXPR-NS", Ns);

    private static string Apply(string svgText, SvgSourceEditResult result)
    {
        Assert.True(result.Succeeded, result.Refusal);

        return SvgTextEdit.ApplyAll(svgText, result.Edits);
    }

    private static string[] Declared(string svgText)
        => SvgExpressionDeclarations.Parse(svgText, out _).Parameters.Select(p => p.Name).ToArray();

    [Fact]
    public void A_Parameter_Nothing_Names_Goes_And_Takes_Its_Line_With_It()
    {
        var source = Source();

        var edited = Apply(source, SvgDeclarationEditor.Remove(source, "spare"));

        Assert.Equal(new[] { "hue" }, Declared(edited));

        // The line, not the tags: leaving the blank it sat on would be a different kind of edit.
        Assert.DoesNotContain("spare", edited);
        Assert.Equal(
            source.Replace("\n      <e:param name=\"spare\" type=\"number\" default=\"1\" />", string.Empty),
            edited);
    }

    [Fact]
    public void A_Parameter_A_Placeholder_Still_Names_Is_Refused()
    {
        var source = Source();

        var result = SvgDeclarationEditor.Remove(source, "hue");

        Assert.False(result.Succeeded);
        Assert.Contains("'hue'", result.Refusal);
        Assert.Contains("once", result.Refusal);
    }

    [Fact]
    public void A_Parameter_A_Let_Still_Names_Is_Refused()
    {
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="217" />
                  <e:let name="half">hue / 2</e:let>
                </e:code>
              </defs>
            </svg>
            """;

        // The other half of what a use is. A let body names it as much as a placeholder does, and
        // nothing about the document's shape says so.
        Assert.False(SvgDeclarationEditor.Remove(source, "hue").Succeeded);
    }

    [Fact]
    public void The_Refusal_Says_How_Many_Uses_There_Are()
    {
        var source = Source().Replace(
            """<rect width="10" height="10" fill="{{ hsl(hue, 1, 0.5) }}" />""",
            """<rect width="10" height="10" fill="{{ hsl(hue, 1, 0.5) }}" opacity="{{ hue / 360 }}" />""");

        var result = SvgDeclarationEditor.Remove(source, "hue");

        Assert.False(result.Succeeded);
        Assert.Contains("2 times", result.Refusal);
    }

    [Fact]
    public void A_Name_In_Its_Own_Bounds_Is_Not_A_Use_Of_It()
    {
        // min, max, default and step are expressions, but the language puts nothing the document
        // declares in scope there -- so `hue` inside them is a different `hue` and not a use.
        var source = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs>
                <e:code>
                  <e:param name="hue" type="number" default="tau" min="0" max="360" />
                </e:code>
              </defs>
            </svg>
            """;

        Assert.Empty(Declared(Apply(source, SvgDeclarationEditor.Remove(source, "hue"))));
    }

    [Fact]
    public void A_Parameter_The_Drawing_Does_Not_Have_Is_Refused()
    {
        var source = Source();

        Assert.False(SvgDeclarationEditor.Remove(source, "missing").Succeeded);
    }

    [Fact]
    public void Everything_The_Removal_Did_Not_Touch_Is_Left_Alone()
    {
        var source = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!-- the drawing this file is for -->
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="{Ns}" width="10" height="10">
              <defs>
                <e:code>
                  <!-- what the tint was going to be -->
                  <e:param name="spare" type="color" default="#ff0000" />
                </e:code>
              </defs>
              <rect width="10" height="10" fill="#00ff00" />
            </svg>
            """;

        var edited = Apply(source, SvgDeclarationEditor.Remove(source, "spare"));

        Assert.Contains("<!-- the drawing this file is for -->", edited);
        Assert.Contains("<!-- what the tint was going to be -->", edited);
        Assert.Contains("""<rect width="10" height="10" fill="#00ff00" />""", edited);

        // The block stays, empty. Taking it away would be a second decision -- about the <defs> that
        // may hold other things, and about an xmlns nothing declares any more -- and re-adding a
        // parameter writes into the one that is already there.
        Assert.Contains("<e:code>", edited);
    }
}
