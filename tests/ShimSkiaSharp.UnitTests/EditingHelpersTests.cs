using System;
using System.Collections.Generic;
using System.Linq;
using ShimSkiaSharp;
using ShimSkiaSharp.Editing;
using Xunit;

namespace ShimSkiaSharp.UnitTests;

public class EditingHelpersTests
{
    [Fact]
    public void FindCommands_ReturnsMatchingCommands()
    {
        var picture = new SKPicture(
            SKRect.Create(0, 0, 10, 10),
            new List<CanvasCommand>
            {
                new DrawPathCanvasCommand(CloneTestData.CreatePath(), CloneTestData.CreatePaint()),
                new SaveCanvasCommand(1)
            });

        var commands = picture.FindCommands<DrawPathCanvasCommand>().ToList();

        Assert.Single(commands);
        Assert.IsType<DrawPathCanvasCommand>(commands[0]);
    }

    [Fact]
    public void ReplaceCommands_RemovesAndReplaces()
    {
        var originalPath = CloneTestData.CreatePath();
        var newPath = CloneTestData.CreatePath();
        var commands = new List<CanvasCommand>
        {
            new DrawTextCanvasCommand("Text", 1, 2, null),
            new DrawPathCanvasCommand(originalPath, null)
        };
        var picture = new SKPicture(SKRect.Create(0, 0, 10, 10), commands);

        var replaced = picture.ReplaceCommands(command =>
            command is DrawTextCanvasCommand
                ? null
                : new DrawPathCanvasCommand(newPath, null));

        Assert.Equal(2, replaced);
        Assert.Single(commands);
        var drawPath = Assert.IsType<DrawPathCanvasCommand>(commands[0]);
        Assert.Same(newPath, drawPath.Path);
    }

    [Fact]
    public void FindCommands_TraversesNestedPictures()
    {
        var nestedPicture = new SKPicture(
            SKRect.Create(0, 0, 10, 10),
            new List<CanvasCommand>
            {
                new DrawPathCanvasCommand(CloneTestData.CreatePath(), CloneTestData.CreatePaint())
            });
        var picture = new SKPicture(
            SKRect.Create(0, 0, 10, 10),
            new List<CanvasCommand>
            {
                new DrawPictureCanvasCommand(nestedPicture)
            });

        var commands = picture.FindCommands<DrawPathCanvasCommand>().ToList();

        Assert.Single(commands);
    }

    [Fact]
    public void FindCommandsBySourceElementId_FiltersNestedCommands()
    {
        var nestedPicture = new SKPicture(
            SKRect.Create(0, 0, 10, 10),
            new List<CanvasCommand>
            {
                new DrawPathCanvasCommand(CloneTestData.CreatePath(), CloneTestData.CreatePaint())
                {
                    SourceElementId = "target",
                    SourceElementAddress = "0/1",
                    SourceElementTypeName = "SvgPath"
                }
            });
        var picture = new SKPicture(
            SKRect.Create(0, 0, 10, 10),
            new List<CanvasCommand>
            {
                new DrawPictureCanvasCommand(nestedPicture),
                new DrawPathCanvasCommand(CloneTestData.CreatePath(), CloneTestData.CreatePaint())
                {
                    SourceElementId = "other",
                    SourceElementAddress = "0/2",
                    SourceElementTypeName = "SvgPath"
                }
            });

        var commands = picture.FindCommandsBySourceElementId<DrawPathCanvasCommand>("target").ToList();

        var command = Assert.Single(commands);
        Assert.Equal("0/1", command.SourceElementAddress);
        Assert.Equal("SvgPath", command.SourceElementTypeName);
    }

    [Fact]
    public void FindCommandsBySourceElementAddress_FiltersCommands()
    {
        var picture = new SKPicture(
            SKRect.Create(0, 0, 10, 10),
            new List<CanvasCommand>
            {
                new DrawPathCanvasCommand(CloneTestData.CreatePath(), CloneTestData.CreatePaint())
                {
                    SourceElementId = "target-a",
                    SourceElementAddress = "0/1",
                    SourceElementTypeName = "SvgPath"
                },
                new DrawPathCanvasCommand(CloneTestData.CreatePath(), CloneTestData.CreatePaint())
                {
                    SourceElementId = "target-b",
                    SourceElementAddress = "0/2",
                    SourceElementTypeName = "SvgPath"
                }
            });

        var commands = picture.FindCommandsBySourceElementAddress<DrawPathCanvasCommand>("0/2").ToList();

        var command = Assert.Single(commands);
        Assert.Equal("target-b", command.SourceElementId);
    }

    [Fact]
    public void UpdatePaints_InPlace_UpdatesUniquePaints()
    {
        var sharedPaint = CloneTestData.CreatePaint();
        var commands = new List<CanvasCommand>
        {
            new DrawPathCanvasCommand(CloneTestData.CreatePath(), sharedPaint),
            new DrawImageCanvasCommand(CloneTestData.CreateImage(), SKRect.Create(0, 0, 1, 1), SKRect.Create(0, 0, 1, 1), sharedPaint)
        };
        var picture = new SKPicture(SKRect.Create(0, 0, 10, 10), commands);

        var updated = picture.UpdatePaints(
            paint => paint.Color is { },
            paint => paint.Color = new SKColor(9, 9, 9, 9));

        Assert.Equal(1, updated);
        Assert.Equal(new SKColor(9, 9, 9, 9), sharedPaint.Color);
        Assert.Same(sharedPaint, ((DrawPathCanvasCommand)commands[0]).Paint);
    }

