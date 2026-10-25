using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(Svg.Viewer.Skia.Avalonia.UnitTests.SvgViewerTestsAppBuilder))]

namespace Svg.Viewer.Skia.Avalonia.UnitTests;

internal static class SvgViewerTestsAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApplication>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .LogToTrace();
}

internal sealed class TestApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

/// <summary>The drag scroll's clock, held still for every test but the ones about scrolling.</summary>
/// <remarks>
/// A drop test aims at a row it has just brought into view, which leaves the row at the edge where a
/// held drag scrolls. On a loaded runner the scroll's 150 ms dwell ran out between the enter and the
/// assert, and the rows slid out from under the drop mark.
/// </remarks>
internal static class TestClock
{
    [ModuleInitializer]
    internal static void Stop() => SvgViewerDragScroll.Now = () => 0L;
}
