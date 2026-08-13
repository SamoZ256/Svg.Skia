using System;
using System.Collections.Generic;
using ShimSkiaSharp;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Expressions;
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

        return new SkiaCSharpDrawing(picture!, namespaceName, className, SvgCodeDeclarations.Parse(svgMarkup));
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

    [Fact]
    public void Caching_Is_Off_Unless_Asked_For()
    {
        var drawing = Tinted("Icons", "Home");
        var code = SkiaCSharpCodeGen.Generate(drawing.Picture, "Icons", "Home", drawing.Declarations);

        Assert.DoesNotContain("s_cachedPicture", code);
        Assert.DoesNotContain("lock (", code);
        Assert.Contains("using (var skPicture = Record(h))", code);
    }

    [Fact]
    public void Caching_Remembers_Every_Argument_Under_A_Lock()
    {
        var drawing = Tinted("Icons", "Home");
        var code = SkiaCSharpCodeGen.Generate(drawing.Picture, "Icons", "Home", drawing.Declarations, cacheLastValue: true);

        Assert.Contains("private static readonly object s_cacheLock = new object();", code);
        Assert.Contains("private static SKPicture s_cachedPicture;", code);
        Assert.Contains("private static float s_arg_h;", code);

        // The draw has to stay inside the lock: releasing it earlier would let another thread
        // replace and dispose the picture midway through playback.
        Assert.Contains("lock (s_cacheLock)", code);
        Assert.Contains("if (s_cachedPicture is null", code);
        Assert.Contains("|| s_arg_h != h)", code);
        Assert.Contains("s_cachedPicture?.Dispose();", code);
        Assert.Contains("s_arg_h = h;", code);
        Assert.Contains("skCanvas.DrawPicture(s_cachedPicture);", code);

        var lockAt = code.IndexOf("lock (s_cacheLock)", StringComparison.Ordinal);
        var drawAt = code.IndexOf("skCanvas.DrawPicture(s_cachedPicture);", StringComparison.Ordinal);
        var closeAt = code.IndexOf("        }", drawAt, StringComparison.Ordinal);
        Assert.True(lockAt < drawAt && drawAt < closeAt);
    }

    [Fact]
    public void Caching_Skips_A_Document_With_No_Parameters()
    {
        // The parameterless shape already caches better: one picture built in the static
        // constructor, drawn with no comparison at all.
        var drawing = Plain("Icons", "Home");
        var code = SkiaCSharpCodeGen.Generate(drawing.Picture, "Icons", "Home", drawing.Declarations, cacheLastValue: true);

        Assert.DoesNotContain("s_cachedPicture", code);
        Assert.Contains("public static SKPicture Picture { get; }", code);
    }

    [Fact]
    public void Caching_Reaches_Every_Class_Of_A_Single_File()
    {
        var code = SkiaCSharpCodeGen.GenerateFile(
            new[] { Tinted("Icons", "Home"), Tinted("Icons", "Search") },
            cacheLastValue: true);

        Assert.Equal(2, Count(code, "lock (s_cacheLock)"));
        Assert.Equal(2, Count(code, "private static SKPicture s_cachedPicture;"));
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
}