    [Fact]
    public void UpdatePaints_CloneOnWrite_SharedPaintUsesClone()
    {
        var sharedPaint = CloneTestData.CreatePaint();
        var commands = new List<CanvasCommand>
        {
            new DrawPathCanvasCommand(CloneTestData.CreatePath(), sharedPaint),
            new DrawPathCanvasCommand(CloneTestData.CreatePath(), sharedPaint)
        };
        var picture = new SKPicture(SKRect.Create(0, 0, 10, 10), commands);

        var updated = picture.UpdatePaints(
            paint => paint.Color is { },
            paint => paint.Color = new SKColor(7, 7, 7, 7),
            EditMode.CloneOnWrite);

        Assert.Equal(1, updated);
        var first = ((DrawPathCanvasCommand)commands[0]).Paint!;
        var second = ((DrawPathCanvasCommand)commands[1]).Paint!;
        Assert.NotSame(sharedPaint, first);
        Assert.Same(first, second);
        Assert.Equal(new SKColor(1, 2, 3, 4), sharedPaint.Color);
        Assert.Equal(new SKColor(7, 7, 7, 7), first.Color);
    }

    [Fact]
    public void UpdatePaints_CloneOnWrite_PreservesSourceMetadata()
    {
        var command = new DrawPathCanvasCommand(CloneTestData.CreatePath(), CloneTestData.CreatePaint())
        {
            SourceElementId = "target",
            SourceElementAddress = "0/1",
            SourceElementTypeName = "SvgPath"
        };
        var picture = new SKPicture(
            SKRect.Create(0, 0, 10, 10),
            new List<CanvasCommand>
            {
                command
            });

        picture.UpdatePaints(
            _ => true,
            paint => paint.Color = new SKColor(0, 1, 2, 3),
            EditMode.CloneOnWrite);

        var updated = Assert.IsType<DrawPathCanvasCommand>(picture.Commands!.Single());
        Assert.NotSame(command, updated);
        Assert.Equal("target", updated.SourceElementId);
        Assert.Equal("0/1", updated.SourceElementAddress);
        Assert.Equal("SvgPath", updated.SourceElementTypeName);
    }

    [Fact]
    public void UpdatePaints_CloneOnWrite_LeavesNonMatchingPaints()
    {
        var paintA = CloneTestData.CreatePaint();
        var paintB = CloneTestData.CreatePaint();
        var commands = new List<CanvasCommand>
        {
            new DrawPathCanvasCommand(CloneTestData.CreatePath(), paintA),
            new DrawPathCanvasCommand(CloneTestData.CreatePath(), paintB)
        };
        var picture = new SKPicture(SKRect.Create(0, 0, 10, 10), commands);

        var updated = picture.UpdatePaints(
            paint => ReferenceEquals(paint, paintA),
            paint => paint.Color = new SKColor(4, 4, 4, 4),
            EditMode.CloneOnWrite);

        Assert.Equal(1, updated);
        var updatedPaint = ((DrawPathCanvasCommand)commands[0]).Paint!;
        var untouchedPaint = ((DrawPathCanvasCommand)commands[1]).Paint!;
        Assert.NotSame(paintA, updatedPaint);
        Assert.Same(paintB, untouchedPaint);
    }

    [Fact]
    public void UpdatePaths_InPlace_UpdatesUniquePaths()
    {
        var sharedPath = CloneTestData.CreatePath();
        var commands = new List<CanvasCommand>
        {
            new DrawPathCanvasCommand(sharedPath, null),
            new DrawTextOnPathCanvasCommand("Text", sharedPath, 0, 0, null)
        };
        var picture = new SKPicture(SKRect.Create(0, 0, 10, 10), commands);
        var originalCount = sharedPath.Commands!.Count;

        var updated = picture.UpdatePaths(
            path => ReferenceEquals(path, sharedPath),
            path => path.LineTo(9, 9));

        Assert.Equal(1, updated);
        Assert.Equal(originalCount + 1, sharedPath.Commands!.Count);
        Assert.Same(sharedPath, ((DrawPathCanvasCommand)commands[0]).Path);
    }

    [Fact]
    public void UpdatePaths_CloneOnWrite_ClonesSharedPath()
    {
        var sharedPath = CloneTestData.CreatePath();
        var commands = new List<CanvasCommand>
        {
            new DrawPathCanvasCommand(sharedPath, null),
            new DrawPathCanvasCommand(sharedPath, null)
        };
        var picture = new SKPicture(SKRect.Create(0, 0, 10, 10), commands);
        var originalCount = sharedPath.Commands!.Count;

        var updated = picture.UpdatePaths(
            path => ReferenceEquals(path, sharedPath),
            path => path.LineTo(9, 9),
            EditMode.CloneOnWrite);

        Assert.Equal(1, updated);
        var first = ((DrawPathCanvasCommand)commands[0]).Path!;
        var second = ((DrawPathCanvasCommand)commands[1]).Path!;
        Assert.NotSame(sharedPath, first);
        Assert.Same(first, second);
        Assert.Equal(originalCount, sharedPath.Commands!.Count);
        Assert.Equal(originalCount + 1, first.Commands!.Count);
    }

