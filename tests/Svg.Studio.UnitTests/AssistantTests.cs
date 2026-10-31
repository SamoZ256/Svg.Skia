using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.AI;
using Svg.CodeGen.Skia;
using Svg.Expressions;
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

    /// <summary>A group's <c>tint</c>, and under it a drawing with a block of its own in front of two gradients.</summary>
    /// <remarks>Built, the group's block goes in front of the drawing's, so g1 is 0/1 in the file and 0/2 in the tree.</remarks>
    private const string Owned = """
        <studio namespace="Demo.Icons">
          <group name="Badges">
            <e:code xmlns:e="https://svg.skia/expr/1.0">
              <e:param name="tint" type="color" default="#ff0000" />
            </e:code>

            <drawing name="Graded">
              <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24">
                <defs>
                  <e:code>
                    <e:param name="a" type="number" default="1" />
                    <e:param name="b" type="number" default="2" />
                  </e:code>
                  <linearGradient id="g1" />
                  <linearGradient id="g2" />
                </defs>
                <rect width="24" height="24" fill="{{ tint }}" />
              </svg>
            </drawing>
          </group>
        </studio>
        """;

    /// <summary>A group sizing and classing what is under it: 0/0 = Filled, with a class, a let and an import of its own, and 0/1 = Wide, with a width.</summary>
    private const string Sized = """
        <studio namespace="Demo.Icons">
          <group name="Badges" class="Badge" width="24" height="24" padding="2">
            <e:code xmlns:e="https://svg.skia/expr/1.0">
              <e:param name="tint" type="color" default="#ff0000" />
            </e:code>

            <drawing name="Filled" class="FilledBadge" source="streamline:abc">
              <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24">
                <defs><e:code><e:let name="half">12</e:let></e:code></defs>
                <rect width="24" height="24" fill="{{ tint }}" />
              </svg>
            </drawing>

            <drawing name="Wide" width="40">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                <rect width="24" height="24" />
              </svg>
            </drawing>
          </group>
        </studio>
        """;

    /// <summary>0 = defs, 0/0 = the gradient #shade, 1 = the group #body, 1/0 = its rect #box, 2 = the circle #dot.</summary>
    private const string Parts = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
          <defs>
            <linearGradient id="shade" />
          </defs>
          <g id="body">
            <rect id="box" width="24" height="24" />
          </g>
          <circle id="dot" cx="12" cy="12" r="4" />
        </svg>
        """;

    /// <summary>A copy of the live run's Keys: 0 = Badges declaring tint, 0/0 = Tinted, with no block of its own, 0/1 = Keys, with one, under a project declaring angle.</summary>
    private const string Keyed = """
        <studio namespace="Mcp.Test">
          <e:code xmlns:e="https://svg.skia/expr/1.0">
            <e:param name="angle" type="number" default="15" />
          </e:code>

          <group name="Badges">
            <e:code xmlns:e="https://svg.skia/expr/1.0">
              <e:param name="tint" type="color" default="#ff0000" />
            </e:code>

            <drawing name="Tinted">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
                <rect width="24" height="24" fill="{{ tint }}" transform="rotate({{ angle }} 12 12)" />
              </svg>
            </drawing>

            <drawing name="Keys">
              <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
                <defs>
                  <e:code>
                    <e:param name="a" type="number" default="2" />
                    <e:param name="b" type="number" default="3" />
                    <e:let name="size">a * b * 2</e:let>
                    <e:let name="half">size / 2</e:let>
                  </e:code>
                  <linearGradient id="g1"><stop offset="0" stop-color="#ff0000" /><stop offset="1" stop-color="#ffff00" /></linearGradient>
                  <linearGradient id="g2"><stop offset="0" stop-color="#0000ff" /><stop offset="1" stop-color="#00ffff" /></linearGradient>
                </defs>
                <rect width="{{ size }}" height="24" fill="url(#g1)" />
                <circle cx="18" cy="12" r="{{ half / 2 }}" fill="{{ tint }}" />
              </svg>
            </drawing>
          </group>
        </studio>
        """;

    /// <summary>0 = Badges, declaring nothing yet, 0/0 = Filled naming tint, 0/1 = Round, whose let wash names tint too, 0/1/0 = Dot naming wash.</summary>
    private const string Washed = """
        <studio namespace="Demo.Icons">
          <group name="Badges">
            <drawing name="Filled">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                <rect width="24" height="24" fill="{{ tint }}" />
              </svg>
            </drawing>

            <group name="Round">
              <e:code xmlns:e="https://svg.skia/expr/1.0">
                <e:let name="wash">tint</e:let>
              </e:code>

              <drawing name="Dot">
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <circle cx="12" cy="12" r="6" fill="{{ wash }}" />
                </svg>
              </drawing>
            </group>
          </group>
        </studio>
        """;

    /// <summary>Templates for one colour, for two, one kept to a family, one for two kept away from one, and one that does not read; 0 = home, 1 = Empty, declaring nothing.</summary>
    private const string Templated = """
        <studio namespace="Demo.Icons">
          <e:templates xmlns:e="https://svg.skia/expr/1.0">
            <e:recipe name="Accent glyph">
              <e:match colors="1" />
              <e:code><e:let name="stateAccentColor">#fb3e72</e:let></e:code>
              <e:slot name="glyph" rest="true">stateAccentColor</e:slot>
            </e:recipe>
            <e:recipe name="Two-tone">
              <e:match colors="2" />
              <e:slot name="ink" rest="true">stateAccentColor</e:slot>
            </e:recipe>
            <e:recipe name="Line icons">
              <e:match family="streamline-light" />
              <e:slot name="ink" rest="true">stateAccentColor</e:slot>
            </e:recipe>
            <e:recipe name="Not solid">
              <e:match colors="2" family="!*solid*" />
              <e:slot name="ink" rest="true">stateAccentColor</e:slot>
            </e:recipe>
            <e:recipe name="Broken">
              <e:bogus />
            </e:recipe>
          </e:templates>

          <drawing name="home">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <rect width="24" height="24" fill="#00ff00" />
            </svg>
          </drawing>

          <group name="Empty">
            <drawing name="Dot">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><circle r="4" /></svg>
            </drawing>
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

        // Any number of percent signs, as a block holding another is written: svg-expressions' first
        // example was served between {%%{ and }%%}.
        Assert.All(AssistantDocs.Sections, section => Assert.DoesNotMatch(@"\{%+\{|\}%+\}", section.Text));
        Assert.Contains("visibility=\"{{ alert }}\"", AssistantDocs.Read("svg-expressions"), StringComparison.Ordinal);
        Assert.DoesNotContain("title: \"Svg Studio\"", whole, StringComparison.Ordinal);
        Assert.Contains("{{ tint }}", whole, StringComparison.Ordinal);
        Assert.Contains("# Svg Studio", whole, StringComparison.Ordinal);

        Assert.Equal(AssistantDocs.Sections.Count, AssistantDocs.Sections.Select(section => section.Id).Distinct().Count());
        Assert.All(AssistantDocs.Sections, section => Assert.Equal(section.Text, AssistantDocs.Read(section.Id)));
        Assert.Contains(AssistantDocs.Sections, section => section.Id == "svg-studio#the-attributes-tab");

        // A board drag and an edit to a drawing in no tab are steps on the project's history. Read
        // across line breaks, which a plain search for the first of these missed.
        var flat = Regex.Replace(whole, @"\s+", " ");

        Assert.DoesNotContain("cannot be undone", flat, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing to undo", flat, StringComparison.Ordinal);
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
        Assert.Contains("get_code", large);
        Assert.True(large.Count > small.Count);

        session.Use(new ScriptedProvider(chat), new AssistantModel("large", "Large", 1_000_000));

        Assert.False(session.Small);

        // And is not told about saving at all, having no tool to do it: told it asks first, a 3B
        // on-device model asked to save after every edit.
        Assert.DoesNotContain("Saving", AssistantSession.SystemPrompt(small: true), StringComparison.Ordinal);
        Assert.Contains("Saving, exporting, removing and committing ask the person first, and so does a step that would throw away edits not saved; don't ask again in words.", AssistantSession.SystemPrompt(small: false), StringComparison.Ordinal);
        Assert.All(
            new AssistantTools(window).For(small: false, docs: false).Where(tool => tool.Name is "remove" or "new_project" or "open_file" or "reload"),
            tool => Assert.Contains("asks the person whether to throw them away", tool.Description, StringComparison.Ordinal));

        // A path is a position: removing 0 and then 1 by one read of the project removed what was 2.
        Assert.Contains("Paths are positions", AssistantSession.SystemPrompt(small: false), StringComparison.Ordinal);
    }

    /// <remarks>
    /// The rules are in every model's prompt, and named the large set's tools to the small one. Those of
    /// one word are words too, so declare and find are looked for alone; move, remove and redo are the
    /// person's as well as tools.
    /// </remarks>
    [AvaloniaFact]
    public void A_Small_Model_Is_Told_Of_No_Tool_It_Has_Not_Got()
    {
        var window = new MainWindow();
        var small = new AssistantTools(window).For(small: true, docs: true).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var told = AssistantSession.SystemPrompt(small: true);

        Assert.All(Regex.Matches(told, @"\b[a-z]+_[a-z_]+\b").Select(match => match.Value), named => Assert.Contains(named, small));
        Assert.DoesNotMatch(@"\b(declare|find)\b", told);
        Assert.Contains("get_changes says what changed since the last commit", AssistantSession.SystemPrompt(small: false), StringComparison.Ordinal);
    }

    /// <remarks>get_context and render are the MCP server's alone, which a panel model told to call one could not.</remarks>
    [AvaloniaFact]
    public void The_Panels_Tools_Name_No_Tool_Only_Claude_Code_Has()
    {
        var window = new MainWindow();
        var panel = new AssistantTools(window).For(small: false, docs: false).OfType<AIFunction>().ToList();
        var absent = AssistantTools.Titles.Keys.Except(panel.Select(tool => tool.Name), StringComparer.Ordinal).ToList();

        Assert.Contains("render", absent);
        Assert.All(panel, tool => Assert.DoesNotMatch($@"\b({string.Join("|", absent)})\b", tool.Description + tool.JsonSchema.GetRawText()));
    }

    [AvaloniaFact]
    public async Task A_Small_Model_Is_Not_Told_There_Is_Anything_To_Save()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "0", ["setting"] = "name", ["value"] = "Tags" }), Say("Renamed.")), "rename");

        Assert.True(window.Workspace!.IsEdited);
        Assert.Contains("unsaved changes", new AssistantTools(window).Context(small: false), StringComparison.Ordinal);
        Assert.DoesNotContain("unsaved", new AssistantTools(window).Context(small: true), StringComparison.Ordinal);

        // Nor pointed at a tool it does not have.
        Assert.DoesNotContain("get_problems", new AssistantTools(window).Context(small: true), StringComparison.Ordinal);

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
            Call("set_attributes", new() { ["node"] = "1", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#123456" } }, ["summary"] = "darken home" }),
            Say("Done."));

        await Send(window, chat, "darken home");

        Assert.Contains("1 drawing home", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("0/0 drawing Filled", chat.Results[0], StringComparison.Ordinal);

        var home = (ProjectDrawing)workspace.Document.Root.Children[1];

        Assert.Contains("#123456", home.Text, StringComparison.Ordinal);
        Assert.Equal("Assistant: darken home", workspace.UndoLabel);

        Assert.True(window.Undo());
        Assert.DoesNotContain("#123456", home.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Keys_Are_The_Files_Even_Where_A_Group_Writes_Declarations_In()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var drawing = window.Workspace!.Document.Root.Children[0] is ProjectGroup group ? group.Children[0] : null;

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

        var edit = Call("set_attributes", new() { ["node"] = "1", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#123456" } }, ["summary"] = "darken" });

        await Send(window, new ScriptedChat(edit, Call("save", new()), Say("Not saved.")), "darken and save", allow: false);

        Assert.DoesNotContain("#123456", TestPackage.Read(path), StringComparison.Ordinal);

        await Send(window, new ScriptedChat(Call("save", new()), Say("Saved.")), "save", allow: true);

        Assert.Contains("#123456", TestPackage.Read(path), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Project_Node_Is_Renamed_Through_Its_Settings()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "0", ["setting"] = "name", ["value"] = "Tags" }), Say("Renamed.")), "rename Badges");

        // Renamed, it sorts after the drawing, so it is the second row now.
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
        var badges = root.Children[0];

        // Unknown, misspelt by case, and the project's own: once an empty step, a rename that was not, and a cast that threw.
        var chat = new ScriptedChat(
            Call("set_node", new() { ["node"] = "0", ["setting"] = "colour", ["value"] = "red" }),
            Call("set_node", new() { ["node"] = "0", ["setting"] = "Name", ["value"] = "Tags" }),
            Call("set_node", new() { ["node"] = "0", ["setting"] = "helperScope", ["value"] = "internal" }),
            Say("Refused."));

        await Send(window, chat, "set what Badges does not take");

        Assert.Equal(3, chat.Results.Count);
        Assert.StartsWith("0 has no setting called 'colour';", chat.Results[0], StringComparison.Ordinal);
        Assert.StartsWith("0 has no setting called 'Name';", chat.Results[1], StringComparison.Ordinal);
        Assert.StartsWith("0 has no setting called 'helperScope';", chat.Results[2], StringComparison.Ordinal);
        Assert.All(chat.Results, result => Assert.Contains("name, namespace, class", result, StringComparison.Ordinal));
        Assert.Equal("Badges", badges.Name);
        Assert.Null(badges.Element.Attribute("helperScope"));
        Assert.False(workspace.CanUndo);
        Assert.False(workspace.IsEdited);

        // Which the project itself still takes.
        var own = new ScriptedChat(Call("set_node", new() { ["node"] = "", ["setting"] = "helperScope", ["value"] = "internal" }), Say("Set."));

        await Send(window, own, "scope the project's helpers");

        Assert.Contains("one undo step", own.Results[0], StringComparison.Ordinal);
        Assert.Equal(SvgHelperScope.Internal, root.HelperScope);
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
        var home = root.Children[1];
        var badges = (ProjectGroup)root.Children[0];
        var filled = badges.Children[0];

        var chat = new ScriptedChat(
            Call("set_node", new() { ["node"] = "1", ["setting"] = "width" }),
            Call("set_node", new() { ["node"] = "0", ["setting"] = "scale" }),
            Call("set_node", new() { ["node"] = "0/0", ["setting"] = "height" }),
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
        Assert.Contains("reads, helperScope, skiaSharp", tool.Description, StringComparison.Ordinal);

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
            Call("set_node", new() { ["node"] = "0", ["setting"] = "scale", ["value"] = "2" }),
            Call("get_project", new()),
            Say("Read."));

        await Send(window, chat, "scale Badges and read the project");

        var lines = chat.Results[1].Split('\n');

        Assert.Contains("(project) namespace=\"Demo.Icons\"", lines);
        Assert.Contains("0 group Badges scale=\"2\" — declares: tint", lines);
        Assert.Contains("  0/0 drawing Filled", lines);
    }

    [AvaloniaFact]
    public async Task A_Continued_Namespace_Is_Set_On_A_Group_And_Refused_On_The_Project()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var root = window.Workspace!.Document.Root;
        var refused = new ScriptedChat(Call("set_node", new() { ["node"] = "", ["setting"] = "namespace", ["value"] = ".Icons" }), Say("No."));

        await Send(window, refused, "root");
        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "0", ["setting"] = "namespace", ["value"] = ".Badges" }), Say("Set.")), "group");

        Assert.Contains("nothing above it", refused.Results.Single(), StringComparison.Ordinal);
        Assert.Equal("Demo.Icons", root.Namespace);
        Assert.Equal("Demo.Icons.Badges", root.Children.Single(child => child.Name == "Badges").EffectiveNamespace);
    }

    /// <summary>
    /// What an export reads is the project's own, set on it and nowhere else; a setting a node does
    /// not take is refused rather than recorded as a step that changed nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task The_Projects_Export_Settings_Are_Set_On_The_Project()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var root = window.Workspace!.Document.Root;

        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "", ["setting"] = "helperScope", ["value"] = "internal" }), Say("Scoped.")), "helperScope");
        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "", ["setting"] = "api", ["value"] = "Icons.Api" }), Say("Named.")), "api");

        Assert.Equal(Svg.CodeGen.Skia.SvgHelperScope.Internal, root.HelperScope);
        Assert.Equal("Icons.Api", root.Api);

        var steps = window.Workspace!.UndoLabel;

        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "", ["setting"] = "helperScope", ["value"] = "always" }), Say("No.")), "bad helperScope");
        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "0", ["setting"] = "helperScope", ["value"] = "file" }), Say("No.")), "group helperScope");
        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "", ["setting"] = "name", ["value"] = "Root" }), Say("No.")), "root name");
        var method = new ScriptedChat(Call("set_node", new() { ["node"] = "0", ["setting"] = "method", ["value"] = "drawX" }), Say("No."));

        await Send(window, method, "group method");

        // Refused, rather than thrown out of a write that casts the group to a drawing.
        Assert.Contains("has no setting called 'method'", method.Results.Single(), StringComparison.Ordinal);

        Assert.Equal(Svg.CodeGen.Skia.SvgHelperScope.Internal, root.HelperScope);
        Assert.NotEqual("Root", root.Name);
        Assert.Equal(steps, window.Workspace!.UndoLabel);
    }

    [AvaloniaFact]
    public async Task The_Panel_Shows_Who_Said_What_In_Bubbles()
    {
        var window = await Host(Write("drawing.svg", Drawing));

        // The second changes nothing, so it has no line: the panel lists what was done.
        var chat = new ScriptedChat(
            Call("set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#ff0000" } }, ["summary"] = "make it red" }),
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
            Call("get_drawing", new() { ["node"] = "0/0" }),
            Call("get_drawing", new() { ["node"] = "1" }),
            Call("get_project", new()),
            Say("Read."));

        await Send(window, chat, "what is wrong");

        // The group's tint is inherited, so it is neither undeclared nor a problem.
        Assert.Contains("Inherited from 0: tint", chat.Results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Problem:", chat.Results[0], StringComparison.Ordinal);

        // The dial's own let and the project's param are one name twice, which its own text never shows.
        Assert.Contains("Inherited from (project): angle", chat.Results[1], StringComparison.Ordinal);
        Assert.Contains("'angle' is declared more than once", chat.Results[1], StringComparison.Ordinal);

        // Spelt as get_element and set_text spell it, and told apart from another let by its name.
        Assert.Contains("  0/0 e:code\n", chat.Results[1], StringComparison.Ordinal);
        Assert.Contains("  0/0/0 e:let name=angle\n", chat.Results[1], StringComparison.Ordinal);

        Assert.Contains("(project) namespace=\"Demo.Icons\" — declares: angle", chat.Results[2], StringComparison.Ordinal);
        Assert.Contains("0 group Badges — declares: tint", chat.Results[2], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_Context_Counts_What_Studio_Counts()
    {
        var window = await Host(Write("declared.svgstudio", Declared));

        await window.ShowAsync(window.Workspace!.Document.Root.Children[1]);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("The drawing has 1 error(s);", new AssistantTools(window).Context(), StringComparison.Ordinal);
    }

    /// <remarks>
    /// A reused id still opens, so the viewer counts it as a warning; the tools called it a problem
    /// like any other, and told a client with no project to ask get_problems, which then refused.
    /// </remarks>
    [AvaloniaFact]
    public async Task Warnings_Are_Said_To_Be_Warnings_And_Counted_Apart_From_Errors()
    {
        var window = await Host(Write("faults.svg", """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24">
              <rect id="a" width="24" height="24" fill="{{ nope }}" />
              <circle id="a" r="6" />
            </svg>
            """));
        var context = new AssistantTools(window).Context();

        Assert.Contains("The drawing has 1 error(s) and 1 warning(s); get_drawing lists them.", context, StringComparison.Ordinal);
        Assert.DoesNotContain("get_problems", context, StringComparison.Ordinal);

        var chat = new ScriptedChat(Call("get_drawing", new()), Say("Read."));

        await Send(window, chat, "what is wrong");

        Assert.Contains("Problem: Warning: The id 'a' is already used", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("Problem: Unknown name 'nope'", chat.Results[0], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Pick_Is_Named_By_The_Files_Key_Where_A_Drawing_Has_A_Block_Before_The_Groups()
    {
        var window = await Host(Write("owned.svgstudio", Owned));

        await window.ShowAsync(((ProjectGroup)window.Workspace!.Document.Root.Children[0]).Children[0]);
        Dispatcher.UIThread.RunJobs();

        var viewer = Viewer(window);

        // g1 as the tree has it, behind the block the group's declarations put in front.
        Assert.True(viewer.Elements.TrySelect(new[] { "0/2" }));
        Assert.Contains("Selected elements: 0/1", new AssistantTools(window).Context().Split('\n'));

        var chat = new ScriptedChat(
            Call("get_element", new() { ["key"] = "0/1" }),
            Call("select", new() { ["keys"] = new[] { "0/0" } }),
            Say("Selected."));

        await Send(window, chat, "pick the drawing's own declarations");

        Assert.Contains("id=g1", chat.Results[0], StringComparison.Ordinal);
        Assert.Equal("0/1", viewer.Elements.SelectedAddresses.Single());
    }

    [AvaloniaFact]
    public async Task What_Is_Picked_On_A_Groups_Board_Is_Said_In_The_Context()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var badges = (ProjectGroup)window.Workspace!.Document.Root.Children[0];

        Assert.DoesNotContain("Picked", new AssistantTools(window).Context(), StringComparison.Ordinal);

        await Pick(window, badges, badges.Children[0]);

        Assert.Contains("Picked on the board: 0/0 drawing Filled", new AssistantTools(window).Context(), StringComparison.Ordinal);

        // An undo takes the add out, and the pick then gave a path of "(no longer in the project)"
        // to name it by.
        await Send(window, new ScriptedChat(Call("add_drawing", new() { ["parent"] = "0", ["name"] = "Added", ["svg"] = Drawing }), Say("Added.")), "add one");

        var added = badges.Children[0];
        var board = await Pick(window, badges, added);

        Assert.Contains("Picked on the board: 0/0 drawing Added", new AssistantTools(window).Context(), StringComparison.Ordinal);
        Assert.True(window.Workspace.Undo());
        Dispatcher.UIThread.RunJobs();

        // Given back to the board's own group, as a node taken out is, and not one to name.
        Assert.Same(badges, board.Described);
        Assert.DoesNotContain("Picked", new AssistantTools(window).Context(), StringComparison.Ordinal);

        await Send(
            window,
            new ScriptedChat(
                Call("add_group", new() { ["parent"] = "0" }),
                Call("add_drawing", new() { ["parent"] = "0/1", ["name"] = "Dot", ["svg"] = Drawing }),
                Say("Added.")),
            "add a group holding one");

        var inner = (ProjectGroup)badges.Children[1];
        var dot = inner.Children[0];

        board = await Pick(window, badges, dot);

        Assert.Contains("Picked on the board: 0/1/0 drawing Dot", new AssistantTools(window).Context(), StringComparison.Ordinal);
        Assert.True(await window.RemoveAsync(inner));
        Dispatcher.UIThread.RunJobs();

        // Still in the group that was taken out, so the board still describes it, and there is no path to name it by.
        Assert.Same(dot, board.Described);
        Assert.DoesNotContain("Picked", new AssistantTools(window).Context(), StringComparison.Ordinal);
    }

    /// <remarks>Each answer under the bound a client cuts a tool's output at, and the base64 an image holds nowhere in either.</remarks>
    [AvaloniaFact]
    public async Task A_Drawing_Too_Long_To_Read_Whole_Leaves_Its_Source_And_Its_Data_Out_And_Says_So()
    {
        var window = await Host(Write("photo.svg", """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
              <text y="20">Caption</text>
              <image width="24" height="24" href="data:image/png;base64,
            """.TrimEnd() + new string('A', 200_000) + "\" />\n</svg>"));
        var chat = new ScriptedChat(
            Call("get_drawing", new()),
            Call("get_element", new() { ["key"] = "1" }),
            Call("get_element", new() { ["key"] = "0" }),
            Say("Read."));

        await Send(window, chat, "what is in it");

        Assert.All(chat.Results, result => Assert.True(result.Length < 40_000, $"{result.Length} characters"));
        Assert.Contains("1 image", chat.Results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Source:", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("get_element", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("replace_drawing", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("href=(left out: data:image/png, 200022 characters)\n", chat.Results[1], StringComparison.Ordinal);
        Assert.Contains("width=24\n", chat.Results[1], StringComparison.Ordinal);

        // What set_text would change, which with the source left out nothing else showed.
        Assert.Contains("Text: Caption\n", chat.Results[2], StringComparison.Ordinal);

        // Drawn but unreadable, every tool refuses it, replace_drawing too, so none is offered.
        var circles = string.Concat(Enumerable.Repeat("<circle r=\"1\" fill=\"&green;\" />\n", 2_000));
        var entities = await Host(Write("entities.svg", Entities.Replace("<rect", circles + "<rect", StringComparison.Ordinal)));
        var unreadable = new ScriptedChat(Call("get_drawing", new()), Say("Read."));

        await Send(entities, unreadable, "what is in it");

        Assert.True(unreadable.Results[0].Length < 40_000, $"{unreadable.Results[0].Length} characters");
        Assert.Contains("declares entities", unreadable.Results[0], StringComparison.Ordinal);
        Assert.Contains("no tool can change it", unreadable.Results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("get_element", unreadable.Results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Source:", unreadable.Results[0], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Long_List_Of_Problems_Is_Cut_And_Says_How_Many_More()
    {
        var rects = string.Concat(Enumerable.Range(0, 60).Select(_ => "<rect width=\"1\" height=\"1\" opacity=\"{{ nothing }}\" />"));
        var window = await Host(Write("broken.svgstudio", "<studio namespace=\"Demo.Icons\"><drawing name=\"Broken\"><svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">" + rects + "</svg></drawing></studio>"));
        var chat = new ScriptedChat(
            Call("get_problems", new()),
            Call("get_problems", new() { ["node"] = "0" }),
            Call("get_drawing", new() { ["node"] = "0" }),
            Call("set_attributes", new() { ["node"] = "0", ["key"] = "0", ["attributes"] = new[] { new { name = "width", value = "2" } }, ["summary"] = "widen" }),
            Say("Read."));

        await Send(window, chat, "what is wrong");

        Assert.Equal(50, chat.Results[0].Split('\n').Count(line => line.StartsWith("- 0 Broken: Unknown name 'nothing'", StringComparison.Ordinal)));
        Assert.Contains("… and 10 more; name a group or a drawing to narrow it.", chat.Results[0], StringComparison.Ordinal);
        Assert.EndsWith("0 drawing(s) with no problems.", chat.Results[0], StringComparison.Ordinal);

        // A drawing is as narrow as a path goes, and get_drawing and an edit's answer cut the list too.
        Assert.EndsWith("… and 10 more.", chat.Results[1], StringComparison.Ordinal);
        Assert.Equal(50, chat.Results[2].Split('\n').Count(line => line.StartsWith("Problem: Unknown name 'nothing'", StringComparison.Ordinal)));
        Assert.Contains("… and 10 more.\n", chat.Results[2], StringComparison.Ordinal);
        Assert.Equal(50, chat.Results[3].Split('\n').Count(line => line.StartsWith("- Unknown name 'nothing'", StringComparison.Ordinal)));
        Assert.EndsWith("… and 10 more.", chat.Results[3], StringComparison.Ordinal);
    }

    /// <remarks>
    /// A library of icons, or a traced drawing of thousands of paths, listed whole ran past what a
    /// client takes, and get_element sent a path's whole d where get_drawing had pointed to it.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Project_Or_A_Drawing_Too_Large_To_List_Whole_Is_Cut_And_Says_How_To_Narrow_It()
    {
        var icon = "<drawing name=\"An icon with a name long enough to fill a row\"><svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" /></drawing>";
        var sets = string.Concat(Enumerable.Range(0, 10).Select(set => $"<group name=\"Set {set}\">{string.Concat(Enumerable.Repeat(icon, 100))}</group>"));
        var paths = string.Concat(Enumerable.Range(0, 30).Select(_ => "<g>" + string.Concat(Enumerable.Repeat("<path d=\"M0 0h1\" />", 100)) + "</g>"));
        var scribble = "M0 0" + string.Concat(Enumerable.Repeat(" l1 1", 10_000));
        var traced = $"<drawing name=\"Traced\"><svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">{paths}<path d=\"{scribble}\" /></svg></drawing>";
        var window = await Host(Write("library.svgstudio", $"<studio namespace=\"Demo.Icons\">{traced}<group name=\"Library\">{sets}</group></studio>"));
        var chat = new ScriptedChat(
            Call("get_project", new()),
            Call("get_project", new() { ["node"] = "0/3" }),
            Call("get_drawing", new() { ["node"] = "1" }),
            Call("get_drawing", new() { ["node"] = "1", ["key"] = "5" }),
            Call("get_element", new() { ["node"] = "1", ["key"] = "30" }),
            Call("get_project", new() { ["node"] = "1" }),
            Call("get_drawing", new() { ["node"] = "1", ["key"] = "31" }),
            Say("Read."));

        await Send(window, chat, "what is in it");

        Assert.All(chat.Results, result => Assert.True(result.Length < 41_000, $"{result.Length} characters"));
        Assert.Matches(@"\n… and \d+ more; get_project with a group's path lists only what is under it\.\n$", chat.Results[0]);
        Assert.Equal(100, chat.Results[1].Split('\n').Count(line => line.StartsWith("0/3/", StringComparison.Ordinal)));
        Assert.DoesNotContain("… and", chat.Results[1], StringComparison.Ordinal);
        Assert.Contains("… and ", chat.Results[2], StringComparison.Ordinal);
        Assert.Contains(" more; get_drawing with an element's key lists only what is inside it.\n", chat.Results[2], StringComparison.Ordinal);
        Assert.DoesNotContain("Source:", chat.Results[2], StringComparison.Ordinal);
        Assert.Equal(101, chat.Results[3].Split('\n').Count(line => line.TrimStart().StartsWith("5", StringComparison.Ordinal)));
        Assert.DoesNotContain("\n  4", chat.Results[3], StringComparison.Ordinal);
        Assert.Contains($"d=(left out: {scribble.Length} characters", chat.Results[4], StringComparison.Ordinal);
        Assert.Equal("'1' is a drawing, not a group. get_project lists the paths.", chat.Results[5]);
        Assert.Equal("There is no element 31. get_drawing lists the keys.", chat.Results[6]);
    }

    [AvaloniaFact]
    public async Task A_Path_Is_Quoted_When_It_Names_Nothing_And_Without_A_Project_None_Is_Asked_For()
    {
        var lone = await Host(Write("drawing.svg", Drawing));
        var none = new ScriptedChat(
            Call("get_problems", new()),
            Call("get_declarations", new()),
            Call("add_group", new() { ["parent"] = "" }),
            Call("add_drawing", new() { ["parent"] = "", ["name"] = "Added", ["svg"] = Drawing }),
            Call("set_declarations", new() { ["code"] = "<e:code xmlns:e=\"https://svg.skia/expr/1.0\" />", ["summary"] = "clear" }),
            Call("remove", new() { ["node"] = "0" }),
            Call("get_drawing", new() { ["node"] = "0" }),
            Say("No project."));

        await Send(lone, none, "what is wrong", allow: true);

        Assert.All(none.Results, result => Assert.Equal("No project is open.", result));

        var window = await Host(Write("icons.svgstudio", Project));
        var named = new ScriptedChat(
            Call("remove", new() { ["node"] = "" }),
            Call("remove", new() { ["node"] = "(project)" }),
            Call("get_problems", new() { ["node"] = "9" }),
            Call("add_group", new() { ["parent"] = "1" }),
            Call("move", new() { ["node"] = "0", ["into"] = "1" }),
            Say("Refused."));

        await Send(window, named, "remove everything", allow: true);

        Assert.Equal("The project itself cannot be removed.", named.Results[0]);
        Assert.Equal("The project itself cannot be removed.", named.Results[1]);
        Assert.Equal("There is no node '9'. get_project lists the paths.", named.Results[2]);
        Assert.Equal("'1' is a drawing, not a group. get_project lists the paths.", named.Results[3]);
        Assert.Equal(named.Results[3], named.Results[4]);
        Assert.Equal(2, window.Workspace!.Document.Root.Children.Count);
    }

    [AvaloniaFact]
    public async Task A_Groups_Declarations_Are_Read_And_Replaced_As_One_Step()
    {
        var window = await Host(Write("declared.svgstudio", Declared));
        var group = (ProjectGroup)window.Workspace!.Document.Root.Children[0];
        var before = group.CodeText;
        var replaced = before.Replace("</e:code>", "  <e:param name=\"size\" type=\"number\" default=\"24\" />\n            </e:code>", StringComparison.Ordinal);

        var chat = new ScriptedChat(
            Call("get_declarations", new() { ["node"] = "0" }),
            Call("set_declarations", new() { ["node"] = "0", ["code"] = replaced, ["summary"] = "add size" }),
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
        var group = (ProjectGroup)window.Workspace!.Document.Root.Children[0];
        var before = group.CodeText;
        var shadowing = before.Replace("name=\"tint\" type=\"color\" default=\"#ff0000\"", "name=\"angle\" type=\"number\" default=\"1\"", StringComparison.Ordinal);

        var chat = new ScriptedChat(
            Call("set_declarations", new() { ["node"] = "0", ["code"] = "<g />", ["summary"] = "wrong" }),
            Call("set_declarations", new() { ["node"] = "0", ["code"] = shadowing, ["summary"] = "shadow" }),
            Say("Refused."));

        await Send(window, chat, "break it");

        Assert.Contains("<e:code> block", chat.Results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("one undo step", chat.Results[1], StringComparison.Ordinal);
        Assert.Contains("angle", chat.Results[1], StringComparison.Ordinal);
        Assert.Equal(before, group.CodeText);
        Assert.False(window.Workspace.IsEdited);
    }

    [AvaloniaFact]
    public async Task A_Parameter_Is_Added_Changed_And_Renamed_On_A_Group_Through_Every_Drawing_Under_It_Each_As_One_Step()
    {
        var window = await Host(Write("washed.svgstudio", Washed));
        var badges = (ProjectGroup)window.Workspace!.Document.Root.Children[0];
        var filled = (ProjectDrawing)badges.Children[0];
        var round = (ProjectGroup)badges.Children[1];
        var tools = new AssistantTools(window);
        var chat = new ScriptedChat(
            Call("declare", new() { ["node"] = "0", ["name"] = "tint", ["type"] = "color", ["default"] = "#ff0000", ["summary"] = "add tint" }),
            Call("declare", new() { ["node"] = "0", ["name"] = "tint", ["default"] = "#00ff00", ["summary"] = "greener" }),
            Call("declare", new() { ["node"] = "0", ["name"] = "tint", ["to"] = "hue", ["summary"] = "rename tint" }),
            Say("Declared."));

        await Send(window, chat, "declare a tint on Badges", tools: tools);

        Assert.Equal("Done. It is one undo step. No problems in the 2 drawing(s) under it.", chat.Results[0]);
        Assert.Equal("Done. It is one undo step. No problems in the 2 drawing(s) under it.", chat.Results[1]);
        Assert.Equal("Done. Every drawing and group under it that named tint now names hue. It is one undo step. No problems in the 2 drawing(s) under it.", chat.Results[2]);
        Assert.Contains("name=\"hue\" type=\"color\" default=\"#00ff00\"", badges.CodeText, StringComparison.Ordinal);
        Assert.Contains("fill=\"{{ hue }}\"", filled.Text, StringComparison.Ordinal);
        Assert.Contains(">hue</e:let>", round.CodeText, StringComparison.Ordinal);
        Assert.Equal("Assistant: rename tint", window.Workspace.UndoLabel);

        var undo = new ScriptedChat(Call("undo", new()), Call("undo", new()), Say("Undone."));

        await Send(window, undo, "undo twice", tools: tools);

        Assert.Equal("Took back \"Assistant: rename tint\".", undo.Results[0]);
        Assert.Equal("Took back \"Assistant: greener\".", undo.Results[1]);
        Assert.Contains("name=\"tint\" type=\"color\" default=\"#ff0000\"", badges.CodeText, StringComparison.Ordinal);
        Assert.Contains("fill=\"{{ tint }}\"", filled.Text, StringComparison.Ordinal);
        Assert.Contains(">tint</e:let>", round.CodeText, StringComparison.Ordinal);
    }

    /// <remarks>A tab's document had nothing from above to read a let against, so one naming the project's angle was refused as naming nothing.</remarks>
    [AvaloniaFact]
    public async Task A_Let_Declared_On_A_Drawing_In_Its_Tab_May_Name_What_The_Project_Declares()
    {
        var window = await Host(Write("keyed.svgstudio", Keyed));
        var keys = (ProjectDrawing)((ProjectGroup)window.Workspace!.Document.Root.Children[0]).Children[0];

        await window.ShowAsync(keys);
        Dispatcher.UIThread.RunJobs();

        var chat = new ScriptedChat(
            Call("declare", new() { ["node"] = "0/0", ["name"] = "turn", ["expression"] = "angle * 2", ["summary"] = "turn twice" }),
            Say("Declared."));

        await Send(window, chat, "turn it twice the angle");

        var viewer = Viewer(window);

        Assert.StartsWith("Done. It is one undo step.", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("<e:let name=\"turn\">angle * 2</e:let>", viewer.Source, StringComparison.Ordinal);
        Assert.Equal("Assistant: turn twice", viewer.UndoLabel);
        Assert.DoesNotContain("turn", keys.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Name_A_Group_Above_Declares_Is_Refused_On_The_Drawing_With_The_Path_To_Change_It_At()
    {
        var window = await Host(Write("keyed.svgstudio", Keyed));
        var chat = new ScriptedChat(
            Call("declare", new() { ["node"] = "0/1", ["name"] = "tint", ["default"] = "#00ff00", ["summary"] = "greener" }),
            Call("declare", new() { ["node"] = "0", ["name"] = "angle", ["default"] = "30", ["summary"] = "steeper" }),
            Call("declare", new() { ["node"] = "0/0", ["name"] = "tint", ["to"] = "size", ["summary"] = "rename tint" }),
            Say("Refused."));

        await Send(window, chat, "change them where they are used");

        Assert.Equal("'tint' is declared by 0 Badges, which every drawing under it inherits; declare it there, with node 0.", chat.Results[0]);
        Assert.Equal("'angle' is declared by (project) Project, which every drawing under it inherits; declare it there, with node (project).", chat.Results[1]);

        // Not taken for a rename retried, although the drawing declares the name it would have left.
        Assert.Equal(chat.Results[0], chat.Results[2]);
        Assert.False(window.Workspace!.IsEdited);
    }

    [AvaloniaFact]
    public async Task A_Declaration_Given_Parts_Its_Kind_Does_Not_Take_Is_Refused_Saying_What_It_Takes()
    {
        var window = await Host(Write("keyed.svgstudio", Keyed));
        var chat = new ScriptedChat(
            Call("declare", new() { ["node"] = "0/0", ["name"] = "a", ["expression"] = "3", ["summary"] = "x" }),
            Call("declare", new() { ["node"] = "0/0", ["name"] = "size", ["default"] = "4", ["summary"] = "x" }),
            Call("declare", new() { ["node"] = "0/0", ["name"] = "fresh", ["summary"] = "x" }),
            Call("declare", new() { ["node"] = "0/0", ["name"] = "fresh", ["to"] = "fresher", ["summary"] = "x" }),
            Call("declare", new() { ["node"] = "0/0", ["name"] = "a", ["type"] = "color", ["summary"] = "x" }),
            Say("Refused."));

        await Send(window, chat, "declare things");

        Assert.Equal("'a' is a parameter: it takes type, default, min, max and step, not expression.", chat.Results[0]);
        Assert.Equal("'size' is a let: it takes expression, not type, default, min, max or step.", chat.Results[1]);
        Assert.Equal("A new parameter needs a type, and a new let an expression.", chat.Results[2]);
        Assert.Equal("Nothing here declares 'fresh' to rename.", chat.Results[3]);
        Assert.Contains("cannot become", chat.Results[4], StringComparison.Ordinal);
        Assert.False(window.Workspace!.IsEdited);
    }

    [AvaloniaFact]
    public async Task A_Part_Of_A_Declaration_Given_Empty_Is_Cleared_And_One_Left_Out_Stays()
    {
        var window = await Host(Write("keyed.svgstudio", Keyed));
        var keys = (ProjectDrawing)((ProjectGroup)window.Workspace!.Document.Root.Children[0]).Children[0];
        var chat = new ScriptedChat(
            Call("declare", new() { ["node"] = "0/0", ["name"] = "a", ["min"] = "1", ["max"] = "10", ["step"] = "1", ["summary"] = "step a" }),
            Call("declare", new() { ["node"] = "0/0", ["name"] = "a", ["step"] = "", ["summary"] = "slide a" }),
            Say("Declared."));

        await Send(window, chat, "step a, then let it slide");

        Assert.All(chat.Results.Take(2), result => Assert.StartsWith("Done.", result, StringComparison.Ordinal));

        var a = SvgExpressionDeclarations.Parse(keys.Text, out _).Parameters.Single(one => one.Name == "a");

        Assert.Null(a.StepExpression);
        Assert.Equal(("2", "1", "10"), (a.DefaultExpression, a.MinExpression, a.MaxExpression));
    }

    /// <remarks>
    /// Rewritten in the project under a tab holding edits, the drawing's next save would put the old
    /// name back. The refusal named the first such tab alone, and told the client to close it, which
    /// no tool does.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Rename_Is_Refused_Whole_While_A_Drawing_Under_The_Group_Holds_Edits_In_Its_Tab()
    {
        var window = await Host(Write("washed.svgstudio", Washed.Replace("<group name=\"Badges\">", "<group name=\"Badges\"><e:code xmlns:e=\"https://svg.skia/expr/1.0\"><e:param name=\"tint\" type=\"color\" default=\"#ff0000\" /></e:code>", StringComparison.Ordinal)));
        var badges = (ProjectGroup)window.Workspace!.Document.Root.Children[0];
        var filled = (ProjectDrawing)badges.Children[0];
        var round = (ProjectGroup)badges.Children[1];

        foreach (var drawing in new[] { filled, (ProjectDrawing)round.Children[0] })
        {
            await window.ShowAsync(drawing);
            Dispatcher.UIThread.RunJobs();

            var viewer = Viewer(window);

            viewer.SetSource(viewer.Source.Replace(" fill=", " stroke=\"black\" fill=", StringComparison.Ordinal), "mine");
        }

        var chat = new ScriptedChat(Call("declare", new() { ["node"] = "0", ["name"] = "tint", ["to"] = "hue", ["summary"] = "rename tint" }), Say("Refused."));

        await Send(window, chat, "rename tint");

        Assert.Equal(
            "Nothing was renamed: the tabs of 0/0 Filled, 0/1/0 Dot hold edits not yet in the project, which saving would write back over the rename. Open each and save, or undo those edits, then rename.",
            chat.Results[0]);
        Assert.Contains("name=\"tint\"", badges.CodeText, StringComparison.Ordinal);
        Assert.Contains(">tint</e:let>", round.CodeText, StringComparison.Ordinal);
        Assert.Contains("fill=\"{{ tint }}\"", filled.Text, StringComparison.Ordinal);
        Assert.False(window.Workspace.IsEdited);
    }

    /// <remarks>The tab in front reads the renamed text in after the rename has answered, and its problems were listed from the text it had before.</remarks>
    [AvaloniaFact]
    public async Task A_Rename_Lists_The_Problems_Of_The_Drawing_In_Front_As_The_Rename_Left_It()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var filled = (ProjectDrawing)((ProjectGroup)window.Workspace!.Document.Root.Children[0]).Children[0];

        await window.ShowAsync(filled);
        Dispatcher.UIThread.RunJobs();

        var chat = new ScriptedChat(Call("declare", new() { ["node"] = "0", ["name"] = "tint", ["to"] = "hue", ["summary"] = "rename tint" }), Say("Renamed."));

        await Send(window, chat, "rename tint");

        Assert.Equal("Done. Every drawing and group under it that named tint now names hue. It is one undo step. No problems in the 1 drawing(s) under it.", chat.Results[0]);
        Assert.Contains("fill=\"{{ hue }}\"", filled.Text, StringComparison.Ordinal);
    }

    /// <remarks>Held there, it is in no save of another tab's and blocks a rename through its group, which the answer said nothing of.</remarks>
    [AvaloniaFact]
    public async Task An_Edit_A_Tab_Holds_Says_Once_That_A_Save_Of_That_Tab_Takes_It_Into_The_Project()
    {
        var window = await Host(Write("icons.svgstudio", Project));

        await window.ShowAsync(window.Workspace!.Document.Root.Children[1]);
        Dispatcher.UIThread.RunJobs();

        var chat = new ScriptedChat(
            Call("set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#ff0000" } }, ["summary"] = "red" }),
            Call("set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#0000ff" } }, ["summary"] = "blue" }),
            Call("set_attributes", new() { ["node"] = "0/0", ["key"] = "0", ["attributes"] = new[] { new { name = "opacity", value = "0.5" } }, ["summary"] = "fade" }),
            Say("Done."));

        await Send(window, chat, "recolour");

        Assert.Equal(
            "Done. It is one undo step. It is held in the tab of 1 home, not yet in the project: save with that tab in front takes it in, and until then a rename through its group, or a commit with another tab in front, is refused. No problems.",
            chat.Results[0]);
        Assert.Equal("Done. It is one undo step. No problems.", chat.Results[1]);
        Assert.Equal("Done. It is one undo step. No problems.", chat.Results[2]);
    }

    [AvaloniaFact]
    public async Task Set_Node_Says_It_Cleared_A_Setting_And_Its_Padding_Reads_As_A_Fraction_Of_The_Size()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var chat = new ScriptedChat(
            Call("set_node", new() { ["node"] = "1", ["setting"] = "class", ["value"] = "Badge" }),
            Call("set_node", new() { ["node"] = "1", ["setting"] = "class", ["value"] = "" }),
            Say("Done."));

        await Send(window, chat, "give Badges a class, then take it off");

        var setting = new AssistantTools(window).For(small: false, docs: false).OfType<AIFunction>().Single(tool => tool.Name == "set_node")
            .JsonSchema.GetProperty("properties").GetProperty("setting").GetProperty("description").GetString();

        Assert.Equal("Set class on 1. It is one undo step.", chat.Results[0]);
        Assert.Equal("Cleared class on 1. It is one undo step.", chat.Results[1]);
        Assert.Contains("padding is a fraction of the drawing's size", setting, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task An_Undo_Of_A_Rename_Waits_While_A_Drawing_It_Rewrote_Holds_Edits_In_A_Tab_Opened_Since()
    {
        var window = await Host(Write("washed.svgstudio", Washed.Replace("<group name=\"Badges\">", "<group name=\"Badges\"><e:code xmlns:e=\"https://svg.skia/expr/1.0\"><e:param name=\"tint\" type=\"color\" default=\"#ff0000\" /></e:code>", StringComparison.Ordinal)));
        var badges = (ProjectGroup)window.Workspace!.Document.Root.Children[0];
        var filled = (ProjectDrawing)badges.Children[0];
        var tools = new AssistantTools(window);

        await Send(window, new ScriptedChat(Call("declare", new() { ["node"] = "0", ["name"] = "tint", ["to"] = "hue", ["summary"] = "rename tint" }), Say("Renamed.")), "rename tint", tools: tools);

        Assert.Contains("fill=\"{{ hue }}\"", filled.Text, StringComparison.Ordinal);

        await window.ShowAsync(filled);
        Dispatcher.UIThread.RunJobs();

        var viewer = Viewer(window);

        viewer.SetSource(viewer.Source.Replace("<rect ", "<rect stroke=\"black\" ", StringComparison.Ordinal), "mine");

        var chat = new ScriptedChat(Call("undo", new()), Say("Not undone."));

        await Send(window, chat, "undo", tools: tools);

        Assert.StartsWith("Nothing was taken back: 0/0 Filled has edits in its tab not yet saved into the project, made over \"Assistant: rename tint\"", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("name=\"hue\"", badges.CodeText, StringComparison.Ordinal);

        Assert.True(viewer.Undo());

        var again = new ScriptedChat(Call("undo", new()), Say("Undone."));

        await Send(window, again, "undo", tools: tools);

        Assert.Equal("Took back \"Assistant: rename tint\".", again.Results[0]);
        Assert.Contains("name=\"tint\"", badges.CodeText, StringComparison.Ordinal);
        Assert.Contains("fill=\"{{ tint }}\"", filled.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Template_Is_Extracted_From_What_A_Drawing_Paints_As_One_Step_And_Nothing_Is_Asked()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var asked = new List<string>();

        window.AskTemplateName = (name, _, _) =>
        {
            asked.Add(name);
            throw new InvalidOperationException("A tool asked for a template's name.");
        };

        var root = window.Workspace!.Document.Root;
        var tools = new AssistantTools(window);
        var chat = new ScriptedChat(Call("extract_template", new() { ["node"] = "0/0", ["name"] = "Tinted badge" }), Say("Added."));

        await Send(window, chat, "make a template of Filled", tools: tools);

        Assert.StartsWith("Added the template 'Tinted badge' to the project. It is one undo step.\n<e:recipe", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains(">tint</e:slot>", chat.Results[0], StringComparison.Ordinal);
        Assert.Equal(new[] { "Tinted badge" }, root.Templates.Select(template => template.Name));
        Assert.Equal("Assistant: add template Tinted badge", window.Workspace.UndoLabel);
        Assert.Empty(asked);

        var undo = new ScriptedChat(Call("undo", new()), Say("Undone."));

        await Send(window, undo, "undo", tools: tools);

        Assert.Equal("Took back \"Assistant: add template Tinted badge\".", undo.Results[0]);
        Assert.Empty(root.Templates);
    }

    [AvaloniaFact]
    public async Task A_Drawing_Painted_Through_No_Expression_Or_Under_A_Taken_Name_Has_No_Template_Extracted()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var chat = new ScriptedChat(
            Call("extract_template", new() { ["node"] = "1" }),
            Call("extract_template", new() { ["node"] = "0" }),
            Call("extract_template", new() { ["node"] = "0/0" }),
            Call("extract_template", new() { ["node"] = "0/0", ["name"] = "Filled" }),
            Call("extract_template", new() { ["node"] = "(project)" }),
            Say("Done."));

        await Send(window, chat, "make templates");

        Assert.Equal("1 home: Nothing here is painted through an expression, so there is nothing for a template to write.", chat.Results[0]);
        Assert.Equal("'0' is a group, not a drawing. get_project lists the paths.", chat.Results[1]);
        Assert.StartsWith("Added the template 'Filled' to the project.", chat.Results[2], StringComparison.Ordinal);
        Assert.Equal("The project already has a template called 'Filled'.", chat.Results[3]);
        Assert.Equal("'(project)' is the project, not a drawing. get_project lists the paths.", chat.Results[4]);
        Assert.Single(window.Workspace!.Document.Root.Templates);

        var lone = await Host(Write("home.svg", Drawing));
        var none = new ScriptedChat(Call("extract_template", new()), Say("No project."));

        await Send(lone, none, "make a template");

        Assert.Equal("Templates are kept in a project, and the drawing in front is in none.", none.Results[0]);
    }

    [AvaloniaFact]
    public async Task A_Drawing_Added_Through_A_Template_Brings_What_Its_Group_Lacks_In_The_Same_Step()
    {
        var window = await Host(Write("icons.svgstudio", Templated));
        var empty = (ProjectGroup)window.Workspace!.Document.Root.Children[0];
        var tools = new AssistantTools(window);
        var chat = new ScriptedChat(
            Call("add_drawing", new() { ["parent"] = "0", ["name"] = "Star", ["svg"] = StreamlinePanelTests.Glyph, ["template"] = "Accent glyph" }),
            Say("Added."));

        await Send(window, chat, "add a star", tools: tools);

        Assert.Equal("Added Star as 0/1 through the template 'Accent glyph', which declared stateAccentColor on 0 Empty as well. It is one undo step.", chat.Results[0]);
        Assert.Contains("fill=\"{{ stateAccentColor }}\"", ((ProjectDrawing)empty.Children[1]).Text, StringComparison.Ordinal);
        Assert.Contains("name=\"stateAccentColor\"", empty.CodeText, StringComparison.Ordinal);
        Assert.Equal("Assistant: add Star", window.Workspace.UndoLabel);

        var undo = new ScriptedChat(Call("undo", new()), Say("Undone."));

        await Send(window, undo, "undo", tools: tools);

        Assert.Equal("Took back \"Assistant: add Star\".", undo.Results[0]);
        Assert.Single(empty.Children);
        Assert.Null(empty.Code);
    }

    [AvaloniaFact]
    public async Task A_Template_Not_There_Unreadable_Or_Not_Fitting_Is_Refused_And_Adds_Nothing()
    {
        var window = await Host(Write("icons.svgstudio", Templated));
        var empty = (ProjectGroup)window.Workspace!.Document.Root.Children[0];
        var chat = new ScriptedChat(
            Call("add_drawing", new() { ["parent"] = "0", ["name"] = "Star", ["svg"] = StreamlinePanelTests.Glyph, ["template"] = "Nope" }),
            Call("add_drawing", new() { ["parent"] = "0", ["name"] = "Star", ["svg"] = StreamlinePanelTests.Glyph, ["template"] = "Broken" }),
            Call("add_drawing", new() { ["parent"] = "0", ["name"] = "Star", ["svg"] = StreamlinePanelTests.Glyph, ["template"] = "Line icons" }),
            Call("add_drawing", new() { ["parent"] = "0", ["name"] = "Star", ["svg"] = StreamlinePanelTests.Glyph, ["template"] = "Two-tone" }),
            Call("add_drawing", new() { ["parent"] = "0", ["name"] = "Star", ["svg"] = StreamlinePanelTests.Glyph, ["template"] = "Not solid" }),
            Say("Refused."));

        await Send(window, chat, "add a star");

        const string Fitting = " Of the project's templates, Accent glyph fits this drawing, and Keep colours always does.";

        Assert.Equal("The project has no template 'Nope'." + Fitting, chat.Results[0]);
        Assert.Equal("The template 'Broken' does not read: <bogus> is not a recipe element. Expected <code>, <replace>, <slot> or <match>." + Fitting, chat.Results[1]);
        Assert.Equal("The template 'Line icons' is kept to icons of the family streamline-light, which only a Streamline import names, so it cannot be applied here." + Fitting, chat.Results[2]);
        Assert.Equal("The template 'Two-tone' does not fit this drawing." + Fitting, chat.Results[3]);

        // A negation passes a drawing whose family is not known, so it is the colours that rule it out.
        Assert.Equal("The template 'Not solid' does not fit this drawing." + Fitting, chat.Results[4]);
        Assert.Single(empty.Children);
        Assert.False(window.Workspace.IsEdited);
    }

    [AvaloniaFact]
    public async Task The_Templates_That_Do_Not_Read_Or_Fit_A_Drawing_Are_Listed_With_Why()
    {
        var window = await Host(Write("icons.svgstudio", Templated));
        var chat = new ScriptedChat(
            Call("get_templates", new()),
            Call("get_templates", new() { ["svg"] = StreamlinePanelTests.Glyph, ["parent"] = "0" }),
            Say("Listed."));

        await Send(window, chat, "which templates are there");

        Assert.Contains(
            "\n- Not solid\n- Broken (does not read: <bogus> is not a recipe element. Expected <code>, <replace>, <slot> or <match>.)\n- Keep colours",
            chat.Results[0],
            StringComparison.Ordinal);
        Assert.Contains(
            "\nNot fitting: Two-tone; Line icons, kept to the family streamline-light, which only a Streamline import names; Not solid; Broken, which does not read.\n",
            chat.Results[1],
            StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Find_Names_Each_Node_Element_Value_And_Use_By_Path_And_Key()
    {
        var window = await Host(Write("keyed.svgstudio", Keyed));
        var chat = new ScriptedChat(
            Call("find", new() { ["what"] = "tint" }),
            Call("find", new() { ["what"] = "g1", ["kind"] = "ids" }),
            Call("find", new() { ["what"] = "size", ["kind"] = "uses" }),
            Call("find", new() { ["what"] = "a * b", ["kind"] = "values" }),
            Call("find", new() { ["what"] = "badges", ["kind"] = "names" }),
            Call("find", new() { ["what"] = "angle", ["node"] = "0/0" }),
            Call("find", new() { ["what"] = "angle", ["node"] = "0/1", ["kind"] = "uses" }),
            Call("find", new() { ["what"] = "g1" }),
            Say("Found."));

        await Send(window, chat, "where is tint");

        Assert.Equal(
            "6 found for \"tint\":\n"
            + "0 group Badges: its block's param tint, name=\"tint\"\n"
            + "0/0 Keys 2 <circle> fill=\"{{ tint }}\"\n"
            + "0/0 Keys 2 <circle> uses tint\n"
            + "0/1 drawing Tinted: its name\n"
            + "0/1 Tinted 0 <rect> fill=\"{{ tint }}\"\n"
            + "0/1 Tinted 0 <rect> uses tint",
            chat.Results[0]);
        Assert.Equal("1 found for \"g1\":\n0/0 Keys 0/1 <linearGradient> #g1", chat.Results[1]);
        Assert.Equal("2 found for \"size\":\n0/0 Keys 0/0/3 <e:let> uses size\n0/0 Keys 1 <rect> uses size", chat.Results[2]);
        Assert.Equal("1 found for \"a * b\":\n0/0 Keys 0/0/2 <e:let>: \"a * b * 2\"", chat.Results[3]);
        Assert.Equal("1 found for \"badges\":\n0 group Badges: its name", chat.Results[4]);
        Assert.Equal("Nothing found for \"angle\".", chat.Results[5]);
        Assert.Equal("1 found for \"angle\":\n0/1 Tinted 0 <rect> uses angle", chat.Results[6]);

        // An id among the values as well would come up twice.
        Assert.Equal("2 found for \"g1\":\n0/0 Keys 0/1 <linearGradient> #g1\n0/0 Keys 1 <rect> fill=\"url(#g1)\"", chat.Results[7]);
    }

    [AvaloniaFact]
    public async Task Find_Reads_A_Drawing_As_Its_Tab_Holds_It_And_Says_What_It_Could_Not_Read()
    {
        var window = await Host(Write("icons.svgstudio", Project.Replace("fill=\"#00ff00\" />", "fill=\"{{ tint }}\" opacity=\"{{ tint = 1 }}\" />", StringComparison.Ordinal)));
        var filled = (ProjectDrawing)((ProjectGroup)window.Workspace!.Document.Root.Children[0]).Children[0];

        await window.ShowAsync(filled);
        Dispatcher.UIThread.RunJobs();

        var viewer = Viewer(window);

        viewer.SetSource(viewer.Source.Replace("</svg>", "<circle id=\"typed\" r=\"2\" fill=\"{{ tint }}\" /></svg>", StringComparison.Ordinal), "mine");

        var chat = new ScriptedChat(
            Call("find", new() { ["what"] = "tint", ["kind"] = "uses" }),
            Call("find", new() { ["what"] = "typed", ["kind"] = "ids" }),
            Say("Found."));

        await Send(window, chat, "where is tint");

        var rows = chat.Results[0].Split('\n');

        Assert.Equal("3 found for \"tint\":", rows[0]);
        Assert.Equal(new[] { "0/0 Filled 0 <rect> uses tint", "0/0 Filled 1 <circle> uses tint" }, rows[1..3]);
        Assert.Equal("1 home 0 <rect> uses tint", rows[3]);
        Assert.StartsWith("1 home: 'tint = 1' cannot be read", rows[4], StringComparison.Ordinal);
        Assert.Equal("1 found for \"typed\":\n0/0 Filled 1 <circle> #typed", chat.Results[1]);
        Assert.DoesNotContain("typed", filled.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Find_With_No_Project_Searches_The_Drawing_In_Front_And_Says_If_It_Does_Not_Read()
    {
        var lone = await Host(Write("home.svg", Drawing));
        var entities = await Host(Write("entities.svg", Entities));
        var empty = new MainWindow();

        empty.Show();
        Dispatcher.UIThread.RunJobs();

        var found = new ScriptedChat(Call("find", new() { ["what"] = "00FF00" }), Say("Found."));
        var unread = new ScriptedChat(Call("find", new() { ["what"] = "green" }), Say("Not read."));
        var nothing = new ScriptedChat(Call("find", new() { ["what"] = "green" }), Say("Nothing."));

        await Send(lone, found, "where is the green");
        await Send(entities, unread, "where is the green");
        await Send(empty, nothing, "where is the green");

        Assert.Equal("1 found for \"00FF00\" in the drawing in front:\n0 <rect> fill=\"#00ff00\"", found.Results[0]);
        Assert.Equal(
            "Nothing found for \"green\" in the drawing in front, but not everything could be searched:\n"
            + "The drawing in front: does not read as XML, so it was not searched: This file declares entities of its own, which cannot be written back as they were written.",
            unread.Results[0]);
        Assert.Equal("Nothing is open.", nothing.Results[0]);
    }

    [AvaloniaFact]
    public async Task A_Long_Find_Is_Cut_And_Says_How_To_Narrow_It()
    {
        var rects = string.Concat(Enumerable.Range(0, 3000).Select(index => $"<rect id=\"r{index}\" width=\"1\" height=\"1\" />"));
        var window = await Host(Write("big.svgstudio", $"<studio namespace=\"Demo.Icons\"><drawing name=\"Big\"><svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">{rects}</svg></drawing></studio>"));
        var chat = new ScriptedChat(Call("find", new() { ["what"] = "r", ["kind"] = "ids" }), Say("Found."));

        await Send(window, chat, "find the rects");

        Assert.StartsWith("3000 found for \"r\":\n0 Big 0 <rect> #r0\n", chat.Results[0], StringComparison.Ordinal);
        Assert.Matches(@"\n… and \d+ more; find with node or kind narrows it\.$", chat.Results[0]);
        Assert.True(chat.Results[0].Length < 41_000, $"{chat.Results[0].Length} characters");
    }

    [AvaloniaFact]
    public async Task Undo_Takes_Back_The_Assistants_Edit_And_Not_The_Front_Tabs()
    {
        var window = await Host(Write("declared.svgstudio", Declared));
        var dial = (ProjectDrawing)window.Workspace!.Document.Root.Children[1];
        var badge = (ProjectDrawing)((ProjectGroup)window.Workspace.Document.Root.Children[0]).Children[0];

        await window.ShowAsync(dial);
        Dispatcher.UIThread.RunJobs();

        var viewer = Viewer(window);

        // The front tab has an edit of its own; the assistant then edits a drawing in no tab.
        viewer.SetSource(viewer.Source.Replace("<rect ", "<rect stroke=\"black\" ", StringComparison.Ordinal));

        var chat = new ScriptedChat(
            Call("set_attributes", new() { ["node"] = "0/0", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#111111" } }, ["summary"] = "darken" }),
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
            Call("set_node", new() { ["node"] = "0", ["setting"] = "name", ["value"] = "Tags" }),
            // Renamed, Badges sorts after home, which is first now.
            Call("remove", new() { ["node"] = "0" }),
            Call("undo", new()),
            Call("undo", new()),
            Say("As it was."));

        await Send(window, chat, "rename Badges, remove home, then take both back", allow: true);

        Assert.Equal("Took back \"Assistant: remove home\".", chat.Results[2]);
        Assert.Equal("Took back \"Assistant: change name\".", chat.Results[3]);
        Assert.Equal(new[] { "Badges", "home" }, root.Children.Select(child => child.Name));
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
            // The new group sorts between Badges and home.
            Call("add_drawing", new() { ["parent"] = "0", ["name"] = "Star", ["svg"] = Drawing }),
            Call("move", new() { ["node"] = "2", ["into"] = "0" }),
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
        Assert.Equal(new[] { "Badges", "home" }, root.Children.Select(child => child.Name));

        // The same gestures made by the person are named as they always were.
        await window.AddGroupAsync(root);
        Assert.Equal("add group", workspace.UndoLabel);
        await window.ImportAsync(root, new[] { new TemplateImport("Star", Drawing) }, show: false);
        Assert.Equal("add Star", workspace.UndoLabel);
        Assert.True(window.Move(root.Children[3], root.Children[0]));
        Assert.Equal("move Star", workspace.UndoLabel);
        Assert.True(await window.RemoveAsync(root.Children[2]));
        Assert.Equal("remove home", workspace.UndoLabel);
    }

    /// <param name="read">Whether get_history is read first: it says undo would refuse, which undo then must.</param>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_Undo_Leaves_The_Persons_Work_Alone_Once_They_Took_Its_Edit_Back(bool read)
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var tools = new AssistantTools(window);

        workspace.Edit("mine");

        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "0", ["setting"] = "name", ["value"] = "Tags" }), Say("Renamed.")), "rename Badges", tools: tools);

        // The person takes the rename back by hand.
        Assert.True(workspace.Undo());

        var chat = new ScriptedChat([.. History(read), Call("undo", new()), Call("undo", new()), Say("Undone.")]);

        await Send(window, chat, "undo that", tools: tools);

        var results = chat.Results.Skip(read ? 1 : 0).ToList();

        Assert.All(chat.Results.Take(read ? 1 : 0), history => Assert.StartsWith("undo would answer: Nothing was taken back", history, StringComparison.Ordinal));
        Assert.StartsWith("Nothing was taken back", results[0], StringComparison.Ordinal);
        Assert.Equal("Badges", workspace.Document.Root.Children[0].Name);

        // With nothing of its own left, the next undo is the person's, and it says which.
        Assert.Equal("Took back \"mine\".", results[1]);
    }

    [AvaloniaFact]
    public async Task An_Undo_Under_A_Newer_Edit_Of_The_Persons_Takes_Nothing_And_Names_It()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var root = workspace.Document.Root;
        var badges = root.Children[0];
        var tools = new AssistantTools(window);

        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "0", ["setting"] = "name", ["value"] = "Tags" }), Say("Renamed.")), "rename Badges", tools: tools);

        // Renamed, Badges sorts after home, which is first now.
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
        var badges = workspace.Document.Root.Children[0];
        var tools = new AssistantTools(window);

        await window.ShowAsync(workspace.Document.Root.Children[1]);
        Dispatcher.UIThread.RunJobs();

        var viewer = Viewer(window);

        await Send(
            window,
            new ScriptedChat(
                Call("set_attributes", new() { ["node"] = "1", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#111111" } }, ["summary"] = "darken" }),
                Call("set_node", new() { ["node"] = "0", ["setting"] = "name", ["value"] = "Tags" }),
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
        await window.ShowAsync(workspace.Document.Root.Children[1]);
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
    public async Task A_Redo_Puts_Back_What_The_Undo_Took_By_Name_Until_A_New_Edit()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var badges = workspace.Document.Root.Children[0];

        await window.ShowAsync(workspace.Document.Root.Children[1]);
        Dispatcher.UIThread.RunJobs();

        var viewer = Viewer(window);

        // One step in the front tab and one on the project, taken back and put back newest first.
        var chat = new ScriptedChat(
            Call("set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#111111" } }, ["summary"] = "darken" }),
            Call("set_node", new() { ["node"] = "0", ["setting"] = "name", ["value"] = "Tags" }),
            Call("undo", new()),
            Call("undo", new()),
            Call("redo", new()),
            Call("redo", new()),
            Call("undo", new()),
            Call("set_node", new() { ["node"] = "0", ["setting"] = "class", ["value"] = "Tag" }),
            Call("redo", new()),
            Say("Done."));

        await Send(window, chat, "darken and rename, take both back, put both back, then rename it otherwise");

        Assert.Equal("Put back \"Assistant: darken\".", chat.Results[4]);
        Assert.Equal("Put back \"Assistant: change name\".", chat.Results[5]);
        Assert.Equal("Took back \"Assistant: change name\".", chat.Results[6]);

        // The new edit ended what the undo had taken back, as it does in any editor.
        Assert.Equal("There is nothing to put back.", chat.Results[8]);
        Assert.Contains("#111111", viewer.Source, StringComparison.Ordinal);
        Assert.Equal("Badges", badges.Name);
        Assert.Equal("Tag", badges.Class);
    }

    [AvaloniaFact]
    public async Task A_Redo_Behind_A_Step_The_Person_Took_Back_Since_Puts_Nothing_Back_And_Names_It()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var badges = workspace.Document.Root.Children[0];
        var tools = new AssistantTools(window);

        workspace.Edit("mine");

        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "0", ["setting"] = "name", ["value"] = "Tags" }), Call("undo", new()), Say("Undone.")), "rename Badges, then undo", tools: tools);

        // The person takes back their own edit, which is now in front of the rename.
        Assert.True(workspace.Undo());

        var chat = new ScriptedChat(Call("redo", new()), Call("redo", new()), Say("Not put back."));

        await Send(window, chat, "redo", tools: tools);

        Assert.All(chat.Results, result => Assert.StartsWith("Nothing was put back", result, StringComparison.Ordinal));
        Assert.Contains("\"mine\"", chat.Results[0], StringComparison.Ordinal);
        Assert.Equal("Badges", badges.Name);
        Assert.Equal("mine", workspace.RedoLabel);

        // Kept for later: once the person puts their edit back, the rename is next again.
        Assert.True(workspace.Redo());

        var again = new ScriptedChat(Call("redo", new()), Say("Put back."));

        await Send(window, again, "redo", tools: tools);

        Assert.Equal("Put back \"Assistant: change name\".", again.Results[0]);
        Assert.Equal("Tags", badges.Name);
    }

    /// <param name="read">Whether get_history is read first: it says redo would refuse, which redo then must.</param>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Redo_The_Person_Built_Over_Puts_Nothing_Back_And_Then_The_Persons_Is_Next(bool read)
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var badges = workspace.Document.Root.Children[0];
        var tools = new AssistantTools(window);

        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "0", ["setting"] = "name", ["value"] = "Tags" }), Call("undo", new()), Say("Undone.")), "rename Badges, then undo", tools: tools);

        // A new edit of the person's drops the rename from the history.
        workspace.Edit("mine");

        var chat = new ScriptedChat([.. History(read), Call("redo", new()), Say("Nothing.")]);

        await Send(window, chat, "redo", tools: tools);

        Assert.All(chat.Results.Take(read ? 1 : 0), history => Assert.Contains("\nredo would answer: Nothing was put back", history, StringComparison.Ordinal));
        Assert.StartsWith("Nothing was put back", chat.Results.Last(), StringComparison.Ordinal);
        Assert.Equal("mine", workspace.UndoLabel);
        Assert.Equal("Badges", badges.Name);

        // With nothing of its own left to put back, a redo is the person's, and says which.
        Assert.True(workspace.Undo());

        var again = new ScriptedChat(Call("redo", new()), Say("Put back."));

        await Send(window, again, "redo", tools: tools);

        Assert.Equal("Put back \"mine\".", again.Results[0]);
        Assert.Equal("mine", workspace.UndoLabel);
    }

    [AvaloniaFact]
    public async Task A_Redo_Puts_Back_The_Persons_Step_That_An_Undo_With_None_Of_Its_Own_Took()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var badges = workspace.Document.Root.Children[1];

        workspace.Edit("mine");

        var chat = new ScriptedChat(
            Call("set_node", new() { ["node"] = "1", ["setting"] = "name", ["value"] = "Tags" }),
            Call("undo", new()),
            Call("undo", new()),
            Call("redo", new()),
            Call("redo", new()),
            Say("As it was."));

        await Send(window, chat, "rename, undo twice, then redo twice");

        // Left behind, the rename refused the first redo for "mine" being in front of it.
        Assert.Equal("Took back \"mine\".", chat.Results[2]);
        Assert.Equal("Put back \"mine\".", chat.Results[3]);
        Assert.Equal("Put back \"Assistant: change name\".", chat.Results[4]);
        Assert.Equal("Tags", badges.Name);
        Assert.Equal("Assistant: change name", workspace.UndoLabel);
    }

    [AvaloniaFact]
    public async Task What_A_Redo_Put_Back_For_The_Person_The_Next_Undo_Takes_Back()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var tools = new AssistantTools(window);

        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "0", ["setting"] = "name", ["value"] = "Tags" }), Say("Renamed.")), "rename Badges", tools: tools);

        // The person edits over the rename and takes their edit back.
        workspace.Edit("mine");
        Assert.True(workspace.Undo());

        var chat = new ScriptedChat(Call("redo", new()), Call("undo", new()), Call("undo", new()), Say("As it was."));

        await Send(window, chat, "redo, then undo twice", tools: tools);

        // Not refused for "mine" being on top of the rename: this put it there.
        Assert.Equal(new[] { "Put back \"mine\".", "Took back \"mine\".", "Took back \"Assistant: change name\"." }, chat.Results);
        Assert.Equal("Badges", workspace.Document.Root.Children[0].Name);
    }

    [AvaloniaFact]
    public async Task The_History_Names_Each_Step_And_What_Undo_And_Redo_Would_Do_Now()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var tools = new AssistantTools(window);

        workspace.Edit("mine");
        await window.ShowAsync(workspace.Document.Root.Children[1]);
        Dispatcher.UIThread.RunJobs();

        var viewer = Viewer(window);

        viewer.SetSource(viewer.Source.Replace("<rect ", "<rect stroke=\"black\" ", StringComparison.Ordinal), "typed");

        var chat = new ScriptedChat(
            Call("set_node", new() { ["node"] = "0", ["setting"] = "name", ["value"] = "Tags" }),
            Call("get_history", new()),
            Say("Read."));

        await Send(window, chat, "rename Badges, then read the history", tools: tools);

        Assert.Equal(
            """
            undo would take back "Assistant: change name" from the project's history.
            redo would answer: There is nothing to put back.
            Cmd/Ctrl+Z in Studio would take back "typed" from the front tab; a text box with the caret in it takes the keystroke first.
            The front tab, 0 home, newest first:
            undo:
            - typed
            redo: none
            The project, newest first:
            undo:
            - Assistant: change name
            - mine
            redo: none
            Steps named "Assistant: …" are these tools', for whichever client called them; another "Name: …" is another assistant's; the rest are the person's.
            """,
            chat.Results[1]);

        // A newer step of the person's on top of the rename, which undo leaves where it is.
        workspace.Edit("theirs");

        var again = new ScriptedChat(Call("get_history", new()), Say("Read."));

        await Send(window, again, "read the history", tools: tools);

        Assert.StartsWith(
            "undo would answer: Nothing was taken back: \"theirs\" is on top of \"Assistant: change name\" in the project's history",
            again.Results[0],
            StringComparison.Ordinal);
        Assert.Contains("The project, newest first:\nundo:\n- theirs\n- Assistant: change name\n- mine\nredo: none\n", again.Results[0], StringComparison.Ordinal);

        // A step of its own in the front tab, read there and then from behind another tab. Renamed,
        // Badges sorts after home.
        var red = new ScriptedChat(
            Call("set_attributes", new() { ["node"] = "0", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#ff0000" } }, ["summary"] = "red" }),
            Call("get_history", new()),
            Say("Reddened."));

        await Send(window, red, "redden home, then read the history", tools: tools);
        await window.ShowAsync(((ProjectGroup)workspace.Document.Root.Children[1]).Children[0]);
        Dispatcher.UIThread.RunJobs();

        var behind = new ScriptedChat(Call("get_history", new()), Say("Read."));

        await Send(window, behind, "read the history", tools: tools);

        Assert.StartsWith("undo would take back \"Assistant: red\" from the front tab's history.\n", red.Results[1], StringComparison.Ordinal);
        Assert.Contains("The front tab, 0 home, newest first:\nundo:\n- Assistant: red\n- typed\nredo: none\n", red.Results[1], StringComparison.Ordinal);
        Assert.StartsWith("undo would take back \"Assistant: red\" from the history of a tab not in front.\n", behind.Results[0], StringComparison.Ordinal);
        Assert.Contains("\nCmd/Ctrl+Z in Studio would take back \"theirs\" from the project;", behind.Results[0], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Reading_The_History_Leaves_Undo_As_It_Was()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var badges = workspace.Document.Root.Children[0];
        var tools = new AssistantTools(window);
        var fresh = new ScriptedChat(Call("get_history", new()), Say("Read."));

        await Send(window, fresh, "read the history", tools: tools);

        Assert.StartsWith(
            """
            undo would answer: There is nothing to take back.
            redo would answer: There is nothing to put back.
            Cmd/Ctrl+Z in Studio would take nothing back; a text box with the caret in it takes the keystroke first.

            """,
            fresh.Results[0],
            StringComparison.Ordinal);

        workspace.Edit("mine");

        // With none of its own, what undo would take is the person's, which reading neither takes nor keeps.
        var read = new ScriptedChat(Call("get_history", new()), Call("get_history", new()), Say("Read."));

        await Send(window, read, "read the history", tools: tools);

        // No tab in front can take a step, so the window's own undo is the project's.
        Assert.StartsWith(
            """
            undo would take back "mine" from the project's history.
            redo would answer: There is nothing to put back.
            Cmd/Ctrl+Z in Studio would take back "mine" from the project; a text box with the caret in it takes the keystroke first.

            """,
            read.Results[0],
            StringComparison.Ordinal);
        Assert.Equal(read.Results[0], read.Results[1]);
        Assert.Equal("mine", workspace.UndoLabel);
        Assert.False(workspace.CanRedo);

        // Read with the rename on top, then under a step of the person's: had a reading dropped the
        // rename, the undo would take the person's step in place of refusing for it.
        var renamed = new ScriptedChat(
            Call("set_node", new() { ["node"] = "0", ["setting"] = "name", ["value"] = "Tags" }),
            Call("get_history", new()),
            Call("get_history", new()),
            Say("Renamed."));

        await Send(window, renamed, "rename Badges, and read the history", tools: tools);

        workspace.Edit("theirs");

        var under = new ScriptedChat(Call("get_history", new()), Call("get_history", new()), Call("undo", new()), Say("Not undone."));

        await Send(window, under, "read the history, and undo", tools: tools);

        Assert.StartsWith("undo would take back \"Assistant: change name\" from the project's history.\n", renamed.Results[1], StringComparison.Ordinal);
        Assert.Equal(renamed.Results[1], renamed.Results[2]);
        Assert.Equal(under.Results[0], under.Results[1]);
        Assert.StartsWith("Nothing was taken back: \"theirs\" is on top of \"Assistant: change name\"", under.Results[2], StringComparison.Ordinal);
        Assert.Equal("theirs", workspace.UndoLabel);

        // Once the person takes theirs back, the rename is on top again.
        Assert.True(workspace.Undo());

        var chat = new ScriptedChat(
            Call("undo", new()),
            Call("get_history", new()),
            Call("redo", new()),
            Call("undo", new()),
            Call("undo", new()),
            Say("As it was."));

        await Send(window, chat, "step the history about", tools: tools);

        Assert.Equal("Took back \"Assistant: change name\".", chat.Results[0]);
        Assert.StartsWith(
            "undo would take back \"mine\" from the project's history.\nredo would put back \"Assistant: change name\" from the project's history.\n",
            chat.Results[1],
            StringComparison.Ordinal);
        Assert.Equal(new[] { "Put back \"Assistant: change name\".", "Took back \"Assistant: change name\".", "Took back \"mine\"." }, chat.Results.Skip(2));
        Assert.Equal("Badges", badges.Name);
        Assert.False(workspace.CanUndo);
    }

    [AvaloniaFact]
    public async Task A_Redo_Never_Reaches_Into_A_Project_Since_Reopened()
    {
        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);
        var tools = new AssistantTools(window);

        await Send(window, new ScriptedChat(Call("set_node", new() { ["node"] = "0", ["setting"] = "name", ["value"] = "Tags" }), Call("undo", new()), Say("Undone.")), "rename Badges, then undo", tools: tools);

        var closed = window.Workspace!;

        // Reopened, as a pull does.
        window.ConfirmDiscard = _ => Task.FromResult(true);
        await window.OpenAsync(new[] { path });
        Dispatcher.UIThread.RunJobs();

        var chat = new ScriptedChat(Call("redo", new()), Say("Nothing."));

        await Send(window, chat, "redo", tools: tools);

        Assert.StartsWith("Nothing was put back", chat.Results[0], StringComparison.Ordinal);
        Assert.False(closed.CanUndo);
        Assert.Equal("Badges", window.Workspace!.Document.Root.Children[0].Name);
    }

    [AvaloniaFact]
    public async Task A_Redo_That_Would_Close_A_Tab_Holding_Edits_Puts_Nothing_Back_And_Names_It()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var badges = (ProjectGroup)window.Workspace!.Document.Root.Children[0];
        var filled = badges.Children[0];
        var tools = new AssistantTools(window);

        await Send(
            window,
            new ScriptedChat(Call("remove", new() { ["node"] = "0/0" }), Call("undo", new()), Call("open", new() { ["node"] = "0/0" }), Say("Back.")),
            "remove Filled, take it back and open it",
            allow: true,
            tools: tools);

        var viewer = Viewer(window);

        // Put back, the remove closes the tab without asking, and what is typed there goes with it.
        viewer.SetSource(viewer.Source.Replace("<rect ", "<rect stroke=\"black\" ", StringComparison.Ordinal), "mine");

        // The second once an edit of the assistant's own has ended its redo, so the window's is next.
        var chat = new ScriptedChat(
            Call("redo", new()),
            Call("set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#111111" } }, ["summary"] = "darken" }),
            Call("redo", new()),
            Say("Not put back."));

        await Send(window, chat, "put the remove back", tools: tools);

        Assert.All(
            new[] { chat.Results[0], chat.Results[2] },
            result => Assert.StartsWith("Nothing was put back: 0/0 Filled has edits in its tab", result, StringComparison.Ordinal));
        Assert.Contains("\"Assistant: remove Filled\"", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains(filled, badges.Children);
        Assert.Contains(viewer, window.GetVisualDescendants().OfType<SvgViewer>());
        Assert.Contains("stroke=\"black\"", viewer.Source, StringComparison.Ordinal);
        Assert.Contains("#111111", viewer.Source, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task An_Undo_That_Would_Close_A_Tab_Holding_Edits_Takes_Nothing_Until_They_Are_Gone()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var root = window.Workspace!.Document.Root;
        var tools = new AssistantTools(window);

        await Send(window, new ScriptedChat(Call("add_drawing", new() { ["parent"] = "", ["name"] = "Star", ["svg"] = Drawing }), Say("Added.")), "add a star", tools: tools);

        // It opens on the drawing it added, and the person types there.
        var viewer = Viewer(window);

        viewer.SetSource(viewer.Source.Replace("<rect ", "<rect stroke=\"black\" ", StringComparison.Ordinal), "mine");

        var chat = new ScriptedChat(Call("undo", new()), Say("Not undone."));

        await Send(window, chat, "undo", tools: tools);

        Assert.StartsWith("Nothing was taken back: 2 Star has edits in its tab", chat.Results[0], StringComparison.Ordinal);
        Assert.Equal(new[] { "Badges", "home", "Star" }, root.Children.Select(child => child.Name));
        Assert.Contains("stroke=\"black\"", viewer.Source, StringComparison.Ordinal);

        // Kept: once the person takes their typing back, the add is next.
        Assert.True(viewer.Undo());

        var again = new ScriptedChat(Call("undo", new()), Say("Undone."));

        await Send(window, again, "undo", tools: tools);

        Assert.Equal("Took back \"Assistant: add Star\".", again.Results[0]);
        Assert.Equal(new[] { "Badges", "home" }, root.Children.Select(child => child.Name));
    }

    /// <remarks>
    /// Taken back underneath, the tab went on showing the edit and handing it to the tools, and its
    /// next save wrote it into the project again.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Project_Edit_Is_Not_Stepped_Under_The_Persons_Typing_In_A_Tab_Opened_On_It_Since()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var home = (ProjectDrawing)window.Workspace!.Document.Root.Children[1];
        var tools = new AssistantTools(window);

        await Send(
            window,
            new ScriptedChat(Call("set_attributes", new() { ["node"] = "1", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#111111" } }, ["summary"] = "darken" }), Say("Done.")),
            "darken home",
            tools: tools);

        await window.ShowAsync(home);
        Dispatcher.UIThread.RunJobs();

        var viewer = Viewer(window);

        viewer.SetSource(viewer.Source.Replace("<rect ", "<rect stroke=\"black\" ", StringComparison.Ordinal), "mine");

        var chat = new ScriptedChat(Call("undo", new()), Say("Not undone."));

        await Send(window, chat, "undo", tools: tools);

        Assert.StartsWith("Nothing was taken back: 1 home has edits in its tab", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("\"Assistant: darken\"", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("#111111", home.Text, StringComparison.Ordinal);

        // Once the person takes their typing back, the edit is next; typed over again, the redo is refused the same way.
        Assert.True(viewer.Undo());

        var again = new ScriptedChat(Call("undo", new()), Say("Undone."));

        await Send(window, again, "undo", tools: tools);

        Assert.Equal("Took back \"Assistant: darken\".", again.Results[0]);
        Assert.DoesNotContain("#111111", home.Text, StringComparison.Ordinal);

        await Gestures.Until(() => !viewer.Source.Contains("#111111", StringComparison.Ordinal));
        viewer.SetSource(viewer.Source.Replace("<rect ", "<rect stroke=\"black\" ", StringComparison.Ordinal), "mine");

        var redo = new ScriptedChat(Call("redo", new()), Say("Not put back."));

        await Send(window, redo, "redo", tools: tools);

        Assert.StartsWith("Nothing was put back: 1 home has edits in its tab", redo.Results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("#111111", home.Text, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A tab behind the front one is reloaded from the project once it comes to the front, so an
    /// edit made in its buffer was thrown away then, and what it read was the text before the step.
    /// </remarks>
    [AvaloniaFact]
    public async Task An_Edit_To_A_Drawing_Whose_Tab_Is_Out_Of_Date_Goes_To_The_Project_And_Shows_When_It_Comes_Back()
    {
        var window = await Host(Write("sized.svgstudio", """
            <studio namespace="Demo.Icons">
              <group name="Badges" width="24" height="24">
                <drawing name="Filled">
                  <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" /></svg>
                </drawing>
                <drawing name="Wide" width="40">
                  <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" /></svg>
                </drawing>
              </group>
            </studio>
            """));
        var group = (ProjectGroup)window.Workspace!.Document.Root.Children[0];
        var filled = (ProjectDrawing)group.Children[0];
        var tools = new AssistantTools(window);

        await window.ShowAsync(filled);
        Dispatcher.UIThread.RunJobs();

        var viewer = Viewer(window);

        await window.ShowAsync(group.Children[1]);
        Dispatcher.UIThread.RunJobs();

        // The group's width is Filled's, so its tab behind is out of date.
        var chat = new ScriptedChat(
            Call("set_node", new() { ["node"] = "0", ["setting"] = "width", ["value"] = "48" }),
            Call("set_attributes", new() { ["node"] = "0/0", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#123456" } }, ["summary"] = "deepen" }),
            Call("get_drawing", new() { ["node"] = "0/0" }),
            Say("Done."));

        await Send(window, chat, "widen Badges and deepen Filled", tools: tools);

        Assert.StartsWith("Done.", chat.Results[1], StringComparison.Ordinal);
        Assert.Contains("#123456", filled.Text, StringComparison.Ordinal);
        Assert.False(viewer.IsSourceModified);
        Assert.Contains("#123456", chat.Results[2], StringComparison.Ordinal);

        await window.ShowAsync(filled);
        await Gestures.Until(() => viewer.Source.Contains("#123456", StringComparison.Ordinal));

        Assert.False(viewer.IsSourceModified);

        await window.ShowAsync(group.Children[1]);
        Dispatcher.UIThread.RunJobs();

        var back = new ScriptedChat(Call("undo", new()), Call("get_drawing", new() { ["node"] = "0/0" }), Say("Undone."));

        await Send(window, back, "undo", tools: tools);

        Assert.Equal("Took back \"Assistant: deepen\".", back.Results[0]);
        Assert.DoesNotContain("#123456", back.Results[1], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Nodes_Own_Settings_Are_Told_From_What_It_Inherits_And_From_Where()
    {
        var window = await Host(Write("sized.svgstudio", Sized));

        var chat = new ScriptedChat(
            Call("get_node", new() { ["node"] = "(project)" }),
            Call("get_node", new() { ["node"] = "0" }),
            Call("get_node", new() { ["node"] = "0/0" }),
            Call("get_node", new() { ["node"] = "0/1" }),
            Call("get_node", new() { ["node"] = "3" }),
            Say("Read."));

        await Send(window, chat, "what are these built at?");

        var (project, group, filled, wide) = (chat.Results[0], chat.Results[1], chat.Results[2], chat.Results[3]);

        Assert.StartsWith("(project) sized.svgstudio\n", project, StringComparison.Ordinal);
        Assert.Contains("\nnamespace=\"Demo.Icons\"\n", project, StringComparison.Ordinal);
        Assert.Contains("\nUnset: class, helperScope, skiaSharp, width, height, scale, padding, api, resizing, context, order, body, typeface, fontsFolder\n", project, StringComparison.Ordinal);
        Assert.Contains("Hands down no size", project, StringComparison.Ordinal);

        Assert.StartsWith("0 group Badges\n", group, StringComparison.Ordinal);
        Assert.Contains("\nnamespace=\"Demo.Icons\" from (project)\n", group, StringComparison.Ordinal);
        Assert.Contains("\nwidth=\"24\"\n", group, StringComparison.Ordinal);
        Assert.Contains("Hands down width=24 height=24, given by itself.", group, StringComparison.Ordinal);
        Assert.Contains("Declares: tint", group, StringComparison.Ordinal);

        Assert.StartsWith("0/0 drawing Filled\n", filled, StringComparison.Ordinal);
        Assert.Contains("\nclass=\"FilledBadge\"\n", filled, StringComparison.Ordinal);
        Assert.Contains("\nwidth=\"24\" from 0\n", filled, StringComparison.Ordinal);
        Assert.Contains("\npadding=\"2\" from 0\n", filled, StringComparison.Ordinal);
        Assert.Contains("\nUnset: scale, x, y, method, parameters\n", filled, StringComparison.Ordinal);
        Assert.Contains("Built at width=24 height=24, given by 0.", filled, StringComparison.Ordinal);
        Assert.Contains("Imported from streamline:abc", filled, StringComparison.Ordinal);
        Assert.Contains("Declares: half", filled, StringComparison.Ordinal);
        Assert.Contains("Inherited from 0: tint", filled, StringComparison.Ordinal);

        // Its own width decides all three, so the group's height is not one it takes.
        Assert.Contains("\nclass=\"Badge\" from 0\n", wide, StringComparison.Ordinal);
        Assert.Contains("\nwidth=\"40\"\n", wide, StringComparison.Ordinal);
        Assert.Contains("\nUnset: height, scale, x, y, method, parameters\n", wide, StringComparison.Ordinal);
        Assert.Contains("Built at width=40, given by itself.", wide, StringComparison.Ordinal);

        // The group's padding of 200% leaves it no room, so it builds to no size at all.
        Assert.DoesNotContain(" px.", wide, StringComparison.Ordinal);

        Assert.Equal("There is no node '3'. get_project lists the paths.", chat.Results[4]);

        var unsized = new ScriptedChat(Call("get_node", new() { ["node"] = "1" }), Say("Read."));
        var scaled = new ScriptedChat(Call("get_node", new() { ["node"] = "1" }), Say("Read."));

        await Send(await Host(Write("icons.svgstudio", Project)), unsized, "and home?");
        await Send(await Host(Write("scaled.svgstudio", Project.Replace("<drawing name=\"home\">", "<drawing name=\"home\" scale=\"2\">", StringComparison.Ordinal))), scaled, "and home now?");

        Assert.Contains("Built at the size its svg gives: neither it nor a node above it gives width, height or scale. That is 24×24 px.", unsized.Results[0], StringComparison.Ordinal);
        Assert.Contains("Built at scale=2, given by itself. That is 48×48 px.", scaled.Results[0], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_Code_Is_Generated_From_What_A_Tab_Holds_And_Says_So_Without_Writing_It_Into_The_Project()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var home = (ProjectDrawing)window.Workspace!.Document.Root.Children[1];

        await window.ShowAsync(home);
        Dispatcher.UIThread.RunJobs();

        var chat = new ScriptedChat(
            Call("get_code", new() { ["node"] = "1" }),
            Call("set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#123456" } }, ["summary"] = "darken" }),
            Call("get_code", new() { ["node"] = "1" }),
            Say("Here."));

        await Send(window, chat, "darken it and show me the code");

        var (before, after) = (chat.Results[0], chat.Results[2]);

        Assert.StartsWith("Drawing 1 drawing home", before, StringComparison.Ordinal);
        Assert.Contains("Demo.Icons.home", before, StringComparison.Ordinal);
        Assert.Contains("public static", before, StringComparison.Ordinal);
        Assert.DoesNotContain("tab", before[..before.IndexOf("\n\n", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Contains("built from the text in its tab", after, StringComparison.Ordinal);
        Assert.NotEqual(before[before.IndexOf("\n\n", StringComparison.Ordinal)..], after[after.IndexOf("\n\n", StringComparison.Ordinal)..]);
        Assert.Contains("#00ff00", home.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Drawing_That_Cannot_Be_Generated_Says_Why_And_A_Group_Is_Not_A_Drawing()
    {
        const string boxed = """
            <studio namespace="Demo.Icons">
              <drawing name="Boxed">
                <svg xmlns="http://www.w3.org/2000/svg" xmlns:e="https://svg.skia/expr/1.0" viewBox="0 0 24 24" width="24" height="24">
                  <rect x="2" y="2" width="20" height="20" e:bounds="Draw" />
                </svg>
              </drawing>
            </studio>
            """;

        var refused = new ScriptedChat(Call("get_code", new() { ["node"] = "0" }), Say("It cannot."));
        var group = new ScriptedChat(Call("get_code", new() { ["node"] = "0" }), Call("get_code", new() { ["node"] = "9" }), Say("Neither."));
        var emptied = new ScriptedChat(Call("get_code", new() { ["node"] = "1" }), Say("It cannot."));
        var icons = await Host(Write("icons.svgstudio", Project));

        await Send(await Host(Write("boxed.svgstudio", boxed)), refused, "show me the code");
        await Send(icons, group, "show me the code");

        // Emptied as a tab is whose drawing would not load: what it holds is no SVG at all.
        await icons.ShowAsync(icons.Workspace!.Document.Root.Children[1]);
        Dispatcher.UIThread.RunJobs();
        Viewer(icons).Close();
        await Send(icons, emptied, "show me the code");

        Assert.StartsWith("Drawing 0 drawing Boxed cannot be generated: ", refused.Results[0], StringComparison.Ordinal);
        Assert.Contains("'Draw'", refused.Results[0], StringComparison.Ordinal);
        Assert.Equal("'0' is a group, not a drawing. get_project lists the paths.", group.Results[0]);
        Assert.Equal("'9' is not a drawing. get_project lists the paths.", group.Results[1]);
        Assert.StartsWith("Drawing 1 drawing home cannot be generated: ", emptied.Results[0], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_Code_Is_Of_The_Drawing_Named_And_Not_Of_The_First_In_The_Project()
    {
        var chat = new ScriptedChat(Call("get_code", new() { ["node"] = "0/0" }), Say("Here."));

        await Send(await Host(Write("icons.svgstudio", Project)), chat, "show me the badge's code");

        var code = chat.Results[0];

        Assert.StartsWith("Drawing 0/0 drawing Filled: ", code, StringComparison.Ordinal);
        Assert.Contains("class Demo.Icons.Filled", code, StringComparison.Ordinal);
        Assert.Contains("tint", code[code.IndexOf("\n\n", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.DoesNotContain("home", code, StringComparison.Ordinal);
    }

    /// <remarks>A namespace starting with a dot continues the one above it, which the header named as it was written.</remarks>
    [AvaloniaFact]
    public async Task The_Code_Names_The_Class_In_The_Namespace_The_Build_Puts_It_In()
    {
        var chat = new ScriptedChat(Call("get_code", new() { ["node"] = "0/0" }), Say("Here."));

        await Send(await Host(Write("icons.svgstudio", Project.Replace("<group name=\"Badges\">", "<group name=\"Badges\" namespace=\".Badges\">", StringComparison.Ordinal))), chat, "show me the badge's code");

        Assert.Contains("class Demo.Icons.Badges.Filled", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("namespace Demo.Icons.Badges", chat.Results[0], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Code_Too_Long_For_One_Answer_Comes_In_Parts_Cut_Between_Lines()
    {
        var squares = string.Concat(Enumerable.Range(0, 576).Select(i => $"<rect x=\"{i % 24}\" y=\"{i / 24}\" width=\"1\" height=\"1\" fill=\"#{i * 29:x6}\" />\n"));
        var window = await Host(Write("many.svg", $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" width=\"24\" height=\"24\">\n{squares}</svg>\n"));
        var viewer = Viewer(window);
        var whole = SvgExport.Generate(viewer.Document!, viewer.Source, "many");

        var chat = new ScriptedChat(Enumerable.Range(1, 12).Select(part => Call("get_code", new() { ["part"] = part })).Append(Say("Read.")).ToArray());

        await Send(window, chat, "show me the code");

        var count = int.Parse(Regex.Match(chat.Results[0], @"part 1 of (\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var parts = chat.Results.Take(count).ToList();

        Assert.InRange(count, 2, 11);
        Assert.Contains("get_code with part 2", chat.Results[0], StringComparison.Ordinal);
        Assert.All(parts, part => Assert.True(part.Length <= 40_000, $"{part.Length} characters"));
        Assert.All(parts.Take(count - 1), part => Assert.EndsWith("\n", part, StringComparison.Ordinal));
        Assert.Equal(whole, string.Concat(parts.Select(part => part[(part.IndexOf("\n\n", StringComparison.Ordinal) + 2)..])));
        Assert.Contains($"{count} parts", chat.Results[count], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Elements_Are_Added_Copied_Moved_And_Removed_Each_As_One_Step_Named_By_The_Key_It_Lands_At()
    {
        var window = await Host(Write("parts.svg", Parts));
        var viewer = Viewer(window);
        var before = viewer.Source;

        var chat = new ScriptedChat(
            Call("insert_element", new() { ["target"] = "1", ["markup"] = "<path d=\"M0 0h4\" />", ["summary"] = "add a stroke" }),
            Call("get_element", new() { ["key"] = "1/1" }),
            Call("duplicate_elements", new() { ["keys"] = new[] { "2" }, ["summary"] = "copy the dot" }),
            Call("get_element", new() { ["key"] = "3" }),
            Call("move_element", new() { ["key"] = "2", ["target"] = "1", ["summary"] = "group the dot" }),
            Call("get_element", new() { ["key"] = "1/2" }),
            Call("remove_elements", new() { ["keys"] = new[] { "2" }, ["summary"] = "drop the copy" }),
            Call("get_drawing", new()),
            Call("undo", new()),
            Call("undo", new()),
            Call("undo", new()),
            Call("undo", new()),
            Say("As it was."));

        await Send(window, chat, "add, copy, move and remove, then take it all back");

        Assert.Contains("It is at 1/1", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("have shifted", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("one undo step", chat.Results[0], StringComparison.Ordinal);
        Assert.StartsWith("<path> at 1/1\nd=M0 0h4", chat.Results[1], StringComparison.Ordinal);

        Assert.Contains("The copy is at 3 (#dot-2)", chat.Results[2], StringComparison.Ordinal);
        Assert.Contains("id=dot-2", chat.Results[3], StringComparison.Ordinal);

        Assert.Contains("It is now at 1/2", chat.Results[4], StringComparison.Ordinal);
        Assert.Contains("id=dot\n", chat.Results[5], StringComparison.Ordinal);

        Assert.Contains("have shifted", chat.Results[6], StringComparison.Ordinal);
        Assert.DoesNotContain("dot-2", chat.Results[7], StringComparison.Ordinal);

        Assert.Equal(
            new[] { "Took back \"Assistant: drop the copy\".", "Took back \"Assistant: group the dot\".", "Took back \"Assistant: copy the dot\".", "Took back \"Assistant: add a stroke\"." },
            chat.Results.Skip(8));
        Assert.Equal(before, viewer.Source);
    }

    [AvaloniaFact]
    public async Task A_Gradient_Takes_Its_Stops_And_Each_Of_Several_Copies_Is_Named()
    {
        var window = await Host(Write("parts.svg", Parts));
        var viewer = Viewer(window);

        var chat = new ScriptedChat(
            Call("insert_element", new() { ["target"] = "0/0", ["markup"] = "<stop offset=\"0\" />", ["summary"] = "start the shade" }),
            Call("insert_element", new() { ["target"] = "0/0/0", ["where"] = "after", ["markup"] = "<stop offset=\"1\" />", ["summary"] = "end the shade" }),
            Call("duplicate_elements", new() { ["keys"] = new[] { "1/0", "2" }, ["summary"] = "copy the box and the dot" }),
            Say("Done."));

        await Send(window, chat, "give the shade two stops, then copy the box and the dot");

        Assert.Contains("It is at 0/0/0;", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("It is at 0/0/1;", chat.Results[1], StringComparison.Ordinal);
        Assert.Contains("The copies are at 1/1 (#box-2), 3 (#dot-2),", chat.Results[2], StringComparison.Ordinal);
        Assert.Contains("<stop offset=\"1\" />", viewer.Source, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task An_Element_Edit_The_Drawing_Cannot_Take_Is_Refused_And_Is_No_Step()
    {
        var window = await Host(Write("parts.svg", Parts));
        var viewer = Viewer(window);
        var before = viewer.Source;

        var chat = new ScriptedChat(
            Call("move_element", new() { ["key"] = "1", ["target"] = "1/0", ["summary"] = "x" }),
            Call("move_element", new() { ["key"] = "1/0", ["target"] = "0", ["summary"] = "x" }),
            Call("remove_elements", new() { ["keys"] = new[] { "(root)" }, ["summary"] = "x" }),
            Call("insert_element", new() { ["target"] = "1", ["markup"] = "<path d=", ["summary"] = "x" }),
            Call("insert_element", new() { ["target"] = "0/0", ["markup"] = "<rect />", ["summary"] = "x" }),
            Call("duplicate_elements", new() { ["keys"] = new[] { "9" }, ["summary"] = "x" }),
            Say("Refused."));

        await Send(window, chat, "break it");

        Assert.Contains("cannot be put inside itself", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("across a <defs>", chat.Results[1], StringComparison.Ordinal);
        Assert.Equal("The drawing itself cannot be deleted.", chat.Results[2]);
        Assert.Contains("not well formed", chat.Results[3], StringComparison.Ordinal);
        Assert.Equal("A <linearGradient> cannot hold a <rect>.", chat.Results[4]);
        Assert.Equal("There is no element at 9 in this drawing.", chat.Results[5]);
        Assert.All(chat.Results, result => Assert.DoesNotContain("undo step", result, StringComparison.Ordinal));
        Assert.Equal(before, viewer.Source);
        Assert.False(viewer.CanUndo);
    }

    [AvaloniaFact]
    public async Task An_Element_Edit_To_A_Drawing_In_No_Tab_Is_One_Step_Of_The_Project()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var filled = (ProjectDrawing)((ProjectGroup)workspace.Document.Root.Children[0]).Children[0];
        var before = filled.Text;

        var chat = new ScriptedChat(
            Call("insert_element", new() { ["node"] = "0/0", ["target"] = "(root)", ["markup"] = "<circle cx=\"12\" cy=\"12\" r=\"2\" />", ["summary"] = "add a dot" }),
            Call("duplicate_elements", new() { ["node"] = "0/0", ["keys"] = new[] { "1" }, ["summary"] = "copy the dot" }),
            Call("get_element", new() { ["node"] = "0/0", ["key"] = "2" }),
            Call("remove_elements", new() { ["node"] = "0/0", ["keys"] = new[] { "" }, ["summary"] = "x" }),
            Call("undo", new()),
            Call("undo", new()),
            Call("redo", new()),
            Say("One dot."));

        await Send(window, chat, "add a dot, copy it, then take both back and put one back");

        Assert.Contains("It is at 1", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("The copy is at 2", chat.Results[1], StringComparison.Ordinal);
        Assert.StartsWith("<circle> at 2\n", chat.Results[2], StringComparison.Ordinal);
        Assert.Equal("The drawing itself cannot be deleted.", chat.Results[3]);
        Assert.Equal("Took back \"Assistant: copy the dot\".", chat.Results[4]);
        Assert.Equal("Took back \"Assistant: add a dot\".", chat.Results[5]);
        Assert.Equal("Put back \"Assistant: add a dot\".", chat.Results[6]);
        Assert.Contains("<circle cx=\"12\" cy=\"12\" r=\"2\" />", filled.Text, StringComparison.Ordinal);
        Assert.Equal("Assistant: add a dot", workspace.UndoLabel);

        Assert.True(workspace.Undo());
        Assert.Equal(before, filled.Text);
    }

    [AvaloniaFact]
    public async Task An_Edit_That_Changes_Nothing_Says_So_And_Is_Not_Undone_For_One_That_Did()
    {
        var window = await Host(Write("declared.svgstudio", Declared));
        var workspace = window.Workspace!;
        var dial = (ProjectDrawing)workspace.Document.Root.Children[1];
        var badges = (ProjectGroup)workspace.Document.Root.Children[0];
        var tools = new AssistantTools(window);

        await window.ShowAsync(dial);
        Dispatcher.UIThread.RunJobs();

        await Send(window, new ScriptedChat(Darken("darken"), Say("Darker.")), "darken the dial", tools: tools);

        // The person removes the badge by hand; the assistant then writes what is there already.
        Assert.True(await window.RemoveAsync(badges.Children[0]));
        Dispatcher.UIThread.RunJobs();

        var chat = new ScriptedChat(
            Call("set_declarations", new() { ["node"] = "0", ["code"] = badges.CodeText, ["summary"] = "keep the tint" }),
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
            => Call("set_attributes", new() { ["node"] = "1", ["key"] = "1", ["attributes"] = new[] { new { name = "fill", value = "#111111" } }, ["summary"] = summary });
    }

    [AvaloniaFact]
    public async Task A_Setting_Set_To_What_It_Says_Already_Is_No_Step_And_The_Undo_Is_The_Renames()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var badges = workspace.Document.Root.Children[0];

        var chat = new ScriptedChat(
            Call("set_node", new() { ["node"] = "0", ["setting"] = "name", ["value"] = "Tags" }),

            // Renamed, it sorts after home.
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

    /// <remarks>Told by the raw text, a width the file writes as 24 asked for as 24.0 was a step that changed nothing, again on every retry.</remarks>
    [AvaloniaFact]
    public async Task A_Setting_Spelled_Otherwise_Than_The_File_Spells_It_Is_No_Step()
    {
        var window = await Host(Write("sized.svgstudio", Sized));
        var workspace = window.Workspace!;

        var chat = new ScriptedChat(
            Call("set_node", new() { ["node"] = "0", ["setting"] = "width", ["value"] = "24.0" }),
            Call("set_node", new() { ["node"] = "0/1", ["setting"] = "width", ["value"] = "4e1" }),
            Say("Unchanged."));

        await Send(window, chat, "set the widths they have");

        Assert.All(chat.Results, result => Assert.StartsWith("Nothing changed", result, StringComparison.Ordinal));
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
        var home = (ProjectDrawing)workspace.Document.Root.Children[1];
        var before = home.Text;

        var chat = new ScriptedChat(
            Call("add_drawing", new() { ["parent"] = "0", ["name"] = "Bad", ["svg"] = "<svg><rect" }),
            Call("add_drawing", new() { ["parent"] = "0", ["name"] = "Div", ["svg"] = "<div/>" }),
            Call("add_drawing", new() { ["parent"] = "0", ["name"] = " ", ["svg"] = Drawing }),
            Call("replace_drawing", new() { ["node"] = "1", ["svg"] = "<div/>", ["summary"] = "swap" }),
            Call("replace_drawing", new() { ["node"] = "1", ["svg"] = before + "\n", ["summary"] = "same" }),
            Call("replace_drawing", new() { ["node"] = "1", ["svg"] = before.Replace("\r\n", "\n", StringComparison.Ordinal), ["summary"] = "same" }),
            Say("Refused."));

        await Send(window, chat, "add drawings the project will not take");

        Assert.Contains("<svg> document", chat.Results[1], StringComparison.Ordinal);
        Assert.Contains("needs a name", chat.Results[2], StringComparison.Ordinal);
        Assert.Contains("<svg> document", chat.Results[3], StringComparison.Ordinal);
        Assert.StartsWith("Nothing changed", chat.Results[4], StringComparison.Ordinal);
        Assert.StartsWith("Nothing changed", chat.Results[5], StringComparison.Ordinal);
        Assert.Single(((ProjectGroup)workspace.Document.Root.Children[0]).Children);
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
            Call("set_attributes", new() { ["node"] = "1", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#123456" } }, ["summary"] = "darken home" }),
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
            Call("set_attributes", new() { ["node"] = "0/0", ["key"] = "0", ["attributes"] = new[] { new { name = "opacity", value = "{{ nothing }}" } }, ["summary"] = "break it" }),
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
        var dial = (ProjectDrawing)window.Workspace!.Document.Root.Children[1];
        var chat = new ScriptedChat(
            Call("set_text", new() { ["node"] = "1", ["key"] = "0/0/0", ["text"] = "90", ["summary"] = "turn it" }),
            Call("set_text", new() { ["node"] = "1", ["key"] = "0", ["text"] = "x", ["summary"] = "defs" }),
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
            Call("get_element", new() { ["node"] = "0/0", ["key"] = "(root)" }),
            Call("get_problems", new() { ["node"] = "1" }),
            Call("get_problems", new() { ["node"] = "0/0" }),
            Say("Read."));

        await Send(window, chat, "what is wrong");

        // The dial is listed with its clash, said at the tag and not the spliced line; the clean drawing is counted.
        Assert.Contains("1 Dial:", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("(at: <e:let name=\"angle\">)", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("1 drawing(s) with no problems", chat.Results[0], StringComparison.Ordinal);
        Assert.StartsWith("<svg> at (root)", chat.Results[1], StringComparison.Ordinal);

        // A drawing's path is taken as well as a group's.
        // One drawing is not counted among drawings.
        Assert.Contains("1 Dial: 'angle' is declared more than once", chat.Results[2], StringComparison.Ordinal);
        Assert.DoesNotContain("drawing(s)", chat.Results[2], StringComparison.Ordinal);
        Assert.Equal("No problems.", chat.Results[3]);
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
        await window.ShowAsync(window.Workspace!.Document.Root.Children[1]);
        Dispatcher.UIThread.RunJobs();
        Viewer(window).SetSource(Viewer(window).Source + "<!-- typed -->");

        var chat = new ScriptedChat(
            Call("add_drawing", new() { ["parent"] = "0", ["name"] = "Bad", ["svg"] = "<svg><rect" }),
            Call("remove", new() { ["node"] = "1" }),
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

    /// <remarks>It writes past the history, as a save does, so in the panel Studio asks first.</remarks>
    [AvaloniaFact]
    public async Task The_Panel_Asks_Before_An_Export_And_Writes_Nothing_On_No()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var target = Path.Combine(_directory, "Icons.cs");
        var asked = new List<string>();
        var chat = new ScriptedChat(Call("export", new() { ["path"] = target }), Say("Not exported."));

        await Send(window, chat, "export it", asked: asked);

        Assert.Equal($"Export to {target}?", Assert.Single(asked));
        Assert.Equal("The person said no; nothing was written.", chat.Results[0]);
        Assert.False(File.Exists(target));
    }

    [AvaloniaFact]
    public async Task The_Panel_Asks_Before_A_Save_To_A_Path_And_Writes_Nothing_On_No()
    {
        var window = await Host(Write("star.svg", Drawing));
        var target = Path.Combine(_directory, "copy.svg");
        var asked = new List<string>();
        var chat = new ScriptedChat(Call("save", new() { ["path"] = target }), Say("Not saved."));

        await Send(window, chat, "save it there", asked: asked);

        Assert.Equal($"Save it as {target}?", Assert.Single(asked));
        Assert.Equal("The person said no; nothing was saved.", chat.Results[0]);
        Assert.False(File.Exists(target));
    }

    /// <remarks>
    /// The panel asks without holding the window, so the person can bring another tab forward before
    /// answering; the save then wrote that tab's text and moved it to the path, and the export wrote it.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Tab_Brought_Forward_While_The_Person_Is_Asked_Is_Not_Saved_Or_Exported_In_Place_Of_The_One_Asked_About()
    {
        var dot = Write("dot.svg", Drawing.Replace("#00ff00", "#0000ff", StringComparison.Ordinal));
        var window = await Host(Write("star.svg", Drawing));
        var copy = Path.Combine(_directory, "copy.svg");
        var exported = Path.Combine(_directory, "exported.svg");

        await window.OpenAsync(new[] { dot });
        Dispatcher.UIThread.RunJobs();

        var tabs = window.FindControl<TabControl>("Tabs")!;
        var items = tabs.Items.OfType<TabItem>().ToList();
        var tools = new AssistantTools(window)
        {
            Confirm = _ =>
            {
                tabs.SelectedItem = items[1];
                return Task.FromResult(true);
            }
        };

        // Asked with the star in front, and answered with the dot.
        async Task<string?> Asked(string tool, string path)
        {
            tabs.SelectedItem = items[0];
            Dispatcher.UIThread.RunJobs();

            var result = await tools.For(small: false, docs: false).OfType<AIFunction>().Single(function => function.Name == tool)
                .InvokeAsync(new AIFunctionArguments { ["path"] = path });

            Dispatcher.UIThread.RunJobs();

            return result?.ToString();
        }

        Assert.Equal("Nothing was saved: what the person was asked about changed while they answered; call it again to go on from things as they are now.", await Asked("save", copy));
        Assert.Equal("Nothing was written: what the person was asked about changed while they answered; call it again to go on from things as they are now.", await Asked("export", exported));
        Assert.False(File.Exists(copy));
        Assert.False(File.Exists(exported));
        Assert.Equal(dot, Assert.IsType<SvgViewer>(items[1].Content).DocumentPath);
    }

    /// <remarks>
    /// Saved meanwhile with ⌘S, the project is written where it is from then on, and a path no longer
    /// applies: the save answered "Saved as" the path it was given, where nothing was written.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Project_Saved_While_The_Person_Is_Asked_Is_Not_Said_To_Be_Saved_At_The_Path_Given()
    {
        var window = await Host(Write("star.svg", Drawing));
        var given = Path.Combine(_directory, "Icons.svgstudio");
        var chosen = Path.Combine(_directory, "Mine.svgstudio");

        await window.NewProjectAsync();
        Dispatcher.UIThread.RunJobs();

        window.AskWhereToSave = _ => Task.FromResult<string?>(chosen);

        var tools = new AssistantTools(window)
        {
            Confirm = async _ => await window.SaveAsync()
        };
        var result = await tools.For(small: false, docs: false).OfType<AIFunction>().Single(function => function.Name == "save")
            .InvokeAsync(new AIFunctionArguments { ["path"] = given });

        Assert.Equal("Nothing was saved: what the person was asked about changed while they answered; call it again to go on from things as they are now.", result?.ToString());
        Assert.False(File.Exists(given));
        Assert.Equal(chosen, window.Workspace!.Document.Path);
    }

    [AvaloniaFact]
    public async Task A_Failed_Save_Is_Said_Back_And_Opens_Nothing()
    {
        var gone = Directory.CreateDirectory(Path.Combine(_directory, "gone")).FullName;
        var path = Path.Combine(gone, "icons.svgstudio");

        TestPackage.Write(path, Project);

        var window = await Host(path);
        var announced = Unanswered(window);

        await window.ShowAsync(window.Workspace!.Document.Root.Children[1]);
        Dispatcher.UIThread.RunJobs();
        Viewer(window).SetSource("<div/>");

        var refused = new ScriptedChat(Call("save", new()), Say("Not saved."));

        await Send(window, refused, "save", allow: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("has to be an <svg>", refused.Results[0], StringComparison.Ordinal);
        Assert.Equal(TestPackage.Normal(Project), TestPackage.Read(path));
        Assert.False(window.Workspace!.CanUndo);
        Assert.False(window.Workspace.IsEdited);

        Viewer(window).SetSource(Drawing.Replace("#00ff00", "#123456", StringComparison.Ordinal));
        // A file where the project's folder was, which no save can write into.
        Directory.Delete(gone, recursive: true);
        File.WriteAllText(gone, string.Empty);

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

        await window.ShowAsync(root.Children[1]);
        Dispatcher.UIThread.RunJobs();
        Assert.True(((GroupPanel)Viewer(window).SidePanels.Single().Content).Edit("width", "40"));

        // And one typed into the tab of a group inside the group being removed.
        var round = ((ProjectGroup)root.Children[0]).Children[1];

        await window.ShowAsync(round);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.GetVisualDescendants().OfType<GroupPanel>().Single(panel => ReferenceEquals(panel.Node, round)).Edit("width", "5"));

        // And one typed for Filled on Badges' board: with Filled gone the board falls back to Badges, and its next save writes the width there.
        var badges = (ProjectGroup)root.Children[0];

        Assert.True((await Pick(window, badges, badges.Children[0])).Edit("width", "100"));

        var chat = new ScriptedChat(
            Call("remove", new() { ["node"] = "1" }),
            Call("remove", new() { ["node"] = "0" }),
            Call("remove", new() { ["node"] = "0/0" }),
            Say("Kept."));

        await Send(window, chat, "remove home, Badges and Filled", allow: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, asked);
        Assert.Empty(announced);
        Assert.StartsWith("1 home has edits in its tab", chat.Results[0], StringComparison.Ordinal);
        Assert.StartsWith("0/1 Round has edits in its tab", chat.Results[1], StringComparison.Ordinal);
        Assert.StartsWith("0 Badges has edits in its tab", chat.Results[2], StringComparison.Ordinal);
        Assert.Equal(2, root.Children.Count);
        Assert.Equal(2, ((ProjectGroup)root.Children[0]).Children.Count);
    }

    /// <remarks>The question stands in for the yes or no a remove asks for, as throwing the edits away is to remove the node.</remarks>
    [AvaloniaFact]
    public async Task A_Remove_Over_A_Tab_Holding_Edits_Asks_Which_And_Goes_On_As_Answered_Without_Asking_Twice()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var root = window.Workspace!.Document.Root;
        var home = root.Children[1];
        var discards = 0;
        var questions = new List<AssistantTools.Question>();
        var confirmed = new List<string>();

        window.ConfirmDiscard = _ =>
        {
            discards++;
            return Task.FromResult(true);
        };

        await window.ShowAsync(home);
        Dispatcher.UIThread.RunJobs();
        Viewer(window).SetSource(Viewer(window).Source + "<!-- typed -->");

        Func<AssistantTools.Question, Task<AssistantTools.Answer?>> Choosing(int choice) => question =>
        {
            questions.Add(question);
            return Task.FromResult<AssistantTools.Answer?>(new AssistantTools.Answer(question.Choices[choice]));
        };

        var kept = new ScriptedChat(Call("remove", new() { ["node"] = "1" }), Say("Kept."));
        var removed = new ScriptedChat(Call("remove", new() { ["node"] = "1" }), Say("Removed."));

        await Send(window, kept, "remove home", allow: true, asked: confirmed, ask: Choosing(1));

        Assert.Equal("Nothing was removed: the person kept the edits held in the tab of 1 home.", kept.Results[0]);
        Assert.Same(home, root.Children[1]);

        await Send(window, removed, "remove home", allow: true, asked: confirmed, ask: Choosing(0));

        Assert.Equal("Removed home, throwing away the edits held in the tab of 1 home. It is one undo step.", removed.Results[0]);
        Assert.DoesNotContain(home, root.Children);
        Assert.Equal(2, questions.Count);
        Assert.Equal(questions[0].Key, questions[1].Key);
        Assert.StartsWith("Removing home closes the tab of 1 home", questions[0].Text, StringComparison.Ordinal);
        Assert.Empty(confirmed);
        Assert.Equal(0, discards);
    }

    /// <remarks>The panel asks without holding the window, so an answer is to the question as it was put, and a tab taking edits meanwhile was asked about by nobody.</remarks>
    [AvaloniaFact]
    public async Task A_Remove_Answered_After_Another_Tab_Under_It_Took_Edits_Does_Nothing_And_Says_What_Changed()
    {
        var window = await Host(Write("pair.svgstudio", $"""<studio namespace="Demo.Icons"><group name="Badges"><drawing name="A">{Drawing}</drawing><drawing name="B">{Drawing}</drawing></group></studio>"""));
        var badges = (ProjectGroup)window.Workspace!.Document.Root.Children[0];
        var chat = new ScriptedChat(Call("remove", new() { ["node"] = "0" }), Say("Refused."));
        var discards = 0;

        window.ConfirmDiscard = _ =>
        {
            discards++;
            return Task.FromResult(false);
        };

        await window.ShowAsync(badges.Children[0]);
        Dispatcher.UIThread.RunJobs();
        Viewer(window).SetSource(Viewer(window).Source + "<!-- typed -->");

        await Send(window, chat, "remove Badges", ask: async question =>
        {
            await window.ShowAsync(badges.Children[1]);
            Viewer(window).SetSource(Viewer(window).Source + "<!-- typed -->");

            return new AssistantTools.Answer(question.Choices[0]);
        }).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("Nothing was removed: what the person was asked about changed while they answered; call it again to go on from things as they are now.", chat.Results[0]);
        Assert.Equal(0, discards);
        Assert.Same(badges, Assert.Single(window.Workspace.Document.Root.Children));
        Assert.Equal(2, window.GetVisualDescendants().OfType<TabControl>().Single().Items.OfType<TabItem>().Count(item => item.Content is SvgViewer { IsSourceModified: true }));
    }

    /// <remarks>
    /// Saved while the person answers, there is nothing left to throw away, and so nothing the question
    /// was about: an answer to keep the work, taken for no answer, opened a new project over it.
    /// </remarks>
    [AvaloniaFact]
    public async Task An_Answer_Given_After_What_It_Asked_About_Was_Saved_Does_Nothing_And_Says_So()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var workspace = window.Workspace!;
        var home = workspace.Document.Root.Children[1];
        var chat = new ScriptedChat(
            Call("remove", new() { ["node"] = "1" }),
            Call("set_node", new() { ["node"] = "1", ["setting"] = "class", ["value"] = "Home" }),
            Call("new_project", new()),
            Say("Kept."));
        var questions = new List<AssistantTools.Question>();

        await window.ShowAsync(home);
        Dispatcher.UIThread.RunJobs();
        Viewer(window).SetSource(Viewer(window).Source + "<!-- typed -->");

        await Send(window, chat, "remove home, then start again", allow: true, ask: async question =>
        {
            questions.Add(question);
            await window.SaveAsync();

            return new AssistantTools.Answer(question.Choices[1]);
        }).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(
            new[]
            {
                "Nothing was removed: what the person was asked about changed while they answered; call it again to go on from things as they are now.",
                "No new project was opened: what the person was asked about changed while they answered; call it again to go on from things as they are now."
            },
            new[] { chat.Results[0], chat.Results[2] });
        Assert.Equal(new[] { "Keep the edits; remove nothing", "Keep them; do nothing" }, questions.Select(question => question.Choices[1]));
        Assert.Same(workspace, window.Workspace);
        Assert.Same(home, workspace.Document.Root.Children[1]);
    }

    /// <remarks>Every save, export, commit and plain remove in the panel asks through this card, as the tools' Confirm is the panel's question of two answers.</remarks>
    [AvaloniaFact]
    public async Task A_Remove_In_The_Panel_Asks_Allow_Or_Deny_And_Goes_On_Only_When_Allowed()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var root = window.Workspace!.Document.Root;
        var home = root.Children[1];
        var chat = new ScriptedChat(
            Call("remove", new() { ["node"] = "1" }),
            Say("Denied."),
            Call("remove", new() { ["node"] = "1" }),
            Say("Removed."));
        var providers = AssistantProviders.All;

        try
        {
            AssistantProviders.All = new IAssistantProvider[] { new ScriptedProvider(chat) };

            var panel = new AssistantPanel(new AssistantTools(window));
            var host = new Window { Content = panel };

            host.Show();
            Dispatcher.UIThread.RunJobs();

            foreach (var (choice, round) in new[] { ("Deny", 0), ("Allow", 1) })
            {
                var sent = panel.SendAsync("remove home");

                await Gestures.Until(() => panel.Transcript.Count(row => row.Role == "ask") > round);

                var buttons = panel.GetVisualDescendants().OfType<Button>().Where(button => button.IsEnabled && button.Content is not ("Send" or "Stop" or "New")).ToList();

                Assert.Equal(new[] { "Allow", "Deny" }, buttons.Select(button => button.Content));
                Assert.Same(home, root.Children[1]);

                buttons.Single(button => Equals(button.Content, choice)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await sent.WaitAsync(TimeSpan.FromSeconds(30));
            }

            Assert.Equal("The person said no; nothing was removed.", chat.Results[0]);
            Assert.StartsWith("Removed home.", chat.Results[1], StringComparison.Ordinal);
            Assert.DoesNotContain(home, root.Children);
            Assert.All(panel.Transcript.Where(row => row.Role == "ask"), row => Assert.Equal("Remove home from the project?", row.Text));
        }
        finally
        {
            AssistantProviders.All = providers;
        }
    }

    [AvaloniaFact]
    public async Task The_Panel_Puts_A_Question_As_A_Button_For_Each_Answer_And_Stop_Answers_None()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var root = window.Workspace!.Document.Root;
        var badges = (ProjectGroup)root.Children[0];
        var chat = new ScriptedChat(
            Call("remove", new() { ["node"] = "1" }),
            Say("Removed."),
            Call("remove", new() { ["node"] = "0" }),
            Say("Kept."));
        var providers = AssistantProviders.All;

        try
        {
            AssistantProviders.All = new IAssistantProvider[] { new ScriptedProvider(chat) };

            var panel = new AssistantPanel(new AssistantTools(window));
            var host = new Window { Content = panel };

            host.Show();
            Dispatcher.UIThread.RunJobs();

            async Task<(Task Sent, List<Button> Choices)> Asked(string text, int round)
            {
                await window.ShowAsync(round == 0 ? root.Children[1] : badges.Children[0]);
                Dispatcher.UIThread.RunJobs();
                Viewer(window).SetSource(Viewer(window).Source + "<!-- typed -->");

                var sent = panel.SendAsync(text);

                await Gestures.Until(() => panel.Transcript.Count(row => row.Role == "ask") > round);

                return (sent, panel.GetVisualDescendants().OfType<Button>().Where(button => button.IsEnabled && button.Content is not ("Send" or "Stop" or "New")).ToList());
            }

            var (removing, first) = await Asked("remove home", 0);

            Assert.Equal(new[] { "Throw the edits away and remove it", "Keep the edits; remove nothing" }, first.Select(button => button.Content));

            first[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await removing.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Same(badges, Assert.Single(root.Children));
            Assert.All(first, button => Assert.False(button.IsEnabled));

            var (stopping, second) = await Asked("remove Badges", 1);

            Assert.Equal(2, second.Count);

            // The reply's own button, which reads Stop while it runs.
            panel.GetVisualDescendants().OfType<Button>().Single(button => button.Content is "Stop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await stopping.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Same(badges, Assert.Single(root.Children));
            Assert.All(second, button => Assert.False(button.IsEnabled));
            Assert.Equal(2, panel.Transcript.Count(row => row.Role == "ask"));
        }
        finally
        {
            AssistantProviders.All = providers;
        }
    }

    [AvaloniaFact]
    public async Task A_Setting_Is_Not_Written_Under_One_Typed_For_The_Same_Node()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var root = window.Workspace!.Document.Root;
        var badges = (ProjectGroup)root.Children[0];
        var filled = badges.Children[0];

        // A width typed for Filled on Badges' board, where Filled has no tab of its own.
        Assert.True((await Pick(window, badges, filled)).Edit("width", "100"));

        // And one typed into a drawing's own Project pane, whose source edits are no reason to refuse.
        await window.ShowAsync(root.Children[1]);
        Dispatcher.UIThread.RunJobs();
        Viewer(window).SetSource(Viewer(window).Source + "<!-- typed -->");

        var chat = new ScriptedChat(
            Call("set_node", new() { ["node"] = "0/0", ["setting"] = "width", ["value"] = "200" }),
            Call("set_node", new() { ["node"] = "0", ["setting"] = "width", ["value"] = "200" }),
            Call("set_node", new() { ["node"] = "1", ["setting"] = "class", ["value"] = "Home" }),
            Call("get_node", new() { ["node"] = "0/0" }),
            Say("Done."));

        await Send(window, chat, "widen them", allow: true);

        Assert.StartsWith("0/0 Filled has settings typed in the tab of 0 Badges", chat.Results[0], StringComparison.Ordinal);

        // Read as the project has it, which says the typed width is not in it.
        Assert.Contains("width=\"200\" from 0\n", chat.Results[3], StringComparison.Ordinal);
        Assert.Contains("Settings typed in the tab of 0 Badges are not in the project yet", chat.Results[3], StringComparison.Ordinal);
        Assert.Null(filled.Width);
        Assert.Equal(200f, badges.Width);
        Assert.Equal("Home", root.Children[1].Class);

        Assert.True(((GroupPanel)Viewer(window).SidePanels.Single().Content).Edit("width", "40"));

        var typed = new ScriptedChat(Call("set_node", new() { ["node"] = "1", ["setting"] = "width", ["value"] = "50" }), Say("Refused."));

        await Send(window, typed, "widen home", allow: true);

        Assert.StartsWith("1 home has settings typed in the tab of 1 home", typed.Results[0], StringComparison.Ordinal);
        Assert.Null(root.Children[1].Width);
    }

    [AvaloniaFact]
    public async Task A_Save_Names_The_Tabs_Whose_Edits_It_Left_Behind()
    {
        var path = Write("icons.svgstudio", Project);
        var window = await Host(path);
        var announced = Unanswered(window);
        var root = window.Workspace!.Document.Root;

        // Both drawings open, Filled in front, so home's edits sit in a tab a save does not hand over.
        await window.ShowAsync(root.Children[1]);
        await window.ShowAsync(((ProjectGroup)root.Children[0]).Children[0]);
        Dispatcher.UIThread.RunJobs();

        var chat = new ScriptedChat(
            Call("set_attributes", new() { ["node"] = "1", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#123456" } }, ["summary"] = "darken" }),
            Call("set_attributes", new() { ["node"] = "0/0", ["key"] = "0", ["attributes"] = new[] { new { name = "stroke", value = "black" } }, ["summary"] = "outline" }),
            Call("get_project", new()),
            Call("get_drawing", new() { ["node"] = "1" }),
            Call("get_drawing", new() { ["node"] = "0/0" }),
            Call("save", new()),
            Say("Saved most of it."));

        await Send(window, chat, "darken, outline and save", allow: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("1 drawing home (open, holding unsaved edits)", chat.Results[2], StringComparison.Ordinal);
        Assert.Contains("0/0 drawing Filled (open, holding unsaved edits)", chat.Results[2], StringComparison.Ordinal);
        Assert.DoesNotContain("that saving writes", chat.Results[3], StringComparison.Ordinal);
        Assert.Contains("open it and save", chat.Results[3], StringComparison.Ordinal);
        Assert.Contains("that saving writes into the project", chat.Results[4], StringComparison.Ordinal);
        Assert.StartsWith("Saved, except 1 home", chat.Results[5], StringComparison.Ordinal);
        Assert.DoesNotContain("#123456", TestPackage.Read(path), StringComparison.Ordinal);
        Assert.Contains("stroke=\"black\"", TestPackage.Read(path), StringComparison.Ordinal);
        Assert.Contains("Tabs holding edits not yet in the project: 1 home", new AssistantTools(window).Context(), StringComparison.Ordinal);

        // Each open and save writes one more tab's, so it ends.
        var rest = new ScriptedChat(Call("open", new() { ["node"] = "1" }), Call("save", new()), Say("Saved."));

        await Send(window, rest, "save home", allow: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("Saved.", rest.Results[1]);
        Assert.Contains("#123456", TestPackage.Read(path), StringComparison.Ordinal);
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

        await window.ShowAsync(window.Workspace!.Document.Root.Children[1]);
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
        Assert.Contains("namespace=\"Demo.Other\"", TestPackage.Read(path), StringComparison.Ordinal);
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
        TestPackage.Write(path, Project);
        ProjectGitTests.Git(work, "add", "icons.svgstudio");
        ProjectGitTests.Git(work, "commit", "-m", "Add the icons");

        var window = await Host(path);
        var root = window.Workspace!.Document.Root;

        await window.ShowAsync(root.Children[1]);
        await window.ShowAsync(root);
        Dispatcher.UIThread.RunJobs();

        var asked = new List<string>();
        var chat = new ScriptedChat(
            Call("set_attributes", new() { ["node"] = "1", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#123456" } }, ["summary"] = "darken" }),
            Call("git_commit", new() { ["message"] = "Darken" }),
            Say("Not committed."));

        await Send(window, chat, "darken and commit", allow: true, asked: asked).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.StartsWith("Nothing was committed: the tab of 1 home holds edits", chat.Results[1], StringComparison.Ordinal);
        Assert.Empty(asked);
        Assert.Equal("1", ProjectGitTests.Git(work, "rev-list", "--count", "HEAD").Trim());
    }

    // Headless only for the queue headless tests run on one at a time: the key it swaps is the one
    // AnthropicKeyTests reads, and as a plain fact it ran beside it.
    [AvaloniaFact]
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

    /// <remarks>
    /// Studio's own dialog, not a stub: the window knows it is asking by the dialog over it. Let
    /// through, the edit went in under a question that never named it, and a tab opened under it
    /// was left over a project the answer closed.
    /// </remarks>
    [AvaloniaFact]
    public async Task Nothing_Is_Edited_While_The_Window_Asks_The_Person_A_Question()
    {
        var window = await Host(Write("icons.svgstudio", Project));
        var filled = (ProjectDrawing)((ProjectGroup)window.Workspace!.Document.Root.Children[0]).Children[0];
        var before = filled.Text;

        await window.ShowAsync(window.Workspace.Document.Root.Children[1]);
        Dispatcher.UIThread.RunJobs();
        Viewer(window).SetSource(Drawing.Replace("#00ff00", "#123456", StringComparison.Ordinal));

        var closing = window.CloseProjectAsync();
        Dispatcher.UIThread.RunJobs();

        var question = Assert.Single(window.OwnedWindows);
        var tabs = window.GetVisualDescendants().OfType<TabItem>().Count();
        ChatResponse Blue() => Call("set_attributes", new() { ["node"] = "0/0", ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "#0000ff" } }, ["summary"] = "blue" });
        var chat = new ScriptedChat(Blue(), Call("open", new() { ["node"] = "0" }), Call("get_project", new()), Say("Waiting."));

        await Send(window, chat, "make the badge blue");

        Assert.All(chat.Results.Take(2), result => Assert.StartsWith("Nothing was done: Studio is waiting on the person to answer \"Unsaved changes\"", result, StringComparison.Ordinal));
        Assert.Contains("0/0 drawing Filled", chat.Results[2], StringComparison.Ordinal);
        Assert.Equal(before, filled.Text);
        Assert.Equal(tabs, window.GetVisualDescendants().OfType<TabItem>().Count());

        question.Close();
        Assert.False(await closing);

        var answered = new ScriptedChat(Blue(), Say("Blue."));

        await Send(window, answered, "now make it blue");

        Assert.Contains("one undo step", answered.Results[0], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Nothing_Is_Put_Beside_The_Drawing_Itself_Or_Inside_A_Shape()
    {
        var window = await Host(Write("parts.svg", Parts));
        var viewer = Viewer(window);
        var before = viewer.Source;

        var chat = new ScriptedChat(
            Call("insert_element", new() { ["target"] = "", ["where"] = "Before", ["markup"] = "<circle />", ["summary"] = "x" }),
            Call("move_element", new() { ["key"] = "2", ["target"] = "(root)", ["where"] = "After", ["summary"] = "x" }),
            Call("insert_element", new() { ["target"] = "1/0", ["markup"] = "<circle />", ["summary"] = "x" }),
            Call("move_element", new() { ["key"] = "2", ["target"] = "1/0", ["summary"] = "x" }),
            Say("Refused."));

        await Send(window, chat, "put a circle where none can go");

        Assert.Equal("Nothing can go beside the drawing itself, which has no siblings; put it inside instead.", chat.Results[0]);
        Assert.Equal(chat.Results[0], chat.Results[1]);
        Assert.Equal("A <rect> cannot hold a <circle>.", chat.Results[2]);
        Assert.Equal(chat.Results[2], chat.Results[3]);
        Assert.Equal(before, viewer.Source);
        Assert.False(viewer.CanUndo);

        // What describes or animates a shape is still what it holds.
        var animated = new ScriptedChat(
            Call("insert_element", new() { ["target"] = "1/0", ["markup"] = "<animateTransform attributeName=\"transform\" type=\"rotate\" to=\"90\" dur=\"1s\" />", ["summary"] = "turn the box" }),
            Say("Turning."));

        await Send(window, animated, "turn the box");

        Assert.Contains("It is at 1/0/0;", animated.Results[0], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task An_Argument_Null_Or_Of_The_Wrong_Kind_Is_Refused_By_Its_Name()
    {
        var window = await Host(Write("parts.svg", Parts));
        var viewer = Viewer(window);
        var before = viewer.Source;

        var chat = new ScriptedChat(
            Call("set_attributes", new() { ["key"] = "1/0", ["attributes"] = new[] { new { name = "fill", value = "red" } }, ["summary"] = null }),
            Call("set_text", new() { ["key"] = "1/0", ["text"] = 5, ["summary"] = "five" }),
            Call("insert_element", new() { ["target"] = "1", ["markup"] = "<circle />", ["where"] = "Under", ["summary"] = "x" }),

            // Between, since the fourth failure in a row is thrown out of the reply rather than answered;
            // and a null where a parameter takes one is no refusal.
            Call("get_element", new() { ["key"] = "1", ["node"] = null }),
            Call("get_code", new() { ["part"] = "first" }),
            Say("Refused."));

        await Send(window, chat, "try it every wrong way");

        Assert.Contains("'summary' cannot be null: it takes a string.", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("'text' takes a string, not 5.", chat.Results[1], StringComparison.Ordinal);
        Assert.Contains("'where' takes one of Before, After, Inside, not \"Under\".", chat.Results[2], StringComparison.Ordinal);
        Assert.StartsWith("<g> at 1", chat.Results[3], StringComparison.Ordinal);
        Assert.Contains("'part' takes a whole number, not \"first\".", chat.Results[4], StringComparison.Ordinal);
        Assert.All(chat.Results, result => Assert.DoesNotContain("Object reference", result, StringComparison.Ordinal));
        Assert.All(chat.Results, result => Assert.DoesNotContain("System.", result, StringComparison.Ordinal));
        Assert.All(chat.Results, result => Assert.DoesNotContain("Svg.SourceEditing", result, StringComparison.Ordinal));
        Assert.Equal(before, viewer.Source);
    }

    /// <remarks>
    /// The tab read Keys on its own and found tint undeclared, which the group declares: three
    /// errors in its status line, where the tools, reading it as built, said two.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_Tab_Counts_The_Errors_The_Tools_Do_In_A_Drawing_Using_What_Its_Group_Declares()
    {
        var keys = await Host(Write("keys.svgstudio", Keyed));

        await keys.ShowAsync(((ProjectGroup)keys.Workspace!.Document.Root.Children[0]).Children[0]);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, Viewer(keys).SourceDiagnostics.Count);
        Assert.DoesNotContain(Viewer(keys).SourceDiagnostics, diagnostic => diagnostic.Message.Contains("tint", StringComparison.Ordinal));
        Assert.Contains("The drawing has 2 error(s)", new AssistantTools(keys).Context(), StringComparison.Ordinal);

        var tinted = await Host(Write("tinted.svgstudio", Keyed));

        await tinted.ShowAsync(((ProjectGroup)tinted.Workspace!.Document.Root.Children[0]).Children[1]);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(Viewer(tinted).SourceDiagnostics);
        Assert.DoesNotContain("error", new AssistantTools(tinted).Context(), StringComparison.Ordinal);
    }

    /// <remarks>Broken from the live run, whose code draws nothing and said nothing of why.</remarks>
    [AvaloniaFact]
    public async Task The_Code_Of_A_Drawing_With_Problems_Says_What_They_Are()
    {
        const string broken = """
            <studio namespace="Demo.Icons">
              <drawing name="Broken">
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
                  <circle cx="12" cy="12" r="{{ radius * 2 }}" fill="#0000ff" />
                </svg>
              </drawing>
            </studio>
            """;
        var chat = new ScriptedChat(Call("get_code", new() { ["node"] = "0" }), Say("Here."));
        var clean = new ScriptedChat(Call("get_code", new() { ["node"] = "1" }), Say("Here."));

        await Send(await Host(Write("broken.svgstudio", broken)), chat, "show me the code");
        await Send(await Host(Write("icons.svgstudio", Project)), clean, "show me the code");

        var header = chat.Results[0][..chat.Results[0].IndexOf("\n\n", StringComparison.Ordinal)];

        Assert.Contains("public static", chat.Results[0], StringComparison.Ordinal);
        Assert.Contains("\nProblems:\n- 'r' does not take an expression.", header, StringComparison.Ordinal);
        Assert.Contains("\n- Unknown name 'radius'", header, StringComparison.Ordinal);
        Assert.DoesNotContain("Problems", clean.Results[0][..clean.Results[0].IndexOf("\n\n", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_Tab_Holding_No_Svg_Says_So_Rather_Than_Naming_A_Row()
    {
        var window = await Host(Write("home.svg", Drawing));

        // Emptied, as a tab is whose drawing would not load.
        Viewer(window).Close();

        var chat = new ScriptedChat(
            Call("replace_drawing", new() { ["svg"] = Drawing, ["summary"] = "fill it" }),
            Call("set_attributes", new() { ["key"] = "0", ["attributes"] = new[] { new { name = "fill", value = "red" } }, ["summary"] = "red" }),
            Say("Refused."));

        await Send(window, chat, "replace it");

        Assert.All(chat.Results, result => Assert.Equal("What is open here does not read as SVG, so it cannot be edited.", result));
    }

    [AvaloniaFact]
    public async Task A_Number_Refused_Is_Written_The_Same_Way_Whatever_The_Culture()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;

        // A comma for the point, and ∞ for infinity.
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

        try
        {
            var window = await Host(Write("icons.svgstudio", Project));
            var chat = new ScriptedChat(
                Call("set_node", new() { ["node"] = "1", ["setting"] = "width", ["value"] = "Infinity" }),
                Call("set_node", new() { ["node"] = "1", ["setting"] = "x", ["value"] = "1e999" }),
                Call("set_node", new() { ["node"] = "1", ["setting"] = "scale", ["value"] = "-1.5" }),
                Call("set_node", new() { ["node"] = "1", ["setting"] = "padding", ["value"] = "-0.5" }),
                Say("Refused."));

            await Send(window, chat, "size Badges as nothing can be");

            Assert.Equal("A width has to be a positive number, but was Infinity.", chat.Results[0]);
            Assert.Equal("A position has to be a finite number, but was Infinity.", chat.Results[1]);
            Assert.Equal("A scale has to be a positive number, but was -1.5.", chat.Results[2]);
            Assert.StartsWith("A top padding has to be zero or more, but was -0.5.", chat.Results[3], StringComparison.Ordinal);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
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

        if (name.EndsWith(".svgstudio", StringComparison.Ordinal))
        {
            return TestPackage.Write(path, text);
        }

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
    /// <param name="ask">How the person answers a question with choices; left out, nobody can be asked one.</param>
    private static async Task<string> Send(
        MainWindow window,
        ScriptedChat chat,
        string text,
        bool allow = false,
        List<string>? asked = null,
        bool small = false,
        AssistantTools? tools = null,
        Func<AssistantTools.Question, Task<AssistantTools.Answer?>>? ask = null)
    {
        tools ??= new AssistantTools(window);
        tools.Confirm = question =>
        {
            asked?.Add(question);
            return Task.FromResult(allow);
        };
        tools.Ask = ask ?? (_ => Task.FromResult<AssistantTools.Answer?>(null));
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

    /// <summary>A get_history call where <paramref name="read"/>, to make ahead of the calls a test is about.</summary>
    private static ChatResponse[] History(bool read) => read ? [Call("get_history", new())] : [];

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
