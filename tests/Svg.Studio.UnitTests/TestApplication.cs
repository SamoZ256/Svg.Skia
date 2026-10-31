using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Svg.Viewer.Skia.Avalonia;

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

/// <summary>The drag scroll's clock, held still for every test but the ones about scrolling.</summary>
/// <remarks>
/// A drop test aims at a row it has just brought into view, which leaves the row at the edge where a
/// held drag scrolls. On a loaded runner the scroll's 150 ms dwell ran out between the enter and the
/// assert, and the rows slid out from under the drop mark.
/// </remarks>
internal static class TestClock
{
    private static int s_lent;

    [ModuleInitializer]
    internal static void Stop() => SvgViewerDragScroll.Now = () => 0L;

    /// <summary>The real clock, for a test about scrolling, held still again once the last test that asked for it is done.</summary>
    /// <remarks>
    /// Lent and counted rather than taken back in each class's Dispose: a test waiting on real time
    /// lets other tests run on the dispatcher, and one of them ending stopped the clock under it —
    /// on Linux CI, 14 ms into a five-second wait for the tab strip to scroll.
    /// </remarks>
    internal static IDisposable Real()
    {
        if (Interlocked.Increment(ref s_lent) == 1)
        {
            SvgViewerDragScroll.Now = Stopwatch.GetTimestamp;
        }

        return new Lent();
    }

    private sealed class Lent : IDisposable
    {
        private int _returned;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0 && Interlocked.Decrement(ref s_lent) == 0)
            {
                Stop();
            }
        }
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
