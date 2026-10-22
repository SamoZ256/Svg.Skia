using Xunit;

namespace Svg.Studio.UiTests;

/// <summary>⌘Q, which goes through the application rather than a window's close button.</summary>
public class QuitTests
{
    [Fact]
    public void Quitting_With_An_Unsaved_Edit_Asks_And_Keep_Editing_Stays()
    {
        using var studio = Studio.Launch();
        var path = studio.Write("square.svg", Drawings.Square);

        studio.Relaunch(path);
        studio.Click("PART_Canvas");
        studio.Menu("Edit", "Delete");
        studio.Until(() => studio.Title.StartsWith("• "), "the mark of an unsaved edit");

        studio.Command("q");
        studio.Window("Unsaved changes");
        studio.Click(studio.Button("Keep editing"));

        studio.Until(() => studio.Windows.Count == 1, "the prompt closing");
        Assert.Equal("• square.svg — SVG Studio", studio.Title);
    }
}
