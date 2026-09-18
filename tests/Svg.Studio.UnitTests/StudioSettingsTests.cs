using System;
using System.IO;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>
/// The one setting the editor keeps between sessions, and the rule that decides it: on unless the
/// file says otherwise.
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
}
