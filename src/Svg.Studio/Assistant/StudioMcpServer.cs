// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Svg.Studio;

/// <summary>
/// Studio's tools over MCP, for Claude Code to connect to from outside: the same functions the
/// assistant panel calls, served on this machine only, to a client holding the token.
/// </summary>
/// <remarks>
/// Asking before a save, a removal or a commit is the client's: those tools are marked destructive
/// and Studio does not ask again. Every other edit is one step on the window's history, labelled
/// "Claude Code: …" in the Edit menu.
/// </remarks>
public sealed class StudioMcpServer : IAsyncDisposable
{
    public const string Name = "svg-studio";

    /// <summary>Where the token is kept in the <see cref="Keychain"/>, beside the API keys.</summary>
    public const string TokenService = "Svg.Studio";

    public const string TokenAccount = "Studio MCP token";

    private const string Instructions =
        "These tools read and edit what is open in Svg Studio, a desktop editor for SVG drawings that are built into C# (SkiaSharp) code.\n"
        + "- Call get_context before acting, to see the project, the tab in front and what is selected.\n"
        + "- read_doc with an empty id lists the documentation's sections; read one before answering how Studio works.\n"
        + "- Every client connected to this Studio shares one undo and redo: undo takes back the newest edit any of them made, and redo what any of them took back, so check the label each names.\n"
        + AssistantSession.Rules;

    private readonly AssistantTools _tools;

    /// <summary>Whether the call in flight reported success, which every tool that did what it was asked does and a refusal does not.</summary>
    /// <remarks>
    /// Flowing with the call rather than one field, because a client can make calls in parallel and
    /// they interleave on the UI thread; Avalonia's dispatcher runs each under the context it was
    /// invoked from.
    /// </remarks>
    private readonly AsyncLocal<StrongBox<bool>?> _reported = new();

    private readonly SemaphoreSlim _applying = new(1, 1);
    private WebApplication? _app;
    private (int Port, string Token)? _serving;

    public StudioMcpServer(MainWindow window)
    {
        _tools = new AssistantTools(window) { Confirm = _ => Task.FromResult(true), Who = "Claude Code" };
        _tools.Called += (_, _) =>
        {
            if (_reported.Value is { } reported)
            {
                reported.Value = true;
            }
        };
    }

    /// <summary>What the server is doing, or why it is not, for Settings to say; null while it is off.</summary>
    /// <remarks>Static because a Studio has one window and so one server, and Settings has no window to ask.</remarks>
    public static string? Status { get; private set; }

    /// <summary>The token a client sends, made and kept the first time one is asked for.</summary>
    /// <exception cref="InvalidOperationException">The keychain refused.</exception>
    public static string? Token(bool make)
    {
        if (Keychain.Current is not { } keychain)
        {
            return null;
        }

        var kept = keychain.Get(TokenService, TokenAccount);

        return kept is { Length: > 0 } || !make ? kept : NewToken();
    }

