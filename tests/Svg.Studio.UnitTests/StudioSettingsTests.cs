using System;
using System.IO;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// The settings the editor keeps between sessions, and the rules that decide them: the copy is on
/// unless the file says otherwise, and a size nobody can use is the one nobody set.
/// </summary>
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
    public void A_Caption_Size_Is_Written_The_Same_Way_Wherever_It_Is_Read()
    {
        // Invariant, so a machine that writes a decimal comma does not save a setting the next one
        // reads as nothing and replaces with the default.
        StudioSettings.CaptionSize = 12.5d;

        Assert.Contains("captionSize=12.5", File.ReadAllText(StudioSettings.Store), StringComparison.Ordinal);
    }
}
