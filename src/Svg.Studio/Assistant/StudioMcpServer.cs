// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// Studio's tools over MCP, for Claude Code to connect to from outside: the functions the assistant
/// panel calls and those <see cref="Tools"/> adds, served on this machine only, to a client holding
/// the token.
/// </summary>
/// <remarks>
/// Asking before a save, an export, a removal or a commit is the client's: those tools are marked
/// destructive and Studio does not ask again. What Studio asks itself, whether to throw away work not
/// saved or how to lay out text an export cannot write out, it asks through the client. Every other
/// edit is one step on the window's history, labelled "Claude Code: …" in the Edit menu.
/// </remarks>
public sealed class StudioMcpServer : IAsyncDisposable
{
    public const string Name = "svg-studio";

    /// <summary>Where the token is kept in the <see cref="Keychain"/>, beside the API keys.</summary>
    public const string TokenService = "Svg.Studio";

    public const string TokenAccount = "Studio MCP token";

    private const string Scheme = "svg-studio://";

    /// <summary>An expression as a prompt shows one, which an interpolated raw string cannot hold as it is.</summary>
    private const string Written = "{{ name }}";

    /// <summary>How many drawings a page of the resource list holds; VectorIconsResource's project has over a thousand.</summary>
    private const int Page = 200;

    /// <summary>What a drawing is read as, by the extension ending its resource's URI.</summary>
    private static readonly (string Extension, string Mime, string Noun, string Description)[] s_kinds =
    {
        ("svg", "image/svg+xml", "source", "The drawing's SVG source as get_drawing gives it: the text in its tab where one holds it."),
        ("png", "image/png", "image", "The drawing as render draws it given nothing but the drawing: at the size it is built at, on transparent."),
        ("cs", "text/x-csharp", "C#", "The C# get_code generates for the drawing alone, whole.")
    };

    private static readonly Regex s_drawing = new(@"^svg-studio://(?:drawing/(?<path>\d+(?:/\d+)*)|front)\.(?<kind>svg|png|cs)$", RegexOptions.CultureInvariant);

    private const string Instructions =
        "These tools read and edit what is open in Svg Studio, a desktop editor for SVG drawings that are built into C# (SkiaSharp) code.\n"
        + "- Call get_context before acting, to see the project, the tab in front and what is selected.\n"
        + "- read_doc with an empty id lists the documentation's sections; read one before answering how Studio works.\n"
        + "- Every client connected to this Studio shares one undo and redo: undo takes back the newest edit any of them made, and redo what any of them took back, so check the label each names.\n"
        + "- Studio does not watch the disk: after a file it has open is changed outside it, by an edit or by git in a terminal, call reload before going on, or Studio's next save writes its copy over the change.\n"
        + "- A step that would throw away edits the person has not saved, or export text its C# cannot write out where they have not chosen for good how to lay it out, asks them through your client and goes on as they answer; where your client cannot ask, the step is refused and says why, or the text is baked.\n"
        + "- Resources: each drawing as svg-studio://drawing/<path>.svg, .png and .cs, the drawing in front as svg-studio://front.svg, .png and .cs, and the docs' sections; a drawing's path is a position, as get_project's are.\n"
        + AssistantSession.Rules
        + AssistantSession.Tooled;

    private readonly AssistantTools _tools;

    /// <summary>The call in flight.</summary>
    /// <remarks>
    /// Flowing with the call rather than one field, because a client can make calls in parallel and
    /// they interleave on the UI thread; Avalonia's dispatcher runs each under the context it was
    /// invoked from.
    /// </remarks>
    private readonly AsyncLocal<Call?> _call = new();

    private readonly SemaphoreSlim _applying = new(1, 1);
    private WebApplication? _app;
    private (int Port, string Token)? _serving;

