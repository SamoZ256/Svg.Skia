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

    /// <summary>Opens the settings, from the macOS application menu.</summary>
    /// <remarks>
    /// Handled here because the menu is the application's rather than a window's, and passed to the
    /// window it is shown over — settings belong to the application, but a window is what a window
    /// opens in front of.
    /// </remarks>
    private async void OnSettings(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
        {
            await window.ShowSettingsAsync();
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
            // A path on the command line opens that drawing, which is also the way to look at a file
            // without going through the picker. It is how Windows and Linux hand over a double-click.
            var path = desktop.Args?.FirstOrDefault(argument => !argument.StartsWith('-'));

            desktop.MainWindow = new MainWindow(path);

            // macOS hands a double-clicked file over as an event instead, at launch and to a copy
            // already running alike.
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
            {
                activatable.Activated += OnActivated;
            }

            _ = System.Threading.Tasks.Task.Run(FileAssociation.Register);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
