using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Svg.Editor.Core;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>The View menu's own commands: how the canvas is shown, rather than what is on it.</summary>
public partial class MainWindow
{
    /// <summary>What the window was before Full Screen, to go back to.</summary>
    private WindowState _windowed = WindowState.Normal;

    private IEnumerable<StudioCommand> Viewing(Func<Action, Func<Task>> done)
    {
        const SvgEditorShortcutModifiers primary = SvgEditorShortcutModifiers.Primary;
        const SvgEditorShortcutScope document = SvgEditorShortcutScope.Document;

        yield return new("workspace-rulers", "Rulers", "View", done(() => Setting(() => StudioSettings.Rulers = !StudioSettings.Rulers)))
        {
            Scope = document,
            Key = SvgEditorShortcutKey.R,
            Modifiers = primary,
            IsChecked = () => StudioSettings.Rulers
        };
        yield return new("guides", "Guides", "View", done(() => Setting(() => StudioSettings.Guides = !StudioSettings.Guides)))
        {
            Scope = document,
            Key = SvgEditorShortcutKey.Semicolon,
            Modifiers = primary,
            IsChecked = () => StudioSettings.Guides
        };
        yield return new("smartguides", "Smart Guides", "View", done(() => Setting(() => StudioSettings.SmartGuides = !StudioSettings.SmartGuides)))
        {
            Scope = document,
            Key = SvgEditorShortcutKey.U,
            Modifiers = primary,
            IsChecked = () => StudioSettings.SmartGuides
        };

        // The ring round what is picked, on either host: an element, a page, a group's frame.
        yield return new("fitselection", "Fit Selection", "View", done(() =>
        {
            if (FrontCanvas() is { Highlight: { } ring } canvas)
            {
                canvas.Fit(ring.Bounds);
            }
        }))
        {
            Scope = document,
            Key = SvgEditorShortcutKey.Number2,
            Modifiers = primary | SvgEditorShortcutModifiers.Shift,
            CanExecute = () => FrontCanvas() is { IsHeld: false, Highlight.Bounds: var ring } && (ring.Width > 0f || ring.Height > 0f)
        };

        // Upstream's Primary+Y, except where the platform's Redo already has it.
        var hotkeys = this.GetPlatformSettings()?.HotkeyConfiguration;
        var redoes = hotkeys?.Redo.Any(gesture => gesture.Key == Key.Y && gesture.KeyModifiers == hotkeys.CommandModifiers) == true;

        yield return new("wireframe", "Outline", "View", done(() => Setting(() => StudioSettings.Outline = !StudioSettings.Outline)))
        {
            Scope = document,
            Key = SvgEditorShortcutKey.Y,
            Modifiers = redoes ? primary | SvgEditorShortcutModifiers.Alt : primary,
            IsChecked = () => StudioSettings.Outline
        };

        foreach (var ground in Enum.GetValues<SvgViewerGround>())
        {
            yield return new($"background-{ground.ToString().ToLowerInvariant()}", $"{ground} Background", "View", done(() => Setting(() => StudioSettings.Ground = ground)))
            {
                IsChecked = () => StudioSettings.Ground == ground
            };
        }

        // A canvas key, so typing an F anywhere else is typing an F; OnCanvasKey answers it.
        yield return new("zen", "Focus Mode", "View", done(() => Panels(!_dock.ShowsRegions)))
        {
            Scope = SvgEditorShortcutScope.Canvas,
            Platform = new[] { new KeyGesture(Key.F) },
            IsChecked = () => !_dock.ShowsRegions
        };

        // AppKit adds Enter Full Screen to a menu titled View, on the same keys, so on macOS that item is the menu's.
        yield return new("fullscreen", "Full Screen", "View", done(FullScreen))
        {
            Platform = new[] { OperatingSystem.IsMacOS() ? new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Meta) : new KeyGesture(Key.F11) },
            IsChecked = () => WindowState == WindowState.FullScreen,
            InApplicationMenu = OperatingSystem.IsMacOS()
        };
    }

    /// <summary>
    /// Shows the panels again, for anything about to arrange them: arranged while hidden, they would
    /// change where nobody can see.
    /// </summary>
    /// <remarks>The dock's line is never touched by Focus Mode, so the panels come back exactly as they were.</remarks>
    private void Unfocus() => Panels(true);

    /// <summary>Shows or hides every panel, and gives the keyboard back to what had it.</summary>
    /// <remarks>
    /// The dock rebuilds round the canvas, and a control taken out of the tree loses the keyboard even
    /// when it goes straight back: F, which only a canvas answers, then needed a click to work again.
    /// </remarks>
    private void Panels(bool shown)
    {
        if (_dock.ShowsRegions == shown)
        {
            return;
        }

        var had = FocusManager?.GetFocusedElement();

        _dock.ShowsRegions = shown;

        // A box in a panel just hidden has nowhere to be, and the canvas is what is left.
        (had is Visual visual && TopLevel.GetTopLevel(visual) is { } ? had : FrontCanvas())?.Focus();
    }

    private void FullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _windowed;
        }
        else
        {
            _windowed = WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    /// <summary>A key a canvas had and left alone, which is the window's canvas commands' to answer.</summary>
    private async void OnCanvasKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Source is not SvgViewerCanvas || _registry.Resolve(e, typing: false, exported: false, canvas: true) is not { } command)
        {
            return;
        }

        e.Handled = true;

        await _registry.Run(command.Id);
    }
}
