using System;
using Avalonia;

namespace Svg.Studio;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // The native pickers, deliberately: the macOS panel appends the extension belonging to the type
    // chosen in its own popup, which is how File → Export… gets away with asking one question. The
    // managed picker this used to force on macOS does neither — it was here for Avalonia 12.0.0's
    // use-after-free on dismissal (AvaloniaUI/Avalonia#21313), fixed in 12.0.2.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseSkia()
            .LogToTrace();
}
