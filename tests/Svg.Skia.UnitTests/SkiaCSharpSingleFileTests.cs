using System;
using System.Collections.Generic;
using ShimSkiaSharp;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Expressions;
using Svg.Expressions;
using Svg.Model.Services;
using Xunit;

namespace Svg.Skia.UnitTests;

public class SkiaCSharpSingleFileTests
{
    private static SkiaCSharpDrawing Drawing(string namespaceName, string className, string svgMarkup)
    {
        var document = SvgService.FromSvg(svgMarkup);
        Assert.NotNull(document);
        var assetLoader = new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings()));
        var picture = SvgSceneRuntime.CreateModel(document!, assetLoader);
        Assert.NotNull(picture);

        return new SkiaCSharpDrawing(picture!, namespaceName, className, SvgExpressionDeclarations.Parse(svgMarkup));
    }

    /// <summary>A drawing whose fill needs the hsl helper, so helper placement is observable.</summary>
    private static SkiaCSharpDrawing Tinted(string namespaceName, string className) => Drawing(
        namespaceName,
        className,
        """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="100" height="100">
          <defs><e:code><e:param name="h" type="number" default="0" /></e:code></defs>
          <rect x="0" y="0" width="10" height="10" fill="{{ hsl(h, 50%, 50%) }}" />
        </svg>
        """);

    private static SkiaCSharpDrawing Plain(string namespaceName, string className) => Drawing(
        namespaceName,
        className,
        """
        <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
          <rect x="0" y="0" width="10" height="10" fill="#808080" />
        </svg>
        """);

    private static int Count(string text, string value)
    {
        var total = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            total++;
        }

        return total;
    }

    [Fact]
    public void Drawings_Share_One_Copy_Of_Each_Helper()
    {
        var code = SkiaCSharpCodeGen.GenerateFile(new[] { Tinted("Icons", "Home"), Tinted("Icons", "Search") });

        Assert.Equal(1, Count(code, "SKColor SvgHsl(float h, float s, float l)"));
        Assert.Contains("public static class Home", code);
        Assert.Contains("public static class Search", code);
        Assert.Equal(1, Count(code, "namespace Icons"));
    }

    [Fact]
    public void Helpers_Sit_Outside_Every_Namespace_And_Are_Imported_Statically()
    {
        var code = SkiaCSharpCodeGen.GenerateFile(new[] { Tinted("Icons", "Home") });

        // Call sites stay unqualified, exactly as when the helper is a private member.
        Assert.Contains("using static SvgExpressionHelpers;", code);
        Assert.Contains("file static class SvgExpressionHelpers", code);
        Assert.True(
            code.IndexOf("file static class", StringComparison.Ordinal) < code.IndexOf("namespace Icons", StringComparison.Ordinal),
            "The helper class belongs at file scope, ahead of the namespaces that use it.");
    }

    [Fact]
    public void Namespaces_Are_Grouped_And_Keep_First_Appearance_Order()
    {
        var code = SkiaCSharpCodeGen.GenerateFile(new[]
        {
            Tinted("Icons", "Home"),
            Tinted("Glyphs", "Chevron"),
            Tinted("Icons", "Search")
        });

        // Two blocks, not three: the second Icons drawing joins the first.
        Assert.Equal(1, Count(code, "namespace Icons"));
        Assert.Equal(1, Count(code, "namespace Glyphs"));
        Assert.True(code.IndexOf("namespace Icons", StringComparison.Ordinal) < code.IndexOf("namespace Glyphs", StringComparison.Ordinal));
        Assert.True(code.IndexOf("class Home", StringComparison.Ordinal) < code.IndexOf("class Search", StringComparison.Ordinal));
    }

    [Fact]
    public void Internal_Scope_Uses_The_Given_Class_Name()
    {
        // An internal helper class is visible across the assembly, so two generated files would
        // collide unless the name differs; svgc derives it from the output file.
        var code = SkiaCSharpCodeGen.GenerateFile(
            new[] { Tinted("Icons", "Home") },
            SvgHelperScope.Internal,
            "Icons_SvgExpressionHelpers");

        Assert.Contains("internal static class Icons_SvgExpressionHelpers", code);
        Assert.Contains("using static Icons_SvgExpressionHelpers;", code);
        Assert.DoesNotContain("file static class", code);
    }

    [Fact]
    public void PerClass_Scope_Keeps_Helpers_Private_To_Each_Class()
    {
        var code = SkiaCSharpCodeGen.GenerateFile(
            new[] { Tinted("Icons", "Home"), Tinted("Icons", "Search") },
            SvgHelperScope.PerClass);

        Assert.Equal(2, Count(code, "private static SKColor SvgHsl"));
        Assert.DoesNotContain("using static", code);
        Assert.DoesNotContain("static class SvgExpressionHelpers", code);
    }

    [Fact]
    public void No_Helper_Class_When_Nothing_Needs_One()
    {
        var code = SkiaCSharpCodeGen.GenerateFile(new[] { Plain("Icons", "Home"), Plain("Icons", "Search") });

        Assert.DoesNotContain("using static", code);
        Assert.DoesNotContain("SvgExpressionHelpers", code);
    }

    [Fact]
    public void Two_Classes_Of_One_Name_In_One_Namespace_Are_Rejected()
    {
        // Emitting both would be CS0101, reporting a fault in generated code rather than in the
        // batch that asked for it.
        var error = Assert.Throws<ExprException>(() => SkiaCSharpCodeGen.GenerateFile(new[]
        {
            Tinted("Icons", "Home"),
            Tinted("Icons", "Home")
        }));

        Assert.Contains("'Icons.Home' is generated twice", error.Message);
    }

    [Fact]
    public void One_Name_In_Two_Namespaces_Is_Fine()
    {
        var code = SkiaCSharpCodeGen.GenerateFile(new[] { Tinted("A", "Home"), Tinted("B", "Home") });

        Assert.Equal(2, Count(code, "public static class Home"));
    }

    [Theory]
    [InlineData(SvgBody.Record)]
    [InlineData(SvgBody.Direct)]
    public void A_Drawing_Without_Parameters_Keeps_No_Picture(SvgBody body)
    {
        var drawing = Plain("Icons", "Home");
        var code = SkiaCSharpCodeGen.GenerateFile(new[] { drawing }, style: new SvgCodeStyle { Body = body });

        Assert.Contains("public static SKPicture Record()", code);
        Assert.Contains("public static void Draw(SKCanvas skCanvas)", code);
        Assert.DoesNotContain("static SKPicture Picture", code);
        Assert.DoesNotContain("private static SKPicture", code);
        Assert.DoesNotContain("Picture = Record();", code);
    }

    [Fact]
    public void SkiaSharp4_Builds_Paths_Through_A_Builder()
    {
        var drawing = Tinted("Icons", "Home");
        var code = SkiaCSharpCodeGen.Generate(drawing.Picture, "Icons", "Home", drawing.Declarations);

        Assert.Contains("var skPathBuilder0 = new SKPathBuilder();", code);
        Assert.Contains("var skPath0 = skPathBuilder0.Detach();", code);
        Assert.Contains("skPathBuilder0?.Dispose();", code);
        Assert.DoesNotContain("new SKPath()", code);
    }

    [Fact]
    public void SkiaSharp3_Builds_Paths_On_The_Path_Itself()
    {
        // SKPathBuilder does not exist in SkiaSharp 3, and SKPath's mutating methods are not
        // obsolete there, so the older shape is the correct one rather than a fallback.
        var drawing = Tinted("Icons", "Home");
        var code = SkiaCSharpCodeGen.Generate(
            drawing.Picture, "Icons", "Home", drawing.Declarations, skiaSharp: SkiaSharpTarget.V3);

        Assert.Contains("var skPath0 = new SKPath();", code);
        Assert.DoesNotContain("SKPathBuilder", code);
        Assert.DoesNotContain(".Detach()", code);
    }

    [Fact]
    public void SkiaSharp_Version_Reaches_Every_Class_Of_A_Single_File()
    {
        var code = SkiaCSharpCodeGen.GenerateFile(
            new[] { Tinted("Icons", "Home"), Tinted("Icons", "Search") },
            skiaSharp: SkiaSharpTarget.V3);

        Assert.DoesNotContain("SKPathBuilder", code);
        Assert.Equal(2, Count(code, "new SKPath()"));
    }

    [Fact]
    public void Single_Drawing_Output_Is_Unchanged()
    {
        var drawing = Tinted("Icons", "Home");
        var code = SkiaCSharpCodeGen.Generate(drawing.Picture, "Icons", "Home", drawing.Declarations);

        // Usings outside the namespace, helper private to the class, no shared machinery.
        Assert.StartsWith("// <auto-generated />\n\nusing System;\nusing SkiaSharp;\n\nnamespace Icons", code.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("        private static SKColor SvgHsl", code);
        Assert.DoesNotContain("using static", code);
    }

    [Fact]
    public void Drawn_Directly_What_No_Argument_Changes_Is_Built_Once()
    {
        var code = SkiaCSharpCodeGen.GenerateFile(
            new[]
            {
                Drawing("Icons", "Tank", """
                    <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="30" height="30">
                      <defs><e:code><e:param name="accent" type="color" default="#f00" /></e:code></defs>
                      <circle cx="15" cy="15" r="13" fill="none" stroke="#1e40af" stroke-width="2" />
                      <rect x="6" y="6" width="18" height="18" fill="{{ accent }}" />
                    </svg>
                    """)
            },
            style: new SvgCodeStyle { Body = SvgBody.Direct });

        Assert.Contains("private static readonly SKPath s_skPath0;", code);
        Assert.Contains("private static readonly SKPaint s_skPaint0;", code);
        Assert.Contains("var skPaint0 = s_skPaint0;", code);
        Assert.DoesNotContain("skPaint0?.Dispose();", code);
        Assert.DoesNotContain("skPath0?.Dispose();", code);

        // The fill follows the argument, so it is built for each draw and let go after it.
        Assert.DoesNotContain("s_skPaint1", code);
        Assert.Contains("skPaint1?.Dispose();", code);
        Assert.Contains("public static void Draw(SKCanvas skCanvas, SKColor? accent = null)", code);
        Assert.DoesNotContain("DrawPicture", code);
    }

    [Fact]
    public void A_Typeface_Template_Names_What_The_Document_Asked_For()
    {
        // Not a font any test machine has, so what was found for it is something else entirely.
        var code = SkiaCSharpCodeGen.GenerateFile(
            new[]
            {
                Drawing("Icons", "Label", """
                    <svg xmlns="http://www.w3.org/2000/svg" width="30" height="30">
                      <text x="2" y="20" font-family="'SF UI Display', sans-serif" font-weight="500" font-size="10">7</text>
                    </svg>
                    """)
            },
            style: new SvgCodeStyle { Typeface = "PaintCode.TypefaceManager.GetTypeface(\"{family-dashed}-{weight}.otf\")" });

        Assert.Contains("= PaintCode.TypefaceManager.GetTypeface(\"SF-UI-Display-Medium.otf\");", code);
        Assert.DoesNotContain("SKFontManager", code);
    }
}
