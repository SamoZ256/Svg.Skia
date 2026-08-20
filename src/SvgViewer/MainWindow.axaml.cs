using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
// Aliased because this application's namespace is also called SvgViewer.
using ViewerControl = Svg.Viewer.Skia.Avalonia.SvgViewer;

namespace SvgViewer;

/// <summary>
/// The shell: one tab per open drawing.
/// </summary>
/// <remarks>
/// The viewer control holds a single document by design, so the tabs are the shell's rather than
/// its: the window puts one viewer in each tab and handles their <c>OpenRequested</c>, which is what
/// turns picking or dropping a file into a new tab instead of replacing what is already up.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly TabControl _tabs;

    public MainWindow()
        : this(null)
    {
    }

    /// <param name="path">A drawing to open instead of the bundled sample.</param>
    public MainWindow(string? path)
    {
        AvaloniaXamlLoader.Load(this);

        _tabs = this.FindControl<TabControl>("Tabs")!;
        _tabs.SelectionChanged += (_, _) => UpdateTitle();

        var viewer = AddTab();

        var startup = path is { } && File.Exists(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, "Assets", "parametric.svg");

        if (File.Exists(startup))
        {
            _ = viewer.LoadAsync(startup);
        }
    }

    /// <summary>Adds an empty tab, selects it, and returns the viewer that fills it.</summary>
    private ViewerControl AddTab()
    {
        var viewer = new ViewerControl();

        var title = new TextBlock
        {
            Text = "Untitled",
            VerticalAlignment = VerticalAlignment.Center
        };

        var close = new Button
        {
            Content = "✕",
            FontSize = 11,
            Padding = new Thickness(5, 1),
            Background = Brushes.Transparent,
            BorderThickness = default,
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = "Close this drawing"
        };

        var item = new TabItem
        {
            Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { title, close }
            },
            Content = viewer
        };

        close.Click += (_, _) => CloseTab(item);

        viewer.DocumentOpened += (_, document) =>
        {
            title.Text = document.Path is { } path ? Path.GetFileName(path) : "drawing";
            UpdateTitle();
        };

        viewer.OpenRequested += (_, request) =>
        {
            request.Handled = true;
            _ = OpenAsync(viewer, request.Paths);
        };

        _tabs.Items.Add(item);
        _tabs.SelectedItem = item;

        return viewer;
    }

    /// <summary>Opens each path in a tab of its own.</summary>
    /// <remarks>
    /// The tab that asked is reused while it holds nothing, so opening from a freshly closed window —
    /// or dropping several files at once — does not leave an empty tab in front of the drawings.
    /// </remarks>
    private async Task OpenAsync(ViewerControl source, IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            var viewer = source.Document is null ? source : AddTab();

            await viewer.LoadAsync(path).ConfigureAwait(true);
        }
    }

    private void CloseTab(TabItem item)
    {
        _tabs.Items.Remove(item);

        // Nothing else disposes the document a discarded viewer is holding.
        (item.Content as ViewerControl)?.Close();

        // The window keeps somewhere to open the next drawing rather than closing itself.
        if (_tabs.Items.Count == 0)
        {
            AddTab();
        }

        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var path = (_tabs.SelectedItem as TabItem)?.Content is ViewerControl viewer
            ? viewer.DocumentPath
            : null;

        Title = path is { } ? $"{Path.GetFileName(path)} — SVG viewer" : "SVG viewer";
    }
}
