// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
        + AssistantSession.Rules;

    private readonly AssistantTools _tools;
    private WebApplication? _app;
    private (int Port, string Token)? _serving;

    public StudioMcpServer(MainWindow window)
    {
        _tools = new AssistantTools(window) { Confirm = _ => Task.FromResult(true), Who = "Claude Code" };
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
    public async Task ApplyAsync()
    {
        (int Port, string Token)? wanted = null;

        if (StudioSettings.McpEnabled)
        {
            try
            {
                wanted = Token(make: true) is { } token ? (StudioSettings.McpPort, token) : null;
            }
            catch (InvalidOperationException failure)
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

    /// <summary>The panel's tools and <c>get_context</c>, told which only read and which a client should ask about.</summary>
    private IEnumerable<McpServerTool> Tools()
        => _tools.For(small: false, docs: true)
            .OfType<AIFunction>()
            .Append(AIFunctionFactory.Create(
                _tools.ContextAsync,
                "get_context",
                "What is open in Studio now: the project, the tab in front, the selected elements and any problems."))
            .Select(function => McpServerTool.Create(new Plain(function), new McpServerToolCreateOptions
            {
                ReadOnly = AssistantTools.Reading.Contains(function.Name),
                Destructive = AssistantTools.Confirming.Contains(function.Name)
            }))
            .ToList();

    /// <summary>A function whose string answer reaches the client as text, not as a JSON string in quotes.</summary>
    /// <remarks>
    /// The factory's functions answer with the JSON of their result, and the server writes a JSON
    /// string out as it is: every reply came quoted, with its newlines spelled <c>\n</c>.
    /// </remarks>
    private sealed class Plain : DelegatingAIFunction
    {
        public Plain(AIFunction inner)
            : base(inner)
        {
        }

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);

            return result is JsonElement { ValueKind: JsonValueKind.String } text ? text.GetString() : result;
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