    public StudioMcpServer(MainWindow window)
    {
        _tools = new AssistantTools(window)
        {
            Confirm = _ => Task.FromResult(true),
            Ask = Ask,
            CanAsk = CanAsk,
            Answered = () => _call.Value?.Request?.Params?.InputResponses is { Count: > 0 },
            Who = "Claude Code"
        };
        _tools.Called += (_, _) =>
        {
            if (_call.Value is { } call)
            {
                call.Reported = true;
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
            .WithTools(Tools())

            // Handlers rather than resources: a collection of them would be advertised as changing, and
            // no notification says when a drawing does.
            .WithListResourcesHandler(ListResources)
            .WithListResourceTemplatesHandler((_, _) => new ValueTask<ListResourceTemplatesResult>(new ListResourceTemplatesResult
            {
                ResourceTemplates = s_kinds.Select(kind => new ResourceTemplate
                {
                    UriTemplate = $"{Scheme}drawing/{{+path}}.{kind.Extension}",
                    Name = $"drawing.{kind.Extension}",
                    Title = $"A drawing's {kind.Noun}",
                    Description = kind.Description + " The path is a node path as get_project lists it, such as 1/0, and it is a position: a move, a remove or an undo of one renumbers the nodes after it, so list again rather than keep one.",
                    MimeType = kind.Mime
                }).ToList()
            }))
            .WithReadResourceHandler(ReadResource)
            .WithPrompts(new[]
            {
                McpServerPrompt.Create(NewDrawing, new McpServerPromptCreateOptions
                {
                    Name = "new_drawing",
                    Title = "Draw a new drawing",
                    Description = "Draws a drawing on the grid of the group it goes into, tries it at small sizes, and adds it."
                }),
                McpServerPrompt.Create(ReviewDrawing, new McpServerPromptCreateOptions
                {
                    Name = "review_drawing",
                    Title = "Review a drawing",
                    Description = "Reads a drawing and draws it small, and says what is wrong with it and how to fix each, changing nothing."
                }),
                McpServerPrompt.Create(AddParameter, new McpServerPromptCreateOptions
                {
                    Name = "add_parameter",
                    Title = "Add a parameter",
                    Description = "Turns a value of a drawing into a parameter code can set, declared with a default that keeps its look."
                })
            });

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

    /// <summary>
    /// The panel's tools, and <c>get_context</c>, <c>render</c> and the Streamline tools, told which only
    /// read, which a client should ask about, and which reach past this machine.
    /// </summary>
    private IEnumerable<McpServerTool> Tools()
        => _tools.For(small: false, docs: true)
            .OfType<AIFunction>()
            .Append(_tools.Function(_tools.ContextAsync, new AIFunctionFactoryOptions
            {
                Name = "get_context",
                Description = "What is open in Studio now: the project, the tab in front, what is selected in it or picked on its board, and how many errors and warnings the drawing in front has; get_problems lists a project's."
            }))
            .Append(_tools.Function(Render, new AIFunctionFactoryOptions
            {
                Name = "render",
                Description = "Draws a drawing as a PNG, to see what it looks like: at the size it is built at, or at several sizes side by side on one sheet, on transparent, light or dark, or a row on each. Values try parameters out, svg tries a whole replacement and revision shows it as a git commit had it, changing nothing.",

                // As it is, which the server sends on unchanged: the JSON of one would arrive as text.
                MarshalResult = (result, _, _) => new ValueTask<object?>(result)
            }))
            .Append(_tools.Function(SearchStreamline, new AIFunctionFactoryOptions
            {
                Name = "streamline_search",
                Description = "Searches Streamline's icons with the person's API key, one request against the thousand an hour it allows, and answers a sheet of their previews and a line for each with the hash streamline_import takes; one marked locked needs a plan the key does not have.",
                MarshalResult = (result, _, _) => new ValueTask<object?>(result)
            }))
            .Append(_tools.Function(_tools.ImportFromStreamline, new AIFunctionFactoryOptions
            {
                Name = "streamline_import",
                Description = "Downloads icons from Streamline by the hashes streamline_search lists and adds them at the end of a group through the project's templates, as one undo step, and opens the last; each is one download from the person's plan, and an icon the group already came from is skipped."
            }))
            .Select(function => McpServerTool.Create(new Plain(function, _call), new McpServerToolCreateOptions
            {
                Title = AssistantTools.Titles[function.Name],
                ReadOnly = AssistantTools.Reading.Contains(function.Name),
                Destructive = AssistantTools.Confirming.Contains(function.Name),
                Idempotent = AssistantTools.Repeatable.Contains(function.Name),
                OpenWorld = AssistantTools.Reaching.Contains(function.Name)
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
        [Description("Pixels on the drawing's longer side, such as [16, 24, 48]: up to 8 sizes from 1 to 1024, side by side on one sheet 8 px apart and from its edges, two rows of them for both, and the sheet at most 4096 px a side; left out, the size it is built at.")] int[]? size = null,
        [Description("What it is drawn on: transparent, the default; light; dark; or both, a row on light above one on dark.")] AssistantTools.Ground background = AssistantTools.Ground.Transparent,
        [Description("Values for the parameters it declares, by name, each written as a default is: #ff0000, true, 3, 'quoted text'. A parameter left out takes what its panel opens with.")] Dictionary<string, JsonElement>? values = null,
        [Description("A complete SVG source to draw in its place, through its declarations and at its size, without applying it: a look before replace_drawing, or before add_drawing where node is a group.")] string? svg = null,
        [Description("A commit, branch or tag, such as HEAD or a hash from git_history, to draw the drawing as git has it there, through that revision's declarations and at its size; empty for as it stands.")] string? revision = null)
    {
        var rendered = await _tools.Render(node, size, background, values, svg, revision).ConfigureAwait(false);
        var said = new TextContentBlock { Text = rendered.Text };

        return rendered.Png is { } png
            ? new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png"), said] }
            : new CallToolResult { IsError = true, Content = [said] };
    }

    /// <summary>Streamline's icons as a sheet of their previews beside a line for each, as render answers a drawing.</summary>
    private async Task<CallToolResult> SearchStreamline(
        [Description("What to look for, in words, as Streamline's own search box takes them.")] string query,
        [Description("One of Streamline's styles to search within, such as line, solid or duo; left out, any.")] string? style = null,
        [Description("Where in the results the 24 icons a call answers start: 0 for the first, then the offset the last answer says reads on.")] int offset = 0)
    {
        var found = await _tools.SearchStreamline(query, style, offset).ConfigureAwait(false);
        var said = new TextContentBlock { Text = found.Text };

        return found.Sheet is { } sheet
            ? new CallToolResult { Content = [ImageContentBlock.FromBytes(sheet, "image/png"), said] }
            : new CallToolResult { IsError = found.Refused, Content = [said] };
    }

    private static string NewDrawing(
        [Description("What to draw, in words.")] string what,
        [Description("The node path of the group it goes into, from get_project; left out, the project itself.")] string? group = null)
    {
        // render reads an empty node as the drawing in front, and (project) as the project.
        var into = string.IsNullOrWhiteSpace(group) ? "(project)" : group.Trim();

        return $"""
            Draw {what} as a new drawing in Svg Studio, in {(into == "(project)" ? "the project itself" : $"the group at node path {into}")}.

            1. Call get_context; then get_node with node {into}, for the size a drawing there is built at, and get_declarations with node {into}, for the parameters and lets it can use. read_doc svg-expressions#1-2-the-declaration-block says how they are written.
            2. Write the SVG on the grid of that size, painting through the declared parameters where they fit, as {Written}.
            3. Try it with render, node {into}, svg the drawing, size [16, 24, 48] and background both. Change what reads badly at 16 px, and render again.
            4. Add it with add_drawing, parent {(into == "(project)" ? "empty" : into)}; render the drawing it added, and run get_problems on it. Save nothing.
            """;
    }

    private static string ReviewDrawing(
        [Description("The drawing's node path, from get_project; left out, the drawing in front.")] string? node = null)
    {
        var named = string.IsNullOrWhiteSpace(node) ? null : $" with node {node.Trim()}";

        return $"""
            Review {(named is null ? "the drawing in front" : $"the drawing at node path {node!.Trim()}")} in Svg Studio, and change nothing.

            1. Read it with get_drawing{named}, which lists its problems, and render it{named} at size [16, 24, 48] on background both.
            2. Read get_code{named}, for the warnings building it gives.
            3. Say what is wrong: its problems; what is lost, or runs together, at the small sizes; ids nothing refers to, and declarations nothing uses (find with kind uses says where a name is used); and get_code's warnings.
            4. Give the edit that would fix each, and make none until asked.
            """;
    }

    private static string AddParameter(
        [Description("The value to make a parameter, in words, such as the fill of the dial.")] string what,
        [Description("The drawing's node path, from get_project; left out, the drawing in front.")] string? node = null)
    {
        var named = string.IsNullOrWhiteSpace(node) ? null : $" with node {node.Trim()}";

        return $"""
            Make {what} a parameter of {(named is null ? "the drawing in front" : $"the drawing at node path {node!.Trim()}")} in Svg Studio, so code can set it, keeping how the drawing looks.

            1. Read the drawing with get_drawing{named}, which lists the names it inherits; read_doc svg-expressions#1-2-the-declaration-block and svg-expressions#2-where-an-expression-can-go.
            2. Use an inherited parameter where one fits. Otherwise declare one with declare, giving it a type and, as its default, the value the drawing has now.
            3. Write {Written} in the value's place with set_attributes.
            4. Render it{named} with values giving the parameter two other settings, and once with none, which must look as it did before; then read get_drawing{named} again for its problems.
            """;
    }

    /// <summary>The docs and the drawing in front, then the project's drawings a page at a time, as their sources: what a person attaches with @ in Claude Code.</summary>
    private async ValueTask<ListResourcesResult> ListResources(RequestContext<ListResourcesRequestParams> request, CancellationToken cancellationToken)
    {
        var from = 0;

        // The client's to send back, so read as nothing more than the index of the next drawing.
        if (request.Params?.Cursor is { } cursor && !int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out from))
        {
            throw new McpProtocolException($"'{cursor}' is not a cursor this server gave.", McpErrorCode.InvalidParams);
        }

        var drawings = await _tools.Drawings().ConfigureAwait(false);
        var listed = new ListResourcesResult { NextCursor = from + Page < drawings.Count ? (from + Page).ToString(CultureInfo.InvariantCulture) : null };

        if (from == 0)
        {
            foreach (var section in AssistantDocs.Sections)
            {
                listed.Resources.Add(new Resource { Uri = $"{Scheme}docs/{section.Id.Replace('#', '/')}", Name = section.Id, Title = section.Title, MimeType = "text/markdown" });
            }

            foreach (var kind in s_kinds)
            {
                listed.Resources.Add(new Resource { Uri = $"{Scheme}front.{kind.Extension}", Name = $"front.{kind.Extension}", Title = $"The {kind.Noun} of the drawing in front", Description = kind.Description, MimeType = kind.Mime });
            }
        }

        foreach (var (path, title) in drawings.Skip(from).Take(Page))
        {
            listed.Resources.Add(new Resource { Uri = $"{Scheme}drawing/{path}.svg", Name = path + ".svg", Title = title, MimeType = "image/svg+xml" });
        }

        return listed;
    }

    /// <summary>A section of the docs, or a drawing as get_drawing, render or get_code would give it, whole or not at all.</summary>
    /// <remarks>A path that names no drawing is the client's mistake, so invalid; one that names a drawing the tool cannot answer for is said as the tool says it.</remarks>
    private async ValueTask<ReadResourceResult> ReadResource(RequestContext<ReadResourceRequestParams> request, CancellationToken cancellationToken)
    {
        var uri = request.Params?.Uri ?? string.Empty;

        if (uri.StartsWith(Scheme + "docs/", StringComparison.Ordinal))
        {
            // A section's id is the article's and a fragment, which a URI cannot carry as one.
            var id = uri[(Scheme.Length + "docs/".Length)..];
            var at = id.IndexOf('/', StringComparison.Ordinal);

            id = at < 0 ? id : id[..at] + "#" + id[(at + 1)..];

            return Text(AssistantDocs.Read(id) ?? throw Missing($"There is no section {id} in the docs; read_doc with an empty id lists them."), "text/markdown");
        }

        if (s_drawing.Match(uri) is not { Success: true } match)
        {
            throw Missing($"There is no resource {uri}: Studio's are svg-studio://drawing/<path>.svg, .png and .cs, svg-studio://front.svg, .png and .cs, and the docs' sections.");
        }

        var node = match.Groups["path"].Value;
        var (source, about) = await _tools.Source(node).ConfigureAwait(false);

        if (source is null)
        {
            throw Missing(about);
        }

        switch (match.Groups["kind"].Value)
        {
            case "png":
                var (png, said) = await _tools.Render(node, null, AssistantTools.Ground.Transparent, null, null, null).ConfigureAwait(false);

                return png is null ? throw new McpException(said)
                    : png.Length > 1_000_000 ? throw new McpException($"{about} comes to an image of {png.Length} bytes, more than the 1,000,000 a resource holds; render with a size draws it smaller.")
                    : new ReadResourceResult { Contents = [BlobResourceContents.FromBytes(png, uri, "image/png")] };
            case "cs":
                var (code, head, _, _) = await _tools.Code(node).ConfigureAwait(false);

                return Whole(code ?? throw new McpException(head), "text/x-csharp", "C#", "get_code reads");
            default:
                return Whole(source, "image/svg+xml", "source", "get_drawing and get_element read");
        }

        // Claude Code cuts what it is handed at 100,000 characters, and a source cut short is no drawing.
        ReadResourceResult Whole(string text, string mime, string what, string parts)
            => text.Length > 100_000
                ? throw new McpException($"{about}: its {what} is {text.Length} characters, more than the 100,000 a resource holds; {parts} it in parts.")
                : Text(text, mime);

        ReadResourceResult Text(string text, string mime) => new() { Contents = [new TextResourceContents { Uri = uri, MimeType = mime, Text = text }] };

        static McpProtocolException Missing(string why) => new(why, McpErrorCode.InvalidParams);
    }

    /// <summary>Puts a question to the person through the client, as one the call takes its answer to when it runs again; null where the client cannot be asked.</summary>
    /// <remarks>The client asks, and calls again from the start with the answer, which is why a tool asks before it changes anything.</remarks>
    private Task<AssistantTools.Answer?> Ask(AssistantTools.Question question)
    {
        if (_call.Value?.Request is not { } request)
        {
            return Task.FromResult<AssistantTools.Answer?>(null);
        }

        if (request.Params?.InputResponses?.TryGetValue(question.Key, out var given) is true)
        {
            var answered = given.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
            var choice = answered is { IsAccepted: true, Content: { } content } && content.TryGetValue("choice", out var chosen) && chosen.ValueKind == JsonValueKind.String ? chosen.GetString() : null;

            return Task.FromResult<AssistantTools.Answer?>(new AssistantTools.Answer(choice is { } && question.Choices.Contains(choice) ? choice : null));
        }

        if (!CanAsk())
        {
            return Task.FromResult<AssistantTools.Answer?>(null);
        }

        throw new InputRequiredException(new Dictionary<string, InputRequest>
        {
            [question.Key] = InputRequest.ForElicitation(new ElicitRequestParams
            {
                Message = question.Text,
                RequestedSchema = new ElicitRequestParams.RequestSchema
                {
                    Properties = { ["choice"] = new ElicitRequestParams.UntitledSingleSelectEnumSchema { Enum = question.Choices.ToList() } },
                    Required = ["choice"]
                }
            })
        });
    }

    /// <summary>Whether the client of the call in flight can be asked, or has answered already.</summary>
    /// <remarks>
    /// A request on 2026-07-28 says what its client can do in itself, which is all a server without
    /// sessions knows of it, and its client takes a question in the call's answer. On an older revision
    /// such a server knows nothing of its client and so never asks, as the SDK would fail the call; with
    /// sessions it would know, and put the question to such a client itself.
    /// </remarks>
    private bool CanAsk()
        => _call.Value?.Request is { } request
           && (request.Params?.InputResponses is { Count: > 0 } || (request.JsonRpcRequest.Context?.ClientCapabilities ?? request.Server.ClientCapabilities)?.Elicitation?.Form is { });

    /// <summary>A call: whether it reported success, which every tool that did what it was asked does and a refusal does not, and its request, to ask the person through.</summary>
    private sealed class Call
    {
        public bool Reported { get; set; }

        public RequestContext<CallToolRequestParams>? Request { get; init; }
    }

    /// <summary>A function whose string answer reaches the client as text, not as a JSON string in quotes, and a refusal as an error.</summary>
    /// <remarks>
    /// The factory's functions answer with the JSON of their result, and the server writes a JSON
    /// string out as it is: every reply came quoted, with its newlines spelled <c>\n</c>. The SDK
    /// passes on the message only of an <see cref="McpException"/>: a missing argument came back as
    /// "An error occurred invoking 'set_node'." and nothing more. A question is let through as it is,
    /// for the SDK to put to the client: wrapped, it came back as that error.
    /// </remarks>
    private sealed class Plain : DelegatingAIFunction
    {
        private readonly AsyncLocal<Call?> _call;

        public Plain(AIFunction inner, AsyncLocal<Call?> call)
            : base(inner)
        {
            _call = call;
        }

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var call = _call.Value = new Call { Request = arguments.Services?.GetService(typeof(RequestContext<CallToolRequestParams>)) as RequestContext<CallToolRequestParams> };
            object? result;

            try
            {
                result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not (OperationCanceledException or McpException or InputRequiredException))
            {
                throw new McpException(failure.Message, failure);
            }

            if (result is not JsonElement { ValueKind: JsonValueKind.String } json)
            {
                return result;
            }

            return call.Reported || AssistantTools.Reading.Contains(Name)
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