    [Fact]
    public void UpdatePaths_CloneOnWrite_UpdatesClipPaths()
    {
        var clipPath = CloneTestData.CreateClipPath();
        var commands = new List<CanvasCommand>
        {
            new ClipPathCanvasCommand(clipPath, SKClipOperation.Intersect, false)
        };
        var picture = new SKPicture(SKRect.Create(0, 0, 10, 10), commands);

        var updated = picture.UpdatePaths(
            path => path.Commands is { Count: > 0 },
            path => path.LineTo(5, 5),
            EditMode.CloneOnWrite);

        var updatedClip = ((ClipPathCanvasCommand)commands[0]).ClipPath!;
        Assert.NotSame(clipPath, updatedClip);
        Assert.Equal(2, updated);
    }

    [Fact]
    public void UpdatePaths_CloneOnWrite_SharedPathUpdatesOnce()
    {
        var sharedPath = CloneTestData.CreatePath();
        var clipPath = new ClipPath();
        clipPath.Clips!.Add(new PathClip { Path = sharedPath });
        var commands = new List<CanvasCommand>
        {
            new DrawPathCanvasCommand(sharedPath, null),
            new ClipPathCanvasCommand(clipPath, SKClipOperation.Intersect, false)
        };
        var picture = new SKPicture(SKRect.Create(0, 0, 10, 10), commands);
        var originalCount = sharedPath.Commands!.Count;

        var updated = picture.UpdatePaths(
            path => ReferenceEquals(path, sharedPath),
            path => path.LineTo(5, 5),
            EditMode.CloneOnWrite);

        Assert.Equal(1, updated);
        var updatedDrawPath = ((DrawPathCanvasCommand)commands[0]).Path!;
        var updatedClipPath = ((ClipPathCanvasCommand)commands[1]).ClipPath!;
        var updatedClipPathPath = updatedClipPath.Clips![0].Path!;
        Assert.NotSame(sharedPath, updatedDrawPath);
        Assert.Same(updatedDrawPath, updatedClipPathPath);
        Assert.Equal(originalCount + 1, updatedDrawPath.Commands!.Count);
        Assert.Equal(originalCount, sharedPath.Commands!.Count);
    }

    [Fact]
    public void PathUpdateCommands_ReplacesMatchingCommands()
    {
        var path = new SKPath();
        path.MoveTo(1, 2);
        path.LineTo(3, 4);

        var updated = path.UpdateCommands(
            command => command is MoveToPathCommand,
            command => new MoveToPathCommand(9, 10));

        Assert.Equal(1, updated);
        var move = Assert.IsType<MoveToPathCommand>(path.Commands![0]);
        Assert.Equal(9, move.X);
        Assert.Equal(10, move.Y);
    }

    [Fact]
    public void PathTransform_MapsCommands()
    {
        var path = new SKPath();
        path.MoveTo(1, 2);
        path.LineTo(3, 4);
        path.AddCircle(1, 1, 2);

        var matrix = SKMatrix.CreateTranslation(2, 3);
        path.Transform(matrix);

        var move = Assert.IsType<MoveToPathCommand>(path.Commands![0]);
        var line = Assert.IsType<LineToPathCommand>(path.Commands![1]);
        var circle = Assert.IsType<AddCirclePathCommand>(path.Commands![2]);
        Assert.Equal(3, move.X);
        Assert.Equal(5, move.Y);
        Assert.Equal(5, line.X);
        Assert.Equal(7, line.Y);
        Assert.Equal(3, circle.X);
        Assert.Equal(4, circle.Y);
    }

    [Fact]
    public void PathTransform_ConvertsCircleToOvalOnNonUniformScale()
    {
        var path = new SKPath();
        path.AddCircle(1, 1, 2);

        var matrix = SKMatrix.CreateScale(2, 3);
        path.Transform(matrix);

        Assert.IsType<AddOvalPathCommand>(path.Commands![0]);
    }

    [Fact]
    public void PathTransform_ThrowsOnRotation()
    {
        var path = new SKPath();
        path.MoveTo(1, 2);

        Assert.Throws<NotSupportedException>(() => path.Transform(SKMatrix.CreateRotationDegrees(90)));
    }

    [Fact]
    public void PathTransform_FlipsArcSweepOnReflection()
    {
        var path = new SKPath();
        path.ArcTo(2, 3, 0, SKPathArcSize.Small, SKPathDirection.Clockwise, 4, 5);

        path.Transform(SKMatrix.CreateScale(-1, 1));

        var arc = Assert.IsType<ArcToPathCommand>(path.Commands![0]);
        Assert.Equal(SKPathDirection.CounterClockwise, arc.Sweep);
    }

    [Fact]
    public void ApplyColorTransform_UpdatesColor()
    {
        var paint = CloneTestData.CreatePaint();

        paint.ApplyColorTransform(color => new SKColor(8, 8, 8, color.Alpha));

        Assert.Equal(new SKColor(8, 8, 8, 4), paint.Color);
    }

