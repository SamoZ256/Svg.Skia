using System.IO;
using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using Xunit;

namespace Svg.Studio.UiTests;

/// <summary>
/// The native open and save panels, chosen over Avalonia's managed one for what macOS adds to them.
/// </summary>
/// <remarks>
/// AppKit draws them out of process, but they answer within Studio's own tree. ⇧⌘G's "Go to folder"
/// field often is not in it at all, so it is typed into blind and the result is what is checked.
/// </remarks>
public class FilePanelTests
{
    [Fact]
    public void File_Open_Opens_The_Drawing_Picked_In_The_Panel()
    {
        using var studio = Studio.Launch();
        var path = studio.Write("square.svg", Drawings.Square);

        studio.Click("WelcomeOpen");
        studio.Labelled("Open drawing or project");

        studio.Command("g", shift: true);
        studio.Press(path);
        // Return goes to the file and selects it; a second Return did not open it on macos-26.
        studio.Press(Keys.Return);
        studio.Click(studio.Enabled("OKButton"));

        studio.Until(() => studio.Title == "square.svg — SVG Studio", "the picked drawing's title");
    }

    [Fact]
    public void Save_As_Writes_Where_The_Panel_Was_Pointed()
    {
        using var studio = Studio.Launch();
        var path = studio.Write("square.svg", Drawings.Square);
        var copy = Path.Combine(studio.Data, "copy.svg");

        studio.Relaunch(path);
        studio.Until(() => studio.Title == "square.svg — SVG Studio", "the drawing's title");
        studio.Menu("File", "Save As…");

        studio.Until(() => studio.Driver.FindElements(MobileBy.AccessibilityId("saveAsNameTextField")).Count > 0, "the save panel's name field");
        studio.Command("a");
        studio.Press("copy.svg");

        studio.Command("g", shift: true);
        studio.Press(studio.Data);
        studio.Press(Keys.Return);
        studio.Click(studio.Enabled("OKButton"));

        studio.Until(() => File.Exists(copy), "copy.svg on disk");
        studio.Until(() => studio.Title == "copy.svg — SVG Studio", "the copy's title");
    }
}