    /// <summary>Replaces the token, so a command copied before stops working.</summary>
    /// <exception cref="InvalidOperationException">The keychain refused.</exception>
    public static string? NewToken()
    {
        if (Keychain.Current is not { } keychain)
        {
            return null;
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        keychain.Set(TokenService, TokenAccount, token);

        return token;
    }

    /// <summary>What to paste into a terminal to add this Studio to Claude Code.</summary>
    public static string Command(int port, string token)
        => $"claude mcp add --transport http {Name} http://127.0.0.1:{port}/mcp --header \"Authorization: Bearer {token}\"";

    /// <summary>Serves what Settings now says, starting, stopping or moving only when that changed.</summary>
    /// <remarks>
    /// One at a time: a second apply arriving while a first stopped the old port saw nothing serving,
    /// and the first then started a server Settings had since switched off.
    /// </remarks>
    public async Task ApplyAsync()
    {
        await _applying.WaitAsync().ConfigureAwait(true);

        try
        {
            (int Port, string Token)? wanted = null;

            if (StudioSettings.McpEnabled)
            {
                try
                {
                    // Off the UI thread: a locked keychain keeps waiting on its prompt.
                    wanted = await Task.Run(() => Token(make: true)).ConfigureAwait(true) is { } token ? (StudioSettings.McpPort, token) : null;
                }
                catch (Exception failure) when (failure is InvalidOperationException or Win32Exception)
                {
                    await StopAsync().ConfigureAwait(true);
                    Status = failure.Message;

                    return;
                }

                if (wanted is null)
                {
                    await StopAsync().ConfigureAwait(true);
                    Status = "Studio knows of no keychain on this machine to keep the token in.";

                    return;
                }
            }

            if (wanted == _serving && (wanted is null || _app is { }))
            {
                Status = wanted is null ? null : Status;

                return;
            }

            await StopAsync().ConfigureAwait(true);

            if (wanted is not { } serving)
            {
                Status = null;

                return;
            }

            var app = Build(serving.Port, serving.Token);

            try
            {
                await app.StartAsync().ConfigureAwait(true);
            }
            catch (IOException failure)
            {
                // Kestrel says "address already in use" as an IOException; another Studio is the usual one.
                await app.DisposeAsync().ConfigureAwait(true);
                Status = $"Port {serving.Port} could not be listened on, perhaps by another Studio: {failure.Message}";

                return;
            }

            _app = app;
            _serving = serving;
            Status = $"Listening on 127.0.0.1:{serving.Port}.";
        }
        finally
        {
            _applying.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Status = null;
    }

    private async Task StopAsync()
    {
        if (_app is { } app)
        {
            _app = null;
            _serving = null;

            await app.StopAsync().ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private WebApplication Build(int port, string token)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });

        builder.Logging.ClearProviders();

        // A client's open event stream would otherwise hold a stop for the default 30 seconds.
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(1));
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = Name, Version = "1.0" };
                options.ServerInstructions = Instructions;
            })
            .WithHttpTransport()
            .WithTools(Tools());

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            if (Refused(context.Request, port, token) is { } status)
            {
                context.Response.StatusCode = status;

                return;
            }

            await next(context).ConfigureAwait(false);
        });

        app.MapMcp("/mcp");

        return app;
    }

    /// <summary>The panel's tools, and <c>get_context</c> and <c>render</c>, told which only read and which a client should ask about.</summary>
    private IEnumerable<McpServerTool> Tools()
        => _tools.For(small: false, docs: true)
            .OfType<AIFunction>()
            .Append(AIFunctionFactory.Create(
                _tools.ContextAsync,
                "get_context",
                "What is open in Studio now: the project, the tab in front, what is selected in it or picked on its board, and how many errors and warnings the drawing in front has; get_problems lists a project's."))
            .Append(AIFunctionFactory.Create(Render, new AIFunctionFactoryOptions
            {
                Name = "render",
                Description = "Draws a drawing as a PNG, to see what it looks like: at the size it is built at, or at several sizes side by side on one sheet, on transparent, light or dark, or a row on each. Values try parameters out and svg tries a whole replacement, changing nothing.",
                SerializerOptions = AssistantTools.s_arguments,

                // As it is, which the server sends on unchanged: the JSON of one would arrive as text.
                MarshalResult = (result, _, _) => new ValueTask<object?>(result)
            }))
            .Select(function => McpServerTool.Create(new Plain(function, _reported), new McpServerToolCreateOptions
            {
                ReadOnly = AssistantTools.Reading.Contains(function.Name),
                Destructive = AssistantTools.Confirming.Contains(function.Name),
                Idempotent = AssistantTools.Repeatable.Contains(function.Name),
                OpenWorld = false
            }))
            .ToList();

    /// <summary>A drawing as an image, beside what it is of; MCP's alone, since the panel's models take no image back.</summary>
    /// <remarks>
    /// Through <see cref="Plain"/> as every tool is, for the reason a binding fails with, which the
    /// SDK's own delegate tools leave out. Plain passes a result that is no string on as it is, so
    /// a refusal is marked an error here.
    /// </remarks>
    private async Task<CallToolResult> Render(
        [Description("The drawing's node path from get_project; empty for the drawing in front; a group's, with svg, for a drawing before add_drawing adds it there.")] string? node = null,
        [Description("Pixels on the drawing's longer side, such as [16, 24, 48]: up to 8 sizes from 1 to 1024, side by side; left out, the size it is built at.")] int[]? size = null,
        [Description("What it is drawn on: transparent, the default; light; dark; or both, a row on light above one on dark.")] AssistantTools.Ground background = AssistantTools.Ground.Transparent,
        [Description("Values for the parameters it declares, by name, each written as a default is: #ff0000, true, 3, 'quoted text'. A parameter left out takes what its panel opens with.")] Dictionary<string, JsonElement>? values = null,
        [Description("A complete SVG source to draw in its place, through its declarations and at its size, without applying it: a look before replace_drawing, or before add_drawing where node is a group.")] string? svg = null)
    {
        var rendered = await _tools.Render(node, size, background, values, svg).ConfigureAwait(false);
        var said = new TextContentBlock { Text = rendered.Text };

        return rendered.Png is { } png
            ? new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png"), said] }
            : new CallToolResult { IsError = true, Content = [said] };
    }

    /// <summary>A function whose string answer reaches the client as text, not as a JSON string in quotes, and a refusal as an error.</summary>
    /// <remarks>
    /// The factory's functions answer with the JSON of their result, and the server writes a JSON
    /// string out as it is: every reply came quoted, with its newlines spelled <c>\n</c>. The SDK
    /// passes on the message only of an <see cref="McpException"/>: a missing argument came back as
    /// "An error occurred invoking 'set_node'." and nothing more.
    /// </remarks>
    private sealed class Plain : DelegatingAIFunction
    {
        private readonly AsyncLocal<StrongBox<bool>?> _reported;

        public Plain(AIFunction inner, AsyncLocal<StrongBox<bool>?> reported)
            : base(inner)
        {
            _reported = reported;
        }

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var reported = _reported.Value = new StrongBox<bool>();
            object? result;

            try
            {
                result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException and not McpException)
            {
                throw new McpException(failure.Message, failure);
            }

            if (result is not JsonElement { ValueKind: JsonValueKind.String } json)
            {
                return result;
            }

            return reported.Value || AssistantTools.Reading.Contains(Name)
                ? json.GetString()
                : new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = json.GetString()! }] };
        }
    }

    /// <summary>Why a request is turned away, or null to serve it.</summary>
    /// <remarks>
    /// The Host and Origin checks are what stop a web page reaching the port through a name that
    /// resolves to this machine; the token is what stops any other program running as you.
    /// </remarks>
    internal static int? Refused(HttpRequest request, int port, string token)
    {
        if (request.Host.Port != port || request.Host.Host is not ("127.0.0.1" or "localhost"))
        {
            return StatusCodes.Status403Forbidden;
        }

        if (request.Headers.Origin.Count > 0)
        {
            return StatusCodes.Status403Forbidden;
        }

        var said = request.Headers.Authorization.ToString();

        return said.StartsWith("Bearer ", StringComparison.Ordinal)
               && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(said[7..]), Encoding.UTF8.GetBytes(token))
            ? null
            : StatusCodes.Status401Unauthorized;
    }
}
