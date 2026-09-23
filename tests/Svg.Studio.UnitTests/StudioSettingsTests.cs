using System;
using System.IO;
using Avalonia.Styling;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// The settings the editor keeps between sessions, and the rules that decide them: the copy is on
/// unless the file says otherwise, and a size nobody can use is the one nobody set.
/// </summary>
[Collection("settings")]
public class StudioSettingsTests : IDisposable
{
    private readonly string _was = StudioSettings.Store;
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public StudioSettingsTests() => StudioSettings.Store = Path.Combine(_directory, "settings");

    public void Dispose()
    {
        StudioSettings.Store = _was;

        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Autosave_Is_On_When_Nothing_Has_Been_Written() => Assert.True(StudioSettings.Autosave);

    [Fact]
    public void Autosave_Is_On_When_The_Store_Cannot_Be_Read()
    {
        // A directory where the file should be, which is the shape of every way reading can fail.
        StudioSettings.Store = _directory;

        Assert.True(StudioSettings.Autosave);
    }

    [Fact]
    public void Switching_It_Off_And_On_Round_Trips()
    {
        StudioSettings.Autosave = false;

        Assert.False(StudioSettings.Autosave);

        // Read through rather than remembered, so a second window sees what the first one wrote.
        Assert.Contains("autosave=off", File.ReadAllText(StudioSettings.Store), StringComparison.Ordinal);

        StudioSettings.Autosave = true;

        Assert.True(StudioSettings.Autosave);
    }

    [Fact]
    public void A_Setting_Nobody_Can_Read_Is_The_Setting_Nobody_Wrote()
    {
        File.WriteAllText(StudioSettings.Store, "autosave=maybe\n");

        Assert.True(StudioSettings.Autosave);
    }

    [Fact]
    public void A_Caption_Size_Nobody_Has_Set_Is_The_Canvas_Default()
    {
        Assert.Equal(SvgViewerCanvas.DefaultCaptionSize, StudioSettings.CaptionSize);
    }

    [Fact]
    public void A_Caption_Size_Survives_Being_Written()
    {
        StudioSettings.CaptionSize = 17d;

        Assert.Equal(17d, StudioSettings.CaptionSize);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("0")]
    [InlineData("999")]
    public void A_Caption_Size_Nobody_Can_Use_Is_The_Default(string written)
    {
        // A settings file is not something to fail over: anything the canvas would not take comes
        // back as what it was before somebody edited the file by hand.
        File.WriteAllText(StudioSettings.Store, $"captionSize={written}");

        Assert.Equal(SvgViewerCanvas.DefaultCaptionSize, StudioSettings.CaptionSize);
    }

    [Fact]
    public void The_Theme_Nobody_Has_Set_Is_The_Machines()
    {
        // The one answer that goes on being right: somebody whose machine turns dark at sunset has
        // said what they want once.
        Assert.Equal(StudioTheme.System, StudioSettings.Theme);
        Assert.Equal(ThemeVariant.Default, StudioSettings.Variant);
    }

    [Theory]
    [InlineData(StudioTheme.System, "system")]
    [InlineData(StudioTheme.Light, "light")]
    [InlineData(StudioTheme.Dark, "dark")]
    public void A_Theme_Survives_Being_Written(StudioTheme theme, string written)
    {
        StudioSettings.Theme = theme;

        Assert.Equal(theme, StudioSettings.Theme);
        Assert.Contains($"theme={written}", File.ReadAllText(StudioSettings.Store), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StudioTheme.System, "Default")]
    [InlineData(StudioTheme.Light, "Light")]
    [InlineData(StudioTheme.Dark, "Dark")]
    public void A_Theme_Is_The_Variant_Avalonia_Spells_It(StudioTheme theme, string variant)
    {
        StudioSettings.Theme = theme;

        // Asserted here rather than where it is assigned, because where it is assigned is App —
        // the one class the suite cannot reach, since it builds an Application of its own.
        Assert.Equal(variant, StudioSettings.Variant.Key);
    }

    [Fact]
    public void A_Theme_Nobody_Can_Read_Is_The_Theme_Nobody_Wrote()
    {
        File.WriteAllText(StudioSettings.Store, "theme=chartreuse\n");

        Assert.Equal(StudioTheme.System, StudioSettings.Theme);
    }

    [Fact]
    public void Snapping_Is_Off_Until_Somebody_Asks_For_It()
    {
        // The other way round from the recovery copy, and for the opposite reason: a drag that does
        // not write what was dragged is a surprise, so only the line the toggle writes turns it on.
        Assert.False(StudioSettings.SnapToGrid);

        Assert.Equal(SvgViewerGrid.DefaultStep, StudioSettings.GridSize);
        Assert.Equal(SvgViewerGrid.DefaultTurn, StudioSettings.RotationStep);
    }

    [Fact]
    public void Snapping_Survives_Being_Switched_On()
    {
        StudioSettings.SnapToGrid = true;

        Assert.True(StudioSettings.SnapToGrid);
        Assert.Contains("snapToGrid=on", File.ReadAllText(StudioSettings.Store), StringComparison.Ordinal);

        StudioSettings.SnapToGrid = false;

        Assert.False(StudioSettings.SnapToGrid);
    }

    [Fact]
    public void The_Two_Steps_Survive_Being_Written()
    {
        StudioSettings.GridSize = 12.5d;
        StudioSettings.RotationStep = 45d;

        Assert.Equal(12.5d, StudioSettings.GridSize);
        Assert.Equal(45d, StudioSettings.RotationStep);

        // Invariant, for the reason a caption size is: a machine writing a decimal comma would save
        // a step the next one reads as nothing.
        Assert.Contains("gridSize=12.5", File.ReadAllText(StudioSettings.Store), StringComparison.Ordinal);

        // And they are the grid a tab is handed, whether or not anything is landing on it.
        Assert.Equal(12.5f, StudioSettings.Grid.Step);
        Assert.Equal(45f, StudioSettings.Grid.Turn);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("0")]
    [InlineData("9999")]
    public void A_Step_Nobody_Can_Use_Is_The_Default(string written)
    {
        File.WriteAllText(StudioSettings.Store, $"gridSize={written}\nrotationStep={written}");

        Assert.Equal(SvgViewerGrid.DefaultStep, StudioSettings.GridSize);
        Assert.Equal(SvgViewerGrid.DefaultTurn, StudioSettings.RotationStep);
    }

    [Fact]
    public void A_Caption_Size_Is_Written_The_Same_Way_Wherever_It_Is_Read()
    {
        // Invariant, so a machine that writes a decimal comma does not save a setting the next one
        // reads as nothing and replaces with the default.
        StudioSettings.CaptionSize = 12.5d;

        Assert.Contains("captionSize=12.5", File.ReadAllText(StudioSettings.Store), StringComparison.Ordinal);
    }
}
