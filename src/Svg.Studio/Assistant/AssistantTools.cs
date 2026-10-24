// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Threading;
using Microsoft.Extensions.AI;
using SkiaSharp;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions;
using Svg.Expressions.Recipes;
using Svg.Highlighting;
using Svg.Skia;
using Svg.SourceEditing;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>One attribute to write; an empty or missing value takes the attribute off.</summary>
public sealed record AttributeChange(
    [property: Description("The attribute's name as written in the file, such as fill or transform.")] string Name,
    [property: Description("The new value, or empty to remove the attribute.")] string? Value);

/// <summary>What the assistant can see and do in the window, as functions a model can call.</summary>
/// <remarks>
/// Each one goes through the method the window's own menu or pane goes through, so an edit is one
/// step on the history the person already undoes with, labelled so the Edit menu says whose it was.
/// A refusal comes back as the editor's own sentence rather than as an exception, because the model
/// can do something with a sentence.
///
/// Nodes are named by child index from the project (<c>0/2</c>), elements by the drawing file's own
/// address key: names repeat, and an index is what every editor here already takes.
/// </remarks>
public sealed class AssistantTools
{
    private const string Undoes = " It is one undo step.";

    private const string Shifted = " shifted, so read get_drawing again before naming another element.";

    private const string Warning = "Warning: ";

    private const string Gone = "(no longer in the project)";

    /// <summary>About how long an answer grows before it leaves things out, well under the 100,000 characters Claude Code cuts a tool's answer at: a source, a value, rows of a list, or the code past a part.</summary>
    private const int Longest = 40_000;

    /// <summary>How many problems an answer lists before it says how many more.</summary>
    private const int Most = 50;

    /// <summary>How far apart the images on a render's or a search's sheet are, and from the sheet's edges.</summary>
    private const int Gap = 8;

    private static readonly string[] s_allow = { "Allow", "Deny" };

    private static readonly string[] s_throw = { "Throw the edits away and remove it", "Keep the edits; remove nothing" };

    private static readonly string[] s_discard = { "Throw them away and go on", "Keep them; do nothing" };

    /// <summary>How an export lays out the text it cannot write out: baked or relaxed, this once or from now on, as the window's checkboxes ask it.</summary>
    private static readonly string[] s_layouts = { "Baked, for this export", "Relaxed, for this export", "Baked, from now on", "Relaxed, from now on" };

    /// <summary>Refuses an argument no parameter takes: dropped, a misspelled node fell back to the drawing in front, which took the edit.</summary>
    private static readonly JsonSerializerOptions s_arguments = new(AIJsonUtilities.DefaultOptions) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private readonly MainWindow _window;

    /// <summary>The edits this made or put back, newest last, so undo takes back this one's and not the tab's.</summary>
    /// <remarks>
    /// The window's own undo asks the front tab first and the project second. An edit to a drawing
    /// in no tab lands on the project, so after one the window's undo took back whatever the front
    /// tab had done instead.
    /// </remarks>
    private readonly Stack<Made> _undos = new();

    /// <summary>The edits <see cref="Undo"/> took back, newest last, for redo to put back the same ones; any new edit of these tools' ends them.</summary>
    private readonly Stack<Made> _redos = new();

    /// <summary>One edit: its step, and the history holding it, the tab's or else the project's.</summary>
    /// <param name="Node">
    /// The node the step adds or removes, or the project's root for a step of anyone's, which could
    /// be either: taking it back or putting it again would close the tabs under it without asking.
    /// </param>
    /// <param name="Text">The drawing whose text a step of the project's rewrites, or the group whose drawings a rename does, under any tab opened on one since.</param>
    private sealed record Made(string Label, object Step, SvgViewer? Viewer, ProjectWorkspace? Workspace, ProjectNode? Node = null, ProjectNode? Text = null);

    /// <summary>Whether <see cref="For"/> last handed out the small set, which has no open or save to act on what a tab holds.</summary>
    /// <remarks><see cref="Context"/> is told instead: a session asks for it before the tools, so after a change of model this is still the last one's.</remarks>
    private bool _small;

