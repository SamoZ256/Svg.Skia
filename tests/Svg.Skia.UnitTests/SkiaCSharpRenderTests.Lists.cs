using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SkiaSharp;
using Svg.CodeGen.Skia;
using Svg.Expressions;
using Svg.Skia.UnitTests.Common;
using Xunit;

namespace Svg.Skia.UnitTests;

public partial class SkiaCSharpRenderTests
{
    // What a host passes a gradient's stops as: a List<SKColor> and a List<float>, two or three long.
    private const string Listed = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs>
            <e:code>
              <e:param name="colors" type="color[]" />
              <e:param name="widths" type="number[]" default="[1, 2.5, 4]" />
              <e:param name="i" type="integer" default="0" />
            </e:code>
          </defs>
          <path d="M 4 4 L 20 4 L 12 20 Z" fill="{{ colors[i] }}" stroke="{{ [#111827, colors[i + 1]][i] }}" stroke-width="{{ widths[i] }}" />
          <circle cx="12" cy="10" r="3" fill="{{ colors[len(colors) - 1] }}" stroke="#facc15" stroke-width="{{ widths[len(widths) - 1] / 2 }}" />
        </svg>
        """;

    private static List<SKColor> Stops(params uint[] colors) => new(Array.ConvertAll(colors, color => new SKColor(color)));

    [Theory]
    [InlineData(SkiaSharpTarget.V4, 0)]
    [InlineData(SkiaSharpTarget.V4, 1)]
    [InlineData(SkiaSharpTarget.V4, 2)]
    [InlineData(SkiaSharpTarget.V4, -1)]
    [InlineData(SkiaSharpTarget.V3, 0)]
    [InlineData(SkiaSharpTarget.V3, 2)]
    public void A_Drawing_Painted_From_Lists_Draws_As_The_Runtime_Binds_It(SkiaSharpTarget skiaSharp, int i)
    {
        AssertBindsTheSame(
            $"Listed_{skiaSharp}_{i}".Replace('-', 'm'),
            Listed,
            new object?[] { Stops(0xffdc2626, 0xff1e40af, 0x8016a34a), new List<float> { 0.5f, 3f, 1.5f }, i },
            skiaSharp);

        // Two stops, and the default widths a null leaves.
        AssertBindsTheSame(
            $"ListedTwo_{skiaSharp}_{i}".Replace('-', 'm'),
            Listed,
            new object?[] { Stops(0xff0ea5e9, 0xff7c3aed), null, i },
            skiaSharp);
    }

    [Fact]
    public void A_Null_List_Is_The_Default_Or_Else_Empty()
    {
        var type = Compile(Code(Model(Listed), "ListedNulls", SvgBody.Record, SvgExpressionDeclarations.Parse(Listed)), "ListedNulls")
            .GetType($"{Namespace}.ListedNulls")!;
        var record = type.GetMethod("Record")!;

        using var empty = (SKPicture)record.Invoke(null, new object?[] { Array.Empty<SKColor>(), new[] { 1f, 2.5f, 4f }, 1 })!;
        using var none = (SKPicture)record.Invoke(null, new object?[] { null, null, 1 })!;

        AssertSamePicture("ListedNulls", empty, none);
    }

    [Fact]
    public void A_List_Parameter_Is_Required_And_Optional_As_Its_Declaration_Says()
    {
        var code = Code(Model(Listed), "ListedSignature", SvgBody.Direct, SvgExpressionDeclarations.Parse(Listed));

        Assert.Contains("using System.Collections.Generic;", code);
        Assert.Contains("public static void Draw(SKCanvas skCanvas, IReadOnlyList<SKColor> colors, IReadOnlyList<float> widths = null, int i = 0)", code);
        Assert.Contains("IReadOnlyList<SKColor> colors__default = colors ?? Array.Empty<SKColor>();", code);
        Assert.Contains("private static readonly IReadOnlyList<float> s_skList1 = new float[] { 1f, 2.5f, 4f };", code);
        Assert.Contains("IReadOnlyList<float> widths__default = widths ?? s_skList1;", code);
        Assert.Contains("SvgAt(colors__default, i)", code);
        Assert.Contains("(colors__default.Count - 1)", code);
        Assert.Contains("private static SKColor[] s_skList0;", code);
        Assert.Contains("SvgList(ref s_skList0, stackalloc SKColor[] { new SKColor(17, 24, 39, 255), SvgAt(colors__default, (i + 1)) })", code);

        // The shared paint emits its settings twice and keeps one; both copies write the one field.
        Assert.DoesNotContain("s_skList2", code);
    }

    /// <summary>A direct drawing's Draw, compiled, after one draw has warmed it.</summary>
    private static T Warmed<T>(string name, string svg, Action<T> draw)
        where T : Delegate
    {
        var code = SkiaCSharpCodeGen.GenerateFile(
            new[] { new SkiaCSharpDrawing(Model(svg), Namespace, name, SvgExpressionDeclarations.Parse(svg)) },
            SvgHelperScope.PerClass,
            style: new SvgCodeStyle { Body = SvgBody.Direct });
        var method = Compile(code, name).GetType($"{Namespace}.{name}")!.GetMethod("Draw", BindingFlags.Public | BindingFlags.Static)!;
        var drawn = method.CreateDelegate<T>();

        draw(drawn);

        return drawn;
    }

    [Fact]
    public void A_Direct_Draw_Reading_List_Arguments_Allocates_Nothing()
    {
        using var bitmap = new SKBitmap(24, 24);
        using var canvas = new SKCanvas(bitmap);
        var colors = Stops(0xffdc2626, 0xff1e40af, 0xff16a34a);
        var widths = new List<float> { 0.5f, 3f, 1.5f };

        var draw = Warmed<Action<SKCanvas, IReadOnlyList<SKColor>, IReadOnlyList<float>, int>>(
            "UnallocatedLists", Listed, warm => warm(canvas, colors, widths, 1));

        var before = GC.GetAllocatedBytesForCurrentThread();
        draw(canvas, colors, widths, 1);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void A_Direct_Draw_Of_A_List_Literal_Reading_Arguments_Allocates_Nothing()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="tint" type="color" default="#1e40af" /><e:param name="w" type="number" default="1" /><e:param name="i" type="integer" default="0" /></e:code></defs>
              <path d="M 2 2 L 22 2 L 12 21 Z" fill="{{ [tint, #fff, mix(tint, #000, 0.5)][i] }}" stroke="#111827" stroke-width="{{ [w, w * 2][i] }}" />
            </svg>
            """;

