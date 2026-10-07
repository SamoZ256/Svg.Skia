using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>A hand on a window: a board's canvas found and pointed at, a drag, a drop, and the wait for what one started.</summary>
internal static class Gestures
{
    /// <summary>Lets the dispatcher run until <paramref name="done"/>, for what a gesture starts and nobody awaits.</summary>
    /// <remarks>
    /// Rounds rather than seconds, so a starved runner that takes longer over each round gets more
    /// of them. One more round after: a background completion can make the model say yes while the
    /// tree's update is still queued.
    /// </remarks>
    internal static async Task Until(Func<bool> done)
    {
        for (var waited = 0; !done(); waited++)
        {
            Assert.True(waited < 1000, "What was waited for never happened.");

            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The one canvas a group tab draws on.</summary>
    internal static SvgViewerCanvas Canvas(GroupPanel panel)
        => panel.GetVisualDescendants().OfType<SvgViewerCanvas>().Single();

    /// <summary>Where in the control a point of the arrangement is, checked by mapping it back.</summary>
    /// <remarks>
    /// The arrangement does not have to start at the origin — a board arranged into places begins
    /// wherever the places say — so where it does start is asked for rather than assumed: the
    /// control's own origin maps to it.
    /// </remarks>
    internal static Point Over(SvgViewerCanvas canvas, float x, float y)
    {
        Assert.True(canvas.TryGetDrawingPoint(new Point(canvas.OffsetX, canvas.OffsetY), out var origin));

        var at = new Point(
            (x - origin.X) * canvas.Scale + canvas.OffsetX,
            (y - origin.Y) * canvas.Scale + canvas.OffsetY);

        Assert.True(canvas.TryGetDrawingPoint(at, out var back));
        Assert.Equal(x, back.X, 3);
        Assert.Equal(y, back.Y, 3);

        return at;
    }

    /// <summary>Drags from one point of the control to another, as a hand would.</summary>
    internal static void Drag(
        Window window,
        Control control,
        Point from,
        Point to,
        MouseButton button = MouseButton.Left,
        KeyModifiers modifiers = KeyModifiers.None)
    {
        Press(window, control, from, button, modifiers);
        Move(window, control, to, button, modifiers);
        Release(window, control, to, button, modifiers);
    }

    /// <summary>Presses at a point of the control, which the window is told its root is at after translating it.</summary>
    /// <param name="clicks">Which press of a run this is, as the pointer counts them: 2 for the second of a double click.</param>
    internal static void Press(Window window, Control control, Point at, MouseButton button = MouseButton.Left, KeyModifiers modifiers = KeyModifiers.None, int clicks = 1)
    {
        control.RaiseEvent(new PointerPressedEventArgs(
            control,
            new Pointer(0, PointerType.Mouse, true),
            window,
            Root(window, control, at),
            0,
            new PointerPointProperties(Held(button), button == MouseButton.Middle ? PointerUpdateKind.MiddleButtonPressed : PointerUpdateKind.LeftButtonPressed),
            modifiers,
            clicks)
        {
            RoutedEvent = InputElement.PointerPressedEvent
        });
    }

    internal static void Move(Window window, Control control, Point to, MouseButton button = MouseButton.Left, KeyModifiers modifiers = KeyModifiers.None)
    {
        control.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent,
            control,
            new Pointer(0, PointerType.Mouse, true),
            window,
            Root(window, control, to),
            0,
            new PointerPointProperties(Held(button), PointerUpdateKind.Other),
            modifiers));
    }

    internal static void Release(Window window, Control control, Point at, MouseButton button = MouseButton.Left, KeyModifiers modifiers = KeyModifiers.None)
    {
        control.RaiseEvent(new PointerReleasedEventArgs(
            control,
            new Pointer(0, PointerType.Mouse, true),
            window,
            Root(window, control, at),
            0,
            new PointerPointProperties(RawInputModifiers.None, button == MouseButton.Middle ? PointerUpdateKind.MiddleButtonReleased : PointerUpdateKind.LeftButtonReleased),
            modifiers,
            button)
        {
            RoutedEvent = InputElement.PointerReleasedEvent
        });

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Carries <paramref name="carried"/> to a point of the window and lets it go, walking the drag through in full.</summary>
    /// <remarks>Only the move over a target works out where the drop would land, and a drop that never moved lands nowhere.</remarks>
    /// <param name="keys">The modifier keys held throughout, as ⌥ is to make a mask of a shape.</param>
    internal static void Drop(Window window, Point at, IDataTransfer carried, DragDropEffects effects = DragDropEffects.Copy, RawInputModifiers keys = RawInputModifiers.None)
    {
        foreach (var stage in new[] { RawDragEventType.DragEnter, RawDragEventType.DragOver, RawDragEventType.Drop })
        {
            window.DragDrop(at, stage, carried, effects, keys);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static RawInputModifiers Held(MouseButton button)
        => button == MouseButton.Middle ? RawInputModifiers.MiddleMouseButton : RawInputModifiers.LeftMouseButton;

    private static Point Root(Window window, Control control, Point at)
        => control.TranslatePoint(at, window) ?? throw new InvalidOperationException("The control is not in the window.");
}
