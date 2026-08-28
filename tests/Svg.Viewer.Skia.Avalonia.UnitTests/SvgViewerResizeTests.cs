using System;
using Svg.Skia;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

/// <summary>
/// The dialog's arithmetic: a width, a height and a scale that are one size seen three ways while
/// the ratio is locked, and two sizes with no scale at all once it is not.
/// </summary>
public class SvgViewerResizeTests
{
    private static SvgViewerResize Drawing() => new(200f, 100f);

    [Fact]
    public void A_Fresh_Resize_Is_The_Size_The_Drawing_Already_Has()
    {
        var resize = Drawing();

        Assert.Equal(200f, resize.Width);
        Assert.Equal(100f, resize.Height);
        Assert.Equal(1f, resize.Scale);
        Assert.True(resize.IsAspectRatioLocked);

        // Nothing to ask for, so nothing is asked.
        Assert.True(resize.ToRequest().IsEmpty);
    }

    [Fact]
    public void A_Locked_Width_Carries_The_Height_And_The_Scale()
    {
        var resize = Drawing();

        resize.SetWidth(400f);

        Assert.Equal(200f, resize.Height);
        Assert.Equal(2f, resize.Scale);
    }

    [Fact]
    public void A_Locked_Height_Carries_The_Width()
    {
        var resize = Drawing();

        resize.SetHeight(50f);

        Assert.Equal(100f, resize.Width);
        Assert.Equal(0.5f, resize.Scale);
    }

    [Fact]
    public void A_Scale_Sets_Both()
    {
        var resize = Drawing();

        resize.SetScale(1.5f);

        Assert.Equal(300f, resize.Width);
        Assert.Equal(150f, resize.Height);
    }

    [Fact]
    public void Unlocked_The_Two_Dimensions_Are_Free_Of_Each_Other()
    {
        var resize = Drawing();

        resize.IsAspectRatioLocked = false;
        resize.SetWidth(400f);

        Assert.Equal(100f, resize.Height);

        resize.SetHeight(400f);

        Assert.Equal(400f, resize.Width);
    }

    [Fact]
    public void Unlocked_A_Scale_Means_Nothing_And_Says_So()
    {
        var resize = Drawing();

        resize.IsAspectRatioLocked = false;

        var refusal = Assert.Throws<InvalidOperationException>(() => resize.SetScale(2f));

        Assert.Contains("one factor for both axes", refusal.Message);
    }

    [Fact]
    public void Locking_Again_Puts_The_Height_Back_On_The_Ratio()
    {
        var resize = Drawing();

        resize.IsAspectRatioLocked = false;
        resize.SetHeight(400f);
        resize.IsAspectRatioLocked = true;

        // The width is the answer and the height follows it, rather than a shape the lock forbids.
        Assert.Equal(200f, resize.Width);
        Assert.Equal(100f, resize.Height);
    }

    [Fact]
    public void A_Locked_Request_Gives_The_Width_Alone()
    {
        var resize = Drawing();

        resize.SetScale(2f);

        var request = resize.ToRequest();

        // Not a scale: the model refuses a scale beside a width, and one dimension already derives
        // the other there as it does here.
        Assert.Equal(400f, request.Width);
        Assert.Null(request.Height);
        Assert.Null(request.Scale);
    }

    [Fact]
    public void An_Unlocked_Request_Gives_A_Box()
    {
        var resize = Drawing();

        resize.IsAspectRatioLocked = false;
        resize.SetWidth(400f);
        resize.SetHeight(400f);

        var request = resize.ToRequest();

        Assert.Equal(400f, request.Width);
        Assert.Equal(400f, request.Height);
        Assert.Null(request.Scale);
    }

    [Fact]
    public void A_Size_Has_To_Be_A_Positive_Number()
    {
        var resize = Drawing();

        Assert.Throws<ArgumentException>(() => resize.SetWidth(0f));
        Assert.Throws<ArgumentException>(() => resize.SetHeight(-1f));
        Assert.Throws<ArgumentException>(() => resize.SetScale(float.NaN));
        Assert.Throws<ArgumentException>(() => new SvgViewerResize(0f, 100f));
    }
}
