// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// Scrolls whatever a drag is held near the edge of, so a drop can reach what is out of sight.
/// </summary>
/// <remarks>
/// A step on every update as well as on a timer. The timer keeps a still pointer scrolling where no
/// updates come (Linux sends them only on a move); the step inside an update lands in the frame of
/// the drop mark that update drew, so a host taking marks down on a scroll does not flicker.
/// </remarks>
public static class SvgViewerDragScroll
{
    /// <summary>How near an edge the pointer has to be, at most: a quarter of a smaller view.</summary>
    private const double Band = 32d;

    /// <summary>Pixels a second, with the pointer on the very edge.</summary>
    private const double Fastest = 800d;

    /// <summary>The most one step makes up for, so a stalled UI thread does not throw the view when it comes back.</summary>
    private const double CatchUp = 0.05d;

    /// <summary>How long the pointer rests in a band before the view moves, so a drag picked up in one, or passing through, does not.</summary>
    private static readonly long s_dwell = Stopwatch.Frequency * 150L / 1000L;

    private static DispatcherTimer? s_timer;

    /// <summary>What the last update was over, whose scrolling ancestors are the views that could move.</summary>
    private static Visual? s_over;

    /// <summary>The window it was in, where <see cref="s_at"/> is measured because scrolling does not move it.</summary>
    private static TopLevel? s_root;

    private static Point s_at;

    /// <summary>The timestamp scrolling is owed from, or null while the pointer is in no band.</summary>
    private static long? s_owed;

    /// <summary>Scrolls any view under <paramref name="root"/> a drag is held near the edge of.</summary>
    /// <remarks>
    /// Handled events too, because every drop target marks its drags handled. Attaching a root inside
    /// another attached root costs nothing: a step is taken in whole pixels, and the second ask within
    /// one event owes less than one.
    /// </remarks>
    public static void Attach(Interactive root)
    {
        root.AddHandler(DragDrop.DragEnterEvent, Carried, handledEventsToo: true);
        root.AddHandler(DragDrop.DragOverEvent, Carried, handledEventsToo: true);

        root.AddHandler(DragDrop.DragLeaveEvent, Left, handledEventsToo: true);
        root.AddHandler(DragDrop.DropEvent, (_, _) => Stop(), handledEventsToo: true);
    }

    /// <summary>What a drag is over now, inside <paramref name="within"/>.</summary>
    /// <remarks>
    /// Asked rather than read off the event's source: Avalonia raises a drop on whatever the last
    /// update hit, without testing again, and a view scrolled since has moved something else there.
    /// </remarks>
    public static Visual? Under(DragEventArgs e, InputElement within)
        => within.InputHitTest(e.GetPosition(within)) as Visual;

    private static void Carried(object? sender, DragEventArgs e)
    {
        if (e.Source is Visual over && TopLevel.GetTopLevel(over) is { } root)
        {
            Hold(over, e.GetPosition(root));
        }
    }

    /// <summary>Scrolls whatever <paramref name="over"/> sits in, with a drag held at <paramref name="at"/>, a point of its window.</summary>
    /// <remarks>
    /// For a drag a control runs itself on a captured pointer, which raises nothing <see cref="Attach"/>
    /// hears: called on each move with <c>e.GetPosition(null)</c>, and <see cref="Stop"/> where it ends.
    /// </remarks>
    public static void Hold(Visual over, Point at)
    {
        if (TopLevel.GetTopLevel(over) is not { } root)
        {
            return;
        }

        // Another window is another drag: whatever the last left without a leave or a drop is ended,
        // timer and all, since a timer whose dispatcher was replaced, as per headless test, never ticks.
        if (!ReferenceEquals(root, s_root))
        {
            Stop();

            s_root = root;
        }

        s_over = over;
        s_at = at;

        if (!Step())
        {
            Stop();

            return;
        }

        s_timer ??= Timer();
        s_timer.IsEnabled = true;
    }

    /// <remarks>
    /// Not a stop there and then: moving from one control to the next raises a leave just before the
    /// enter, and stopping between them would start the dwell again at every row the pointer crosses.
    /// </remarks>
    private static void Left(object? sender, DragEventArgs e)
    {
        s_over = null;

        Dispatcher.UIThread.Post(() =>
        {
            if (s_over is null)
            {
                Stop();
            }
        });
    }

    private static DispatcherTimer Timer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16d) };

        timer.Tick += (_, _) =>
        {
            if (!Step())
            {
                Stop();
            }
        };

        return timer;
    }

    /// <summary>Ends the scrolling a drag started.</summary>
    public static void Stop()
    {
        s_timer?.Stop();
        s_timer = null;
        s_over = null;
        s_root = null;
        s_owed = null;
    }

    /// <summary>Scrolls by what is owed since the last step, and says whether the pointer is still where a view would scroll.</summary>
    /// <remarks>Each axis goes to the innermost view that can still move that way, so one at its end hands on to the one around it.</remarks>
    private static bool Step()
    {
        if (s_over is not { } over || TopLevel.GetTopLevel(over) is not { } root)
        {
            return false;
        }

        ScrollViewer? across = null;
        ScrollViewer? down = null;
        var speed = default(Vector);

        foreach (var viewer in over.GetSelfAndVisualAncestors().OfType<ScrollViewer>())
        {
            // A box's own scroller would slide its text sideways under a drop meant for the whole box.
            if (viewer.TemplatedParent is TextBox || root.TranslatePoint(s_at, viewer) is not { } at)
            {
                continue;
            }

            if (across is null && Speed(at.X, viewer.Bounds.Width, viewer.Offset.X, viewer.ScrollBarMaximum.X) is var x and not 0d)
            {
                across = viewer;
                speed = speed.WithX(x);
            }

            if (down is null && Speed(at.Y, viewer.Bounds.Height, viewer.Offset.Y, viewer.ScrollBarMaximum.Y) is var y and not 0d)
            {
                down = viewer;
                speed = speed.WithY(y);
            }
        }

        if (across is null && down is null)
        {
            s_owed = null;

            return false;
        }

        var now = Stopwatch.GetTimestamp();
        var seconds = Math.Min((now - (s_owed ??= now + s_dwell)) / (double)Stopwatch.Frequency, CatchUp);
        var step = new Vector(Math.Round(speed.X * Math.Max(seconds, 0d)), Math.Round(speed.Y * Math.Max(seconds, 0d)));

        if (step == default)
        {
            return true;
        }

        s_owed = now;

        if (across is { })
        {
            across.Offset += new Vector(step.X, 0d);
        }

        if (down is { })
        {
            down.Offset += new Vector(0d, step.Y);
        }

        return true;
    }

    /// <summary>
    /// Pixels a second along one axis of a view: negative toward its start, positive toward its end,
    /// and none outside both bands or where it can go no further that way.
    /// </summary>
    private static double Speed(double at, double length, double offset, double furthest)
    {
        var band = Math.Min(Band, length / 4d);

        return at < band && offset > 0d ? -Fastest * Math.Min((band - at) / band, 1d)
            : at > length - band && offset < furthest ? Fastest * Math.Min((at - length + band) / band, 1d)
            : 0d;
    }
}
