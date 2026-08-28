using System;
using System.Linq;
using Svg.Expressions;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

public class SvgViewerDocumentTests
{
    private const string Parametric = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs><e:code><e:param name="hue" type="number" default="217" min="0" max="360" step="1" /></e:code></defs>
          <rect x="0" y="0" width="24" height="24" fill="{{ hsl(hue, 74%, 55%) }}" />
        </svg>
        """;

    [Fact]
    public void A_Document_Reports_What_It_Declares()
    {
        using var document = SvgViewerDocument.LoadFromSvg(Parametric);

        Assert.Null(document.DeclarationError);
        Assert.Equal("hue", Assert.Single(document.Declarations.Parameters).Name);
        Assert.NotNull(document.Svg.Picture);
    }

    [Fact]
    public void A_Malformed_Declaration_Block_Is_Recorded_And_Still_Draws()
    {
        // Loading deliberately does not read declarations, so a bad block must not cost the drawing.
        // This is the property that keeps a viewer showing something rather than an error page.
        using var document = SvgViewerDocument.LoadFromSvg("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="tint" type="color" min="0" max="1" /></e:code></defs>
              <rect x="0" y="0" width="24" height="24" fill="#ff0000" />
            </svg>
            """);

        Assert.NotNull(document.DeclarationError);
        Assert.Contains("cannot carry min, max or step", document.DeclarationError);
        Assert.Empty(document.Declarations.Parameters);
        Assert.NotNull(document.Svg.Picture);
    }

    [Fact]
    public void A_Document_That_Is_Not_Svg_Fails_To_Load()
    {
        Assert.ThrowsAny<Exception>(() => SvgViewerDocument.LoadFromSvg("not markup at all"));
    }

    [Fact]
    public void Rows_Built_From_A_Document_Bind_Through_To_The_Drawing()
    {
        using var document = SvgViewerDocument.LoadFromSvg(Parametric);

        var rows = SvgViewerParameterFactory.Create(document.Declarations.Parameters);
        var values = rows.ToDictionary(r => r.Name, r => r.ToExprValue(), StringComparer.Ordinal);

        Assert.NotNull(document.Svg.SetExpressionValues(values));
        Assert.Equal(217f, document.Svg.ExpressionValues!["hue"].AsNumber);
    }
}
