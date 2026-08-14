using System;
using System.Collections.Generic;
using ShimSkiaSharp;
using Svg.Model;
using Svg.Skia.UnitTests.Common;
using Xunit;

namespace Svg.Skia.UnitTests;

public class SvgSceneSizingTests : SvgUnitTest
{
    // A 20x20 square inset by 2 in a 24x24 document: every dimension of the result is a round
    // number at the scales the tests use, so a wrong one is obvious rather than approximate.
    private const string Square =
        """<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24"><rect x="2" y="2" width="20" height="20" fill="red"/></svg>""";

    private const string Tall =
        """<svg xmlns="http://www.w3.org/2000/svg" width="24" height="48" viewBox="0 0 24 48"><rect x="2" y="2" width="20" height="44" fill="red"/></svg>""";

    private const string NoViewBox =
        """<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24"><rect x="2" y="2" width="20" height="20" fill="red"/></svg>""";

    private const string NoSizeAtAll =
        """<svg xmlns="http://www.w3.org/2000/svg"><rect x="2" y="2" width="20" height="20" fill="red"/></svg>""";

    private const string Nothing =
        """<svg xmlns="http://www.w3.org/2000/svg"></svg>""";

    private static ISvgAssetLoader Loader() => new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings()));

    private static SKPicture Compile(string svg, SvgSizeRequest size)
    {
        var document = Svg.Model.Services.SvgService.FromSvg(svg);
        Assert.NotNull(document);

        var assetLoader = Loader();
        SvgSceneSizing.Apply(document!, assetLoader, size);

        var picture = SvgSceneRuntime.CreateModel(document!, assetLoader);
        Assert.NotNull(picture);

        return picture!;
    }

    /// <summary>The bounds of the first path the picture draws, wherever it is nested.</summary>
    private static SKRect FirstPathBounds(SKPicture picture)
    {
        var bounds = Find(picture);
        Assert.NotNull(bounds);

        return bounds!.Value;

        static SKRect? Find(SKPicture? picture)
        {
            foreach (var command in picture?.Commands ?? new List<CanvasCommand>())
            {
                switch (command)
                {
                    case DrawPathCanvasCommand { Path: { } path }:
                        return path.Bounds;
                    case DrawPictureCanvasCommand { Picture: { } nested } when Find(nested) is { } found:
                        return found;
                }
            }

            return null;
        }
    }

    [Fact]
    public void An_Empty_Request_Changes_Nothing()
    {
        var picture = Compile(Square, SvgSizeRequest.None);

        Assert.Equal(SKRect.Create(0f, 0f, 24f, 24f), picture.CullRect);
        Assert.Equal(SKRect.Create(2f, 2f, 20f, 20f), FirstPathBounds(picture));
    }

    [Fact]
    public void A_Width_And_A_Height_Are_Taken_As_Given()
    {
        var picture = Compile(Square, new SvgSizeRequest(96f, 96f, null));

        Assert.Equal(SKRect.Create(0f, 0f, 96f, 96f), picture.CullRect);
        Assert.Equal(SKRect.Create(8f, 8f, 80f, 80f), FirstPathBounds(picture));
    }

    [Fact]
    public void A_Width_Alone_Derives_The_Height()
    {
        var picture = Compile(Tall, new SvgSizeRequest(48f, null, null));

        Assert.Equal(SKRect.Create(0f, 0f, 48f, 96f), picture.CullRect);
    }

    [Fact]
    public void A_Height_Alone_Derives_The_Width()
    {
        var picture = Compile(Tall, new SvgSizeRequest(null, 96f, null));

        Assert.Equal(SKRect.Create(0f, 0f, 48f, 96f), picture.CullRect);
    }

    [Fact]
    public void A_Scale_Multiplies_The_Size_The_Document_Already_Has()
    {
        var picture = Compile(Square, new SvgSizeRequest(null, null, 2f));

        Assert.Equal(SKRect.Create(0f, 0f, 48f, 48f), picture.CullRect);
        Assert.Equal(SKRect.Create(4f, 4f, 40f, 40f), FirstPathBounds(picture));
    }

    [Fact]
    public void A_Mismatched_Pair_Letterboxes_Rather_Than_Stretching()
    {
        // 96x48 asked of a square drawing. preserveAspectRatio centres it in the box: the square
        // stays square at 40x40, rather than being pulled into a 80x40 rectangle.
        var picture = Compile(Square, new SvgSizeRequest(96f, 48f, null));

        Assert.Equal(SKRect.Create(0f, 0f, 96f, 48f), picture.CullRect);
        Assert.Equal(SKRect.Create(28f, 4f, 40f, 40f), FirstPathBounds(picture));
    }

    [Fact]
    public void A_Document_Without_A_ViewBox_Is_Resized_Rather_Than_Reframed()
    {
        // Width and height alone are only a viewport: without the synthesized viewBox the picture
        // would be 96x96 with the square still sitting at 2,2 in the corner.
        var picture = Compile(NoViewBox, new SvgSizeRequest(96f, null, null));

        Assert.Equal(SKRect.Create(0f, 0f, 96f, 96f), picture.CullRect);
        Assert.Equal(SKRect.Create(8f, 8f, 80f, 80f), FirstPathBounds(picture));
    }

    [Fact]
    public void A_Document_With_No_Size_At_All_Is_Measured_By_What_It_Draws()
    {
        // Nothing to read a size from, so the natural one is what the drawing covers: 22x22,
        // measured by compiling it once. 88 is four times that.
        var picture = Compile(NoSizeAtAll, new SvgSizeRequest(88f, null, null));

        Assert.Equal(SKRect.Create(0f, 0f, 88f, 88f), picture.CullRect);
        Assert.Equal(SKRect.Create(8f, 8f, 80f, 80f), FirstPathBounds(picture));
    }

    [Fact]
    public void A_Document_That_Draws_Nothing_Still_Gets_The_Size_It_Was_Asked_For()
    {
        // Nothing to measure and nothing to scale, so the result is an empty picture of the
        // requested size rather than a failure.
        var picture = Compile(Nothing, new SvgSizeRequest(96f, null, null));

        Assert.Equal(SKRect.Create(0f, 0f, 96f, 96f), picture.CullRect);
    }

    [Fact]
    public void A_Size_Of_Nothing_Cannot_Be_Resized()
    {
        var error = Assert.Throws<ArgumentException>(
            () => new SvgSizeRequest(96f, null, null).Resolve(SKSize.Empty));

        Assert.Contains("no size to resize from", error.Message);
    }

    [Fact]
    public void Text_Is_Placed_Under_A_Transform_Rather_Than_Restated_At_The_New_Size()
    {
        // Worth pinning down, because it is the one place a resized document does not differ from
        // a scale wrapped around the finished picture: a subtree the compiler cannot fold into
        // geometry keeps its own coordinates and is drawn under a matrix. The drawing is right
        // either way; what a resize adds is the fitting, not a re-layout of the text.
        const string text =
            """<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24"><text x="2" y="20" font-family="Noto Sans" font-size="10">A</text></svg>""";

        var picture = Compile(text, new SvgSizeRequest(96f, null, null));

        Assert.Equal(SKRect.Create(0f, 0f, 96f, 96f), picture.CullRect);

        var matrix = Assert.Single(picture.Commands!, command => command is SetMatrixCanvasCommand);
        var scale = ((SetMatrixCanvasCommand)matrix).TotalMatrix;
        Assert.Equal(4f, scale.ScaleX);
        Assert.Equal(4f, scale.ScaleY);
    }

    [Theory]
    [InlineData(96f, null, 2f)]
    [InlineData(null, 96f, 2f)]
    [InlineData(0f, null, null)]
    [InlineData(-5f, null, null)]
    [InlineData(null, 0f, null)]
    [InlineData(null, null, 0f)]
    [InlineData(null, null, -1f)]
    [InlineData(float.NaN, null, null)]
    [InlineData(float.PositiveInfinity, null, null)]
    public void A_Request_That_Cannot_Mean_Anything_Is_Rejected(float? width, float? height, float? scale)
        => Assert.Throws<ArgumentException>(() => new SvgSizeRequest(width, height, scale));
}
