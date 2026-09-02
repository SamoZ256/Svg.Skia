using System;
using System.Linq;
using Svg.Expressions;
using Svg.Skia;
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

    /// <summary>The plain drawing a rewrite is given, and what it makes of it.</summary>
    private const string Plain = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect width="24" height="24" fill="#00ff00" />
        </svg>
        """;

    private static string Declared(string svgText) => svgText.Replace(
        "<rect",
        """<defs><e:code><e:param name="hue" type="number" default="120" /></e:code></defs><rect""",
        StringComparison.Ordinal)
        .Replace(
            "<svg xmlns=\"http://www.w3.org/2000/svg\"",
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:e=\"https://svg.skia/expr/1.0\"",
            StringComparison.Ordinal);

    [Fact]
    public void A_Rewritten_Document_Draws_What_The_Rewrite_Made_And_Keeps_The_Text_It_Was_Given()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName() + ".svg");

        System.IO.File.WriteAllText(path, Plain);

        try
        {
            using var document = SvgViewerDocument.Load(path, SvgSizeRequest.None, Declared);

            // What is drawn is what the rewrite made: the file itself declares nothing.
            Assert.Equal("hue", Assert.Single(document.Declarations.Parameters).Name);

            // What is shown, edited and saved is still the file.
            Assert.Equal(Plain, document.SourceText);

            // And a rebuild from the pane goes through it too, or the first keystroke would drop
            // the declarations and every expression bound to them.
            using var again = document.Reload(Plain);

            Assert.Equal("hue", Assert.Single(again.Declarations.Parameters).Name);
            Assert.Equal(Plain, again.SourceText);
            Assert.Same(document.Rewrite, again.Rewrite);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void A_Document_Without_A_Rewrite_Is_Built_From_Its_Text()
    {
        using var document = SvgViewerDocument.LoadFromSvg(Plain);

        Assert.Null(document.Rewrite);
        Assert.Equal(Plain, document.Built(Plain));
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
