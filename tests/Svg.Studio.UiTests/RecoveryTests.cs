using System.IO;
using System.Linq;
using Xunit;

namespace Svg.Studio.UiTests;

/// <summary>The copy autosave keeps, which only a process that died without closing leaves behind.</summary>
public class RecoveryTests
{
    [Fact]
    public void Work_Lost_To_A_Crash_Is_Offered_Back_On_The_Next_Open()
    {
        using var studio = Studio.Launch();
        var project = studio.Package("icons.svgstudio", ("home", Drawings.Square));
        var recovery = Path.Combine(studio.Data, "recovery");

        studio.Relaunch(project);

        // A project edit: a drawing's own canvas edits only its tab, which recovery does not keep. The
        // row's context menu would do it too, but an Avalonia popup stalls XCUITest; ⌘ selects the row
        // without opening it, and the menu bar's Delete removes what the tree has selected.
        studio.CommandClick(studio.Row("home"));
        studio.Menu("Edit", "Delete");
        studio.Until(() => studio.Title.StartsWith("• "), "the mark of an unsaved edit");

        // One edit is written after ProjectRecovery's half-minute patience.
        studio.Until(() => Directory.Exists(recovery) && Directory.EnumerateFiles(recovery, "icons-*.zip").Any(), "the recovery copy", 60d);

        studio.Kill();
        studio.Relaunch(project);

        studio.Window("Recovered work");
        studio.Click(studio.Button("Restore"));

        studio.Until(() => studio.Title == "• Project — icons.svgstudio — SVG Studio", "the restored work's title");
        Assert.False(studio.Says("home"));
    }
}