    public AssistantTools(MainWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));

        // Through Ask as it is when asked, which a panel sets after this.
        Confirm = async question => (await Ask(new Question(question, s_allow)).ConfigureAwait(true))?.Choice == s_allow[0];
    }

    /// <summary>Something to ask the person, and the answers it offers.</summary>
    /// <param name="Text">Built from the state alone: a client answers by a question's <see cref="Key"/>, and one saying how often it was asked is a new question every time.</param>
    public sealed record Question(string Text, IReadOnlyList<string> Choices)
    {
        /// <summary>The same for the same words and choices, so a call run again once answered finds its answer, and a question about what has since changed is asked again.</summary>
        public string Key => "q" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Text + "\n" + string.Join("\n", Choices))))[..16];
    }

    /// <summary>One of a question's choices, or null where the person declined it, cancelled it or stopped the reply.</summary>
    public sealed record Answer(string? Choice);

    /// <summary>What a render draws on.</summary>
    internal enum Ground
    {
        Transparent,
        Light,
        Dark,
        Both
    }

    /// <summary>What find looks among; left out, all four.</summary>
    internal enum Sought
    {
        Names,
        Ids,
        Values,
        Uses
    }

    /// <summary>The tools that read and change nothing, for a client to call without asking.</summary>
    public static readonly IReadOnlySet<string> Reading = new HashSet<string>(StringComparer.Ordinal)
    {
        "read_doc", "get_project", "get_drawing", "get_element", "get_node", "get_context", "get_declarations", "get_problems", "get_code", "get_templates", "find", "render", "streamline_search",
        "get_changes", "git_history", "get_history"
    };

    /// <summary>The tools that write past the history - to disk, to git, or a node away - which a client should ask about.</summary>
    public static readonly IReadOnlySet<string> Confirming = new HashSet<string>(StringComparer.Ordinal)
    {
        "save", "export", "remove", "git_commit"
    };

    /// <summary>The tools that, called again with the same arguments, leave things as the first call did.</summary>
    public static readonly IReadOnlySet<string> Repeatable = new HashSet<string>(StringComparer.Ordinal)
    {
        "select", "open", "set_attributes", "set_text", "set_node", "set_declarations", "declare", "replace_drawing", "export", "reload", "streamline_import"
    };

    /// <summary>The tools that reach past this machine, to Streamline with the person's key.</summary>
    public static readonly IReadOnlySet<string> Reaching = new HashSet<string>(StringComparer.Ordinal)
    {
        "streamline_search", "streamline_import"
    };

    /// <summary>What a client shows each tool as: what Studio's own menus would call it.</summary>
    public static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["read_doc"] = "Read the docs",
        ["get_context"] = "See what is open",
        ["get_project"] = "List the project",
        ["get_drawing"] = "Read a drawing",
        ["get_element"] = "Read an element",
        ["get_node"] = "Read a node's settings",
        ["get_problems"] = "List problems",
        ["get_code"] = "Read the generated C#",
        ["get_declarations"] = "Read declarations",
        ["get_templates"] = "List templates",
        ["get_history"] = "Read the undo history",
        ["get_changes"] = "List changes",
        ["git_history"] = "List commits",
        ["find"] = "Find in the project",
        ["render"] = "Render a drawing",
        ["streamline_search"] = "Search Streamline",
        ["streamline_import"] = "Import from Streamline",
        ["set_attributes"] = "Set attributes",
        ["set_text"] = "Set an element's text",
        ["replace_drawing"] = "Replace a drawing's source",
        ["insert_element"] = "Insert an element",
        ["remove_elements"] = "Remove elements",
        ["move_element"] = "Move an element",
        ["duplicate_elements"] = "Duplicate elements",
        ["select"] = "Select elements",
        ["open"] = "Open a node",
        ["add_group"] = "Add a group",
        ["add_drawing"] = "Add a drawing",
        ["extract_template"] = "Make a template",
        ["move"] = "Move a node",
        ["set_declarations"] = "Set declarations",
        ["declare"] = "Declare a name",
        ["set_node"] = "Change a node's setting",
        ["save"] = "Save",
        ["export"] = "Export",
        ["new_project"] = "New project",
        ["open_file"] = "Open a file",
        ["reload"] = "Reload from disk",
        ["remove"] = "Remove a node",
        ["git_commit"] = "Commit",
        ["undo"] = "Undo",
        ["redo"] = "Redo"
    };

    /// <summary>Who the Edit menu says made an edit: "Undo Assistant: make it red".</summary>
    public string Who { get; init; } = "Assistant";

    /// <summary>Asks the person before something that cannot be taken back with ⌘Z: allow or deny, through <see cref="Ask"/> unless set.</summary>
    public Func<string, Task<bool>> Confirm { get; set; }

    /// <summary>Asks the person to choose, answering null where nobody can be asked.</summary>
    public Func<Question, Task<Answer?>> Ask { get; set; } = _ => Task.FromResult<Answer?>(null);

    /// <summary>Whether a question could reach the person now, so that one costly to work out, as an export's is, is worked out only then.</summary>
    public Func<bool> CanAsk { get; set; } = () => true;

    /// <summary>Whether the call carries an answer already, as a client calling again with one does; to a question it no longer asks, it is no yes to anything.</summary>
    public Func<bool> Answered { get; set; } = () => false;

    /// <summary>Raised on the UI thread after each call that did what it was asked, with the line the panel shows for it.</summary>
    /// <remarks>
    /// The MCP server takes a call that answers without it, other than a <see cref="Reading"/> one,
    /// for a refusal. Null where what was asked held already, which is no refusal and no step either.
    /// </remarks>
    public event EventHandler<string?>? Called;

    /// <summary>The functions for a model: all of them, or the few a small context has room for.</summary>
    /// <param name="docs">Whether the model reads the docs a section at a time, rather than being given them whole.</param>
    public IList<AITool> For(bool small, bool docs)
    {
        var tools = new List<AITool>();

        _small = small;

        if (docs)
        {
            Add(ReadDoc, "read_doc", "Reads one section of the Svg Studio documentation by its id from the contents.");
        }

        Add(GetProject, "get_project", "Lists the open project's groups and drawings with their node paths, and which one is in front.");
        Add(GetDrawing, "get_drawing", "Reads a drawing: its elements with their address keys, any problems, and its SVG source, as it stands or as a git revision has it.");
        Add(SetAttributes, "set_attributes", "Sets or removes attributes on one element of a drawing, as a single undo step.");
        Add(Undo, "undo", "Takes back the last edit these tools made, whichever drawing or the project it went to, and names it. Takes nothing back while another edit is on top of it in its tab or the project, and names that one. With no edit of these tools' left, takes back the person's last and names it; where the person has taken back every one already, the first call takes nothing and says so.");

        if (small)
        {
            return tools;
        }

        Add(Redo, "redo", "Puts back what undo last took back, and names it. Puts nothing back while a step taken back since is in front of it, and names that one. Once these tools make a newer edit, or with nothing of undo's left, puts back what Studio's own Redo would, and names it; where all undo took has been put back already or built over, the first call puts nothing back and says so.");
        Add(GetHistory, "get_history", "Reads the undo history: what undo and redo would take or put back if called now, or why they would refuse; what Cmd/Ctrl+Z in Studio would take; and the steps of the drawing in front and of the project, newest first, each named as the Edit menu names it, which says whose it is. Changes nothing.");
        Add(GetElement, "get_element", "Reads every attribute of one element of a drawing, and the text set_text changes, as the file writes them.");
        Add(GetNode, "get_node", "Reads the settings of a group, a drawing or the project: each one it gives itself, or the value it inherits and the path of the node giving it, the size it is built at, and what it declares and inherits.");
        Add(GetProblems, "get_problems", "Lists every problem in a drawing, in the drawings under a group, or in the whole project: the one call that finds what is wrong.");
        Add(GetCode, "get_code", "Generates the C# a drawing is built into, as svgc writes it for that drawing alone, from the text in its tab where one holds it; or says why it cannot be generated, which get_problems does not find.");
        Add(SetText, "set_text", "Sets the text between an element's tags - a <text>'s words or an <e:let>'s expression - as a single undo step.");
        Add(ReplaceDrawing, "replace_drawing", "Replaces a drawing's whole SVG source, as a single undo step. Prefer set_attributes for small changes.");
        Add(InsertElement, "insert_element", "Adds an element, written as SVG markup, inside or beside another element of a drawing, as a single undo step, and says the key it lands at.");
        Add(RemoveElements, "remove_elements", "Removes elements of a drawing, with everything inside them, as a single undo step.");
        Add(MoveElement, "move_element", "Moves an element of a drawing inside or beside another, as a single undo step, and says the key it lands at. A move into itself, or across a <defs>, <clipPath> or <mask>, is refused.");
        Add(DuplicateElements, "duplicate_elements", "Copies elements of a drawing, each right after itself with every id in it renamed, as a single undo step, and says the copies' keys and ids.");
        Add(Select, "select", "Selects elements of the drawing in front, by address key, so the person sees them.");
        Add(Open, "open", "Opens a group or drawing of the project in its tab.");
        Add(AddGroup, "add_group", "Adds an empty group inside a group, and opens it.");
        Add(AddDrawing, "add_drawing", "Adds a drawing from SVG text inside a group, and opens it; with template, through one of the project's templates as an import puts it, declaring on the group what it needs and lacks, in the same undo step.");
        Add(GetTemplates, "get_templates", "Lists the project's import templates as its <e:templates> block holds them, and any that do not read and why; with svg, which of them fit that drawing going into a group, best first, and whether an import would be sure of the first. Changes nothing.");
        Add(ExtractTemplate, "extract_template", "Adds a template to the project made from what a drawing paints through {{ }}: a slot for each expression, picked by where it paints and how much, which an import then offers for icons laid out like it, as a single undo step.");
        Add(Move, "move", "Moves a group or drawing into another group.");
        Add(GetDeclarations, "get_declarations", "Reads the declarations block a group, or the project, gives every drawing under it.");
        Add(SetDeclarations, "set_declarations", "Replaces the declarations block of a group, or of the project, as a single undo step.");
        Add(Declare, "declare", "Adds one parameter or let to a drawing, a group or the project, or changes one, as a single undo step: what is left out stays as it is, and to renames it with every use, through every drawing and group under a group or the project (not the project's templates). A new parameter needs a type, a new let an expression; a name a group above declares is refused, naming the group to declare it on.");
        Add(Find, "find", "Finds where something is in the project, each drawing as it stands: group and drawing names, element ids, attribute values and text between tags containing the words given, and the {{ }} expressions and lets naming a declared name, matched whole. Does not search the project's templates.");
        Add(SetNode, "set_node", "Changes one setting of a group, a drawing or the project, as a single undo step. cache, helperScope and skiaSharp are the project's alone, and name, x and y a group's or drawing's; a width or height clears a scale, and a scale both.");
        Add(Save, "save", "Saves the project, or the drawing in front when it is a file of its own. Given a path, saves a project never saved, or a drawing of no project's, there: the drawing's tab then saves to it, and its undo history starts again.");
        Add(Export, "export", "Writes what is open to a file outside the project: with a project open, every drawing of it as one C# file, as Export in the File menu does, after taking in the edits of the tab in front; with none, the drawing in front, as C# where the path ends .cs and as SVG where it ends .svg. Text the project's C# cannot write out is laid out as the person chooses where they can be asked, and baked where not, unless they chose for good.");
        Add(NewProject, "new_project", "Starts a new, empty project in place of the one open, closing its tabs; where it or a tab of it holds changes not saved, asks the person whether to throw them away, and is refused where they cannot be asked. Nothing is written until save is given a path.");
        Add(OpenFile, "open_file", "Opens a file by its full path: a drawing (.svg) in a tab of its own, or a project (.svgstudio) in place of the one open, closing its tabs, which over changes not saved in it or a tab of it asks the person whether to throw them away, and is refused where they cannot be asked. An svgc project (.svgcproj) or a PaintCode document (.pcvd) is converted into a new project, held in Studio until save is given a path.");
        Add(Reload, "reload", "Reads the open project again from its file after it was changed outside Studio, which does not watch the disk; while a drawing of no project's is in front, reads that drawing from its own file instead, and says the project was not read. Over edits not saved, asks the person whether to throw them away, and is refused where they cannot be asked. The project's tabs open again where their path still names the same node; the undo history starts again.");
        Add(Remove, "remove", "Removes a group or drawing from the project; where a tab under it holds edits not yet saved into the project, asks the person whether to throw them away, and is refused where they cannot be asked.");
        Add(GetChanges, "get_changes", "Lists what has changed in the project since its last commit, edits not yet saved included, as Studio's Changes pane compares it: each group or drawing added, removed, changed, moved or given new settings, and what about it changed, for a commit message; with since, against another commit, branch or tag. Edits held in a tab are left out until saved into the project.");
        Add(GitHistory, "git_history", "Lists the commits that touched the project's file, newest first, each with the hash get_drawing and get_changes take as a revision.");
        Add(GitCommit, "git_commit", "Saves and commits the project to its git repository.");

        return tools;

        void Add(Delegate tool, string name, string description) => tools.Add(Function(tool, new AIFunctionFactoryOptions { Name = name, Description = description }));
    }

    /// <summary>A tool made as <see cref="For"/> makes each of its own, for one the MCP server adds.</summary>
    internal AIFunction Function(Delegate tool, AIFunctionFactoryOptions options)
    {
        options.SerializerOptions = s_arguments;

        return new Checked(AIFunctionFactory.Create(tool, options), Reading.Contains(options.Name!) ? null : _window);
    }

    /// <summary>What is open, in a few lines, for the start of every message the person sends.</summary>
    /// <param name="small">
    /// Whether the model is one without the save tool, which is not told there is anything to save:
    /// a 3B on-device model answered every mention of it by telling the person to.
    /// </param>
    /// <param name="conflicted">Whether git, asked now, says the project is part way through a merge or a rebase.</param>
    public string Context(bool small = false, bool conflicted = false)
    {
        var text = new StringBuilder();
        var front = _window.FrontNode;

        if (_window.Workspace is { } workspace)
        {
            text.Append("Project: ").Append(workspace.Name).Append(workspace.IsEdited && !small ? " (unsaved changes)" : string.Empty).Append('\n');
        }
        else
        {
            text.Append("No project is open.\n");
        }

        if (front is { })
        {
            text.Append("In front: ").Append(Describe(front, small)).Append('\n');

            // A group's tab is its board, with no drawing in front for a tool to take without a path;
            // a pick an undo has taken out the board still describes, and it has none to give.
            if (_window.TabOf(front)?.Content is GroupPanel board && !ReferenceEquals(board.Described, front) && Path(board.Described) is not Gone)
            {
                text.Append("Picked on the board: ").Append(Describe(board.Described, small)).Append("; name it by that path.\n");
            }
        }
        else if (_window.FrontViewer is { } viewer)
        {
            text.Append("In front: the drawing ").Append(viewer.DocumentPath ?? "Untitled").Append(", not part of a project\n");
        }

        if (!small && _window.HoldingEdits() is { Count: > 0 } held)
        {
            text.Append("Tabs holding edits not yet in the project: ").Append(string.Join(", ", held.Select(Named))).Append("; a save writes only the one in front.\n");
        }

        if (_window.FrontViewer is { } shown)
        {
            var map = shown.SourceAddresses();
            var selected = shown.Elements.SelectedAddresses
                .Select(key => map.TryGetValue(key, out var mine) ? mine : null)
                .OfType<string>()
                .ToList();

            if (selected.Count > 0)
            {
                text.Append("Selected elements: ").Append(string.Join(", ", selected.Select(key => key.Length == 0 ? "(root)" : key))).Append('\n');
            }

            if (Problems(front as ProjectDrawing, shown.Source) is { Count: > 0 } problems)
            {
                // Apart, as the viewer counts them: a warning is something the drawing opens in spite of.
                var warnings = problems.Count(problem => problem.StartsWith(Warning, StringComparison.Ordinal));
                var errors = problems.Count - warnings;

                text.Append("The drawing has ")
                    .Append(errors > 0 ? $"{errors} error(s)" : null)
                    .Append(errors > 0 && warnings > 0 ? " and " : null)
                    .Append(warnings > 0 ? $"{warnings} warning(s)" : null)
                    .Append(_window.Workspace is { } && !small ? "; get_drawing lists them, get_problems the whole project's.\n" : "; get_drawing lists them.\n");
            }
        }

        if (_window.Workspace is { } && _window.FrontViewer is null && !small)
        {
            text.Append("get_problems lists what is wrong in the project's drawings.\n");
        }

        if (_window.Changes.Summary is { } git)
        {
            text.Append("Git: ").Append(git)
                .Append(conflicted ? "; the project is part way through a merge or a rebase, so save and git_commit refuse until it is finished or aborted." : null)
                .Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    [Description("Reads a section of the documentation.")]
    /// <remarks>An id it does not know is answered with the contents, so asking with none is how a model finds the ids.</remarks>
    private string ReadDoc([Description("The section id, as the contents lists it; empty to list the sections.")] string? id = null)
        => (string.IsNullOrWhiteSpace(id) ? null : AssistantDocs.Read(id))
           ?? $"{(string.IsNullOrWhiteSpace(id) ? "The" : $"There is no section {id}. The")} documentation's sections, by id:\n{AssistantDocs.Contents}";

    /// <summary><see cref="Context"/> on the UI thread, for a client that has no message to put it in.</summary>
    /// <remarks>
    /// Git is asked, as save and git_commit ask it before refusing: the panel's last look is from when
    /// the window was last in front, and a client calls while it is not.
    /// </remarks>
    public Task<string> ContextAsync() => UiAsync(async () => Context(conflicted: await _window.Changes.Conflicted().ConfigureAwait(true)));

    private Task<string> GetProject([Description("A group's node path to list only what is under it; empty for the whole project.")] string? node = null) => Ui(() =>
    {
        if (_window.Workspace is not { } workspace)
        {
            return _window.FrontViewer is { } viewer
                ? $"No project is open. The front tab is the drawing {viewer.DocumentPath ?? "Untitled"}; leave node empty to work on it."
                : "Nothing is open.";
        }

        if (Resolve(node) is not ProjectGroup listed)
        {
            return NotA("group", node);
        }

        var text = new StringBuilder();
        var rows = new List<string>();
        var held = _window.HoldingEdits();

        text.Append("Project ").Append(workspace.Name)
            .Append(workspace.Document.Path is { } path ? $" at {path}" : " (never saved)")
            .Append(workspace.IsEdited ? ", with unsaved changes" : string.Empty)
            .Append(held.Contains(workspace.Document.Root) ? ", with edits held in its tab" : string.Empty)
            .Append('\n');

        if (_window.FrontNode is { } front)
        {
            text.Append("In front: ").Append(Path(front)).Append('\n');
        }

        if (OwnSettings(workspace.Document.Root) + Declares(workspace.Document.Root) is { Length: > 0 } project)
        {
            text.Append("(project)").Append(project).Append('\n');
        }

        text.Append("Nodes (path, kind, name, the settings it gives itself; a drawing takes each setting but x and y from the nearest of itself and the nodes above it to give one, the nearest giving any of width, height or scale decides all three, and every drawing under a group inherits what it declares):\n");

        Walk(listed, 0);

        return text.Append(Fit(rows, "get_project with a group's path lists only what is under it", text.Length)).Append(rows.Count > 0 ? "\n" : null).ToString();

        void Walk(ProjectGroup group, int depth)
        {
            foreach (var child in group.Children)
            {
                var row = new StringBuilder(new string(' ', depth * 2)).Append(Path(child)).Append(' ')
                    .Append(child is ProjectGroup ? "group" : "drawing").Append(' ')
                    .Append(ProjectWorkspace.Label(child))
                    .Append(OwnSettings(child));

                if (_window.TabOf(child) is { })
                {
                    row.Append(held.Contains(child) ? " (open, holding unsaved edits)" : " (open)");
                }

                if (child is ProjectGroup holder && Declares(holder) is { } names)
                {
                    row.Append(names);
                }

                rows.Add(row.ToString());

                if (child is ProjectGroup inner)
                {
                    Walk(inner, depth + 1);
                }
            }
        }
    });

    private Task<string> GetDrawing(
        [Description("The drawing's node path from get_project; empty for the drawing in front.")] string? node = null,
        [Description("An element's address key to list only the elements inside it; empty for all of them.")] string? key = null,
        [Description("A commit, branch or tag, such as HEAD or a hash from git_history, to read the drawing as git has it there, through that revision's declarations; empty for as it stands.")] string? revision = null)
        => UiAsync(async () =>
        {
            ProjectDocument? then = null;

            if (!string.IsNullOrEmpty(revision))
            {
                (then, var why) = await Committed(revision).ConfigureAwait(true);

                if (then is null)
                {
                    return why;
                }
            }

            if (Target(node, out var about) is not { } target)
            {
                return about;
            }

            var drawing = Drawing(node);
            ProjectDrawing? old = null;

            if (then is { })
            {
                if ((old = Was(then, drawing, target.Text, revision!, out var said)) is null)
                {
                    return said;
                }

                about += said;
            }

            var text = new StringBuilder(about).Append('\n');
            var source = old?.Text ?? target.Text;
            var read = SvgSourceDocument.Read(source, out var unreadable);
            var root = read?.Document.Root;
            var under = Key(key);

            if (read is { } && under.Length > 0 && SvgAttributeEditor.ElementName(read, under) is null)
            {
                return $"There is no element {under}. get_drawing lists the keys.";
            }

            // Read first, for the outline to leave them room; no blocks above an old one, whose paths are its project's.
            var after = new StringBuilder(drawing is { } && old is null ? Inherits(drawing) : null);
            var rows = new List<string>();

            if (Problems(old ?? drawing, source) is { Count: > 0 } problems)
            {
                after.Append(Cut(problems.Select(problem => "Problem: " + problem).ToList(), null)).Append('\n');
            }

            if (root is { })
            {
                foreach (var (at, element, depth) in Keyed(root))
                {
                    if (under.Length == 0 || at == under || at.StartsWith(under + "/", StringComparison.Ordinal))
                    {
                        var row = new StringBuilder(new string(' ', depth * 2)).Append(at.Length == 0 ? "(root)" : at).Append(' ')
                            .Append(SvgAttributeEditor.ElementName(element));

                        if (element.Attribute("id")?.Value is { } id)
                        {
                            row.Append(" #").Append(id);
                        }
                        else if (element.Attribute("name")?.Value is { } name)
                        {
                            // Not "#", which is an id's here: no url(#…) finds a declaration by its name.
                            row.Append(" name=").Append(name);
                        }

                        rows.Add(row.ToString());
                    }
                }

                text.Append("Elements (address key, element, id or name):\n");
                text.Append(Fit(rows, "get_drawing with an element's key lists only what is inside it", text.Length + after.Length)).Append('\n');
            }
            else
            {
                text.Append("The source does not read as XML: ").Append(unreadable).Append('\n');
            }

            text.Append(after);

            // Whole or not at all: replace_drawing takes the source back whole, and one cut short
            // and sent back would write the cut into the drawing.
            if (text.Length + source.Length > Longest)
            {
                text.Append("The source, ").Append(source.Length).Append(" characters, is too long for this answer and is left out. ").Append(
                    old is { } ? $"It is git's at {revision}, which no tool changes; the keys above are its, and get_drawing without revision reads the drawing the other tools change."
                    : root is { } ? "get_element reads one element's attributes and text, and set_attributes and set_text change them; replace_drawing needs the whole source, so it cannot be used from this answer."
                    : "Until it reads, no tool can change it, replace_drawing included; the reason is above.");
            }
            else
            {
                text.Append("Source:\n").Append(source);
            }

            return text.ToString();
        });

    /// <summary>Every element from <paramref name="root"/> down, in document order, with its address key and how deep it is.</summary>
    /// <remarks>Counted on the way down: each key worked out from its element counts its siblings again.</remarks>
    private static IEnumerable<(string Key, XElement Element, int Depth)> Keyed(XElement root)
    {
        var pending = new Stack<(string Key, XElement Element, int Depth)>();

        pending.Push((string.Empty, root, 0));

        while (pending.TryPop(out var next))
        {
            yield return next;

            var children = next.Element.Elements().ToList();

            for (var index = children.Count - 1; index >= 0; index--)
            {
                var at = index.ToString(CultureInfo.InvariantCulture);

                pending.Push((next.Key.Length == 0 ? at : $"{next.Key}/{at}", children[index], next.Depth + 1));
            }
        }
    }

    private Task<string> GetElement(
        [Description("The element's address key from get_drawing; empty for the root svg element.")] string key,
        [Description("The drawing's node path; empty for the drawing in front.")] string? node = null)
        => Ui(() =>
        {
            if (Target(node, out var about) is not { } target)
            {
                return about;
            }

            if (SvgSourceDocument.Read(target.Text, out var unreadable) is not { } source)
            {
                return unreadable ?? "The drawing cannot be read.";
            }

            key = Key(key);

            if (SvgAttributeEditor.ElementName(source, key) is not { } name)
            {
                return $"There is no element {key}. get_drawing lists the keys.";
            }

            var text = new StringBuilder($"<{name}> at {(key.Length == 0 ? "(root)" : key)}\n");

            foreach (var attribute in SvgAttributeEditor.Attributes(source, key))
            {
                text.Append(attribute.Name).Append('=').Append(Shown(attribute.Value)).Append('\n');
            }

            // What set_text changes, which get_drawing does not show where it leaves the source out.
            if (SvgAttributeEditor.Content(source, key, out _) is { } words)
            {
                text.Append("Text: ").Append(Shown(words)).Append('\n');
            }

            return text.ToString();

            // An embedded image is its base64, which no answer has room for, and the client cuts one
            // longer than an answer holds; marked so that, copied back, it is plainly no value.
            string Shown(string value)
                => value.Length > 1_000 && value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? $"(left out: {value[..Math.Max(value.IndexOfAny(new[] { ';', ',' }), 5)]}, {value.Length} characters)"
                   : text.Length + value.Length > Longest ? $"(left out: {value.Length} characters, more than this answer holds)"
                   : value;
        });

    private Task<string> GetNode(
        [Description("The node path from get_project; empty or (project) for the project itself.")] string? node = null)
        => Ui(() =>
        {
            if (Resolve(node) is not { } found || _window.Workspace is not { } workspace)
            {
                return NoNode(node);
            }

            var text = new StringBuilder(found is ProjectRoot ? $"(project) {workspace.Name}" : Describe(found, _small)).Append('\n');
            var unset = new List<string>();

            foreach (var name in ProjectDocument.Settings(found))
            {
                if (GroupPanel.Value(found, name) is { } own)
                {
                    text.Append(name).Append("=\"").Append(own).Append("\"\n");
                }
                else if (GroupPanel.Inherited(found, name) is { } inherited)
                {
                    text.Append(name).Append("=\"").Append(inherited.Value).Append("\" from ").Append(Path(inherited.Owner)).Append('\n');
                }
                else
                {
                    unset.Add(name);
                }
            }

            if (unset.Count > 0)
            {
                text.Append("Unset: ").Append(string.Join(", ", unset)).Append('\n');
            }

            // The node giving any of the three gives all three, so it is asked for one and read for each.
            var sizer = found.OwnerOf("width");
            var size = sizer is null
                ? $"{(found is ProjectDrawing ? "the size its svg gives" : "no size")}: neither it nor a node above it gives width, height or scale."
                : string.Join(" ", new[] { "width", "height", "scale" }.Select(name => GroupPanel.Value(sizer, name) is { } value ? $"{name}={value}" : null).OfType<string>())
                  + $", given by {(ReferenceEquals(sizer, found) ? "itself" : Path(sizer))}.";

            text.Append(found is ProjectDrawing ? "Built at " : "Hands down ").Append(size).Append(found is ProjectDrawing drawn ? Pixels(drawn) : null).Append('\n');

            if (found is ProjectDrawing { Source: { } source })
            {
                text.Append("Imported from ").Append(source).Append('\n');
            }

            if ((found is ProjectGroup group ? Names(group) : Declared((ProjectDrawing)found)).ToList() is { Count: > 0 } declares)
            {
                text.Append("Declares: ").Append(string.Join(", ", declares)).Append('\n');
            }

            text.Append(Inherits(found));

            if (_window.Pending(found) is { } typed)
            {
                text.Append("Settings typed in the tab of ").Append(Named(typed)).Append(" are not in the project yet, and are not shown here.\n");
            }

            return text.ToString();

            IEnumerable<string> Declared(ProjectDrawing drawing)
            {
                var own = SvgExpressionDeclarations.Parse(Text(drawing), out _);

                return own.Parameters.Select(parameter => parameter.Name).Concat(own.Lets.Select(let => let.Name));
            }

            // Loaded as render loads it, so the two agree; nothing where it will not build.
            string? Pixels(ProjectDrawing drawing)
            {
                try
                {
                    using var document = GroupPanel.Load(Text(drawing), drawing);

                    return document.Svg.Picture is { CullRect: { Width: > 0f, Height: > 0f } cull }
                        ? FormattableString.Invariant($" That is {cull.Width:0.##}×{cull.Height:0.##} px.")
                        : null;
                }
                catch (Exception)
                {
                    // Anything, as on the board: this is user data reaching a parser.
                    return null;
                }
            }
        });

    /// <remarks>
    /// Not in the small set: the code of one icon runs to thousands of characters, more than a small
    /// model's whole context. Read only: Export first writes the front tab's edits into the project,
    /// and this builds from them where they are.
    /// </remarks>
    private async Task<string> GetCode(
        [Description("The drawing's node path from get_project; empty for the drawing in front.")] string? node = null,
        [Description("Which part to read of code too long for one answer, from 1; the first part says how many there are.")] int part = 1)
    {
        var (code, head, warnings, problems) = await Code(node).ConfigureAwait(false);

        if (code is null)
        {
            return head;
        }

        // Room left for the header, the warnings, the problems, and "; part 10 of 10, get_code with part 11 reads on".
        var parts = Parts(code, Math.Max(Longest - head.Length - warnings.Length - (problems?.Length ?? 0) - 100, Longest / 2));

        if (part < 1 || part > parts.Count)
        {
            return $"The code comes in {parts.Count} part{(parts.Count == 1 ? null : "s")}; ask for 1 to {parts.Count}.";
        }

        return head
               + (parts.Count == 1 ? null : $"; part {part} of {parts.Count}" + (part < parts.Count ? $", get_code with part {part + 1} reads on" : null))
               + ".\n"
               + (part == 1 && warnings.Length > 0 ? warnings + "\n" : null)
               + (part == 1 ? problems : null)
               + "\n"
               + parts[part - 1];

        // At the last line end that fits, or mid-line where one line is longer than a part.
        static List<string> Parts(string text, int most)
        {
            var parts = new List<string>();

            for (var at = 0; at < text.Length;)
            {
                var end = text.Length - at <= most ? text.Length : text.LastIndexOf('\n', at + most - 1, most) + 1;

                end = end > at ? end : at + most;
                parts.Add(text[at..end]);
                at = end;
            }

            return parts;
        }
    }

    /// <summary>The C# get_code generates for a drawing, whole, with what heads its answer; or no code, and why not.</summary>
    internal Task<(string? Code, string Head, string Warnings, string? Problems)> Code(string? node)
        => UiAsync<(string?, string, string, string?)>(async () =>
        {
            if (Target(node, out var about) is not { } target)
            {
                return (null, about, string.Empty, null);
            }

            var log = new List<string>();
            var (drawing, text) = (Drawing(node), target.Text);

            // Before the build, which frees the UI thread for a tab switch or another call's edit.
            var found = Problems(drawing, text);
            string what;
            string? code;

            try
            {
                Func<string?> generate;

                // Everything read here, on the UI thread, and built from off it as Export builds.
                if (drawing is { } && _window.Workspace is { } workspace)
                {
                    var project = workspace.Document.Flatten((drawing, text));
                    var item = project.Items.Single();
                    var settings = SvgcBuildSettings.For(project);
                    var loader = new SkiaSvgAssetLoader(new SkiaModel(new SKSvgSettings()));

                    // Export's answer where the person gave one; asked, it is baked unless they choose
                    // otherwise, and a text it freezes is among the warnings.
                    settings.TextLayout = StudioSettings.RelaxedText == RelaxedTextAnswer.Always ? SvgTextLayout.Relaxed : SvgTextLayout.Baked;
                    generate = () => SvgcProjectBuild.Generate(item, settings, loader, log.Add);
                    what = $"the C# svgc writes for it alone, class {item.Namespace ?? settings.Namespace}.{item.Class}"
                           + (string.Equals(text, drawing.Text, StringComparison.Ordinal) ? null : ", built from the text in its tab")
                           + " (Export writes every drawing into one file, with the helpers where helperScope puts them)";
                }
                else if (target is SvgViewer { Document: { } document } viewer)
                {
                    var name = viewer.ClassName?.Invoke() ?? SvgExport.ClassName(viewer.DocumentPath ?? "drawing");

                    generate = () => SvgExport.Generate(document, text, name);
                    what = $"the C# Export writes for it when no project is open, class {SvgExport.Namespace}.{name}";
                }
                else
                {
                    return (null, $"{about} has not loaded, so there is nothing to generate from.", string.Empty, null);
                }

                code = await Task.Run(generate).ConfigureAwait(true);
            }
            // ArgumentException for an emptied tab, which the parser takes for no text, for clamp, and
            // for a size or padding the build refuses, which a file can hold since each is read alone.
            catch (Exception failure) when (failure is SvgcProjectException or SvgRecipeException or ExprException or InvalidOperationException or XmlException or ArgumentException)
            {
                return (null, $"{about} cannot be generated: {failure.Message}", string.Empty, null);
            }

            if (code is null)
            {
                return (null, $"{about} compiled to nothing to generate from.", string.Empty, null);
            }

            var warnings = Cut(log.Where(line => line.StartsWith("warning:", StringComparison.Ordinal)).ToList(), null);

            // A drawing with errors is generated all the same, drawing nothing of what they are in.
            var problems = found.Count > 0 ? $"Problems:\n{Cut(found.Select(problem => "- " + problem).ToList(), null)}\n" : null;

            return (code, $"{about}: {what}", warnings, problems);
        });

    /// <summary>A drawing's source as get_drawing gives it, the text in its tab where one holds it, and what it is; or no source, and why not.</summary>
    internal Task<(string? Text, string About)> Source(string? node) => Ui<(string?, string)>(() => (Target(node, out var about)?.Text, about));

    /// <summary>The project's drawings in get_project's order, each by its path and by its name after the groups it is in.</summary>
    internal Task<IReadOnlyList<(string Path, string Title)>> Drawings() => Ui<IReadOnlyList<(string, string)>>(() =>
    {
        if (_window.Workspace is not { } workspace)
        {
            return Array.Empty<(string, string)>();
        }

        return Under(workspace.Document.Root).Select(drawing => (Path(drawing), Chain(drawing))).ToList();

        static string Chain(ProjectNode node)
        {
            var names = new List<string>();

            for (var at = node; at.Parent is { } parent; at = parent)
            {
                names.Insert(0, ProjectWorkspace.Label(at));
            }

            return string.Join(" / ", names);
        }
    });

    /// <summary>A PNG of a drawing, or a sheet of it at several sizes and on light and dark, and what it shows; or no PNG, and why not.</summary>
    /// <remarks>
    /// The MCP server's alone, as only it answers with an image. Loaded as the board loads a drawing,
    /// so it is drawn with the values its panel opens with, not ones the person has set in its tab.
    /// </remarks>
    internal Task<(byte[]? Png, string Text)> Render(string? node, int[]? size, Ground background, IReadOnlyDictionary<string, JsonElement>? values, string? svg, string? revision)
        => UiAsync<(byte[]?, string)>(async () =>
        {
            ProjectDocument? then = null;

            if (!string.IsNullOrEmpty(revision))
            {
                if (svg is { })
                {
                    return (null, "revision draws the drawing as git had it, and svg one in its place; give one of them.");
                }

                (then, var why) = await Committed(revision).ConfigureAwait(true);

                if (then is null)
                {
                    return (null, why);
                }
            }

            // A group draws only an svg given, as add_drawing would add it there.
            var into = string.IsNullOrEmpty(node) ? null : Resolve(node) as ProjectGroup;
            ISvgViewerDeclarationTarget? target = null;
            string about;

            if (into is { })
            {
                if (svg is null)
                {
                    return (null, $"{(into is ProjectRoot ? "(project) is the project" : $"{Named(into)} is a group")}, and render draws one drawing; name one of the drawings under it, or give svg to see one before add_drawing adds it there.");
                }

                about = $"A drawing added to {(into is ProjectRoot ? "the project" : "group " + Named(into))}";
            }
            else if ((target = Target(node, out about)) is null)
            {
                return (null, about);
            }

            if (size is { Length: > 8 })
            {
                return (null, $"{size.Length} sizes are more than the 8 a sheet holds.");
            }

            if (size?.Where(px => px is < 1 or > 1024).ToList() is { Count: > 0 } wrong)
            {
                return (null, $"A size is the pixels on the drawing's longer side, from 1 to 1024, which {string.Join(", ", wrong)} {(wrong.Count == 1 ? "is" : "are")} not.");
            }

            if (svg is { } && ProjectDrawing.Read(svg, out var unreadable) is null)
            {
                return (null, unreadable ?? "That SVG cannot be read.");
            }

            var at = into ?? (ProjectNode?)Drawing(node);
            var text = svg ?? target!.Text;
            SvgViewerDocument document;

            // Loaded below through the old drawing's own project, so its blocks and size are that revision's.
            if (then is { })
            {
                if (Was(then, at, text, revision!, out var said) is not { } old)
                {
                    return (null, said);
                }

                (at, text, about) = (old, old.Text, about + said);
            }

            try
            {
                document = GroupPanel.Load(text, at, at is null ? (target as SvgViewer)?.DocumentPath : null);
            }
            catch (Exception failure)
            {
                // Anything, as on the board: this is user data reaching a parser.
                return (null, $"{about} cannot be drawn: {failure.Message}");
            }

            using (document)
            {
                var declared = document.Declarations.Parameters;
                var declares = declared.Count == 0
                    ? "no parameters"
                    : string.Join(", ", declared.Select(parameter => $"{parameter.Name} ({ExprFunctions.Describe(parameter.Type)})"));

                if (values is { Count: > 0 })
                {
                    var bound = GroupPanel.Seeded(document);

                    try
                    {
                        foreach (var (name, value) in values)
                        {
                            if (declared.FirstOrDefault(parameter => parameter.Name == name) is not { } parameter)
                            {
                                return (null, $"{about} has no parameter {name}; it declares {declares}.");
                            }

                            // Written as a default is, so a colour is #ff0000 and a string 'quoted'.
                            bound[name] = ExprEvaluator.Create(SvgExpressionDeclarations.Empty).EvaluateTo(
                                value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText(),
                                parameter.Type,
                                $"The value for '{name}'");
                        }

                        document.Svg.SetExpressionValues(bound);
                    }
                    catch (Exception failure) when (failure is ExprException or ArgumentException)
                    {
                        return (null, $"{failure.Message} {about} declares {declares}.");
                    }
                }

                if (document.Svg.Picture is not { CullRect: { Width: > 0f, Height: > 0f } cull })
                {
                    return (null, $"{about} has nothing to draw: it comes to no width or no height.");
                }

                // Save truncates the size it is scaled to, and 24 × 16/24 can come to 15.99; a float
                // up lands on the size asked for.
                var longest = Math.Max(cull.Width, cull.Height);
                var scales = size is { Length: > 0 }
                    ? size.Select(px => (int)(longest * (px / longest)) < px ? MathF.BitIncrement(px / longest) : px / longest).ToList()
                    : new List<float> { 1f };
                var cells = scales.Select(scale => (Width: (int)(cull.Width * scale), Height: (int)(cull.Height * scale))).ToList();
                var (grounds, on) = background switch
                {
                    Ground.Light => (new[] { StreamlineImportWindow.s_lightPlate }, "on light"),
                    Ground.Dark => (new[] { StreamlineImportWindow.s_darkPlate }, "on dark"),
                    Ground.Both => (new[] { StreamlineImportWindow.s_lightPlate, StreamlineImportWindow.s_darkPlate }, "in a row on light above one on dark"),
                    _ => (new[] { SKColors.Transparent }, "on transparent")
                };
                var one = cells.Count == 1 && grounds.Length == 1;
                var gap = one ? 0 : Gap;
                var width = gap + cells.Sum(cell => cell.Width + gap);
                var row = cells.Max(cell => cell.Height) + 2 * gap;

                if (width > 4096 || row * grounds.Length > 4096)
                {
                    return (null, $"That would be {width}×{row * grounds.Length} px, more than 4096 on a side; {(size is { Length: > 0 } ? "ask for fewer or smaller sizes" : "give a size to draw it smaller")}.");
                }

                using var sheet = new SKBitmap(width, row * grounds.Length);

                using (var canvas = new SKCanvas(sheet))
                {
                    canvas.Clear(SKColors.Transparent);

                    for (var y = 0; y < grounds.Length; y++)
                    {
                        using var plate = new SKPaint { Color = grounds[y] };

                        canvas.DrawRect(SKRect.Create(0, y * row, width, row), plate);

                        for (int x = gap, column = 0; column < cells.Count; x += cells[column].Width + gap, column++)
                        {
                            // Each through Save, so a sheet draws what one image does: Save draws a
                            // non-scaling stroke at the size asked for, and a small size from a larger one.
                            using var stream = new MemoryStream();

                            if (!document.Svg.Save(stream, grounds[y], SKEncodedImageFormat.Png, 100, scales[column], scales[column]))
                            {
                                return (null, $"{about} comes to nothing at {Math.Max(cells[column].Width, cells[column].Height)} px.");
                            }

                            using var image = SKImage.FromEncodedData(stream.ToArray());

                            canvas.DrawImage(image, x, (y + 1) * row - gap - cells[column].Height, new SKSamplingOptions(SKFilterMode.Nearest));
                        }
                    }
                }

                using var png = sheet.Encode(SKEncodedImageFormat.Png, 100);
                var problems = Problems(at, text);
                var said = new StringBuilder()
                    .Append(about).Append(", drawn at ").Append(string.Join(", ", cells.Select(cell => Math.Max(cell.Width, cell.Height))))
                    .Append(" px on its longer side").Append(size is { Length: > 0 } ? null : ", the size it is built at").Append(".\n")
                    .Append(one
                        ? $"One image, {width}×{row} px, {on}.\n"
                        : $"One sheet, {width}×{row * grounds.Length} px: a column for each size in the order asked, standing on one line, {on}, {Gap} px apart.\n");

                if (declared.Count > 0)
                {
                    said.Append(document.Svg.ExpressionValues is { } given
                        ? $"Values: {string.Join(", ", given.Select(pair => $"{pair.Key}={SvgViewerParameterFactory.Describe(pair.Value)}"))}.\n"
                        : "Its values would not bind, so it is drawn with its placeholders.\n");
                }

                if (svg is { })
                {
                    said.Append(into is { }
                        ? "A dry run of the svg given, through what a drawing added there inherits and at the size it would be built at: nothing was changed, and add_drawing adds it.\n"
                        : "A dry run of the svg given, through the drawing's declarations and at its size: nothing was changed, and replace_drawing writes it.\n");
                }

                said.Append(problems.Count == 0 ? "No problems." : $"Problems:\n{Cut(problems.Select(problem => "- " + problem).ToList(), null)}");

                return (png.ToArray(), said.ToString());
            }
        });

    /// <summary>A page of Streamline's icons as one sheet of their previews and a line for each; or no sheet and why, refused where the search was not made or failed, but not where nothing matched.</summary>
    /// <remarks>
    /// The MCP server's alone, as render is. Only the CDN's previews, which no plan counts: the
    /// panel's fallback for a missing one is the icon's details, one request each against the hour's.
    /// </remarks>
    internal async Task<(byte[]? Sheet, string Text, bool Refused)> SearchStreamline(string query, string? style, int offset)
    {
        const int cell = 64, across = 8, most = 24;

        var known = style is null ? null : StreamlinePanel.StyleNames.FirstOrDefault(one => string.Equals(one, style.Trim(), StringComparison.OrdinalIgnoreCase));

        if (style is { } && known is null)
        {
            return (null, $"Streamline has no style '{style}'; it has {string.Join(", ", StreamlinePanel.StyleNames)}.", true);
        }

        // The plate read here too, on the UI thread: its first read makes the window's brushes, which belong to whichever thread that is.
        var (connected, plate) = await Ui(() => (_window.Streamline.Client, StreamlineImportWindow.s_lightPlate)).ConfigureAwait(false);

        if (connected is not { } client)
        {
            return (null, "Searching Streamline needs an API key, which the person sets in Studio's Settings.", true);
        }

        StreamlinePage<StreamlineIcon> page;

        try
        {
            page = await client.Search(query, offset: offset, limit: most, style: known).ConfigureAwait(false);
        }
        catch (Exception failure) when (StreamlinePanel.Failure(failure) is { } said)
        {
            return (null, said, true);
        }

        var icons = page.Items;

        if (icons.Count == 0)
        {
            return (null, $"Nothing on Streamline matches '{query}'{(offset > 0 ? $" from offset {offset} on" : null)}.", false);
        }

        // Kept by the panel, whose import prepares each icon in the style it was found under.
        await Dispatcher.UIThread.InvokeAsync(() => _window.Streamline.Searched(icons, known)).GetTask().ConfigureAwait(false);

        using var limit = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));

        var previews = await Task.WhenAll(icons.Select(async icon =>
        {
            try
            {
                return icon.ImagePreviewUrl is { } url ? SKImage.FromEncodedData(await client.Thumbnail(url, limit.Token).ConfigureAwait(false)) : null;
            }
            catch (Exception)
            {
                // Anything, from the CDN or the decoder: the cell is left empty, and its line says so.
                return null;
            }
        })).ConfigureAwait(false);

        try
        {
            var width = Gap + Math.Min(across, icons.Count) * (cell + Gap);
            var height = Gap + (icons.Count + across - 1) / across * (cell + Gap);

            using var sheet = new SKBitmap(width, height);

            using (var canvas = new SKCanvas(sheet))
            {
                canvas.Clear(plate);

                for (var index = 0; index < icons.Count; index++)
                {
                    if (previews[index] is { } preview)
                    {
                        var scale = Math.Min((float)cell / preview.Width, (float)cell / preview.Height);
                        var (w, h) = (preview.Width * scale, preview.Height * scale);

                        canvas.DrawImage(
                            preview,
                            SKRect.Create(Gap + (index % across * (cell + Gap)) + ((cell - w) / 2), Gap + (index / across * (cell + Gap)) + ((cell - h) / 2), w, h),
                            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                    }
                }
            }

            using var png = sheet.Encode(SKEncodedImageFormat.Png, 100);
            var text = new StringBuilder()
                .Append($"{page.Total} icons on Streamline match '{query}'{(known is { } ? $" in the {known} style" : null)}; these are {offset + 1} to {offset + icons.Count}, ")
                .Append($"on one sheet, {width}×{height} px, in {cell} px cells {across} across and {Gap} px apart, on light:\n");

            for (var index = 0; index < icons.Count; index++)
            {
                var icon = icons[index];

                text.Append($"{offset + index + 1}. {icon.Name}: {icon.Hash}, {icon.FamilyName ?? icon.FamilySlug ?? "no family"}, row {(index / across) + 1}, column {(index % across) + 1}")
                    .Append(!icon.IsFree && !icon.HasPremiumAccess ? ", locked" : null)
                    .Append(previews[index] is null ? ", no preview" : null)
                    .Append('\n');
            }

            return (png.ToArray(), text.Append(page.HasMore ? $"offset {page.NextOffset} reads on." : "There are no more.").ToString(), false);
        }
        finally
        {
            foreach (var preview in previews)
            {
                preview?.Dispose();
            }
        }
    }

    private Task<string> SetAttributes(
        [Description("The element's address key from get_drawing; empty for the root svg element.")] string key,
        [Description("The attributes to write.")] AttributeChange[] attributes,
        [Description("A few words saying what this does, which the Edit menu shows after Undo.")] string summary,
        [Description("The drawing's node path; empty for the drawing in front.")] string? node = null)
        => Ui(() => Edit(node, summary, source =>
        {
            foreach (var change in attributes)
            {
                if (SvgAttributeEditor.SetAttribute(source, Key(key), change.Name, string.IsNullOrEmpty(change.Value) ? null : change.Value) is { } refused)
                {
                    return $"{change.Name}: {refused}";
                }
            }

            return null;
        }));

    private Task<string> SetText(
        [Description("The element's address key from get_drawing.")] string key,
        [Description("The new text between the element's tags; empty to leave it with none.")] string text,
        [Description("A few words saying what this does, shown in the Edit menu.")] string summary,
        [Description("The drawing's node path; empty for the drawing in front.")] string? node = null)
        => Ui(() => Edit(node, summary, source => SvgAttributeEditor.SetContent(source, Key(key), text)));

    private Task<string> ReplaceDrawing(
        [Description("The complete new SVG source.")] string svg,
        [Description("A few words saying what this does, shown in the Edit menu.")] string summary,
        [Description("The drawing's node path; empty for the drawing in front.")] string? node = null)
        => Ui(() =>
        {
            if (Target(node, out var about) is not { } target)
            {
                return about;
            }

            if (ProjectDrawing.Read(svg, out var unreadable) is not { } read)
            {
                return unreadable ?? "That SVG cannot be read.";
            }

            var before = target.Text;

            // A tab has a history of its own to put the whole text on; a drawing in no tab is the
            // project's, where the same text goes in as one step of the project's history.
            if (target is SvgViewer viewer)
            {
                // False is a refusal, which the viewer says through ErrorRaised, or text already there, which it does not.
                string? refused = null;
                EventHandler<string> said = (_, message) => refused = message;

                viewer.ErrorRaised += said;

                try
                {
                    if (!viewer.SetSource(svg, Label(summary)) && refused is { })
                    {
                        return refused;
                    }
                }
                finally
                {
                    viewer.ErrorRaised -= said;
                }
            }
            else if (Resolve(node) is ProjectDrawing drawing && _window.Workspace is { } workspace
                // Only the root is kept, read with plain newlines where the file may end its lines
                // otherwise, so the same root again is no step.
                && read.TextOf(read.Document.Root!) != before.Replace("\r\n", "\n", StringComparison.Ordinal))
            {
                workspace.Do(Label(summary), () => ProjectSnapshot.Text(drawing), () => drawing.SetText(svg));
            }

            return Done(target, node, summary, before);
        });

    private Task<string> InsertElement(
        [Description("The element to add, as SVG markup such as <circle cx=\"12\" cy=\"12\" r=\"4\" />; it may hold elements of its own.")] string markup,
        [Description("The address key from get_drawing of the element it goes inside, or before or after; empty for the root svg element.")] string target,
        [Description("A few words saying what this does, which the Edit menu shows after Undo.")] string summary,
        [Description("Inside, the default, puts it last inside the target; Before or After puts it beside the target.")] SvgElementDrop where = SvgElementDrop.Inside,
        [Description("The drawing's node path; empty for the drawing in front.")] string? node = null)
        => Ui(() =>
        {
            string? made = null;

            return Edit(
                node,
                summary,
                source =>
                {
                    // Read as a file is, for the reason it is refused; the editor gives it the drawing's namespace.
                    if (SvgSourceDocument.Read(markup, out var unreadable)?.Document.Root is not { } element)
                    {
                        return unreadable ?? "That markup cannot be read.";
                    }

                    // Out of its document, or the drawing is given a copy and the key is the original's.
                    element.Remove();

                    return SvgElementEditor.Insert(source, Key(target), where, element, out made);
                },
                () => $"It is at {made}; the keys after it have{Shifted}");
        });

    private Task<string> RemoveElements(
        [Description("The address keys from get_drawing of the elements to remove.")] string[] keys,
        [Description("A few words saying what this does, which the Edit menu shows after Undo.")] string summary,
        [Description("The drawing's node path; empty for the drawing in front.")] string? node = null)
        => Ui(() => Edit(node, summary, source => SvgElementEditor.Remove(source, keys.Select(Key).ToList()), () => $"The keys after what was removed have{Shifted}"));

    private Task<string> MoveElement(
        [Description("The address key from get_drawing of the element to move.")] string key,
        [Description("The address key of the element to move it inside, or before or after; empty for the root svg element.")] string target,
        [Description("A few words saying what this does, which the Edit menu shows after Undo.")] string summary,
        [Description("Inside, the default, puts it last inside the target; Before or After puts it beside the target.")] SvgElementDrop where = SvgElementDrop.Inside,
        [Description("The drawing's node path; empty for the drawing in front.")] string? node = null)
        => Ui(() =>
        {
            string? moved = null;

            return Edit(
                node,
                summary,
                source => SvgElementEditor.Move(source, Key(key), Key(target), where, out moved),
                () => $"It is now at {moved}; the keys after where it was and where it is have{Shifted}");
        });

    private Task<string> DuplicateElements(
        [Description("The address keys from get_drawing of the elements to copy.")] string[] keys,
        [Description("A few words saying what this does, which the Edit menu shows after Undo.")] string summary,
        [Description("The drawing's node path; empty for the drawing in front.")] string? node = null)
        => Ui(() =>
        {
            var copies = new List<string>();

            return Edit(
                node,
                summary,
                source =>
                {
                    var refused = SvgElementEditor.Duplicate(source, keys.Select(Key).ToList(), out var made);

                    // Read off the copies, whose ids the editor renamed.
                    copies.AddRange(made.Select(copy => SvgAttributeEditor.Attribute(source, copy, "id") is { } id ? $"{copy} (#{id})" : copy));

                    return refused;
                },
                () => copies.Count == 1
                    ? $"The copy is at {copies[0]}, with every id in it renamed to one the drawing does not have; the keys after it have{Shifted}"
                    : $"The copies are at {string.Join(", ", copies)}, with every id in them renamed to ones the drawing does not have; the keys after them have{Shifted}");
        });

    private Task<string> Select(
        [Description("The address keys to select, from get_drawing. None clears the selection.")] string[] keys)
        => Ui(() =>
        {
            if (_window.FrontViewer is not { } viewer)
            {
                return "The tab in front is not a drawing.";
            }

            // The tree is keyed by the drawing that was built, which a group's declarations are written
            // into, so a key of the file is looked up the other way round.
            var tree = viewer.SourceAddresses()
                .GroupBy(pair => pair.Value)
                .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);
            var picked = keys.Select(key => tree.TryGetValue(Key(key), out var built) ? built : null).OfType<string>().ToList();
            var missing = keys.Where(key => !tree.ContainsKey(Key(key))).ToList();

            if (keys.Length > 0 && picked.Count == 0)
            {
                return "None of those keys is an element of the drawing in front.";
            }

            // By what is selected afterwards: TrySelect is false for a row the Elements filter hides,
            // and selects it all the same.
            viewer.Elements.TrySelect(picked);

            return viewer.Elements.SelectedAddresses.ToHashSet(StringComparer.Ordinal).SetEquals(picked)
                ? Report((keys.Length == 0 ? "Selection cleared." : "Selected.") + (missing.Count > 0 ? $" Not elements of this drawing: {string.Join(", ", missing)}." : string.Empty), "select")
                : "Nothing changed: the Elements pane has no row for any of them"
                  + (viewer.Elements.Filter is { Length: > 0 } filter ? $"; the filter \"{filter}\" typed there hides them, and clearing it shows them." : ".");
        });

    private Task<string> Undo() => Ui(() => Step(back: true));

    /// <summary>Puts back what <see cref="Undo"/> took, known by its step as undo knows it.</summary>
    private Task<string> Redo() => Ui(() => Step(back: false));

    private Task<string> GetHistory() => Ui(() =>
    {
        var text = new StringBuilder();
        var front = _window.FrontViewer;
        var workspace = _window.Workspace;

        foreach (var back in new[] { true, false })
        {
            text.Append(Next(back, out var refusal, out _) is { } made
                ? $"{(back ? "undo would take" : "redo would put")} back \"{made.Label}\" from "
                  + (made.Viewer is null ? "the project's history" : ReferenceEquals(made.Viewer, front) ? "the front tab's history" : "the history of a tab not in front")
                  + ".\n"
                : $"{(back ? "undo" : "redo")} would answer: {refusal}\n");
        }

        // As the window's own undo goes: the drawing in front while it has a step, then the project.
        var (keyed, from) = front is { CanUndo: true } ? (front.UndoLabel, "the front tab")
            : workspace is { CanUndo: true } ? (workspace.UndoLabel, "the project")
            : (null, null);

        text.Append(keyed is null ? "Cmd/Ctrl+Z in Studio would take nothing back" : $"Cmd/Ctrl+Z in Studio would take back \"{keyed}\" from {from}")
            .Append("; a text box with the caret in it takes the keystroke first.\n");

        if (front is { })
        {
            text.Append("The front tab, ").Append(_window.FrontNode is { } node ? Named(node) : front.DocumentPath ?? "Untitled").Append(", newest first:\n")
                .Append(Steps("undo", front.UndoLabels))
                .Append(Steps("redo", front.RedoLabels));
        }

        if (workspace is { })
        {
            text.Append("The project, newest first:\n")
                .Append(Steps("undo", workspace.UndoLabels))
                .Append(Steps("redo", workspace.RedoLabels));
        }

        return text.Append($"Steps named \"{Who}: …\" are these tools', for whichever client called them; another \"Name: …\" is another assistant's; the rest are the person's.").ToString();

        static string Steps(string way, IReadOnlyList<string> labels)
            => labels.Count == 0 ? $"{way}: none\n" : $"{way}:\n{Cut(labels.Select(label => "- " + label).ToList(), null)}\n";
    });

    /// <summary>
    /// Takes back the newest edit these tools made, or puts back the newest they took back, known by
    /// the step its history handed out; with none, the window's next, remembered for the other way.
    /// </summary>
    /// <remarks>
    /// One the person has stepped already, built over, or that was in a tab or a project since closed,
    /// is passed over. One behind a step of the person's is refused, naming that step, and kept; so is
    /// one that would take a node whose tab holds edits out of the project, which closes the tab unasked,
    /// and one rewriting a drawing's text under a tab holding edits, which shows and saves them over it.
    /// </remarks>
    private string Step(bool back)
    {
        var made = Next(back, out var refusal, out var passed);
        var (from, to) = back ? (_undos, _redos) : (_redos, _undos);

        // Here and not in Next, which get_history asks: a reading that dropped them would turn the
        // refusal it gave, once every edit was stepped already, into a step of the person's.
        for (; passed > 0; passed--)
        {
            from.Pop();
        }

        if (made is null)
        {
            return refusal;
        }

        if (from.TryPeek(out var mine) && ReferenceEquals(mine, made))
        {
            from.Pop();
        }

        _ = Way(made.Viewer, made.Workspace, back).Take();
        to.Push(made);

        return Report($"{(back ? "Took" : "Put")} back \"{made.Label}\".", back ? "undo" : "redo");
    }

    /// <summary>The edit <see cref="Step"/> would take back or put back now, or null and its refusal.</summary>
    /// <param name="passed">How many of these tools' entries on top no history holds any more, for Step to drop.</param>
    private Made? Next(bool back, out string refusal, out int passed)
    {
        var from = back ? _undos : _redos;
        var (verb, doing) = back ? ("taken", "taking") : ("put", "putting");
        Made? made = null;

        refusal = string.Empty;
        passed = 0;

        if (from.Count == 0)
        {
            // Read first: once stepped, it is the other way's. Remembered for that way, which would
            // otherwise find it in front of whatever these tools stepped before it, and refuse.
            var front = _window.FrontViewer is { } viewer && (back ? viewer.CanUndo : viewer.CanRedo) ? viewer : null;
            var workspace = front is null ? _window.Workspace : null;

            if (Way(front, workspace, back) is not ({ } step, var label, _, _))
            {
                refusal = $"There is nothing to {(back ? "take" : "put")} back.";

                return null;
            }

            made = new Made(label ?? string.Empty, step, front, workspace, workspace?.Document.Root);
        }

        // Top first, as a stack enumerates.
        foreach (var top in from)
        {
            var (next, on, has, _) = Way(top.Viewer, ReferenceEquals(top.Workspace, _window.Workspace) ? top.Workspace : null, back);

            if (!has(top.Step))
            {
                passed++;
            }
            else if (!ReferenceEquals(next, top.Step))
            {
                refusal = $"Nothing was {verb} back: \"{on}\" "
                          + (back ? "is on top of" : "was taken back after")
                          + $" \"{top.Label}\" in {(top.Viewer is { } ? "its tab's" : "the project's")} history, and only the step "
                          + (back ? "on top can be taken back." : "taken back last can be put back.");

                return null;
            }
            else
            {
                made = top;
                break;
            }
        }

        if (made is null)
        {
            refusal = back
                ? $"Nothing was taken back: every edit {Who} made has been taken back already, or was in a tab or a project since closed."
                : $"Nothing was put back: what {Who} took back has been put back already, been built over by a newer edit, or was in a tab or a project since closed.";

            return null;
        }

        if (made.Node is { } node && _window.HoldingEdits(node) is [var holding, ..])
        {
            refusal = $"Nothing was {verb} back: {Named(holding)} has edits in its tab not yet saved into the project, and {doing} back \"{made.Label}\" could close it without asking; open it and save them first.";

            return null;
        }

        var rewritten = made.Text switch
        {
            ProjectGroup group => group.Drawings,
            ProjectDrawing one => new[] { one },
            _ => Enumerable.Empty<ProjectDrawing>()
        };

        if (rewritten.FirstOrDefault(one => _window.TabOf(one)?.Content is SvgViewer { IsSourceModified: true }) is { } drawing)
        {
            refusal = $"Nothing was {verb} back: {Named(drawing)} has edits in its tab not yet saved into the project, made over \"{made.Label}\"; {doing} it back in the project would not reach the tab, whose next save writes over it. Open it and save or undo them first.";

            return null;
        }

        return made;
    }

    /// <summary>A history's next step one way, its label, whether it still has a given step to take that way, and the call taking it.</summary>
    private static (object? Step, string? Label, Func<object, bool> Has, Func<bool> Take) Way(SvgViewer? viewer, ProjectWorkspace? workspace, bool back)
        => (viewer, workspace) switch
        {
            ({ } tab, _) => back ? (tab.UndoStep, tab.UndoLabel, tab.Holds, tab.Undo) : (tab.RedoStep, tab.RedoLabel, tab.Undone, tab.Redo),
            (_, { } project) => back ? (project.UndoStep, project.UndoLabel, project.Holds, project.Undo) : (project.RedoStep, project.RedoLabel, project.Undone, project.Redo),
            _ => (null, null, _ => false, () => false)
        };

    private Task<string> Open([Description("The node path from get_project.")] string node) => UiAsync(async () =>
    {
        if (Resolve(node) is not { } found)
        {
            return NoNode(node);
        }

        await _window.ShowAsync(found).ConfigureAwait(true);

        return Report($"Opened {Path(found)}.", "open");
    });

    private Task<string> AddGroup([Description("The node path of the group to add it to; empty for the project itself.")] string parent) => UiAsync(async () =>
    {
        if (Resolve(parent) is not ProjectGroup group)
        {
            return NotA("group", parent);
        }

        var label = Label("add group");

        var added = await _window.AddGroupAsync(group, label).ConfigureAwait(true);

        Undoable(label, node: added);

        // Null only with no project open, which Resolve has ruled out.
        return Report($"Added the group {Path(added!)}; set_node renames it." + Undoes, "add group");
    });

    private Task<string> AddDrawing(
        [Description("The node path of the group to add it to; empty for the project itself.")] string parent,
        [Description("The drawing's name, which is also its class unless one is set.")] string name,
        [Description("The drawing's complete SVG source.")] string svg,
        [Description("A template's name from get_templates, to write the drawing's colours as its expressions; left out, it goes in as given.")] string? template = null)
        => UiAsync(async () =>
        {
            if (Resolve(parent) is not ProjectGroup group)
            {
                return NotA("group", parent);
            }

            // Only where named, as a file dropped in goes in as it is: what Studio is sure of is
            // this machine's choices, which would make what the same call adds differ by machine.
            var library = new TemplateLibrary(_window.Workspace!.Document.Root);
            var import = new TemplateImport(name, svg);
            var named = !string.IsNullOrWhiteSpace(template) && template != TemplateLibrary.KeepColoursName;
            TemplateIcon? icon = null;
            string? hint = null;

            try
            {
                icon = TemplateLibrary.Prepare(svg);
            }
            catch (SvgRecipeException failure) when (named)
            {
                return failure.Message;
            }
            catch (SvgRecipeException)
            {
                // The import says why it does not read.
            }

            // Asked of the group as it is, which the drawing is not yet one of.
            var suggestions = icon is null ? Array.Empty<TemplateSuggestion>() : library.Suggest(icon, group);

            if (named)
            {
                if (StreamlineRow.Recipe(suggestions, template) is not { } recipe)
                {
                    var fit = suggestions.Select(one => one.Template.Name).Where(one => one != TemplateLibrary.KeepColoursName).ToList();
                    var chosen = library.Templates.FirstOrDefault(one => one.Name == template);

                    return (chosen is null ? $"The project has no template '{template}'."
                               : chosen.Error is { } error ? $"The template '{template}' does not read: {error}"
                               : chosen.Recipe?.Match is { NeedsFamily: true } match ? $"The template '{template}' is kept to icons of the family {match.Family}, which only a Streamline import names, so it cannot be applied here."
                               : $"The template '{template}' does not fit this drawing.")
                           + (fit.Count == 0
                               ? $" None of the project's templates fits this drawing, but {TemplateLibrary.KeepColoursName} always does."
                               : $" Of the project's templates, {string.Join(", ", fit)} {(fit.Count == 1 ? "fits" : "fit")} this drawing, and {TemplateLibrary.KeepColoursName} always does.");
                }

                import = new TemplateImport(name, icon!.Text, recipe);
            }
            else if (string.IsNullOrWhiteSpace(template) && suggestions is [{ Sure: true } sure, ..] && sure.Template.Name != TemplateLibrary.KeepColoursName)
            {
                hint = $"\nThe template '{sure.Template.Name}' fits it, and a Streamline import would apply it without asking; to have it so, undo this and add it with template \"{sure.Template.Name}\".";
            }

            // Through the import every other drawing arrives by, with its notes said here: a dialog
            // in the window is one a client cannot close, and a call that opens one never returns.
            // Opened only once its step and path are read, since the load gives up the UI thread
            // and whatever ran meanwhile was taken for them.
            var had = Names(group).ToHashSet(StringComparer.Ordinal);
            var notes = new List<string>();
            var label = Label($"add {name}");
            var added = await _window.ImportAsync(group, group.Children.Count, new[] { import }, show: false, notes: notes, label: label).ConfigureAwait(true);

            if (added.Count == 0)
            {
                return notes.Count > 0 ? string.Join("\n", notes) : "That drawing couldn't be added.";
            }

            Undoable(label, node: added[^1]);

            var declared = Names(group).Where(one => !had.Contains(one)).ToList();
            var reply = $"Added {name} as {Path(added[^1])}"
                        + (import.Recipe is null ? null : $" through the template '{template}'")
                        + (declared.Count > 0 ? $", which declared {string.Join(", ", declared)} on {Named(group)} as well" : null)
                        + "." + Undoes + hint;

            await _window.ShowAsync(added[^1]).ConfigureAwait(true);

            return Report(reply, $"add {name}");
        });

    /// <remarks>Never remembers a choice, which is the person's to teach the Streamline panel.</remarks>
    private Task<string> GetTemplates(
        [Description("A complete SVG source to rank the templates for, as an import would; left out, they are only listed.")] string? svg = null,
        [Description("The node path of the group it would go into; empty for the project itself.")] string? parent = null)
        => Ui(() =>
        {
            if (Resolve(parent) is not ProjectGroup group)
            {
                return NotA("group", parent);
            }

            var root = _window.Workspace!.Document.Root;
            var library = new TemplateLibrary(root);
            var keep = $"{TemplateLibrary.KeepColoursName}, which keeps a drawing's colours as they are";

            if (svg is null)
            {
                if (root.Templates.Count == 0)
                {
                    return $"The project has no templates, so an import offers only {keep}; extract_template makes one from a drawing painted through {{{{ }}}}.";
                }

                var listed = new StringBuilder("The project's templates, in the order its <e:templates> block holds them:\n");

                foreach (var one in library.Templates.SkipLast(1))
                {
                    listed.Append("- ").Append(one.Name).Append(one.Error is { } error ? $" (does not read: {error})" : null).Append('\n');
                }

                listed.Append("- ").Append(keep).Append(", always last\n");

                // Whole or not at all, as get_drawing gives a source.
                var block = root.TemplatesText;

                return listed.Length + block.Length > Longest
                    ? listed.Append($"The block, {block.Length} characters, is too long for this answer and is left out.").ToString()
                    : listed.Append("\nThe block:\n").Append(block).ToString();
            }

            TemplateIcon icon;

            try
            {
                icon = TemplateLibrary.Prepare(svg);
            }
            catch (SvgRecipeException failure)
            {
                return failure.Message;
            }

            var suggestions = library.Suggest(icon, group);
            var rows = suggestions
                .Select((one, index) => $"{index + 1}. {one.Template.Name}, score {one.Score.ToString("0.##", CultureInfo.InvariantCulture)}"
                                        + (one.Template.Name == TemplateLibrary.KeepColoursName ? ", which keeps the drawing's colours as they are" : null)
                                        + (one.Sure ? ": sure, so a Streamline import would apply it without asking" : null))
                .ToList();

            var unfit = library.Templates
                .Where(one => !suggestions.Any(suggestion => ReferenceEquals(suggestion.Template, one)))
                .Select(one => one.Error is { } ? $"{one.Name}, which does not read"
                    : one.Recipe?.Match is { NeedsFamily: true } match ? $"{one.Name}, kept to the family {match.Family}, which only a Streamline import names"
                    : one.Name)
                .ToList();

            return $"How an import ranks the project's templates for that drawing going into {(group is ProjectRoot ? "the project" : "group " + Named(group))}, best first:\n"
                   + Cut(rows, null) + "\n"
                   + (suggestions[0].Sure ? null : "Studio is not sure of the first, so the Streamline panel would ask which to use.\n")
                   + (unfit.Count > 0 ? $"Not fitting: {string.Join("; ", unfit)}.\n" : null)
                   + "add_drawing with template brings a drawing in through one.";
        });

    private Task<string> ExtractTemplate(
        [Description("The drawing's node path from get_project; empty for the drawing in front.")] string? node = null,
        [Description("What to call it, unique among the project's templates; empty for the drawing's own name.")] string? name = null)
        => Ui(() =>
        {
            if (Target(node, out var about) is null)
            {
                return about;
            }

            if (Drawing(node) is not { } drawing || _window.Workspace is not { } workspace)
            {
                return "Templates are kept in a project, and the drawing in front is in none.";
            }

            var called = string.IsNullOrWhiteSpace(name) ? ProjectWorkspace.Label(drawing) : name.Trim();
            string text;

            try
            {
                text = TemplateLibrary.Extract(called, Text(drawing));
            }
            catch (SvgRecipeException failure)
            {
                return $"{Named(drawing)}: {failure.Message}";
            }

            // Not the window's ExtractTemplateAsync, which asks for the name in a dialog no client can close.
            var label = Label($"add template {called}");

            if (TemplateLibrary.Put(workspace, workspace.Document.Root.Templates.Count, text, label) is { } refusal)
            {
                return refusal;
            }

            Undoable(label);

            return Report($"Added the template '{called}' to the project." + Undoes + "\n" + text, $"add template {called}");
        });

    /// <summary>Icons from Streamline added at the end of a group through the project's templates, as one step, and the last opened as add_drawing opens its drawing.</summary>
    /// <remarks>The MCP server's alone: it takes the hashes only streamline_search answers.</remarks>
    internal Task<string> ImportFromStreamline(
        [Description("The icons' hashes from streamline_search: up to 20, each one download.")] string[] hashes,
        [Description("The node path of the group to add them to; empty for the project itself.")] string parent,
        [Description("A template of the project's to bring them in through, by name, where it fits them; Keep colours keeps Streamline's own colours. Left out, each batch of icons with the same colours goes through the one Studio suggests.")] string? template = null)
        => UiAsync(async () =>
        {
            if (Resolve(parent) is not ProjectGroup group)
            {
                return NotA("group", parent);
            }

            if (hashes.Length == 0)
            {
                return "Name the icons to import by the hashes streamline_search lists.";
            }

            if (hashes.Length > 20)
            {
                return $"{hashes.Length} icons are more than the 20 one call imports: each is a download Streamline counts against the key's plan.";
            }

            var templates = new TemplateLibrary(_window.Workspace!.Document.Root).Templates.Select(one => one.Name).ToList();

            if (template is { } && !templates.Contains(template))
            {
                var own = templates.Where(name => name != TemplateLibrary.KeepColoursName).ToList();

                return $"The project has no template '{template}'. Its own are {(own.Count > 0 ? string.Join(", ", own) : "none")}; {TemplateLibrary.KeepColoursName} brings the icons in as Streamline drew them.";
            }

            var present = TemplateLibrary.Imported(group, hashes.Select(hash => StreamlinePanel.SourcePrefix + hash));
            var wanted = hashes.Where(hash => !present.Contains(StreamlinePanel.SourcePrefix + hash)).Distinct(StringComparer.Ordinal).ToList();

            // Before the key and the panel: nothing is to be downloaded, so neither is wanted.
            if (wanted.Count == 0)
            {
                return Report($"Nothing changed: {Named(group)} has every one already, as {There()}.", null);
            }

            if (_window.Streamline.Busy)
            {
                return "Icons are on their way in from the Streamline panel; try again once they have landed.";
            }

            if (_window.Streamline.Client is null)
            {
                return "Importing from Streamline needs an API key, which the person sets in Studio's Settings.";
            }

            var notes = new List<string>();
            var label = Label("import from Streamline");
            var added = await _window.Streamline.ImportAsync(wanted, group, template, notes, label).ConfigureAwait(true);

            if (added.Count == 0)
            {
                return notes.Count > 0 ? Cut(notes, null) : "No icon could be imported.";
            }

            Undoable(label, node: group);

            // Paths read now: the downloads gave up the UI thread, and a call meanwhile can have renumbered the nodes.
            var reply = $"Imported {added.Count} icon{(added.Count == 1 ? null : "s")} into {Named(group)}: {string.Join(", ", added.Select(Named))}." + Undoes
                        + (present.Count > 0 ? $"\nAlready there, so not downloaded: {There()}." : null)
                        + (notes.Count > 0 ? "\n" + Cut(notes, null) : null);

            await _window.ShowAsync(added[^1]).ConfigureAwait(true);

            return Report(reply, "import from Streamline");

            string There() => string.Join(", ", group.Drawings.Where(drawing => drawing.Source is { } source && present.Contains(source)).Select(Named));
        });

    private Task<string> Move(
        [Description("The node path of the group or drawing to move.")] string node,
        [Description("The node path of the group to move it into; empty for the project itself.")] string into)
        => Ui(() =>
        {
            if (Resolve(node) is not { } moving)
            {
                return NoNode(node);
            }

            if (Resolve(into) is not ProjectGroup group)
            {
                return NotA("group", into);
            }

            if (ReferenceEquals(moving.Parent, group))
            {
                return Report("Nothing changed; it is already in that group.", null);
            }

            var label = Label($"move {ProjectWorkspace.Label(moving)}");

            if (!_window.Move(moving, group, label))
            {
                return "It did not move: that would put a group inside itself.";
            }

            Undoable(label);

            return Report($"Moved; it is now {Path(moving)}." + Undoes, "move");
        });

    private Task<string> SetNode(
        [Description("The node path of the group or drawing; empty for the project itself.")] string node,
        [Description("A group or drawing takes name, namespace, class, padding, width, height, scale, x and y; the project takes namespace, class, padding, width, height, scale, cache, helperScope and skiaSharp.")] string setting,
        [Description("The new value; left out or empty, the setting is cleared and inherited again.")] string? value = null)
        => Ui(() =>
        {
            if (Resolve(node) is not { } found || _window.Workspace is not { } workspace)
            {
                return NoNode(node);
            }

            // Only what the reader takes back, since Validate never sees the node a value is for.
            if (!ProjectDocument.Settings(found).Contains(setting, StringComparer.Ordinal))
            {
                return $"{Path(found)} has no setting {setting}; {(found is ProjectRoot ? "the project" : "a group or drawing")} takes {string.Join(", ", ProjectDocument.Settings(found))}.";
            }

            var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

            try
            {
                GroupPanel.Validate(setting, text);
            }
            catch (SvgcProjectException refusal)
            {
                return refusal.Message;
            }

            if (_window.Pending(found) is { } holder)
            {
                return $"{Named(found)} has settings typed in the tab of {Named(holder)}, not yet in the project, that saving would write over this; open that tab and save first.";
            }

            // Written and put back, since Write spells a value as the file does and can take a partner
            // with it: 24.0 for a width of 24 is no step, and the same width beside a scale is one.
            var was = found.Element.Attributes().Select(attribute => new XAttribute(attribute)).ToList();

            GroupPanel.Write(found, setting, text);

            var same = string.Concat(found.Element.Attributes()) == string.Concat(was);

            found.Element.ReplaceAttributes(was);

            if (same)
            {
                return Report("Nothing changed; it already says that.", null);
            }

            var label = Label($"change {setting}");

            // Write can take a size's or a place's partners with it, which a client that reads nothing again would not know.
            var given = ProjectDocument.Settings(found).Where(name => name != setting && found.Setting(name) is { }).ToList();

            workspace.Do(label, () => ProjectSnapshot.Attributes(found), () => GroupPanel.Write(found, setting, text));
            Undoable(label);

            var cleared = given.Where(name => found.Setting(name) is null).ToList();

            return Report(
                $"Set {setting} on {Path(found)}." + (cleared.Count > 0 ? $" It also cleared {string.Join(" and ", cleared)}." : null) + Undoes,
                $"change {setting}");
        });

    private Task<string> Save(
        [Description("Where to write a project never saved, ending .svgstudio, or the drawing in front when it is of no project, ending .svg; left out, each is saved to its own file.")] string? path = null)
        => UiAsync(async () =>
        {
            if (_window.Workspace is null && _window.FrontViewer is null)
            {
                return "Nothing is open.";
            }

            string? target = null;

            if (!string.IsNullOrEmpty(path) && (target = Located(path, existing: false, out var why)) is null)
            {
                return why;
            }

            // A save that would have to ask where opens a picker, which a client cannot answer. A tab of
            // no project's saves to its own file, whether or not the project has one.
            var shown = Front();
            var alone = shown.Alone;

            if (alone is { })
            {
                if (target is null && alone.DocumentPath is null)
                {
                    return "The drawing in front has no file; give save a path ending .svg to write it there.";
                }

                if (target is { } && !Ends(target, ".svg"))
                {
                    return "A drawing of no project's saves as SVG; give a path ending .svg.";
                }
            }
            else if (_window.Workspace?.Document.Path is { } file)
            {
                if (target is { })
                {
                    return $"The project is saved at {file} already, and Studio has no Save As for a project; save without a path writes it there.";
                }

                // WriteAsync asks too, but an unedited project never reaches it and would answer "Saved."
                // over a file holding the conflict markers.
                if (await _window.Changes.Conflicted().ConfigureAwait(true))
                {
                    return "The project is in conflict, part way through a merge or a rebase; finish it, or abort it, before saving.";
                }
            }
            else if (target is null)
            {
                return "The project has never been saved; give save a path ending .svgstudio to write it there.";
            }
            else if (!Ends(target, ".svgstudio"))
            {
                return "A project saves to a path ending .svgstudio.";
            }

            if (!await Confirm(target is { } ? $"Save it as {target}?" : "Save the project?").ConfigureAwait(true))
            {
                return "The person said no; nothing was saved.";
            }

            // A project the person saved meanwhile is written where it is, whatever path is given.
            if (Front() != shown || target is { } && alone is null && _window.Workspace?.Document.Path is { })
            {
                return Changed("Nothing was saved");
            }

            var replacing = target is { } && File.Exists(target);
            var moving = alone is { } && target is { };
            var notes = new List<string>();

            if (!await (moving ? _window.SaveAsAsync(target!, notes) : _window.SaveAsync(notes, target)).ConfigureAwait(true))
            {
                return string.Join("\n", notes.DefaultIfEmpty("Nothing was saved."));
            }

            var saved = target is null ? "Saved" : $"Saved as {target}" + (replacing ? ", replacing the file there" : null);

            // Moving the drawing writes it alone, so the project's tabs are no exception to it.
            return Report(
                string.Join("\n", notes.Prepend(
                    moving ? saved + ". Its tab saves there from now on, and its undo history started again."
                    : _window.HoldingEdits() is { Count: > 0 } held ? $"{saved}, except {string.Join(", ", held.Select(Named))}: a save takes only the front tab's edits, so open each and save again."
                    : saved + ".")),
                "save");
        });

    private Task<string> Remove([Description("The node path of the group or drawing to remove.")] string node) => UiAsync(async () =>
    {
        if (Resolve(node) is not { } found)
        {
            return NoNode(node);
        }

        if (found is ProjectRoot)
        {
            return "The project itself cannot be removed.";
        }

        // Removing closes the tabs under the node, and a tab holding edits would ask in a dialog,
        // which a client cannot answer; asked here, the answer is also the one to removing it.
        var held = _window.HoldingEdits(found);
        var (answer, changed) = await Choose("Nothing was removed", () => _window.HoldingEdits(found) is { Count: > 0 } now
            ? new Question($"Removing {ProjectWorkspace.Label(found)} closes the {Tabs(now)}, which {(now.Count == 1 ? "holds" : "hold")} edits not yet saved into the project, and throws them away.", s_throw)
            : null).ConfigureAwait(true);

        if (changed is { })
        {
            return changed;
        }

        if (held.Count > 0)
        {
            if (answer is null)
            {
                return $"{Named(held[0])} has edits in its tab not yet saved into the project; open it and save them first.";
            }

            if (answer.Choice != s_throw[0])
            {
                return $"Nothing was removed: the person kept the edits held in the {Tabs(held)}.";
            }

            foreach (var holding in held)
            {
                _window.Discard(holding);
            }
        }
        else if (!await Confirm($"Remove {ProjectWorkspace.Label(found)} from the project?").ConfigureAwait(true))
        {
            return "The person said no; nothing was removed.";
        }

        // Named while they are still where they were.
        var thrown = held.Count > 0 ? $", throwing away the edits held in the {Tabs(held)}" : null;
        var label = Label($"remove {ProjectWorkspace.Label(found)}");

        if (!await _window.RemoveAsync(found, label).ConfigureAwait(true))
        {
            return "It was not removed.";
        }

        Undoable(label, node: found);

        return Report($"Removed {ProjectWorkspace.Label(found)}{thrown}." + Undoes, "remove");

        static string Tabs(IReadOnlyList<ProjectNode> nodes) => $"{(nodes.Count == 1 ? "tab" : "tabs")} of {string.Join(", ", nodes.Select(Named))}";
    });

    private Task<string> GitCommit([Description("The commit message.")] string message) => UiAsync(async () =>
    {
        if (Repository(out var why) is null)
        {
            return why;
        }

        // The commit saves the front tab's edits first, and no other tab's.
        if (_window.HoldingEdits().Where(held => !ReferenceEquals(held, _window.FrontNode)).ToList() is { Count: > 0 } behind)
        {
            return $"Nothing was committed: the tabs of {string.Join(", ", behind.Select(Named))} hold edits a commit would leave out; open each and save, then commit.";
        }

        if (!await Confirm($"Save and commit the project as \"{message}\"?").ConfigureAwait(true))
        {
            return "The person said no; nothing was committed.";
        }

        var notes = new List<string>();

        return await _window.Changes.Commit(message, notes).ConfigureAwait(true)
            ? Report("Committed.", "commit")
            : string.Join("\n", notes.DefaultIfEmpty("Nothing was committed."));
    });

    /// <remarks>
    /// Compared as the Changes pane compares, so a rename reads as one removed and one added; but
    /// against what git has now, where the pane's HEAD is as old as the window's last time in front.
    /// </remarks>
    private Task<string> GetChanges(
        [Description("A commit, branch or tag to compare with, such as HEAD~1 or a hash from git_history; empty for the last commit.")] string? since = null)
        => UiAsync(async () =>
        {
            var asked = _window.Workspace;

            if (Repository(out var why) is not { } git)
            {
                return why;
            }

            var latest = string.IsNullOrEmpty(since);
            var revision = latest ? "HEAD" : since!;
            (var then, why) = await Committed(revision).ConfigureAwait(true);

            if (then is null)
            {
                // Before the first commit HEAD names nothing, which leaves all of it new rather than unreadable.
                return latest && await git.Head().ConfigureAwait(true) is null ? $"No commit has {git.Name} yet, so all of it is new." : why;
            }

            // The project Committed found still open once git answered.
            var now = asked!.Document;
            var changes = ProjectChanges.Compare(then, now).ToList();
            var templates = Templates(then.Root, now.Root);

            // No node holds the templates, so Compare has no row for them where the project's own settings are the same.
            if (templates is { } && changes.FirstOrDefault()?.After is not ProjectRoot)
            {
                changes.Insert(0, new ProjectChange(string.Empty, ProjectChangeKind.Settings, then.Root, now.Root));
            }

            var held = _window.HoldingEdits();
            var footer = (held.Count > 0 ? $"Not in it: edits held in the tabs of {string.Join(", ", held.Select(Named))}, until each is saved into the project.\n" : null)
                         + $"get_drawing with revision \"{revision}\" reads a drawing as git has it there.";

            if (changes.Count == 0)
            {
                return $"Nothing has changed since {revision}.\n{footer}";
            }

            var rows = changes.Select(Row).ToList();

            // After the rows, as it names no node: the header's hash alone.
            var head = latest ? await git.Head().ConfigureAwait(true) : null;
            var counts = changes.GroupBy(change => change.Kind).OrderBy(kind => kind.Key)
                .Select(kind => $"{kind.Count()} {(kind.Key == ProjectChangeKind.Settings ? "with new settings" : kind.Key.ToString().ToLowerInvariant())}");
            var header = $"Since {revision}{(head is { } ? $" ({head[..10]})" : null)}, edits not yet saved included: "
                         + $"{changes.Count} change{(changes.Count == 1 ? null : "s")}: {string.Join(", ", counts)}.\n";

            return header + Fit(rows, "the counts above take in every one", header.Length + footer.Length) + "\n" + footer;

            string Row(ProjectChange change)
            {
                var node = change.After ?? change.Before!;
                var row = new StringBuilder(change.Kind.ToString().ToLowerInvariant()).Append(' ').Append(node is ProjectRoot
                    ? "(project)"
                    : $"{(change.After is { } ? Path(change.After) : change.Key)} {(node is ProjectGroup ? "group" : "drawing")} {ProjectWorkspace.Label(node)}");
                var differs = Differs(change.Before, change.After).Append(node is ProjectRoot ? templates : null).OfType<string>().ToList();

                if (change.Kind == ProjectChangeKind.Moved && differs.Count == 0)
                {
                    differs.Add("moved among the rows beside it");
                }

                return (differs.Count > 0 ? row.Append(": ").AppendJoin("; ", differs) : row).ToString();
            }
        });

    /// <summary>What differs between two versions of a node, an entry for each of <see cref="ProjectChanges.Aspects"/>; nothing where either is missing.</summary>
    private static IEnumerable<string> Differs(ProjectNode? before, ProjectNode? after)
    {
        if (before is null || after is null)
        {
            yield break;
        }

        var (was, now) = (ProjectChanges.Aspects(before), ProjectChanges.Aspects(after));

        foreach (var aspect in was.Keys.Union(now.Keys))
        {
            var (old, @new) = (ProjectChanges.Value(was, aspect), ProjectChanges.Value(now, aspect));

            if (old == @new)
            {
                continue;
            }

            yield return aspect switch
            {
                ProjectChanges.SvgAspect => "its drawing changed",
                ProjectChanges.CodeAspect => Declarations((ProjectGroup)before, (ProjectGroup)after),
                ProjectChanges.PlaceAspect => $"its place {old ?? "unset"} → {@new ?? "unset"}",
                _ => $"{aspect} {Quoted(old)} → {Quoted(@new)}"
            };
        }

        static string Quoted(string? value) => value is null ? "unset" : $"\"{(value.Length > 60 ? value[..60] + "…" : value)}\"";

        static string Declarations(ProjectGroup before, ProjectGroup after)
        {
            var adding = Names(after).Except(Names(before)).ToList();
            var dropping = Names(before).Except(Names(after)).ToList();

            return "its declarations changed"
                   + (adding.Count > 0 ? ", adding " + string.Join(", ", adding) : null)
                   + (dropping.Count > 0 ? (adding.Count > 0 ? " and" : ",") + " dropping " + string.Join(", ", dropping) : null);
        }
    }

    /// <summary>How the project's templates differ between two versions, by name, or null where they do not, compared as a drawing's text is.</summary>
    private static string? Templates(ProjectRoot before, ProjectRoot after)
    {
        var was = before.Templates.Select(one => (one.Name, Text: ProjectChanges.Dedented(one.Text))).ToList();
        var now = after.Templates.Select(one => (one.Name, Text: ProjectChanges.Dedented(one.Text))).ToList();

        if (was.SequenceEqual(now))
        {
            return null;
        }

        var (had, has) = (was.Select(one => one.Name).ToList(), now.Select(one => one.Name).ToList());
        var parts = new[]
        {
            Part("adding", has.Except(had)),
            Part("dropping", had.Except(has)),
            Part("editing", now.Where(one => !was.Contains(one) && had.Contains(one.Name)).Select(one => one.Name).Distinct())
        }.OfType<string>().ToList();

        return "its templates changed" + (parts.Count > 0 ? ", " + string.Join("; ", parts) : null);

        static string? Part(string verb, IEnumerable<string> names)
            => names.ToList() is { Count: > 0 } some ? $"{verb} {string.Join(", ", some)}" : null;
    }

    private Task<string> GitHistory([Description("How many to list, from 1 to 100.")] int count = 20) => UiAsync(async () =>
    {
        if (count is < 1 or > 100)
        {
            return "count is from 1 to 100.";
        }

        if (Repository(out var why) is not { } git)
        {
            return why;
        }

        var commits = await git.History(count).ConfigureAwait(true);

        if (commits.Count == 0)
        {
            return $"No commit has touched {git.Name} yet.";
        }

        var header = $"The last {(commits.Count == 1 ? "commit" : $"{commits.Count} commits")} of {git.Name}, newest first:\n";
        var rows = commits
            .Select(commit => $"{commit.Hash[..10]} {commit.When.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} {commit.Author}: {commit.Subject}")
            .ToList();

        return header + Fit(rows, "git log in a terminal lists the rest", header.Length);
    });

    /// <summary>The repository the open project is in, or null and why there is none to ask.</summary>
    private ProjectGit? Repository(out string why)
    {
        why = _window.Workspace is not { } workspace ? "No project is open."
            : workspace.Document.Path is null ? "The project has never been saved, so it is in no git repository."
            : ProjectGit.Installed is null ? "Studio found no git on this machine."
            : _window.Changes.Git is null ? "The project is not in a git repository."
            : string.Empty;

        return why.Length == 0 ? _window.Changes.Git : null;
    }

    /// <summary>The project as git has it at <paramref name="revision"/>, or null and why not.</summary>
    /// <remarks>
    /// Read from git itself and never through the Changes pane, whose refresh can offer the merge
    /// window, which no client can answer. Awaiting git lets other calls run, so a caller awaits
    /// nothing before this, and resolves its nodes only once this has answered.
    /// </remarks>
    private async Task<(ProjectDocument? Project, string Why)> Committed(string revision)
    {
        var asked = _window.Workspace;

        if (Repository(out var why) is not { } git)
        {
            return (null, why);
        }

        // Git would take one starting with - for an option.
        if (string.IsNullOrWhiteSpace(revision) || revision.StartsWith('-'))
        {
            return (null, $"'{revision}' is not a revision; name a commit, a branch or a tag, such as HEAD~1.");
        }

        var text = await git.At(revision).ConfigureAwait(true);

        // One of those calls can open another project, whose nodes would then be read against this one's revision.
        if (!ReferenceEquals(_window.Workspace, asked))
        {
            return (null, "Another project was opened while git was being read; ask again.");
        }

        if (text is null)
        {
            return (null, $"Git has no {git.Name} at {revision}.");
        }

        try
        {
            return (ProjectDocument.Parse(text, System.IO.Path.Combine(git.Directory, git.Name)), string.Empty);
        }
        catch (Exception failure) when (failure is SvgcProjectException or SvgRecipeException)
        {
            return (null, $"{git.Name} at {revision} does not read as a project: {failure.Message}");
        }
    }

    /// <summary><paramref name="now"/> as <paramref name="then"/> has it, paired as a merge pairs it, so through a move or a rename.</summary>
    /// <param name="current">The drawing's text as it stands, where it is held: in its tab when it has one.</param>
    /// <param name="said">Why there is none; or, where there is, what to say of it after the drawing's name.</param>
    private static ProjectDrawing? Was(ProjectDocument then, ProjectNode? now, string current, string revision, out string said)
    {
        if (now is not ProjectDrawing drawing)
        {
            said = "revision reads a drawing of the project's, and the drawing in front is in none.";

            return null;
        }

        var was = new ProjectMerge.Side(then, drawing.Owner).BaseOf.TryGetValue(drawing, out var node) ? node as ProjectDrawing : null;

        // The same as get_changes judges it, so a move to another depth since reads as no change.
        said = was is null ? $"{Named(drawing)} is not in the project at {revision}: it was added since, or renamed and changed."
            : $", as git has it at {revision}"
              + (was.Name == drawing.Name ? null : $", named {was.Name} there")
              + (ProjectChanges.Dedented(was.Text) == ProjectChanges.Dedented(current) ? ", its text the same as now" : null);

        return was;
    }

    /// <remarks>
    /// Repeatable, since a second writes the same file again; destructive, since it replaces one.
    /// A project's takes in the front tab's edits first, as the File menu's does, which leaves the
    /// project edited.
    /// </remarks>
    private Task<string> Export(
        [Description("The full path of the file to write, which replaces any file there; a project's ends .cs, and a drawing's .svg or .cs.")] string path)
        => UiAsync(async () =>
        {
            if (_window.Workspace is null && _window.FrontViewer is null)
            {
                return "Nothing is open.";
            }

            if (Located(path, existing: false, out var why) is not { } target)
            {
                return why;
            }

            if (_window.Workspace is { } && !SvgExport.IsCSharp(target))
            {
                return "A project exports as one C# file; give a path ending .cs.";
            }

            var shown = Front();

            if (shown is { Workspace: null, Alone: { } alone })
            {
                if (!SvgExport.IsCSharp(target) && !Ends(target, ".svg"))
                {
                    return "A drawing exports as SVG to a path ending .svg, or as C# to one ending .cs.";
                }

                if (alone.DocumentPath is { } own && Same(own, target))
                {
                    return "That is the drawing's own file, which save writes.";
                }

                if (alone.Document is null)
                {
                    return "The drawing in front has not loaded, so there is nothing to export.";
                }
            }

            if (!await Confirm($"Export to {target}?").ConfigureAwait(true))
            {
                return "The person said no; nothing was written.";
            }

            SvgTextLayout? layout = null;
            var remember = false;

            // As the File menu's export asks it, and worked out only where it can be asked; otherwise as the
            // person chose for good, or baked, as get_code builds it. The survey does not hold the window,
            // so what it read is read again before asking, and after.
            var surveying = _window.Workspace is { } && StudioSettings.RelaxedText == RelaxedTextAnswer.Ask && CanAsk();
            var surveyed = surveying ? _window.Surveyed() : default;
            var frozen = surveying ? await _window.FrozenAsync().ConfigureAwait(true) : Array.Empty<string>();

            if (frozen.Count > 0 && _window.Surveyed() != surveyed)
            {
                return Changed("Nothing was written");
            }

            var (answer, changed) = await Choose("Nothing was written", () => frozen.Count > 0 && _window.Surveyed() == surveyed ? new Question(MainWindow.Freezing(frozen), s_layouts) : null).ConfigureAwait(true);

            if (changed is { })
            {
                return changed;
            }

            if (answer is { Choice: null })
            {
                return "Nothing was written: the person did not say how to lay out the text it cannot write out.";
            }

            if (answer?.Choice is { } choice)
            {
                var at = Array.IndexOf(s_layouts, choice);

                (layout, remember) = (at % 2 == 1 ? SvgTextLayout.Relaxed : SvgTextLayout.Baked, at >= 2);
            }

            if (Front() != shown)
            {
                return Changed("Nothing was written");
            }

            // Read once asked: the person can bring another of the project's tabs forward before answering.
            var front = _window.FrontNode;
            var held = _window.HoldingEdits();
            var replacing = File.Exists(target);
            var notes = new List<string>();

            if (remember)
            {
                StudioSettings.RelaxedText = layout == SvgTextLayout.Relaxed ? RelaxedTextAnswer.Always : RelaxedTextAnswer.Never;
            }

            if (!await _window.ExportAsync(target, notes, layout).ConfigureAwait(true))
            {
                return string.Join("\n", notes.DefaultIfEmpty("Nothing was written."));
            }

            var what = _window.Workspace is { } ? "every drawing of the project as one C# file"
                : SvgExport.IsCSharp(target) ? $"the drawing in front as C#, class {SvgExport.Namespace}.{SvgExport.ClassName(target)}"
                : "the drawing in front as SVG";
            var behind = held.Where(node => !ReferenceEquals(node, front)).ToList();

            return Report(
                $"Wrote {target}{(replacing ? ", replacing the file there" : null)}: {what}."
                + (notes.Count > 0 ? "\n" + Cut(notes, null) : null)
                + (front is { } && held.Contains(front) ? $"\nIt took in the edits held in the tab of {Named(front)}, as Export does; the project holds them unsaved." : null)
                + (behind.Count > 0 ? $"\nLeft out: the edits held in the tabs of {string.Join(", ", behind.Select(Named))}; open each and save, then export again." : null),
                "export");
        });

    private Task<string> NewProject() => UiAsync(async () =>
    {
        var was = _window.Workspace;
        var notes = new List<string>();
        var (agreed, kept) = await Losing($"Starting a new project closes {was?.Name}", null, "No new project was opened").ConfigureAwait(true);

        if (kept is { })
        {
            return kept;
        }

        if (!await _window.NewProjectAsync(notes, agreed).ConfigureAwait(true))
        {
            return string.Join("\n", notes.DefaultIfEmpty("No new project was opened."));
        }

        return Report(
            "Opened a new project, never saved; add_group and add_drawing fill it, and save with a path ending .svgstudio writes it."
            + (was is { } ? $" {was.Name} was closed, with its tabs." : null),
            "new project");
    });

    private Task<string> OpenFile([Description("The file's full path.")] string path) => UiAsync(async () =>
    {
        if (Located(path, existing: true, out var why) is not { } file)
        {
            return why;
        }

        var was = _window.Workspace;

        if (was?.Document.Path is { } open && Same(open, file))
        {
            return Report($"The project at {file} is open already; reload reads it again from its file.", null);
        }

        var notes = new List<string>();
        var (agreed, kept) = await Losing($"Opening {file} closes {was?.Name}", file, "Nothing was opened").ConfigureAwait(true);

        if (kept is { })
        {
            return kept;
        }

        if (!await _window.OpenAsync(new[] { file }, notes, agreed).ConfigureAwait(true))
        {
            return string.Join("\n", notes.DefaultIfEmpty("Nothing was opened."));
        }

        var opened = _window.Workspace;
        var text = ReferenceEquals(opened, was) ? $"Opened {file} in a tab of its own, not part of a project."
            : opened is { Document.Path: null } ? $"Converted {System.IO.Path.GetFileName(file)} into a new project, held unsaved; save with a path ending .svgstudio writes it, and get_project lists it."
            : $"Opened the project {opened!.Name} at {file}; get_project lists it.";

        return Report(
            text + (was is { } && !ReferenceEquals(opened, was) ? $" {was.Name} was closed, with its tabs." : null) + (notes.Count > 0 ? "\n" + Cut(notes, null) : null),
            "open file");
    });

    /// <remarks>
    /// Repeatable: a second reads what the first did, and changes nothing. Tabs are opened again by
    /// path and name, since the nodes they were on are the old document's.
    /// </remarks>
    private Task<string> Reload() => UiAsync(async () =>
    {
        if (_window.FrontNode is null && _window.FrontViewer is { } alone)
        {
            // A client reloading after git changed the project would otherwise take it for read.
            var besides = _window.Workspace is { } open ? $" The project {open.Name} was not read again: open brings a tab of it forward, and reload then reads it." : null;

            if (alone.DocumentPath is not { } drawing)
            {
                return "The drawing in front has no file to read again." + besides;
            }

            var (answer, changed) = await Choose("Nothing was reloaded", () => ReferenceEquals(_window.FrontViewer, alone) && alone.IsSourceModified
                ? new Question($"Reloading {drawing} reads it again from its file, throwing away the edits its tab holds, not saved.", s_discard)
                : null).ConfigureAwait(true);

            if (changed is { })
            {
                return changed;
            }

            if (alone.IsSourceModified && answer is null)
            {
                return "Nothing was reloaded: the drawing in front holds edits not saved. Saving them writes Studio's copy over what changed on disk; take them back first, or close its tab unsaved in Studio.";
            }

            if (answer is { } && answer.Choice != s_discard[0])
            {
                return "Nothing was reloaded: the person kept the changes not saved.";
            }

            if (File.Exists(drawing) && string.Equals(File.ReadAllText(drawing), alone.Source, StringComparison.Ordinal))
            {
                return Report($"Nothing changed: {drawing} holds what Studio has.{besides}", null);
            }

            var faults = new List<string>();

            return await _window.LoadAsync(alone, drawing, faults).ConfigureAwait(true)
                ? Report($"Read {drawing} again. Its undo history started again.{besides}", "reload")
                : string.Join("\n", faults.Append("The tab still shows what it had."));
        }

        if (_window.Workspace is not { } workspace)
        {
            return "Nothing is open.";
        }

        if (workspace.Document.Path is not { } path)
        {
            return "The project has never been saved, so it has no file to read again.";
        }

        if (await _window.Changes.Conflicted().ConfigureAwait(true))
        {
            return "The project is part way through a merge or a rebase; finish it, or abort it, then reload.";
        }

        var held = (workspace.IsEdited ? new[] { "the project" } : Array.Empty<string>())
            .Concat(_window.HoldingEdits().Select(node => $"the tab of {Named(node)}"))
            .ToList();

        // Read here first, so a file that no longer parses is said, not taken for a merge to resolve, and
        // nobody is asked to throw work away for it.
        string read;

        try
        {
            read = ProjectDocument.Load(path).ToXml();
        }
        catch (Exception failure) when (failure is SvgcProjectException or SvgRecipeException or IOException or UnauthorizedAccessException)
        {
            return $"{path} no longer reads as a project: {failure.Message}";
        }

        var (agreed, kept) = await Losing($"Reloading {path} closes {workspace.Name} to read it again", null, "Nothing was reloaded").ConfigureAwait(true);

        if (kept is { })
        {
            return kept;
        }

        // Nobody is asked while icons are on their way in, and taking the edits back would not let the
        // reload through, so closing says why instead.
        if (held.Count > 0 && agreed is null && !_window.Streamline.Busy)
        {
            return $"Nothing was reloaded: changes not saved are held in {string.Join(", ", held)}. Saving them writes Studio's copy over what changed on disk; take them back first, or close the project unsaved in Studio.";
        }

        // Over what is held it is read again all the same: the person has said to throw it away, or closing says why not.
        if (held.Count == 0 && string.Equals(read, workspace.Document.ToXml(), StringComparison.Ordinal))
        {
            return Report($"Nothing changed: {path} holds what Studio has.", null);
        }

        var front = _window.FrontNode;
        var tabs = _window.OpenNodes()
            .OrderBy(node => ReferenceEquals(node, front))
            .Select(node => (At: Path(node), Label: ProjectWorkspace.Label(node), Group: node is ProjectGroup))
            .ToList();
        var notes = new List<string>();

        if (!await _window.OpenAsync(new[] { path }, notes, agreed).ConfigureAwait(true))
        {
            return string.Join("\n", notes.DefaultIfEmpty("Nothing was reloaded."));
        }

        var lost = new List<string>();

        foreach (var (at, label, group) in tabs)
        {
            if (Resolve(at) is { } node && node is ProjectGroup == group && ProjectWorkspace.Label(node) == label)
            {
                await _window.ShowAsync(node).ConfigureAwait(true);
            }
            else
            {
                lost.Add($"{at} {label}");
            }
        }

        return Report(
            $"Read {path} again; get_project lists it now."
            + (lost.Count > 0 ? $" The tabs of {string.Join(", ", lost)} were not opened again, as their paths now name other nodes or none." : null)
            + " Its undo history started again."
            + (notes.Count > 0 ? "\n" + Cut(notes, null) : null),
            "reload");
    });

    private Task<string> GetDeclarations(
        [Description("The group's node path from get_project; empty for the project itself.")] string? node = null)
        => Ui(() => Resolve(node) is ProjectGroup group ? group.CodeText : NotA("group", node));

    private Task<string> SetDeclarations(
        [Description("The complete new <e:code> block, as get_declarations gives it: params and lets, with xmlns:e declared.")] string code,
        [Description("A few words saying what this does, shown in the Edit menu.")] string summary,
        [Description("The group's node path from get_project; empty for the project itself.")] string? node = null)
        => Ui(() => Resolve(node) is not ProjectGroup group ? NotA("group", node) : Block(group, summary, source =>
        {
            if (SvgSourceDocument.Read(code, out var unreadable) is not { } given)
            {
                return unreadable;
            }

            if (given.Document.Root is not { } block || block.Name != XNamespace.Get(SvgExpressionDeclarations.Namespace) + "code")
            {
                return "Declarations have to be one <e:code> block, with xmlns:e declared on it.";
            }

            block.Remove();
            source.Document.Root!.ReplaceWith(block);

            return null;
        }));

    /// <summary>Makes one edit to a group's or the project's block, a rename carried through everything under it, and answers with the problems under it now.</summary>
    private string Block(ProjectGroup group, string summary, Func<SvgSourceDocument, string?> edit, SvgDeclarationRename? rename = null)
    {
        var was = group.CodeText;
        var label = Label(summary);

        // Through the group's own target, which refuses a name declared above it or below it already,
        // and a rename while a tab holds edits that saving would write back over it.
        var refusal = new GroupTarget(_window.Workspace!, group) { Held = held => _window.DrawingOf(held)?.Text }.Commit(label, edit, rename);

        if (refusal is { })
        {
            return refusal;
        }

        if (string.Equals(group.CodeText, was, StringComparison.Ordinal))
        {
            return Report("Nothing changed; the block already says that.", null);
        }

        Undoable(label, text: rename is { } ? group : null);

        var under = Under(group).ToList();
        var wrong = Listed(under, "get_problems lists them", out var clean, 5);

        return Report(
            "Done."
            + (rename is { } renamed ? $" Every drawing and group under it that named {renamed.From} now names {renamed.To}." : null)
            + Undoes
            + (clean == under.Count ? $" No problems in the {under.Count} drawing(s) under it." : $"\nProblems under it now:\n{wrong}"),
            summary);
    }

    /// <remarks>
    /// Repeatable: a rename asked for again finds its name gone and the new one declared, and goes on
    /// as the declaration it named, which then changes nothing.
    /// </remarks>
    private Task<string> Declare(
        [Description("The parameter's or let's name; to rename one, the name it has now.")] string name,
        [Description("A few words saying what this does, shown in the Edit menu.")] string summary,
        [Description("The node path of the drawing, the group or (project) declaring it; empty for the drawing in front.")] string? node = null,
        [Description("A new parameter's type, or a new let's to hold it to one; a parameter's type is not changed, but removed and declared again.")] ExprType? type = null,
        [Description("A parameter's default, written as an expression such as #ff0000, 2 or 'text'; empty clears it.")] string? @default = null,
        [Description("A parameter's lowest value; empty clears it.")] string? min = null,
        [Description("A parameter's highest value; empty clears it.")] string? max = null,
        [Description("How far a parameter's slider moves at a time; empty clears it.")] string? step = null,
        [Description("A let's expression, which may name what the declarations above it give.")] string? expression = null,
        [Description("A new name, which every use is renamed to as well.")] string? to = null)
        => Ui(() =>
        {
            var found = string.IsNullOrEmpty(node) ? null : Resolve(node);
            var group = found as ProjectGroup;
            ISvgViewerDeclarationTarget? target = null;

            if (!string.IsNullOrEmpty(node) && found is null)
            {
                return NoNode(node);
            }

            if (group is null && (target = Target(node, out var about)) is null)
            {
                return about;
            }

            var at = group ?? (ProjectNode?)Drawing(node);
            var own = SvgExpressionDeclarations.Parse(group?.CodeText ?? target!.Text, out _);
            var parameter = own.Parameters.FirstOrDefault(one => one.Name == name);
            var let = own.Lets.FirstOrDefault(one => one.Name == name);

            var holder = parameter is null && let is null && at is { } ? ProjectDeclarations.Chain(at).LastOrDefault(one => Names(one).Contains(name)) : null;

            to = string.IsNullOrWhiteSpace(to) || to == name ? null : to.Trim();

            if (to is { } && parameter is null && let is null && holder is null)
            {
                var renamed = to;

                parameter = own.Parameters.FirstOrDefault(one => one.Name == renamed);
                let = own.Lets.FirstOrDefault(one => one.Name == renamed);

                if (parameter is { } || let is { })
                {
                    (name, to) = (renamed, null);
                }
            }

            if (holder is { })
            {
                return $"'{name}' is declared by {Named(holder)}, which every drawing under it inherits; declare it there, with node {Path(holder)}.";
            }

            var bounds = @default ?? min ?? max ?? step;
            Func<SvgSourceDocument, string?> edit;

            if (parameter is { })
            {
                if (expression is { })
                {
                    return $"'{name}' is a parameter: it takes type, default, min, max and step, not expression.";
                }

                edit = source => SvgDeclarationEditor.Update(source, name, new SvgExpressionParameter(
                    to ?? name,
                    type ?? parameter.Type,
                    Kept(@default, parameter.DefaultExpression),
                    Kept(min, parameter.MinExpression),
                    Kept(max, parameter.MaxExpression),
                    Kept(step, parameter.StepExpression)));
            }
            else if (let is { })
            {
                // The type it was added with, given again, is a retry.
                if ((type is { } given && given != let.DeclaredType) || bounds is { })
                {
                    return $"'{name}' is a let: it takes expression, not type, default, min, max or step.";
                }

                edit = source => SvgDeclarationEditor.UpdateLet(source, name, to ?? name, expression ?? let.Expression);
            }
            else if (to is { })
            {
                return $"Nothing here declares '{name}' to rename.";
            }
            else if (expression is { })
            {
                if (bounds is { })
                {
                    return "A new let takes expression, and type to hold it to one, not default, min, max or step.";
                }

                edit = source => SvgDeclarationEditor.AddLet(source, name, expression, type);
            }
            else if (type is { } kind)
            {
                edit = source => SvgDeclarationEditor.Add(source, new SvgExpressionParameter(name, kind, Kept(@default, null), Kept(min, null), Kept(max, null), Kept(step, null)));
            }
            else
            {
                return "A new parameter needs a type, and a new let an expression.";
            }

            if (group is { })
            {
                return Block(group, summary, edit, to is { } ? new SvgDeclarationRename(name, to) : null);
            }

            return Edit(node, summary, source =>
            {
                // A tab's document has nothing from above to read a let against, and is put back after:
                // it outlives this edit, and the blocks above can change before its next.
                var inherited = source.Inherited;

                source.Inherited = at is ProjectDrawing drawing ? ProjectDeclarations.Declared(drawing) : null;

                try
                {
                    return edit(source);
                }
                finally
                {
                    source.Inherited = inherited;
                }
            });

            static string? Kept(string? given, string? was) => given is null ? was : given.Length == 0 ? null : given;
        });

    private Task<string> Find(
        [Description("What to look for: words in names, ids, values and text, in any case; and a declared name, matched whole, among uses.")] string what,
        [Description("Names, ids, values or uses, to look one way only; left out, all four.")] Sought? kind = null,
        [Description("A group's or drawing's node path to look only there; empty for the whole project, or for the drawing in front where no project is open.")] string? node = null)
        => Ui(() =>
        {
            if (string.IsNullOrWhiteSpace(what))
            {
                return "Say what to find.";
            }

            var rows = new List<string>();
            var found = 0;
            var where = string.Empty;

            if (_window.Workspace is null)
            {
                if (_window.FrontViewer is not { } viewer)
                {
                    return "Nothing is open.";
                }

                where = " in the drawing in front";
                Search(string.Empty, "The drawing in front", viewer.Source, block: false);
            }
            else if (Resolve(node) is not { } scope)
            {
                return NoNode(node);
            }
            else
            {
                Walk(scope);
            }

            if (rows.Count == 0)
            {
                return $"Nothing found for \"{what}\"{where}.";
            }

            var head = found == 0 ? $"Nothing found for \"{what}\"{where}, but not everything could be searched:\n" : $"{found} found for \"{what}\"{where}:\n";

            return head + Fit(rows, "find with node or kind narrows it", head.Length);

            bool Looks(Sought one) => kind is null || kind == one;

            bool Has(string text) => text.Contains(what, StringComparison.OrdinalIgnoreCase);

            void Found(string row)
            {
                rows.Add(row);
                found++;
            }

            void Walk(ProjectNode under)
            {
                if (under is not ProjectRoot && Looks(Sought.Names) && Has(ProjectWorkspace.Label(under)))
                {
                    Found($"{Describe(under, small: true)}: its name");
                }

                if (under is ProjectDrawing drawing)
                {
                    Search(Named(drawing) + " ", Named(drawing), Text(drawing), block: false);
                }
                else if (under is ProjectGroup group)
                {
                    var holder = group is ProjectRoot ? "(project)" : Describe(group, small: true);

                    if (group.Code is { })
                    {
                        Search($"{holder}: its block's ", holder, group.CodeText, block: true);
                    }

                    foreach (var child in group.Children)
                    {
                        Walk(child);
                    }
                }
            }

            // A block's declarations are named by what they declare, as nothing takes their keys.
            void Search(string prefix, string named, string text, bool block)
            {
                if (!Looks(Sought.Ids) && !Looks(Sought.Values) && !Looks(Sought.Uses))
                {
                    return;
                }

                if (SvgSourceDocument.Read(text, out var unreadable) is not { Document.Root: { } root } source)
                {
                    rows.Add($"{named}: does not read as XML, so it was not searched: {unreadable}");

                    return;
                }

                string? stopped = null;
                var users = Looks(Sought.Uses) ? SvgDeclarationEditor.Where(source, what.Trim(), out stopped).ToHashSet(StringComparer.Ordinal) : new HashSet<string>();

                foreach (var (key, element, _) in Keyed(root))
                {
                    if (block && key.Length == 0)
                    {
                        continue;
                    }

                    var place = block ? $"{element.Name.LocalName} {(string?)element.Attribute("name")}" : $"{(key.Length == 0 ? "(root)" : key)} <{SvgAttributeEditor.ElementName(element)}>";
                    var by = block ? ", " : " ";

                    if (!block && Looks(Sought.Ids) && element.Attribute("id")?.Value is { } id && Has(id))
                    {
                        Found($"{prefix}{place} #{id}");
                    }

                    if (Looks(Sought.Values))
                    {
                        // An id is the ids' to find, which would otherwise come up twice.
                        foreach (var attribute in SvgAttributeEditor.Attributes(element).Where(one => (block || one.Name != "id") && Has(one.Value)))
                        {
                            Found($"{prefix}{place}{by}{attribute.Name}=\"{Around(attribute.Value)}\"");
                        }

                        if (string.Concat(element.Nodes().OfType<XText>().Select(run => run.Value)).Trim() is { Length: > 0 } words && Has(words))
                        {
                            Found($"{prefix}{place}: \"{Around(words)}\"");
                        }
                    }

                    if (users.Contains(key))
                    {
                        Found($"{prefix}{place} uses {what.Trim()}");
                    }
                }

                if (stopped is { })
                {
                    rows.Add($"{named}: {stopped} Uses past it were not looked for.");
                }
            }

            // About a hundred characters around the match, as a path's data or an embedded image runs to thousands.
            string Around(string value)
            {
                if (value.Length <= 100)
                {
                    return value;
                }

                var start = Math.Clamp(value.IndexOf(what, StringComparison.OrdinalIgnoreCase) - 40, 0, value.Length - 100);

                return (start > 0 ? "…" : null) + value.Substring(start, 100) + (start + 100 < value.Length ? "…" : null);
            }
        });

    private Task<string> GetProblems(
        [Description("A group's or drawing's node path from get_project; empty for the whole project.")] string? node = null)
        => Ui(() =>
        {
            var found = Resolve(node);
            IReadOnlyCollection<ProjectDrawing>? drawings = found switch
            {
                ProjectGroup group => Under(group).ToList(),
                ProjectDrawing drawing => new[] { drawing },
                _ => null
            };

            if (drawings is null)
            {
                return NoNode(node);
            }

            var wrong = Listed(drawings, found is ProjectGroup ? "name a group or a drawing to narrow it" : null, out var clean);

            if (found is ProjectDrawing)
            {
                return wrong.Length > 0 ? wrong : "No problems.";
            }

            return (wrong.Length > 0 ? wrong + "\n" : string.Empty) + $"{clean} drawing(s) with no problems.";
        });

    /// <summary>A line for each problem in <paramref name="drawings"/>, cut as <see cref="Cut"/> cuts them, and how many drawings have none.</summary>
    private string Listed(IReadOnlyCollection<ProjectDrawing> drawings, string? narrower, out int clean, int most = Most)
    {
        var checks = drawings.Select(drawing => (Drawing: drawing, Problems: Problems(drawing, Text(drawing)))).ToList();

        clean = checks.Count(check => check.Problems.Count == 0);

        return Cut(checks.SelectMany(check => check.Problems.Select(problem => $"- {Named(check.Drawing)}: {problem}")).ToList(), narrower, most);
    }

    /// <summary>The first <paramref name="most"/> of <paramref name="lines"/>, and how many more, with what to call to narrow them where anything can.</summary>
    /// <remarks>Cut, because a broken group block is a problem again in every drawing under it, and a lost name once for every use.</remarks>
    private static string Cut(IReadOnlyCollection<string> lines, string? narrower, int most = Most)
        => string.Join("\n", lines.Take(most))
           + (lines.Count > most ? $"\n… and {lines.Count - most} more{(narrower is null ? null : "; " + narrower)}." : string.Empty);

    /// <summary><see cref="Cut"/> at as many of <paramref name="lines"/> as fit in an answer holding <paramref name="used"/> characters besides.</summary>
    private static string Fit(IReadOnlyList<string> lines, string narrower, int used)
    {
        var most = 0;

        while (most < lines.Count && (used += lines[most].Length + 1) <= Longest)
        {
            most++;
        }

        return Cut(lines, narrower, most);
    }

    /// <summary>The drawing as it stands: the buffer of a tab holding it, or the project's text.</summary>
    private string Text(ProjectDrawing drawing) => _window.DrawingOf(drawing)?.Text ?? drawing.Text;

    private static IEnumerable<ProjectDrawing> Under(ProjectGroup group)
        => group.Children.SelectMany(child => child switch
        {
            ProjectDrawing drawing => new[] { drawing },
            ProjectGroup inner => Under(inner),
            _ => Enumerable.Empty<ProjectDrawing>()
        });

    /// <summary>Makes one edit to a drawing, in its tab or else the project, and answers it as <see cref="Done"/> does.</summary>
    /// <param name="said">What else to say once it is done, read after the edit has run.</param>
    private string Edit(string? node, string summary, Func<SvgSourceDocument, string?> edit, Func<string>? said = null)
    {
        if (Target(node, out var about) is not { } target)
        {
            return about;
        }

        var before = target.Text;

        return target.Commit(Label(summary), edit) ?? Done(target, node, summary, before, said?.Invoke());
    }

    /// <summary>An edit's answer: done, and what is wrong with the drawing now, so the model need not read it again.</summary>
    /// <param name="before">The drawing's text before the edit; the same afterwards, the edit made no step to remember.</param>
    /// <param name="said">What else to say once it is done, such as where what it added is.</param>
    private string Done(ISvgViewerDeclarationTarget target, string? node, string summary, string before, string? said = null)
    {
        if (string.Equals(target.Text, before, StringComparison.Ordinal))
        {
            return Report("Nothing changed; it already says that.", null);
        }

        Undoable(Label(summary), target as SvgViewer, text: target is SvgViewer ? null : Drawing(node));

        var problems = Problems(Drawing(node), target.Text);

        return Report(
            "Done." + (said is null ? null : " " + said) + Undoes + (problems.Count == 0 ? " No problems." : $"\nProblems now:\n{Cut(problems.Select(problem => "- " + problem).ToList(), null)}"),
            summary);
    }

    /// <summary>
    /// Remembers the step on top of <paramref name="viewer"/>'s history, or else the project's, as this
    /// tool's for <see cref="Undo"/> when it is the one just made under <paramref name="label"/>; and
    /// ends what redo had to put back, as any new edit does.
    /// </summary>
    /// <param name="node">The node the step added or removed, if it did either.</param>
    /// <param name="text">The drawing whose text a step of the project's rewrote, or the group whose drawings a rename did, if it did.</param>
    private void Undoable(string label, SvgViewer? viewer = null, ProjectNode? node = null, ProjectNode? text = null)
    {
        _redos.Clear();

        var workspace = viewer is null ? _window.Workspace : null;

        if (Way(viewer, workspace, back: true) is ({ } step, var top, _, _) && top == label)
        {
            _undos.Push(new Made(label, step, viewer, workspace, node, text));
        }
    }

    /// <summary>The key as the tools spell the root in their own output.</summary>
    private static string Key(string? key) => key is null || key.Trim() == "(root)" ? string.Empty : key.Trim();

    /// <summary>What Studio finds wrong with a drawing, which it checks as built: with the blocks above it written in.</summary>
    /// <remarks>
    /// Checked on its own text, a drawing redeclaring a name its group declares came back clean and
    /// every name it inherits came back undeclared. Each problem is given with the line it is on,
    /// since an offset into the built text is nowhere in the drawing's own.
    /// </remarks>
    /// <param name="node">The drawing, or the group a drawing not yet added would go into.</param>
    private static List<string> Problems(ProjectNode? node, string text)
    {
        // Every inherited name, not only the ones the drawing reaches: an unknown name's "in
        // scope" list is then the names the drawing could have used.
        var built = node switch
        {
            ProjectDrawing drawing => ProjectDeclarations.Built(drawing, text, narrow: false),
            ProjectGroup group => ProjectDeclarations.Built(group, text, narrow: false),
            _ => text
        };

        return SvgSourceDiagnostics.Analyse(built)
            .Select(problem => $"{(problem.Severity == SvgSourceSeverity.Warning ? Warning : null)}{problem.Message} (at: {Tag(built, problem.Start)})")
            .ToList();
    }

    /// <summary>The tag the offset falls in, which is what names an element: a whole line of built text would carry the spliced blocks too.</summary>
    private static string Tag(string text, int at)
    {
        at = Math.Clamp(at, 0, Math.Max(text.Length - 1, 0));

        var start = text.LastIndexOf('<', at);
        var end = text.IndexOf('>', at);

        if (start < 0 || end < 0)
        {
            var line = text[(text.LastIndexOf('\n', at) + 1)..(text.IndexOf('\n', at) is var next and >= 0 ? next : text.Length)].Trim();

            return line.Length > 120 ? line[..120] + "…" : line;
        }

        var tag = text[start..(end + 1)];

        return tag.Length > 160 ? tag[..160] + "…" : tag;
    }

    private static IEnumerable<string> Names(ProjectGroup group)
        => group.Code?.Elements().Select(declaration => ((string?)declaration.Attribute("name"))?.Trim()).OfType<string>() ?? Enumerable.Empty<string>();

    /// <summary>A line for each block above <paramref name="node"/>, with the names it declares.</summary>
    private static string Inherits(ProjectNode node)
        => string.Concat(ProjectDeclarations.Chain(node).Select(holder => $"Inherited from {Path(holder)}: {string.Join(", ", Names(holder))}\n"));

    private static string? Declares(ProjectGroup group)
        => group.Code is { } ? " — declares: " + (string.Join(", ", Names(group)) is { Length: > 0 } names ? names : "nothing") : null;

    /// <summary>What the node sets itself, as the file spells it and set_node takes it back; its name is the row's already.</summary>
    private static string OwnSettings(ProjectNode node)
        => string.Concat(ProjectDocument.Settings(node).Where(name => name != "name").Select(name => node.Setting(name) is { } value ? $" {name}=\"{value}\"" : null));

    /// <summary>The project's drawing a node path names, or the one in front for none.</summary>
    private ProjectDrawing? Drawing(string? node)
        => string.IsNullOrEmpty(node) ? _window.FrontNode as ProjectDrawing : Resolve(node) as ProjectDrawing;

    /// <summary>Where an edit to a drawing goes: its tab when it has one, the project when it has none.</summary>
    private ISvgViewerDeclarationTarget? Target(string? node, out string about)
    {
        if (string.IsNullOrEmpty(node))
        {
            if (_window.FrontViewer is { } viewer)
            {
                about = _window.FrontNode is { } front ? $"Drawing {Describe(front, _small)}" : $"Drawing {viewer.DocumentPath ?? "Untitled"}";

                return viewer;
            }

            about = "The tab in front is not a drawing; name one by its node path.";

            return null;
        }

        if (Resolve(node) is not ProjectDrawing drawing || _window.Workspace is not { } workspace)
        {
            about = NotA("drawing", node);

            return null;
        }

        about = $"Drawing {Describe(drawing, _small)}";

        return _window.DrawingOf(drawing) ?? new DrawingTarget(workspace, drawing);
    }

    private ProjectNode? Resolve(string? path)
    {
        if (_window.Workspace is not { } workspace)
        {
            return null;
        }

        ProjectNode at = workspace.Document.Root;

        // The root as Path spells it, which the tools' answers name it by.
        foreach (var segment in (path?.Trim() == "(project)" ? string.Empty : path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (at is not ProjectGroup group
                || !int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                || index < 0
                || index >= group.Children.Count)
            {
                return null;
            }

            at = group.Children[index];
        }

        return at;
    }

    private static string Path(ProjectNode node)
    {
        var segments = new List<string>();

        for (var at = node; at.Parent is { } parent; at = parent)
        {
            var index = parent.Children.ToList().IndexOf(at);

            // A node an undo has taken out keeps its Parent and is in nobody's Children.
            if (index < 0)
            {
                return Gone;
            }

            segments.Add(index.ToString(CultureInfo.InvariantCulture));
        }

        segments.Reverse();

        return segments.Count == 0 ? "(project)" : string.Join("/", segments);
    }

    /// <param name="small">Whether the model has no save, and so is told nothing of what saving would write.</param>
    private string Describe(ProjectNode node, bool small)
        => $"{Path(node)} {(node is ProjectGroup ? "group" : "drawing")} {ProjectWorkspace.Label(node)}"
           + (small || !_window.HoldingEdits().Contains(node) ? string.Empty
               : ReferenceEquals(node, _window.FrontNode) ? ", with edits held in its tab that saving writes into the project"
               : ", with edits held in its tab; open it and save to write them into the project");

    private static string Named(ProjectNode node) => $"{Path(node)} {ProjectWorkspace.Label(node)}";

    /// <summary>Why a path finds nothing, which with no project open is never the path.</summary>
    private string NoNode(string? path)
        => _window.Workspace is null ? "No project is open." : $"There is no node '{path}'. get_project lists the paths.";

    /// <inheritdoc cref="NoNode"/>
    private string NotA(string kind, string? path)
        => _window.Workspace is null ? "No project is open." : $"'{path}' is not a {kind}. get_project lists the paths.";

    /// <summary>The file a tool was given a path to, or null and why that path will not do.</summary>
    /// <param name="existing">Whether the file has to be there already.</param>
    private static string? Located(string path, bool existing, out string why)
    {
        var full = System.IO.Path.IsPathFullyQualified(path) ? System.IO.Path.GetFullPath(path) : null;
        var folder = full is { } ? System.IO.Path.GetDirectoryName(full) : null;

        why = full is null ? $"'{path}' is not a full path; Studio has no folder it is relative to."
            : !Directory.Exists(folder) ? $"There is no folder {folder ?? full}."
            : existing && !File.Exists(full) ? $"There is no file at {full}."
            : string.Empty;

        return why.Length == 0 ? full : null;
    }

    private static bool Ends(string path, string extension) => System.IO.Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="path"/> names the file at <paramref name="full"/>, by case too only where the disk tells cases apart.</summary>
    private static bool Same(string path, string full)
        => string.Equals(System.IO.Path.GetFullPath(path), full, OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>What a tool checks before asking, and again once answered: the panel asks without holding the window, so the person can bring another tab forward first.</summary>
    private (ProjectWorkspace? Workspace, SvgViewer? Alone) Front() => (_window.Workspace, _window.FrontNode is null ? _window.FrontViewer : null);

    /// <summary>Asks the question <paramref name="build"/> makes of things as they are, where it makes one.</summary>
    /// <remarks>Made again once answered, for the reason <see cref="Front"/> is checked again.</remarks>
    /// <param name="nothing">What a refusal starts with, such as "Nothing was removed".</param>
    /// <returns>
    /// The answer; or none, where there is nothing to ask or nobody to ask it; or, where what it asked
    /// about changed while the person answered, the refusal to give, whatever they chose.
    /// </returns>
    private async Task<(Answer? Answer, string? Changed)> Choose(string nothing, Func<Question?> build)
    {
        if (build() is not { } question)
        {
            return (null, Answered() ? Changed(nothing) : null);
        }

        var answer = await Ask(question).ConfigureAwait(true);

        return answer is null || build()?.Key == question.Key ? (answer, null) : (null, Changed(nothing));
    }

    private static string Changed(string nothing) => $"{nothing}: what the person was asked about changed while they answered; call it again to go on from things as they are now.";

    /// <summary>Asks whether closing the project may throw away what is not saved, where it would.</summary>
    /// <param name="doing">What closes it, which starts the question.</param>
    /// <param name="opening">The file opened in its place, if one is.</param>
    /// <param name="nothing">What the answer to a person who keeps it starts with.</param>
    /// <returns>
    /// What the person agreed to throw away, as the window says it; or, where they kept it or what they
    /// were asked about changed, the answer to give; or neither, where nothing would be lost, nobody could
    /// be asked or icons are on their way in from Streamline, each of which the window refuses.
    /// </returns>
    private async Task<(string? Agreed, string? Kept)> Losing(string doing, string? opening, string nothing)
    {
        var (answer, changed) = await Choose(nothing, () => !_window.Streamline.Busy && _window.WouldLose(opening) is { } lost ? new Question($"{doing}, throwing away what is not saved. {lost}", s_discard) : null).ConfigureAwait(true);

        return changed is { } ? (null, changed)
            : answer is null ? (null, null)
            : answer.Choice == s_discard[0] ? (_window.WouldLose(opening), null)
            : (null, $"{nothing}: the person kept the changes not saved.");
    }

    private string Label(string summary) => $"{Who}: {summary.Trim()}";

    private string Report(string result, string? summary)
    {
        Called?.Invoke(this, summary);

        return result;
    }

    /// <summary>A tool whose arguments are read before it runs, and which does nothing while the window waits on the person.</summary>
    /// <remarks>
    /// The factory's own binding hands a null to a parameter taking none, which then threw from inside
    /// the tool, and says of a value that would not convert only the .NET type it wanted. A dialog's
    /// answer applies to what it described, so an edit made under it was lost unnamed; checked on
    /// the UI thread with the call, so none can open between the two.
    /// </remarks>
    private sealed class Checked : DelegatingAIFunction
    {
        /// <summary>The window whose questions hold the tool back, or null for a tool that only reads.</summary>
        private readonly MainWindow? _window;

        public Checked(AIFunction inner, MainWindow? window)
            : base(inner)
        {
            _window = window;
        }

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, System.Threading.CancellationToken cancellationToken)
        {
            foreach (var parameter in UnderlyingMethod!.GetParameters())
            {
                if (parameter.Name is not { } name
                    || !arguments.TryGetValue(name, out var value)
                    || !JsonSchema.TryGetProperty("properties", out var properties)
                    || !properties.TryGetProperty(name, out var schema))
                {
                    continue;
                }

                if (value is null or JsonElement { ValueKind: JsonValueKind.Null })
                {
                    if (!(schema.TryGetProperty("type", out var types) && types.ValueKind == JsonValueKind.Array && types.EnumerateArray().Any(type => type.GetString() == "null")))
                    {
                        throw new ArgumentException($"'{name}' cannot be null: it takes {Kind(schema)}.");
                    }
                }
                else if (value is JsonElement json)
                {
                    try
                    {
                        _ = json.Deserialize(parameter.ParameterType, s_arguments);
                    }
                    catch (JsonException failure)
                    {
                        throw new ArgumentException($"'{name}' takes {Kind(schema)}"
                            + (failure.Path is { Length: > 1 } at ? $", and {at[1..]} in what was given does not read as its schema says." : $", not {json.GetRawText()}."));
                    }
                }
            }

            if (_window is not { } window)
            {
                return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            }

            Task<object?> Run() => window.Asking is { } question
                ? Task.FromResult<object?>(JsonSerializer.SerializeToElement(
                    $"Nothing was done: Studio is waiting on the person to answer {(question.Title is { Length: > 0 } title ? $"\"{title}\"" : "a question")} in its window, and takes no edit until they have; ask them to, then try again.",
                    s_arguments))
                : base.InvokeCoreAsync(arguments, cancellationToken).AsTask();

            return await (Dispatcher.UIThread.CheckAccess() ? Run() : Dispatcher.UIThread.InvokeAsync(Run)).ConfigureAwait(false);
        }

        /// <summary>What a parameter takes, as its schema tells the model.</summary>
        private static string Kind(JsonElement schema)
        {
            if (schema.TryGetProperty("enum", out var values))
            {
                return "one of " + string.Join(", ", values.EnumerateArray());
            }

            var type = schema.TryGetProperty("type", out var types) ? types.ValueKind == JsonValueKind.Array ? types[0].GetString() : types.GetString() : null;

            return type switch
            {
                "integer" => "a whole number",
                "boolean" => "true or false",
                "array" or "object" => "an " + type,
                null => "a value",
                _ => "a " + type
            };
        }
    }

    /// <summary>Runs on the UI thread, where a function is called from whichever thread the model's reply arrived on.</summary>
    private static Task<T> Ui<T>(Func<T> call)
        => Dispatcher.UIThread.CheckAccess() ? Task.FromResult(call()) : Dispatcher.UIThread.InvokeAsync(call).GetTask();

    private static Task<T> UiAsync<T>(Func<Task<T>> call)
        => Dispatcher.UIThread.CheckAccess() ? call() : Dispatcher.UIThread.InvokeAsync(call);
}
