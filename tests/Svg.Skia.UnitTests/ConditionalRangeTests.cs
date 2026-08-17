// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;
using System.Linq;
using ShimSkiaSharp;
using Svg.Expressions;
using Svg.SceneGraph;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>
/// How the evaluator resolves a conditional range, on pictures built by hand.
/// </summary>
/// <remarks>
/// <para>
/// Built by hand rather than compiled from SVG on purpose. <c>SvgSceneRenderer</c> opens a range
/// around everything a node contributes, so every range a document can produce is balanced — a
/// matrix or clip inside one sits inside a <c>Save</c> that the range's own <c>Restore</c> pops. That
/// makes deleting a false range and keeping its state commands indistinguishable through any SVG,
/// which the render tests confirm by passing either way.
/// </para>
/// <para>
/// The rewriter still must not depend on it. The balance is the recorder's guarantee, not the
/// model's, and a picture that arrives some other way has to render the same. These are the tests
/// that say what the rewriter promises, and they are the ones that fail if a suppressed range starts
/// being deleted.
/// </para>
/// </remarks>
public class ConditionalRangeTests
{
    private static ExprEvaluator Evaluator(bool shown)
    {
        var symbols = new Dictionary<string, ExprType>(System.StringComparer.Ordinal)
        {
            ["shown"] = ExprType.Boolean
        };

        var values = new Dictionary<string, ExprValue>(System.StringComparer.Ordinal)
        {
            ["shown"] = ExprValue.Boolean(shown)
        };

        return new ExprEvaluator(symbols, values);
    }

    private static SKPicture Picture(params CanvasCommand[] commands)
        => new(SKRect.Create(0, 0, 24, 24), commands.ToList());

    private static SKPicture Evaluate(SKPicture picture, bool shown)
    {
        var result = SvgSceneExpressionEvaluator.Evaluate(picture, Evaluator(shown));
        Assert.NotNull(result);

        return result!;
    }

    private static readonly SKMatrix s_translate = SKMatrix.CreateTranslation(0f, -8f);

    private static SKPaint Red => new() { Color = new SKColor(255, 0, 0, 255) };

    [Fact]
    public void A_Matrix_Inside_A_False_Range_Survives_When_Nothing_Restores_It()
    {
        // The case that makes state preservation load-bearing. No Save wraps the SetMatrix, so
        // deleting the range would drop a delta the later DrawPath depends on — the runtime renderer
        // applies Concat(DeltaMatrix), unlike generated code, which restates the absolute matrix.
        var picture = Picture(
            new BeginConditionalCanvasCommand(SymNode.Source("shown")),
            new SetMatrixCanvasCommand(s_translate, s_translate),
            new DrawPathCanvasCommand(new SKPath(), Red),
            new EndConditionalCanvasCommand(),
            new DrawPathCanvasCommand(new SKPath(), Red));

        var evaluated = Evaluate(picture, shown: false);

        Assert.Collection(
            evaluated.Commands!,
            command => Assert.IsType<SetMatrixCanvasCommand>(command),
            command => Assert.IsType<DrawPathCanvasCommand>(command));
    }

    [Fact]
    public void Save_And_Restore_Inside_A_False_Range_Survive_So_The_Depth_Balances()
    {
        var picture = Picture(
            new BeginConditionalCanvasCommand(SymNode.Source("shown")),
            new SaveCanvasCommand(0),
            new SetMatrixCanvasCommand(s_translate, s_translate),
            new DrawPathCanvasCommand(new SKPath(), Red),
            new RestoreCanvasCommand(0),
            new EndConditionalCanvasCommand());

        var evaluated = Evaluate(picture, shown: false);

        Assert.Collection(
            evaluated.Commands!,
            command => Assert.IsType<SaveCanvasCommand>(command),
            command => Assert.IsType<SetMatrixCanvasCommand>(command),
            command => Assert.IsType<RestoreCanvasCommand>(command));
    }

    [Fact]
    public void A_False_Range_Drops_Every_Kind_Of_Drawing_Command()
    {
        var picture = Picture(
            new BeginConditionalCanvasCommand(SymNode.Source("shown")),
            new DrawPathCanvasCommand(new SKPath(), Red),
            new DrawTextCanvasCommand("x", 0f, 0f, Red),
            new DrawImageCanvasCommand(null, SKRect.Empty, SKRect.Empty),
            new DrawPictureCanvasCommand(Picture()),
            new EndConditionalCanvasCommand());

        var evaluated = Evaluate(picture, shown: false);

        Assert.Empty(evaluated.Commands!);
    }

