using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.AI;
using Svg.CodeGen.Skia;
using Svg.Viewer.Skia.Avalonia;
using Xunit;

namespace Svg.Studio.UnitTests;

/// <summary>The assistant: what it is told, and that what it does lands on the window's own history.</summary>
/// <remarks>
/// No model is called. A scripted client answers with the tool calls a model would make, and the
/// real function-invoking pipeline runs them against a real window, which is the part that is ours.
/// </remarks>
public class AssistantTests : IDisposable
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

          <group name="Badges">
            <e:code xmlns:e="https://svg.skia/expr/1.0">
              <e:param name="tint" type="color" default="#ff0000" />
            </e:code>

            <drawing name="Filled">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                <rect width="24" height="24" fill="{{ tint }}" />
              </svg>
            </drawing>
          </group>

        </studio>
        """;

    /// <summary>A project block declaring <c>angle</c>, a drawing with a <c>let angle</c> of its own, and a group's <c>tint</c>.</summary>
    /// <remarks>The shape an agent's Thermostat had when it was told it was clean and Studio said otherwise.</remarks>
    private const string Declared = """
        <studio namespace="Demo.Icons">
          <e:code xmlns:e="https://svg.skia/expr/1.0">
            <e:param name="angle" type="number" default="0" />
          </e:code>

          <drawing name="Dial">
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24">
              <defs><e:code><e:let name="angle">45</e:let></e:code></defs>
              <rect width="24" height="24" transform="rotate({{ angle }})" />
            </svg>
          </drawing>

          <group name="Badges">
            <e:code xmlns:e="https://svg.skia/expr/1.0">
              <e:param name="tint" type="color" default="#ff0000" />
            </e:code>

            <drawing name="Filled">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                <rect width="24" height="24" fill="{{ tint }}" />
              </svg>
            </drawing>
          </group>
        </studio>
        """;

    /// <summary>A group inside a group: 0 = home, 1 = Badges, 1/0 = Filled, 1/1 = Round, 1/1/0 = Dot.</summary>
    private const string Nested = """
        <studio namespace="Demo.Icons">
          <drawing name="home">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#00ff00" />
            </svg>
          </drawing>

          <group name="Badges">
            <drawing name="Filled">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                <rect width="24" height="24" />
              </svg>
            </drawing>

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

    /// <summary>A drawing that declares entities of its own, which Studio draws and has no source to edit for.</summary>
    internal const string Entities = """
        <?xml version="1.0"?>
        <!DOCTYPE svg [ <!ENTITY green "#00ff00"> ]>
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
          <rect width="24" height="24" fill="&green;" />
        </svg>
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Scratch.Delete(_directory);

    [Fact]
    public void The_Docs_Are_What_A_Reader_Of_The_Site_Sees()
    {
        var whole = AssistantDocs.Whole;

        Assert.DoesNotContain("{%{", whole, StringComparison.Ordinal);
        Assert.DoesNotContain("title: \"Svg Studio\"", whole, StringComparison.Ordinal);
        Assert.Contains("{{ tint }}", whole, StringComparison.Ordinal);
        Assert.Contains("# Svg Studio", whole, StringComparison.Ordinal);

        Assert.Equal(AssistantDocs.Sections.Count, AssistantDocs.Sections.Select(section => section.Id).Distinct().Count());
        Assert.All(AssistantDocs.Sections, section => Assert.Equal(section.Text, AssistantDocs.Read(section.Id)));
        Assert.Contains(AssistantDocs.Sections, section => section.Id == "svg-studio#the-attributes-tab");
    }

    [Fact]
    public void A_Heading_Inside_A_Code_Block_Does_Not_Cut_A_Section()
    {
        var sections = AssistantDocs.Split("doc", "# Doc\n\nintro\n\n## First\n\n```sh\n## not a heading\n```\n\n## Second\n\ntext").ToList();

        Assert.Equal(new[] { "doc", "doc#first", "doc#second" }, sections.Select(section => section.Id));
        Assert.Contains("## not a heading", sections[1].Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void A_Small_Model_Reads_The_Docs_A_Section_At_A_Time_With_Fewer_Tools()
    {
        var window = new MainWindow();
        var session = new AssistantSession(new AssistantTools(window));
        var chat = new ScriptedChat();

        session.Use(new ScriptedProvider(chat), new AssistantModel("small", "Small", 4096));

        Assert.True(session.Small);

        var small = new AssistantTools(window).For(small: true, docs: true).Select(tool => tool.Name).ToList();
        var large = new AssistantTools(window).For(small: false, docs: false).Select(tool => tool.Name).ToList();

        Assert.Equal(new[] { "read_doc", "get_project", "get_drawing", "set_attributes", "undo" }, small);
        Assert.DoesNotContain("read_doc", large);
        Assert.Contains("save", large);
        Assert.True(large.Count > small.Count);

        session.Use(new ScriptedProvider(chat), new AssistantModel("large", "Large", 1_000_000));

        Assert.False(session.Small);

        // And is not told about saving at all, having no tool to do it: told it asks first, a 3B
        // on-device model asked to save after every edit.
        Assert.DoesNotContain("Saving", AssistantSession.SystemPrompt(small: true), StringComparison.Ordinal);
        Assert.Contains("Saving, removing and committing ask the person first", AssistantSession.SystemPrompt(small: false), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Small_Model_Is_Not_Told_There_Is_Anything_To_Save()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "1", ["setting"] = "name", ["value"] = "Tags" }), Say("Renamed.")), "rename");

        Assert.True(window.Workspace!.IsEdited);
        Assert.Contains("unsaved changes", new AssistantTools(window).Context(small: false), StringComparison.Ordinal);
        Assert.DoesNotContain("unsaved", new AssistantTools(window).Context(small: true), StringComparison.Ordinal);

        // Nor of the tabs holding its edits, in front or behind, which it has no open or save to act on.
        var root = window.Workspace.Document.Root;

        await window.ShowAsync(root.Children[0]);
        await window.ShowAsync(((ProjectGroup)root.Children[1]).Children[0]);
        Dispatcher.UIThread.RunJobs();

        var chat = new ScriptedChat(
            Call("set_attributes", new() { ["node"] = "0", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#123456" } }, ["summary"] = "darken" }),
            Call("set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "stroke", value = "black" } }, ["summary"] = "outline" }),
            Call("get_drawing", new() { ["node"] = "0" }),
            Say("Done."));

        await Send(window, chat, "darken and outline", small: true);

        Assert.Contains("Tabs holding edits not yet in the project: 0 home, 1/0 Filled", new AssistantTools(window).Context(small: false), StringComparison.Ordinal);
        Assert.DoesNotContain("sav", new AssistantTools(window).Context(small: true), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sav", chat.Results[2], StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task An_Edit_Is_One_Undo_Step_Labelled_As_The_Assistants()
    {
        var window = await Host(Write("drawing.svg", Drawing));
        var viewer = Viewer(window);
        var before = viewer.Source;

        var chat = new ScriptedChat(
            Call("set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#ff0000" }, new { name = "opacity", value = "0.5" } }, ["summary"] = "make it red" }),
            Say("It is red now."));

        var reply = await Send(window, chat, "make the square red");

        Assert.Equal("It is red now.", reply);
        Assert.Contains("fill=\"#ff0000\"", viewer.Source, StringComparison.Ordinal);
        Assert.Contains("opacity=\"0.5\"", viewer.Source, StringComparison.Ordinal);
        Assert.Equal("Assistant: make it red", viewer.UndoLabel);

        // What the model was told back is the result, not an error.
        Assert.Contains("one undo step", chat.Results.Single(), StringComparison.Ordinal);

        // Both attributes in one step: one undo takes the drawing back to exactly what it was.
        Assert.True(window.Undo());
        Assert.Equal(before, viewer.Source);
    }

    [AvaloniaFact]
    public async Task A_Refused_Edit_Is_Said_Back_And_Changes_Nothing()
    {
        var window = await Host(Write("drawing.svg", Drawing));
        var before = Viewer(window).Source;

        var chat = new ScriptedChat(
            Call("set_attributes", new() { ["key"] = "7", ["attributes"] = new[] { new { name = "fill", value = "red" } }, ["summary"] = "nothing" }),
            Say("That element does not exist."));

        await Send(window, chat, "recolour element 7");

        Assert.Equal(before, Viewer(window).Source);
        Assert.DoesNotContain("one undo step", chat.Results.Single(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Undo_Is_The_Documents_Even_While_The_Chat_Box_Has_Focus()
    {
        var window = await Host(Write("drawing.svg", Drawing));
        var viewer = Viewer(window);
        var before = viewer.Source;

        viewer.SetSource(before.Replace("#00ff00", "#0000ff", StringComparison.Ordinal));

        // The chat's input is where the caret is while a reply runs, and the window's own Undo would
        // take back what was typed there instead.
        var box = new TextBox();
        window.GetVisualDescendants().OfType<Panel>().First().Children.Add(box);
        Dispatcher.UIThread.RunJobs();
        box.Focus();

        await Send(window, new ScriptedChat(Call("undo", new()), Say("Undone.")), "undo that");

        Assert.Equal(before, viewer.Source);
    }

    [AvaloniaFact]
    public async Task A_Drawing_In_No_Tab_Is_Edited_In_The_Project()
    {
        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);
        var workspace = window.Workspace!;

        var chat = new ScriptedChat(
            Call("get_project", new()),
            Call("set_attributes", new() { ["node"] = "0", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#123456" } }, ["summary"] = "darken home" }),
            Say("Done."));

        await Send(window, chat, "darken home");

        Assert.Contains("0 drawing home", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("1/0 drawing Filled", chat.Results[0], StringComparison.Ordinal);

        var home = (ProjectDrawing)workspace.Document.Root.Children[0];

        Assert.Contains("#123456", home.Text, StringComparison.Ordinal);
        Assert.Equal("Assistant: darken home", workspace.UndoLabel);

        Assert.True(window.Undo());
        Assert.DoesNotContain("#123456", home.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Keys_Are_The_Files_Even_Where_A_Group_Writes_Declarations_In()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var drawing = window.Workspace!.Document.Root.Children[1] is ProjectGroup group ? group.Children[0] : null;

        await window.ShowAsync(drawing!);
        Dispatcher.UIThread.RunJobs();

        var viewer = Viewer(window);

        // The built drawing has the group's block written into it, so its rect is not at 0.
        Assert.NotEqual("0", viewer.SourceAddresses().Single(pair => pair.Value == "0").Key);

        var chat = new ScriptedChat(
            Call("select", new() { ["keys"] = new[] { "0" } }),
            Call("set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "stroke", value = "black" } }, ["summary"] = "outline" }),
            Say("Done."));

        await Send(window, chat, "outline the rect");

        Assert.Equal("0", viewer.SourceAddresses()[viewer.Elements.SelectedAddresses.Single()]);
        Assert.Contains("<rect width=\"24\" height=\"24\" fill=\"{{ tint }}\" stroke=\"black\" />", viewer.Source, StringComparison.Ordinal);
        Assert.Contains("Selected elements: 0", new AssistantTools(window).Context(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Saving_Waits_For_The_Person()
    {
        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);

        var edit = Call("set_attributes", new() { ["node"] = "0", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#123456" } }, ["summary"] = "darken" });

        await Send(window, new ScriptedChat(edit, Call("save", new()), Say("Not saved.")), "darken and save", allow: false);

        Assert.DoesNotContain("#123456", File.ReadAllText(path), StringComparison.Ordinal);

        await Send(window, new ScriptedChat(Call("save", new()), Say("Saved.")), "save", allow: true);

        Assert.Contains("#123456", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Project_Node_Is_Renamed_Through_Its_Settings()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "1", ["setting"] = "name", ["value"] = "Tags" }), Say("Renamed.")), "rename Badges");

        Assert.Equal("Tags", window.Workspace!.Document.Root.Children[1].Name);

        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "1", ["setting"] = "scale", ["value"] = "big" }), Say("No.")), "scale");

        Assert.Null(window.Workspace!.Document.Root.Children[1].Scale);
    }

    [AvaloniaFact]
    public async Task A_Setting_The_Node_Does_Not_Take_Is_Refused_With_The_Ones_It_Does()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var root = workspace.Document.Root;
        var badges = root.Children[1];

        // Unknown, misspelt by case, and the project's own: once an empty step, a rename that was not, and a cast that threw.
        var chat = new ScriptedChat(
            Call("set_node", new() { ["node"] = "1", ["setting"] = "colour", ["value"] = "red" }),
            Call("set_node", new() { ["node"] = "1", ["setting"] = "Name", ["value"] = "Tags" }),
            Call("set_node", new() { ["node"] = "1", ["setting"] = "cache", ["value"] = "lastValue" }),
            Say("Refused."));

        await Send(window, chat, "set what Badges does not take");

        Assert.Equal(3, chat.Results.Count);
        Assert.StartsWith("1 has no setting colour;", chat.Results[0], StringComparison.Ordinal);
        Assert.StartsWith("1 has no setting Name;", chat.Results[1], StringComparison.Ordinal);
        Assert.StartsWith("1 has no setting cache;", chat.Results[2], StringComparison.Ordinal);
        Assert.All(chat.Results, result => Assert.Contains("name, namespace, class", result, StringComparison.Ordinal));
        Assert.Equal("Badges", badges.Name);
        Assert.Null(badges.Element.Attribute("cache"));
        Assert.False(workspace.CanUndo);
        Assert.False(workspace.IsEdited);

        // Which the project itself still takes.
        var own = new ScriptedChat(Call("set_node", new() { ["node"] = "", ["setting"] = "cache", ["value"] = "lastValue" }), Say("Set."));

        await Send(window, own, "cache the project");

        Assert.Contains("one undo step", own.Results[0], StringComparison.Ordinal);
        Assert.Equal(SvgPictureCache.LastValue, root.Cache);
    }

    [AvaloniaFact]
    public async Task The_Project_Takes_No_Name_Or_Place_So_It_Still_Opens_Once_Saved()
    {
        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);
        var workspace = window.Workspace!;

        var refused = new ScriptedChat(
            Call("set_node", new() { ["node"] = "", ["setting"] = "name", ["value"] = "Icons" }),
            Call("set_node", new() { ["node"] = "", ["setting"] = "x", ["value"] = "5" }),
            Say("Refused."));

        await Send(window, refused, "name and place the project");

        Assert.All(refused.Results, result => Assert.StartsWith("(project) has no setting", result, StringComparison.Ordinal));
        Assert.Contains("helperScope", refused.Results[0], StringComparison.Ordinal);
        Assert.False(workspace.CanUndo);
        Assert.False(workspace.IsEdited);

        var saved = new ScriptedChat(
            Call("set_node", new() { ["node"] = "", ["setting"] = "class", ["value"] = "Icons" }),
            Call("save", new()),
            Say("Saved."));

        await Send(window, saved, "name the class and save", allow: true);

        Assert.Equal("Saved.", saved.Results[1]);

        var reopened = ProjectDocument.Load(path);

        Assert.Equal("Icons", reopened.Root.Class);
        Assert.Null(reopened.Root.Element.Attribute("name"));
        Assert.Null(reopened.Root.Element.Attribute("x"));
    }

    [AvaloniaFact]
    public async Task A_Size_The_Build_Would_Refuse_Is_Refused_And_A_Scale_And_A_Width_Take_Each_Others_Place()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var root = workspace.Document.Root;
        var home = root.Children[0];
        var badges = root.Children[1];

        // In a tab, whose size is worked out again from the project after every edit to it.
        await window.ShowAsync(home);
        Dispatcher.UIThread.RunJobs();

        var refused = new ScriptedChat(
            Call("set_node", new() { ["node"] = "1", ["setting"] = "scale", ["value"] = "0" }),
            Call("set_node", new() { ["node"] = "1", ["setting"] = "width", ["value"] = "-1" }),
            Call("set_node", new() { ["node"] = "1", ["setting"] = "height", ["value"] = "NaN" }),
            Call("set_node", new() { ["node"] = "1", ["setting"] = "x", ["value"] = "Infinity" }),
            Say("Refused."));

        await Send(window, refused, "size Badges as nothing can be");

        Assert.Equal(4, refused.Results.Count);
        Assert.All(refused.Results.Take(3), result => Assert.Contains("has to be a positive number", result, StringComparison.Ordinal));
        Assert.Contains("finite", refused.Results[3], StringComparison.Ordinal);
        Assert.False(badges.HasSize);
        Assert.Null(badges.Element.Attribute("x"));
        Assert.False(workspace.CanUndo);
        Assert.False(workspace.IsEdited);

        // A scale beside a width or a height is a size the build refuses, so the one written takes the other's place, and says so.
        var chat = new ScriptedChat(
            Call("set_node", new() { ["node"] = "0", ["setting"] = "width", ["value"] = "48" }),
            Call("set_node", new() { ["node"] = "0", ["setting"] = "height", ["value"] = "32" }),
            Call("set_node", new() { ["node"] = "0", ["setting"] = "scale", ["value"] = "2" }),
            Call("set_node", new() { ["node"] = "0", ["setting"] = "width", ["value"] = "48" }),
            Call("set_node", new() { ["node"] = "0", ["setting"] = "scale", ["value"] = "2" }),
            Call("set_node", new() { ["node"] = "0", ["setting"] = "height", ["value"] = "32" }),
            Call("set_node", new() { ["node"] = "1", ["setting"] = "class", ["value"] = "Badge" }),
            Say("Done."));

        await Send(window, chat, "size home every way, then name Badges' class");

        Assert.All(chat.Results, result => Assert.Contains("one undo step", result, StringComparison.Ordinal));
        Assert.Equal("Set height on 0. It is one undo step.", chat.Results[1]);
        Assert.Contains("It also cleared width and height.", chat.Results[2], StringComparison.Ordinal);
        Assert.Contains("It also cleared scale.", chat.Results[3], StringComparison.Ordinal);
        Assert.Contains("It also cleared width.", chat.Results[4], StringComparison.Ordinal);
        Assert.Contains("It also cleared scale.", chat.Results[5], StringComparison.Ordinal);
        Assert.Null(home.Width);
        Assert.Equal(32f, home.Height);
        Assert.Null(home.Scale);
        Assert.Equal("Badge", badges.Class);
    }

    [AvaloniaFact]
    public async Task Clearing_Part_Of_A_Size_A_File_Gave_Twice_Leaves_The_Rest()
    {
        // A scale beside a width, which only a file written by hand gives and the reader still takes; clearing one mends it.
        var window = await Host(Write("icons.svgstudio", Project
            .Replace("<drawing name=\"home\">", "<drawing name=\"home\" width=\"16\" scale=\"4\">", StringComparison.Ordinal)
            .Replace("<group name=\"Badges\">", "<group name=\"Badges\" width=\"48\" scale=\"2\">", StringComparison.Ordinal)
            .Replace("<drawing name=\"Filled\">", "<drawing name=\"Filled\" height=\"32\" scale=\"3\">", StringComparison.Ordinal)));
        var root = window.Workspace!.Document.Root;
        var home = root.Children[0];
        var badges = (ProjectGroup)root.Children[1];
        var filled = badges.Children[0];

        var chat = new ScriptedChat(
            Call("set_node", new() { ["node"] = "0", ["setting"] = "width" }),
            Call("set_node", new() { ["node"] = "1", ["setting"] = "scale" }),
            Call("set_node", new() { ["node"] = "1/0", ["setting"] = "height" }),
            Say("Mended."));

        await Send(window, chat, "mend the sizes");

        Assert.All(chat.Results, result => Assert.EndsWith("It is one undo step.", result, StringComparison.Ordinal));
        Assert.All(chat.Results, result => Assert.DoesNotContain("cleared", result, StringComparison.Ordinal));
        Assert.Null(home.Width);
        Assert.Equal(4f, home.Scale);
        Assert.Null(badges.Scale);
        Assert.Equal(48f, badges.Width);
        Assert.Null(filled.Height);
        Assert.Equal(3f, filled.Scale);
    }

    [AvaloniaFact]
    public async Task A_Setting_Is_Cleared_By_Leaving_Its_Value_Out()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var root = window.Workspace!.Document.Root;
        var tool = new AssistantTools(window).For(small: false, docs: false).OfType<AIFunction>().Single(tool => tool.Name == "set_node");
        var required = tool.JsonSchema.GetProperty("required").EnumerateArray().Select(name => name.GetString()).ToList();

        Assert.DoesNotContain("value", required);
        Assert.Contains("cache, helperScope and skiaSharp", tool.Description, StringComparison.Ordinal);

        var chat = new ScriptedChat(Call("set_node", new() { ["node"] = "", ["setting"] = "namespace" }), Say("Cleared."));

        await Send(window, chat, "clear the namespace");

        Assert.Contains("one undo step", chat.Results[0], StringComparison.Ordinal);
        Assert.Null(root.Namespace);
    }

    [AvaloniaFact]
    public async Task The_Project_Lists_The_Settings_Each_Node_Gives_Itself()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var chat = new ScriptedChat(
            Call("set_node", new() { ["node"] = "1", ["setting"] = "scale", ["value"] = "2" }),
            Call("get_project", new()),
            Say("Read."));

        await Send(window, chat, "scale Badges and read the project");

        var lines = chat.Results[1].Split('\n');

        Assert.Contains("(project) namespace=\"Demo.Icons\"", lines);
        Assert.Contains("1 group Badges scale=\"2\" — declares: tint", lines);
        Assert.Contains("  1/0 drawing Filled", lines);
    }

    [AvaloniaFact]
    public async Task The_Panel_Shows_Who_Said_What_In_Bubbles()
    {
        var window = await Host(Write("drawing.svg", Drawing));
        var chat = new ScriptedChat(
            Call("set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#ff0000" } }, ["summary"] = "make it red" }),
            Say("It is **red** now."));
        var providers = AssistantProviders.All;

        try
        {
            AssistantProviders.All = new IAssistantProvider[] { new ScriptedProvider(chat) };

            var panel = new AssistantPanel(new AssistantTools(window));
            var host = new Window { Content = panel };

            host.Show();
            Dispatcher.UIThread.RunJobs();

            await panel.SendAsync("make the square red");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new[] { "you", "tool", "assistant" }, panel.Transcript.Select(row => row.Role));
            Assert.Equal("make the square red", panel.Transcript[0].Text);
            Assert.Equal("It is **red** now.", panel.Transcript[2].Text);

            // Yours on the right, the assistant's on the left, the reply drawn rather than shown raw.
            var bubbles = panel.GetVisualDescendants().OfType<Border>().Where(border => border.CornerRadius.TopLeft == 10).ToList();

            Assert.Equal(HorizontalAlignment.Right, bubbles[0].HorizontalAlignment);
            Assert.Equal(HorizontalAlignment.Left, bubbles[1].HorizontalAlignment);
            Assert.Contains(bubbles[1].GetVisualDescendants().OfType<SelectableTextBlock>().Single().Inlines!, inline => inline is Bold);

            host.Close();
        }
        finally
        {
            AssistantProviders.All = providers;
        }
    }

    [AvaloniaFact]
    public async Task A_Drawing_Is_Checked_As_Studio_Builds_It()
    {
        var window = await Host(Write("declared.svgstudio", Declared));
        var chat = new ScriptedChat(
            Call("get_drawing", new() { ["node"] = "1/0" }),
            Call("get_drawing", new() { ["node"] = "0" }),
            Call("get_project", new()),
            Say("Read."));

        await Send(window, chat, "what is wrong");

        // The group's tint is inherited, so it is neither undeclared nor a problem.
        Assert.Contains("Inherited from 1: tint", chat.Results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Problem:", chat.Results[0], StringComparison.Ordinal);

        // The dial's own let and the project's param are one name twice, which its own text never shows.
        Assert.Contains("Inherited from (project): angle", chat.Results[1], StringComparison.Ordinal);
        Assert.Contains("'angle' is declared more than once", chat.Results[1], StringComparison.Ordinal);

        Assert.Contains("(project) namespace=\"Demo.Icons\" — declares: angle", chat.Results[2], StringComparison.Ordinal);
        Assert.Contains("1 group Badges — declares: tint", chat.Results[2], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_Context_Counts_What_Studio_Counts()
    {
        var window = await Host(Write("declared.svgstudio", Declared));

        await window.ShowAsync(window.Workspace!.Document.Root.Children[0]);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("1 problem(s)", new AssistantTools(window).Context(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Groups_Declarations_Are_Read_And_Replaced_As_One_Step()
    {
        var window = await Host(Write("declared.svgstudio", Declared));
        var group = (ProjectGroup)window.Workspace!.Document.Root.Children[1];
        var before = group.CodeText;
        var replaced = before.Replace("</e:code>", "  <e:param name=\"size\" type=\"number\" default=\"24\" />\n            </e:code>", StringComparison.Ordinal);

        var chat = new ScriptedChat(
            Call("get_declarations", new() { ["node"] = "1" }),
            Call("set_declarations", new() { ["node"] = "1", ["code"] = replaced, ["summary"] = "add size" }),
            Say("Added."));

        await Send(window, chat, "add a size parameter to Badges");

        Assert.Equal(before, chat.Results[0]);
        Assert.Contains("one undo step", chat.Results[1], StringComparison.Ordinal);
        Assert.Contains("name=\"size\"", group.CodeText, StringComparison.Ordinal);
        Assert.Equal("Assistant: add size", window.Workspace.UndoLabel);

        Assert.True(window.Undo());
        Assert.Equal(before, group.CodeText);
    }

    [AvaloniaFact]
    public async Task Declarations_That_Are_Not_A_Block_Or_Shadow_The_Project_Are_Refused()
    {
        var window = await Host(Write("declared.svgstudio", Declared));
        var group = (ProjectGroup)window.Workspace!.Document.Root.Children[1];
        var before = group.CodeText;
        var shadowing = before.Replace("name=\"tint\" type=\"color\" default=\"#ff0000\"", "name=\"angle\" type=\"number\" default=\"1\"", StringComparison.Ordinal);

        var chat = new ScriptedChat(
            Call("set_declarations", new() { ["node"] = "1", ["code"] = "<g />", ["summary"] = "wrong" }),
            Call("set_declarations", new() { ["node"] = "1", ["code"] = shadowing, ["summary"] = "shadow" }),
            Say("Refused."));

        await Send(window, chat, "break it");

        Assert.Contains("<e:code> block", chat.Results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("one undo step", chat.Results[1], StringComparison.Ordinal);
        Assert.Contains("angle", chat.Results[1], StringComparison.Ordinal);
        Assert.Equal(before, group.CodeText);
        Assert.False(window.Workspace.IsEdited);
    }

    [AvaloniaFact]
    public async Task Undo_Takes_Back_The_Assistants_Edit_And_Not_The_Front_Tabs()
    {
        var window = await Host(Write("declared.svgstudio", Declared));
        var dial = (ProjectDrawing)window.Workspace!.Document.Root.Children[0];
        var badge = (ProjectDrawing)((ProjectGroup)window.Workspace.Document.Root.Children[1]).Children[0];

        await window.ShowAsync(dial);
        Dispatcher.UIThread.RunJobs();

        var viewer = Viewer(window);

        // The front tab has an edit of its own; the assistant then edits a drawing in no tab.
        viewer.SetSource(viewer.Source.Replace("<rect ", "<rect stroke=\"black\" ", StringComparison.Ordinal));

        var chat = new ScriptedChat(
            Call("set_attributes", new() { ["node"] = "1/0", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#111111" } }, ["summary"] = "darken" }),
            Call("undo", new()),
            Say("Undone."));

        await Send(window, chat, "darken the badge, then undo");

        Assert.Contains("No problems", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("Took back \"Assistant: darken\"", chat.Results[1], StringComparison.Ordinal);
        Assert.DoesNotContain("#111111", badge.Text, StringComparison.Ordinal);
        Assert.Contains("stroke=\"black\"", viewer.Source, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Each_Undo_Takes_Back_One_Of_The_Assistants_Own_Edits_By_Name()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var root = workspace.Document.Root;

        workspace.Edit("mine");

        var chat = new ScriptedChat(
            Call("set_node", new() { ["node"] = "1", ["setting"] = "name", ["value"] = "Tags" }),
            Call("remove", new() { ["node"] = "0" }),
            Call("undo", new()),
            Call("undo", new()),
            Say("As it was."));

        await Send(window, chat, "rename Badges, remove home, then take both back", allow: true);

        Assert.Equal("Took back \"Assistant: remove home\".", chat.Results[2]);
        Assert.Equal("Took back \"Assistant: change name\".", chat.Results[3]);
        Assert.Equal(new[] { "home", "Badges" }, root.Children.Select(child => child.Name));
        Assert.Equal("mine", workspace.UndoLabel);
    }

    [AvaloniaFact]
    public async Task The_Assistants_Project_Edits_Are_Labelled_As_Its_Own_And_The_Persons_As_Before()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var root = workspace.Document.Root;

        var chat = new ScriptedChat(
            Call("add_group", new() { ["parent"] = "" }),
            Call("add_drawing", new() { ["parent"] = "1", ["name"] = "Star", ["svg"] = Drawing }),
            Call("move", new() { ["node"] = "0", ["into"] = "1" }),
            Call("remove", new() { ["node"] = "1" }),
            Call("undo", new()),
            Call("undo", new()),
            Call("undo", new()),
            Call("undo", new()),
            Say("As it was."));

        await Send(window, chat, "add, move and remove, then take it all back", allow: true);

        Assert.Equal(
            new[] { "Took back \"Assistant: remove group\".", "Took back \"Assistant: move home\".", "Took back \"Assistant: add Star\".", "Took back \"Assistant: add group\"." },
            chat.Results.Skip(4));
        Assert.Equal(new[] { "home", "Badges" }, root.Children.Select(child => child.Name));

        // The same gestures made by the person are named as they always were.
        await window.AddGroupAsync(root);
        Assert.Equal("add group", workspace.UndoLabel);
        await window.ImportAsync(root, 0, new[] { new TemplateImport("Star", Drawing) }, show: false);
        Assert.Equal("add Star", workspace.UndoLabel);
        Assert.True(window.Move(root.Children[0], root.Children[2]));
        Assert.Equal("move Star", workspace.UndoLabel);
        Assert.True(await window.RemoveAsync(root.Children[0]));
        Assert.Equal("remove home", workspace.UndoLabel);
    }

    [AvaloniaFact]
    public async Task An_Undo_Leaves_The_Persons_Work_Alone_Once_They_Took_Its_Edit_Back()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var tools = new AssistantTools(window);

        workspace.Edit("mine");

        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "1", ["setting"] = "name", ["value"] = "Tags" }), Say("Renamed.")), "rename Badges", tools: tools);

        // The person takes the rename back by hand.
        Assert.True(workspace.Undo());

        var chat = new ScriptedChat(Call("undo", new()), Call("undo", new()), Say("Undone."));

        await Send(window, chat, "undo that", tools: tools);

        Assert.StartsWith("Nothing was taken back", chat.Results[0], StringComparison.Ordinal);
        Assert.Equal("Badges", workspace.Document.Root.Children[1].Name);

        // With nothing of its own left, the next undo is the person's, and it says which.
        Assert.Equal("Took back \"mine\".", chat.Results[1]);
    }

    [AvaloniaFact]
    public async Task An_Undo_Under_A_Newer_Edit_Of_The_Persons_Takes_Nothing_And_Names_It()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var root = workspace.Document.Root;
        var badges = root.Children[1];
        var tools = new AssistantTools(window);

        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "1", ["setting"] = "name", ["value"] = "Tags" }), Say("Renamed.")), "rename Badges", tools: tools);

        Assert.True(window.Move(root.Children[0], badges));

        // Asked twice: the rename is kept for later, not dropped so the second reaches the move.
        var chat = new ScriptedChat(Call("undo", new()), Call("undo", new()), Say("Not undone."));

        await Send(window, chat, "undo", tools: tools);

        Assert.All(chat.Results, result => Assert.StartsWith("Nothing was taken back", result, StringComparison.Ordinal));
        Assert.Contains("\"move home\"", chat.Results[0], StringComparison.Ordinal);
        Assert.Equal("move home", workspace.UndoLabel);
        Assert.Equal("Tags", badges.Name);

        // Once the person takes their move back, the rename is on top again, and still the assistant's.
        Assert.True(workspace.Undo());

        var again = new ScriptedChat(Call("undo", new()), Say("Undone."));

        await Send(window, again, "undo", tools: tools);

        Assert.Equal("Took back \"Assistant: change name\".", again.Results[0]);
        Assert.Equal("Badges", badges.Name);
    }

    [AvaloniaFact]
    public async Task An_Undo_Under_The_Persons_Typing_In_A_Tab_Takes_Nothing_And_Keeps_The_Edit_For_Later()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var badges = workspace.Document.Root.Children[1];
        var tools = new AssistantTools(window);

        await window.ShowAsync(workspace.Document.Root.Children[0]);
        Dispatcher.UIThread.RunJobs();

        var viewer = Viewer(window);

        await Send(
            window,
            new ScriptedChat(
                Call("set_attributes", new() { ["node"] = "0", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#111111" } }, ["summary"] = "darken" }),
                Call("set_node", new() { ["node"] = "1", ["setting"] = "name", ["value"] = "Tags" }),
                Say("Done.")),
            "darken home, rename Badges",
            tools: tools);

        // The person types in home's tab, over the assistant's edit there.
        viewer.SetSource(viewer.Source.Replace("<rect ", "<rect stroke=\"black\" ", StringComparison.Ordinal), "mine");

        var chat = new ScriptedChat(Call("undo", new()), Call("undo", new()), Call("undo", new()), Say("Undone what I could."));

        await Send(window, chat, "undo everything", tools: tools);

        Assert.Equal("Took back \"Assistant: change name\".", chat.Results[0]);
        Assert.All(chat.Results.Skip(1), result => Assert.StartsWith("Nothing was taken back", result, StringComparison.Ordinal));
        Assert.Contains("\"mine\"", chat.Results[1], StringComparison.Ordinal);
        Assert.Equal("Badges", badges.Name);
        Assert.Equal("mine", viewer.UndoLabel);
        Assert.Contains("stroke=\"black\"", viewer.Source, StringComparison.Ordinal);

        Assert.True(viewer.Undo());

        var again = new ScriptedChat(Call("undo", new()), Say("Undone."));

        await Send(window, again, "undo", tools: tools);

        Assert.Equal("Took back \"Assistant: darken\".", again.Results[0]);
        Assert.DoesNotContain("#111111", viewer.Source, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task With_No_Edit_Of_Its_Own_An_Undo_Takes_Back_The_Front_Tabs_Last_And_Names_It()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;

        workspace.Edit("theirs");
        await window.ShowAsync(workspace.Document.Root.Children[0]);
        Dispatcher.UIThread.RunJobs();

        var viewer = Viewer(window);
        var before = viewer.Source;

        viewer.SetSource(before.Replace("<rect ", "<rect stroke=\"black\" ", StringComparison.Ordinal), "mine");

        var chat = new ScriptedChat(Call("undo", new()), Say("Undone."));

        await Send(window, chat, "undo");

        Assert.Equal("Took back \"mine\".", chat.Results[0]);
        Assert.Equal(before, viewer.Source);
        Assert.Equal("theirs", workspace.UndoLabel);
    }

    [AvaloniaFact]
    public async Task An_Undo_Never_Reaches_Into_A_Project_Since_Reopened()
    {
        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);
        var tools = new AssistantTools(window);

        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "1", ["setting"] = "name", ["value"] = "Tags" }), Say("Renamed.")), "rename Badges", tools: tools);

        // Reopened, as a pull does, and edited by the person.
        window.ConfirmDiscard = _ => Task.FromResult(true);
        await window.OpenAsync(new[] { path });
        Dispatcher.UIThread.RunJobs();

        var reopened = window.Workspace!;

        reopened.Edit("mine");

        var chat = new ScriptedChat(Call("undo", new()), Say("Nothing."));

        await Send(window, chat, "undo", tools: tools);

        Assert.StartsWith("Nothing was taken back", chat.Results[0], StringComparison.Ordinal);
        Assert.Equal("mine", reopened.UndoLabel);
    }

    [AvaloniaFact]
    public async Task An_Edit_That_Changes_Nothing_Says_So_And_Is_Not_Undone_For_One_That_Did()
    {
        var window = await Host(Write("declared.svgstudio", Declared));
        var workspace = window.Workspace!;
        var dial = (ProjectDrawing)workspace.Document.Root.Children[0];
        var badges = (ProjectGroup)workspace.Document.Root.Children[1];
        var tools = new AssistantTools(window);

        await window.ShowAsync(dial);
        Dispatcher.UIThread.RunJobs();

        await Send(window, new ScriptedChat(Darken("darken"), Say("Darker.")), "darken the dial", tools: tools);

        // The person removes the badge by hand; the assistant then writes what is there already.
        Assert.True(await window.RemoveAsync(badges.Children[0]));
        Dispatcher.UIThread.RunJobs();

        var chat = new ScriptedChat(
            Call("set_declarations", new() { ["node"] = "1", ["code"] = badges.CodeText, ["summary"] = "keep the tint" }),
            Darken("darken again"),
            Call("undo", new()),
            Say("Undone."));

        await Send(window, chat, "check it, then undo", tools: tools);

        Assert.StartsWith("Nothing changed", chat.Results[0], StringComparison.Ordinal);
        Assert.StartsWith("Nothing changed", chat.Results[1], StringComparison.Ordinal);
        Assert.Equal("Took back \"Assistant: darken\".", chat.Results[2]);
        Assert.Empty(badges.Children);
        Assert.DoesNotContain("#111111", Viewer(window).Source, StringComparison.Ordinal);

        static ChatResponse Darken(string summary)
            => Call("set_attributes", new() { ["node"] = "0", ["key"] = "1", ["attributes"] = new[] { new { name = "fill", value = "#111111" } }, ["summary"] = summary });
    }

    [AvaloniaFact]
    public async Task A_Setting_Set_To_What_It_Says_Already_Is_No_Step_And_The_Undo_Is_The_Renames()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var badges = workspace.Document.Root.Children[1];

        var chat = new ScriptedChat(
            Call("set_node", new() { ["node"] = "1", ["setting"] = "name", ["value"] = "Tags" }),
            Call("set_node", new() { ["node"] = "1", ["setting"] = "name", ["value"] = "Tags" }),
            Call("undo", new()),
            Say("As it was."));

        await Send(window, chat, "rename Badges, again, then undo");

        Assert.StartsWith("Nothing changed", chat.Results[1], StringComparison.Ordinal);
        Assert.Equal("Took back \"Assistant: change name\".", chat.Results[2]);
        Assert.Equal("Badges", badges.Name);
        Assert.False(workspace.CanUndo);
        Assert.False(workspace.IsEdited);
    }

    /// <param name="ending">The project's line ending, which a drawing's text carries and a replacement read as a drawing does not.</param>
    [AvaloniaTheory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task A_Drawing_The_Project_Refuses_Or_Already_Holds_Leaves_No_Step_Behind(string ending)
    {
        var window = await Host(Write("icons.svgstudio", Project.Replace("\n", ending, StringComparison.Ordinal)));
        var workspace = window.Workspace!;
        var home = (ProjectDrawing)workspace.Document.Root.Children[0];
        var before = home.Text;

        var chat = new ScriptedChat(
            Call("add_drawing", new() { ["parent"] = "1", ["name"] = "Bad", ["svg"] = "<svg><rect" }),
            Call("add_drawing", new() { ["parent"] = "1", ["name"] = "Div", ["svg"] = "<div/>" }),
            Call("add_drawing", new() { ["parent"] = "1", ["name"] = " ", ["svg"] = Drawing }),
            Call("replace_drawing", new() { ["node"] = "0", ["svg"] = "<div/>", ["summary"] = "swap" }),
            Call("replace_drawing", new() { ["node"] = "0", ["svg"] = before + "\n", ["summary"] = "same" }),
            Call("replace_drawing", new() { ["node"] = "0", ["svg"] = before.Replace("\r\n", "\n", StringComparison.Ordinal), ["summary"] = "same" }),
            Say("Refused."));

        await Send(window, chat, "add drawings the project will not take");

        Assert.Contains("<svg> document", chat.Results[1], StringComparison.Ordinal);
        Assert.Contains("needs a name", chat.Results[2], StringComparison.Ordinal);
        Assert.Contains("<svg> document", chat.Results[3], StringComparison.Ordinal);
        Assert.StartsWith("Nothing changed", chat.Results[4], StringComparison.Ordinal);
        Assert.StartsWith("Nothing changed", chat.Results[5], StringComparison.Ordinal);
        Assert.Single(((ProjectGroup)workspace.Document.Root.Children[1]).Children);
        Assert.Equal(before, home.Text);
        Assert.False(workspace.CanUndo);
        Assert.False(workspace.IsEdited);
    }

    [AvaloniaFact]
    public async Task A_Replacement_The_Tab_Cannot_Take_Says_Why()
    {
        // Drawn, but with no source to edit: what the entities stand for cannot be written back.
        var window = await Host(Write("entities.svg", Entities));
        var viewer = Viewer(window);
        var before = viewer.Source;

        var chat = new ScriptedChat(
            Call("replace_drawing", new() { ["svg"] = Drawing, ["summary"] = "replace it" }),
            Say("Refused."));

        await Send(window, chat, "replace the drawing");

        Assert.Contains("declares entities", chat.Results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing changed", chat.Results[0], StringComparison.Ordinal);
        Assert.Equal(before, viewer.Source);
        Assert.False(viewer.CanUndo);
    }

    [AvaloniaFact]
    public async Task A_Replacement_A_Tab_Already_Holds_Is_Nothing_Changed()
    {
        // SetSource's other false, for which the viewer raises no error.
        var window = await Host(Write("home.svg", Drawing));
        var viewer = Viewer(window);

        var chat = new ScriptedChat(
            Call("replace_drawing", new() { ["svg"] = viewer.Source, ["summary"] = "replace it" }),
            Say("Unchanged."));

        await Send(window, chat, "replace the drawing with itself");

        Assert.StartsWith("Nothing changed", chat.Results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("one undo step", chat.Results[0], StringComparison.Ordinal);
        Assert.False(viewer.CanUndo);
    }

    [AvaloniaFact]
    public async Task An_Added_Drawing_Is_Named_By_Its_Own_Path_Though_The_Person_Works_While_It_Opens()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var root = workspace.Document.Root;
        var tools = new AssistantTools(window);

        await window.ShowAsync(root.Children[1]);
        Dispatcher.UIThread.RunJobs();

        var adding = tools.For(small: false, docs: false).OfType<AIFunction>().Single(tool => tool.Name == "add_drawing")
            .InvokeAsync(new AIFunctionArguments { ["parent"] = "", ["name"] = "Star", ["svg"] = Drawing })
            .AsTask();

        // Loading off the UI thread, while the person goes back to the board and edits the project.
        Assert.False(adding.IsCompleted);
        await window.ShowAsync(root.Children[1]);
        workspace.Edit("mine");

        await Gestures.Until(() => adding.IsCompleted);

        Assert.StartsWith("Added Star as 2.", (await adding)?.ToString(), StringComparison.Ordinal);

        var chat = new ScriptedChat(Call("undo", new()), Say("Not undone."));

        await Send(window, chat, "undo", tools: tools);

        Assert.StartsWith("Nothing was taken back", chat.Results[0], StringComparison.Ordinal);
        Assert.Equal("mine", workspace.UndoLabel);
    }

    [AvaloniaFact]
    public async Task Taking_Back_Every_Edit_Since_The_Save_Leaves_Nothing_To_Save()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        var chat = new ScriptedChat(
            Call("set_attributes", new() { ["node"] = "0", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#123456" } }, ["summary"] = "darken home" }),
            Call("undo", new()),
            Say("As it was."));

        await Send(window, chat, "darken home, then undo");

        Assert.Equal("Took back \"Assistant: darken home\".", chat.Results[1]);
        Assert.False(window.Workspace!.IsEdited);
        Assert.DoesNotContain("unsaved", new AssistantTools(window).Context(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task An_Edit_Says_What_Is_Wrong_With_The_Drawing_Afterwards()
    {
        var window = await Host(Write("declared.svgstudio", Declared));
        var chat = new ScriptedChat(
            Call("set_attributes", new() { ["node"] = "1/0", ["key"] = "0", ["attributes"] = new[] { new { name = "opacity", value = "{{ nothing }}" } }, ["summary"] = "break it" }),
            Say("Broken."));

        await Send(window, chat, "break the badge");

        Assert.Contains("Problems now:", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("Unknown name 'nothing'", chat.Results[0], StringComparison.Ordinal);

        // In scope: what it inherits, not only the built-ins, since those are the names it could have used.
        Assert.Contains("tint", chat.Results[0].Split("in scope")[1], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Text_Between_Tags_Is_Set_And_An_Element_With_Children_Is_Refused()
    {
        var window = await Host(Write("declared.svgstudio", Declared));
        var dial = (ProjectDrawing)window.Workspace!.Document.Root.Children[0];
        var chat = new ScriptedChat(
            Call("set_text", new() { ["node"] = "0", ["key"] = "0/0/0", ["text"] = "90", ["summary"] = "turn it" }),
            Call("set_text", new() { ["node"] = "0", ["key"] = "0", ["text"] = "x", ["summary"] = "defs" }),
            Say("Done."));

        await Send(window, chat, "set the angle to 90");

        Assert.Contains("one undo step", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("<e:let name=\"angle\">90</e:let>", dial.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("one undo step", chat.Results[1], StringComparison.Ordinal);
        Assert.Contains("has text of its own", chat.Results[1], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_Projects_Problems_Come_As_One_List_And_The_Root_Key_Is_Known()
    {
        var window = await Host(Write("declared.svgstudio", Declared));
        var chat = new ScriptedChat(
            Call("get_problems", new()),
            Call("get_element", new() { ["node"] = "1/0", ["key"] = "(root)" }),
            Say("Read."));

        await Send(window, chat, "what is wrong");

        // The dial is listed with its clash, said at the tag and not the spliced line; the clean drawing is counted.
        Assert.Contains("0 Dial:", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("(at: <e:let name=\"angle\">)", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("1 drawing(s) with no problems", chat.Results[0], StringComparison.Ordinal);
        Assert.StartsWith("<svg> at (root)", chat.Results[1], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Undoing_An_Added_Group_Closes_Its_Tab()
    {
        var window = await Host(Write("declared.svgstudio", Declared));
        var chat = new ScriptedChat(
            Call("add_group", new() { ["parent"] = "" }),
            Call("undo", new()),
            Call("get_project", new()),
            Say("Done."));

        await Send(window, chat, "add a group and take it back");

        Assert.Contains("Took back", chat.Results[1], StringComparison.Ordinal);
        Assert.DoesNotContain("no longer in the project", chat.Results[2], StringComparison.Ordinal);
        Assert.Equal(2, window.Workspace!.Document.Root.Children.Count);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TabItem>(), tab => tab.Tag is ProjectGroup { Parent: { } } group && !window.Workspace.Document.Root.Children.Contains(group));
    }

    [AvaloniaFact]
    public async Task A_Tool_Never_Opens_A_Dialog_The_Client_Cannot_Close()
    {
        var window = await Host(Write("declared.svgstudio", Declared));
        var asked = 0;

        window.Announce = (_, _) => { asked++; return Task.CompletedTask; };

        // An unreadable drawing is said back, not announced; a project never saved is not sent to a
        // picker; a node whose tab holds edits is not sent to a discard prompt.
        await window.ShowAsync(window.Workspace!.Document.Root.Children[0]);
        Dispatcher.UIThread.RunJobs();
        Viewer(window).SetSource(Viewer(window).Source + "<!-- typed -->");

        var chat = new ScriptedChat(
            Call("add_drawing", new() { ["parent"] = "1", ["name"] = "Bad", ["svg"] = "<svg><rect" }),
            Call("remove", new() { ["node"] = "0" }),
            Say("Done."));

        await Send(window, chat, "add a broken drawing and remove the dial", allow: true);

        Assert.Equal(0, asked);
        Assert.DoesNotContain("one undo step", chat.Results[0], StringComparison.Ordinal);
        Assert.NotEqual("That drawing couldn't be added.", chat.Results[0]);
        Assert.Contains("edits in its tab", chat.Results[1], StringComparison.Ordinal);
        Assert.Equal(2, window.Workspace.Document.Root.Children.Count);

        var untitled = new MainWindow { Announce = (_, _) => Task.CompletedTask };

        untitled.Show();
        Dispatcher.UIThread.RunJobs();
        await untitled.NewProjectAsync();
        Dispatcher.UIThread.RunJobs();

        var saving = new ScriptedChat(Call("save", new()), Say("Not saved."));

        await Send(untitled, saving, "save", allow: true);

        Assert.Contains("never been saved", saving.Results[0], StringComparison.Ordinal);
        Assert.Null(untitled.Workspace?.Document.Path);
    }

    [AvaloniaFact]
    public async Task A_Failed_Save_Is_Said_Back_And_Opens_Nothing()
    {
        var gone = Directory.CreateDirectory(Path.Combine(_directory, "gone")).FullName;
        var path = Path.Combine(gone, "icons.svgstudio");

        File.WriteAllText(path, Project);

        var window = await Host(path);
        var announced = Unanswered(window);

        await window.ShowAsync(window.Workspace!.Document.Root.Children[0]);
        Dispatcher.UIThread.RunJobs();
        Viewer(window).SetSource("<div/>");

        var refused = new ScriptedChat(Call("save", new()), Say("Not saved."));

        await Send(window, refused, "save", allow: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("has to be an <svg>", refused.Results[0], StringComparison.Ordinal);
        Assert.Equal(Project, File.ReadAllText(path));
        Assert.False(window.Workspace!.CanUndo);
        Assert.False(window.Workspace.IsEdited);

        Viewer(window).SetSource(Drawing.Replace("#00ff00", "#123456", StringComparison.Ordinal));
        Directory.Delete(gone, recursive: true);

        var failed = new ScriptedChat(Call("save", new()), Say("Not saved."));

        await Send(window, failed, "save", allow: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("The project couldn't be saved", failed.Results[0], StringComparison.Ordinal);
        Assert.Empty(announced);
    }

    [AvaloniaFact]
    public async Task A_Save_That_Wrote_Nothing_Says_So()
    {
        var gone = Directory.CreateDirectory(Path.Combine(_directory, "gone")).FullName;
        var path = Path.Combine(gone, "drawing.svg");

        File.WriteAllText(path, Drawing);

        var window = await Host(path);
        var announced = Unanswered(window);

        Viewer(window).SetSource(Drawing.Replace("#00ff00", "#123456", StringComparison.Ordinal));
        Directory.Delete(gone, recursive: true);

        // Said over the drawing rather than in a dialog, so only the answer carries it to a client.
        var failed = new ScriptedChat(Call("save", new()), Say("Not saved."));

        await Send(window, failed, "save", allow: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("The drawing couldn't be saved", failed.Results[0], StringComparison.Ordinal);
        Assert.Empty(announced);

        // One save writes both the drawing in front and an edited project, and the project's write hides nothing.
        var lost = Directory.CreateDirectory(Path.Combine(_directory, "lost")).FullName;
        var alone = Path.Combine(lost, "drawing.svg");

        File.WriteAllText(alone, Drawing);

        var project = await Host(Write("icons.svgstudio", Project));
        var held = Unanswered(project);

        project.Workspace!.Edit();
        await project.OpenAsync(new[] { alone });
        Dispatcher.UIThread.RunJobs();
        Viewer(project).SetSource(Drawing.Replace("#00ff00", "#123456", StringComparison.Ordinal));
        Directory.Delete(lost, recursive: true);

        var both = new ScriptedChat(Call("save", new()), Say("Not saved."));

        await Send(project, both, "save", allow: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("The drawing couldn't be saved", both.Results[0], StringComparison.Ordinal);
        Assert.False(project.Workspace!.IsEdited);
        Assert.Empty(held);

        var empty = new MainWindow();
        var asked = Unanswered(empty);

        empty.Show();
        Dispatcher.UIThread.RunJobs();

        var nothing = new ScriptedChat(Call("save", new()), Say("Nothing."));

        await Send(empty, nothing, "save", allow: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("Nothing is open.", nothing.Results[0]);
        Assert.Empty(asked);
    }

    [AvaloniaFact]
    public async Task A_Save_Goes_By_The_Tab_In_Front_And_Never_Asks_Where()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var announced = Unanswered(window);

        // The project in front with nothing to write is up to date, not a failure.
        var current = new ScriptedChat(Call("save", new()), Say("Saved."));

        await Send(window, current, "save", allow: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("Saved.", current.Results[0]);

        // A tab belonging to no project, as a Streamline icon opened before any project was.
        await window.OpenTextAsync(Drawing, "Icon");
        Dispatcher.UIThread.RunJobs();

        var picker = new Picker();
        Viewer(window).FileDialogService = picker;

        var untitled = new ScriptedChat(Call("save", new()), Say("Not saved."));

        await Send(window, untitled, "save", allow: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, picker.Asked);
        Assert.Contains("has no file", untitled.Results[0], StringComparison.Ordinal);
        Assert.Empty(announced);

        // A drawing with a file of its own saves to it, whether or not the project has one.
        var path = Write("drawing.svg", Drawing);
        var unsaved = new MainWindow();
        var asked = Unanswered(unsaved);

        unsaved.Show();
        Dispatcher.UIThread.RunJobs();
        await unsaved.NewProjectAsync();
        await unsaved.OpenAsync(new[] { path });
        Dispatcher.UIThread.RunJobs();
        Viewer(unsaved).SetSource(Drawing.Replace("#00ff00", "#123456", StringComparison.Ordinal));

        var drawing = new ScriptedChat(Call("save", new()), Say("Saved."));

        await Send(unsaved, drawing, "save", allow: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("Saved.", drawing.Results[0]);
        Assert.Contains("#123456", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Empty(asked);
    }

    [AvaloniaFact]
    public async Task A_Node_Whose_Tabs_Hold_Typed_Settings_Is_Not_Removed_And_Nothing_Asks()
    {
        var window = await Host(Write("nested.svgstudio", Nested));
        var announced = Unanswered(window);
        var asked = 0;

        window.ConfirmDiscard = _ =>
        {
            asked++;
            return Task.FromResult(false);
        };

        // A width typed into a drawing's Project pane, which is beside the tab's content and leaves its source alone.
        var root = window.Workspace!.Document.Root;

        await window.ShowAsync(root.Children[0]);
        Dispatcher.UIThread.RunJobs();
        Assert.True(((GroupPanel)Viewer(window).SidePanels.Single().Content).Edit("width", "40"));

        // And one typed into the tab of a group inside the group being removed.
        var round = ((ProjectGroup)root.Children[1]).Children[1];

        await window.ShowAsync(round);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.GetVisualDescendants().OfType<GroupPanel>().Single(panel => ReferenceEquals(panel.Node, round)).Edit("width", "5"));

        // And one typed for Filled on Badges' board: with Filled gone the board falls back to Badges, and its next save writes the width there.
        var badges = (ProjectGroup)root.Children[1];

        Assert.True((await Pick(window, badges, badges.Children[0])).Edit("width", "100"));

        var chat = new ScriptedChat(
            Call("remove", new() { ["node"] = "0" }),
            Call("remove", new() { ["node"] = "1" }),
            Call("remove", new() { ["node"] = "1/0" }),
            Say("Kept."));

        await Send(window, chat, "remove home, Badges and Filled", allow: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, asked);
        Assert.Empty(announced);
        Assert.StartsWith("0 home has edits in its tab", chat.Results[0], StringComparison.Ordinal);
        Assert.StartsWith("1/1 Round has edits in its tab", chat.Results[1], StringComparison.Ordinal);
        Assert.StartsWith("1 Badges has edits in its tab", chat.Results[2], StringComparison.Ordinal);
        Assert.Equal(2, root.Children.Count);
        Assert.Equal(2, ((ProjectGroup)root.Children[1]).Children.Count);
    }

    [AvaloniaFact]
    public async Task A_Setting_Is_Not_Written_Under_One_Typed_For_The_Same_Node()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var root = window.Workspace!.Document.Root;
        var badges = (ProjectGroup)root.Children[1];
        var filled = badges.Children[0];

        // A width typed for Filled on Badges' board, where Filled has no tab of its own.
        Assert.True((await Pick(window, badges, filled)).Edit("width", "100"));

        // And one typed into a drawing's own Project pane, whose source edits are no reason to refuse.
        await window.ShowAsync(root.Children[0]);
        Dispatcher.UIThread.RunJobs();
        Viewer(window).SetSource(Viewer(window).Source + "<!-- typed -->");

        var chat = new ScriptedChat(
            Call("set_node", new() { ["node"] = "1/0", ["setting"] = "width", ["value"] = "200" }),
            Call("set_node", new() { ["node"] = "1", ["setting"] = "width", ["value"] = "200" }),
            Call("set_node", new() { ["node"] = "0", ["setting"] = "class", ["value"] = "Home" }),
            Say("Done."));

        await Send(window, chat, "widen them", allow: true);

        Assert.StartsWith("1/0 Filled has settings typed in the tab of 1 Badges", chat.Results[0], StringComparison.Ordinal);
        Assert.Null(filled.Width);
        Assert.Equal(200f, badges.Width);
        Assert.Equal("Home", root.Children[0].Class);

        Assert.True(((GroupPanel)Viewer(window).SidePanels.Single().Content).Edit("width", "40"));

        var typed = new ScriptedChat(Call("set_node", new() { ["node"] = "0", ["setting"] = "width", ["value"] = "50" }), Say("Refused."));

        await Send(window, typed, "widen home", allow: true);

        Assert.StartsWith("0 home has settings typed in the tab of 0 home", typed.Results[0], StringComparison.Ordinal);
        Assert.Null(root.Children[0].Width);
    }

    [AvaloniaFact]
    public async Task A_Save_Names_The_Tabs_Whose_Edits_It_Left_Behind()
    {
        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);
        var announced = Unanswered(window);
        var root = window.Workspace!.Document.Root;

        // Both drawings open, Filled in front, so home's edits sit in a tab a save does not hand over.
        await window.ShowAsync(root.Children[0]);
        await window.ShowAsync(((ProjectGroup)root.Children[1]).Children[0]);
        Dispatcher.UIThread.RunJobs();

        var chat = new ScriptedChat(
            Call("set_attributes", new() { ["node"] = "0", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#123456" } }, ["summary"] = "darken" }),
            Call("set_attributes", new() { ["node"] = "1/0", ["key"] = "0", ["attributes"] = new[] { new { name = "stroke", value = "black" } }, ["summary"] = "outline" }),
            Call("get_project", new()),
            Call("get_drawing", new() { ["node"] = "0" }),
            Call("get_drawing", new() { ["node"] = "1/0" }),
            Call("save", new()),
            Say("Saved most of it."));

        await Send(window, chat, "darken, outline and save", allow: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("0 drawing home (open, holding unsaved edits)", chat.Results[2], StringComparison.Ordinal);
        Assert.Contains("1/0 drawing Filled (open, holding unsaved edits)", chat.Results[2], StringComparison.Ordinal);
        Assert.DoesNotContain("that saving writes", chat.Results[3], StringComparison.Ordinal);
        Assert.Contains("open it and save", chat.Results[3], StringComparison.Ordinal);
        Assert.Contains("that saving writes into the project", chat.Results[4], StringComparison.Ordinal);
        Assert.StartsWith("Saved, except 0 home", chat.Results[5], StringComparison.Ordinal);
        Assert.DoesNotContain("#123456", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains("stroke=\"black\"", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains("Tabs holding edits not yet in the project: 0 home", new AssistantTools(window).Context(), StringComparison.Ordinal);

        // Each open and save writes one more tab's, so it ends.
        var rest = new ScriptedChat(Call("open", new() { ["node"] = "0" }), Call("save", new()), Say("Saved."));

        await Send(window, rest, "save home", allow: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("Saved.", rest.Results[1]);
        Assert.Contains("#123456", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("Tabs holding edits", new AssistantTools(window).Context(), StringComparison.Ordinal);
        Assert.Empty(announced);
    }

    [AvaloniaFact]
    public async Task The_Projects_Own_Tab_Holding_Edits_Is_Named_As_Open_Takes_It()
    {
        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);
        var announced = Unanswered(window);

        // A namespace typed into the project's tab, which then goes behind home's.
        Assert.True(window.GetVisualDescendants().OfType<GroupPanel>().Single(panel => panel.Node is ProjectRoot).Edit("namespace", "Demo.Other"));

        await window.ShowAsync(window.Workspace!.Document.Root.Children[0]);
        Dispatcher.UIThread.RunJobs();

        var chat = new ScriptedChat(
            Call("get_project", new()),
            Call("save", new()),
            Call("open", new() { ["node"] = "(project)" }),
            Call("save", new()),
            Call("get_project", new()),
            Say("Saved."));

        await Send(window, chat, "save it all", allow: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.EndsWith(", with edits held in its tab", chat.Results[0].Split('\n')[0], StringComparison.Ordinal);
        Assert.StartsWith("Saved, except (project) Project", chat.Results[1], StringComparison.Ordinal);
        Assert.Equal("Opened (project).", chat.Results[2]);
        Assert.Equal("Saved.", chat.Results[3]);
        Assert.Contains("namespace=\"Demo.Other\"", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("held", chat.Results[4].Split('\n')[0], StringComparison.Ordinal);
        Assert.Empty(announced);
    }

    [AvaloniaFact]
    public async Task A_Commit_Leaving_A_Tab_Behind_Is_Refused_Before_The_Person_Is_Asked()
    {
        var work = Path.Combine(_directory, "work");
        var path = Path.Combine(work, "icons.svgstudio");

        Directory.CreateDirectory(work);
        ProjectGitTests.Git(work, "init", "-b", "main");
        ProjectGitTests.Configure(work);
        File.WriteAllText(path, Project);
        ProjectGitTests.Git(work, "add", "icons.svgstudio");
        ProjectGitTests.Git(work, "commit", "-m", "Add the icons");

        var window = await Host(path);
        var root = window.Workspace!.Document.Root;

        await window.ShowAsync(root.Children[0]);
        await window.ShowAsync(root);
        Dispatcher.UIThread.RunJobs();

        var asked = new List<string>();
        var chat = new ScriptedChat(
            Call("set_attributes", new() { ["node"] = "0", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#123456" } }, ["summary"] = "darken" }),
            Call("git_commit", new() { ["message"] = "Darken" }),
            Say("Not committed."));

        await Send(window, chat, "darken and commit", allow: true, asked: asked).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.StartsWith("Nothing was committed: the tabs of 0 home", chat.Results[1], StringComparison.Ordinal);
        Assert.Empty(asked);
        Assert.Equal("1", ProjectGitTests.Git(work, "rev-list", "--count", "HEAD").Trim());
    }

    [Fact]
    public void Claude_Needs_A_Key()
    {
        var was = ClaudeProvider.ApiKey;

        try
        {
            ClaudeProvider.ApiKey = () => null;
            Assert.NotNull(new ClaudeProvider().Unavailable);

            ClaudeProvider.ApiKey = () => "sk-test";
            Assert.Null(new ClaudeProvider().Unavailable);
        }
        finally
        {
            ClaudeProvider.ApiKey = was;
        }
    }

    [AvaloniaFact]
    public void An_Attribute_List_Is_Described_To_The_Model_As_Objects()
    {
        var tool = new AssistantTools(new MainWindow()).For(small: true, docs: false).OfType<AIFunction>().Single(tool => tool.Name == "set_attributes");
        var schema = tool.JsonSchema.GetRawText();

        Assert.Contains("\"attributes\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"array\"", schema.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("\"name\"", schema, StringComparison.Ordinal);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);

        File.WriteAllText(path, text);

        return path;
    }

    private static SvgViewer Viewer(Window window) => window.GetVisualDescendants().OfType<SvgViewer>().First();

    /// <summary>Opens <paramref name="group"/> and clicks <paramref name="node"/> on its board, which its panel then describes.</summary>
    private static async Task<GroupPanel> Pick(MainWindow window, ProjectGroup group, ProjectNode node)
    {
        await window.ShowAsync(group);
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(900, 600));
        window.Arrange(new Rect(0, 0, 900, 600));
        Dispatcher.UIThread.RunJobs();

        var board = window.GetVisualDescendants().OfType<GroupPanel>().Single(panel => ReferenceEquals(panel.Node, group));
        var canvas = Gestures.Canvas(board);
        var placement = canvas.Placements[board.Board.ToList().IndexOf((ProjectDrawing)node)];
        var cull = placement.Svg.Picture!.CullRect;
        var middle = Gestures.Over(canvas, placement.At.X + cull.MidX, placement.At.Y + cull.MidY);

        Gestures.Press(window, canvas, middle);
        Gestures.Release(window, canvas, middle);

        Assert.Same(node, board.Described);

        return board;
    }

    private static async Task<MainWindow> Host(string path)
    {
        var window = new MainWindow { Announce = (_, _) => Task.CompletedTask };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        await window.OpenAsync(new[] { path });

        Dispatcher.UIThread.RunJobs();

        return window;
    }

    /// <summary>Records the window's dialogs and never closes them, as nobody closes one for a client.</summary>
    private static List<string> Unanswered(MainWindow window)
    {
        var titles = new List<string>();

        window.Announce = (title, _) =>
        {
            titles.Add(title);
            return new TaskCompletionSource().Task;
        };

        return titles;
    }

    /// <param name="asked">Where each question the tools put to the person goes.</param>
    /// <param name="small">Whether the model is one with a context too small for the docs, and so the core tools only.</param>
    /// <param name="tools">Tools kept from an earlier send, whose undo remembers what they did there.</param>
    private static async Task<string> Send(MainWindow window, ScriptedChat chat, string text, bool allow = false, List<string>? asked = null, bool small = false, AssistantTools? tools = null)
    {
        tools ??= new AssistantTools(window);
        tools.Confirm = question =>
        {
            asked?.Add(question);
            return Task.FromResult(allow);
        };
        var session = new AssistantSession(tools);

        session.Use(new ScriptedProvider(chat), new AssistantModel("scripted", "Scripted", small ? 4096 : 1_000_000));

        var reply = string.Empty;

        await foreach (var delta in session.SendAsync(text, CancellationToken.None))
        {
            reply += delta;
        }

        Dispatcher.UIThread.RunJobs();

        return reply;
    }

    private static ChatResponse Call(string name, Dictionary<string, object?> arguments)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, arguments.ToDictionary(pair => pair.Key, pair => (object?)JsonSerializer.SerializeToElement(pair.Value)))]));

    private static ChatResponse Say(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    /// <summary>A model that answers with what it was given, in order, and remembers what came back.</summary>
    private sealed class ScriptedChat : IChatClient
    {
        private readonly Queue<ChatResponse> _replies;

        public ScriptedChat(params ChatResponse[] replies) => _replies = new Queue<ChatResponse>(replies);

        /// <summary>What each tool call returned, in the order they were made.</summary>
        public List<string> Results { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var results = messages.Last().Contents.OfType<FunctionResultContent>().Select(result => result.Result?.ToString() ?? string.Empty);

            Results.AddRange(results.Where(_ => messages.Last().Role == ChatRole.Tool));

            return Task.FromResult(_replies.Dequeue());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class Picker : ISvgViewerFileDialogService
    {
        public int Asked { get; private set; }

        public Task<string?> OpenSvgAsync(TopLevel? owner) => Task.FromResult<string?>(null);

        public Task<string?> SaveSvgAsync(TopLevel? owner, string? suggested)
        {
            Asked++;
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class ScriptedProvider : IAssistantProvider
    {
        private readonly IChatClient _chat;

        public ScriptedProvider(IChatClient chat) => _chat = chat;

        public string Id => "scripted";

        public string Name => "Scripted";

        public string? Unavailable => null;

        public Task<IReadOnlyList<AssistantModel>> ModelsAsync(CancellationToken cancellation)
            => Task.FromResult<IReadOnlyList<AssistantModel>>(new[] { new AssistantModel("scripted", "Scripted", 1_000_000) });

        public IChatClient Create(AssistantModel model) => _chat;
    }
}
