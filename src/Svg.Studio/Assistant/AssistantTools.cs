// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Threading;
using Microsoft.Extensions.AI;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions;
using Svg.Highlighting;
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

    private const string Warning = "Warning: ";

    private const string Gone = "(no longer in the project)";

    /// <summary>How long get_drawing's answer can grow before it leaves the source out, well under the 100,000 characters Claude Code cuts a tool's answer at.</summary>
    private const int Answer = 40_000;

    /// <summary>How many problems an answer lists before it says how many more.</summary>
    private const int Most = 50;

    /// <summary>Refuses an argument no parameter takes: dropped, a misspelled node fell back to the drawing in front, which took the edit.</summary>
    private static readonly JsonSerializerOptions s_arguments = new(AIJsonUtilities.DefaultOptions) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private readonly MainWindow _window;

    /// <summary>The edits this made, newest last, so undo takes back this one's and not the tab's.</summary>
    /// <remarks>
    /// The window's own undo asks the front tab first and the project second. An edit to a drawing
    /// in no tab lands on the project, so after one the window's undo took back whatever the front
    /// tab had done instead. Each edit is known by the step its history handed out: one the person
    /// has taken back, or one in a tab or a project since closed, is passed over, and one they have
    /// built on is refused, naming what is on top.
    /// </remarks>
    private readonly Stack<Made> _undos = new();

    /// <summary>One edit: its step, and the history holding it, the tab's or else the project's.</summary>
    private sealed record Made(string Label, object Step, SvgViewer? Viewer, ProjectWorkspace? Workspace);

    /// <summary>Whether <see cref="For"/> last handed out the small set, which has no open or save to act on what a tab holds.</summary>
    /// <remarks><see cref="Context"/> is told instead: a session asks for it before the tools, so after a change of model this is still the last one's.</remarks>
    private bool _small;

    public AssistantTools(MainWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    /// <summary>The tools that read and change nothing, for a client to call without asking.</summary>
    public static readonly IReadOnlySet<string> Reading = new HashSet<string>(StringComparer.Ordinal)
    {
        "read_doc", "get_project", "get_drawing", "get_element", "get_context", "get_declarations", "get_problems"
    };

    /// <summary>The tools that write past the history - to disk, to git, or a node away - which a client should ask about.</summary>
    public static readonly IReadOnlySet<string> Confirming = new HashSet<string>(StringComparer.Ordinal)
    {
        "save", "remove", "git_commit"
    };

    /// <summary>The tools that, called again with the same arguments, leave things as the first call did.</summary>
    public static readonly IReadOnlySet<string> Repeatable = new HashSet<string>(StringComparer.Ordinal)
    {
        "select", "open", "set_attributes", "set_text", "set_node", "set_declarations", "replace_drawing"
    };

    /// <summary>Who the Edit menu says made an edit: "Undo Assistant: make it red".</summary>
    public string Who { get; init; } = "Assistant";

    /// <summary>Asks the person before something that cannot be taken back with ⌘Z.</summary>
    public Func<string, Task<bool>> Confirm { get; set; } = _ => Task.FromResult(false);

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
        Add(GetDrawing, "get_drawing", "Reads a drawing: its elements with their address keys, any problems, and its SVG source.");
        Add(SetAttributes, "set_attributes", "Sets or removes attributes on one element of a drawing, as a single undo step.");
        Add(Undo, "undo", "Takes back the last edit these tools made, whichever drawing or the project it went to, and names it. Takes nothing back while another edit is on top of it in its tab or the project, and names that one; with no edit of these tools' left, takes back the person's last and names it.");

        if (small)
        {
            return tools;
        }

        Add(GetElement, "get_element", "Reads every attribute of one element of a drawing, and the text set_text changes, as the file writes them.");
        Add(GetProblems, "get_problems", "Lists every problem in a drawing, in the drawings under a group, or in the whole project: the one call that finds what is wrong.");
        Add(SetText, "set_text", "Sets the text between an element's tags - a <text>'s words or an <e:let>'s expression - as a single undo step.");
        Add(ReplaceDrawing, "replace_drawing", "Replaces a drawing's whole SVG source, as a single undo step. Prefer set_attributes for small changes.");
        Add(Select, "select", "Selects elements of the drawing in front, by address key, so the person sees them.");
        Add(Open, "open", "Opens a group or drawing of the project in its tab.");
        Add(AddGroup, "add_group", "Adds an empty group inside a group, and opens it.");
        Add(AddDrawing, "add_drawing", "Adds a drawing from SVG text inside a group, and opens it.");
        Add(Move, "move", "Moves a group or drawing into another group.");
        Add(GetDeclarations, "get_declarations", "Reads the declarations block a group, or the project, gives every drawing under it.");
        Add(SetDeclarations, "set_declarations", "Replaces the declarations block of a group, or of the project, as a single undo step.");
        Add(SetNode, "set_node", "Changes one setting of a group, a drawing or the project, as a single undo step. cache, helperScope and skiaSharp are the project's alone, and name, x and y a group's or drawing's; a width or height clears a scale, and a scale both.");
        Add(Save, "save", "Saves the project, or the drawing in front when it is a file of its own.");
        Add(Remove, "remove", "Removes a group or drawing from the project.");
        Add(GitCommit, "git_commit", "Saves and commits the project to its git repository.");

        return tools;

        void Add(Delegate tool, string name, string description) => tools.Add(AIFunctionFactory.Create(tool, name, description, s_arguments));
    }

    /// <summary>What is open, in a few lines, for the start of every message the person sends.</summary>
    /// <param name="small">
    /// Whether the model is one without the save tool, which is not told there is anything to save:
    /// a 3B on-device model answered every mention of it by telling the person to.
    /// </param>
    public string Context(bool small = false)
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
            text.Append("Git: ").Append(git).Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    [Description("Reads a section of the documentation.")]
    /// <remarks>An id it does not know is answered with the contents, so asking with none is how a model finds the ids.</remarks>
    private string ReadDoc([Description("The section id, as the contents lists it; empty to list the sections.")] string? id = null)
        => (string.IsNullOrWhiteSpace(id) ? null : AssistantDocs.Read(id))
           ?? $"{(string.IsNullOrWhiteSpace(id) ? "The" : $"There is no section {id}. The")} documentation's sections, by id:\n{AssistantDocs.Contents}";

    /// <summary><see cref="Context"/> on the UI thread, for a client that has no message to put it in.</summary>
    public Task<string> ContextAsync() => Ui(() => Context());

    private Task<string> GetProject() => Ui(() =>
    {
        if (_window.Workspace is not { } workspace)
        {
            return _window.FrontViewer is { } viewer
                ? $"No project is open. The front tab is the drawing {viewer.DocumentPath ?? "Untitled"}; leave node empty to work on it."
                : "Nothing is open.";
        }

        var text = new StringBuilder();
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

        Walk(workspace.Document.Root, 0);

        return text.ToString();

        void Walk(ProjectGroup group, int depth)
        {
            foreach (var child in group.Children)
            {
                text.Append(new string(' ', depth * 2)).Append(Path(child)).Append(' ')
                    .Append(child is ProjectGroup ? "group" : "drawing").Append(' ')
                    .Append(ProjectWorkspace.Label(child))
                    .Append(OwnSettings(child));

                if (_window.TabOf(child) is { })
                {
                    text.Append(held.Contains(child) ? " (open, holding unsaved edits)" : " (open)");
                }

                if (child is ProjectGroup holder && Declares(holder) is { } names)
                {
                    text.Append(names);
                }

                text.Append('\n');

                if (child is ProjectGroup inner)
                {
                    Walk(inner, depth + 1);
                }
            }
        }
    });

    private Task<string> GetDrawing(
        [Description("The drawing's node path from get_project; empty for the drawing in front.")] string? node = null)
        => Ui(() =>
        {
            if (Target(node, out var about) is not { } target)
            {
                return about;
            }

            var text = new StringBuilder(about).Append('\n');
            var source = target.Text;

            var root = SvgSourceDocument.Read(source, out var unreadable)?.Document.Root;

            if (root is { })
            {
                text.Append("Elements (address key, element, id or name):\n");
                Outline(root, string.Empty, 0);
            }
            else
            {
                text.Append("The source does not read as XML: ").Append(unreadable).Append('\n');
            }

            var drawing = Drawing(node);

            foreach (var holder in drawing is { } ? ProjectDeclarations.Chain(drawing) : Array.Empty<ProjectGroup>())
            {
                text.Append("Inherited from ").Append(Path(holder)).Append(": ").Append(string.Join(", ", Names(holder))).Append('\n');
            }

            if (Problems(drawing, source) is { Count: > 0 } problems)
            {
                text.Append(Cut(problems.Select(problem => "Problem: " + problem).ToList(), null)).Append('\n');
            }

            // Whole or not at all: replace_drawing takes the source back whole, and one cut short
            // and sent back would write the cut into the drawing.
            if (text.Length + source.Length > Answer)
            {
                text.Append("The source, ").Append(source.Length).Append(" characters, is too long for this answer and is left out. ").Append(root is { }
                    ? "get_element reads one element's attributes and text, and set_attributes and set_text change them; replace_drawing needs the whole source, so it cannot be used from this answer."
                    : "Until it reads, no tool can change it, replace_drawing included; the reason is above.");
            }
            else
            {
                text.Append("Source:\n").Append(source);
            }

            return text.ToString();

            void Outline(XElement element, string key, int depth)
            {
                text.Append(new string(' ', depth * 2)).Append(key.Length == 0 ? "(root)" : key).Append(' ')
                    .Append(SvgAttributeEditor.ElementName(element));

                if (element.Attribute("id")?.Value is { } id)
                {
                    text.Append(" #").Append(id);
                }
                else if (element.Attribute("name")?.Value is { } name)
                {
                    // Not "#", which is an id's here: no url(#…) finds a declaration by its name.
                    text.Append(" name=").Append(name);
                }

                text.Append('\n');

                var index = 0;

                foreach (var child in element.Elements())
                {
                    Outline(child, key.Length == 0 ? index.ToString(CultureInfo.InvariantCulture) : $"{key}/{index}", depth + 1);
                    index++;
                }
            }
        });

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
                var value = attribute.Value;

                // An embedded image is its base64, which no answer has room for; marked so that, copied back, it is plainly no URI.
                if (value.Length > 1_000 && value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    value = $"(left out: {value[..Math.Max(value.IndexOfAny(new[] { ';', ',' }), 5)]}, {value.Length} characters)";
                }

                text.Append(attribute.Name).Append('=').Append(value).Append('\n');
            }

            // What set_text changes, which get_drawing does not show where it leaves the source out.
            if (SvgAttributeEditor.Content(source, key, out _) is { } words)
            {
                text.Append("Text: ").Append(words).Append('\n');
            }

            return text.ToString();
        });

    private Task<string> SetAttributes(
        [Description("The element's address key from get_drawing; empty for the root svg element.")] string key,
        [Description("The attributes to write.")] AttributeChange[] attributes,
        [Description("A few words saying what this does, which the Edit menu shows after Undo.")] string summary,
        [Description("The drawing's node path; empty for the drawing in front.")] string? node = null)
        => Ui(() =>
        {
            if (Target(node, out var about) is not { } target)
            {
                return about;
            }

            key = Key(key);

            var before = target.Text;
            var refusal = target.Commit(Label(summary), source =>
            {
                foreach (var change in attributes)
                {
                    if (SvgAttributeEditor.SetAttribute(source, key, change.Name, string.IsNullOrEmpty(change.Value) ? null : change.Value) is { } refused)
                    {
                        return $"{change.Name}: {refused}";
                    }
                }

                return null;
            });

            return refusal ?? Done(target, node, summary, before);
        });

    private Task<string> SetText(
        [Description("The element's address key from get_drawing.")] string key,
        [Description("The new text between the element's tags; empty to leave it with none.")] string text,
        [Description("A few words saying what this does, shown in the Edit menu.")] string summary,
        [Description("The drawing's node path; empty for the drawing in front.")] string? node = null)
        => Ui(() =>
        {
            if (Target(node, out var about) is not { } target)
            {
                return about;
            }

            var before = target.Text;
            var refusal = target.Commit(Label(summary), source => SvgAttributeEditor.SetContent(source, Key(key), text));

            return refusal ?? Done(target, node, summary, before);
        });

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
            // project's, where the same text goes in as one step of the project's history. Only the
            // root is kept, so the same root again is no step, whatever surrounds it. It is read with
            // plain newlines, and the drawing's text ends its lines as the project file does.
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
                && read.TextOf(read.Document.Root!) != before.Replace("\r\n", "\n", StringComparison.Ordinal))
            {
                workspace.Do(Label(summary), () => ProjectSnapshot.Text(drawing), () => drawing.SetText(svg));
            }

            return Done(target, node, summary, before);
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

            return viewer.Elements.TrySelect(picked)
                ? Report((keys.Length == 0 ? "Selection cleared." : "Selected.") + (missing.Count > 0 ? $" Not elements of this drawing: {string.Join(", ", missing)}." : string.Empty), "select")
                : "Nothing changed.";
        });

    private Task<string> Undo() => Ui(() =>
    {
        if (_undos.Count == 0)
        {
            // Read first: once taken back, it is the redo's.
            var last = _window.FrontViewer is { CanUndo: true } front ? front.UndoLabel : _window.Workspace?.UndoLabel;

            return _window.UndoDocument() ? Report($"Took back \"{last}\".", "undo") : "There is nothing to take back.";
        }

        while (_undos.TryPeek(out var made))
        {
            var viewer = made.Viewer;
            var workspace = ReferenceEquals(made.Workspace, _window.Workspace) ? made.Workspace : null;
            var (held, top, on) = viewer is { }
                ? (viewer.Holds(made.Step), viewer.UndoStep, viewer.UndoLabel)
                : (workspace?.Holds(made.Step) == true, workspace?.UndoStep, workspace?.UndoLabel);

            if (!held)
            {
                _undos.Pop();
            }
            else if (!ReferenceEquals(top, made.Step))
            {
                return $"Nothing was taken back: \"{on}\" is on top of \"{made.Label}\" in {(viewer is { } ? "its tab's" : "the project's")} history, and only the step on top can be taken back.";
            }
            else
            {
                _undos.Pop();
                _ = viewer?.Undo() ?? workspace!.Undo();

                return Report($"Took back \"{made.Label}\".", "undo");
            }
        }

        return $"Nothing was taken back: every edit {Who} made has been taken back already, or was in a tab or a project since closed.";
    });

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

        await _window.AddGroupAsync(group, label).ConfigureAwait(true);
        Undoable(label);

        return Report($"Added the group {(_window.FrontNode is { } added ? Path(added) : "(unknown)")}; set_node renames it." + Undoes, "add group");
    });

    private Task<string> AddDrawing(
        [Description("The node path of the group to add it to; empty for the project itself.")] string parent,
        [Description("The drawing's name, which is also its class unless one is set.")] string name,
        [Description("The drawing's complete SVG source.")] string svg)
        => UiAsync(async () =>
        {
            if (Resolve(parent) is not ProjectGroup group)
            {
                return NotA("group", parent);
            }

            // Through the import every other drawing arrives by, with its notes said here: a dialog
            // in the window is one a client cannot close, and a call that opens one never returns.
            // Opened only once its step and path are read, since the load gives up the UI thread
            // and whatever ran meanwhile was taken for them.
            var notes = new List<string>();
            var label = Label($"add {name}");
            var added = await _window.ImportAsync(group, group.Children.Count, new[] { new TemplateImport(name, svg) }, show: false, notes: notes, label: label).ConfigureAwait(true);

            if (added.Count == 0)
            {
                return notes.Count > 0 ? string.Join("\n", notes) : "That drawing couldn't be added.";
            }

            Undoable(label);

            var reply = $"Added {name} as {Path(added[^1])}." + Undoes;

            await _window.ShowAsync(added[^1]).ConfigureAwait(true);

            return Report(reply, $"add {name}");
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

            var label = Label($"move {ProjectWorkspace.Label(moving)}");

            if (!_window.Move(moving, group, label))
            {
                return "It did not move: it is already there, or that would put a group inside itself.";
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

            // As the node's own panel tells a value typed back to what the file says.
            if (text == GroupPanel.Value(found, setting))
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

    private Task<string> Save() => UiAsync(async () =>
    {
        if (_window.Workspace is null && _window.FrontViewer is null)
        {
            return "Nothing is open.";
        }

        // A save that would have to ask where opens a picker, which a client cannot answer. A tab of
        // no project's saves to its own file, whether or not the project has one.
        if (_window.FrontNode is null && _window.FrontViewer is { } alone)
        {
            if (alone.DocumentPath is null)
            {
                return "The drawing in front has no file; save it once in Studio, which asks where.";
            }
        }
        else if (_window.Workspace is { Document.Path: null })
        {
            return "The project has never been saved, so there is nowhere to write it; save it once in Studio, which asks where.";
        }
        else if (await _window.Changes.Conflicted().ConfigureAwait(true))
        {
            // WriteAsync asks too, but an unedited project never reaches it and would answer "Saved."
            // over a file holding the conflict markers.
            return "The project is in conflict, part way through a merge or a rebase; finish it, or abort it, before saving.";
        }

        if (!await Confirm("Save the project?").ConfigureAwait(true))
        {
            return "The person said no; nothing was saved.";
        }

        var notes = new List<string>();

        if (!await _window.SaveAsync(notes).ConfigureAwait(true))
        {
            return string.Join("\n", notes.DefaultIfEmpty("Nothing was saved."));
        }

        return Report(
            _window.HoldingEdits() is { Count: > 0 } held
                ? $"Saved, except {string.Join(", ", held.Select(Named))}: a save takes only the front tab's edits, so open each and save again."
                : "Saved.",
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

        // Removing closes the tabs under the node, and a tab holding edits would ask whether to
        // keep them, which a client cannot answer.
        if (_window.HoldingEdits(found) is { } holding)
        {
            return $"{Named(holding)} has edits in its tab not yet saved into the project; open it and save them first.";
        }

        if (!await Confirm($"Remove {ProjectWorkspace.Label(found)} from the project?").ConfigureAwait(true))
        {
            return "The person said no; nothing was removed.";
        }

        var label = Label($"remove {ProjectWorkspace.Label(found)}");

        if (!await _window.RemoveAsync(found, label).ConfigureAwait(true))
        {
            return "It was not removed.";
        }

        Undoable(label);

        return Report($"Removed {ProjectWorkspace.Label(found)}." + Undoes, "remove");
    });

    private Task<string> GitCommit([Description("The commit message.")] string message) => UiAsync(async () =>
    {
        if (_window.Changes.Git is null)
        {
            return "The project is not in a git repository.";
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

    private Task<string> GetDeclarations(
        [Description("The group's node path from get_project; empty for the project itself.")] string? node = null)
        => Ui(() => Resolve(node) is ProjectGroup group ? group.CodeText : NotA("group", node));

    private Task<string> SetDeclarations(
        [Description("The complete new <e:code> block, as get_declarations gives it: params and lets, with xmlns:e declared.")] string code,
        [Description("A few words saying what this does, shown in the Edit menu.")] string summary,
        [Description("The group's node path from get_project; empty for the project itself.")] string? node = null)
        => Ui(() =>
        {
            if (Resolve(node) is not ProjectGroup group || _window.Workspace is not { } workspace)
            {
                return NotA("group", node);
            }

            var was = group.CodeText;
            var label = Label(summary);

            // Through the group's own target, which refuses a name declared above it or below it already.
            var refusal = new GroupTarget(workspace, group).Commit(label, source =>
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
            });

            if (refusal is { })
            {
                return refusal;
            }

            if (string.Equals(group.CodeText, was, StringComparison.Ordinal))
            {
                return Report("Nothing changed; the block already says that.", null);
            }

            Undoable(label);

            var under = Under(group).ToList();
            var wrong = Listed(under, "get_problems lists them", out var clean, 5);

            return Report(
                "Done." + Undoes + (clean == under.Count
                    ? $" No problems in the {under.Count} drawing(s) under it."
                    : $"\nProblems under it now:\n{wrong}"),
                summary);
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

    /// <summary>The drawing as it stands: the buffer of a tab holding it, or the project's text.</summary>
    private string Text(ProjectDrawing drawing) => _window.DrawingOf(drawing)?.Text ?? drawing.Text;

    private static IEnumerable<ProjectDrawing> Under(ProjectGroup group)
        => group.Children.SelectMany(child => child switch
        {
            ProjectDrawing drawing => new[] { drawing },
            ProjectGroup inner => Under(inner),
            _ => Enumerable.Empty<ProjectDrawing>()
        });

    /// <summary>An edit's answer: done, and what is wrong with the drawing now, so the model need not read it again.</summary>
    /// <param name="before">The drawing's text before the edit; the same afterwards, the edit made no step to remember.</param>
    private string Done(ISvgViewerDeclarationTarget target, string? node, string summary, string before)
    {
        if (string.Equals(target.Text, before, StringComparison.Ordinal))
        {
            return Report("Nothing changed; it already says that.", null);
        }

        var label = Label(summary);

        if (target is not SvgViewer viewer)
        {
            Undoable(label);
        }
        else if (viewer is { UndoStep: { } step } && viewer.UndoLabel == label)
        {
            _undos.Push(new Made(label, step, viewer, null));
        }

        var problems = Problems(Drawing(node), target.Text);

        return Report(
            "Done." + Undoes + (problems.Count == 0 ? " No problems." : $"\nProblems now:\n{Cut(problems.Select(problem => "- " + problem).ToList(), null)}"),
            summary);
    }

    /// <summary>Remembers the project's step on top as this tool's, for <see cref="Undo"/>, when it is the one just made under <paramref name="label"/>.</summary>
    private void Undoable(string label)
    {
        if (_window.Workspace is { UndoStep: { } step } workspace && workspace.UndoLabel == label)
        {
            _undos.Push(new Made(label, step, null, workspace));
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
    private static List<string> Problems(ProjectDrawing? drawing, string text)
    {
        // Every inherited name, not only the ones the drawing reaches: an unknown name's "in
        // scope" list is then the names the drawing could have used.
        var built = drawing is { } ? ProjectDeclarations.Built(drawing, text, narrow: false) : text;

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

    private string Label(string summary) => $"{Who}: {summary.Trim()}";

    private string Report(string result, string? summary)
    {
        Called?.Invoke(this, summary);

        return result;
    }

    /// <summary>Runs on the UI thread, where a function is called from whichever thread the model's reply arrived on.</summary>
    private static Task<string> Ui(Func<string> call)
        => Dispatcher.UIThread.CheckAccess() ? Task.FromResult(call()) : Dispatcher.UIThread.InvokeAsync(call).GetTask();

    private static Task<string> UiAsync(Func<Task<string>> call)
        => Dispatcher.UIThread.CheckAccess() ? call() : Dispatcher.UIThread.InvokeAsync(call);
}
