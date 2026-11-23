using Xunit;

namespace Svg.Studio.UiTests;

/// <summary>The menu bar macOS builds from Studio's NativeMenu, which the headless tests cannot see.</summary>
public class MenuTests
{
    [Fact]
    public void Settings_In_The_Application_Menu_Opens_The_Settings()
    {
        using var studio = Studio.Launch();

        studio.Find("WelcomeOpen");
        studio.Command(",");

        studio.Window("Settings");
    }

    [Fact]
    public void An_Edit_Names_Itself_In_The_Edit_Menu()
    {
        using var studio = Studio.Launch();
        var path = studio.Write("square.svg", Drawings.Square);

        studio.Relaunch(path);

        studio.Click("PART_Canvas");
        studio.Menu("Edit", "Delete");

        studio.Until(() => studio.Title == "• square.svg — SVG Studio", "the mark of an unsaved edit");
        Assert.Contains("Undo delete an element", studio.MenuItems("Edit"));
    }

    [Fact]
    public void The_Menu_Bar_Holds_Studios_Menus()
    {
        using var studio = Studio.Launch();

        studio.Find("WelcomeOpen");

        foreach (var menu in new[] { "File", "Edit", "View", "Window", "Project", "Help" })
        {
            Assert.NotEmpty(studio.MenuItems(menu));
        }
    }

    /// <remarks>The menu answers ⌘S on macOS now: the window no longer looks for the key itself there.</remarks>
    [Fact]
    public void Command_S_Saves_Through_The_Menu()
    {
        using var studio = Studio.Launch();
        var path = studio.Write("square.svg", Drawings.Square);

        studio.Relaunch(path);

        studio.Click("PART_Canvas");
        studio.Menu("Edit", "Delete");
        studio.Until(() => studio.Title == "• square.svg — SVG Studio", "the mark of an unsaved edit");

        studio.Command("s");

        studio.Until(() => studio.Title == "square.svg — SVG Studio", "the mark gone once it is saved");
    }
}
