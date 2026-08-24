using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
// Aliased because this application's namespace is also called SvgViewer.
using ViewerControl = Svg.Viewer.Skia.Avalonia.SvgViewer;

namespace SvgViewer;

/// <summary>
/// The shell: one tab per open drawing.
/// </summary>
/// <remarks>
/// <para>
/// The viewer control holds a single document by design, so the tabs are the shell's rather than
/// its: the window puts one viewer in each tab and handles their <c>OpenRequested</c>, which is what
/// turns picking or dropping a file into a new tab instead of replacing what is already up.
/// </para>
/// <para>
/// Reordering is the window's too, because <see cref="TabControl"/> has none to enable: a tab is
/// dragged by moving it within <see cref="ItemsControl.Items"/> as the pointer crosses its
/// neighbours, which works precisely because the items <em>are</em> the containers — there is no
/// data behind them to keep in step.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>How far the pointer travels before a press on a tab is a drag and not a click.</summary>
    private const double DragThreshold = 4d;

    /// <summary>How far one wheel notch scrolls the strip.</summary>
    private const double WheelStep = 50d;

    private readonly TabControl _tabs;

    private Border? _strip;

    private TabItem? _pressed;
    private Point _pressedAt;
    private double _grabbedAt;
    private bool _dragging;

    /// <summary>Where the dragged tab is drawn relative to the slot it has been laid out in.</summary>
    private readonly TranslateTransform _carry = new();

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
        _tabs.TemplateApplied += OnTabsTemplateApplied;

        // On the strip rather than on each tab, and tunnelling because TabItem handles a press
        // itself to become the selected tab, so a bubbling handler would never see it.
        // Tunnelling so the editor in the pane does not get first refusal on it.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        _tabs.AddHandler(PointerPressedEvent, OnTabPointerPressed, RoutingStrategies.Tunnel);
        _tabs.AddHandler(PointerMovedEvent, OnTabPointerMoved, RoutingStrategies.Tunnel);
        _tabs.AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Tunnel);
        _tabs.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(null));

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

        // Both are dressed by the window's styles, which is also where the trimming that keeps one
        // long file name from filling the strip lives.
        var title = new TextBlock { Text = "Untitled", Classes = { "title" } };

        var close = new Button
        {
            Content = "✕",
            Classes = { "close" },
            [ToolTip.TipProperty] = "Close this drawing"
        };

        var item = new TabItem
        {
            Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { title, close }
            },
            Content = viewer
        };

        close.Click += async (_, _) => await CloseTabAsync(item);

        var name = "drawing";

        viewer.DocumentOpened += (_, document) =>
        {
            name = document.Path is { } path ? Path.GetFileName(path) : "drawing";
            title.Text = name;
            item[ToolTip.TipProperty] = document.Path;
            UpdateTitle();
        };

        // A dot rather than an asterisk: the tab already ends in a close button, and two marks
        // competing for the same corner read as one smudge.
        viewer.SourceModifiedChanged += (_, modified) =>
        {
            title.Text = modified ? name + " •" : name;
            UpdateTitle();
        };

        viewer.OpenRequested += (_, request) =>
        {
            request.Handled = true;

            // Handed back rather than discarded, so whoever asked — the toolbar, a drop, a test —
            // waits for the drawings instead of for the request being taken.
            request.Completion = OpenAsync(viewer, request.Paths);
        };

        _tabs.Items.Add(item);
        _tabs.SelectedItem = item;

        UpdateStrip();

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

    // ---- reordering -------------------------------------------------------------------------

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source
            // The close button is a button, not a drag handle.
            || source.FindAncestorOfType<Button>(true) is { }
            // A press anywhere but on a tab — the drawing, the toolbar — is not a drag either. Only
            // the headers are inside a TabItem; the selected tab's content is not.
            || source.FindAncestorOfType<TabItem>(true) is not { } item
            || item.GetVisualParent() is not { } strip)
        {
            return;
        }

        if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pressed = item;
        _pressedAt = e.GetPosition(strip);
        _grabbedAt = _pressedAt.X - item.Bounds.X;
        _dragging = false;
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } dragged || dragged.GetVisualParent() is not Layoutable strip)
        {
            return;
        }

        // A release the window never saw — the button let go outside it, or over another
        // application — leaves a drag that would otherwise resume the moment the pointer comes back.
        if (!e.GetCurrentPoint(strip).Properties.IsLeftButtonPressed)
        {
            EndDrag(e.Pointer);
            return;
        }

        var position = e.GetPosition(strip);

        if (!_dragging)
        {
            if (Math.Abs(position.X - _pressedAt.X) < DragThreshold)
            {
                return;
            }

            // Without the capture the moves stop arriving the moment the pointer leaves the tab,
            // which is immediately: a drag is the pointer going somewhere else. The strip is
            // captured rather than the tab, because reordering takes the tab out of Items and a
            // captured control that leaves the tree loses the capture — which ended the drag after
            // its own first swap.
            _dragging = true;
            dragged.ZIndex = 1;
            dragged.RenderTransform = _carry;
            e.Pointer.Capture(_tabs);
        }

        var from = _tabs.Items.IndexOf(dragged);
        var to = from;

        for (var index = 0; index < _tabs.Items.Count; index++)
        {
            if (index == from || _tabs.Items[index] is not TabItem neighbour)
            {
                continue;
            }

            // Half of a neighbour, not its edge: tabs are as wide as their titles, so trading places
            // on contact would leave the pointer over the tab it just displaced and trade straight
            // back. Every neighbour is measured rather than only the adjacent one, because a quick
            // drag lands the pointer several tabs along and should take the tab all the way there.
            if (index > from && position.X > neighbour.Bounds.Center.X)
            {
                to = Math.Max(to, index);
            }
            else if (index < from && position.X < neighbour.Bounds.Center.X)
            {
                to = Math.Min(to, index);
            }
        }

        if (to != from)
        {
            MoveTab(dragged, to);

            // The tab is placed against its own laid-out position below, and a move it has not been
            // arranged for yet would put it a whole tab-width off for one frame.
            strip.UpdateLayout();
        }

        // The tab follows the pointer, held by the point it was grabbed at. Reordering underneath is
        // what the strip does; this is what makes it look like the tab is being carried there. The
        // one transform is moved rather than replaced, so a drag allocates nothing per frame.
        _carry.X = position.X - _grabbedAt - dragged.Bounds.X;
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e) => EndDrag(e.Pointer);

    /// <summary>Puts the dragged tab down where the strip has already made room for it.</summary>
    private void EndDrag(IPointer? pointer)
    {
        if (_pressed is { } dragged)
        {
            dragged.RenderTransform = null;
            dragged.ZIndex = 0;
        }

        if (_dragging)
        {
            pointer?.Capture(null);
        }

        _pressed = null;
        _dragging = false;
    }

    /// <summary>Moves a tab within the strip, keeping it the selected one.</summary>
    /// <remarks>
    /// Removing the selected item clears the selection, and a tab that deselected itself halfway
    /// through being dragged would swap the drawing under the pointer.
    /// </remarks>
    private void MoveTab(TabItem item, int index)
    {
        _tabs.Items.Remove(item);
        _tabs.Items.Insert(index, item);
        _tabs.SelectedItem = item;
    }

    private void OnTabsTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        // The first tab is added before the control has a template, so the strip settles its own
        // visibility as it arrives rather than waiting for the second one.
        _strip = e.NameScope.Find<Border>("PART_TabStripBand");
        UpdateStrip();

        if (e.NameScope.Find<ScrollViewer>("PART_TabStrip") is not { } strip)
        {
            return;
        }

        // The strip only scrolls sideways, and a wheel that does nothing over an overflowing row of
        // tabs reads as the row being stuck.
        strip.AddHandler(
            PointerWheelChangedEvent,
            (_, wheel) =>
            {
                strip.Offset = strip.Offset.WithX(strip.Offset.X - (wheel.Delta.Y + wheel.Delta.X) * WheelStep);
                wheel.Handled = true;
            },
            RoutingStrategies.Tunnel);
    }

    // ---- lifetime ----------------------------------------------------------------------------

    private async Task CloseTabAsync(TabItem item)
    {
        // A close button is one click away from losing an edit, and nothing else would have said so.
        if (item.Content is ViewerControl { IsSourceModified: true } editing
            && !await ConfirmDiscard(editing.DocumentPath is { } path ? Path.GetFileName(path) : "This drawing"))
        {
            return;
        }

        CloseTab(item);
    }

    /// <summary>Asks whether edits that are not on disk may be thrown away.</summary>
    private async Task<bool> ConfirmDiscard(string name)
    {
        var discard = new Button { Content = "Discard changes" };
        var keep = new Button { Content = "Keep editing", IsDefault = true };

        var dialog = new Window
        {
            Title = "Unsaved changes",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20d),
                Spacing = 16d,
                Children =
                {
                    new TextBlock { Text = $"{name} has changes that have not been saved." },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8d,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { discard, keep }
                    }
                }
            }
        };

        discard.Click += (_, _) => dialog.Close(true);
        keep.Click += (_, _) => dialog.Close(false);

        return await dialog.ShowDialog<bool>(this);
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

        UpdateStrip();
        UpdateTitle();
    }

    /// <summary>Saves the drawing in the selected tab.</summary>
    /// <remarks>
    /// The modifier follows the platform rather than being spelled Control, so this is Cmd+S on
    /// macOS and Ctrl+S everywhere else, which is what each of them means by "save".
    /// </remarks>
    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        if (e.Key != Key.S || e.KeyModifiers != command)
        {
            return;
        }

        e.Handled = true;

        if ((_tabs.SelectedItem as TabItem)?.Content is ViewerControl viewer)
        {
            await viewer.SaveSourceAsync();
        }
    }

    /// <summary>Shows the strip only once there is a choice to make.</summary>
    /// <remarks>
    /// A window on a single drawing is not a window with one tab in it: the strip would be a row of
    /// chrome naming what the title bar already says, above a drawing it takes 34px from.
    /// </remarks>
    private void UpdateStrip()
    {
        if (_strip is { } strip)
        {
            strip.IsVisible = _tabs.Items.Count > 1;
        }
    }

    private void UpdateTitle()
    {
        var open = (_tabs.SelectedItem as TabItem)?.Content as ViewerControl;
        var path = open?.DocumentPath;
        var mark = open is { IsSourceModified: true } ? " •" : string.Empty;

        Title = path is { } ? $"{Path.GetFileName(path)}{mark} — SVG viewer" : "SVG viewer";
    }
}