    [Fact]
    public void A_True_Range_Keeps_Its_Contents_And_Loses_Only_The_Markers()
    {
        var picture = Picture(
            new BeginConditionalCanvasCommand(SymNode.Source("shown")),
            new SaveCanvasCommand(0),
            new DrawPathCanvasCommand(new SKPath(), Red),
            new RestoreCanvasCommand(0),
            new EndConditionalCanvasCommand());

        var evaluated = Evaluate(picture, shown: true);

        // The markers go either way: a resolved conditional is not a conditional any more, and a
        // renderer that still saw one would have no way to know it had been dealt with.
        Assert.Collection(
            evaluated.Commands!,
            command => Assert.IsType<SaveCanvasCommand>(command),
            command => Assert.IsType<DrawPathCanvasCommand>(command),
            command => Assert.IsType<RestoreCanvasCommand>(command));
    }

    [Fact]
    public void A_Nested_Range_Is_Matched_By_Depth_Rather_Than_By_The_Next_End()
    {
        // Two conditions, the outer one true and the inner false, so finding the closing marker by
        // scanning for the first End would swallow the outer range's tail.
        var symbols = new Dictionary<string, ExprType>(System.StringComparer.Ordinal)
        {
            ["outer"] = ExprType.Boolean,
            ["inner"] = ExprType.Boolean
        };

        var values = new Dictionary<string, ExprValue>(System.StringComparer.Ordinal)
        {
            ["outer"] = ExprValue.Boolean(true),
            ["inner"] = ExprValue.Boolean(false)
        };

        var picture = Picture(
            new BeginConditionalCanvasCommand(SymNode.Source("outer")),
            new DrawTextCanvasCommand("before", 0f, 0f, Red),
            new BeginConditionalCanvasCommand(SymNode.Source("inner")),
            new DrawTextCanvasCommand("hidden", 0f, 0f, Red),
            new EndConditionalCanvasCommand(),
            new DrawTextCanvasCommand("after", 0f, 0f, Red),
            new EndConditionalCanvasCommand());

        var evaluated = SvgSceneExpressionEvaluator.Evaluate(picture, new ExprEvaluator(symbols, values));

        var texts = evaluated!.Commands!.OfType<DrawTextCanvasCommand>().Select(c => c.Text).ToList();

        Assert.Equal(new[] { "before", "after" }, texts);
    }

    [Fact]
    public void A_Condition_Inside_An_Already_Dropped_Range_Is_Not_Evaluated()
    {
        // Generated code nests the ifs, so behind a false outer condition the inner one never runs.
        // Here the inner condition is nonsense that would throw if it were evaluated, which is the
        // only way to observe the difference.
        var symbols = new Dictionary<string, ExprType>(System.StringComparer.Ordinal)
        {
            ["outer"] = ExprType.Boolean
        };

        var values = new Dictionary<string, ExprValue>(System.StringComparer.Ordinal)
        {
            ["outer"] = ExprValue.Boolean(false)
        };

        var picture = Picture(
            new BeginConditionalCanvasCommand(SymNode.Source("outer")),
            new BeginConditionalCanvasCommand(SymNode.Source("no_such_name")),
            new DrawPathCanvasCommand(new SKPath(), Red),
            new EndConditionalCanvasCommand(),
            new EndConditionalCanvasCommand());

        var evaluated = SvgSceneExpressionEvaluator.Evaluate(picture, new ExprEvaluator(symbols, values));

        Assert.Empty(evaluated!.Commands!);
    }

    [Fact]
    public void An_Unterminated_Range_Consumes_The_Rest_Of_The_Picture()
    {
        // The recorder cannot produce this, so the point is only that it resolves rather than
        // looping or throwing, and that the markers do not reach a renderer.
        var picture = Picture(
            new BeginConditionalCanvasCommand(SymNode.Source("shown")),
            new DrawPathCanvasCommand(new SKPath(), Red));

        var evaluated = Evaluate(picture, shown: true);

        Assert.Collection(evaluated.Commands!, command => Assert.IsType<DrawPathCanvasCommand>(command));
    }

    [Fact]
    public void A_Picture_With_No_Conditionals_And_No_Expressions_Comes_Back_Unchanged()
    {
        // Reference equality, so a document without expressions costs a walk and no allocation.
        var picture = Picture(
            new SaveCanvasCommand(0),
            new DrawPathCanvasCommand(new SKPath(), Red),
            new RestoreCanvasCommand(0));

        Assert.Same(picture, Evaluate(picture, shown: true));
    }
}