    [Fact]
    public void ApplyShaderTransform_UpdatesShader()
    {
        var paint = CloneTestData.CreatePaint();
        var newShader = SKShader.CreateColor(new SKColor(9, 9, 9, 9), SKColorSpace.Srgb);

        paint.ApplyShaderTransform(_ => newShader);

        Assert.Same(newShader, paint.Shader);
    }

    [Fact]
    public void CanvasCommandVisitor_VisitsExpectedCommand()
    {
        var visited = new List<string>();
        var visitor = new TrackingVisitor(visited);
        var command = new DrawTextCanvasCommand("Text", 1, 2, null);

        command.Accept(visitor);

        Assert.Single(visited);
        Assert.Equal(nameof(DrawTextCanvasCommand), visited[0]);
    }

    [Fact]
    public void CommandPlan_SavesInsteadOfALayerOverSourceOverContent_WhateverTheAlpha()
    {
        Assert.True(Saves(Layer(new SKPaint(), Draw()), 0));
        Assert.True(Saves(Layer(new SKPaint { Color = new SKColor(255, 255, 255, 51) }, Draw()), 0));
        Assert.True(Saves(Layer(null, Draw()), 0));
    }

    [Fact]
    public void CommandPlan_KeepsALayerWhosePaintChangesSomething()
    {
        var paints = new Dictionary<string, SKPaint>
        {
            ["blend mode"] = new() { BlendMode = SKBlendMode.Multiply },
            ["colour filter"] = new() { ColorFilter = SKColorFilter.CreateLumaColor() },
            ["image filter"] = new() { ImageFilter = SKImageFilter.CreateBlur(2, 2) },
            ["shader"] = new() { Shader = SKShader.CreateColor(new SKColor(0, 0, 0, 255), SKColorSpace.Srgb) },
            ["path effect"] = new() { PathEffect = SKPathEffect.CreateDash(new[] { 1f, 1f }, 0) },
            ["no colour"] = new() { Color = null }
        };

        foreach (var (because, paint) in paints)
        {
            Assert.False(Saves(Layer(paint, Fill(SKRect.Create(2, 2, 10, 10))), 0), because);
        }
    }

    [Fact]
    public void CommandPlan_KeepsALayerThatKeepsADstInOffTheBackdrop()
    {
        // The shape a mask records: opaque source-over outside, DstIn inside.
        var mask = Layer(
            new SKPaint(),
            Draw(),
            new SaveLayerCanvasCommand(1, new SKPaint { BlendMode = SKBlendMode.DstIn }),
            Draw(),
            new RestoreCanvasCommand(1));

        Assert.False(Saves(mask, 0));
        Assert.False(Saves(mask, 2));
    }

    [Fact]
    public void CommandPlan_LooksInsideNestedPicturesForBlending()
    {
        var nested = new SKPicture(SKRect.Create(0, 0, 10, 10), new List<CanvasCommand> { Draw(SKBlendMode.Multiply) });

        Assert.False(Saves(Layer(new SKPaint(), new DrawPictureCanvasCommand(nested)), 0));
    }

    [Fact]
    public void CommandPlan_JudgesALayerByItsOwnScope()
    {
        var commands = Layer(new SKPaint(), new SaveCanvasCommand(1), Draw(), new RestoreCanvasCommand(1), Draw());
        commands.Add(Draw(SKBlendMode.Multiply));

        Assert.True(Saves(commands, 0));
    }

    [Fact]
    public void CommandPlan_IgnoresALayersBounds()
    {
        var commands = Layer(new SKPaint(), Draw());
        commands[0] = new SaveLayerCanvasCommand(0, new SKPaint(), SKRect.Create(0, 0, 10, 10));

        Assert.True(Saves(commands, 0));
    }

    [Fact]
    public void CommandPlan_SkipsAClipThatContainsEverythingUnderIt()
    {
        var commands = Clipped(
            ClipRect(s_frame),
            Fill(SKRect.Create(2, 2, 16, 16)),
            Stroke(new AddRectPathCommand(SKRect.Create(5, 5, 10, 10)), 2));

        Assert.True(Plan(commands).Skips(1));
    }

    [Fact]
    public void CommandPlan_KeepsAClipThatCuts()
    {
        Assert.False(Plan(Clipped(ClipRect(s_frame), Fill(SKRect.Create(2, 2, 19, 19)))).Skips(1));
    }

