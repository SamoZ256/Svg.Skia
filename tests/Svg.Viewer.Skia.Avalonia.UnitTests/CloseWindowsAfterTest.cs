using System;
using System.Collections.Generic;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit.v3;

[assembly: Svg.Tests.CloseWindowsAfterTest]

namespace Svg.Tests;

/// <summary>Closes every window a headless test left open, and lets go of keyboard focus.</summary>
/// <remarks>
/// Per-test isolation gives each test its own compositor, and anything left attached keeps it: a brush
/// in a static field caches a resource per compositor, and Avalonia 12.1's TextInputMethodManager holds
/// the focused control from a static subscription it never drops. Without this the viewer's tests
/// peaked at 9.4 GB and Studio's at 8 GB, past the 7 GB of a macOS CI runner; with it, 5.9 and 3.6 GB,
/// a gigabyte of that the synthetic bold Helvetica Avalonia builds afresh and never frees.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class CloseWindowsAfterTestAttribute : BeforeAfterTestAttribute
{
    // Both are internal in Avalonia's reference assemblies; FocusManager.Focus(null) makes this same call.
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly PropertyInfo s_keyboard =
        typeof(KeyboardDevice).GetProperty("Instance", Any | BindingFlags.Static)
        ?? throw new MissingMemberException(nameof(KeyboardDevice), "Instance");

    private static readonly MethodInfo s_setFocusedElement =
        typeof(KeyboardDevice).GetMethod(
            "SetFocusedElement",
            Any | BindingFlags.Instance,
            [typeof(IInputElement), typeof(NavigationMethod), typeof(KeyModifiers), typeof(bool)])
        ?? throw new MissingMemberException(nameof(KeyboardDevice), "SetFocusedElement");

    private static readonly List<Window> s_opened = [];

    private static IDisposable? s_tracking;

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (IsHeadless(methodUnderTest))
        {
            s_tracking ??= Window.WindowOpenedEvent.AddClassHandler(typeof(Window), (sender, _) => s_opened.Add((Window)sender!));
        }
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (!IsHeadless(methodUnderTest))
        {
            return;
        }

        // Disposed rather than closed, so a window holding unsaved work goes without asking about it.
        // Work still queued runs first: a close the test called off posts its question, which throws
        // if its owner has already gone. Then oldest first, so an owner goes before its dialogs rather
        // than being activated by them, once the test class has deleted what its handlers work on.
        Dispatcher.UIThread.RunJobs();

        while (s_opened.Count > 0)
        {
            var window = s_opened[0];
            s_opened.RemoveAt(0);
            window.PlatformImpl?.Dispose();
        }

        if (s_keyboard.GetValue(null) is { } keyboard)
        {
            s_setFocusedElement.Invoke(keyboard, [null, NavigationMethod.Unspecified, KeyModifiers.None, false]);
        }
    }

    // A plain [Fact] runs on the pool beside a headless test, whose windows and keyboard it must not
    // touch. Not Dispatcher.UIThread.CheckAccess(): between tests that getter makes the asking thread
    // the UI thread, and the next test's app then hangs on setup.
    private static bool IsHeadless(MethodInfo method) =>
        method.IsDefined(typeof(AvaloniaFactAttribute)) || method.IsDefined(typeof(AvaloniaTheoryAttribute));
}
