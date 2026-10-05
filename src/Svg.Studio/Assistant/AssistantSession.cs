// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace Svg.Studio;

/// <summary>One conversation with one model, and what it is told before the first word.</summary>
/// <remarks>
/// The history is only ever appended to. A model that thinks binds its thinking to the conversation it
/// was in, and an edited history is one it refuses to read back; a turn that fails part way is taken
/// off whole instead, since a question left without its answer is not a history either.
/// </remarks>
public sealed class AssistantSession : IDisposable
{
    /// <remarks>
    /// Worded for the smallest model that reads it. A 3B on-device one, told an edit was "labelled
    /// Assistant: ... in the Edit menu" and to "say what you changed", answered a request with
    /// "Assistant: Changed the fill to red" and no tool call at all; the same model with the rule
    /// below read the drawing and made the call every time.
    /// </remarks>
    private const string Instructions =
        """
        You are the assistant inside Svg Studio, a desktop editor for SVG drawings that are built into C# (SkiaSharp) code.
        You help the person use Studio, and when they ask for a change you make it with your tools.

        - Act only through tools. Never say something changed unless a tool reported it done; after your tools have run, say in a sentence what they did.
        - A request is the permission: never ask whether to proceed, just do it.
        - Answer questions from the documentation you are given, and say plainly when it does not cover something.

        """ + Rules + """

        - Reply briefly. Markdown is rendered: short paragraphs, lists, and `code` for attribute names and values.
        """;

    /// <summary>What any model driving Studio's tools has to know, the panel's or one connected over MCP.</summary>
    internal const string Rules =
        """
        - get_drawing gives the address keys set_attributes takes. Keys are positions, so read again after an edit that adds, moves or removes elements.
        - Nodes of the project are named by path from get_project, such as 0/2. Names can repeat; paths cannot.
        - Expressions are written {{ name }} inside an attribute; parameters and lets are declared in the drawing's or a group's declarations.
        - Every edit is one step the person can undo with Cmd/Ctrl+Z.
        """;

    /// <summary>Only for a model that has the save, remove and git_commit tools; a small one nagged about saving from the mention alone.</summary>
    private const string Confirming = "\n- Saving, removing and committing ask the person first; don't ask again in words.";

    private readonly AssistantTools _tools;
    private readonly List<ChatMessage> _messages = new();
    private IChatClient? _client;
    private AssistantModel? _model;
    private string? _conversation;

    public AssistantSession(AssistantTools tools)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
    }

    public AssistantModel? Model => _model;

    /// <summary>Whether the model gets the docs a section at a time and the core tools only.</summary>
    /// <remarks>Four times the docs is room for them, the tools, a drawing's source and a conversation.</remarks>
    public bool Small => _model is { } model && model.ContextTokens < AssistantDocs.EstimatedTokens * 4;

    /// <summary>Starts again with <paramref name="model"/>, forgetting the conversation so far.</summary>
    public void Use(IAssistantProvider provider, AssistantModel model)
    {
        _client?.Dispose();
        _client = provider.Create(model).AsBuilder().UseFunctionInvocation().Build();
        _model = model;

        Clear();
    }

    public void Clear()
    {
        _messages.Clear();
        _conversation = null;

        // The documentation leads, fixed for the whole conversation, so every turn after the first
        // reads it from the cache rather than paying for it again.
        _messages.Add(new ChatMessage(ChatRole.System, [new TextContent(SystemPrompt(Small)).WithCacheControl(Ttl.Ttl1h)]));
    }

    /// <summary>What a model is told before the conversation: the instructions, then the docs or their contents.</summary>
    public static string SystemPrompt(bool small)
        => small
            ? $"{Instructions}\nThe documentation, by section id (read one with read_doc):\n{AssistantDocs.Contents}"
            : $"{Instructions}{Confirming}\nThe documentation:\n\n{AssistantDocs.Whole}";

    /// <summary>Sends what the person typed, with what is open, and streams the reply's text.</summary>
    public async IAsyncEnumerable<string> SendAsync(string text, [EnumeratorCancellation] CancellationToken cancellation)
    {
        if (_client is not { } client)
        {
            throw new InvalidOperationException("No model has been chosen.");
        }

        var before = _messages.Count;

        _messages.Add(new ChatMessage(ChatRole.User, $"{text}\n\n<studio>\n{_tools.Context(Small)}\n</studio>"));

        var options = new ChatOptions
        {
            Tools = _tools.For(Small, docs: Small),
            ToolMode = ChatToolMode.Auto,
            ConversationId = _conversation
        };

        // A provider that keeps the conversation itself is sent only what is new.
        IEnumerable<ChatMessage> sending = _conversation is { } ? _messages.Skip(before) : _messages;
        var updates = new List<ChatResponseUpdate>();
        var finished = false;

        try
        {
            await foreach (var update in client.GetStreamingResponseAsync(sending, options, cancellation).ConfigureAwait(true))
            {
                updates.Add(update);

                if (update.Text is { Length: > 0 } delta)
                {
                    yield return delta;
                }
            }

            finished = true;
        }
        finally
        {
            if (finished)
            {
                _messages.AddMessages(updates);
                _conversation = updates.LastOrDefault(update => update.ConversationId is { })?.ConversationId ?? _conversation;
            }
            else
            {
                _messages.RemoveRange(before, _messages.Count - before);
            }
        }
    }

    public void Dispose() => _client?.Dispose();
}
