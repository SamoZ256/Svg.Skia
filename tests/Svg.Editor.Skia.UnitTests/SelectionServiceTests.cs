using Svg.Editor.Skia;
using Svg.Skia;
using Xunit;
using Shim = ShimSkiaSharp;
using SK = SkiaSharp;

namespace Svg.Editor.Skia.UnitTests;

public class SelectionServiceTests
{
    [Fact]
    public void SnapAndContainsRect_RespectGridAndBounds()
    {
        var service = new SelectionService
        {
            SnapToGrid = true,
            GridSize = 10
        };

        Assert.Equal(20, service.Snap(17));
        Assert.True(SelectionService.ContainsRect(new SK.SKRect(0, 0, 100, 100), new SK.SKRect(10, 10, 90, 90)));
        Assert.False(SelectionService.ContainsRect(new SK.SKRect(0, 0, 10, 10), new SK.SKRect(-1, -1, 5, 5)));
    }

    /// <summary>
    /// The box a rectangle is given, and where its stalk stands.
    /// </summary>
    /// <remarks>
    /// The arithmetic a selection of several elements gets its one box from: no matrix, because the
    /// only frame several elements agree on is the upright one. The stalk is twenty screen pixels
    /// out from the top middle, so at twice the scale it is ten units up in the drawing.
    /// </remarks>
    [Fact]
    public void GetBoundsInfo_PutsTheStalkAboveAnUprightRectangle()
    {
        var service = new SelectionService();
        var box = service.GetBoundsInfo(
            new Shim.SKRect(20f, 20f, 80f, 80f),
            Shim.SKMatrix.CreateIdentity(),
            () => 2f);

        Assert.Equal(new SK.SKPoint(50f, 20f), box.TopMid);
        Assert.Equal(new SK.SKPoint(50f, 50f), box.Center);
        Assert.Equal(new SK.SKPoint(50f, 10f), box.RotHandle);
    }

    /// <summary>Several boxes span one rectangle, which is what a selection of several is given.</summary>
    [Fact]
    public void GetBoundsRect_SpansEveryBox()
    {
        var service = new SelectionService();
        var identity = Shim.SKMatrix.CreateIdentity();

        var boxes = new[]
        {
            service.GetBoundsInfo(new Shim.SKRect(20f, 20f, 40f, 40f), identity, () => 1f),
            service.GetBoundsInfo(new Shim.SKRect(60f, 60f, 80f, 80f), identity, () => 1f)
        };

        Assert.Equal(new SK.SKRect(20f, 20f, 80f, 80f), SelectionService.GetBoundsRect(boxes));
        Assert.Equal(SK.SKRect.Empty, SelectionService.GetBoundsRect(System.Array.Empty<BoundsInfo>()));
    }

    [Fact]
    public void HitHandle_FindsResizeAndRotationHandles()
    {
        var service = new SelectionService();
        var bounds = new BoundsInfo(
            new SK.SKPoint(0, 0),
            new SK.SKPoint(100, 0),
            new SK.SKPoint(100, 100),
            new SK.SKPoint(0, 100),
            new SK.SKPoint(50, 0),
            new SK.SKPoint(100, 50),
            new SK.SKPoint(50, 100),
            new SK.SKPoint(0, 50),
            new SK.SKPoint(50, 50),
            new SK.SKPoint(50, -20));

        var topLeft = service.HitHandle(bounds, new SK.SKPoint(0, 0), 1f, out var center);
        var rotate = service.HitHandle(bounds, new SK.SKPoint(50, -20), 1f, out _);

        Assert.Equal(new SK.SKPoint(50, 50), center);
        Assert.Equal(0, topLeft);
        Assert.Equal(8, rotate);
    }

    [Fact]
    public void GetBoundsInfo_PrefersRetainedSceneNodeGeometry()
    {
        const string svgMarkup = "<svg width=\"64\" height=\"64\"><g transform=\"translate(10,20)\"><rect id=\"target\" x=\"1\" y=\"2\" width=\"10\" height=\"6\" /></g></svg>";

        using var svg = new SKSvg();
        svg.FromSvg(svgMarkup);

        Assert.True(svg.TryGetRetainedSceneNodeById("target", out var sceneNode));
        Assert.NotNull(sceneNode);

        var service = new SelectionService();
        var bounds = service.GetBoundsInfo(sceneNode!, () => 1f);

        Assert.Equal(new SK.SKPoint(11f, 22f), bounds.TL);
        Assert.Equal(new SK.SKPoint(21f, 22f), bounds.TR);
        Assert.Equal(new SK.SKPoint(21f, 28f), bounds.BR);
        Assert.Equal(new SK.SKPoint(11f, 28f), bounds.BL);
        Assert.Equal(new SK.SKPoint(16f, 25f), bounds.Center);
    }
}
