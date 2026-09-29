using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
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
    }
}

internal static class TestStores
{
    // Every test that opens a drawing adds it to Open Recent, and every test that edits a project
    // has a copy of it kept. Pointed at files of their own so a run does not rewrite what belongs to
    // whoever is running it — here rather than in TestApplication.Initialize, which runs when the
    // first [AvaloniaFact] does and so could repoint the store under a settings test already running.
    [ModuleInitializer]
    internal static void Redirect()
    {
        RecentFiles.Store = Path.Combine(Path.GetTempPath(), $"svg-studio-recent-{Guid.NewGuid():N}");
        StudioSettings.Store = Path.Combine(Path.GetTempPath(), $"svg-studio-settings-{Guid.NewGuid():N}");
        ProjectRecovery.Store = Path.Combine(Path.GetTempPath(), $"svg-studio-recovery-{Guid.NewGuid():N}");
        TemplateLibrary.ChoicesStore = Path.Combine(Path.GetTempPath(), $"svg-studio-choices-{Guid.NewGuid():N}");

        // Every settings window asks whether a Streamline key is kept, and one test puts one there.
        Keychain.Current = new MemoryKeychain();
    }
}

/// <summary>A keychain that forgets everything when the run ends.</summary>
internal sealed class MemoryKeychain : Keychain
{
    private readonly Dictionary<(string, string), string> _secrets = new();

    public override string? Get(string service, string account) =>
        _secrets.TryGetValue((service, account), out var secret) ? secret : null;

    public override void Set(string service, string account, string secret) => _secrets[(service, account)] = secret;

    public override void Remove(string service, string account) => _secrets.Remove((service, account));
}
