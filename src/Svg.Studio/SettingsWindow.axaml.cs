// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace Svg.Studio;

/// <summary>What the editor has been told to do differently, on screen.</summary>
/// <remarks>
/// It reads and writes <see cref="StudioSettings"/> directly rather than answering with a value:
/// the setting is already a read-through static that outlives the window, so a window that held the
/// answer would be a second copy of it for as long as it was open. What the host has to do about a
/// setting once it changes — dropping the copies it has kept — is the host's, and it asks the
/// setting again once this closes.
/// </remarks>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        Autosave = this.FindControl<CheckBox>("AutosaveBox")!;
        Autosave.IsChecked = StudioSettings.Autosave;
        Autosave.IsCheckedChanged += (_, _) => StudioSettings.Autosave = Autosave.IsChecked is true;
    }

    /// <summary>The box for the recovery copy, for a test to drive.</summary>
    public CheckBox Autosave { get; }

    /// <inheritdoc />
    /// <remarks>
    /// There are no buttons here to carry Escape as a Cancel, and a settings window that can only be
    /// closed by its own corner is one a keyboard cannot put down.
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;

            Close();
        }

        base.OnKeyDown(e);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
