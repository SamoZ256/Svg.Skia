// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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

        CaptionSize = Stepped(
            "CaptionSizeBox",
            SvgViewerCanvas.MinimumCaptionSize,
            SvgViewerCanvas.MaximumCaptionSize,
            StudioSettings.CaptionSize);

        // Only a value: the box empties itself while somebody is part way through typing one, and
        // writing that would be writing a setting nobody asked for.
        CaptionSize.ValueChanged += (_, _) =>
        {
            if (CaptionSize.Value is { } size)
            {
                StudioSettings.CaptionSize = (double)size;
            }
        };

        DrawingCaptions = this.FindControl<CheckBox>("DrawingCaptionsBox")!;
        DrawingCaptions.IsChecked = StudioSettings.DrawingCaptions;
        DrawingCaptions.IsCheckedChanged += (_, _) =>
            StudioSettings.DrawingCaptions = DrawingCaptions.IsChecked is true;

        SnapToGrid = this.FindControl<CheckBox>("SnapToGridBox")!;
        SnapToGrid.IsChecked = StudioSettings.SnapToGrid;
        SnapToGrid.IsCheckedChanged += (_, _) => StudioSettings.SnapToGrid = SnapToGrid.IsChecked is true;

        GridSize = Stepped("GridSizeBox", SvgViewerGrid.MinimumStep, SvgViewerGrid.MaximumStep, StudioSettings.GridSize);
        GridSize.ValueChanged += (_, _) =>
        {
            if (GridSize.Value is { } step)
            {
                StudioSettings.GridSize = (double)step;
            }
        };

        RotationStep = Stepped("RotationStepBox", SvgViewerGrid.MinimumTurn, SvgViewerGrid.MaximumTurn, StudioSettings.RotationStep);
        RotationStep.ValueChanged += (_, _) =>
        {
            if (RotationStep.Value is { } turn)
            {
                StudioSettings.RotationStep = (double)turn;
            }
        };

        ResetLayout = this.FindControl<Button>("ResetLayoutButton")!;

        // Written rather than cleared, so the line on file is the arrangement rather than a gap
        // meaning "whatever the default happens to be next time".
        ResetLayout.Click += (_, _) =>
        {
            StudioSettings.Layout = StudioSettings.DefaultLayout;

            Applied?.Invoke();
        };

        ConvertAsks = this.FindControl<CheckBox>("ConvertAsksBox")!;
        ConvertAsks.IsChecked = StudioSettings.ConvertAsks;
        ConvertAsks.IsCheckedChanged += (_, _) => StudioSettings.ConvertAsks = ConvertAsks.IsChecked is true;

        ConvertIntegers = this.FindControl<CheckBox>("ConvertIntegersBox")!;
        ConvertIntegers.IsChecked = StudioSettings.ConvertIntegers;
        ConvertIntegers.IsCheckedChanged += (_, _) => StudioSettings.ConvertIntegers = ConvertIntegers.IsChecked is true;

        ConvertOrganizes = this.FindControl<CheckBox>("ConvertOrganizesBox")!;
        ConvertOrganizes.IsChecked = StudioSettings.ConvertOrganizes;
        ConvertOrganizes.IsCheckedChanged += (_, _) => StudioSettings.ConvertOrganizes = ConvertOrganizes.IsChecked is true;

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

        StreamlineKey = this.FindControl<TextBox>("StreamlineKeyBox")!;
        ForgetStreamlineKey = this.FindControl<Button>("ForgetStreamlineKeyButton")!;
        StreamlineKeyStatus = this.FindControl<TextBlock>("StreamlineKeyStatusText")!;
        AnthropicKey = this.FindControl<TextBox>("AnthropicKeyBox")!;
        ForgetAnthropicKey = this.FindControl<Button>("ForgetAnthropicKeyButton")!;
        AnthropicKeyStatus = this.FindControl<TextBlock>("AnthropicKeyStatusText")!;

        var keys = new[]
        {
            new KeyField(
                StreamlineKey,
                ForgetStreamlineKey,
                StreamlineKeyStatus,
                StreamlineClient.KeyService,
                StreamlineClient.KeyAccount,
                "Paste a key from your Streamline profile, under API keys."),
            new KeyField(
                AnthropicKey,
                ForgetAnthropicKey,
                AnthropicKeyStatus,
                ClaudeProvider.KeyService,
                ClaudeProvider.KeyAccount,
                "Paste a key from console.anthropic.com, under API keys, for the assistant.")
        };

        McpEnabled = this.FindControl<CheckBox>("McpEnabledBox")!;
        McpPort = Stepped("McpPortBox", 1024, 65535, StudioSettings.McpPort);
        CopyMcpCommand = this.FindControl<Button>("CopyMcpCommandButton")!;
        NewMcpToken = this.FindControl<Button>("NewMcpTokenButton")!;
        McpStatus = this.FindControl<TextBlock>("McpStatusText")!;

        McpEnabled.IsChecked = StudioSettings.McpEnabled;
        McpEnabled.IsCheckedChanged += (_, _) =>
        {
            StudioSettings.McpEnabled = McpEnabled.IsChecked == true;
            Served();
        };

        McpPort.ValueChanged += (_, _) =>
        {
            if (McpPort.Value is { } port)
            {
                StudioSettings.McpPort = (int)port;
                Served();
            }
        };

        CopyMcpCommand.Click += async (_, _) =>
        {
            if (McpCommand is { } command && Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(command).ConfigureAwait(true);
                McpStatus.Text = "Copied. Paste it into a terminal to add this Studio to Claude Code.";
            }
        };

        NewMcpToken.Click += (_, _) =>
        {
            Tokened(() => StudioMcpServer.NewToken());
            Served();
        };

        Served();

        // Closing by the corner or Escape does not move focus out of the box first. A key the keychain
        // refuses here holds the window open, once, so the reason is not closed along with it. Every
        // box is kept, not only the first that refuses.
        Closing += (_, e) =>
        {
            var held = false;

            foreach (var key in keys)
            {
                held |= key.HoldsOpen();
            }

            if (held && e.CloseReason == WindowCloseReason.WindowClosing)
            {
                e.Cancel = true;
            }
        };
    }

    /// <summary>The command that adds this Studio to Claude Code, or null while it is off or has no token.</summary>
    public string? McpCommand { get; private set; }

    /// <summary>Says what the server will do once this window closes, which is when the window applies it.</summary>
    private void Served()
    {
        var on = StudioSettings.McpEnabled;
        var token = on ? Tokened(() => StudioMcpServer.Token(make: true)) : null;

        McpCommand = token is { } ? StudioMcpServer.Command(StudioSettings.McpPort, token) : null;
        McpPort.IsEnabled = on;
        CopyMcpCommand.IsEnabled = McpCommand is { };
        NewMcpToken.IsEnabled = McpCommand is { };
        McpEnabled.IsEnabled = Keychain.Current is { };

        if (Keychain.Current is null)
        {
            McpStatus.Text = "Studio knows of no keychain on this machine to keep the token in.";
        }
        else if (token is { } || !on)
        {
            McpStatus.Text = on
                ? StudioMcpServer.Status is { } status && status.Contains($":{StudioSettings.McpPort}.", StringComparison.Ordinal)
                    ? status + " Copy the command to add it to Claude Code."
                    : $"Served on 127.0.0.1:{StudioSettings.McpPort} when Settings closes. Copy the command to add it to Claude Code."
                : StudioMcpServer.Status ?? "Claude Code can drive this Studio over MCP, with the same tools as the assistant.";
        }
    }

    /// <summary>Asks the keychain for the token, putting its refusal in the status line.</summary>
    private string? Tokened(Func<string?> ask)
    {
        try
        {
            return ask();
        }
        catch (Exception failure) when (failure is InvalidOperationException or Win32Exception)
        {
            McpStatus.Text = failure.Message;

            return null;
        }
    }

    /// <summary>A box a secret is typed into, kept in the keychain under one account and never shown back.</summary>
    private sealed class KeyField
    {
        private readonly TextBox _box;
        private readonly Button _forget;
        private readonly TextBlock _status;
        private readonly string _service;
        private readonly string _account;
        private readonly string _hint;

        /// <summary>The key last refused by the keychain, whose reason the window is already showing.</summary>
        private string? _refused;

        public KeyField(TextBox box, Button forget, TextBlock status, string service, string account, string hint)
        {
            _box = box;
            _forget = forget;
            _status = status;
            _service = service;
            _account = account;
            _hint = hint;

            _box.LostFocus += (_, _) => Keep();
            _box.KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Return)
                {
                    e.Handled = true;

                    Keep();
                }
            };

            _forget.Click += (_, _) => Keyed(keychain => keychain.Remove(_service, _account));

            Keyed(null);
        }

        /// <summary>Keeps what the box holds, and says whether a refusal not yet seen should hold the window open.</summary>
        public bool HoldsOpen()
        {
            var shown = _refused is not null && _refused == _box.Text?.Trim();

            return !Keep() && !shown;
        }

        /// <summary>Writes a key typed into the box to the keychain, emptying the box once it is kept there.</summary>
        /// <returns>False when the keychain refused it, which leaves it in the box.</returns>
        private bool Keep()
        {
            if (_box.Text?.Trim() is not { Length: > 0 } key)
            {
                return true;
            }

            if (!Keyed(keychain => keychain.Set(_service, _account, key)))
            {
                _refused = key;

                return false;
            }

            _box.Text = "";

            return true;
        }

        /// <summary>Makes a change to the keychain, then says whether a key is now kept there.</summary>
        /// <returns>False when the keychain refused, with its reason in place of the status.</returns>
        private bool Keyed(Action<Keychain>? change)
        {
            var keychain = Keychain.Current;
            var stored = false;

            try
            {
                if (keychain is not null)
                {
                    change?.Invoke(keychain);

                    stored = keychain.Get(_service, _account) is not null;
                }

                _status.Text = keychain is null
                    ? "Studio knows of no keychain on this machine to keep a key in."
                    : stored
                        ? "A key is kept in this machine's keychain. Typing another replaces it."
                        : _hint + " It is kept in this machine's keychain rather than in Studio's settings.";
            }
            catch (Exception failure) when (failure is InvalidOperationException or ArgumentException or Win32Exception)
            {
                _status.Text = failure.Message;

                return false;
            }
            finally
            {
                _box.IsEnabled = keychain is not null;
                _forget.IsEnabled = stored;
            }

            return true;
        }
    }

    /// <summary>Puts the panels back where they started.</summary>
    public Button ResetLayout { get; }

    /// <summary>
    /// Asked when something changed here has to reach what is already open, before this closes.
    /// </summary>
    /// <remarks>
    /// Everything else on this window is read again when it closes, which is soon enough for a grid
    /// size and much too late for the panels moving: a button that appears to do nothing is a button
    /// nobody presses twice. The theme has the same problem and answers it the same way, by applying
    /// itself rather than waiting. What "apply" means is the host's, not this window's.
    /// </remarks>
    public Action? Applied { get; set; }

    /// <summary>A box holding a number between two bounds, seeded from what is on file.</summary>
    /// <remarks>
    /// Three boxes on this window are that, and the bounds have to be the ones the setting itself
    /// falls back on — a box that will take a number the setting then refuses is a box that silently
    /// does nothing.
    /// </remarks>
    private NumericUpDown Stepped(string name, double least, double most, double value)
    {
        var box = this.FindControl<NumericUpDown>(name)!;

        box.Minimum = (decimal)least;
        box.Maximum = (decimal)most;
        box.Value = (decimal)value;

        return box;
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

    /// <summary>The box for whether a drawing is named under it, for a test to drive.</summary>
    public CheckBox DrawingCaptions { get; }

    /// <summary>The box for whether a gesture lands on the grid, for a test to drive.</summary>
    public CheckBox SnapToGrid { get; }

    /// <summary>The box for how far apart the grid lines are, for a test to drive.</summary>
    public NumericUpDown GridSize { get; }

    /// <summary>The box for how far apart the angles a turn lands on are, for a test to drive.</summary>
    public NumericUpDown RotationStep { get; }

    /// <summary>The list of answers about text an export cannot write out, for a test to drive.</summary>
    public ComboBox RelaxedText { get; }

    /// <summary>The box for whether a PaintCode document is asked about, for a test to drive.</summary>
    public CheckBox ConvertAsks { get; }

    /// <summary>The box for whether a conversion writes integers, for a test to drive.</summary>
    public CheckBox ConvertIntegers { get; }

    /// <summary>The box for whether a conversion organizes variables, for a test to drive.</summary>
    public CheckBox ConvertOrganizes { get; }

    /// <summary>The box a Streamline API key is typed into, for a test to drive.</summary>
    public TextBox StreamlineKey { get; }

    /// <summary>Takes the Streamline API key out of the keychain.</summary>
    public Button ForgetStreamlineKey { get; }

    /// <summary>Whether a Streamline API key is kept, or why none can be.</summary>
    public TextBlock StreamlineKeyStatus { get; }

    /// <summary>Whether Claude Code may connect, for a test to drive.</summary>
    public CheckBox McpEnabled { get; }

    public NumericUpDown McpPort { get; }

    public Button CopyMcpCommand { get; }

    /// <summary>Replaces the token, so a command copied before stops working.</summary>
    public Button NewMcpToken { get; }

    public TextBlock McpStatus { get; }

    /// <summary>The box the assistant's Anthropic API key is typed into, for a test to drive.</summary>
    public TextBox AnthropicKey { get; }

    /// <summary>Takes the Anthropic API key out of the keychain.</summary>
    public Button ForgetAnthropicKey { get; }

    /// <summary>Whether an Anthropic API key is kept, or why none can be.</summary>
    public TextBlock AnthropicKeyStatus { get; }

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
