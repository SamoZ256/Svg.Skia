using System;
using System.IO;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(Svg.Studio.UnitTests.SvgStudioTestsAppBuilder))]

namespace Svg.Studio.UnitTests;

internal static class SvgStudioTestsAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApplication>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .LogToTrace();
}

internal sealed class TestApplication : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());

        // The editor's own chrome, which App.axaml includes the same way. Without it every window
        // here stands on Fluent's pure black, which is the thing that file exists to replace.
        Styles.Add(new StyleInclude(new Uri("avares://Svg.Studio/"))
        {
            Source = new Uri("avares://Svg.Studio/StudioChrome.axaml")
        });

        // Every test that opens a drawing adds it to Open Recent, and every test that edits a
        // project has a copy of it kept. Pointed at files of their own so a run does not rewrite
        // what belongs to whoever is running it.
        RecentFiles.Store = Path.Combine(Path.GetTempPath(), $"svg-studio-recent-{Guid.NewGuid():N}");
        StudioSettings.Store = Path.Combine(Path.GetTempPath(), $"svg-studio-settings-{Guid.NewGuid():N}");
        ProjectRecovery.Store = Path.Combine(Path.GetTempPath(), $"svg-studio-recovery-{Guid.NewGuid():N}");
    }
}
