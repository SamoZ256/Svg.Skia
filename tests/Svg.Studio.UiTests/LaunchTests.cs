using Xunit;

namespace Svg.Studio.UiTests;

public class LaunchTests
{
    [Fact]
    public void A_Cold_Launch_Opens_On_The_Welcome_Screen()
    {
        using var studio = Studio.Launch();

        studio.Find("WelcomeOpen");
        studio.Find("WelcomeNew");

        Assert.Equal("SVG Studio", studio.Title);
    }

    [Fact]
    public void A_Path_On_The_Command_Line_Opens_That_Drawing()
    {
        using var studio = Studio.Launch();
        var path = studio.Write("square.svg", Drawings.Square);

        studio.Relaunch(path);

        studio.Until(() => studio.Title == "square.svg — SVG Studio", "the drawing's title");
        Assert.False(studio.Shows("WelcomeOpen"));
    }
}
