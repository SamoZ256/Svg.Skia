using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>The assistant's Anthropic API key, kept beside the Streamline one and read by the provider.</summary>
/// <remarks>In the settings collection for the reason <see cref="StreamlineKeyTests"/> is.</remarks>
[Collection("settings")]
public class AnthropicKeyTests : IDisposable
{
    private readonly Keychain? _was = Keychain.Current;
    private readonly MemoryKeychain _keychain = new();

    public AnthropicKeyTests() => Keychain.Current = _keychain;

    public void Dispose() => Keychain.Current = _was;

    private static SettingsWindow Shown()
    {
        var settings = new SettingsWindow();

        settings.Show();
        Dispatcher.UIThread.RunJobs();

        return settings;
    }

    [AvaloniaFact]
    public void A_Key_Typed_And_Entered_Is_Kept_Under_Its_Own_Account()
    {
        var settings = Shown();

        Assert.False(settings.ForgetAnthropicKey.IsEnabled);

        settings.AnthropicKey.Text = "  sk-ant-abc  ";
        settings.AnthropicKey.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("sk-ant-abc", _keychain.Get(ClaudeProvider.KeyService, ClaudeProvider.KeyAccount));
        Assert.Null(_keychain.Get(StreamlineClient.KeyService, StreamlineClient.KeyAccount));
        Assert.Equal("", settings.AnthropicKey.Text);
        Assert.True(settings.ForgetAnthropicKey.IsEnabled);
        Assert.Contains("kept", settings.AnthropicKeyStatus.Text, StringComparison.Ordinal);

        // And the provider reads it from there, without the environment being asked.
        Assert.Equal("sk-ant-abc", ClaudeProvider.StoredKey());
        Assert.Null(new ClaudeProvider().Unavailable);

        settings.ForgetAnthropicKey.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(_keychain.Get(ClaudeProvider.KeyService, ClaudeProvider.KeyAccount));
        Assert.False(settings.ForgetAnthropicKey.IsEnabled);
    }

    [AvaloniaFact]
    public void A_Kept_Key_Stays_Out_Of_The_Settings_File()
    {
        var autosave = StudioSettings.Autosave;
        var settings = Shown();

        settings.Autosave.IsChecked = !autosave;
        settings.AnthropicKey.Text = "sk-ant-secret";
        settings.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("sk-ant-secret", _keychain.Get(ClaudeProvider.KeyService, ClaudeProvider.KeyAccount));
        Assert.DoesNotContain("sk-ant-secret", File.ReadAllText(StudioSettings.Store));

        StudioSettings.Autosave = autosave;
    }

    [AvaloniaFact]
    public void Both_Boxes_Are_Kept_When_The_Window_Closes()
    {
        var settings = Shown();

        settings.StreamlineKey.Text = "sk_live_one";
        settings.AnthropicKey.Text = "sk-ant-two";
        settings.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("sk_live_one", _keychain.Get(StreamlineClient.KeyService, StreamlineClient.KeyAccount));
        Assert.Equal("sk-ant-two", _keychain.Get(ClaudeProvider.KeyService, ClaudeProvider.KeyAccount));
    }
}
