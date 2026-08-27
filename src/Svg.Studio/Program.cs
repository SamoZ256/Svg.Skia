using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Dialogs;

namespace Svg.Studio;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseSkia()
            .LogToTrace();

        // Avalonia 12.0.0's native storage provider crashes as the panel is dismissed on macOS --
        // inside StorageProvider::OpenFileDialog's completion block, under
        // -[NSSavePanel didEndPanelWithReturnCode:], and samples/TestApp crashes there identically.
        // The managed picker is drawn by Avalonia itself and never reaches that code.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            builder = builder.UseManagedSystemDialogs();
        }

        return builder;
    }
}
