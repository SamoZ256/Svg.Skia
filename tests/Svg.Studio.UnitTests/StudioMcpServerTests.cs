using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SkiaSharp;
using Svg.CodeGen.Skia.Projects;
using Svg.Skia;
using Svg.Viewer.Skia.Avalonia;
using Xunit;
using static Svg.Studio.UnitTests.ProjectGitTests;
using static Svg.Studio.UnitTests.StreamlinePanelTests;

namespace Svg.Studio.UnitTests;

/// <summary>Studio's tools served over MCP, connected to with the SDK's own client.</summary>
/// <remarks>
/// In the settings collection: the switch and the port are <see cref="StudioSettings"/>, the token
/// is <see cref="Keychain.Current"/>, and template choices <see cref="TemplateLibrary.ChoicesStore"/>,
/// each one static for the whole run.
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

    /// <summary>0 = Badges, declaring tint, 0/0 = Filled, drawn in it.</summary>
    private const string Tinted = """
        <studio namespace="Demo.Icons">
          <group name="Badges">
            <e:code xmlns:e="https://svg.skia/expr/1.0">
              <e:param name="tint" type="color" default="#ff0000" />
            </e:code>
            <drawing name="Filled">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
                <rect width="24" height="24" fill="{{ tint }}" />
              </svg>
            </drawing>
          </group>
        </studio>
        """;

    /// <summary>Words placed by a span of their own, which a baked build freezes at their default and a relaxed one carries.</summary>
    private const string Badge = """
        <studio namespace="Demo.Icons">
          <drawing name="Badge">
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 64 24" width="64" height="24">
              <defs><e:code><e:param name="step" type="string" default="'1'" /></e:code></defs>
              <text x="32" y="18" font-family="Arial" font-size="12" fill="#111827"><tspan x="4" y="18">{{ step }}</tspan></text>
            </svg>
          </drawing>
        </studio>
        """;

    private static readonly SKColor s_green = new(0x00, 0xFF, 0x00);
    private static readonly SKColor s_light = new(0xF2, 0xF2, 0xF4);
    private static readonly SKColor s_dark = new(0x2E, 0x2E, 0x34);

    private readonly Keychain? _was = Keychain.Current;
    private readonly MemoryKeychain _keychain = new();
    private readonly bool _enabled = StudioSettings.McpEnabled;
    private readonly int _port = StudioSettings.McpPort;
    private readonly RelaxedTextAnswer _relaxed = StudioSettings.RelaxedText;
    private readonly string _choices = TemplateLibrary.ChoicesStore;
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;
    private readonly List<Window> _windows = new();
    private readonly List<string> _announced = new();

    public StudioMcpServerTests()
    {
        Keychain.Current = _keychain;
        TemplateLibrary.ChoicesStore = Path.Combine(_directory, "templates-choices");
    }

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
        StudioSettings.RelaxedText = _relaxed;
        TemplateLibrary.ChoicesStore = _choices;
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
        Assert.True(tools["export"].ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(tools["git_commit"].ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(tools["remove"].ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(tools["get_drawing"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tools["get_node"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tools["get_code"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(tools["set_attributes"].ProtocolTool.Annotations?.DestructiveHint);

        // Over MCP alone, since its answer is an image, or what only such an answer gives.
        Assert.True(tools["render"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(tools["render"].ProtocolTool.Annotations?.OpenWorldHint);
        Assert.True(tools["streamline_search"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.NotEqual(true, tools["streamline_import"].ProtocolTool.Annotations?.ReadOnlyHint);

        // One step on the history each, as set_attributes is; and the file tools that refuse over
        // unsaved work rather than lose it.
        Assert.All(
            new[] { "insert_element", "remove_elements", "move_element", "duplicate_elements", "redo", "new_project", "open_file", "reload", "streamline_import" },
            name => Assert.False(tools[name].ProtocolTool.Annotations?.DestructiveHint, name));

        // Over MCP nobody but the client asks: Studio answers its own question yes.
        foreach (var name in new[] { "save", "export", "remove", "git_commit" })
        {
            Assert.DoesNotContain("Asks the person", tools[name].Description, StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public async Task Only_The_Streamline_Tools_Leave_This_Machine_And_The_Repeatable_Ones_Say_So()
    {
        var (_, port) = await Serving();
        await using var client = await Client(port);

        var tools = (await client.ListToolsAsync()).ToDictionary(tool => tool.Name);
        var repeatable = new[] { "select", "open", "set_attributes", "set_text", "set_node", "set_declarations", "replace_drawing", "export", "reload", "streamline_import" };
        var reaching = new[] { "streamline_search", "streamline_import" };

        Assert.All(tools.Values, tool => Assert.True(tool.ProtocolTool.Annotations?.OpenWorldHint == reaching.Contains(tool.Name), tool.Name));
        Assert.All(repeatable, name => Assert.True(tools[name].ProtocolTool.Annotations?.IdempotentHint, name));
        Assert.All(
            new[] { "undo", "redo", "add_group", "add_drawing", "move", "remove", "git_commit", "insert_element", "remove_elements", "move_element", "duplicate_elements", "new_project", "open_file" },
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

        // Taking none, get_context answered one given as though it had narrowed what it said.
        var context = await Call(client, "get_context", new() { ["node"] = "0" });

        Assert.True(context.IsError);
        Assert.Contains("unexpected key 'node'", Text(context), StringComparison.Ordinal);
    }

    /// <remarks>A row the Elements filter hides is selected all the same, so only a selection left as it was is an error.</remarks>
    [AvaloniaFact]
    public async Task A_Selection_The_Filter_Partly_Hides_Is_Made_And_One_It_Wholly_Hides_Says_So()
    {
        var path = Path.Combine(_directory, "dots.svg");

        File.WriteAllText(path, """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <circle cx="12" cy="12" r="4" />
              <rect width="24" height="24" />
            </svg>
            """);

        var (window, port) = await Serving(path);
        var viewer = window.GetVisualDescendants().OfType<SvgViewer>().First();
        await using var client = await Client(port);

        viewer.Elements.Filter = "circle";
        Dispatcher.UIThread.RunJobs();

        var both = await Call(client, "select", new() { ["keys"] = new[] { "0", "1" } });
        var hidden = await Call(client, "select", new() { ["keys"] = new[] { "1" } });

        Assert.NotEqual(true, both.IsError);
        Assert.Equal("Selected.", Text(both));
        Assert.True(hidden.IsError);
        Assert.Contains("filter", Text(hidden), StringComparison.Ordinal);
        Assert.Equal(new[] { "0", "1" }, viewer.Elements.SelectedAddresses);
    }

    /// <remarks>As for an edit asked for twice: what was asked holds already, which a retry after a lost answer finds.</remarks>
    [AvaloniaFact]
    public async Task A_Move_Into_The_Group_A_Node_Is_In_Changes_Nothing_And_Is_No_Error()
    {
        var path = Path.Combine(_directory, "nested.svgstudio");

        File.WriteAllText(path, Nested);

        var (window, port) = await Serving(path);
        await using var client = await Client(port);

        var there = await Call(client, "move", new() { ["node"] = "1/0", ["into"] = "1" });
        var inside = await Call(client, "move", new() { ["node"] = "1", ["into"] = "1/0" });

        Assert.NotEqual(true, there.IsError);
        Assert.StartsWith("Nothing changed", Text(there), StringComparison.Ordinal);
        Assert.True(inside.IsError);
        Assert.Equal("It did not move: that would put a group inside itself.", Text(inside));
        Assert.False(window.Workspace!.CanUndo);
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
    public async Task A_Drawing_Is_Rendered_At_The_Size_It_Is_Built_At_Or_As_A_Sheet_Of_Sizes_On_Light_And_Dark()
    {
        var (_, port) = await Serving();
        await using var client = await Client(port);

        var alone = await Call(client, "render", new());
        var sheet = await Call(client, "render", new() { ["size"] = new[] { 16, 48 }, ["background"] = "both" });

        Assert.NotEqual(true, alone.IsError);
        Assert.NotEqual(true, sheet.IsError);

        using (var image = Png(alone))
        {
            Assert.Equal((24, 24), (image.Width, image.Height));
            Assert.Equal(s_green, image.GetPixel(12, 12));
        }

        // A column per size standing on one line, a row per background, 8 px around and between.
        using var drawn = Png(sheet);

        Assert.Equal((88, 128), (drawn.Width, drawn.Height));
        Assert.Equal(s_green, drawn.GetPixel(16, 48));
        Assert.Equal(s_light, drawn.GetPixel(16, 20));
        Assert.Equal(s_green, drawn.GetPixel(56, 32));
        Assert.Equal(s_green, drawn.GetPixel(16, 112));
        Assert.Equal(s_dark, drawn.GetPixel(16, 84));
        Assert.Contains("16, 48 px", Text(sheet), StringComparison.Ordinal);
        Assert.Contains("88×128", Text(sheet), StringComparison.Ordinal);
        Assert.Contains("24 px", Text(alone), StringComparison.Ordinal);
        Assert.Contains("No problems.", Text(alone), StringComparison.Ordinal);
    }

    /// <remarks>23 × (7 / 23) is 6.9999995 as a float, which an image's size truncates to 6.</remarks>
    [AvaloniaFact]
    public async Task A_Size_A_Float_Would_Cut_Short_Is_Drawn_At_The_Size_Asked()
    {
        var path = Path.Combine(_directory, "odd.svg");

        File.WriteAllText(path, """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 23 23" width="23" height="23"><rect width="23" height="23" fill="#00ff00" /></svg>""");

        var (_, port) = await Serving(path);
        await using var client = await Client(port);

        var seven = await Call(client, "render", new() { ["size"] = new[] { 7 } });

        using var image = Png(seven);

        Assert.Equal((7, 7), (image.Width, image.Height));
        Assert.Contains("drawn at 7 px", Text(seven), StringComparison.Ordinal);
    }

    /// <remarks>clamp throws ArgumentException for a range the wrong way round, which is not the language's own exception.</remarks>
    [AvaloniaFact]
    public async Task A_Seed_The_Drawing_Will_Not_Take_Leaves_It_On_Its_Placeholders_As_Its_Tab_Does()
    {
        var path = Path.Combine(_directory, "clamped.svg");

        File.WriteAllText(path, """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
              <defs><e:code><e:param name="low" type="number" default="3" /></e:code></defs>
              <rect width="24" height="24" fill="#00ff00" fill-opacity="{{ clamp(0.5, low, 1) }}" />
            </svg>
            """);

        var (_, port) = await Serving(path);
        await using var client = await Client(port);

        var drawn = await Call(client, "render", new());

        Assert.NotEqual(true, drawn.IsError);
        Assert.Single(drawn.Content.OfType<ImageContentBlock>());
        Assert.Contains("Its values would not bind, so it is drawn with its placeholders.", Text(drawn), StringComparison.Ordinal);
    }

    /// <remarks>An image beside the drawing's file, so it is drawn only if what it references resolves against where the drawing is.</remarks>
    [AvaloniaFact]
    public async Task A_Drawing_Of_No_Project_Draws_What_It_References_Beside_Its_File()
    {
        var path = Path.Combine(_directory, "framed.svg");

        using (var blue = new SKBitmap(24, 24))
        {
            blue.Erase(new SKColor(0x00, 0x00, 0xFF));

            using var encoded = blue.Encode(SKEncodedImageFormat.Png, 100);

            File.WriteAllBytes(Path.Combine(_directory, "dot.png"), encoded.ToArray());
        }

        File.WriteAllText(path, """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24"><image href="dot.png" width="24" height="24" /></svg>""");

        var (_, port) = await Serving(path);
        await using var client = await Client(port);

        using var image = Png(await Call(client, "render", new()));

        Assert.Equal(new SKColor(0x00, 0x00, 0xFF), image.GetPixel(12, 12));
    }

    /// <remarks>Edge has no default, so the one left out is drawn only if it takes what its panel opens with.</remarks>
    [AvaloniaFact]
    public async Task Values_Given_Are_What_Is_Drawn_And_One_The_Drawing_Does_Not_Take_Is_An_Error()
    {
        const string edged = """
            <studio namespace="Demo.Icons">
              <group name="Badges">
                <e:code xmlns:e="https://svg.skia/expr/1.0">
                  <e:param name="tint" type="color" default="#ff0000" />
                  <e:param name="edge" type="color" />
                </e:code>
                <drawing name="Filled">
                  <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
                    <rect width="12" height="24" fill="{{ tint }}" />
                    <rect x="12" width="12" height="24" fill="{{ edge }}" />
                  </svg>
                </drawing>
              </group>
            </studio>
            """;
        var path = Path.Combine(_directory, "edged.svgstudio");

        File.WriteAllText(path, edged);

        var (_, port) = await Serving(path);
        await using var client = await Client(port);

        var seeded = await Call(client, "render", new() { ["node"] = "0/0" });
        var blue = await Call(client, "render", new() { ["node"] = "0/0", ["values"] = new Dictionary<string, object?> { ["tint"] = "#0000ff" } });
        var unknown = await Call(client, "render", new() { ["node"] = "0/0", ["values"] = new Dictionary<string, object?> { ["tone"] = "#0000ff" } });
        var wrong = await Call(client, "render", new() { ["node"] = "0/0", ["values"] = new Dictionary<string, object?> { ["tint"] = 3 } });

        Assert.NotEqual(true, blue.IsError);

        using (var red = Png(seeded))
        using (var given = Png(blue))
        {
            Assert.Equal(new SKColor(0xFF, 0x00, 0x00), red.GetPixel(6, 12));
            Assert.Equal(new SKColor(0x00, 0x00, 0xFF), given.GetPixel(6, 12));
            Assert.Equal(new SKColor(0x80, 0x80, 0x80), red.GetPixel(18, 12));
            Assert.Equal(new SKColor(0x80, 0x80, 0x80), given.GetPixel(18, 12));
        }

        Assert.Contains("Values: tint=#0000ff, edge=#808080.", Text(blue), StringComparison.Ordinal);
        Assert.All(new[] { unknown, wrong }, result => Assert.True(result.IsError, Text(result)));
        Assert.All(new[] { unknown, wrong }, result => Assert.Empty(result.Content.OfType<ImageContentBlock>()));
        Assert.Contains("declares tint (colour), edge (colour).", Text(unknown), StringComparison.Ordinal);
        Assert.Contains("declares tint (colour), edge (colour).", Text(wrong), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Candidate_Is_Drawn_Through_The_Drawings_Declarations_And_Changes_Nothing()
    {
        var path = Path.Combine(_directory, "tinted.svgstudio");

        File.WriteAllText(path, Tinted);

        var (window, port) = await Serving(path);
        var workspace = window.Workspace!;
        var drawing = workspace.Document.Root.Drawings.Single();

        await window.ShowAsync(drawing);
        Dispatcher.UIThread.RunJobs();

        var viewer = window.GetVisualDescendants().OfType<SvgViewer>().First();
        var before = viewer.Source;
        await using var client = await Client(port);

        const string circle = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24"><circle cx="12" cy="12" r="6" fill="{{ tint }}" /></svg>""";
        var dot = await Call(client, "render", new() { ["svg"] = circle });
        var light = await Call(client, "render", new() { ["svg"] = circle, ["background"] = "light" });
        var dark = await Call(client, "render", new() { ["svg"] = circle, ["background"] = "dark" });

        Assert.NotEqual(true, dot.IsError);

        using (var image = Png(dot))
        using (var onLight = Png(light))
        using (var onDark = Png(dark))
        {
            Assert.Equal(new SKColor(0xFF, 0x00, 0x00), image.GetPixel(12, 12));
            Assert.Equal(0, image.GetPixel(1, 1).Alpha);
            Assert.Equal((24, 24), (onLight.Width, onLight.Height));
            Assert.Equal(s_light, onLight.GetPixel(1, 1));
            Assert.Equal(new SKColor(0xFF, 0x00, 0x00), onLight.GetPixel(12, 12));
            Assert.Equal(s_dark, onDark.GetPixel(1, 1));
        }

        Assert.Contains("dry run", Text(dot), StringComparison.Ordinal);
        Assert.Contains("replace_drawing", Text(dot), StringComparison.Ordinal);
        Assert.Contains("on light.", Text(light), StringComparison.Ordinal);
        Assert.Contains("on dark.", Text(dark), StringComparison.Ordinal);
        Assert.Equal(before, viewer.Source);
        Assert.False(viewer.CanUndo);
        Assert.False(workspace.CanUndo);
        Assert.False(workspace.IsEdited);
    }

    /// <remarks>Tint and the size are the group's, so the candidate is drawn red at 48 and checked clean only through what a drawing added there inherits.</remarks>
    [AvaloniaFact]
    public async Task A_Candidate_For_A_Group_Is_Drawn_As_Add_Drawing_Would_Add_It_There()
    {
        var path = Path.Combine(_directory, "tinted.svgstudio");

        File.WriteAllText(path, Tinted.Replace("<group name=\"Badges\">", "<group name=\"Badges\" width=\"48\" height=\"48\">", StringComparison.Ordinal));

        var (window, port) = await Serving(path);
        var workspace = window.Workspace!;
        await using var client = await Client(port);

        var dot = await Call(client, "render", new()
        {
            ["node"] = "0",
            ["svg"] = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24"><circle cx="12" cy="12" r="6" fill="{{ tint }}" /></svg>"""
        });
        var misnamed = await Call(client, "render", new()
        {
            ["node"] = "0",
            ["svg"] = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24"><circle cx="12" cy="12" r="6" fill="{{ tone }}" /></svg>"""
        });

        Assert.NotEqual(true, dot.IsError);

        using (var image = Png(dot))
        {
            Assert.Equal((48, 48), (image.Width, image.Height));
            Assert.Equal(new SKColor(0xFF, 0x00, 0x00), image.GetPixel(24, 24));
        }

        Assert.StartsWith("A drawing added to group 0 Badges, drawn at 48 px", Text(dot), StringComparison.Ordinal);
        Assert.Contains("add_drawing", Text(dot), StringComparison.Ordinal);
        Assert.EndsWith("No problems.", Text(dot), StringComparison.Ordinal);
        Assert.NotEqual(true, misnamed.IsError);
        Assert.Matches(@"\nProblems:\n- [^\n]*tone", Text(misnamed));
        Assert.Single(workspace.Document.Root.Drawings);
        Assert.False(workspace.CanUndo);
    }

    [AvaloniaFact]
    public async Task What_Render_Cannot_Draw_Is_An_Error_That_Says_Why()
    {
        var path = Path.Combine(_directory, "tinted.svgstudio");

        File.WriteAllText(path, Tinted);

        var (window, port) = await Serving(path);
        await using var client = await Client(port);

        var group = await Call(client, "render", new() { ["node"] = "0" });
        var project = await Call(client, "render", new() { ["node"] = "(project)" });
        var missing = await Call(client, "render", new() { ["node"] = "9" });
        var none = await Call(client, "render", new() { ["node"] = "0/0", ["size"] = new[] { 0 } });
        var huge = await Call(client, "render", new() { ["node"] = "0/0", ["size"] = new[] { 5000 } });
        var many = await Call(client, "render", new() { ["node"] = "0/0", ["size"] = Enumerable.Range(1, 9).ToArray() });
        var wide = await Call(client, "render", new() { ["node"] = "0/0", ["size"] = new[] { 1024, 1024, 1024, 1024 } });
        var unreadable = await Call(client, "render", new() { ["node"] = "0/0", ["svg"] = "<oops" });
        var misspelled = await Call(client, "render", new() { ["nodes"] = "0/0" });
        var purple = await Call(client, "render", new() { ["node"] = "0/0", ["background"] = "purple" });
        var flat = await Call(client, "render", new() { ["node"] = "0/0", ["svg"] = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="0" height="24"><rect width="24" height="24" /></svg>""" });
        var large = await Call(client, "render", new() { ["node"] = "0/0", ["svg"] = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="5000" height="3000"><rect width="24" height="24" /></svg>""" });

        // Emptied as a tab is whose drawing would not load, so what it holds is no SVG at all.
        await window.ShowAsync(window.Workspace!.Document.Root.Drawings.Single());
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<SvgViewer>().First().Close();

        var empty = await Call(client, "render", new() { ["node"] = "0/0" });
        var refusals = new[] { group, project, missing, none, huge, many, wide, unreadable, misspelled, purple, flat, large, empty };

        Assert.All(refusals, result => Assert.True(result.IsError, Text(result)));
        Assert.All(refusals, result => Assert.Empty(result.Content.OfType<ImageContentBlock>()));
        Assert.Contains("one drawing", Text(group), StringComparison.Ordinal);
        Assert.StartsWith("(project) is the project, and render draws one drawing", Text(project), StringComparison.Ordinal);
        Assert.Contains("'background' takes one of Transparent, Light, Dark, Both, not \"purple\".", Text(purple), StringComparison.Ordinal);
        Assert.Contains("1 to 1024", Text(none), StringComparison.Ordinal);
        Assert.Contains("1 to 1024", Text(huge), StringComparison.Ordinal);
        Assert.Contains("8", Text(many), StringComparison.Ordinal);
        Assert.Contains("4096", Text(wide), StringComparison.Ordinal);
        Assert.Contains("ask for fewer or smaller sizes", Text(wide), StringComparison.Ordinal);

        // Built that large, with no size asked for, there is no size to ask fewer of.
        Assert.Contains("give a size to draw it smaller", Text(large), StringComparison.Ordinal);
        Assert.Contains("'nodes'", Text(misspelled), StringComparison.Ordinal);
        Assert.Equal("Drawing 0/0 drawing Filled has nothing to draw: it comes to no width or no height.", Text(flat));
        Assert.StartsWith("Drawing 0/0 drawing Filled cannot be drawn: ", Text(empty), StringComparison.Ordinal);
    }

    /// <remarks>Equal to what svgc writes for the drawing's item.</remarks>
    [AvaloniaFact]
    public async Task Get_Code_Answers_What_Svgc_Writes_For_The_Drawing_And_A_Refusal_As_Plain_Text()
    {
        var path = Path.Combine(_directory, "tinted.svgstudio");

        File.WriteAllText(path, Tinted);

        var (window, port) = await Serving(path);
        await using var client = await Client(port);

        var code = await Call(client, "get_code", new() { ["node"] = "0/0" });
        var group = await Call(client, "get_code", new() { ["node"] = "0" });

        var flat = window.Workspace!.Document.Flatten();
        var item = flat.Items.Single();
        var settings = SvgcBuildSettings.For(flat);
        var written = Path.Combine(_directory, "Filled.cs");

        settings.TextLayout = SvgTextLayout.Baked;
        SvgcProjectBuild.Write(
            new SvgcProjectItem(item.Input, written, item.Namespace, item.Class, item.Recipe, item.Width, item.Height, item.Scale, item.Padding, item.Source),
            settings,
            new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings())));

        Assert.NotEqual(true, code.IsError);
        Assert.EndsWith("\n\n" + File.ReadAllText(written), Text(code), StringComparison.Ordinal);
        Assert.Contains("Demo.Icons.Filled", Text(code), StringComparison.Ordinal);
        Assert.Contains("tint", File.ReadAllText(written), StringComparison.Ordinal);

        // A read tool's answer is never an error, whatever it says: what it read is no refusal.
        Assert.NotEqual(true, group.IsError);
        Assert.Equal("'0' is not a drawing. get_project lists the paths.", Text(group));
    }

    /// <remarks>A file can hold a size the build refuses, since each setting is read on its own; render already said why in plain text.</remarks>
    [AvaloniaFact]
    public async Task Get_Code_Of_A_Size_The_Build_Refuses_Says_Why_As_Plain_Text()
    {
        var path = Path.Combine(_directory, "scaled.svgstudio");

        File.WriteAllText(path, Project.Replace("<studio namespace=\"Demo.Icons\">", "<studio namespace=\"Demo.Icons\" width=\"24\" scale=\"2\">", StringComparison.Ordinal));

        var (_, port) = await Serving(path);
        await using var client = await Client(port);

        var code = await Call(client, "get_code", new() { ["node"] = "0" });

        Assert.NotEqual(true, code.IsError);
        Assert.StartsWith("Drawing 0 drawing home cannot be generated: ", Text(code), StringComparison.Ordinal);
    }

    /// <remarks>
    /// The words are placed by a span of their own, so baked freezes them at their default and
    /// relaxed carries them; Export would ask which, and get_code takes the answer on file.
    /// </remarks>
    [AvaloniaFact]
    public async Task Get_Code_Lays_Text_Out_As_The_Person_Answered_Export_And_Says_What_That_Did()
    {
        var path = Path.Combine(_directory, "badge.svgstudio");

        File.WriteAllText(path, Badge);

        var (window, port) = await Serving(path);
        await using var client = await Client(port);

        StudioSettings.RelaxedText = RelaxedTextAnswer.Never;
        var baked = Text(await Call(client, "get_code", new() { ["node"] = "0" }));

        StudioSettings.RelaxedText = RelaxedTextAnswer.Always;
        var relaxed = Text(await Call(client, "get_code", new() { ["node"] = "0" }));

        var flat = window.Workspace!.Document.Flatten();

        string Svgc(SvgTextLayout layout)
        {
            var settings = SvgcBuildSettings.For(flat);

            settings.TextLayout = layout;

            return SvgcProjectBuild.Generate(flat.Items.Single(), settings, new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings())))!;
        }

        // The header, a line for each warning, and a blank line before the code.
        static string[] Head(string answer) => answer[..answer.IndexOf("\n\n", StringComparison.Ordinal)].Split('\n');

        Assert.NotEqual(Svgc(SvgTextLayout.Baked), Svgc(SvgTextLayout.Relaxed));
        Assert.EndsWith("\n\n" + Svgc(SvgTextLayout.Baked), baked, StringComparison.Ordinal);
        Assert.EndsWith("\n\n" + Svgc(SvgTextLayout.Relaxed), relaxed, StringComparison.Ordinal);

        foreach (var (answer, said) in new[] { (baked, "is frozen at its default"), (relaxed, "relaxed text layout") })
        {
            var head = Head(answer);

            Assert.StartsWith("Drawing 0 drawing Badge: ", head[0], StringComparison.Ordinal);
            Assert.NotEmpty(head[1..]);
            Assert.All(head[1..], line => Assert.StartsWith("warning: ", line, StringComparison.Ordinal));
            Assert.Contains(head[1..], line => line.Contains(said, StringComparison.Ordinal));
        }
    }

    /// <remarks>Export would ask whether to relax the words; asked to ask, a tool lays them out as get_code does.</remarks>
    [AvaloniaFact]
    public async Task Export_Writes_The_Project_As_One_CSharp_File_With_Its_Warnings_And_Asks_Nothing()
    {
        var path = Path.Combine(_directory, "badge.svgstudio");
        var target = Path.Combine(_directory, "Icons.cs");

        File.WriteAllText(path, Badge);

        var (window, port) = await Serving(path);
        await using var client = await Client(port);

        StudioSettings.RelaxedText = RelaxedTextAnswer.Ask;

        var first = await Call(client, "export", new() { ["path"] = target });
        var again = await Call(client, "export", new() { ["path"] = target });

        // Built as svgc builds it, to a file of the same name, which names the helper class.
        var flat = window.Workspace!.Document.Flatten();
        var settings = SvgcBuildSettings.For(flat);
        var expected = Path.Combine(Directory.CreateDirectory(Path.Combine(_directory, "svgc")).FullName, "Icons.cs");

        settings.SingleFile = expected;
        settings.TextLayout = SvgTextLayout.Baked;
        SvgcProjectBuild.Run(flat, settings, new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings())));

        Assert.NotEqual(true, first.IsError);
        Assert.StartsWith($"Wrote {target}: every drawing of the project as one C# file.\nwarning: ", Text(first), StringComparison.Ordinal);
        Assert.Contains("is frozen at its default", Text(first), StringComparison.Ordinal);
        Assert.Equal(File.ReadAllText(expected), File.ReadAllText(target));
        Assert.NotEqual(true, again.IsError);
        Assert.StartsWith($"Wrote {target}, replacing the file there: ", Text(again), StringComparison.Ordinal);
        Assert.False(window.Workspace.IsEdited);
        Assert.Empty(_announced);
    }

    [AvaloniaFact]
    public async Task Export_Refuses_A_Path_It_Cannot_Write_And_Writes_Nothing()
    {
        var path = Path.Combine(_directory, "icons.svgstudio");
        var locked = Path.Combine(_directory, "Locked.cs");

        File.WriteAllText(path, Project);
        File.WriteAllText(locked, "// kept");

        var (_, port) = await Serving(path);
        await using var client = await Client(port);

        File.SetAttributes(locked, FileAttributes.ReadOnly);

        CallToolResult unwritable;

        try
        {
            unwritable = await Call(client, "export", new() { ["path"] = locked });
        }
        finally
        {
            File.SetAttributes(locked, FileAttributes.Normal);
        }

        var relative = await Call(client, "export", new() { ["path"] = "Icons.cs" });
        var nowhere = await Call(client, "export", new() { ["path"] = Path.Combine(_directory, "gone", "Icons.cs") });
        var svg = await Call(client, "export", new() { ["path"] = Path.Combine(_directory, "Icons.svg") });

        Assert.All(new[] { unwritable, relative, nowhere, svg }, result => Assert.True(result.IsError, Text(result)));
        Assert.StartsWith("The project couldn't be exported: ", Text(unwritable), StringComparison.Ordinal);
        Assert.Equal("'Icons.cs' is not a full path; Studio has no folder it is relative to.", Text(relative));
        Assert.Equal($"There is no folder {Path.Combine(_directory, "gone")}.", Text(nowhere));
        Assert.Equal("A project exports as one C# file; give a path ending .cs.", Text(svg));
        Assert.Equal("// kept", File.ReadAllText(locked));
        Assert.Equal(new[] { "Locked.cs", "icons.svgstudio" }, Directory.GetFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Empty(_announced);
    }

    /// <remarks>A failed write was said in a dialog of the window's own, which no test could replace.</remarks>
    [AvaloniaFact]
    public async Task Export_Of_A_Drawing_Of_No_Project_Writes_Svg_Or_CSharp_By_The_Name_Given()
    {
        var (window, port) = await Serving();
        await using var client = await Client(port);
        var svg = Path.Combine(_directory, "copy.svg");
        var cs = Path.Combine(_directory, "Copy.cs");
        var locked = Path.Combine(_directory, "locked.svg");

        File.WriteAllText(locked, "<svg />");
        File.SetAttributes(locked, FileAttributes.ReadOnly);

        CallToolResult unwritable;

        try
        {
            unwritable = await Call(client, "export", new() { ["path"] = locked });
        }
        finally
        {
            File.SetAttributes(locked, FileAttributes.Normal);
        }

        var drawn = await Call(client, "export", new() { ["path"] = svg });
        var coded = await Call(client, "export", new() { ["path"] = cs });
        var own = await Call(client, "export", new() { ["path"] = Path.Combine(_directory, "drawing.svg") });
        var png = await Call(client, "export", new() { ["path"] = Path.Combine(_directory, "copy.png") });

        Assert.NotEqual(true, drawn.IsError);
        Assert.Equal($"Wrote {svg}: the drawing in front as SVG.", Text(drawn));
        Assert.Contains("fill=\"#00ff00\"", File.ReadAllText(svg), StringComparison.Ordinal);
        Assert.NotEqual(true, coded.IsError);
        Assert.Equal($"Wrote {cs}: the drawing in front as C#, class Svg.Copy.", Text(coded));
        Assert.Contains("class Copy", File.ReadAllText(cs), StringComparison.Ordinal);

        Assert.All(new[] { unwritable, own, png }, result => Assert.True(result.IsError, Text(result)));
        Assert.StartsWith("Export failed: ", Text(unwritable), StringComparison.Ordinal);
        Assert.Equal("That is the drawing's own file, which save writes.", Text(own));
        Assert.Equal("A drawing exports as SVG to a path ending .svg, or as C# to one ending .cs.", Text(png));
        Assert.Equal(Drawing, File.ReadAllText(Path.Combine(_directory, "drawing.svg")));
        Assert.Equal("<svg />", File.ReadAllText(locked));

        // A tab whose drawing would not load, as the window's own Open leaves one.
        var broken = Path.Combine(_directory, "broken.svg");

        File.WriteAllText(broken, "<svg><rect");
        await window.OpenAsync(new[] { broken });
        Dispatcher.UIThread.RunJobs();

        var empty = await Call(client, "export", new() { ["path"] = Path.Combine(_directory, "empty.svg") });

        Assert.True(empty.IsError);
        Assert.Equal("The drawing in front has not loaded, so there is nothing to export.", Text(empty));
        Assert.False(File.Exists(Path.Combine(_directory, "empty.svg")));
        Assert.Empty(_announced);
    }

    [AvaloniaFact]
    public async Task Export_Takes_In_The_Front_Tabs_Edits_And_Names_The_Tabs_It_Left_Out()
    {
        var path = Path.Combine(_directory, "nested.svgstudio");
        var target = Path.Combine(_directory, "Icons.cs");

        File.WriteAllText(path, Nested);

        var (window, port) = await Serving(path);
        await using var client = await Client(port);
        var root = window.Workspace!.Document.Root;
        var home = (ProjectDrawing)root.Children[0];
        var dot = (ProjectDrawing)((ProjectGroup)((ProjectGroup)root.Children[1]).Children[0]).Children[0];

        // home's edit held in a tab behind, and Dot's in the tab in front.
        await window.ShowAsync(home);
        Dispatcher.UIThread.RunJobs();
        await Call(client, "set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#123456" } }, ["summary"] = "darken" });
        await window.ShowAsync(dot);
        Dispatcher.UIThread.RunJobs();
        await Call(client, "set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#654321" } }, ["summary"] = "brown" });

        var exported = await Call(client, "export", new() { ["path"] = target });
        var edits = window.Workspace.Edits;

        // The tab still holds the edits, unsaved, and the project has them already: no second step.
        var again = await Call(client, "export", new() { ["path"] = target });

        Assert.NotEqual(true, exported.IsError);
        Assert.Equal(
            $"Wrote {target}: every drawing of the project as one C# file.\n"
            + "It took in the edits held in the tab of 1/0/0 Dot, as Export does; the project holds them unsaved.\n"
            + "Left out: the edits held in the tabs of 0 home; open each and save, then export again.",
            Text(exported));
        Assert.Contains("#654321", dot.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("#123456", home.Text, StringComparison.Ordinal);
        Assert.True(window.Workspace.IsEdited);
        Assert.NotEqual(true, again.IsError);
        Assert.Equal(Text(exported).Replace($"Wrote {target}:", $"Wrote {target}, replacing the file there:", StringComparison.Ordinal), Text(again));
        Assert.Equal(edits, window.Workspace.Edits);
        Assert.Empty(_announced);
    }

    /// <remarks>Taking in the tab's text would have said why it cannot, in a dialog nobody closes for a client.</remarks>
    [AvaloniaFact]
    public async Task Export_Is_Refused_Where_The_Tab_In_Front_Holds_Text_The_Project_Will_Not_Take()
    {
        var path = Path.Combine(_directory, "icons.svgstudio");
        var target = Path.Combine(_directory, "Icons.cs");

        File.WriteAllText(path, Project);

        var (window, port) = await Serving(path);
        await using var client = await Client(port);

        await window.ShowAsync(window.Workspace!.Document.Root.Children[0]);
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<SvgViewer>(((TabItem)Tabs(window).SelectedItem!).Content).SetSource("<div/>");

        var refused = await Call(client, "export", new() { ["path"] = target });

        Assert.True(refused.IsError);
        Assert.StartsWith("The project couldn't be exported: ", Text(refused), StringComparison.Ordinal);
        Assert.Contains("has to be an <svg>", Text(refused), StringComparison.Ordinal);
        Assert.False(File.Exists(target));
        Assert.False(window.Workspace.IsEdited);
        Assert.Empty(_announced);
    }

    [AvaloniaFact]
    public async Task A_Project_Never_Saved_Is_Saved_To_The_Path_Given_Without_Asking_Where()
    {
        var (window, port) = await Serving();
        await using var client = await Client(port);
        var target = Path.Combine(_directory, "fresh.svgstudio");

        var opened = await Call(client, "new_project", new());
        var unnamed = await Call(client, "save", new());
        var added = await Call(client, "add_drawing", new() { ["parent"] = "", ["name"] = "dot", ["svg"] = Drawing });
        var saved = await Call(client, "save", new() { ["path"] = target });

        Assert.All(new[] { opened, added, saved }, result => Assert.NotEqual(true, result.IsError));
        Assert.True(unnamed.IsError);
        Assert.Equal("The project has never been saved; give save a path ending .svgstudio to write it there.", Text(unnamed));
        Assert.Equal($"Saved as {target}.", Text(saved));
        Assert.Equal(target, window.Workspace!.Document.Path);
        Assert.False(window.Workspace.IsEdited);
        Assert.Equal("dot", Assert.Single(ProjectDocument.Load(target).Root.Drawings).Name);
        Assert.Empty(_announced);
    }

    [AvaloniaFact]
    public async Task Save_With_A_Path_Is_Refused_For_A_Project_Saved_Already_Or_A_File_Of_Another_Kind()
    {
        var path = Path.Combine(_directory, "icons.svgstudio");

        File.WriteAllText(path, Project);

        var (window, port) = await Serving(path);
        await using var client = await Client(port);

        var saved = await Call(client, "save", new() { ["path"] = Path.Combine(_directory, "other.svgstudio") });

        await Call(client, "new_project", new());

        var drawing = await Call(client, "save", new() { ["path"] = Path.Combine(_directory, "other.svg") });
        var relative = await Call(client, "save", new() { ["path"] = "other.svgstudio" });

        // A tab of no project's, with no file of its own.
        await window.OpenTextAsync(Drawing, "Icon");
        Dispatcher.UIThread.RunJobs();

        var project = await Call(client, "save", new() { ["path"] = Path.Combine(_directory, "icon.svgstudio") });
        var untitled = await Call(client, "save", new());

        Assert.All(new[] { saved, drawing, relative, project, untitled }, result => Assert.True(result.IsError, Text(result)));
        Assert.Equal($"The project is saved at {path} already, and Studio has no Save As for a project; save without a path writes it there.", Text(saved));
        Assert.Equal("A project saves to a path ending .svgstudio.", Text(drawing));
        Assert.Equal("'other.svgstudio' is not a full path; Studio has no folder it is relative to.", Text(relative));
        Assert.Equal("A drawing of no project's saves as SVG; give a path ending .svg.", Text(project));
        Assert.Equal("The drawing in front has no file; give save a path ending .svg to write it there.", Text(untitled));
        Assert.Equal(new[] { "icons.svgstudio" }, Directory.GetFiles(_directory).Select(Path.GetFileName));
        Assert.Empty(_announced);
    }

    [AvaloniaFact]
    public async Task A_Drawing_Of_No_Project_Saved_To_A_Path_Takes_Its_Tab_There()
    {
        var (window, port) = await Serving();
        var viewer = window.GetVisualDescendants().OfType<SvgViewer>().First();
        await using var client = await Client(port);
        var target = Path.Combine(_directory, "red.svg");
        var icon = Path.Combine(_directory, "icon.svg");

        File.WriteAllText(icon, "<svg />");

        await Call(client, "set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#ff0000" } }, ["summary"] = "red" });

        var saved = await Call(client, "save", new() { ["path"] = target });

        // And one with no file at all, which a save without a path could only have asked about.
        await window.OpenTextAsync(Drawing, "Icon");
        Dispatcher.UIThread.RunJobs();

        var named = await Call(client, "save", new() { ["path"] = icon });

        Assert.NotEqual(true, saved.IsError);
        Assert.Equal($"Saved as {target}. Its tab saves there from now on, and its undo history started again.", Text(saved));
        Assert.Equal(target, viewer.DocumentPath);
        Assert.False(viewer.IsSourceModified);
        Assert.Contains("#ff0000", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.Equal(Drawing, File.ReadAllText(Path.Combine(_directory, "drawing.svg")));
        Assert.NotEqual(true, named.IsError);
        Assert.Equal($"Saved as {icon}, replacing the file there. Its tab saves there from now on, and its undo history started again.", Text(named));
        Assert.Equal(Drawing, File.ReadAllText(icon));
        Assert.Empty(_announced);
    }

    /// <remarks>Only the drawing is written, so the project's tabs that hold edits are no exception to the save.</remarks>
    [AvaloniaFact]
    public async Task A_Drawing_Of_No_Project_Saved_To_A_Path_Beside_A_Project_Says_Nothing_Of_The_Projects_Tabs()
    {
        var path = Path.Combine(_directory, "icons.svgstudio");
        var star = Path.Combine(_directory, "star.svg");
        var target = Path.Combine(_directory, "copy.svg");

        File.WriteAllText(path, Project);
        File.WriteAllText(star, Drawing);

        var (window, port) = await Serving(path);
        await using var client = await Client(port);

        await window.ShowAsync(window.Workspace!.Document.Root.Children[0]);
        Dispatcher.UIThread.RunJobs();
        await Call(client, "set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#123456" } }, ["summary"] = "darken" });
        await window.OpenAsync(new[] { star });
        Dispatcher.UIThread.RunJobs();

        var saved = await Call(client, "save", new() { ["path"] = target });

        Assert.NotEqual(true, saved.IsError);
        Assert.Equal($"Saved as {target}. Its tab saves there from now on, and its undo history started again.", Text(saved));
        Assert.Equal(Drawing, File.ReadAllText(target));
        Assert.Equal(Project, File.ReadAllText(path));
        Assert.Empty(_announced);
    }

    /// <remarks>A failed write was announced, which nobody closes for a client; text that would not read back was said to have moved the tab.</remarks>
    [AvaloniaFact]
    public async Task A_Drawing_Of_No_Project_That_Cannot_Be_Saved_To_A_Path_Says_Why_And_Its_Tab_Stays_Where_It_Was()
    {
        var (window, port) = await Serving();
        var viewer = window.GetVisualDescendants().OfType<SvgViewer>().First();
        await using var client = await Client(port);
        var own = Path.Combine(_directory, "drawing.svg");
        var locked = Path.Combine(_directory, "locked.svg");
        var unread = Path.Combine(_directory, "div.svg");

        File.WriteAllText(locked, "<svg />");
        File.SetAttributes(locked, FileAttributes.ReadOnly);

        await Call(client, "set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#ff0000" } }, ["summary"] = "red" });

        CallToolResult unwritable;

        try
        {
            unwritable = await Call(client, "save", new() { ["path"] = locked });
        }
        finally
        {
            File.SetAttributes(locked, FileAttributes.Normal);
        }

        // Text that reads, but not as a drawing: written, and then not read back.
        viewer.SetSource("<div/>");

        var unreadable = await Call(client, "save", new() { ["path"] = unread });

        Assert.All(new[] { unwritable, unreadable }, result => Assert.True(result.IsError, Text(result)));
        Assert.StartsWith("The drawing couldn't be saved: ", Text(unwritable), StringComparison.Ordinal);
        Assert.Equal("<svg />", File.ReadAllText(locked));
        Assert.Matches(@"^div\.svg couldn't be opened: \S", Text(unreadable));
        Assert.EndsWith("\nIt was written all the same, but the tab is where it was, holding the edits.", Text(unreadable), StringComparison.Ordinal);
        Assert.Equal("<div/>", File.ReadAllText(unread));
        Assert.Equal(own, viewer.DocumentPath);
        Assert.True(viewer.IsSourceModified);
        Assert.Equal(Drawing, File.ReadAllText(own));
        Assert.Empty(_announced);
    }

    /// <remarks>A drawing of no project's is not the project's to close, so its edits are no reason to refuse.</remarks>
    [AvaloniaFact]
    public async Task A_New_Project_Replaces_The_One_Open_And_Is_Refused_Over_Unsaved_Changes()
    {
        var path = Path.Combine(_directory, "icons.svgstudio");
        var star = Path.Combine(_directory, "star.svg");

        File.WriteAllText(path, Project);
        File.WriteAllText(star, Drawing);

        var (window, port) = await Serving(path);
        await using var client = await Client(port);
        var workspace = window.Workspace!;

        await window.OpenAsync(new[] { star });
        Dispatcher.UIThread.RunJobs();
        await Call(client, "set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#ff0000" } }, ["summary"] = "red" });
        await Call(client, "set_node", new() { ["node"] = "0", ["setting"] = "class", ["value"] = "Home" });

        var refused = await Call(client, "new_project", new());

        Assert.True(refused.IsError);
        Assert.StartsWith("icons.svgstudio has changes that have not been saved. ", Text(refused), StringComparison.Ordinal);
        Assert.Same(workspace, window.Workspace);

        await Call(client, "undo", new());

        var opened = await Call(client, "new_project", new());

        Assert.NotEqual(true, opened.IsError);
        Assert.Equal(
            "Opened a new project, never saved; add_group and add_drawing fill it, and save with a path ending .svgstudio writes it. icons.svgstudio was closed, with its tabs.",
            Text(opened));
        Assert.NotSame(workspace, window.Workspace);
        Assert.Null(window.Workspace!.Document.Path);
        Assert.Contains(Tabs(window).Items.OfType<TabItem>(), item => item.Content is SvgViewer { DocumentPath: { } shown, IsSourceModified: true } && shown == star);
        Assert.Empty(_announced);
    }

    [AvaloniaFact]
    public async Task Open_File_Opens_A_Drawing_In_A_Tab_Of_Its_Own_And_A_Project_In_Place_Of_The_One_Open()
    {
        var first = Path.Combine(_directory, "icons.svgstudio");
        var second = Path.Combine(_directory, "nested.svgstudio");
        var drawing = Path.Combine(_directory, "dot.svg");

        File.WriteAllText(first, Project);
        File.WriteAllText(second, Nested);
        File.WriteAllText(drawing, Drawing);

        var (window, port) = await Serving(first);
        await using var client = await Client(port);

        await window.ShowAsync(window.Workspace!.Document.Root.Children[0]);
        Dispatcher.UIThread.RunJobs();

        var tab = await Call(client, "open_file", new() { ["path"] = drawing });

        Assert.NotEqual(true, tab.IsError);
        Assert.Equal($"Opened {drawing} in a tab of its own, not part of a project.", Text(tab));
        Assert.Equal(drawing, Assert.IsType<SvgViewer>(((TabItem)Tabs(window).SelectedItem!).Content).DocumentPath);
        Assert.Equal(first, window.Workspace!.Document.Path);

        var project = await Call(client, "open_file", new() { ["path"] = second });

        Assert.NotEqual(true, project.IsError);
        Assert.Equal($"Opened the project nested.svgstudio at {second}; get_project lists it. icons.svgstudio was closed, with its tabs.", Text(project));
        Assert.Equal(second, window.Workspace!.Document.Path);

        // The old project's tabs went with it, and the drawing of its own stayed.
        Assert.Equal(
            new object?[] { null, window.Workspace.Document.Root },
            Tabs(window).Items.OfType<TabItem>().Select(item => item.Tag));

        var again = await Call(client, "open_file", new() { ["path"] = second });

        Assert.NotEqual(true, again.IsError);
        Assert.Equal($"The project at {second} is open already; reload reads it again from its file.", Text(again));

        // The same file by another case, where the disk does not tell them apart: opened again, it lost its tabs.
        var cased = Path.Combine(_directory, "NESTED.svgstudio");

        if (!OperatingSystem.IsLinux() && File.Exists(cased))
        {
            var workspace = window.Workspace;
            var same = await Call(client, "open_file", new() { ["path"] = cased });

            Assert.Equal($"The project at {cased} is open already; reload reads it again from its file.", Text(same));
            Assert.Same(workspace, window.Workspace);
        }

        Assert.Empty(_announced);
    }

    /// <remarks>A project that would not read was said in a dialog, which nobody closes for a client.</remarks>
    [AvaloniaFact]
    public async Task Open_File_Refuses_A_Relative_Or_Missing_Path_And_A_Drawing_That_Will_Not_Load_Leaves_No_Tab()
    {
        var (window, port) = await Serving();
        await using var client = await Client(port);
        var broken = Path.Combine(_directory, "broken.svg");
        var unparsed = Path.Combine(_directory, "broken.svgstudio");
        var garbage = Path.Combine(_directory, "broken.pcvd");
        var count = Tabs(window).Items.Count;

        File.WriteAllText(broken, "<svg><rect");
        File.WriteAllText(unparsed, "<studio><drawing /></studio>");
        File.WriteAllText(garbage, "not a PaintCode document");

        var relative = await Call(client, "open_file", new() { ["path"] = "broken.svg" });
        var missing = await Call(client, "open_file", new() { ["path"] = Path.Combine(_directory, "gone.svg") });
        var unreadable = await Call(client, "open_file", new() { ["path"] = broken });
        var project = await Call(client, "open_file", new() { ["path"] = unparsed });
        var paintCode = await Call(client, "open_file", new() { ["path"] = garbage });

        Assert.All(new[] { relative, missing, unreadable, project, paintCode }, result => Assert.True(result.IsError, Text(result)));
        Assert.Equal("'broken.svg' is not a full path; Studio has no folder it is relative to.", Text(relative));
        Assert.Equal($"There is no file at {Path.Combine(_directory, "gone.svg")}.", Text(missing));

        // With the viewer's reason, which it shows only over the drawing.
        Assert.Matches(@"^broken\.svg couldn't be opened: \S", Text(unreadable));
        Assert.StartsWith("The project couldn't be opened: ", Text(project), StringComparison.Ordinal);
        Assert.StartsWith("The document couldn't be imported: ", Text(paintCode), StringComparison.Ordinal);
        Assert.Equal(count, Tabs(window).Items.Count);
        Assert.Null(window.Workspace);
        Assert.Empty(_announced);
    }

    /// <remarks>The PaintCode document is converted on the terms the person last chose, which the dialog would offer.</remarks>
    [AvaloniaFact]
    public async Task Open_File_Converts_An_Svgc_Project_And_A_PaintCode_Document_And_Says_What_They_Lost()
    {
        var svgc = Path.Combine(_directory, "icons.svgcproj");
        var paintCode = Path.Combine(_directory, "sample.pcvd");
        var (asks, integers, organizes) = (StudioSettings.ConvertAsks, StudioSettings.ConvertIntegers, StudioSettings.ConvertOrganizes);

        File.WriteAllText(Path.Combine(_directory, "badge.svg"), Drawing);
        File.WriteAllText(svgc, """
            <svgc namespace="Demo.Icons">
              <svg input="badge.svg" class="Badge" />
            </svgc>
            """);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestAssets", "sample.pcvd"), paintCode);

        var (window, port) = await Serving();
        await using var client = await Client(port);

        try
        {
            StudioSettings.ConvertAsks = true;
            StudioSettings.ConvertIntegers = true;
            StudioSettings.ConvertOrganizes = false;

            var converted = await Call(client, "open_file", new() { ["path"] = svgc });

            Assert.NotEqual(true, converted.IsError);
            Assert.StartsWith(
                "Converted icons.svgcproj into a new project, held unsaved; save with a path ending .svgstudio writes it, and get_project lists it.\nConverted: ",
                Text(converted),
                StringComparison.Ordinal);
            Assert.Contains("icons.svgcproj was converted into a project holding the drawings themselves", Text(converted), StringComparison.Ordinal);
            Assert.Equal("badge", Assert.Single(window.Workspace!.Document.Root.Drawings).Name);
            Assert.True(window.Workspace.IsEdited);

            // Held unsaved, so opening another in its place would throw it away.
            var refused = await Call(client, "open_file", new() { ["path"] = paintCode });

            Assert.True(refused.IsError);
            Assert.StartsWith("Untitled has changes that have not been saved. ", Text(refused), StringComparison.Ordinal);

            await Call(client, "save", new() { ["path"] = Path.Combine(_directory, "converted.svgstudio") });

            var imported = await Call(client, "open_file", new() { ["path"] = paintCode });

            Assert.NotEqual(true, imported.IsError);
            Assert.StartsWith(
                "Converted sample.pcvd into a new project, held unsaved; save with a path ending .svgstudio writes it, and get_project lists it. converted.svgstudio was closed, with its tabs.\nImported: ",
                Text(imported),
                StringComparison.Ordinal);
            Assert.Equal(new[] { "badge", "host" }, window.Workspace!.Document.Root.Drawings.Select(drawing => drawing.Name).Order(StringComparer.Ordinal));
            Assert.Contains("<e:param name=\"level\" type=\"integer\"", window.Workspace.Document.Root.CodeText, StringComparison.Ordinal);
        }
        finally
        {
            (StudioSettings.ConvertAsks, StudioSettings.ConvertIntegers, StudioSettings.ConvertOrganizes) = (asks, integers, organizes);
        }

        Assert.Empty(_announced);
    }

    /// <remarks>Opening would close the project open and then ask whether to restore the copy, which a client cannot answer.</remarks>
    [AvaloniaFact]
    public async Task Open_File_Leaves_A_Copy_Of_Unsaved_Work_For_The_Person_And_Closes_Nothing()
    {
        var store = ProjectRecovery.Store;
        var open = Path.Combine(_directory, "icons.svgstudio");
        var kept = Path.Combine(_directory, "nested.svgstudio");

        ProjectRecovery.Store = Directory.CreateDirectory(Path.Combine(_directory, "recovery")).FullName;

        try
        {
            File.WriteAllText(open, Project);
            File.WriteAllText(kept, Nested);
            File.WriteAllText(ProjectRecovery.For(kept), Nested.Replace("\"Dot\"", "\"Spot\"", StringComparison.Ordinal));

            var (window, port) = await Serving(open);
            await using var client = await Client(port);
            var workspace = window.Workspace;

            var refused = await Call(client, "open_file", new() { ["path"] = kept });

            Assert.True(refused.IsError);
            Assert.StartsWith("Svg Studio kept a copy of nested.svgstudio from ", Text(refused), StringComparison.Ordinal);
            Assert.EndsWith(" Nothing was opened: open it from Studio's File menu, which asks whether to restore the copy.", Text(refused), StringComparison.Ordinal);
            Assert.Same(workspace, window.Workspace);
            Assert.True(File.Exists(ProjectRecovery.For(kept)));
            Assert.Empty(_announced);
        }
        finally
        {
            ProjectRecovery.Store = store;
        }
    }

    [AvaloniaFact]
    public async Task Reload_Reads_A_Changed_Project_Again_Reopens_Its_Tabs_And_Changes_Nothing_The_Second_Time()
    {
        var path = Path.Combine(_directory, "nested.svgstudio");

        File.WriteAllText(path, Nested);

        var (window, port) = await Serving(path);
        await using var client = await Client(port);
        var root = window.Workspace!.Document.Root;
        var round = (ProjectGroup)((ProjectGroup)root.Children[1]).Children[0];

        await window.ShowAsync(round);
        await window.ShowAsync(round.Children[0]);
        await window.ShowAsync(root.Children[0]);
        Dispatcher.UIThread.RunJobs();

        // As an editor or git would leave it: home recoloured, and Dot renamed, so its path names another node.
        File.WriteAllText(path, Nested.Replace("#00ff00", "#0000ff", StringComparison.Ordinal).Replace("\"Dot\"", "\"Spot\"", StringComparison.Ordinal));

        var reloaded = await Call(client, "reload", new());
        var again = await Call(client, "reload", new());
        var now = window.Workspace!.Document.Root;

        Assert.NotEqual(true, reloaded.IsError);
        Assert.Equal(
            $"Read {path} again; get_project lists it now. The tabs of 1/0/0 Dot were not opened again, as their paths now name other nodes or none. Its undo history started again.",
            Text(reloaded));
        Assert.Contains("#0000ff", ((ProjectDrawing)now.Children[0]).Text, StringComparison.Ordinal);
        Assert.Equal(
            new[] { now, ((ProjectGroup)now.Children[1]).Children[0], now.Children[0] },
            Tabs(window).Items.OfType<TabItem>().Select(item => item.Tag));
        Assert.Same(now.Children[0], ((TabItem)Tabs(window).SelectedItem!).Tag);
        Assert.NotEqual(true, again.IsError);
        Assert.Equal("Nothing changed: the file holds what Studio has.", Text(again));
        Assert.Same(now, window.Workspace.Document.Root);
        Assert.Empty(_announced);
    }

    [AvaloniaFact]
    public async Task Reload_Is_Refused_While_The_Project_Or_A_Tab_Holds_Unsaved_Edits()
    {
        var path = Path.Combine(_directory, "icons.svgstudio");

        File.WriteAllText(path, Project);

        var (window, port) = await Serving(path);
        await using var client = await Client(port);
        var workspace = window.Workspace!;

        await window.ShowAsync(workspace.Document.Root.Children[0]);
        Dispatcher.UIThread.RunJobs();

        await Call(client, "set_node", new() { ["node"] = "0", ["setting"] = "class", ["value"] = "Home" });
        var project = await Call(client, "reload", new());
        await Call(client, "undo", new());

        await Call(client, "set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#ff0000" } }, ["summary"] = "red" });
        var tab = await Call(client, "reload", new());
        await Call(client, "undo", new());

        await Call(client, "new_project", new());
        var untitled = await Call(client, "reload", new());

        Assert.All(new[] { project, tab, untitled }, result => Assert.True(result.IsError, Text(result)));
        Assert.StartsWith("Nothing was reloaded: changes not saved are held in the project. ", Text(project), StringComparison.Ordinal);
        Assert.StartsWith("Nothing was reloaded: changes not saved are held in the tab of 0 home. ", Text(tab), StringComparison.Ordinal);
        Assert.Equal("The project has never been saved, so it has no file to read again.", Text(untitled));
        Assert.Equal(Project, File.ReadAllText(path));
        Assert.Empty(_announced);
    }

    /// <remarks>Opened again, a file that does not parse goes to git's merge resolution, which says what it found in a dialog.</remarks>
    [AvaloniaFact]
    public async Task Reload_Refuses_A_Project_File_That_No_Longer_Reads_And_Keeps_What_Is_Open()
    {
        var path = Path.Combine(_directory, "icons.svgstudio");

        File.WriteAllText(path, Project);

        var (window, port) = await Serving(path);
        await using var client = await Client(port);
        var workspace = window.Workspace;

        File.WriteAllText(path, "<studio><drawing /></studio>");

        var refused = await Call(client, "reload", new());

        Assert.True(refused.IsError);
        Assert.StartsWith($"{path} no longer reads as a project: ", Text(refused), StringComparison.Ordinal);
        Assert.Same(workspace, window.Workspace);
        Assert.Empty(_announced);
    }

    /// <remarks>
    /// A copy written while the project held an edit since taken back is still on disk until the next
    /// write; it is the project's own, which closing it throws away, and no copy to keep for the person.
    /// </remarks>
    [AvaloniaFact]
    public async Task Reload_Is_Not_Refused_Over_The_Copy_Studio_Kept_Of_The_Open_Projects_Own_Work()
    {
        var store = ProjectRecovery.Store;
        var path = Path.Combine(_directory, "icons.svgstudio");

        ProjectRecovery.Store = Directory.CreateDirectory(Path.Combine(_directory, "recovery")).FullName;

        try
        {
            File.WriteAllText(path, Project);

            var (window, port) = await Serving(path);
            await using var client = await Client(port);
            var copy = ProjectRecovery.For(path);

            Assert.Equal(copy, window.Recovery!.Path);

            File.WriteAllText(copy, Project.Replace("\"home\"", "\"house\"", StringComparison.Ordinal));
            File.WriteAllText(path, Project.Replace("#00ff00", "#0000ff", StringComparison.Ordinal));

            var reloaded = await Call(client, "reload", new());

            Assert.NotEqual(true, reloaded.IsError);
            Assert.StartsWith($"Read {path} again; ", Text(reloaded), StringComparison.Ordinal);
            Assert.Contains("#0000ff", ((ProjectDrawing)window.Workspace!.Document.Root.Children[0]).Text, StringComparison.Ordinal);
            Assert.False(File.Exists(copy));
            Assert.Empty(_announced);
        }
        finally
        {
            ProjectRecovery.Store = store;
        }
    }

    [AvaloniaFact]
    public async Task Reload_Reads_A_Drawing_Of_No_Project_Again_Unless_Its_Tab_Holds_Edits()
    {
        var (window, port) = await Serving();
        var viewer = window.GetVisualDescendants().OfType<SvgViewer>().First();
        await using var client = await Client(port);
        var path = Path.Combine(_directory, "drawing.svg");

        var same = await Call(client, "reload", new());

        File.WriteAllText(path, Drawing.Replace("#00ff00", "#0000ff", StringComparison.Ordinal));

        var reloaded = await Call(client, "reload", new());

        File.WriteAllText(path, "<svg><rect");

        var unloadable = await Call(client, "reload", new());

        await Call(client, "set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#ff0000" } }, ["summary"] = "red" });

        var refused = await Call(client, "reload", new());

        Assert.NotEqual(true, same.IsError);
        Assert.Equal("Nothing changed: the file holds what Studio has.", Text(same));
        Assert.NotEqual(true, reloaded.IsError);
        Assert.Equal($"Read {path} again. Its undo history started again.", Text(reloaded));
        Assert.True(unloadable.IsError);
        Assert.Matches(@"^drawing\.svg couldn't be opened: \S", Text(unloadable));
        Assert.EndsWith("\nThe tab still shows what it had.", Text(unloadable), StringComparison.Ordinal);
        Assert.True(refused.IsError);
        Assert.StartsWith("Nothing was reloaded: the drawing in front holds edits not saved. ", Text(refused), StringComparison.Ordinal);
        Assert.Contains("#ff0000", viewer.Source, StringComparison.Ordinal);
        Assert.Empty(_announced);
    }

    /// <remarks>In the instructions alone, not the rules every model is given: the panel's small one has no reload to call.</remarks>
    [AvaloniaFact]
    public async Task Claude_Code_Is_Told_Studio_Does_Not_Watch_The_Disk()
    {
        var (_, port) = await Serving();
        await using var client = await Client(port);

        Assert.Contains("Studio does not watch the disk", client.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("call reload", client.ServerInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("reload", AssistantSession.SystemPrompt(small: true), StringComparison.Ordinal);
    }

    /// <summary>The previews are the CDN's, which the plan does not count: no download, and no icon's details either.</summary>
    [AvaloniaFact]
    public async Task Streamline_Search_Answers_A_Sheet_Of_Previews_And_A_Line_For_Each_Icon()
    {
        var streamline = new Streamline();

        streamline.Paths["/v1/search/global"] = request => Streamline.Answer(
            request.RequestUri!.Query.Contains("query=zebra", StringComparison.Ordinal)
                ? Page("results", Array.Empty<string>(), more: false, next: 0)
                : Page("results", new[] { Icon("ico_a", "bell"), Icon("ico_b", "bin", free: false), Icon("ico_c", "cog") }, more: true, next: 24),
            "application/json");
        streamline.Preview("ico_a");
        streamline.Preview("ico_b");

        var (_, port) = await Streaming(streamline);
        await using var client = await Client(port);

        var found = await Call(client, "streamline_search", new() { ["query"] = "bell", ["style"] = "Line" });
        var none = await Call(client, "streamline_search", new() { ["query"] = "zebra" });
        var sheet = Png(found);

        Assert.NotEqual(true, found.IsError);
        Assert.Equal((8 + 3 * 72, 8 + 72), (sheet.Width, sheet.Height));

        // ico_c has no preview on the CDN, so its cell is left as the plate.
        Assert.NotEqual(s_light, sheet.GetPixel(8 + 32, 8 + 32));
        Assert.Equal(s_light, sheet.GetPixel(8 + 2 * 72 + 32, 8 + 32));
        Assert.Contains("\n1. bell: ico_a, Core Duo, row 1, column 1\n", Text(found), StringComparison.Ordinal);
        Assert.Contains("\n2. bin: ico_b, Core Duo, row 1, column 2, locked\n", Text(found), StringComparison.Ordinal);
        Assert.Contains("\n3. cog: ico_c, Core Duo, row 1, column 3, no preview\n", Text(found), StringComparison.Ordinal);
        Assert.EndsWith("offset 24 reads on.", Text(found), StringComparison.Ordinal);
        Assert.Contains(streamline.Asked, asked => asked.StartsWith("/v1/search/global?", StringComparison.Ordinal)
                                                   && asked.Contains("query=bell", StringComparison.Ordinal)
                                                   && asked.Contains("limit=24", StringComparison.Ordinal)
                                                   && asked.Contains("style=line", StringComparison.Ordinal));
        Assert.Contains("/ico_c.png", streamline.Asked);
        Assert.DoesNotContain(streamline.Asked, asked => asked.StartsWith("/v1/icons/", StringComparison.Ordinal));

        Assert.NotEqual(true, none.IsError);
        Assert.Equal("Nothing on Streamline matches 'zebra'.", Text(none));
        Assert.Empty(none.Content.OfType<ImageContentBlock>());
        Assert.Empty(_announced);
    }

    [AvaloniaFact]
    public async Task Streamline_Search_Without_A_Key_Or_Past_The_Limit_Is_An_Error_That_Says_Why()
    {
        var lifts = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var streamline = new Streamline();

        streamline.Paths["/v1/search/global"] = _ =>
        {
            var spent = Streamline.Refused(HttpStatusCode.TooManyRequests, "Hourly limit reached");

            spent.Headers.RetryAfter = new RetryConditionHeaderValue(lifts);

            return spent;
        };

        var (window, port) = await Streaming(streamline, key: null);
        await using var client = await Client(port);

        var keyless = await Call(client, "streamline_search", new() { ["query"] = "bell" });

        window.Streamline.StoredKey = () => "sk_test";
        await window.Streamline.RefreshAsync();

        var spent = await Call(client, "streamline_search", new() { ["query"] = "bell" });

        Assert.True(keyless.IsError);
        Assert.Equal("Searching Streamline needs an API key, which the person sets in Studio's Settings.", Text(keyless));
        Assert.Empty(keyless.Content.OfType<ImageContentBlock>());
        Assert.True(spent.IsError);
        Assert.Equal($"Hourly limit reached. Streamline's limit lifts {lifts.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}.", Text(spent));
        Assert.Single(streamline.Asked);
    }

    [AvaloniaFact]
    public async Task Streamline_Search_Refuses_A_Style_Streamline_Has_Not_Got_Without_Asking_It()
    {
        var streamline = new Streamline();
        var (_, port) = await Streaming(streamline);
        await using var client = await Client(port);

        var refused = await Call(client, "streamline_search", new() { ["query"] = "bell", ["style"] = "sparkly" });

        Assert.True(refused.IsError);
        Assert.Equal(
            "Streamline has no style 'sparkly'; it has line, solid, flat, duo, hand-drawn, creative, gradient, remix, neon, pop, light, glyph, minimal, outlined, geometric, bold, stroke, wireframe, filled.",
            Text(refused));
        Assert.Empty(streamline.Asked);
    }

    /// <remarks>A model's choice is not the person's, so it is not remembered as what Studio is sure of next time.</remarks>
    [AvaloniaFact]
    public async Task Streamline_Import_Brings_Icons_In_Through_Their_Template_As_One_Undo_Step()
    {
        var (window, port) = await Streaming(Glyphs("line-glyphs", ("ico_b", "bin"), ("ico_c", "cog")));
        await using var client = await Client(port);
        var scheme = (ProjectGroup)window.Workspace!.Document.Root.Children[0];

        var imported = await Call(client, "streamline_import", new() { ["hashes"] = new[] { "ico_b", "ico_c" }, ["parent"] = "0" });
        var added = scheme.Children.Skip(2).OfType<ProjectDrawing>().ToList();

        Assert.NotEqual(true, imported.IsError);
        Assert.Equal(
            "Imported 2 icons into 0 Scheme: 0/2 bin, 0/3 cog. It is one undo step.\n"
            + "bin, cog: through Accent glyph, Studio's best guess; Mono glyph, Keep colours would fit too.",
            Text(imported));
        Assert.Equal(new[] { "streamline:ico_b", "streamline:ico_c" }, added.Select(drawing => drawing.Source));
        Assert.All(added, drawing => Assert.Contains("fill=\"{{ stateAccentColor }}\"", drawing.Text, StringComparison.Ordinal));
        Assert.Equal("Claude Code: import from Streamline", window.Workspace.UndoLabel);
        Assert.Same(added[^1], ((TabItem)Tabs(window).SelectedItem!).Tag);
        Assert.Null(TemplateLibrary.Remembered(TemplateLibrary.Prepare(Glyph, "line-glyphs", null, "bin", new[] { "#000000" })));

        var undone = await Call(client, "undo", new());

        Assert.Equal("Took back \"Claude Code: import from Streamline\".", Text(undone));
        Assert.Equal(new[] { "Bell", "Core Duo" }, scheme.Children.Select(child => child.Name));
        Assert.Empty(_announced);
    }

    /// <summary>One at a time: what came before a spent limit lands, and what is after it is never asked for.</summary>
    [AvaloniaFact]
    public async Task Streamline_Import_Skips_What_The_Group_Has_And_Keeps_What_Came_Before_A_Spent_Limit()
    {
        var lifts = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var streamline = Glyphs("sure-glyphs", ("ico_a", "a"), ("ico_b", "bin"), ("ico_c", "cog"), ("ico_d", "dot"));

        streamline.Paths["/v1/icons/ico_c/download/svg"] = _ =>
        {
            var spent = Streamline.Refused(HttpStatusCode.TooManyRequests, "Weekly download limit reached");

            spent.Headers.RetryAfter = new RetryConditionHeaderValue(lifts);

            return spent;
        };
        Sure("sure-glyphs");

        var (window, port) = await Streaming(streamline);
        await using var client = await Client(port);

        // 0/1/0 a came from ico_a, a group further down.
        var imported = await Call(client, "streamline_import", new() { ["hashes"] = new[] { "ico_a", "ico_b", "ico_c", "ico_d" }, ["parent"] = "0" });
        var again = await Call(client, "streamline_import", new() { ["hashes"] = new[] { "ico_a", "ico_b" }, ["parent"] = "0" });

        Assert.NotEqual(true, imported.IsError);
        Assert.Equal(
            "Imported 1 icon into 0 Scheme: 0/2 bin. It is one undo step.\n"
            + "Already there, so not downloaded: 0/1/0 a.\n"
            + $"ico_c: Weekly download limit reached. Streamline's limit lifts {lifts.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}.\n"
            + "Stopped there, so ico_d was not downloaded.\n"
            + "bin: through Accent glyph, Studio's sure choice.",
            Text(imported));
        Assert.DoesNotContain(streamline.Asked, asked => asked.StartsWith("/v1/icons/ico_a", StringComparison.Ordinal) || asked.StartsWith("/v1/icons/ico_d", StringComparison.Ordinal));
        Assert.NotEqual(true, again.IsError);
        Assert.Equal("Nothing changed: 0 Scheme has every one already, as 0/1/0 a, 0/2 bin.", Text(again));
        Assert.Equal(1, streamline.Count("/v1/icons/ico_b/download"));

        Assert.True(window.Workspace!.Undo());
        Assert.False(window.Workspace.CanUndo);
        Assert.Empty(_announced);
    }

    /// <summary>One the key may not have, Streamline does not know or Studio cannot read fails on its own, so what is after it still comes.</summary>
    [AvaloniaFact]
    public async Task Streamline_Import_Passes_Over_An_Icon_It_May_Not_Have_And_Brings_In_The_Rest()
    {
        var streamline = Glyphs("line-glyphs", ("ico_b", "bin"), ("ico_d", "dot"), ("ico_e", "eye"));

        streamline.Paths["/v1/icons/ico_b/download/svg"] = _ => Streamline.Refused(HttpStatusCode.Forbidden, "Your plan does not include this icon");
        streamline.Svg("ico_d", """<!DOCTYPE svg [<!ENTITY ink "#000000">]><svg xmlns="http://www.w3.org/2000/svg" />""");

        var (_, port) = await Streaming(streamline);
        await using var client = await Client(port);

        // ico_c is not on Streamline, so its details answer 404.
        var imported = await Call(client, "streamline_import", new() { ["hashes"] = new[] { "ico_b", "ico_c", "ico_d", "ico_e" }, ["parent"] = "0" });

        Assert.NotEqual(true, imported.IsError);
        Assert.Equal(
            "Imported 1 icon into 0 Scheme: 0/2 eye. It is one undo step.\n"
            + "ico_b: Streamline refused: Your plan does not include this icon\n"
            + "ico_c: Streamline answered 404: Resource not found\n"
            + "ico_d: This file declares entities of its own, which cannot be written back as they were written.\n"
            + "eye: through Accent glyph, Studio's best guess; Mono glyph, Keep colours would fit too.",
            Text(imported));
        Assert.Equal(1, streamline.Count("/v1/icons/ico_e/download"));
        Assert.Empty(_announced);
    }

    /// <summary>Nor no icon or more than twenty, nor without a key: each download counts, so nothing is asked for until the call is one that can land.</summary>
    [AvaloniaFact]
    public async Task Streamline_Import_Refuses_A_Template_The_Project_Has_Not_Got_Before_Downloading()
    {
        var streamline = Glyphs("line-glyphs", ("ico_b", "bin"));

        streamline.Json("/v1/icons/ico_d", Icon("ico_d", "dot", "line-glyphs", colour: null));
        streamline.Svg("ico_d", Duo);

        var (window, port) = await Streaming(streamline, key: null);
        await using var client = await Client(port);
        var scheme = (ProjectGroup)window.Workspace!.Document.Root.Children[0];

        var unknown = await Call(client, "streamline_import", new() { ["hashes"] = new[] { "ico_b" }, ["parent"] = "0", ["template"] = "Sparkle" });
        var none = await Call(client, "streamline_import", new() { ["hashes"] = Array.Empty<string>(), ["parent"] = "0" });
        var many = await Call(client, "streamline_import", new() { ["hashes"] = Enumerable.Range(0, 21).Select(n => $"ico_{n}").ToArray(), ["parent"] = "0" });
        var keyless = await Call(client, "streamline_import", new() { ["hashes"] = new[] { "ico_b" }, ["parent"] = "0", ["template"] = "Mono glyph" });

        // Nothing would be downloaded, so neither the key nor the panel is asked about.
        var there = await Call(client, "streamline_import", new() { ["hashes"] = new[] { "ico_a" }, ["parent"] = "0" });

        Assert.True(unknown.IsError);
        Assert.Equal("The project has no template 'Sparkle'. Its own are Accent glyph, Mono glyph; Keep colours brings the icons in as Streamline drew them.", Text(unknown));
        Assert.True(none.IsError);
        Assert.Equal("Name the icons to import by the hashes streamline_search lists.", Text(none));
        Assert.True(many.IsError);
        Assert.Equal("21 icons are more than the 20 one call imports: each is a download Streamline counts against the key's plan.", Text(many));
        Assert.True(keyless.IsError);
        Assert.Equal("Importing from Streamline needs an API key, which the person sets in Studio's Settings.", Text(keyless));
        Assert.NotEqual(true, there.IsError);
        Assert.Equal("Nothing changed: 0 Scheme has every one already, as 0/1/0 a.", Text(there));
        Assert.Empty(streamline.Asked);

        window.Streamline.StoredKey = () => "sk_test";
        await window.Streamline.RefreshAsync();

        // dot has two colours, which Mono glyph does not match, so it keeps them.
        var imported = await Call(client, "streamline_import", new() { ["hashes"] = new[] { "ico_b", "ico_d" }, ["parent"] = "0", ["template"] = "Mono glyph" });

        Assert.NotEqual(true, imported.IsError);
        Assert.Equal(
            "Imported 2 icons into 0 Scheme: 0/2 bin, 0/3 dot. It is one undo step.\n"
            + "bin: through Mono glyph, as asked.\n"
            + "dot: through Keep colours, as Mono glyph does not fit it.",
            Text(imported));
        Assert.Contains("fill=\"{{ stateBlackColor }}\"", ((ProjectDrawing)scheme.Children[2]).Text, StringComparison.Ordinal);
        Assert.Contains("fill=\"#fb3e72\"", ((ProjectDrawing)scheme.Children[3]).Text, StringComparison.Ordinal);
        Assert.Empty(_announced);
    }

    /// <summary>A tool's import and the panel's share the one flag, so neither brings icons in under the other.</summary>
    [AvaloniaFact]
    public async Task Streamline_Import_Waits_For_Icons_The_Panel_Is_Bringing_In()
    {
        var held = new TaskCompletionSource();
        var streamline = Glyphs("line-glyphs", ("ico_b", "bin"), ("ico_c", "cog"));

        streamline.Json("/v1/search/global", Page("results", new[] { Icon("ico_c", "cog", "line-glyphs") }, more: false, next: 1));
        streamline.Holding = request => request.RequestUri!.AbsolutePath == "/v1/icons/ico_c/download/svg" ? held.Task : Task.CompletedTask;

        var (window, port) = await Streaming(streamline);
        await using var client = await Client(port);
        var panel = window.Streamline;
        var scheme = (ProjectGroup)window.Workspace!.Document.Root.Children[0];

        // Turned down, so the panel's own import ends once its download is let through.
        window.ShowImport = _ => Task.FromResult(false);

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);

        var picking = panel.OpenImportAsync();
        var refused = await Call(client, "streamline_import", new() { ["hashes"] = new[] { "ico_b" }, ["parent"] = "0" });

        Assert.True(refused.IsError);
        Assert.Equal("Icons are on their way in from the Streamline panel; try again once they have landed.", Text(refused));
        Assert.DoesNotContain(streamline.Asked, asked => asked.StartsWith("/v1/icons/ico_b", StringComparison.Ordinal));
        Assert.False(picking.IsCompleted);

        held.SetResult();
        await picking;

        var imported = await Call(client, "streamline_import", new() { ["hashes"] = new[] { "ico_b" }, ["parent"] = "0" });

        Assert.NotEqual(true, imported.IsError);
        Assert.Equal("streamline:ico_b", ((ProjectDrawing)scheme.Children[^1]).Source);
        Assert.Empty(_announced);
    }

    /// <summary>The other way round: while a tool's icons are on their way, the panel's button, an update and a family wait.</summary>
    [AvaloniaFact]
    public async Task The_Panel_Waits_For_Icons_A_Tool_Is_Bringing_In()
    {
        var held = new TaskCompletionSource();
        var streamline = Glyphs("line-glyphs", ("ico_b", "bin"), ("ico_c", "cog"));

        streamline.Json("/v1/search/global", Page("results", new[] { Icon("ico_c", "cog", "line-glyphs") }, more: false, next: 1));
        streamline.Holding = request => request.RequestUri!.AbsolutePath == "/v1/icons/ico_b/download/svg" ? held.Task : Task.CompletedTask;

        var (window, port) = await Streaming(streamline);
        await using var client = await Client(port);
        var panel = window.Streamline;
        var scheme = (ProjectGroup)window.Workspace!.Document.Root.Children[0];

        // Turned down, so one let through ends rather than waits on a window.
        window.ShowImport = _ => Task.FromResult(false);
        panel.ConfirmFamily = _ => Task.FromResult(false);

        await panel.SearchAsync();
        await panel.PickAsync(0, KeyModifiers.None);

        var importing = Call(client, "streamline_import", new() { ["hashes"] = new[] { "ico_b" }, ["parent"] = "0" });

        await Until(() => streamline.Count("/v1/icons/ico_b/download") == 1);

        var button = panel.OpenImportAsync();

        await panel.UpdateAsync((ProjectDrawing)((ProjectGroup)scheme.Children[1]).Children[0]);

        var update = panel.Said;

        await panel.ImportFamilyAsync(panel.Results[0]);

        Assert.True(button.IsCompleted);
        Assert.Equal("Other icons are on their way in; update this one once they are.", update);
        Assert.Equal("Other icons are on their way in; import the family once they are.", panel.Said);
        Assert.DoesNotContain(streamline.Asked, asked => asked.StartsWith("/v1/icons/ico_a", StringComparison.Ordinal)
                                                       || asked.StartsWith("/v1/icons/ico_c/download", StringComparison.Ordinal)
                                                       || asked.StartsWith("/v1/famil", StringComparison.Ordinal));

        held.SetResult();

        var imported = await importing;

        Assert.NotEqual(true, imported.IsError);
        Assert.Equal("streamline:ico_b", ((ProjectDrawing)scheme.Children[^1]).Source);

        await panel.OpenImportAsync();

        Assert.Equal(1, streamline.Count("/v1/icons/ico_c/download"));
        Assert.Empty(_announced);
    }

    /// <summary>An icon does not say its style, so a template matching one fits only what a search found under it, as in the panel.</summary>
    [AvaloniaFact]
    public async Task Streamline_Import_Fits_A_Template_To_The_Style_A_Search_Found_The_Icon_Under()
    {
        const string line = """<e:recipe name="Line glyph"><e:match colors="1" style="line" /><e:slot name="glyph" rest="true">stateBlackColor</e:slot></e:recipe>""";
        var streamline = Glyphs("line-glyphs", ("ico_b", "bin"), ("ico_c", "cog"));

        streamline.Json("/v1/search/global", Page("results", new[] { Icon("ico_b", "bin", "line-glyphs") }, more: false, next: 1));

        var (_, port) = await Streaming(streamline, text: StreamlinePanelTests.Project.Replace("""<e:recipe name="Accent glyph">""", line + """<e:recipe name="Accent glyph">""", StringComparison.Ordinal));
        await using var client = await Client(port);

        var unsearched = await Call(client, "streamline_import", new() { ["hashes"] = new[] { "ico_c" }, ["parent"] = "0", ["template"] = "Line glyph" });
        var found = await Call(client, "streamline_search", new() { ["query"] = "bin", ["style"] = "line" });
        var searched = await Call(client, "streamline_import", new() { ["hashes"] = new[] { "ico_b" }, ["parent"] = "0", ["template"] = "Line glyph" });

        Assert.NotEqual(true, found.IsError);
        Assert.Equal(
            "Imported 1 icon into 0 Scheme: 0/2 cog. It is one undo step.\ncog: through Accent glyph, as Line glyph does not fit it.",
            Text(unsearched));
        Assert.Equal(
            "Imported 1 icon into 0 Scheme: 0/3 bin. It is one undo step.\nbin: through Line glyph, as asked.",
            Text(searched));
        Assert.Empty(_announced);
    }

    /// <remarks>Closing the window with an edit asks, in Studio's own dialog, whether to throw it away.</remarks>
    [AvaloniaFact]
    public async Task A_Call_While_Studio_Asks_The_Person_Is_Refused_As_An_Error_And_Changes_Nothing()
    {
        var (window, port) = await Serving();
        await using var client = await Client(port);
        var viewer = window.GetVisualDescendants().OfType<SvgViewer>().First();

        viewer.SetSource(Drawing.Replace("#00ff00", "#123456", StringComparison.Ordinal));
        window.Close();
        Dispatcher.UIThread.RunJobs();

        var question = Assert.Single(window.OwnedWindows);
        var refused = await Call(client, "set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#0000ff" } }, ["summary"] = "blue" });
        var read = await Call(client, "get_drawing", new());

        Assert.True(refused.IsError);
        Assert.StartsWith("Nothing was done: Studio is waiting on the person to answer \"Unsaved changes\"", Text(refused), StringComparison.Ordinal);
        Assert.NotEqual(true, read.IsError);
        Assert.Contains("#123456", viewer.Source, StringComparison.Ordinal);

        question.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <remarks>The settings ask nothing about the drawings, so an edit made under them loses nothing.</remarks>
    [AvaloniaFact]
    public async Task A_Call_While_The_Settings_Are_Open_Goes_Through()
    {
        var (window, port) = await Serving();
        await using var client = await Client(port);
        var viewer = window.GetVisualDescendants().OfType<SvgViewer>().First();

        var shown = window.ShowSettingsAsync();
        Dispatcher.UIThread.RunJobs();

        var settings = Assert.IsType<SettingsWindow>(Assert.Single(window.OwnedWindows));
        var result = await Call(client, "set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#0000ff" } }, ["summary"] = "blue" });

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("one undo step", Text(result), StringComparison.Ordinal);
        Assert.Contains("#0000ff", viewer.Source, StringComparison.Ordinal);

        settings.Close();
        await shown;
    }

    [AvaloniaFact]
    public async Task An_Argument_Null_Or_Of_The_Wrong_Kind_Is_An_Error_Naming_It()
    {
        var (window, port) = await Serving();
        await using var client = await Client(port);
        var before = window.GetVisualDescendants().OfType<SvgViewer>().First().Source;

        var nulled = await Call(client, "set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "red" } }, ["summary"] = null });
        var numbered = await Call(client, "set_node", new() { ["node"] = "0", ["setting"] = "width", ["value"] = 5 });
        var listed = await Call(client, "set_attributes", new() { ["key"] = "0", ["attributes"] = "fill=red", ["summary"] = "red" });
        var placed = await Call(client, "insert_element", new() { ["target"] = "", ["markup"] = "<circle />", ["where"] = "Under", ["summary"] = "x" });
        var nested = await Call(client, "set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = 5, value = "red" } }, ["summary"] = "red" });
        var optional = await Call(client, "get_element", new() { ["key"] = "0", ["node"] = null });
        var refusals = new[] { nulled, numbered, listed, placed, nested };

        Assert.NotEqual(true, optional.IsError);

        Assert.All(refusals, result => Assert.True(result.IsError, Text(result)));
        Assert.Contains("'summary' cannot be null: it takes a string.", Text(nulled), StringComparison.Ordinal);
        Assert.Contains("'value' takes a string, not 5.", Text(numbered), StringComparison.Ordinal);
        Assert.Contains("'attributes' takes an array, not \"fill=red\".", Text(listed), StringComparison.Ordinal);
        Assert.Contains("'where' takes one of Before, After, Inside, not \"Under\".", Text(placed), StringComparison.Ordinal);
        Assert.Contains("'attributes' takes an array, and [0].name in what was given does not read as its schema says.", Text(nested), StringComparison.Ordinal);
        Assert.All(refusals, result => Assert.DoesNotContain("Object reference", Text(result), StringComparison.Ordinal));
        Assert.All(refusals, result => Assert.DoesNotContain("System.", Text(result), StringComparison.Ordinal));
        Assert.All(refusals, result => Assert.DoesNotContain("Svg.", Text(result), StringComparison.Ordinal));
        Assert.Equal(before, window.GetVisualDescendants().OfType<SvgViewer>().First().Source);
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

    /// <summary>A window serving <paramref name="text"/>, whose Streamline panel answers from <paramref name="streamline"/> with <paramref name="key"/>.</summary>
    private async Task<(MainWindow Window, int Port)> Streaming(Streamline streamline, string? key = "sk_test", string text = StreamlinePanelTests.Project)
    {
        var path = Path.Combine(_directory, "icons.svgstudio");

        File.WriteAllText(path, text);

        var (window, port) = await Serving(path);

        window.Streamline.StoredKey = () => key;
        window.Streamline.Connect = given => new StreamlineClient(given, new HttpClient(streamline));
        await window.Streamline.RefreshAsync();

        return (window, port);
    }

    /// <summary>Streamline giving each of <paramref name="icons"/> as a glyph of <paramref name="family"/>: its details, and its download.</summary>
    private static Streamline Glyphs(string family, params (string Hash, string Name)[] icons)
    {
        var streamline = new Streamline();

        foreach (var (hash, name) in icons)
        {
            streamline.Json($"/v1/icons/{hash}", Icon(hash, name, family));
            streamline.Svg(hash, Glyph);
        }

        return streamline;
    }

    private async Task<MainWindow> Open(string? path = null)
    {
        if (path is null)
        {
            path = Path.Combine(_directory, "drawing.svg");
            File.WriteAllText(path, Drawing);
        }

        // Never closed, as nobody closes one for a client: a call that opens a dialog hangs until Call
        // gives up. The discard prompt stays the real one, which the tests of Asking answer.
        Task<T> Unanswered<T>(string asked)
        {
            _announced.Add(asked);
            return new TaskCompletionSource<T>().Task;
        }

        var window = new MainWindow
        {
            Announce = (title, _) => Unanswered<bool>(title),
            AskWhereToSave = _ => Unanswered<string?>(nameof(MainWindow.AskWhereToSave)),
            ConfirmRelax = _ => Unanswered<SvgTextLayout?>(nameof(MainWindow.ConfirmRelax)),
            ConfirmConvert = _ => Unanswered<(bool Integers, bool Organize)?>(nameof(MainWindow.ConfirmConvert)),
            ConfirmDiscardRecovery = _ => Unanswered<bool>(nameof(MainWindow.ConfirmDiscardRecovery)),
            ResolveConflicts = _ => Unanswered<bool?>(nameof(MainWindow.ResolveConflicts)),
            ShowImport = _ => Unanswered<bool>(nameof(MainWindow.ShowImport))
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

    private static TabControl Tabs(MainWindow window) => window.FindControl<TabControl>("Tabs")!;

    /// <summary>The one image an answer holds, checked to be a PNG.</summary>
    private static SKBitmap Png(CallToolResult result)
    {
        var image = Assert.Single(result.Content.OfType<ImageContentBlock>());
        var bytes = image.DecodedData.ToArray();

        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);

        return SKBitmap.Decode(bytes);
    }

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
