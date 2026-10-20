using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>Studio's tools served over MCP, connected to with the SDK's own client.</summary>
/// <remarks>
/// In the settings collection: the switch and the port are <see cref="StudioSettings"/>, the token
/// is <see cref="Keychain.Current"/>, and both are one static for the whole run.
/// </remarks>
[Collection("settings")]
public class StudioMcpServerTests : IDisposable
{
    private const string Drawing = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect width="24" height="24" fill="#00ff00" />
        </svg>
        """;

    private readonly Keychain? _was = Keychain.Current;
    private readonly MemoryKeychain _keychain = new();
    private readonly bool _enabled = StudioSettings.McpEnabled;
    private readonly int _port = StudioSettings.McpPort;
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;
    private readonly List<Window> _windows = new();

    public StudioMcpServerTests() => Keychain.Current = _keychain;

    public void Dispose()
    {
        foreach (var window in _windows)
        {
            // Set only now: a yes during the test let a tool call that reached the prompt throw the
            // edits away and pass, where the real modal holds the call until Call gives up on it.
            if (window is MainWindow main)
            {
                main.ConfirmDiscard = _ => Task.FromResult(true);
            }

            window.Close();
        }

        StudioSettings.McpEnabled = _enabled;
        StudioSettings.McpPort = _port;
        Keychain.Current = _was;
        Scratch.Delete(_directory);
    }

    [AvaloniaFact]
    public async Task Claude_Code_Sees_The_Tools_And_Which_Ones_To_Ask_About()
    {
        var (_, port) = await Serving();
        await using var client = await Client(port);

        var tools = (await client.ListToolsAsync()).ToDictionary(tool => tool.Name);

        Assert.Contains("set_attributes", tools.Keys);
        Assert.Contains("read_doc", tools.Keys);
        Assert.Contains("get_context", tools.Keys);
        Assert.True(tools["get_declarations"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tools["get_problems"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.Contains("set_text", tools.Keys);
        Assert.False(tools["set_declarations"].ProtocolTool.Annotations?.DestructiveHint);

        Assert.True(tools["save"].ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(tools["git_commit"].ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(tools["remove"].ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(tools["get_drawing"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(tools["set_attributes"].ProtocolTool.Annotations?.DestructiveHint);

        // Over MCP nobody but the client asks: Studio answers its own question yes.
        foreach (var name in new[] { "save", "remove", "git_commit" })
        {
            Assert.DoesNotContain("Asks the person", tools[name].Description, StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public async Task Every_Tool_Stays_On_This_Machine_And_The_Repeatable_Ones_Say_So()
    {
        var (_, port) = await Serving();
        await using var client = await Client(port);

        var tools = (await client.ListToolsAsync()).ToDictionary(tool => tool.Name);
        var repeatable = new[] { "select", "open", "set_attributes", "set_text", "set_node", "set_declarations", "replace_drawing" };

        Assert.All(tools.Values, tool => Assert.False(tool.ProtocolTool.Annotations?.OpenWorldHint, tool.Name));
        Assert.All(repeatable, name => Assert.True(tools[name].ProtocolTool.Annotations?.IdempotentHint, name));
        Assert.All(
            new[] { "undo", "add_group", "add_drawing", "move", "remove", "git_commit" },
            name => Assert.NotEqual(true, tools[name].ProtocolTool.Annotations?.IdempotentHint));
    }

    [AvaloniaFact]
    public async Task Claude_Code_Is_Told_Every_Client_Shares_One_Undo()
    {
        var (_, port) = await Serving();
        await using var client = await Client(port);

        Assert.Contains("shares one undo", client.ServerInstructions, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Refusal_Comes_Back_As_An_Error_And_A_Success_Does_Not()
    {
        var (_, port) = await Serving();
        await using var client = await Client(port);

        var elsewhere = await Call(client, "set_attributes", new()
        {
            ["key"] = "0",
            ["attributes"] = new[] { new { name = "fill", value = "red" } },
            ["summary"] = "x",
            ["node"] = "9"
        });
        var none = await Call(client, "undo", new());
        var picked = await Call(client, "select", new() { ["keys"] = new[] { "0" } });

        Assert.True(elsewhere.IsError);
        Assert.Contains("is not a drawing", Text(elsewhere), StringComparison.Ordinal);
        Assert.True(none.IsError);
        Assert.Equal("There is nothing to take back.", Text(none));
        Assert.NotEqual(true, picked.IsError);
        Assert.Equal("Selected.", Text(picked));
    }

    [AvaloniaFact]
    public async Task Calls_Made_At_Once_Are_Each_Judged_By_What_They_Did()
    {
        var (_, port) = await Serving();
        await using var client = await Client(port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // The UI thread is held while both arrive, so the success runs after the refusal has begun
        // and one box shared between calls has it mark the refusal's. Too short a pause, and they
        // only fail to overlap.
        var picked = Task.Run(() => client.CallToolAsync("select", new Dictionary<string, object?> { ["keys"] = new[] { "0" } }, cancellationToken: timeout.Token).AsTask());
        Thread.Sleep(500);
        var elsewhere = Task.Run(() => client.CallToolAsync(
            "set_attributes",
            new Dictionary<string, object?>
            {
                ["key"] = "0",
                ["attributes"] = new[] { new { name = "fill", value = "red" } },
                ["summary"] = "x",
                ["node"] = "9"
            },
            cancellationToken: timeout.Token).AsTask());
        Thread.Sleep(500);

        var results = await Task.WhenAll(picked, elsewhere);

        Assert.NotEqual(true, results[0].IsError);
        Assert.Equal("Selected.", Text(results[0]));
        Assert.True(results[1].IsError);
    }

    [AvaloniaFact]
    public async Task A_Call_That_Throws_Says_Why()
    {
        var (_, port) = await Serving();
        await using var client = await Client(port);

        var result = await Call(client, "set_attributes", new());

        Assert.True(result.IsError);
        Assert.Contains("required parameter 'key'", Text(result), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Misspelled_Argument_Is_Refused_Rather_Than_Dropped()
    {
        var (window, port) = await Serving();
        var viewer = window.GetVisualDescendants().OfType<SvgViewer>().First();
        var before = viewer.Source;
        await using var client = await Client(port);

        // Dropped, the path fell back to the drawing in front, which then took the edit.
        var result = await Call(client, "set_attributes", new()
        {
            ["key"] = "0",
            ["attributes"] = new[] { new { name = "fill", value = "red" } },
            ["summary"] = "red",
            ["nodePath"] = "0/3"
        });

        Assert.True(result.IsError);
        Assert.Contains("unexpected key 'nodePath'", Text(result), StringComparison.Ordinal);
        Assert.Equal(before, viewer.Source);
    }

    [AvaloniaFact]
    public async Task Overlapping_Applies_End_On_What_Settings_Last_Said()
    {
        StudioSettings.McpEnabled = false;

        var window = await Open();
        await using var server = new StudioMcpServer(window);

        StudioSettings.McpEnabled = true;
        StudioSettings.McpPort = FreePort();
        await server.ApplyAsync();

        var token = StudioMcpServer.Token(make: false);
        var moved = StudioSettings.McpPort = FreePort();
        var first = server.ApplyAsync();

        // Part way through, or the apply below would not overlap it.
        Assert.False(first.IsCompleted);

        StudioSettings.McpEnabled = false;
        await server.ApplyAsync();
        await first;

        Assert.Null(StudioMcpServer.Status);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => Post(moved, token));
    }

    [AvaloniaFact]
    public async Task A_Keychain_That_Cannot_Be_Started_Is_Asked_Off_The_UI_Thread_And_Said()
    {
        StudioSettings.McpEnabled = false;

        var window = await Open();
        var keychain = new Unstartable();

        Keychain.Current = keychain;
        StudioSettings.McpEnabled = true;
        StudioSettings.McpPort = FreePort();
        await using var server = new StudioMcpServer(window);

        await server.ApplyAsync();

        Assert.False(keychain.AskedOnUiThread);
        Assert.Equal(Unstartable.Said, StudioMcpServer.Status);
    }

    [AvaloniaFact]
    public async Task A_New_Token_Is_Said_To_Work_Once_Settings_Closes()
    {
        var (_, port) = await Serving();

        var settings = new SettingsWindow();
        _windows.Add(settings);
        settings.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal($"Listening on 127.0.0.1:{port}.", settings.McpStatus.Text);

        // The server still holds the old token, which the person's terminal does not.
        settings.NewMcpToken.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("once Settings closes", settings.McpStatus.Text, StringComparison.Ordinal);

        settings.CopyMcpCommand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => settings.McpStatus.Text?.StartsWith("Copied", StringComparison.Ordinal) == true);

        Assert.Contains("once Settings closes", settings.McpStatus.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task An_Edit_Is_One_Undo_Step_Named_For_Claude_Code()
    {
        var (window, port) = await Serving();
        var viewer = window.GetVisualDescendants().OfType<SvgViewer>().First();
        var before = viewer.Source;
        await using var client = await Client(port);

        var result = await client.CallToolAsync(
            "set_attributes",
            new Dictionary<string, object?>
            {
                ["key"] = "0",
                ["attributes"] = new[] { new { name = "fill", value = "#ff0000" } },
                ["summary"] = "make it red"
            });
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("fill=\"#ff0000\"", viewer.Source, StringComparison.Ordinal);
        Assert.Equal("Claude Code: make it red", viewer.UndoLabel);

        Assert.True(window.Undo());
        Assert.Equal(before, viewer.Source);
    }

    [AvaloniaFact]
    public async Task Context_And_The_Docs_Contents_Are_Tools()
    {
        var (_, port) = await Serving();
        await using var client = await Client(port);

        var context = Text(await client.CallToolAsync("get_context", new Dictionary<string, object?>()));

        // Plain text, not the JSON of a string: a quoted reply with its newlines spelled out is what every call came back as.
        Assert.False(context.StartsWith("\"", StringComparison.Ordinal), context);
        Assert.DoesNotContain("\\n", context, StringComparison.Ordinal);
        var contents = Text(await client.CallToolAsync("read_doc", new Dictionary<string, object?> { ["id"] = "" }));

        Assert.Contains("In front: the drawing", context, StringComparison.Ordinal);
        Assert.Contains("svg-studio#connecting-claude-code", contents, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Request_Without_The_Token_Or_From_A_Page_Is_Turned_Away()
    {
        var (_, port) = await Serving();
        var token = StudioMcpServer.Token(make: false)!;

        Assert.Equal(HttpStatusCode.Unauthorized, await Post(port, null));
        Assert.Equal(HttpStatusCode.Unauthorized, await Post(port, "not-the-token"));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(port, token, origin: "https://example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(port, token, host: $"rebound.example:{port}"));
    }

    [AvaloniaFact]
    public async Task Off_Nothing_Listens()
    {
        StudioSettings.McpEnabled = false;
        StudioSettings.McpPort = FreePort();

        await Open();

        Assert.Null(StudioMcpServer.Status);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => Post(StudioSettings.McpPort, "x"));
    }

    [AvaloniaFact]
    public async Task A_Port_Already_Taken_Is_Said_And_Not_Thrown()
    {
        var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();

        try
        {
            StudioSettings.McpEnabled = true;
            StudioSettings.McpPort = ((IPEndPoint)taken.LocalEndpoint).Port;

            await Open();
            await Until(() => StudioMcpServer.Status is { });

            Assert.Contains("could not be listened on", StudioMcpServer.Status, StringComparison.Ordinal);
        }
        finally
        {
            taken.Stop();
        }
    }

    [AvaloniaFact]
    public void Settings_Switches_It_And_Hands_Over_The_Command()
    {
        StudioSettings.McpEnabled = false;

        var settings = new SettingsWindow();
        _windows.Add(settings);
        settings.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(settings.McpCommand);
        Assert.False(settings.CopyMcpCommand.IsEnabled);

        settings.McpEnabled.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        var token = StudioMcpServer.Token(make: false);

        Assert.True(StudioSettings.McpEnabled);
        Assert.NotNull(token);
        Assert.Equal(StudioMcpServer.Command(StudioSettings.McpPort, token!), settings.McpCommand);
        Assert.Contains($"127.0.0.1:{StudioSettings.McpPort}/mcp", settings.McpCommand, StringComparison.Ordinal);

        // A new token is a new command; the one copied before stops working.
        settings.NewMcpToken.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(token, StudioMcpServer.Token(make: false));
        Assert.DoesNotContain(token!, settings.McpCommand, StringComparison.Ordinal);

        settings.McpEnabled.IsChecked = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(StudioSettings.McpEnabled);
        Assert.Null(settings.McpCommand);
    }

    /// <summary>A window with a drawing open and the server listening on a port of its own.</summary>
    private async Task<(MainWindow Window, int Port)> Serving()
    {
        StudioSettings.McpEnabled = true;
        StudioSettings.McpPort = FreePort();

        var window = await Open();

        // This port exactly: a window a test left holding edits is still listening on its own.
        await Until(() => StudioMcpServer.Status == $"Listening on 127.0.0.1:{StudioSettings.McpPort}.");

        return (window, StudioSettings.McpPort);
    }

    private async Task<MainWindow> Open()
    {
        var path = Path.Combine(_directory, "drawing.svg");

        File.WriteAllText(path, Drawing);

        var window = new MainWindow { Announce = (_, _) => Task.CompletedTask };

        _windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        await window.OpenAsync(new[] { path });
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    /// <summary>Lets the window's own start, which runs from its Opened handler, finish.</summary>
    private static async Task Until(Func<bool> done)
    {
        for (var tries = 0; !done() && tries < 200; tries++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(done(), $"Gave up waiting; the status is {StudioMcpServer.Status ?? "null"}.");
    }

    private static Task<McpClient> Client(int port)
        => McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + StudioMcpServer.Token(make: false) }
        }));

    /// <summary>Calls a tool, giving up after half a minute so a call that never returns fails rather than holds the run.</summary>
    private static async Task<CallToolResult> Call(McpClient client, string name, Dictionary<string, object?> arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await client.CallToolAsync(name, arguments, cancellationToken: timeout.Token);
        Dispatcher.UIThread.RunJobs();

        return result;
    }

    private static async Task<HttpStatusCode> Post(int port, string? token, string? origin = null, string? host = null)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", System.Text.Encoding.UTF8, "application/json")
        };

        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        if (token is { })
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        if (origin is { })
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (host is { })
        {
            request.Headers.Host = host;
        }

        using var response = await http.SendAsync(request);

        return response.StatusCode;
    }

    private static string Text(CallToolResult result)
        => string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    /// <summary>A keychain whose tool will not start, as when secret-tool has gone since Studio started.</summary>
    private sealed class Unstartable : Keychain
    {
        public const string Said = "No such file or directory";

        public bool? AskedOnUiThread { get; private set; }

        public override string? Get(string service, string account)
        {
            AskedOnUiThread = Dispatcher.UIThread.CheckAccess();

            throw new Win32Exception(2, Said);
        }

        public override void Set(string service, string account, string secret) => throw new Win32Exception(2, Said);

        public override void Remove(string service, string account) => throw new Win32Exception(2, Said);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);

        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        listener.Stop();

        return port;
    }
}
