using System.Collections.Generic;
using System.Linq;
using ShimSkiaSharp;
using Xunit;

namespace ShimSkiaSharp.UnitTests;

public class SKCanvasTests
{
    private static SKCanvas CreateCanvas()
    {
        var recorder = new SKPictureRecorder();
        return recorder.BeginRecording(SKRect.Create(0, 0, 10, 10));
    }

    [Fact]
    public void SetMatrix_AddsCommandAndUpdatesMatrix()
    {
        var canvas = CreateCanvas();
        var delta = SKMatrix.CreateTranslation(5, 5);
        canvas.SetMatrix(delta);
        Assert.Equal(delta, canvas.TotalMatrix);
        var cmd = Assert.IsType<SetMatrixCanvasCommand>(canvas.Commands!.Single());
        Assert.Equal(delta, cmd.DeltaMatrix);
        Assert.Equal(delta, cmd.TotalMatrix);
    }

    [Fact]
    public void SaveAndRestore_RecordCommands()
    {
        var canvas = CreateCanvas();
        var count = canvas.Save();
        Assert.Equal(1, count);
        canvas.Restore();
        Assert.Equal(2, canvas.Commands!.Count);
        var save = Assert.IsType<SaveCanvasCommand>(canvas.Commands![0]);
        Assert.Equal(0, save.Count);
        var restore = Assert.IsType<RestoreCanvasCommand>(canvas.Commands![1]);
        Assert.Equal(0, restore.Count);
    }

    [Fact]
    public void DrawPicture_RecordsCommand()
    {
        var canvas = CreateCanvas();
        var picture = new SKPicture(
            SKRect.Create(0, 0, 5, 5),
            new List<CanvasCommand> { new SaveCanvasCommand(0) });

        canvas.DrawPicture(picture);

        var command = Assert.IsType<DrawPictureCanvasCommand>(canvas.Commands!.Single());
        Assert.Same(picture, command.Picture);
    }

    [Fact]
    public void PushCommandSource_AppliesMetadataAndRestoresNestedScopes()
    {
        var canvas = CreateCanvas();

        using (canvas.PushCommandSource("outer", "0", "SvgGroup"))
        {
            canvas.Save();

            using (canvas.PushCommandSource("inner", "0/1", "SvgPath"))
            {
                canvas.DrawPath(CloneTestData.CreatePath(), CloneTestData.CreatePaint());
            }

            canvas.Restore();
        }

        canvas.DrawPath(CloneTestData.CreatePath(), CloneTestData.CreatePaint());

        var save = Assert.IsType<SaveCanvasCommand>(canvas.Commands![0]);
        Assert.Equal("outer", save.SourceElementId);
        Assert.Equal("0", save.SourceElementAddress);
        Assert.Equal("SvgGroup", save.SourceElementTypeName);

        var inner = Assert.IsType<DrawPathCanvasCommand>(canvas.Commands![1]);
        Assert.Equal("inner", inner.SourceElementId);
        Assert.Equal("0/1", inner.SourceElementAddress);
        Assert.Equal("SvgPath", inner.SourceElementTypeName);

        var restore = Assert.IsType<RestoreCanvasCommand>(canvas.Commands![2]);
        Assert.Equal("outer", restore.SourceElementId);
        Assert.Equal("0", restore.SourceElementAddress);
        Assert.Equal("SvgGroup", restore.SourceElementTypeName);

        var unscoped = Assert.IsType<DrawPathCanvasCommand>(canvas.Commands![3]);
        Assert.Null(unscoped.SourceElementId);
        Assert.Null(unscoped.SourceElementAddress);
        Assert.Null(unscoped.SourceElementTypeName);
    }

    [Fact]
    public void DeepClone_DoesNotCarryActiveCommandSourceScopeToFutureCommands()
    {
        var canvas = CreateCanvas();

        using var _ = canvas.PushCommandSource("target", "0/1", "SvgPath");
        var clone = canvas.DeepClone();

        clone.DrawPath(CloneTestData.CreatePath(), CloneTestData.CreatePaint());

        var command = Assert.IsType<DrawPathCanvasCommand>(clone.Commands!.Single());
        Assert.Null(command.SourceElementId);
        Assert.Null(command.SourceElementAddress);
        Assert.Null(command.SourceElementTypeName);
    }
}

/// <summary>
/// A driven transform composes its total the way the baked one does, off the same stack.
/// </summary>
/// <remarks>
/// Generated code assigns an absolute matrix where a renderer concatenates a delta, so the total has
/// to be composed while recording — an emitter that had to track a running local would need a save
/// stack of its own, which it does not have.
/// </remarks>
public class SymbolicMatrixTests
{
    private static SKCanvas Canvas() => new SKPictureRecorder().BeginRecording(SKRect.Create(0, 0, 10, 10));

