using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
        Assert.False(tools["set_declarations"].ProtocolTool.Annotations?.DestructiveHint);

        Assert.True(tools["save"].ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(tools["git_commit"].ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(tools["get_drawing"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(tools["set_attributes"].ProtocolTool.Annotations?.DestructiveHint);
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

        await Until(() => StudioMcpServer.Status?.StartsWith("Listening", StringComparison.Ordinal) == true);

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

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);

        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        listener.Stop();

        return port;
    }
}
