// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Svg.Studio;

/// <summary>The chat: a provider and model to talk to, the conversation, and a box to type in.</summary>
/// <remarks>
/// Built in code like <see cref="ChangesPanel"/>, and like it knows nothing of the window: what it
/// can do there is <see cref="AssistantTools"/>'s.
/// </remarks>
public sealed class AssistantPanel : UserControl
{
    private readonly AssistantTools _tools;
    private readonly AssistantSession _session;
    private readonly ComboBox _providers = new() { FontSize = 12, MinWidth = 90 };
    private readonly ComboBox _models = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _note = new() { FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel _transcript = new() { Spacing = 8 };
    private readonly ScrollViewer _scroller;
    private readonly TextBox _input = new()
    {
        FontSize = 12,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MaxHeight = 160,
        PlaceholderText = "Ask about Studio, or ask it to do something"
    };

    private readonly Button _send = new() { Content = "Send", FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom };

    private IReadOnlyList<AssistantModel> _listed = Array.Empty<AssistantModel>();
    private CancellationTokenSource? _running;
    private SelectableTextBlock? _reply;
    private int _loading;

    public AssistantPanel(AssistantTools tools)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _session = new AssistantSession(tools);

        _tools.Confirm = Ask;
        _tools.Called += (_, summary) =>
        {
            _transcript.Children.Add(new TextBlock { Text = "· " + summary, FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
            _reply = null;
            Follow();
        };

        _providers.ItemsSource = AssistantProviders.All.Select(provider => provider.Name).ToList();
        _providers.SelectionChanged += async (_, _) => await ChooseProvider().ConfigureAwait(true);
        _models.SelectionChanged += (_, _) => ChooseModel();

        var restart = new Button { Content = "New", FontSize = 12, [ToolTip.TipProperty] = "Start a new conversation" };
        restart.Click += (_, _) => Restart();

        _send.Click += async (_, _) => await SendOrStop().ConfigureAwait(true);

        // Enter sends, Shift+Enter is a new line, as in every chat box.
        _input.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;

                if (_running is null)
                {
                    await SendOrStop().ConfigureAwait(true);
                }
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var header = new DockPanel { Margin = new Thickness(10, 10, 10, 6) };
        DockPanel.SetDock(_providers, Dock.Left);
        DockPanel.SetDock(restart, Dock.Right);
        _providers.Margin = new Thickness(0, 0, 6, 0);
        restart.Margin = new Thickness(6, 0, 0, 0);
        header.Children.Add(_providers);
        header.Children.Add(restart);
        header.Children.Add(_models);

        var input = new DockPanel { Margin = new Thickness(10, 6, 10, 10) };
        DockPanel.SetDock(_send, Dock.Right);
        _send.Margin = new Thickness(6, 0, 0, 0);
        input.Children.Add(_send);
        input.Children.Add(_input);

        _scroller = new ScrollViewer { Content = _transcript, Padding = new Thickness(10, 0, 10, 0) };

        var top = new StackPanel { Children = { header, _note } };
        _note.Margin = new Thickness(10, 0, 10, 6);

        var body = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(input, Dock.Bottom);
        body.Children.Add(top);
        body.Children.Add(input);
        body.Children.Add(_scroller);

        Content = body;

        // Chosen when first shown rather than here: listing a provider's models is a request, and a
        // window whose assistant nobody opens has no business making it.
        AttachedToVisualTree += (_, _) =>
        {
            if (_providers.SelectedIndex >= 0)
            {
                return;
            }

            var saved = AssistantProviders.All.ToList().FindIndex(provider => provider.Id == StudioSettings.AssistantProvider);
            var usable = AssistantProviders.All.ToList().FindIndex(provider => provider.Unavailable is null);

            _providers.SelectedIndex = saved >= 0 ? saved : Math.Max(usable, 0);
        };
    }

    /// <summary>Sends <paramref name="text"/> as if it had been typed, and waits for the whole reply.</summary>
    public async Task SendAsync(string text)
    {
        _input.Text = text;

        await SendOrStop().ConfigureAwait(true);
    }

    /// <summary>What the conversation shows, one row per message, activity line or question.</summary>
    public IEnumerable<string> Transcript => _transcript.Children.Select(row => row switch
    {
        TextBlock block => block.Text ?? string.Empty,
        Border { Child: StackPanel { Children: [TextBlock asked, ..] } } => asked.Text ?? string.Empty,
        _ => string.Empty
    });

    private IAssistantProvider? Provider => _providers.SelectedIndex is var at and >= 0 && at < AssistantProviders.All.Count
        ? AssistantProviders.All[at]
        : null;

    private async Task ChooseProvider()
    {
        if (Provider is not { } provider)
        {
            return;
        }

        if (StudioSettings.AssistantProvider != provider.Id)
        {
            StudioSettings.AssistantProvider = provider.Id;
        }

        var loading = ++_loading;

        _models.ItemsSource = null;
        _listed = Array.Empty<AssistantModel>();

        if (provider.Unavailable is { } why)
        {
            Say(why);
            Enable();

            return;
        }

        Say(null);

        try
        {
            var listed = await provider.ModelsAsync(CancellationToken.None).ConfigureAwait(true);

            if (loading != _loading)
            {
                return;
            }

            _listed = listed;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            if (loading == _loading)
            {
                Say($"{provider.Name}'s models could not be listed: {failure.Message}");
                Enable();
            }

            return;
        }

        _models.ItemsSource = _listed.Select(model => model.Name).ToList();

        var saved = _listed.ToList().FindIndex(model => model.Id == StudioSettings.AssistantModel);
        var preferred = _listed.ToList().FindIndex(model => model.Id == ClaudeProvider.DefaultModel);

        _models.SelectedIndex = saved >= 0 ? saved : Math.Max(preferred, 0);
    }

    private void ChooseModel()
    {
        if (Provider is not { } provider || _models.SelectedIndex is not (var at and >= 0) || at >= _listed.Count)
        {
            Enable();

            return;
        }

        StudioSettings.AssistantModel = _listed[at].Id;

        _session.Use(provider, _listed[at]);
        _transcript.Children.Clear();
        _reply = null;

        Enable();
    }

    private void Restart()
    {
        _running?.Cancel();
        _session.Clear();
        _transcript.Children.Clear();
        _reply = null;
    }

    private async Task SendOrStop()
    {
        if (_running is { } running)
        {
            running.Cancel();

            return;
        }

        if (_input.Text?.Trim() is not { Length: > 0 } text || _session.Model is null)
        {
            return;
        }

        _input.Text = string.Empty;
        _transcript.Children.Add(new SelectableTextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        _reply = null;
        _running = new CancellationTokenSource();
        Enable();
        Follow();

        try
        {
            await foreach (var delta in _session.SendAsync(text, _running.Token).ConfigureAwait(true))
            {
                if (_reply is null)
                {
                    _reply = new SelectableTextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
                    _transcript.Children.Add(_reply);
                }

                _reply.Text += delta;
                Follow();
            }
        }
        catch (OperationCanceledException)
        {
            Line("Stopped. Edits already made stay, each one an undo step.");
        }
        catch (Exception failure)
        {
            Line(failure.Message);
        }
        finally
        {
            _running.Dispose();
            _running = null;
            _reply = null;
            Enable();
        }
    }

    /// <summary>Asks in the conversation rather than in a dialog, so the question sits beside what prompted it.</summary>
    private async Task<bool> Ask(string question)
    {
        var answer = new TaskCompletionSource<bool>();
        var allow = new Button { Content = "Allow", FontSize = 12 };
        var deny = new Button { Content = "Deny", FontSize = 12 };

        allow.Click += (_, _) => answer.TrySetResult(true);
        deny.Click += (_, _) => answer.TrySetResult(false);

        // A question left open when the reply is stopped is a no.
        _running?.Token.Register(() => answer.TrySetResult(false));

        var card = new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = question, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { allow, deny } }
                }
            }
        };

        _transcript.Children.Add(card);
        _reply = null;
        Follow();

        var allowed = await answer.Task.ConfigureAwait(true);

        allow.IsEnabled = deny.IsEnabled = false;

        return allowed;
    }

    private void Line(string text)
    {
        _transcript.Children.Add(new TextBlock { Text = text, FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        Follow();
    }

    private void Say(string? note)
    {
        _note.Text = note;
        _note.IsVisible = note is { };
    }

    private void Enable()
    {
        var ready = _session.Model is { } && Provider?.Unavailable is null;

        _input.IsEnabled = ready;
        _models.IsEnabled = _running is null;
        _providers.IsEnabled = _running is null;
        _send.IsEnabled = ready;
        _send.Content = _running is null ? "Send" : "Stop";
    }

    private void Follow() => _scroller.ScrollToEnd();
}
