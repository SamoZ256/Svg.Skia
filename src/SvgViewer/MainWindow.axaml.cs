using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
// Aliased because this application's namespace is also called SvgViewer.
using ViewerControl = Svg.Viewer.Skia.Avalonia.SvgViewer;

namespace SvgViewer;

public partial class MainWindow : Window
{
    public MainWindow()
        : this(null)
    {
    }

    /// <param name="path">A drawing to open instead of the bundled sample.</param>
    public MainWindow(string? path)
    {
        AvaloniaXamlLoader.Load(this);

        var viewer = this.FindControl<ViewerControl>("Viewer")!;

        viewer.DocumentOpened += (_, document) =>
            Title = document.Path is { } path ? $"{Path.GetFileName(path)} — SVG viewer" : "SVG viewer";

        var startup = path is { } && File.Exists(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, "Assets", "parametric.svg");

        if (File.Exists(startup))
        {
            _ = viewer.LoadAsync(startup);
        }
    }
}
