using System;
using System.IO;
using Avalonia;
using Avalonia.Headless;
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

        // Every test that opens a drawing adds it to Open Recent. Pointed at a file of its own so a
        // run does not rewrite the list belonging to whoever is running it.
        RecentFiles.Store = Path.Combine(Path.GetTempPath(), $"svg-studio-recent-{Guid.NewGuid():N}");
    }
}