    [Fact]
    public void CommandPlan_MeasuresAStrokeAtItsFullWidth()
    {
        var line = new PathCommand[] { new MoveToPathCommand(1, 10), new LineToPathCommand(19, 10) };
        var square = new AddRectPathCommand(SKRect.Create(1.6f, 1.6f, 16.8f, 16.8f));

        // Its 53° turn puts a miter's tip 1/sin(26.6°) ≈ 2.24 half-widths out, past the bottom edge.
        var sharp = new PathCommand[] { new MoveToPathCommand(2, 2), new LineToPathCommand(10, 18), new LineToPathCommand(18, 2) };

        // circle-chart's polyline: turns of a few degrees under a miter limit of 10.
        var shallow = new PathCommand[] { new MoveToPathCommand(1, 10), new LineToPathCommand(10, 9), new LineToPathCommand(19, 10) };

        Assert.True(Skips(Stroke(line, 2, cap: SKStrokeCap.Butt)));
        Assert.False(Skips(Stroke(line, 4, cap: SKStrokeCap.Butt)), "half the width crosses");
        Assert.False(Skips(Stroke(line, 2, cap: SKStrokeCap.Square)), "a square cap reaches √2 × half the width");
        Assert.True(Skips(Stroke(square, 2, join: SKStrokeJoin.Miter, miter: 4)), "a right angle's miter reaches √2 × half the width");
        Assert.False(Skips(Stroke(square, 3, join: SKStrokeJoin.Miter, miter: 4)));
        Assert.True(Skips(Stroke(square, 3, join: SKStrokeJoin.Bevel, miter: 4)));
        Assert.False(Skips(Stroke(sharp, 2, join: SKStrokeJoin.Miter, miter: 10)), "the miter's tip crosses");
        Assert.True(Skips(Stroke(sharp, 2, join: SKStrokeJoin.Miter, miter: 2)), "past the limit the join is a bevel");
        Assert.True(Skips(Stroke(shallow, 1, join: SKStrokeJoin.Miter, miter: 10)), "not the full limit at every vertex");

        static bool Skips(CanvasCommand draw) => Plan(Clipped(ClipRect(s_frame), draw)).Skips(1);
    }

    [Fact]
    public void CommandPlan_ComparesADrawWithAClipInEachOnesOwnSpace()
    {
        // A square turned 45° about its centre holds an upright square of half-size 10/√2 ≈ 7.07.
        var turn = SKMatrix.CreateRotationDegrees(45, 10, 10);
        var back = SKMatrix.CreateRotationDegrees(-45, 10, 10);
        var diamond = ClipPath(new AddPolyPathCommand(
            new[] { new SKPoint(0, 0), new SKPoint(20, 0), new SKPoint(20, 20), new SKPoint(0, 20) },
            true));

        Assert.True(Skips(Fill(SKRect.Create(4, 4, 12, 12))));
        Assert.False(Skips(Fill(SKRect.Create(2, 2, 16, 16))));

        bool Skips(CanvasCommand draw)
            => Plan(Clipped(Turn(turn), diamond, Turn(back), draw)).Skips(2);
    }

    [Fact]
    public void CommandPlan_TestsRoundShapesExactly()
    {
        var oval = ClipPath(new AddOvalPathCommand(s_frame));
        var roundRect = ClipPath(new AddRoundRectPathCommand(s_frame, 6, 6));

        // An upright square of half-size 10/√2 fits the circle; its bounding box does not.
        Assert.True(Plan(Clipped(oval, Fill(SKRect.Create(3, 3, 14, 14)))).Skips(1));
        Assert.False(Plan(Clipped(oval, Fill(SKRect.Create(1, 1, 18, 18)))).Skips(1));
        Assert.True(Plan(Clipped(roundRect, Fill(SKRect.Create(2, 2, 16, 16)))).Skips(1));
        Assert.False(Plan(Clipped(roundRect, Fill(SKRect.Create(0, 0, 20, 20)))).Skips(1));
    }

    [Fact]
    public void CommandPlan_KeepsAClipItCannotTestExactlyOrNeedNotDrop()
    {
        var corner = SKRect.Create(0, 0, 10, 10);
        var lShape = new PathCommand[]
        {
            new MoveToPathCommand(0, 0), new LineToPathCommand(20, 0), new LineToPathCommand(20, 10),
            new LineToPathCommand(10, 10), new LineToPathCommand(10, 20), new LineToPathCommand(0, 20),
            new ClosePathCommand()
        };
        var union = new ClipPath
        {
            Clips = new List<PathClip> { new() { Path = PathOf(new AddRectPathCommand(s_frame)) }, new() { Path = PathOf(new AddRectPathCommand(corner)) } }
        };

        var clips = new Dictionary<string, CanvasCommand>
        {
            ["a concave polygon"] = ClipPath(lShape),
            ["a union"] = new ClipPathCanvasCommand(union, SKClipOperation.Intersect, true),
            ["a difference"] = new ClipRectCanvasCommand(corner, SKClipOperation.Difference, true),
            ["an aliased clip, which costs nothing and cuts whole pixels"] = new ClipRectCanvasCommand(s_frame, SKClipOperation.Intersect, false)
        };

        foreach (var (because, clip) in clips)
        {
            Assert.False(Plan(Clipped(clip, Fill(SKRect.Create(1, 1, 2, 2)))).Skips(1), because);
        }
    }