        using var bitmap = new SKBitmap(24, 24);
        using var canvas = new SKCanvas(bitmap);
        var draw = Warmed<Action<SKCanvas, SKColor?, float, int>>(
            "UnallocatedLiteral", svg, warm => warm(canvas, new SKColor(0xdc, 0x26, 0x26), 2f, 2));

        var before = GC.GetAllocatedBytesForCurrentThread();
        draw(canvas, new SKColor(0xdc, 0x26, 0x26), 2f, 2);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void A_Direct_Draw_Left_To_A_List_Default_Allocates_Nothing()
    {
        using var bitmap = new SKBitmap(24, 24);
        using var canvas = new SKCanvas(bitmap);
        var colors = Stops(0xffdc2626, 0xff1e40af);

        var draw = Warmed<Action<SKCanvas, IReadOnlyList<SKColor>, IReadOnlyList<float>, int>>(
            "UnallocatedDefault", Listed, warm => warm(canvas, colors, null!, 2));

        var before = GC.GetAllocatedBytesForCurrentThread();
        draw(canvas, colors, null!, 2);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void A_List_Literal_Reading_Arguments_Answers_Each_Draw_Afresh()
    {
        // The array a site fills is kept between draws, so a second draw must not see the first's.
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="tint" type="color" default="#1e40af" /><e:param name="i" type="integer" default="0" /></e:code></defs>
              <path d="M 2 2 L 22 2 L 12 21 Z" fill="{{ [tint, #fff][i] }}" />
            </svg>
            """;
        var declarations = SvgExpressionDeclarations.Parse(svg);
        var record = Compile(Code(Model(svg), "RefilledLiteral", SvgBody.Record, declarations), "RefilledLiteral")
            .GetType($"{Namespace}.RefilledLiteral")!.GetMethod("Record")!;

        foreach (var arguments in new[]
                 {
                     new object?[] { new SKColor(0xdc, 0x26, 0x26), 0 },
                     new object?[] { new SKColor(0x16, 0xa3, 0x4a), 0 },
                     new object?[] { new SKColor(0x16, 0xa3, 0x4a), 1 }
                 })
        {
            using var runtime = Bound(Model(svg), declarations, arguments);
            using var generated = (SKPicture)record.Invoke(null, arguments)!;

            AssertSamePicture("RefilledLiteral", runtime, generated);
        }
    }

    [Fact]
    public void A_Use_Giving_Its_Drawing_A_List_Passes_It()
    {
        const string code = """
            <e:code>
              <e:param name="colors" type="color[]" default="[#ef4444, #1e40af]" />
              <e:param name="widths" type="number[]" default="[1, 3]" />
              <e:param name="i" type="integer" default="1" />
            </e:code>
            """;
        const string shape = """<path d="M 2 2 L 22 2 L 12 21 Z" fill="{0}" stroke="{1}" stroke-width="{2}" transform="translate(0.5 0.25)" />""";
        const string ground = """<path d="M 0 40 L 48 40 L 48 48 L 0 48 Z" fill="{{ colors[i] }}" />""";
        const string use = """<use href="#sym-listed" e:draws="Listed" e:arg-colors="{{ [#facc15, colors[1]] }}" e:arg-i="{{ 0 }}" transform="translate(8 4)" clip-path="url(#badge-clip)" />""";

        // The copy is the target with what the use gives written in: the colours as a literal reading
        // the caller's own, and the index as 0.
        AssertCallsTheSame(
            "CallsWithAList",
            new object?[] { Stops(0xff0ea5e9, 0xff7c3aed), new List<float> { 2f, 0.5f }, 1 },
            ("UsesWithAList", $"""
                <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="48" height="48" viewBox="0 0 48 48">
                  <defs>
                    {code}
                    <g id="sym-listed">{string.Format(shape, "{{ [#facc15, colors[1]][0] }}", "{{ [#facc15, colors[1]][0 + 1] }}", "{{ widths[0] }}")}</g>
                    <clipPath id="badge-clip"><rect width="24" height="24" /></clipPath>
                  </defs>
                  {ground}
                  {use}
                </svg>
                """),
            ("Listed", $"""
                <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24" viewBox="0 0 24 24">
                  <defs>{code}</defs>
                  {string.Format(shape, "{{ colors[i] }}", "{{ colors[i + 1] }}", "{{ widths[i] }}")}
                </svg>
                """));
    }

    [Fact]
    public void A_Call_Refused_At_A_Later_Argument_Leaves_No_List_Behind()
    {
        // The target reads a tint the caller never declares, so the call is refused at that argument,
        // after the list before it was written: the field that list registered is read by nothing.
        const string target = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24" viewBox="0 0 24 24">
              <defs><e:code><e:param name="colors" type="color[]" default="[#ef4444, #1e40af]" /><e:param name="tint" type="color" default="#facc15" /></e:code></defs>
              <path d="M 2 2 L 22 2 L 12 21 Z" fill="{{ len(colors) > 9 ? tint : colors[0] }}" />
            </svg>
            """;
        const string caller = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="48" height="48" viewBox="0 0 48 48">
              <defs>
                <e:code><e:param name="colors" type="color[]" default="[#0ea5e9, #7c3aed]" /></e:code>
                <g id="sym-toned"><path d="M 2 2 L 22 2 L 12 21 Z" fill="{{ len(colors) > 9 ? #000 : colors[1] }}" /></g>
              </defs>
              <use href="#sym-toned" e:draws="Toned" e:arg-colors="{{ [colors[1], #fff] }}" transform="translate(8 4)" />
            </svg>
            """;

        var code = Together(SvgBody.Direct, bake: false, ("Untoned", caller), ("Toned", target));

        Assert.DoesNotContain($"global::{Namespace}.Toned.Draw(", code);
        Assert.Empty(Warnings(code));
    }

    [Fact]
    public void The_Api_Takes_A_List_As_It_Takes_A_Colour()
    {
        var code = SkiaCSharpCodeGen.GenerateFile(
            new[] { new SkiaCSharpDrawing(Model(Listed), "Icons", "Listed", SvgExpressionDeclarations.Parse(Listed)) { Method = "drawListed" } },
            style: new SvgCodeStyle { Api = "Resources.Api", Order = new[] { "i", "colors", "widths" } });

        Assert.Contains("public static void drawListed(SKCanvas canvas, int i, IReadOnlyList<SKColor> colors, IReadOnlyList<float> widths)", code);
        Assert.Contains("global::Icons.Listed.Draw(canvas, colors: colors, widths: widths, i: i);", code);

        var assembly = Compile(code, "ListedApi");
        var colors = Stops(0xffdc2626, 0xff1e40af);
        var widths = new List<float> { 3f, 1f };

        using var expected = Recorded(new SKRect(0, 0, 24, 24), canvas => assembly.GetType("Icons.Listed")!.GetMethod("Draw")!.Invoke(null, new object?[] { canvas, colors, widths, 1 }));
        using var actual = Recorded(new SKRect(0, 0, 24, 24), canvas => assembly.GetType("Resources.Api")!.GetMethod("drawListed")!.Invoke(null, new object?[] { canvas, 1, colors, widths }));

        AssertSamePicture("ListedApi", expected, actual);
    }

    [Theory]
    [InlineData(SvgBody.Record)]
    [InlineData(SvgBody.Direct)]
    public void Generated_List_Code_Compiles_Without_A_Warning_Nullable_Or_Not(SvgBody body)
    {
        // The reason a defaulted list is not `IReadOnlyList<float>?`: an annotation in a file marked
        // auto-generated is CS8669 in every host build, nullable enabled or not.
        Assert.Empty(Warnings(Code(Model(Listed), $"ListedWarnings{body}", body, SvgExpressionDeclarations.Parse(Listed))));
    }

    /// <summary>What a host building <paramref name="code"/> is warned of, with nullable enabled and disabled.</summary>
    private static IEnumerable<string> Warnings(string code)
        => new[] { NullableContextOptions.Enable, NullableContextOptions.Disable }
            .SelectMany(nullable => CSharpCompilation.Create(
                    $"{Namespace}.Warnings.{++s_generation}",
                    new[] { CSharpSyntaxTree.ParseText(code) },
                    CSharpReferences.All,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: nullable))
                .GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning)
                .Select(diagnostic => $"{nullable} {diagnostic.Id}: {diagnostic.GetMessage()}"))
            .ToList();

    [Theory]
    [InlineData(SvgBody.Record, "{{ [tint, #fff][i] }}")]
    [InlineData(SvgBody.Direct, "{{ [tint, #fff][i] }}")]
    [InlineData(SvgBody.Record, "{{ [#f00, #fff][i] }}")]
    [InlineData(SvgBody.Direct, "{{ [#f00, #fff][i] }}")]
    public void A_List_Behind_A_Range_Folded_To_False_Leaves_No_Field(SvgBody body, string fill)
    {
        // The range is emitted all the same, into text that is thrown away, so that it is checked.
        var svg = $$$"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24" viewBox="0 0 24 24">
              <defs><e:code><e:param name="tint" type="color" default="#facc15" /><e:param name="i" type="integer" default="0" /></e:code></defs>
              <path d="M 2 2 L 22 2 L 12 21 Z" fill="{{ tint }}" />
              <g display="{{ false }}"><path d="M 2 2 L 22 2 L 12 21 Z" fill="{{{fill}}}" /></g>
            </svg>
            """;
        var code = Code(Model(svg), $"Hidden{body}", body, SvgExpressionDeclarations.Parse(svg));

        Assert.DoesNotContain("s_skList", code);
        Assert.Empty(Warnings(code));
    }

    [Theory]
    [InlineData(SvgBody.Record)]
    [InlineData(SvgBody.Direct)]
    public void A_Call_Leaves_No_Field_For_The_Copy_It_Replaces(SvgBody body)
    {
        // The copy's literal is spelled unlike anything the call passes, so they share no field.
        const string target = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="24" height="24" viewBox="0 0 24 24">
              <defs><e:code><e:param name="tint" type="color" default="#facc15" /><e:param name="k" type="integer" default="1" /></e:code></defs>
              <path d="M 2 2 L 22 2 L 12 21 Z" fill="{{ [tint, #fff][k] }}" />
            </svg>
            """;
        const string caller = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="48" height="48" viewBox="0 0 48 48">
              <defs>
                <e:code><e:param name="tint" type="color" default="#0ea5e9" /></e:code>
                <g id="sym-toned"><path d="M 2 2 L 22 2 L 12 21 Z" fill="{{ [tint, #fff][0] }}" /></g>
                <clipPath id="badge-clip"><rect width="24" height="24" /></clipPath>
              </defs>
              <path d="M 0 40 L 48 40 L 48 48 L 0 48 Z" fill="{{ tint }}" />
              <use href="#sym-toned" e:draws="Toned" e:arg-k="{{ 0 }}" transform="translate(8 4)" clip-path="url(#badge-clip)" />
            </svg>
            """;

        var code = Together(body, bake: false, ("Untoned", caller), ("Toned", target));

        Assert.Contains($"global::{Namespace}.Toned.Draw(", code);
        Assert.Empty(Warnings(code));
    }

    // Only a call the folder leaves alone keeps a default's list from folding away: sin() is one.
    private const string ListedDefaults = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
          <defs><e:code>
            <e:param name="tint" type="color" default="[#dc2626, #16a34a][int(sin(1))]" />
            <e:param name="label" type="string" default="str(len([sin(1), 2]))" />
          </e:code></defs>
          <path d="M 2 2 L 22 2 L 12 21 Z" fill="{{ tint }}" stroke="{{ label == '2' ? #1e40af : #000 }}" />
        </svg>
        """;

    [Fact]
    public void A_Default_Reading_A_List_Draws_As_The_Runtime_Binds_It()
        => AssertBindsTheSame("ListedDefaults", ListedDefaults, new object?[] { null, null });

    [Fact]
    public void A_Direct_Draw_Left_To_A_Default_Reading_A_List_Allocates_Nothing()
    {
        using var bitmap = new SKBitmap(24, 24);
        using var canvas = new SKCanvas(bitmap);

        // The label given, since str() makes a string whatever it is given.
        var draw = Warmed<Action<SKCanvas, SKColor?, string>>("UnallocatedListedDefaults", ListedDefaults, warm => warm(canvas, null, "2"));

        var before = GC.GetAllocatedBytesForCurrentThread();
        draw(canvas, null, "2");

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
