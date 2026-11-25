// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>The window's control bar and status bar, both about whichever tab is in front.</summary>
/// <remarks>
/// The window's rather than each tab's: a drawing and a board each carried a toolbar repeating the
/// View menu. Every button is a command's (see <see cref="StudioCommands.Bound"/>), so what is held
/// here is only what no command says: the selection, the zoom as typed, the status, the note and
/// the pointer.
/// </remarks>
internal sealed class StudioBars
{
    private readonly Func<Control?> _front;
    private Control? _shown;
    private bool _queued;

    /// <summary>Where the pointer last was over a canvas, read again when the view moves under it.</summary>
    private (SvgViewerCanvas Canvas, Point At)? _hover;

    private readonly TextBlock _selection = new()
    {
        Name = "Selection",
        MinWidth = 80,
        MaxWidth = 130,
        FontSize = 11,
        Opacity = 0.65,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private readonly TextBox _zoom = new()
    {
        Name = "Zoom",
        Width = 64,
        MinHeight = 0,
        FontSize = 11,
        Padding = new Thickness(4, 1),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly TextBlock _status = Said("Status");
    private readonly TextBlock _note = Said("Note");
    private readonly TextBlock _pointer = Said("Pointer");

    public StudioBars(StudioCommands commands, Func<Control?> front)
    {
        _front = front;

        // The right end, where the layout picker joins Search.
        var end = Row(commands.Bound("commands", "Search"));

        DockPanel.SetDock(_selection, Dock.Left);
        DockPanel.SetDock(end, Dock.Right);

        Top = new DockPanel
        {
            Children =
            {
                _selection,
                end,
                Row(
                    commands.Bound("reset-parameters", "Reset parameters"),
                    commands.Bound("lock-ratio", "Lock ratio"),
                    commands.Bound("snapgrid", "Snap"),
                    commands.Bound("show-invisible", "Invisible"),
                    commands.Bound("captions", "Captions"))
            }
        };

        var zoom = Row(commands.Bound("zoomout", "−"), _zoom, commands.Bound("zoomin", "+"), commands.Bound("fit", "Fit"));
        var toggles = Row(commands.Bound("snapgrid", "Snap"), commands.Bound("show-grid", "Grid"));
        // Clipped, and the note capped: an Auto column is measured unbounded, so a long note would paint over its neighbours.
        var said = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ClipToBounds = true, Children = { _status, _note } };

        _status.Opacity = _pointer.Opacity = 0.65;
        _pointer.FontSize = 10;
        _pointer.Margin = new Thickness(12, 0);
        _note.Margin = new Thickness(12, 0, 0, 0);
        _note.MaxWidth = 420;
        Grid.SetColumn(_note, 1);
        DockPanel.SetDock(zoom, Dock.Left);
        DockPanel.SetDock(toggles, Dock.Right);
        DockPanel.SetDock(_pointer, Dock.Right);

        Bottom = new DockPanel { Children = { zoom, toggles, _pointer, said } };

        // Enter is left unhandled, so the window's own Enter handler takes the focus off the box.
        _zoom.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return)
            {
                Zoom();
            }
            else if (e.Key == Key.Escape)
            {
                ShowZoom();
            }
        };

