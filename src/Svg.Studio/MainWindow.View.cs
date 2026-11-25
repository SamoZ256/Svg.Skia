using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Svg.Editor.Core;

namespace Svg.Studio;

/// <summary>The View menu's own commands: how the canvas is shown, rather than what is on it.</summary>
public partial class MainWindow
{
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
    }
}
