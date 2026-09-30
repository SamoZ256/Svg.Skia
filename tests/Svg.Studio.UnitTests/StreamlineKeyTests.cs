using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>The Streamline API key, from the settings window to the keychain and back.</summary>
/// <remarks>
/// In the settings collection because <see cref="Keychain.Current"/> is one static for the whole run,
/// and each test here puts a keychain of its own there and the run's back afterwards.
/// </remarks>
[Collection("settings")]
public class StreamlineKeyTests : IDisposable
{
    private readonly Keychain? _was = Keychain.Current;
    private readonly MemoryKeychain _keychain = new();

    public StreamlineKeyTests() => Keychain.Current = _keychain;

    public void Dispose() => Keychain.Current = _was;

    private static string? Kept(Keychain keychain) =>
        keychain.Get(StreamlineClient.KeyService, StreamlineClient.KeyAccount);

    private static SettingsWindow Shown()
    {
        var settings = new SettingsWindow();

        settings.Show();
        Dispatcher.UIThread.RunJobs();

        return settings;
    }

    /// <summary>Guards every other test: none of them may reach the keychain of whoever runs the suite.</summary>
    [AvaloniaFact]
    public void The_Run_Keeps_Its_Secrets_In_Memory()
    {
        Assert.IsType<MemoryKeychain>(_was);
    }

    [AvaloniaFact]
    public void A_Key_Typed_And_Entered_Is_Kept_And_Not_Shown_Back()
    {
        var settings = Shown();

        Assert.False(settings.ForgetStreamlineKey.IsEnabled);

        settings.StreamlineKey.Text = "  sk_live_abc  ";
        settings.StreamlineKey.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("sk_live_abc", Kept(_keychain));
        Assert.Equal("", settings.StreamlineKey.Text);
        Assert.True(settings.ForgetStreamlineKey.IsEnabled);
        Assert.Contains("is kept", settings.StreamlineKeyStatus.Text);

        settings.Close();

        // A second window reads the keychain rather than anything the first one held.
        var again = Shown();

        Assert.True(again.ForgetStreamlineKey.IsEnabled);
        Assert.Equal("", again.StreamlineKey.Text ?? "");

        again.ForgetStreamlineKey.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(Kept(_keychain));
        Assert.False(again.ForgetStreamlineKey.IsEnabled);

        again.Close();
    }

    [AvaloniaFact]
    public void A_Key_Left_In_The_Box_Is_Kept_When_The_Window_Closes()
    {
        var settings = Shown();

        settings.StreamlineKey.Text = "sk_live_closed";
        settings.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("sk_live_closed", Kept(_keychain));
    }

    [AvaloniaFact]
    public void An_Empty_Box_Leaves_A_Kept_Key_Alone()
    {
        _keychain.Set(StreamlineClient.KeyService, StreamlineClient.KeyAccount, "sk_live_old");

        var settings = Shown();

        settings.StreamlineKey.Text = "   ";
        settings.StreamlineKey.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        settings.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("sk_live_old", Kept(_keychain));
    }

    [AvaloniaFact]
    public void Without_A_Keychain_The_Box_Is_Off_And_Says_Why()
    {
        Keychain.Current = null;

        var settings = Shown();

        Assert.False(settings.StreamlineKey.IsEnabled);
        Assert.False(settings.ForgetStreamlineKey.IsEnabled);
        Assert.Contains("no keychain", settings.StreamlineKeyStatus.Text);

        settings.Close();
    }

    [AvaloniaFact]
    public void A_Keychain_That_Refuses_Says_What_It_Said()
    {
        Keychain.Current = new Refusing();

        var settings = Shown();

        settings.StreamlineKey.Text = "sk_live_abc";
        settings.StreamlineKey.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Writing to the keychain failed (51): locked", settings.StreamlineKeyStatus.Text);
        Assert.Equal("sk_live_abc", settings.StreamlineKey.Text);

        // The reason is already on screen, so closing is not held up to show it again.
        settings.Close();

        Assert.False(settings.IsVisible);
    }

    [AvaloniaFact]
    public void A_Key_Refused_As_The_Window_Closes_Holds_It_Open_Once()
    {
        Keychain.Current = new Refusing();

        var settings = Shown();

        settings.StreamlineKey.Text = "sk_live_abc";
        settings.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.True(settings.IsVisible);
        Assert.Equal("sk_live_abc", settings.StreamlineKey.Text);
        Assert.Equal("Writing to the keychain failed (51): locked", settings.StreamlineKeyStatus.Text);

        settings.Close();

        Assert.False(settings.IsVisible);
    }

    [AvaloniaFact]
    public void A_Key_Is_Kept_When_The_Box_Loses_Focus()
    {
        var settings = Shown();

        settings.StreamlineKey.Focus();
        settings.StreamlineKey.Text = "sk_live_focus";
        settings.Autosave.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("sk_live_focus", Kept(_keychain));
        Assert.Equal("", settings.StreamlineKey.Text);

        settings.Close();
    }

    [AvaloniaFact]
    public void A_Kept_Key_Stays_Out_Of_The_Settings_File()
    {
        var autosave = StudioSettings.Autosave;
        var settings = Shown();

        // Something the file does hold, so there is a file to look in.
        settings.Autosave.IsChecked = !autosave;
        settings.StreamlineKey.Text = "sk_live_secret";
        settings.StreamlineKey.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        settings.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("sk_live_secret", Kept(_keychain));
        Assert.DoesNotContain("sk_live_secret", File.ReadAllText(StudioSettings.Store));

        StudioSettings.Autosave = autosave;
    }

    private sealed class Refusing : Keychain
    {
        public override string? Get(string service, string account) => null;

        public override void Set(string service, string account, string secret) =>
            throw new InvalidOperationException("Writing to the keychain failed (51): locked");

        public override void Remove(string service, string account)
        {
        }
    }
}