    [Fact]
    public void CommandPlan_KeepsAClipOverAnythingWithoutBounds()
    {
        var inside = SKRect.Create(5, 5, 10, 10);
        var hairline = Stroke(new AddRectPathCommand(inside), 0);
        var nonScaling = Stroke(new AddRectPathCommand(inside), 1);
        nonScaling.Paint!.IsStrokeNonScaling = true;
        var blurred = Fill(inside);
        blurred.Paint!.ImageFilter = SKImageFilter.CreateBlur(2, 2);
        var arc = new DrawPathCanvasCommand(
            PathOf(new MoveToPathCommand(5, 10), new ArcToPathCommand(5, 2, 30, SKPathArcSize.Small, SKPathDirection.Clockwise, 15, 10)),
            new SKPaint());

        var draws = new Dictionary<string, CanvasCommand[]>
        {
            ["a hairline"] = new CanvasCommand[] { hairline },
            ["a non-scaling stroke"] = new CanvasCommand[] { nonScaling },
            ["an image filter"] = new CanvasCommand[] { blurred },
            ["text"] = new CanvasCommand[] { new DrawTextCanvasCommand("Text", 10, 10, new SKPaint()) },
            ["a turned arc"] = new CanvasCommand[] { arc },
            ["a mask's DstIn layer"] = new CanvasCommand[]
            {
                new SaveLayerCanvasCommand(1, new SKPaint { BlendMode = SKBlendMode.DstIn }), Fill(inside), new RestoreCanvasCommand(1)
            }
        };

        foreach (var (because, contents) in draws)
        {
            Assert.False(Plan(Clipped(contents.Prepend(ClipRect(s_frame)).ToArray())).Skips(1), because);
        }
    }

    [Fact]
    public void CommandPlan_LooksPastAMaskInsideALayerOpenedUnderTheClip()
    {
        // The shape a masked group records: an isolating layer, its content, a DstIn layer with the mask.
        var mask = new CanvasCommand[]
        {
            new SaveLayerCanvasCommand(1, new SKPaint { BlendMode = SKBlendMode.DstIn, ColorFilter = SKColorFilter.CreateLumaColor() }),
            Fill(SKRect.Create(-50, -50, 120, 120)),
            new RestoreCanvasCommand(1)
        };
        var isolated = new CanvasCommand[] { ClipRect(s_frame), new SaveLayerCanvasCommand(1, new SKPaint()), Fill(SKRect.Create(2, 2, 10, 10)) }
            .Concat(mask)
            .Append(new RestoreCanvasCommand(1))
            .ToArray();

        Assert.True(Plan(Clipped(isolated)).Skips(1));
        Assert.False(Plan(Clipped(mask.Prepend(Fill(SKRect.Create(2, 2, 10, 10))).Prepend(ClipRect(s_frame)).ToArray())).Skips(1));
    }

    [Fact]
    public void CommandPlan_KeepsAClipOverDrivenGeometry_UnlessItsValuesAreTheOnesDrawn()
    {
        var slide = new SKMatrix { ScaleX = 1, ScaleY = 1, TransX = 2, Persp2 = 1 };
        var driven = new SymMatrix(new[] { new SymTransform(SymTransformOp.Translate, new[] { SymNode.Source("level"), SymNode.Zero }) });
        var moved = Clipped(
            ClipRect(s_frame),
            new SetMatrixCanvasCommand(slide, slide, driven, driven),
            Fill(SKRect.Create(2, 2, 10, 10)));
        var widened = Stroke(new AddRectPathCommand(SKRect.Create(5, 5, 10, 10)), 1);
        widened.Paint!.StrokeWidthExpression = SymNode.Source("width");

        Assert.False(CommandPlan.For(moved, symbolic: true).Skips(1));
        Assert.True(CommandPlan.For(moved, symbolic: false).Skips(1));
        Assert.False(CommandPlan.For(Clipped(ClipRect(s_frame), widened), symbolic: true).Skips(1));
        Assert.True(CommandPlan.For(Clipped(ClipRect(s_frame), widened), symbolic: false).Skips(1));
    }

    [Fact]
    public void CommandPlan_LooksInsideNestedPicturesForClips()
    {
        var inside = Picture(Fill(SKRect.Create(2, 2, 10, 10)));
        var pushedOut = Picture(Turn(SKMatrix.CreateTranslation(12, 0)), Fill(SKRect.Create(2, 2, 10, 10)));

        Assert.True(Plan(Clipped(ClipRect(s_frame), inside)).Skips(1));
        Assert.False(Plan(Clipped(ClipRect(s_frame), pushedOut)).Skips(1));
    }

    [Fact]
    public void CommandPlan_JudgesAClipByItsOwnScope()
    {
        var commands = Clipped(ClipRect(s_frame), Fill(SKRect.Create(2, 2, 10, 10)));
        commands.Add(Fill(SKRect.Create(-10, -10, 50, 50)));

        Assert.True(Plan(commands).Skips(1));

        // An unrestored clip outlives the list, so nothing in it can vouch for it.
        Assert.False(Plan(new List<CanvasCommand> { ClipRect(s_frame), Fill(SKRect.Create(2, 2, 10, 10)) }).Skips(0));
    }