        _zoom.LostFocus += (_, _) => ShowZoom();
    }

    /// <summary>The row under the menu: what is selected, and the switches about the tab.</summary>
    public DockPanel Top { get; }

    /// <summary>The row at the foot of the window: zoom, what the tab says, the pointer, the grid, each a segment of its own.</summary>
    public DockPanel Bottom { get; }

    /// <summary>Follows a drawing's tab, saying anything about it only while it is in front.</summary>
    public void Watch(SvgViewer viewer)
    {
        viewer.StatusChanged += (_, _) => Told(viewer);
        viewer.Elements.Selected += (_, _) => Told(viewer);
        viewer.Drafting.Changed += (_, _) => Told(viewer);

        Watch(viewer, viewer.Canvas);
    }

    /// <inheritdoc cref="Watch(SvgViewer)"/>
    public void Watch(GroupPanel board)
    {
        board.NoticeChanged += (_, _) => Told(board);
        board.DescribedChanged += (_, _) => Told(board);
        board.Drafting.Changed += (_, _) => Told(board);

        Watch(board, board.Canvas);
    }

    /// <summary>Says everything again about the tab in front, once whatever asked has finished.</summary>
    /// <remarks>
    /// Posted, because the window hears of a project edit before a board has rebuilt the rows it
    /// counts; and several asks in one pass are one refresh.
    /// </remarks>
    public void Show()
    {
        if (_queued)
        {
            return;
        }

        _queued = true;

        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            Say();
        });
    }

    private void Say()
    {
        var front = _front();
        var turned = !ReferenceEquals(front, _shown);

        if (turned)
        {
            _shown = front;
            _hover = null;
            _pointer.Text = string.Empty;
        }

        switch (front)
        {
            case SvgViewer viewer:
                _selection.Text = Selection(viewer);
                _status.Text = viewer.Status + Armed(viewer.Drafting);
                _note.Text = viewer.Note ?? string.Empty;
                _note[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(
                    viewer.NoteWarns ? "SvgViewerSourceWarningBrush" : "SvgViewerSourceErrorBrush");
                break;

            case GroupPanel board:
                _selection.Text = ReferenceEquals(board.Described, board.Node) ? "No selection" : ProjectWorkspace.Label(board.Described);
                _status.Text = Counted(ProjectWorkspace.Label(board.Node), board.ParameterCount) + Armed(board.Drafting);
                _note.Text = board.Notice ?? string.Empty;
                _note.ClearValue(TextBlock.ForegroundProperty);
                break;

            default:
                _selection.Text = _status.Text = _note.Text = string.Empty;
                break;
        }

        _note.IsVisible = _note.Text is { Length: > 0 };

        // Not under somebody typing a zoom, unless it was for a tab no longer in front.
        if (turned || !_zoom.IsFocused)
        {
            ShowZoom();
        }
    }

    private void Watch(Control tab, SvgViewerCanvas canvas)
    {
        canvas.ViewChanged += (_, _) =>
        {
            // A pan with no pointer move, from a trackpad, moves the drawing under a pointer standing still.
            if (_hover is { } hover && ReferenceEquals(hover.Canvas, canvas))
            {
                _pointer.Text = Pointer(canvas, hover.At);
            }

            Told(tab);
        };

        // Handled ones too: the canvas takes the pointer for its own gestures.
        canvas.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (ReferenceEquals(_front(), tab))
            {
                _hover = (canvas, e.GetPosition(canvas));
                _pointer.Text = Pointer(canvas, _hover.Value.At);
            }
        }, handledEventsToo: true);

        canvas.AddHandler(InputElement.PointerExitedEvent, (_, _) =>
        {
            if (ReferenceEquals(_front(), tab))
            {
                _hover = null;
                _pointer.Text = string.Empty;
            }
        }, handledEventsToo: true);

        // Selecting the page raises nothing of its own, and it is a click that does it.
        canvas.AddHandler(InputElement.PointerReleasedEvent, (_, _) => Told(tab), handledEventsToo: true);
    }

    private void Told(Control tab)
    {
        if (ReferenceEquals(_front(), tab))
        {
            Show();
        }
    }

    private SvgViewerCanvas? Canvas() => _front() switch
    {
        SvgViewer viewer => viewer.Canvas,
        GroupPanel board => board.Canvas,
        _ => null
    };

    private void ShowZoom()
    {
        var canvas = Canvas();

        _zoom.IsEnabled = canvas is { };
        _zoom.Text = canvas is { } ? (canvas.Scale * 100d).ToString("0", CultureInfo.CurrentCulture) + "%" : string.Empty;
    }

    /// <summary>Zooms to what was typed, about the middle of the view; anything else puts the zoom back.</summary>
    private void Zoom()
    {
        if (Canvas() is { } canvas
            && double.TryParse((_zoom.Text ?? string.Empty).Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.CurrentCulture, out var percent)
            && percent > 0d
            && double.IsFinite(percent))
        {
            canvas.ZoomTo(percent / 100d, new Point(canvas.Bounds.Width / 2d, canvas.Bounds.Height / 2d));
        }

        ShowZoom();
    }

    private static string Selection(SvgViewer viewer)
    {
        var count = viewer.Elements.SelectedAddresses.Count;

        return count > 1 ? $"{count} objects"
            : count == 1 ? viewer.Elements.SelectedNode?.ToString() ?? "1 object"
            : viewer.IsPageSelected ? "Page"
            : viewer.Document is null ? string.Empty
            : "No selection";
    }

    /// <summary>A board's name and its parameters, worded as a drawing's status words its own.</summary>
    private static string Counted(string name, int count)
        => count == 0 ? $"{name} — no variables" : $"{name} — {count} parameter{(count == 1 ? string.Empty : "s")}";

    /// <summary>The armed tool and how it is used, read off the strip's own table.</summary>
    private static string Armed(SvgViewerDraw draw)
    {
        foreach (var row in SvgViewerDraw.Tools)
        {
            if (draw.Reshaping ? row.Name == "Points" : row.Tool == draw.Shape)
            {
                return $" · {row.Name}: {row.Use}";
            }
        }

        return string.Empty;
    }

    /// <summary>Where the pointer is in the user units of the drawing under it, or nothing off every drawing.</summary>
    /// <remarks>A placement is drawn after its root transform (viewBox, size), so that is what is taken back off.</remarks>
    private static string Pointer(SvgViewerCanvas canvas, Point at)
    {
        if (!canvas.TryGetPlacementAt(at, out var placed, out var drawn)
            || placed is null
            || !placed.Svg.TryEnsureRetainedSceneGraph(out var scene)
            || scene is null
            || !scene.Root.TotalTransform.TryInvert(out var back))
        {
            return string.Empty;
        }

        var user = back.MapPoint(new ShimSkiaSharp.SKPoint(drawn.X, drawn.Y));

        return string.Create(CultureInfo.CurrentCulture, $"X {user.X:0.0}  Y {user.Y:0.0}");
    }

    private static StackPanel Row(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };

        row.Children.AddRange(controls);

        return row;
    }

    private static TextBlock Said(string name) => new()
    {
        Name = name,
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
}
