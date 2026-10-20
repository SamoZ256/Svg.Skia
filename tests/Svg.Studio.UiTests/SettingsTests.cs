using System.IO;
using Xunit;

namespace Svg.Studio.UiTests;

public class SettingsTests
{
    /// <summary>A setting is written the moment it changes, and read back by the next launch.</summary>
    /// <remarks>
    /// A checkbox rather than the theme box: while an Avalonia popup is open, XCUITest waits on every
    /// event for an idle that does not come, 65 s for one click on hosted macos-26, and the item still
    /// was not picked.
    /// </remarks>
    [Fact]
    public void A_Setting_Changed_In_Settings_Survives_A_Relaunch()
    {
        using var studio = Studio.Launch();
        var settings = Path.Combine(studio.Data, "settings");

        studio.Find("WelcomeOpen");
        studio.Command(",");
        studio.Window("Settings");
        studio.Until(() => studio.Find("AutosaveBox").GetAttribute("value") == "1", "autosave on, as it starts");
        studio.Click("AutosaveBox");

        studio.Until(() => File.Exists(settings) && File.ReadAllText(settings).Contains("autosave=off"), "autosave=off in the settings file");

        studio.Relaunch();
        studio.Find("WelcomeOpen");
        studio.Command(",");
        studio.Window("Settings");

        studio.Until(() => studio.Find("AutosaveBox").GetAttribute("value") == "0", "autosave still off");
    }
}
