using System.Diagnostics;
using Xunit;

namespace Svg.Studio.UiTests;

/// <summary>Finder's double-click, which macOS hands a running Studio as an event rather than on the command line.</summary>
public class FileActivationTests
{
    [Fact]
    public void A_Project_Opened_From_Finder_Reaches_The_Running_Studio()
    {
        using var studio = Studio.Launch();
        var project = studio.Package("icons.svgstudio", ("home", Drawings.Square));

        studio.Find("WelcomeOpen");

        // What Finder does with a package whose type Studio owns, as Info.plist declares it. By the
        // bundle's path, as Appium launched it: by its identifier, any other copy registered could take it.
        using (var open = Process.Start("/usr/bin/open", ["-a", Studio.App!, project]))
        {
            open.WaitForExit();
            Assert.Equal(0, open.ExitCode);
        }

        studio.Until(() => studio.Title == "Project — icons.svgstudio — SVG Studio", "the project's title");
    }
}
