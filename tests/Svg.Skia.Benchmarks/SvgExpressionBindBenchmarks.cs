using System.Collections.Generic;
using System.Text;
using BenchmarkDotNet.Attributes;
using Svg.Expressions;

namespace Svg.Skia.Benchmarks;

/// <summary>
/// What binding a value costs, for each of the two kinds of expression.
/// </summary>
/// <remarks>
/// The two are here together because the only interesting number is the ratio. A colour is rewritten
/// into the recorded drawing; a string in text content is substituted into the document and the scene
/// is compiled again, because the text has to be measured with it. The second is bound to be slower,
/// and how much slower is what decides whether a host has to throttle it -- so it is measured rather
/// than guessed at.
///
/// Both documents draw the same shapes so the difference is the path and not the drawing.
/// </remarks>
public class SvgExpressionBindBenchmarks
{
    private SKSvg? recompiled;
    private SKSvg? evaluated;
    private int step;

    [Params(10, 50, 200)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        recompiled = new SKSvg();
        recompiled.FromSvg(Markup("{{ label }}", "#336699"));

        evaluated = new SKSvg();
        evaluated.FromSvg(Markup("Item", "{{ ink }}"));
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        recompiled?.Dispose();
        evaluated?.Dispose();
    }

    /// <summary>A string in text content: substituted into the document, and the scene recompiled.</summary>
    [Benchmark]
    [BenchmarkCategory("Expressions", "BeforeRecording")]
    public object? BindTextContent()
    {
        step++;

        return recompiled!.SetExpressionValues(
            new Dictionary<string, ExprValue> { ["label"] = ExprValue.String("Item " + step) });
    }

    /// <summary>A colour: rewritten into the recorded drawing, with nothing compiled again.</summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Expressions", "Recorded")]
    public object? BindPaint()
    {
        step++;

        return evaluated!.SetExpressionValues(
            new Dictionary<string, ExprValue> { ["ink"] = ExprValue.Color((byte)(step & 0xFF), 0x66, 0x99, 0xFF) });
    }

    private string Markup(string text, string fill)
    {
        var body = new StringBuilder();

        for (var row = 0; row < Rows; row++)
        {
            body.Append($"<rect x='1' y='{row}' width='10' height='2' fill='{fill}' />");
            body.Append($"<text x='20' y='{row + 8}' font-size='6'>{text}</text>");
        }

        return $"<svg xmlns='http://www.w3.org/2000/svg' xmlns:e='https://svg.skia/expr/1.0' width='400' height='{Rows + 20}'>"
             + "<defs><e:code>"
             + "<e:param name='label' type='string' default=\"'Item'\" />"
             + "<e:param name='ink' type='color' default='#336699' />"
             + "</e:code></defs>"
             + body
             + "</svg>";
    }
}
