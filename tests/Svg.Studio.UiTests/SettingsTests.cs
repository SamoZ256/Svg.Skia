using System.IO;
using Xunit;

namespace Svg.Studio.UiTests;

public class SettingsTests
{
    /// <summary>The choice is written the moment it is made, and the next launch paints with it.</summary>
    [Fact]
    public void A_Theme_Picked_In_Settings_Survives_A_Relaunch()
    {
        using var studio = Studio.Launch();
        var settings = Path.Combine(studio.Data, "settings");

        studio.Find("WelcomeOpen");
        studio.Command(",");
        studio.Window("Settings");
        studio.Click("ThemeBox");
        studio.Click(studio.Labelled("Dark"));

        studio.Until(() => File.Exists(settings) && File.ReadAllText(settings).Contains("theme=dark"), "theme=dark in the settings file");

        studio.Relaunch();
        studio.Find("WelcomeOpen");
        studio.Command(",");
        studio.Window("Settings");

        studio.Until(() => studio.Find("ThemeBox").GetAttribute("value") == "Dark", "the theme box reading Dark");
    }
}
