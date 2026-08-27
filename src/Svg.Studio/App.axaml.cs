using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Svg.Studio;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // A path on the command line opens that drawing instead of the bundled sample, which is
            // also the way to look at a file without going through the picker.
            var path = desktop.Args?.FirstOrDefault(argument => !argument.StartsWith('-'));

            desktop.MainWindow = new MainWindow(path);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
