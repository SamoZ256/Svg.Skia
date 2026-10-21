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
using static Svg.Studio.UnitTests.ProjectGitTests;

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

    private const string Project = """
        <studio namespace="Demo.Icons">
          <drawing name="home">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#00ff00" />
            </svg>
          </drawing>
        </studio>
        """;

    /// <summary>0 = home, 1 = Badges, 1/0 = Round, a group inside it, 1/0/0 = Dot.</summary>
    private const string Nested = """
        <studio namespace="Demo.Icons">
          <drawing name="home">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#00ff00" />
            </svg>
          </drawing>
          <group name="Badges">
            <group name="Round">
              <drawing name="Dot">
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <circle cx="12" cy="12" r="6" />
                </svg>
              </drawing>
            </group>
          </group>
        </studio>
        """;

    private readonly Keychain? _was = Keychain.Current;
    private readonly MemoryKeychain _keychain = new();
    private readonly bool _enabled = StudioSettings.McpEnabled;
    private readonly int _port = StudioSettings.McpPort;
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;
    private readonly List<Window> _windows = new();
    private readonly List<string> _announced = new();

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
        Assert.True(tools["get_node"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(tools["set_attributes"].ProtocolTool.Annotations?.DestructiveHint);

        // One step on the history each, as set_attributes is.
        Assert.All(
            new[] { "insert_element", "remove_elements", "move_element", "duplicate_elements", "redo" },
            name => Assert.False(tools[name].ProtocolTool.Annotations?.DestructiveHint, name));

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
            new[] { "undo", "redo", "add_group", "add_drawing", "move", "remove", "git_commit", "insert_element", "remove_elements", "move_element", "duplicate_elements" },
            name => Assert.NotEqual(true, tools[name].ProtocolTool.Annotations?.IdempotentHint));
    }

    [AvaloniaFact]
    public async Task Claude_Code_Is_Told_Every_Client_Shares_One_Undo()
    {
        var (_, port) = await Serving();
        await using var client = await Client(port);

        Assert.Contains("shares one undo", client.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("Paths are positions", client.ServerInstructions, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The second leaves things as asked, which is what a tool marked idempotent promises: an error
    /// for it made a retry look as if the first had failed.
    /// </remarks>
    [AvaloniaFact]
    public async Task An_Edit_Asked_For_Twice_Changes_Nothing_The_Second_Time_And_Is_No_Error()
    {
        var (window, port) = await Serving();
        var viewer = window.GetVisualDescendants().OfType<SvgViewer>().First();
        await using var client = await Client(port);
        var redden = new Dictionary<string, object?>
        {
            ["key"] = "0",
            ["attributes"] = new[] { new { name = "fill", value = "#ff0000" } },
            ["summary"] = "red"
        };

        var first = await Call(client, "set_attributes", redden);
        var again = await Call(client, "set_attributes", redden);
        var picked = await Call(client, "select", new() { ["keys"] = new[] { "0" } });
        var still = await Call(client, "select", new() { ["keys"] = new[] { "0" } });

        Assert.All(new[] { first, again, picked, still }, result => Assert.NotEqual(true, result.IsError));
        Assert.StartsWith("Nothing changed", Text(again), StringComparison.Ordinal);
        Assert.Equal("Selected.", Text(still));

        var undone = await Call(client, "undo", new());

        Assert.Equal("Took back \"Claude Code: red\".", Text(undone));
        Assert.False(viewer.CanUndo);
    }

    /// <remarks>As for an element's attributes, though each of these says so on its own path rather than the one set_attributes takes.</remarks>
    [AvaloniaFact]
    public async Task A_Setting_Or_A_Block_Asked_For_Twice_Changes_Nothing_The_Second_Time_And_Is_No_Error()
    {
        var path = Path.Combine(_directory, "icons.svgstudio");

        File.WriteAllText(path, Project);

        var (_, port) = await Serving(path);
        await using var client = await Client(port);
        var rename = new Dictionary<string, object?> { ["node"] = "0", ["setting"] = "name", ["value"] = "house" };
        var declare = new Dictionary<string, object?>
        {
            ["code"] = "<e:code xmlns:e=\"https://svg.skia/expr/1.0\"><e:param name=\"tint\" type=\"color\" default=\"#ff0000\" /></e:code>",
            ["summary"] = "tint"
        };

        var renamed = await Call(client, "set_node", rename);
        var named = await Call(client, "set_node", rename);
        var declared = await Call(client, "set_declarations", declare);
        var same = await Call(client, "set_declarations", declare);

        Assert.All(new[] { renamed, named, declared, same }, result => Assert.NotEqual(true, result.IsError));
        Assert.StartsWith("Nothing changed", Text(named), StringComparison.Ordinal);
        Assert.StartsWith("Nothing changed", Text(same), StringComparison.Ordinal);
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
        Assert.Equal("No project is open.", Text(elsewhere));
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
    public async Task A_Failed_Commit_Is_Said_Back_And_Opens_Nothing()
    {
        var work = Path.Combine(_directory, "work");
        var path = Path.Combine(work, "icons.svgstudio");

        Directory.CreateDirectory(work);
        Git(work, "init", "-b", "main");
        Configure(work);
        File.WriteAllText(path, Project);
        Git(work, "add", "icons.svgstudio");
        Git(work, "commit", "-m", "Add the icons");

        var (window, port) = await Serving(path);
        await using var client = await Client(port);

        var again = await Call(client, "git_commit", new() { ["message"] = "Again" });

        Assert.True(again.IsError);
        Assert.Contains("nothing to commit", Text(again), StringComparison.Ordinal);
        Assert.Empty(_announced);
        Assert.Equal("1", Git(work, "rev-list", "--count", "HEAD").Trim());

        await Call(client, "set_attributes", new()
        {
            ["node"] = "0",
            ["key"] = "0",
            ["attributes"] = new[] { new { name = "fill", value = "#ff0000" } },
            ["summary"] = "red"
        });

        // Refused in the save before git is asked, which is where the commit writes the project.
        File.SetAttributes(path, FileAttributes.ReadOnly);

        CallToolResult unwritable;

        try
        {
            unwritable = await Call(client, "git_commit", new() { ["message"] = "Redden" });
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.True(unwritable.IsError);
        Assert.Contains("The project couldn't be saved", Text(unwritable), StringComparison.Ordinal);

        var committed = await Call(client, "git_commit", new() { ["message"] = "Redden" });

        Assert.NotEqual(true, committed.IsError);
        Assert.Equal("Committed.", Text(committed));
        Assert.Equal("2", Git(work, "rev-list", "--count", "HEAD").Trim());

        await window.ShowAsync(window.Workspace!.Document.Root.Children[0]);
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<SvgViewer>().First().SetSource("<div/>");

        var top = window.Workspace.UndoLabel;
        var refused = await Call(client, "git_commit", new() { ["message"] = "Break it" });

        Assert.True(refused.IsError);
        Assert.Contains("has to be an <svg>", Text(refused), StringComparison.Ordinal);
        Assert.Empty(_announced);
        Assert.Equal("2", Git(work, "rev-list", "--count", "HEAD").Trim());
        Assert.Equal(top, window.Workspace.UndoLabel);
        Assert.False(window.Workspace.IsEdited);
    }

    [AvaloniaFact]
    public async Task A_Commit_Is_Refused_While_A_Tab_Behind_Holds_Edits_It_Would_Leave_Out()
    {
        var work = Path.Combine(_directory, "work");
        var path = Path.Combine(work, "icons.svgstudio");

        Directory.CreateDirectory(work);
        Git(work, "init", "-b", "main");
        Configure(work);
        File.WriteAllText(path, Project);
        Git(work, "add", "icons.svgstudio");
        Git(work, "commit", "-m", "Add the icons");

        var (window, port) = await Serving(path);
        await using var client = await Client(port);
        var root = window.Workspace!.Document.Root;

        // home's tab behind the project's, so its edit is in a tab a save does not hand over; and
        // the project edited too, so the commit has something of its own to write.
        await window.ShowAsync(root.Children[0]);
        await window.ShowAsync(root);
        Dispatcher.UIThread.RunJobs();

        await Call(client, "set_attributes", new()
        {
            ["node"] = "0",
            ["key"] = "0",
            ["attributes"] = new[] { new { name = "fill", value = "#123456" } },
            ["summary"] = "darken"
        });
        await Call(client, "set_node", new() { ["node"] = "0", ["setting"] = "class", ["value"] = "Home" });

        var refused = await Call(client, "git_commit", new() { ["message"] = "Darken" });

        Assert.True(refused.IsError);
        Assert.Contains("0 home", Text(refused), StringComparison.Ordinal);
        Assert.Equal("1", Git(work, "rev-list", "--count", "HEAD").Trim());

        // The save writes what it can and says what it left, without calling that a failure.
        var saved = await Call(client, "save", new());

        Assert.NotEqual(true, saved.IsError);
        Assert.StartsWith("Saved, except 0 home", Text(saved), StringComparison.Ordinal);
        Assert.DoesNotContain("#123456", File.ReadAllText(path), StringComparison.Ordinal);

        await Call(client, "open", new() { ["node"] = "0" });

        var committed = await Call(client, "git_commit", new() { ["message"] = "Darken" });

        Assert.Equal("Committed.", Text(committed));
        Assert.Contains("#123456", Git(work, "show", "HEAD:icons.svgstudio"), StringComparison.Ordinal);

        // And the tab the commit wrote is no longer said to hold what is now in HEAD.
        Assert.DoesNotContain("Tabs holding edits", Text(await Call(client, "get_context", new())), StringComparison.Ordinal);
        Assert.Contains("0 drawing home class=\"Home\" (open)\n", Text(await Call(client, "get_project", new())), StringComparison.Ordinal);
        Assert.Empty(_announced);
    }

    [AvaloniaFact]
    public async Task A_Setting_A_Node_Cannot_Hold_Is_An_Error_That_Says_Which_It_Takes()
    {
        var path = Path.Combine(_directory, "nested.svgstudio");

        File.WriteAllText(path, Nested);

        var (window, port) = await Serving(path);
        await using var client = await Client(port);
        var workspace = window.Workspace!;

        var schema = (await client.ListToolsAsync()).Single(tool => tool.Name == "set_node").ProtocolTool.InputSchema;

        Assert.DoesNotContain("value", schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));

        var unknown = await Call(client, "set_node", new() { ["node"] = "1", ["setting"] = "colour", ["value"] = "red" });
        var projects = await Call(client, "set_node", new() { ["node"] = "1", ["setting"] = "cache", ["value"] = "lastValue" });
        var named = await Call(client, "set_node", new() { ["node"] = "", ["setting"] = "name", ["value"] = "Icons" });
        var nothing = await Call(client, "set_node", new() { ["node"] = "1", ["setting"] = "scale", ["value"] = "0" });

        Assert.All(new[] { unknown, projects, named, nothing }, result => Assert.True(result.IsError, Text(result)));
        Assert.StartsWith("1 has no setting colour;", Text(unknown), StringComparison.Ordinal);
        Assert.StartsWith("1 has no setting cache;", Text(projects), StringComparison.Ordinal);
        Assert.StartsWith("(project) has no setting name;", Text(named), StringComparison.Ordinal);
        Assert.Contains("has to be a positive number", Text(nothing), StringComparison.Ordinal);
        Assert.False(workspace.CanUndo);
        Assert.False(workspace.IsEdited);

        var cleared = await Call(client, "set_node", new() { ["node"] = "", ["setting"] = "namespace" });

        Assert.NotEqual(true, cleared.IsError);
        Assert.Null(workspace.Document.Root.Namespace);
        Assert.Empty(_announced);
    }

    [AvaloniaFact]
    public async Task A_Replacement_The_Tab_Cannot_Take_Is_An_Error_That_Says_Why()
    {
        var path = Path.Combine(_directory, "entities.svg");

        File.WriteAllText(path, AssistantTests.Entities);

        var (window, port) = await Serving(path);
        await using var client = await Client(port);
        var viewer = window.GetVisualDescendants().OfType<SvgViewer>().First();
        var before = viewer.Source;

        var refused = await Call(client, "replace_drawing", new() { ["svg"] = Drawing, ["summary"] = "replace it" });

        Assert.True(refused.IsError);
        Assert.Contains("declares entities", Text(refused), StringComparison.Ordinal);
        Assert.Equal(before, viewer.Source);
        Assert.False(viewer.CanUndo);
    }

    [AvaloniaFact]
    public async Task A_Node_Whose_Tabs_Hold_Typed_Settings_Is_Refused_Rather_Than_Asked_About()
    {
        var path = Path.Combine(_directory, "nested.svgstudio");

        File.WriteAllText(path, Nested);

        var (window, port) = await Serving(path);
        await using var client = await Client(port);
        var root = window.Workspace!.Document.Root;

        // Typed into a drawing's Project pane, and into the tab of a group inside the one removed.
        await window.ShowAsync(root.Children[0]);
        Dispatcher.UIThread.RunJobs();
        Assert.True(((GroupPanel)window.GetVisualDescendants().OfType<SvgViewer>().First().SidePanels.Single().Content).Edit("width", "40"));

        var round = ((ProjectGroup)root.Children[1]).Children[0];

        await window.ShowAsync(round);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.GetVisualDescendants().OfType<GroupPanel>().Single(panel => ReferenceEquals(panel.Node, round)).Edit("width", "5"));

        var pane = await Call(client, "remove", new() { ["node"] = "0" });
        var nested = await Call(client, "remove", new() { ["node"] = "1" });

        Assert.True(pane.IsError);
        Assert.StartsWith("0 home has edits in its tab", Text(pane), StringComparison.Ordinal);
        Assert.True(nested.IsError);
        Assert.StartsWith("1/0 Round has edits in its tab", Text(nested), StringComparison.Ordinal);
        Assert.Equal(2, root.Children.Count);
        Assert.Empty(_announced);
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
    public async Task An_Undo_Under_The_Persons_Edit_Is_Refused_And_Names_It()
    {
        var path = Path.Combine(_directory, "nested.svgstudio");

        File.WriteAllText(path, Nested);

        var (window, port) = await Serving(path);
        await using var client = await Client(port);
        var workspace = window.Workspace!;
        var root = workspace.Document.Root;

        var moved = await Call(client, "move", new() { ["node"] = "0", ["into"] = "1" });

        Assert.NotEqual(true, moved.IsError);
        Assert.Equal("Claude Code: move home", workspace.UndoLabel);

        // The person drags Dot out to the project.
        Assert.True(window.Move(((ProjectGroup)((ProjectGroup)root.Children[0]).Children[0]).Children[0], root));

        var buried = await Call(client, "undo", new());
        var still = await Call(client, "undo", new());

        Assert.True(buried.IsError);
        Assert.Contains("\"move Dot\"", Text(buried), StringComparison.Ordinal);
        Assert.Equal(Text(buried), Text(still));
        Assert.Equal("move Dot", workspace.UndoLabel);

        Assert.True(workspace.Undo());

        var undone = await Call(client, "undo", new());
        var same = await Call(client, "set_attributes", new()
        {
            ["node"] = "0",
            ["key"] = "0",
            ["attributes"] = new[] { new { name = "fill", value = "#00ff00" } },
            ["summary"] = "green"
        });

        Assert.NotEqual(true, undone.IsError);
        Assert.Equal("Took back \"Claude Code: move home\".", Text(undone));
        Assert.Equal(new[] { "home", "Badges" }, root.Children.Select(child => child.Name));
        Assert.NotEqual(true, same.IsError);
        Assert.StartsWith("Nothing changed", Text(same), StringComparison.Ordinal);
        Assert.Empty(_announced);
    }

    [AvaloniaFact]
    public async Task An_Element_Is_Added_Moved_Taken_Back_And_Put_Back_And_A_Refusal_Is_An_Error()
    {
        var (window, port) = await Serving();
        var viewer = window.GetVisualDescendants().OfType<SvgViewer>().First();
        await using var client = await Client(port);

        var added = await Call(client, "insert_element", new() { ["target"] = "(root)", ["markup"] = "<circle cx=\"12\" cy=\"12\" r=\"4\" />", ["summary"] = "add a dot" });
        var moved = await Call(client, "move_element", new() { ["key"] = "1", ["target"] = "0", ["where"] = "before", ["summary"] = "dot first" });
        var first = await Call(client, "get_element", new() { ["key"] = "0" });

        Assert.All(new[] { added, moved }, result => Assert.NotEqual(true, result.IsError));
        Assert.Contains("It is at 1", Text(added), StringComparison.Ordinal);
        Assert.Contains("It is now at 0", Text(moved), StringComparison.Ordinal);
        Assert.StartsWith("<circle> at 0\n", Text(first), StringComparison.Ordinal);

        var refusals = new[]
        {
            await Call(client, "move_element", new() { ["key"] = "(root)", ["target"] = "0", ["summary"] = "x" }),
            await Call(client, "insert_element", new() { ["target"] = "0", ["markup"] = "<oops", ["summary"] = "x" }),
            await Call(client, "remove_elements", new() { ["keys"] = new[] { "(root)" }, ["summary"] = "x" }),
            await Call(client, "duplicate_elements", new() { ["keys"] = new[] { "7" }, ["summary"] = "x" }),
            await Call(client, "move_element", new() { ["key"] = "0", ["target"] = "1", ["where"] = "beside", ["summary"] = "x" })
        };

        Assert.All(refusals, result => Assert.True(result.IsError, Text(result)));
        Assert.Equal("The drawing itself cannot be moved.", Text(refusals[0]));
        Assert.Equal("Claude Code: dot first", viewer.UndoLabel);

        var undone = await Call(client, "undo", new());
        var redone = await Call(client, "redo", new());
        var none = await Call(client, "redo", new());

        Assert.Equal("Took back \"Claude Code: dot first\".", Text(undone));
        Assert.NotEqual(true, redone.IsError);
        Assert.Equal("Put back \"Claude Code: dot first\".", Text(redone));
        Assert.True(none.IsError);
        Assert.Equal("There is nothing to put back.", Text(none));
        Assert.Equal("Claude Code: dot first", viewer.UndoLabel);
        Assert.Empty(_announced);
    }

    [AvaloniaFact]
    public async Task An_Element_Edit_To_A_Drawing_In_No_Tab_Is_The_Projects_And_A_Refusal_Leaves_No_Step()
    {
        var path = Path.Combine(_directory, "nested.svgstudio");

        File.WriteAllText(path, Nested);

        var (window, port) = await Serving(path);
        await using var client = await Client(port);
        var workspace = window.Workspace!;

        var copied = await Call(client, "duplicate_elements", new() { ["node"] = "0", ["keys"] = new[] { "0" }, ["summary"] = "two squares" });
        var refused = await Call(client, "remove_elements", new() { ["node"] = "0", ["keys"] = new[] { "(root)" }, ["summary"] = "x" });
        var node = await Call(client, "get_node", new() { ["node"] = "1/0/0" });

        Assert.NotEqual(true, copied.IsError);
        Assert.Contains("The copy is at 1", Text(copied), StringComparison.Ordinal);
        Assert.True(refused.IsError);
        Assert.Equal("Claude Code: two squares", workspace.UndoLabel);
        Assert.NotEqual(true, node.IsError);
        Assert.StartsWith("1/0/0 drawing Dot\n", Text(node), StringComparison.Ordinal);

        var undone = await Call(client, "undo", new());

        Assert.Equal("Took back \"Claude Code: two squares\".", Text(undone));
        Assert.False(workspace.CanUndo);
        Assert.Empty(_announced);
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

        // It counts the drawing in front's problems, not the project's.
        Assert.DoesNotContain("any problems", (await client.ListToolsAsync()).Single(tool => tool.Name == "get_context").Description, StringComparison.Ordinal);
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

    /// <summary>A window with <paramref name="path"/>, or a drawing, open and the server listening on a port of its own.</summary>
    private async Task<(MainWindow Window, int Port)> Serving(string? path = null)
    {
        StudioSettings.McpEnabled = true;
        StudioSettings.McpPort = FreePort();

        var window = await Open(path);

        // This port exactly: a window a test left holding edits is still listening on its own.
        await Until(() => StudioMcpServer.Status == $"Listening on 127.0.0.1:{StudioSettings.McpPort}.");

        return (window, StudioSettings.McpPort);
    }

    private async Task<MainWindow> Open(string? path = null)
    {
        if (path is null)
        {
            path = Path.Combine(_directory, "drawing.svg");
            File.WriteAllText(path, Drawing);
        }

        // Never closed, as nobody closes one for a client: a call that opens a dialog hangs until Call gives up.
        var window = new MainWindow
        {
            Announce = (title, _) =>
            {
                _announced.Add(title);
                return new TaskCompletionSource().Task;
            }
        };

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