    [Fact]
    public void CommandPlan_FoldsALayerOverOneShapeIntoItsPaint()
    {
        var faded = new SKPaint { Color = new SKColor(255, 255, 255, 51) };
        var shaded = Fill(SKRect.Create(2, 2, 10, 10));
        shaded.Paint!.Shader = SKShader.CreateColor(new SKColor(0, 0, 255, 255), SKColorSpace.Srgb);

        var plan = Plan(Layer(faded, new SaveCanvasCommand(1), Fill(SKRect.Create(2, 2, 10, 10)), new RestoreCanvasCommand(1)));
        Assert.Equal(LayerPlan.Fold, plan.Layer(0));
        Assert.Equal(new[] { 0 }, plan.FoldedInto(2));

        Assert.Equal(LayerPlan.Fold, Plan(Layer(faded, shaded)).Layer(0));
        Assert.Equal(LayerPlan.Fold, Plan(Layer(null, Fill(SKRect.Create(2, 2, 10, 10)))).Layer(0));
    }

    [Fact]
    public void CommandPlan_FoldsNestedLayersTogether()
    {
        var plan = Plan(Layer(
            new SKPaint { Color = new SKColor(255, 255, 255, 128) },
            new SaveLayerCanvasCommand(1, new SKPaint { Color = new SKColor(255, 255, 255, 51) }),
            Fill(SKRect.Create(2, 2, 10, 10)),
            new RestoreCanvasCommand(1)));

        Assert.Equal(LayerPlan.Fold, plan.Layer(0));
        Assert.Equal(LayerPlan.Fold, plan.Layer(1));
        Assert.Equal(new[] { 1, 0 }, plan.FoldedInto(2));
    }

    [Fact]
    public void CommandPlan_DoesNotFoldWhatCouldOverlapOrChangeMoreThanAlpha()
    {
        var faded = new SKPaint { Color = new SKColor(255, 255, 255, 51) };
        var shape = SKRect.Create(2, 2, 10, 10);
        var blurred = Fill(shape);
        blurred.Paint!.ImageFilter = SKImageFilter.CreateBlur(2, 2);
        var image = new DrawImageCanvasCommand(new SKImage { Data = new byte[] { 1 }, Width = 1, Height = 1 }, SKRect.Create(0, 0, 1, 1), shape, null, null);

        var contents = new Dictionary<string, CanvasCommand[]>
        {
            ["two shapes"] = new CanvasCommand[] { Fill(shape), Fill(shape) },
            ["text"] = new CanvasCommand[] { new DrawTextCanvasCommand("Text", 10, 10, new SKPaint()) },
            ["an image"] = new CanvasCommand[] { image },
            ["a shadow"] = new CanvasCommand[] { blurred },
            ["a hairline"] = new CanvasCommand[] { Stroke(new AddRectPathCommand(shape), 0) },
            ["a layer that stays one"] = new CanvasCommand[]
            {
                new SaveLayerCanvasCommand(1, new SKPaint { BlendMode = SKBlendMode.Multiply }), Fill(shape), new RestoreCanvasCommand(1)
            },
            ["two branches"] = new CanvasCommand[]
            {
                new BeginConditionalCanvasCommand(SymNode.Source("on")), Fill(shape), new EndConditionalCanvasCommand(),
                new BeginConditionalCanvasCommand(SymNode.Source("!on")), Fill(shape), new EndConditionalCanvasCommand()
            }
        };

        foreach (var (because, inside) in contents)
        {
            Assert.NotEqual(LayerPlan.Fold, Plan(Layer(faded, inside)).Layer(0));
        }

        var dstIn = Fill(shape);
        dstIn.Paint!.BlendMode = SKBlendMode.DstIn;
        Assert.Equal(LayerPlan.Layer, Plan(Layer(faded, dstIn)).Layer(0));
    }


    [Fact]
    public void SaveLayerTracker_Primitive_IsTheLoneShapeOfAFill()
    {
        var tracker = new SaveLayerTracker();
        var shapes = new PathCommand[]
        {
            new AddRectPathCommand(SKRect.Create(1, 2, 3, 4)),
            new AddRoundRectPathCommand(SKRect.Create(1, 2, 3, 4), 1, 1),
            new AddOvalPathCommand(SKRect.Create(1, 2, 3, 4)),
            new AddCirclePathCommand(2, 3, 1)
        };

        foreach (var shape in shapes)
        {
            Assert.Same(shape, tracker.Primitive(Fill(SKPaintStyle.Fill, shape)));
            Assert.Null(tracker.Primitive(Fill(SKPaintStyle.Stroke, shape)));
            Assert.Null(tracker.Primitive(Fill(SKPaintStyle.StrokeAndFill, shape)));
            Assert.Null(tracker.Primitive(Fill(SKPaintStyle.Fill, shape, new ClosePathCommand())));
        }

        Assert.Null(tracker.Primitive(Fill(SKPaintStyle.Fill, new MoveToPathCommand(1, 1))));
    }

    [Fact]
    public void SaveLayerTracker_Primitive_IsNoneWhileALayerIsOpen()
    {
        var tracker = new SaveLayerTracker();
        var rect = Fill(SKPaintStyle.Fill, new AddRectPathCommand(SKRect.Create(1, 2, 3, 4)));

        tracker.Save(isLayer: false);
        Assert.NotNull(tracker.Primitive(rect));

        tracker.Save(isLayer: true);
        tracker.Save(isLayer: false);
        Assert.Null(tracker.Primitive(rect));

        tracker.Restore();
        Assert.Null(tracker.Primitive(rect));

        tracker.Restore();
        Assert.NotNull(tracker.Primitive(rect));
    }

