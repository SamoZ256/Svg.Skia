// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Microsoft.Extensions.AI;
using Beta = Anthropic.Models.Beta.Messages;

namespace Svg.Studio;

/// <summary>A model the assistant can talk to, and how much it can be told at once.</summary>
public sealed record AssistantModel(string Id, string Name, int ContextTokens);

/// <summary>Somewhere the assistant's models come from.</summary>
/// <remarks>
/// Everything past choosing one is <see cref="IChatClient"/>'s, so the tools and the conversation are
/// written once and a new provider is only this.
/// </remarks>
public interface IAssistantProvider
{
    string Id { get; }

    string Name { get; }

    /// <summary>Why the provider cannot be used on this machine now, or null when it can.</summary>
    string? Unavailable { get; }

    Task<IReadOnlyList<AssistantModel>> ModelsAsync(CancellationToken cancellation);

    IChatClient Create(AssistantModel model);
}

public static class AssistantProviders
{
    /// <summary>Settable so a test offers a scripted provider instead of the real ones.</summary>
    public static IReadOnlyList<IAssistantProvider> All { get; set; } = new IAssistantProvider[]
    {
        new ClaudeProvider()
    };
}

public sealed class ClaudeProvider : IAssistantProvider
{
    public const string DefaultModel = "claude-opus-5-5";

    private const int MaxTokens = 32000;

    /// <summary>Models the server keeps a default fallback for; asking any other for one is a 400.</summary>
    private static readonly HashSet<string> Falling = new(StringComparer.Ordinal)
    {
        "claude-fable-5-1", "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5"
    };

    /// <summary>What the listing said about each model, which the request has to match.</summary>
    private readonly Dictionary<string, (bool Adaptive, int Output)> _listed = new(StringComparer.Ordinal);

    /// <summary>The API key.</summary>
    /// <remarks>
    /// Temporary: read from the environment only until Studio's keychain lands, when this becomes
    /// <c>Keychain.Current?.Get("Svg.Studio", "anthropic")</c> with a field in Settings to write it.
    /// Settable so a test decides whether there is one.
    /// </remarks>
    public static Func<string?> ApiKey { get; set; } = () => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

    public string Id => "claude";

    public string Name => "Claude";

    public string? Unavailable => string.IsNullOrWhiteSpace(ApiKey())
        ? "Set ANTHROPIC_API_KEY in the environment Studio is started from."
        : null;

    public async Task<IReadOnlyList<AssistantModel>> ModelsAsync(CancellationToken cancellation)
    {
        var models = new List<AssistantModel>();
        var page = await Client().Models.List(cancellationToken: cancellation).ConfigureAwait(false);

        while (true)
        {
            foreach (var model in page.Items)
            {
                _listed[model.ID] = (
                    model.Capabilities?.Thinking?.Types?.Adaptive?.Supported ?? true,
                    (int)Math.Min(model.MaxTokens ?? MaxTokens, MaxTokens));

                models.Add(new AssistantModel(model.ID, model.DisplayName, (int)Math.Min(model.MaxInputTokens ?? 200_000, int.MaxValue)));
            }

            if (!page.HasNext())
            {
                return models;
            }

            page = await page.Next(cancellation).ConfigureAwait(false);
        }
    }

    public IChatClient Create(AssistantModel model)
    {
        var (adaptive, output) = _listed.TryGetValue(model.Id, out var listed) ? listed : (true, MaxTokens);

        return Client().Beta.AsIChatClient(model.Id, output, adaptive ? AnthropicThinkingMode.Adaptive : AnthropicThinkingMode.Extended)
            .AsBuilder()
            .ConfigureOptions(options => options.RawRepresentationFactory ??= _ => Falling.Contains(model.Id)
                ? new Beta::MessageCreateParams
                {
                    Model = model.Id,
                    MaxTokens = output,
                    Messages = [],

                    // A request the model declines is retried on the fallback the server picks for
                    // it, rather than coming back as a refusal the panel can only report.
                    Fallbacks = new Beta::Default(),
                    Betas = ["server-side-fallback-2026-07-01"]
                }
                : null)
            .Build();
    }

    private static AnthropicClient Client() => new() { ApiKey = ApiKey() };
}
