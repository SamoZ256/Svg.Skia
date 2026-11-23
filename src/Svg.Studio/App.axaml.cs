using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace Svg.Studio;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Before anything is shown, so a launch opens in the theme somebody picked rather than
        // flashing the other one on the way there.
        SettingsWindow.Repaint();
    }

    private void OnSettings(object? sender, EventArgs e) => Run("preferences");

    private void OnAbout(object? sender, EventArgs e) => Run("about");

    /// <summary>Runs one of the window's commands, from the macOS application menu.</summary>
    /// <remarks>
    /// Handled here because the menu is the application's rather than a window's, and passed to the
    /// window it is shown over — settings belong to the application, but a window is what a window
    /// opens in front of.
    /// </remarks>
    private async void Run(string id)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
        {
            await window.Registry.Run(id);
        }
    }

    private async void OnActivated(object? sender, ActivatedEventArgs e)
    {
        if (e is FileActivatedEventArgs { Files: var files }
            && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
        {
            await window.OpenAsync(files.Select(file => file.TryGetLocalPath()).OfType<string>().ToList());
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // A path on the command line opens that drawing or project, which is also the way to look
            // at a file without going through the picker.
            var path = desktop.Args?.FirstOrDefault(argument => !argument.StartsWith('-'));

            desktop.MainWindow = new MainWindow(path);

            // macOS hands a double-clicked file over as an event instead, at launch and to a copy
            // already running alike.
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
            {
                activatable.Activated += OnActivated;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