    private static DrawPathCanvasCommand Fill(SKPaintStyle style, params PathCommand[] commands)
    {
        var path = new SKPath();
        foreach (var command in commands)
        {
            path.Add(command);
        }

        return new DrawPathCanvasCommand(path, new SKPaint { Style = style });
    }

    private static DrawPathCanvasCommand Draw(SKBlendMode blendMode = SKBlendMode.SrcOver)
        => new(new SKPath(), new SKPaint { BlendMode = blendMode });

    private static List<CanvasCommand> Layer(SKPaint? paint, params CanvasCommand[] inside)
    {
        var commands = new List<CanvasCommand> { new SaveLayerCanvasCommand(0, paint) };
        commands.AddRange(inside);
        commands.Add(new RestoreCanvasCommand(0));
        return commands;
    }

    private static readonly SKRect s_frame = SKRect.Create(0, 0, 20, 20);

    private static CommandPlan Plan(IList<CanvasCommand> commands) => CommandPlan.For(commands, symbolic: true);

    private static List<CanvasCommand> Clipped(params CanvasCommand[] inside)
    {
        var commands = new List<CanvasCommand> { new SaveCanvasCommand(0) };
        commands.AddRange(inside);
        commands.Add(new RestoreCanvasCommand(0));
        return commands;
    }

    private static ClipRectCanvasCommand ClipRect(SKRect rect) => new(rect, SKClipOperation.Intersect, true);

    private static ClipPathCanvasCommand ClipPath(params PathCommand[] commands)
        => new(new ClipPath { Clips = new List<PathClip> { new() { Path = PathOf(commands) } } }, SKClipOperation.Intersect, true);

    private static SetMatrixCanvasCommand Turn(SKMatrix delta) => new(delta, delta, null, null);

    private static DrawPictureCanvasCommand Picture(params CanvasCommand[] commands)
        => new(new SKPicture(s_frame, commands.ToList()));

    private static DrawPathCanvasCommand Fill(SKRect rect) => new(PathOf(new AddRectPathCommand(rect)), new SKPaint());

    private static DrawPathCanvasCommand Stroke(PathCommand command, float width, SKStrokeJoin join = SKStrokeJoin.Round, SKStrokeCap cap = SKStrokeCap.Butt, float miter = 4)
        => Stroke(new[] { command }, width, join, cap, miter);

    private static DrawPathCanvasCommand Stroke(PathCommand[] commands, float width, SKStrokeJoin join = SKStrokeJoin.Round, SKStrokeCap cap = SKStrokeCap.Butt, float miter = 4)
        => new(PathOf(commands), new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = width, StrokeJoin = join, StrokeCap = cap, StrokeMiter = miter });

    private static SKPath PathOf(params PathCommand[] commands)
    {
        var path = new SKPath();
        foreach (var command in commands)
        {
            path.Add(command);
        }

        return path;
    }

    private static bool Saves(IList<CanvasCommand> commands, int index) => Plan(commands).Layer(index) != LayerPlan.Layer;

    private sealed class TrackingVisitor : ICanvasCommandVisitor
    {
        private readonly IList<string> _visited;

        public TrackingVisitor(IList<string> visited) => _visited = visited;

        public void Visit(ClipPathCanvasCommand cmd) => _visited.Add(nameof(ClipPathCanvasCommand));
        public void Visit(ClipRectCanvasCommand cmd) => _visited.Add(nameof(ClipRectCanvasCommand));
        public void Visit(DrawImageCanvasCommand cmd) => _visited.Add(nameof(DrawImageCanvasCommand));
        public void Visit(DrawPictureCanvasCommand cmd) => _visited.Add(nameof(DrawPictureCanvasCommand));
        public void Visit(DrawPathCanvasCommand cmd) => _visited.Add(nameof(DrawPathCanvasCommand));
        public void Visit(DrawPositionedTextRunCanvasCommand cmd) => _visited.Add(nameof(DrawPositionedTextRunCanvasCommand));
        public void Visit(DrawTextBlobCanvasCommand cmd) => _visited.Add(nameof(DrawTextBlobCanvasCommand));
        public void Visit(DrawTextCanvasCommand cmd) => _visited.Add(nameof(DrawTextCanvasCommand));
        public void Visit(DrawTextOnPathCanvasCommand cmd) => _visited.Add(nameof(DrawTextOnPathCanvasCommand));
        public void Visit(RestoreCanvasCommand cmd) => _visited.Add(nameof(RestoreCanvasCommand));
        public void Visit(SaveCanvasCommand cmd) => _visited.Add(nameof(SaveCanvasCommand));
        public void Visit(SaveLayerCanvasCommand cmd) => _visited.Add(nameof(SaveLayerCanvasCommand));
        public void Visit(SetMatrixCanvasCommand cmd) => _visited.Add(nameof(SetMatrixCanvasCommand));

        public void Visit(BeginConditionalCanvasCommand cmd) => _visited.Add(nameof(BeginConditionalCanvasCommand));

        public void Visit(EndConditionalCanvasCommand cmd) => _visited.Add(nameof(EndConditionalCanvasCommand));
    }
}