    private static SymMatrix Driven(SymTransformOp op, params SymNode[] arguments)
        => new(new[] { new SymTransform(op, arguments) });

    private static SetMatrixCanvasCommand Recorded(SKCanvas canvas, int index)
        => (SetMatrixCanvasCommand)canvas.Commands![index];

    [Fact]
    public void An_Ordinary_Transform_Records_Nothing_Symbolic()
    {
        var canvas = Canvas();

        canvas.SetMatrix(SKMatrix.CreateTranslation(4f, 0f));

        Assert.Null(Recorded(canvas, 0).SymbolicDelta);
        Assert.Null(Recorded(canvas, 0).SymbolicTotal);
        Assert.Null(canvas.SymbolicTotalMatrix);
    }

    [Fact]
    public void A_Driven_Transform_Is_Carried_On_The_Command()
    {
        var canvas = Canvas();
        var driven = Driven(SymTransformOp.Rotate, SymNode.Source("a"), SymNode.Zero, SymNode.Zero);

        canvas.SetMatrix(SKMatrix.CreateRotationDegrees(0f), driven);

        Assert.Same(driven, Recorded(canvas, 0).SymbolicDelta);
        Assert.True(Recorded(canvas, 0).SymbolicTotal!.IsSymbolic);
    }

    [Fact]
    public void An_Ordinary_Transform_Above_A_Driven_One_Is_Folded_Into_Its_Total()
    {
        var canvas = Canvas();

        canvas.SetMatrix(SKMatrix.CreateScale(2f, 2f));
        canvas.SetMatrix(
            SKMatrix.CreateTranslation(0f, 0f),
            Driven(SymTransformOp.Translate, SymNode.Source("dx"), SymNode.Zero));

        // The scale it sat under, then the translate it drives: the baked ancestor stands in for the
        // side that carries no functions of its own.
        Assert.Collection(
            Recorded(canvas, 1).SymbolicTotal!.Transforms,
            transform => Assert.Equal(SymTransformOp.Matrix, transform.Op),
            transform => Assert.Equal(SymTransformOp.Translate, transform.Op));
    }

    [Fact]
    public void A_Restore_Puts_Back_The_Symbolic_Total_With_The_Baked_One()
    {
        var canvas = Canvas();

        canvas.Save();
        canvas.SetMatrix(
            SKMatrix.CreateRotationDegrees(0f),
            Driven(SymTransformOp.Rotate, SymNode.Source("a"), SymNode.Zero, SymNode.Zero));
        Assert.NotNull(canvas.SymbolicTotalMatrix);

        canvas.Restore();

        Assert.Null(canvas.SymbolicTotalMatrix);
    }

    /// <summary>
    /// What follows a Restore still sits under what was driven above it.
    /// </summary>
    /// <remarks>
    /// The null assertion above passes against a Restore that clears the field rather than popping
    /// the frame, and against one that never pushed it. Only a second child, recorded after the
    /// first has restored, tells those apart — and it shows up nowhere at run time, because a
    /// renderer concatenates the delta and only generated code assigns the total.
    /// </remarks>
    [Fact]
    public void What_Follows_A_Restore_Is_Still_Under_The_Driven_Ancestor()
    {
        var canvas = Canvas();

        canvas.SetMatrix(
            SKMatrix.CreateTranslation(0f, 0f),
            Driven(SymTransformOp.Translate, SymNode.Source("dx"), SymNode.Zero));

        canvas.Save();
        canvas.SetMatrix(SKMatrix.CreateRotationDegrees(30f));
        canvas.Restore();

        canvas.SetMatrix(SKMatrix.CreateTranslation(4f, 0f));

        var total = Recorded(canvas, 4).SymbolicTotal;

        Assert.NotNull(total);
        Assert.Contains(
            total!.Transforms,
            transform => transform.Op == SymTransformOp.Translate && !transform.IsLiteral);
    }

    [Fact]
    public void A_Chain_Grows_With_Driven_Functions_And_Not_With_Depth()
    {
        var canvas = Canvas();

        canvas.SetMatrix(
            SKMatrix.CreateTranslation(0f, 0f),
            Driven(SymTransformOp.Translate, SymNode.Source("dx"), SymNode.Zero));
        canvas.SetMatrix(SKMatrix.CreateScale(2f, 2f));
        canvas.SetMatrix(SKMatrix.CreateTranslation(3f, 3f));

        Assert.Equal(2, Recorded(canvas, 2).SymbolicTotal!.Transforms.Count);
    }
}
