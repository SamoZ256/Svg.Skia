using System;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// Picking a theme: what is written, and that the application is repainted as it is picked.
/// </summary>
/// <remarks>
/// Both the setting and the variant are global and this suite runs in parallel, so the pair of them
/// are put back afterwards the way <see cref="StudioSettingsTests"/> puts the store back. Without
/// that, a run of this leaves every later test looking at whichever theme it finished on.
/// </remarks>
public class SettingsThemeTests : IDisposable
{
    private readonly StudioTheme _was = StudioSettings.Theme;
    private readonly ThemeVariant? _wasVariant = Application.Current?.RequestedThemeVariant;

    public void Dispose()
    {
        StudioSettings.Theme = _was;

        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = _wasVariant;
        }
    }

    /// <summary>The window shows the theme on file, and writes and applies the one that is picked.</summary>
    /// <remarks>
    /// Applied here rather than once the window closes, which is what every other setting on it
    /// leaves to the host: this window is shown modally, so a theme that waited for it to close
    /// would be one nobody could see themselves picking.
    /// </remarks>
    [AvaloniaFact]
    public void The_Theme_Is_Shown_Written_And_Painted_Where_It_Is_Set()
    {
        StudioSettings.Theme = StudioTheme.Dark;

        var settings = new SettingsWindow();

        settings.Show();
        Dispatcher.UIThread.RunJobs();

        // "Dark", which is third: following the machine is first, being the one it starts on.
        Assert.Equal(2, settings.Theme.SelectedIndex);

        settings.Theme.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(StudioTheme.Light, StudioSettings.Theme);
        Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);

        // And back to the machine's own answer, which is a variant rather than an absence of one.
        settings.Theme.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(StudioTheme.System, StudioSettings.Theme);
        Assert.Equal(ThemeVariant.Default, Application.Current.RequestedThemeVariant);

        settings.Close();
    }

    /// <summary>
    /// A window stands on the editor's own ground rather than on Fluent's.
    /// </summary>
    /// <remarks>
    /// Fluent hands every window <c>SystemControlBackgroundAltHighBrush</c>, which is literally
    /// <c>Black</c> in the dark variant. Every pane in the editor is transparent over its window, so
    /// that one brush was the whole of what a dark Studio looked like: flat black, with the canvas
    /// floating in the middle of it a shade lighter than the chrome around it.
    ///
    /// The exact colour, because it is half of a relation rather than a free choice: the chrome
    /// sits above the canvas's own ground in dark and below it in light, so the drawing surface
    /// reads as sunk into the editor either way round. The other half is asserted in the viewer's
    /// suite, on a canvas in a window and in pixels.
    /// </remarks>
    [AvaloniaTheory]
    [InlineData("Dark", 0x25, 0x25, 0x28)]
    [InlineData("Light", 0xFF, 0xFF, 0xFF)]
    public void A_Window_Stands_On_The_Editors_Own_Ground(string variant, byte red, byte green, byte blue)
    {
        Application.Current!.RequestedThemeVariant =
            variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        var window = new SettingsWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var painted = Assert.IsAssignableFrom<ISolidColorBrush>(window.Background);

        Assert.Equal(Color.FromRgb(red, green, blue), painted.Color);

        window.Close();
    }

    /// <summary>A second window reads the file rather than anything the first one is holding.</summary>
    [AvaloniaFact]
    public void The_Theme_Survives_A_Restart()
    {
        StudioSettings.Theme = StudioTheme.Light;

        var settings = new SettingsWindow();

        settings.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, settings.Theme.SelectedIndex);

        settings.Close();
    }
}
