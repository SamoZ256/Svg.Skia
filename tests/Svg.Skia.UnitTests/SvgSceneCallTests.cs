// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Linq;
using ShimSkiaSharp;
using Svg.Model.Services;
using Svg.SceneGraph;
using Xunit;

namespace Svg.Skia.UnitTests;

/// <summary>Where the scene marks a use's content as a call to the drawing it copies.</summary>
public class SvgSceneCallTests
{
    private static CanvasCommand[] Commands(string use)
    {
        var document = SvgService.FromSvg($"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" width="48" height="48">
              <defs>
                <g id="sym-badge"><path d="M 2 2 L 22 2 L 12 21 Z" fill="#ef4444" /></g>
                <clipPath id="badge-clip"><rect width="24" height="24" /></clipPath>
              </defs>
              {use}
            </svg>
            """);
        Assert.NotNull(document);

        var picture = SvgSceneRuntime.CreateModel(document!, new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings())));
        Assert.NotNull(picture);

        return picture!.Commands!.ToArray();
    }

    [Fact]
    public void A_Use_Naming_Its_Drawing_Marks_Exactly_What_It_References()
    {
        var commands = Commands("""
            <use href="#sym-badge" e:draws="Badge" e:arg-on="{{ false }}" e:arg-tint="{{ #0ea5e9 }}"
                 transform="translate(8 4)" clip-path="url(#badge-clip)" />
            """);

        // The use's own place and clip stay the caller's; the range holds what the copy draws.
        Assert.Collection(
            commands,
            command => Assert.IsType<SaveCanvasCommand>(command),
            command => Assert.IsType<SetMatrixCanvasCommand>(command),
            command => Assert.IsType<ClipPathCanvasCommand>(command),
            command =>
            {
                var call = Assert.IsType<BeginCallCanvasCommand>(command);
                Assert.Equal("Badge", call.Target);
                Assert.Equal(new[] { "on=false", "tint=#0ea5e9" }, call.Arguments.Select(argument => $"{argument.Key}={((SymSource)argument.Value).Text}").OrderBy(text => text));
            },
            command => Assert.IsType<DrawPathCanvasCommand>(command),
            command => Assert.IsType<EndCallCanvasCommand>(command),
            command => Assert.IsType<RestoreCanvasCommand>(command));
    }

    [Theory]
    [InlineData("""<use href="#sym-badge" transform="translate(8 4)" />""")]
    [InlineData("""<use href="#sym-gone" e:draws="Badge" transform="translate(8 4)" />""")]
    [InlineData("""<use href="#sym-badge" e:draws="Badge" e:arg-on="false" transform="translate(8 4)" />""")]
    public void A_Use_Without_A_Claim_That_Holds_Marks_Nothing(string use)
        => Assert.DoesNotContain(Commands(use), command => command is BeginCallCanvasCommand or EndCallCanvasCommand);
}
