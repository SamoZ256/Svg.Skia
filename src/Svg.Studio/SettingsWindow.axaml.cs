// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Svg.Viewer.Skia.Avalonia;

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

        Theme = this.FindControl<ComboBox>("ThemeBox")!;
        Theme.ItemsSource = s_themes.Select(theme => theme.Said).ToList();
        Theme.SelectedIndex = Array.FindIndex(s_themes, theme => theme.Theme == StudioSettings.Theme);

        // Applied here and not left to the host to re-read afterwards, which is what every other
        // setting on this window does. This window is modal: a theme applied once it closed would
        // be one nobody could see themselves picking.
        Theme.SelectionChanged += (_, _) =>
        {
            if (Theme.SelectedIndex >= 0)
            {
                StudioSettings.Theme = s_themes[Theme.SelectedIndex].Theme;

                Repaint();
            }
        };

        Autosave = this.FindControl<CheckBox>("AutosaveBox")!;
        Autosave.IsChecked = StudioSettings.Autosave;
        Autosave.IsCheckedChanged += (_, _) => StudioSettings.Autosave = Autosave.IsChecked is true;

        CaptionSize = this.FindControl<NumericUpDown>("CaptionSizeBox")!;
        CaptionSize.Minimum = (decimal)SvgViewerCanvas.MinimumCaptionSize;
        CaptionSize.Maximum = (decimal)SvgViewerCanvas.MaximumCaptionSize;
        CaptionSize.Value = (decimal)StudioSettings.CaptionSize;

        // Only a value: the box empties itself while somebody is part way through typing one, and
        // writing that would be writing a setting nobody asked for.
        CaptionSize.ValueChanged += (_, _) =>
        {
            if (CaptionSize.Value is { } size)
            {
                StudioSettings.CaptionSize = (double)size;
            }
        };

        RelaxedText = this.FindControl<ComboBox>("RelaxedTextBox")!;
        RelaxedText.ItemsSource = s_answers.Select(answer => answer.Said).ToList();
        RelaxedText.SelectedIndex = Array.FindIndex(s_answers, answer => answer.Answer == StudioSettings.RelaxedText);
        RelaxedText.SelectionChanged += (_, _) =>
        {
            if (RelaxedText.SelectedIndex >= 0)
            {
                StudioSettings.RelaxedText = s_answers[RelaxedText.SelectedIndex].Answer;
            }
        };
    }

    /// <summary>Puts the theme now on file onto the application, wherever the window is shown.</summary>
    /// <remarks>
    /// The variant is the application's rather than a window's, so this reaches the editor behind
    /// this window as well as this one. <see cref="Application.Current"/> because a settings window
    /// belongs to whichever application opened it, including the one a test builds.
    /// </remarks>
    public static void Repaint()
    {
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = StudioSettings.Variant;
        }
    }

    /// <summary>The three themes, in the order the list shows them.</summary>
    /// <remarks>
    /// Following the machine first, because it is the answer somebody who has not thought about it
    /// wants and the one this starts on.
    /// </remarks>
    private static readonly (StudioTheme Theme, string Said)[] s_themes =
    {
        (StudioTheme.System, "Same as system"),
        (StudioTheme.Light, "Light"),
        (StudioTheme.Dark, "Dark")
    };

    /// <summary>The three answers, in the order the list shows them.</summary>
    /// <remarks>
    /// Said as what happens rather than as on and off: "off" for a setting called relaxed layout
    /// reads as though nothing happens, and what happens is that the drawing keeps its default.
    /// </remarks>
    private static readonly (RelaxedTextAnswer Answer, string Said)[] s_answers =
    {
        (RelaxedTextAnswer.Ask, "Ask each time"),
        (RelaxedTextAnswer.Always, "Relax the layout"),
        (RelaxedTextAnswer.Never, "Keep the default text")
    };

    /// <summary>The list of themes, for a test to drive.</summary>
    /// <remarks>
    /// <c>new</c> because a Window already has a Theme, which is its control template. Named for
    /// what it is here rather than renamed around the collision: the other three boxes on this
    /// window are named after their setting, and this is the setting's box.
    /// </remarks>
    public new ComboBox Theme { get; }

    /// <summary>The box for the recovery copy, for a test to drive.</summary>
    public CheckBox Autosave { get; }

    /// <summary>The box for how big a caption is drawn, for a test to drive.</summary>
    public NumericUpDown CaptionSize { get; }

    /// <summary>The list of answers about text an export cannot write out, for a test to drive.</summary>
    public ComboBox RelaxedText { get; }

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
