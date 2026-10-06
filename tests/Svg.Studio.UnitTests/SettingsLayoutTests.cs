using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>How the settings are laid out: in tabs, in a window that scrolls rather than grows.</summary>
/// <remarks>In the settings collection because showing the window reads <see cref="StudioSettings"/>.</remarks>
[Collection("settings")]
public class SettingsLayoutTests
{
    [AvaloniaFact]
    public void The_Settings_Are_On_Four_Tabs_Each_Of_Which_Scrolls()
    {
        var settings = new SettingsWindow();

        settings.Show();
        Dispatcher.UIThread.RunJobs();

        var tabs = settings.Tabs.Items.OfType<TabItem>().ToList();

        Assert.Equal(new[] { "General", "Canvas", "Import & export", "Accounts" }, tabs.Select(tab => (string)tab.Header!));
        Assert.All(tabs, tab => Assert.IsType<ScrollViewer>(tab.Content));

        // Every control a test or the code reaches is on one of them, not left outside the tabs.
        Control[] reached =
        [
            settings.Theme, settings.Autosave, settings.ResetLayout,
            settings.SnapToGrid, settings.GridSize, settings.RotationStep, settings.CaptionSize, settings.DrawingCaptions,
            settings.RelaxedText, settings.ConvertAsks, settings.ConvertIntegers, settings.ConvertOrganizes,
            settings.StreamlineKey, settings.AnthropicKey, settings.McpEnabled, settings.McpPort, settings.CopyMcpCommand
        ];

        Assert.All(reached, control => Assert.Contains(control.GetLogicalAncestors().OfType<TabItem>(), tabs.Contains));

        settings.Close();
    }

    [AvaloniaFact]
    public void The_Window_Keeps_Its_Size_Whatever_A_Tab_Holds()
    {
        var settings = new SettingsWindow();

        settings.Show();
        Dispatcher.UIThread.RunJobs();

        var height = settings.Bounds.Height;

        foreach (var tab in settings.Tabs.Items.OfType<TabItem>())
        {
            settings.Tabs.SelectedItem = tab;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(height, settings.Bounds.Height);
        }

        Assert.Equal(SizeToContent.Manual, settings.SizeToContent);
        Assert.True(settings.CanResize);

        settings.Close();
    }
}
