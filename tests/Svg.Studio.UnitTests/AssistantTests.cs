using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.AI;
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

        Assert.Contains("(project) — declares: angle", chat.Results[2], StringComparison.Ordinal);
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

    private static async Task<MainWindow> Host(string path)
    {
        var window = new MainWindow { Announce = (_, _) => Task.CompletedTask };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        await window.OpenAsync(new[] { path });

        Dispatcher.UIThread.RunJobs();

        return window;
    }

    private static async Task<string> Send(MainWindow window, ScriptedChat chat, string text, bool allow = false)
    {
        var tools = new AssistantTools(window) { Confirm = _ => Task.FromResult(allow) };
        var session = new AssistantSession(tools);

        session.Use(new ScriptedProvider(chat), new AssistantModel("scripted", "Scripted", 1_000_000));

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
