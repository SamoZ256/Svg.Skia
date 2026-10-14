using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Threading;
using Xunit.v3;

[assembly: Svg.Tests.CloseWindowsAfterTest]

namespace Svg.Tests;

/// <summary>Closes every window a headless test left open, lets go of keyboard focus, and frees the
/// HarfBuzz faces of the test before.</summary>
/// <remarks>
/// Per-test isolation gives each test its own app, and Avalonia 12.1.3 keeps some of each for good: its
/// TextInputMethodManager holds the focused control from a static subscription it never drops, and a
/// disposed typeface leaves its HarfBuzz face behind. The viewer's tests peaked at 9.4 GB and Studio's at
/// 8 GB on a 7 GB macOS runner; with this and 12.1.3, 0.6 and 0.7 GB. The windows no longer keep an app,
/// but closing them still frees their surfaces at once rather than at a GC, 100 MB off the peak.
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

    // Internal and private: Avalonia 12.1.3 disposes a GlyphTypeface but not its HarfBuzz face, whose
    // callback a strong handle keeps alive, and with it pinned copies of the font's tables.
    private static readonly FieldInfo s_glyphTypefaces =
        typeof(FontCollectionBase).GetField("_glyphTypefaceCache", Any | BindingFlags.Instance)
        ?? throw new MissingMemberException(nameof(FontCollectionBase), "_glyphTypefaceCache");

    private static readonly FieldInfo s_shaper =
        typeof(GlyphTypeface).GetField("_textShaperTypeface", Any | BindingFlags.Instance)
        ?? throw new MissingMemberException(nameof(GlyphTypeface), "_textShaperTypeface");

    private static readonly List<Window> s_opened = [];

    private static IFontCollection? s_lastFonts;

    private static IDisposable? s_tracking;

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (IsHeadless(methodUnderTest))
        {
            s_tracking ??= Window.WindowOpenedEvent.AddClassHandler(typeof(Window), (sender, _) => s_opened.Add((Window)sender!));

            // The last test's faces, now that its app is gone and nothing can shape text with them.
            if (s_lastFonts is FontCollectionBase fonts && fonts != FontManager.Current.SystemFonts)
            {
                var cache = (ConcurrentDictionary<string, ConcurrentDictionary<FontCollectionKey, GlyphTypeface?>>)s_glyphTypefaces.GetValue(fonts)!;
                foreach (var typeface in cache.Values.SelectMany(family => family.Values).OfType<GlyphTypeface>().Distinct())
                {
                    (s_shaper.GetValue(typeface) as IDisposable)?.Dispose();
                }
            }

            s_lastFonts = null;
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

        s_lastFonts = FontManager.Current.SystemFonts;
    }

    // A plain [Fact] runs on the pool beside a headless test, whose windows and keyboard it must not
    // touch. Not Dispatcher.UIThread.CheckAccess(): between tests that getter makes the asking thread
    // the UI thread, and the next test's app then hangs on setup.
    private static bool IsHeadless(MethodInfo method) =>
        method.IsDefined(typeof(AvaloniaFactAttribute)) || method.IsDefined(typeof(AvaloniaTheoryAttribute));
}
