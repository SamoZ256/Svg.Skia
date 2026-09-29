// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Threading;
using Microsoft.Extensions.AI;
using Svg.CodeGen.Skia.Projects;
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

    private readonly MainWindow _window;

    public AssistantTools(MainWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    /// <summary>Asks the person before something that cannot be taken back with ⌘Z.</summary>
    public Func<string, Task<bool>> Confirm { get; set; } = _ => Task.FromResult(false);

    /// <summary>Raised on the UI thread after each call, with the line the panel shows for it.</summary>
    public event EventHandler<string>? Called;

    /// <summary>The functions for a model: all of them, or the few a small context has room for.</summary>
    /// <param name="docs">Whether the model reads the docs a section at a time, rather than being given them whole.</param>
    public IList<AITool> For(bool small, bool docs)
    {
        var tools = new List<AITool>();

        if (docs)
        {
            tools.Add(AIFunctionFactory.Create(ReadDoc, "read_doc", "Reads one section of the Svg Studio documentation by its id from the contents."));
        }

        tools.Add(AIFunctionFactory.Create(GetProject, "get_project", "Lists the open project's groups and drawings with their node paths, and which one is in front."));
        tools.Add(AIFunctionFactory.Create(GetDrawing, "get_drawing", "Reads a drawing: its elements with their address keys, any problems, and its SVG source."));
        tools.Add(AIFunctionFactory.Create(SetAttributes, "set_attributes", "Sets or removes attributes on one element of a drawing, as a single undo step."));
        tools.Add(AIFunctionFactory.Create(Undo, "undo", "Takes back the last edit to the drawing in front, or to the project."));

        if (small)
        {
            return tools;
        }

        tools.Add(AIFunctionFactory.Create(GetElement, "get_element", "Reads every attribute of one element of a drawing, as the file writes it."));
        tools.Add(AIFunctionFactory.Create(ReplaceDrawing, "replace_drawing", "Replaces a drawing's whole SVG source, as a single undo step. Prefer set_attributes for small changes."));
        tools.Add(AIFunctionFactory.Create(Select, "select", "Selects elements of the drawing in front, by address key, so the person sees them."));
        tools.Add(AIFunctionFactory.Create(Open, "open", "Opens a group or drawing of the project in its tab."));
        tools.Add(AIFunctionFactory.Create(AddGroup, "add_group", "Adds an empty group inside a group, and opens it."));
        tools.Add(AIFunctionFactory.Create(AddDrawing, "add_drawing", "Adds a drawing from SVG text inside a group, and opens it."));
        tools.Add(AIFunctionFactory.Create(Move, "move", "Moves a group or drawing into another group."));
        tools.Add(AIFunctionFactory.Create(SetNode, "set_node", "Changes a group's or drawing's project setting: name, namespace, class, padding, width, height, scale, x, y, cache, helperScope or skiaSharp."));
        tools.Add(AIFunctionFactory.Create(Save, "save", "Saves the project, or the drawing in front when it is a file of its own. Asks the person first."));
        tools.Add(AIFunctionFactory.Create(Remove, "remove", "Removes a group or drawing from the project. Asks the person first."));
        tools.Add(AIFunctionFactory.Create(GitCommit, "git_commit", "Saves and commits the project to its git repository. Asks the person first."));

        return tools;
    }

    /// <summary>What is open, in a few lines, for the start of every message the person sends.</summary>
    public string Context()
    {
        var text = new StringBuilder();
        var front = _window.FrontNode;

        if (_window.Workspace is { } workspace)
        {
            text.Append("Project: ").Append(workspace.Name).Append(workspace.IsEdited ? " (unsaved changes)" : string.Empty).Append('\n');
        }
        else
        {
            text.Append("No project is open.\n");
        }

        if (front is { })
        {
            text.Append("In front: ").Append(Describe(front)).Append('\n');
        }
        else if (_window.FrontViewer is { } viewer)
        {
            text.Append("In front: the drawing ").Append(viewer.DocumentPath ?? "Untitled").Append(", not part of a project\n");
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

            if (shown.SourceDiagnostics.Count > 0)
            {
                text.Append("The drawing has ").Append(shown.SourceDiagnostics.Count).Append(" problem(s); get_drawing lists them.\n");
            }
        }

        if (_window.Changes.Summary is { } git)
        {
            text.Append("Git: ").Append(git).Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    [Description("Reads a section of the documentation.")]
    private string ReadDoc([Description("The section id, as the contents lists it.")] string id)
        => AssistantDocs.Read(id) ?? $"There is no section {id}. The contents lists the ids.";

    private Task<string> GetProject() => Ui(() =>
    {
        if (_window.Workspace is not { } workspace)
        {
            return _window.FrontViewer is { } viewer
                ? $"No project is open. The front tab is the drawing {viewer.DocumentPath ?? "Untitled"}; leave node empty to work on it."
                : "Nothing is open.";
        }

        var text = new StringBuilder();

        text.Append("Project ").Append(workspace.Name)
            .Append(workspace.Document.Path is { } path ? $" at {path}" : " (never saved)")
            .Append(workspace.IsEdited ? ", with unsaved changes" : string.Empty)
            .Append('\n');

        if (_window.FrontNode is { } front)
        {
            text.Append("In front: ").Append(Path(front)).Append('\n');
        }

        text.Append("Nodes (path, kind, name):\n");

        Walk(workspace.Document.Root, 0);

        return text.ToString();

        void Walk(ProjectGroup group, int depth)
        {
            foreach (var child in group.Children)
            {
                text.Append(new string(' ', depth * 2)).Append(Path(child)).Append(' ')
                    .Append(child is ProjectGroup ? "group" : "drawing").Append(' ')
                    .Append(ProjectWorkspace.Label(child));

                if (_window.TabOf(child) is { })
                {
                    text.Append(" (open)");
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

            if (SvgSourceDocument.Read(source, out var unreadable) is { } read && read.Document.Root is { } root)
            {
                text.Append("Elements (address key, element, id):\n");
                Outline(root, string.Empty, 0);
            }
            else
            {
                text.Append("The source does not read as XML: ").Append(unreadable).Append('\n');
            }

            foreach (var problem in SvgSourceDiagnostics.Analyse(source))
            {
                text.Append("Problem at ").Append(problem.Start).Append(": ").Append(problem.Message).Append('\n');
            }

            text.Append("Source:\n").Append(source);

            return text.ToString();

            void Outline(XElement element, string key, int depth)
            {
                text.Append(new string(' ', depth * 2)).Append(key.Length == 0 ? "(root)" : key).Append(' ')
                    .Append(element.Name.LocalName);

                if (element.Attribute("id")?.Value is { } id)
                {
                    text.Append(" #").Append(id);
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

            if (SvgAttributeEditor.ElementName(source, key) is not { } name)
            {
                return $"There is no element {key}. get_drawing lists the keys.";
            }

            var text = new StringBuilder($"<{name}> at {(key.Length == 0 ? "(root)" : key)}\n");

            foreach (var attribute in SvgAttributeEditor.Attributes(source, key))
            {
                text.Append(attribute.Name).Append('=').Append(attribute.Value).Append('\n');
            }

            return text.ToString();
        });

    private Task<string> SetAttributes(
        [Description("The element's address key from get_drawing; empty for the root svg element.")] string key,
        [Description("The attributes to write.")] AttributeChange[] attributes,
        [Description("A few words saying what this does, shown in the Edit menu as Undo Assistant: <summary>.")] string summary,
        [Description("The drawing's node path; empty for the drawing in front.")] string? node = null)
        => Ui(() =>
        {
            if (Target(node, out var about) is not { } target)
            {
                return about;
            }

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

            return refusal ?? Report("Done." + Undoes, summary);
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

            if (SvgSourceDocument.Read(svg, out var unreadable) is null)
            {
                return unreadable ?? "That SVG cannot be read.";
            }

            // A tab has a history of its own to put the whole text on; a drawing in no tab is the
            // project's, where the same text goes in as one step of the project's history.
            if (target is SvgViewer viewer)
            {
                viewer.SetSource(svg, Label(summary));
            }
            else if (Resolve(node) is ProjectDrawing drawing && _window.Workspace is { } workspace)
            {
                string? bad = null;

                workspace.Do(Label(summary), () => ProjectSnapshot.Text(drawing), () => bad = drawing.SetText(svg));

                if (bad is { })
                {
                    return bad;
                }
            }

            return Report("Done." + Undoes, summary);
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
            var picked = keys.Select(key => tree.TryGetValue(key, out var built) ? built : null).OfType<string>().ToList();

            return viewer.Elements.TrySelect(picked) ? "Selected." : "None of those keys is an element of the drawing in front.";
        });

    private Task<string> Undo() => Ui(() => _window.UndoDocument() ? Report("Took back the last edit.", "undo") : "There is nothing to take back.");

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
            return $"{parent} is not a group.";
        }

        await _window.AddGroupAsync(group).ConfigureAwait(true);

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
                return $"{parent} is not a group.";
            }

            if (await _window.AddTextAsync(group, group.Children.Count, name, svg).ConfigureAwait(true) is { } refusal)
            {
                return refusal;
            }

            return Report($"Added {name} as {(_window.FrontNode is { } added ? Path(added) : "(unknown)")}." + Undoes, $"add {name}");
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
                return $"{into} is not a group.";
            }

            return _window.Move(moving, group)
                ? Report($"Moved; it is now {Path(moving)}." + Undoes, "move")
                : "It did not move: it is already there, or that would put a group inside itself.";
        });

    private Task<string> SetNode(
        [Description("The node path of the group or drawing.")] string node,
        [Description("The setting: name, namespace, class, padding, width, height, scale, x, y, cache, helperScope or skiaSharp.")] string setting,
        [Description("The new value, or empty to clear it so it is inherited again.")] string? value)
        => Ui(() =>
        {
            if (Resolve(node) is not { } found || _window.Workspace is not { } workspace)
            {
                return NoNode(node);
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

            workspace.Do(Label($"change {setting}"), () => ProjectSnapshot.Attributes(found), () => GroupPanel.Write(found, setting, text));

            return Report($"Set {setting} on {Path(found)}." + Undoes, $"change {setting}");
        });

    private Task<string> Save() => UiAsync(async () =>
    {
        if (!await Confirm("Save the project?").ConfigureAwait(true))
        {
            return "The person said no; nothing was saved.";
        }

        await _window.SaveAsync().ConfigureAwait(true);

        return Report("Saved.", "save");
    });

    private Task<string> Remove([Description("The node path of the group or drawing to remove.")] string node) => UiAsync(async () =>
    {
        if (Resolve(node) is not { Parent: { } } found)
        {
            return NoNode(node);
        }

        if (!await Confirm($"Remove {ProjectWorkspace.Label(found)} from the project?").ConfigureAwait(true))
        {
            return "The person said no; nothing was removed.";
        }

        return await _window.RemoveAsync(found).ConfigureAwait(true)
            ? Report($"Removed {ProjectWorkspace.Label(found)}." + Undoes, "remove")
            : "It was not removed.";
    });

    private Task<string> GitCommit([Description("The commit message.")] string message) => UiAsync(async () =>
    {
        if (_window.Changes.Git is null)
        {
            return "The project is not in a git repository.";
        }

        if (!await Confirm($"Save and commit the project as \"{message}\"?").ConfigureAwait(true))
        {
            return "The person said no; nothing was committed.";
        }

        await _window.Changes.Commit(message).ConfigureAwait(true);

        return Report("Committed.", "commit");
    });

    /// <summary>Where an edit to a drawing goes: its tab when it has one, the project when it has none.</summary>
    private ISvgViewerDeclarationTarget? Target(string? node, out string about)
    {
        if (string.IsNullOrEmpty(node))
        {
            if (_window.FrontViewer is { } viewer)
            {
                about = _window.FrontNode is { } front ? $"Drawing {Describe(front)}" : $"Drawing {viewer.DocumentPath ?? "Untitled"}";

                return viewer;
            }

            about = "The tab in front is not a drawing; name one by its node path.";

            return null;
        }

        if (Resolve(node) is not ProjectDrawing drawing || _window.Workspace is not { } workspace)
        {
            about = $"{node} is not a drawing. get_project lists the paths.";

            return null;
        }

        about = $"Drawing {Describe(drawing)}";

        return _window.DrawingOf(drawing) ?? new DrawingTarget(workspace, drawing);
    }

    private ProjectNode? Resolve(string? path)
    {
        if (_window.Workspace is not { } workspace)
        {
            return null;
        }

        ProjectNode at = workspace.Document.Root;

        foreach (var segment in (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries))
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
            segments.Add(parent.Children.ToList().IndexOf(at).ToString(CultureInfo.InvariantCulture));
        }

        segments.Reverse();

        return segments.Count == 0 ? "(project)" : string.Join("/", segments);
    }

    private string Describe(ProjectNode node)
        => $"{Path(node)} {(node is ProjectGroup ? "group" : "drawing")} {ProjectWorkspace.Label(node)}"
           + (_window.TabOf(node) is { } tab && tab.Content is SvgViewer { IsSourceModified: true } ? ", with edits not yet saved into the project" : string.Empty);

    private static string NoNode(string? path) => $"There is no node {path}. get_project lists the paths.";

    private static string Label(string summary) => "Assistant: " + summary.Trim();

    private string Report(string result, string summary)
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
