// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Projects;
using Svg.Skia;

namespace Svg.Studio;

/// <summary>
/// One node of a project: its settings, and — for a group — what it builds.
/// </summary>
/// <remarks>
/// Built in code rather than declared, for the reason the tabs are: the rows depend on what kind of
/// node this is, so a template would have to be chosen at runtime anyway. A group fills a tab, since
/// the list of what it builds wants the room; a drawing is its settings alone, and fits the pane
/// beside the tree — its own tab is the viewer, which belongs to another package and has a right
/// pane of its own about the drawing rather than about the project.
/// </remarks>
public sealed class GroupPanel : UserControl
{
    private readonly StackPanel _properties = new() { Spacing = 8, Margin = new Thickness(10) };
    private readonly StackPanel _summary = new() { Spacing = 2, Margin = new Thickness(10) };
    private readonly TextBlock _heading = new() { FontWeight = FontWeight.SemiBold, Margin = new Thickness(10, 10, 10, 0) };

    /// <summary>Edits typed here and not yet written to the project, by setting name.</summary>
    /// <remarks>
    /// Held rather than applied, so a tab saves what was typed in it and nothing else. The cost is
    /// that the tree, the values other tabs inherit and the drawings already open all go on showing
    /// what is in the file until this is saved — the document is the one thing they all read.
    /// </remarks>
    private readonly Dictionary<string, string?> _pending = new(StringComparer.Ordinal);

    public GroupPanel(ProjectWorkspace workspace, SvgcProjectNode node)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Node = node ?? throw new ArgumentNullException(nameof(node));

        Content = node is SvgcProjectGroup ? Built() : Alone();

        // A group saved in another tab changes what this one inherits, so every tab follows the
        // one document rather than the copy it was opened with. Anything typed here and not saved
        // survives it.
        workspace.Edited += (_, _) => Refresh();

        Refresh();
    }

    /// <summary>A group's tab: what it builds beside the settings that decide it.</summary>
    private Control Built()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,6,340")
        };

        var centre = new DockPanel();

        centre.Children.Add(_heading);
        DockPanel.SetDock(_heading, Dock.Top);
        centre.Children.Add(new ScrollViewer { Content = _summary });

        grid.Children.Add(centre);

        var splitter = new GridSplitter { Background = Brushes.Transparent };

        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);

        var right = new Border
        {
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#20808080")),
            Child = new ScrollViewer { Content = _properties }
        };

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        return grid;
    }

    /// <summary>A drawing's settings, with nothing beside them, for the pane.</summary>
    private Control Alone()
    {
        var panel = new DockPanel();

        panel.Children.Add(_heading);
        DockPanel.SetDock(_heading, Dock.Top);
        panel.Children.Add(new ScrollViewer { Content = _properties });

        return panel;
    }

    public ProjectWorkspace Workspace { get; }

    /// <summary>The node this is about.</summary>
    public SvgcProjectNode Node { get; }

    /// <summary>Whether anything typed here has not been written to the project.</summary>
    public bool IsModified => _pending.Count > 0;

    /// <summary>Raised when <see cref="IsModified"/> changes, for a host that marks its tab.</summary>
    public event EventHandler<bool>? ModifiedChanged;

    /// <summary>Raised with the file when somebody asks to edit the recipe this node names.</summary>
    /// <remarks>
    /// The panel says so rather than opening it: a recipe is a file of the project's, and where the
    /// project's files are shown is the window's business, not a settings pane's.
    /// </remarks>
    public event EventHandler<string>? RecipeOpened;

    /// <summary>Writes what was typed here into the project, and the project to its file.</summary>
    public void Save()
    {
        // What is in the box being typed in, before deciding there is nothing to save. Recording
        // an edit when the box loses focus is what lets a half-typed value be rejected while it is
        // still on screen, but it also meant Ctrl+S did nothing at all until the caret left.
        var typing = Typing();

        if (typing is { } box)
        {
            var typed = (string)box.Tag!;

            // Checked now, since typing records without checking. Put back rather than written,
            // the same as the caret leaving would.
            if (!Edit(typed, box.Text))
            {
                box.Text = Value(Node, typed);
            }
        }

        if (_pending.Count == 0)
        {
            return;
        }

        var caret = typing?.CaretIndex ?? 0;
        var setting = typing?.Tag as string;

        var was = IsModified;

        foreach (var edit in _pending)
        {
            Write(Node, edit.Key, edit.Value);
        }

        _pending.Clear();

        // The file first: a tab that reports itself saved when the write threw would be lying, and
        // the edits are already in the document either way.
        Workspace.Save();

        // What is true, not the false this used to announce. Saving rebuilds the rows, which
        // detaches whichever box had focus, and a box losing focus records what is in it — so a
        // save can end with something pending again, and saying "saved" there left the tab with
        // no mark and an unsaved warning waiting at the close button.
        Announce(was);

        // The rows were rebuilt under whoever was typing, so put them back where they were rather
        // than making a save cost the caret.
        Resume(setting, caret);
    }

    /// <summary>The box being typed in, if the caret is in one of this panel's.</summary>
    private TextBox? Typing()
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox box
           && box.Tag is string
           && ReferenceEquals(box.FindAncestorOfType<GroupPanel>(), this)
            ? box
            : null;

    /// <summary>Puts the caret back in the box for <paramref name="setting"/>, where it was.</summary>
    private void Resume(string? setting, int caret)
    {
        if (setting is null)
        {
            return;
        }

        var box = _properties.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(candidate => Equals(candidate.Tag, setting));

        if (box is null)
        {
            return;
        }

        box.Focus();
        box.CaretIndex = Math.Min(caret, box.Text?.Length ?? 0);
    }

    /// <summary>Says so if the tab's unsaved state has changed since <paramref name="was"/>.</summary>
    private void Announce(bool was)
    {
        if (IsModified != was)
        {
            ModifiedChanged?.Invoke(this, IsModified);
        }
    }

    /// <summary>Puts the group's settings on the right, and the drawings under it in the centre.</summary>
    public void Refresh()
    {
        _heading.Text = ProjectWorkspace.Label(Node);

        if (Node is SvgcProjectGroup)
        {
            ShowSummary();
        }

        ShowProperties(Node);
    }

    private void ShowSummary()
    {
        _summary.Children.Clear();

        var drawings = ((SvgcProjectGroup)Node).Drawings.ToList();

        if (drawings.Count == 0)
        {
            _summary.Children.Add(new TextBlock { Text = "This group holds no drawings.", Opacity = 0.65 });
            return;
        }

        foreach (var drawing in drawings)
        {
            var name = drawing.EffectiveNamespace is { } space && drawing.EffectiveClass is { } className
                ? $"{space}.{className}"
                : drawing.EffectiveClass ?? drawing.EffectiveNamespace ?? "(unnamed)";

            _summary.Children.Add(new TextBlock
            {
                Text = $"{Path.GetFileName(drawing.Input)}  →  {name}   {Size(drawing)}",
                FontFamily = new FontFamily("Menlo, Consolas, monospace"),
                FontSize = 12
            });
        }
    }

    /// <summary>What the sizing comes to, said the way the project says it.</summary>
    private static string Size(SvgcProjectNode node)
    {
        var parts = new List<string>();

        if (node.EffectiveWidth is { } width)
        {
            parts.Add($"w{Number(width)}");
        }

        if (node.EffectiveHeight is { } height)
        {
            parts.Add($"h{Number(height)}");
        }

        if (node.EffectiveScale is { } scale)
        {
            parts.Add($"×{Number(scale)}");
        }

        if (node.EffectivePadding is { } padding)
        {
            parts.Add($"pad {padding}");
        }

        return parts.Count == 0 ? "as written" : string.Join(" ", parts);
    }

    private void ShowProperties(SvgcProjectNode node)
    {
        _properties.Children.Clear();

        if (node is SvgcProjectDrawing)
        {
            Add("input");
            Add("output");

            _properties.Children.Add(new Separator { Margin = new Thickness(0, 6) });
        }

        Add("namespace");
        Add("class");

        _properties.Children.Add(RecipeRow(node));

        if (node is SvgcProjectRoot)
        {
            Add("singleFile");
            Add("emit");
            Add("cache");
            Add("helperScope");
            Add("skiaSharp");
        }

        _properties.Children.Add(new Separator { Margin = new Thickness(0, 6) });

        Add("width");
        Add("height");
        Add("scale");
        Add("padding");

        void Add(string name) => _properties.Children.Add(Row(node, name));
    }

    /// <summary>What the box shows: what was typed here if anything, and what the file says if not.</summary>
    public string? Shown(string name) => Shown(Node, name);

    private string? Shown(SvgcProjectNode node, string name)
        => _pending.TryGetValue(name, out var pending) ? pending : Value(node, name);

    /// <summary>Writes one setting onto <paramref name="node"/>, or throws if the value is not one.</summary>
    private static void Write(SvgcProjectNode node, string name, string? value)
    {
        switch (name)
        {
            case "input": ((SvgcProjectDrawing)node).Input = value!; break;
            case "output": ((SvgcProjectDrawing)node).Output = value; break;
            case "namespace": node.Namespace = value; break;
            case "class": node.Class = value; break;
            case "recipe": node.Recipe = value; break;
            case "padding": node.Padding = value; break;
            case "width": node.Width = SvgcProject.ParseLength(value, "width"); break;
            case "height": node.Height = SvgcProject.ParseLength(value, "height"); break;
            case "scale": node.Scale = SvgcProject.ParseScale(value); break;
            case "singleFile": ((SvgcProjectRoot)node).SingleFile = value; break;
            case "emit": ((SvgcProjectRoot)node).Emit = SvgcProject.ParseEmit(value); break;
            case "cache": ((SvgcProjectRoot)node).Cache = SvgcProject.ParseCache(value); break;
            case "helperScope": ((SvgcProjectRoot)node).HelperScope = SvgcProject.ParseHelperScope(value); break;
            case "skiaSharp": ((SvgcProjectRoot)node).SkiaSharp = SvgcProject.ParseSkiaSharpTarget(value); break;
        }
    }

    private static readonly FilePickerFileType Recipes = new("Svg Recipes")
    {
        Patterns = new[] { "*.recipe" },
        AppleUniformTypeIdentifiers = new[] { "public.xml" },
        MimeTypes = new[] { "application/xml" }
    };

    /// <summary>What a new recipe is written with.</summary>
    /// <remarks>
    /// A parameter and a let, and the rule commented out. It applies cleanly as it stands — the
    /// declarations reach the drawing and nothing is recoloured — so the slider is there to drag
    /// the moment the file is made, and what to write next is on the line below it. An empty
    /// recipe would have been the safer thing to write and would have shown nothing at all.
    /// </remarks>
    private const string Skeleton = """
        <?xml version="1.0" encoding="utf-8"?>
        <recipe xmlns="https://svg.skia/expr/1.0">

          <!-- What the drawing is given, and what is worked out from it. -->
          <code>
            <param name="hue" type="number" default="200" min="0" max="360" />
            <let name="accent">hsl(hue, 74%, 55%)</let>
          </code>

          <!-- One line per colour in the drawing an expression above should paint. -->
          <!-- <replace color="#3b82f6">accent</replace> -->
        </recipe>

        """;

    /// <summary>
    /// The recipe: a file, chosen with buttons rather than typed as a path.
    /// </summary>
    /// <remarks>
    /// Not a box like the rest of the settings. A recipe is a second file the project has to find,
    /// and a path typed wrong is only discovered when a drawing under it is opened or a build runs.
    /// <b>New…</b> is there because a recipe that does not exist yet cannot be picked, and having to
    /// leave for a text editor to make an empty one was the whole of what made recipes awkward to
    /// start using.
    /// </remarks>
    private Control RecipeRow(SvgcProjectNode node)
    {
        var shown = Shown(node, "recipe");

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        if (shown is { })
        {
            var name = new TextBlock
            {
                // The file, not the path: the path is what the project carries and rarely what
                // anybody wants to read, and the tip has it in full for when they do.
                Text = Path.GetFileName(shown),
                FontFamily = new FontFamily("Menlo, Consolas, monospace"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            // A file is opened by being double-clicked, the same as a row of the tree is. On a
            // border filling the cell rather than on the text: a file name is a small target, and
            // a TextBlock with nothing painted behind it is not one at all.
            var target = new Border
            {
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = name
            };

            ToolTip.SetTip(target, $"{shown}\n\nDouble-click to edit it.");

            target.DoubleTapped += (_, e) =>
            {
                e.Handled = true;
                RecipeOpened?.Invoke(this, Resolved(shown));
            };

            content.Children.Add(target);
            content.Children.Add(Buttons(Command("✕", "Stop using this recipe. The file is left where it is.", RemoveRecipe)));
        }
        else
        {
            content.Children.Add(new TextBlock
            {
                // What it would inherit, or that it has none — the same thing the watermark of an
                // empty box says for every other setting.
                Text = Inherited(node, "recipe") ?? "none",
                Opacity = 0.55,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            content.Children.Add(Buttons(
                Command("Add…", "Use a recipe that already exists.", async () => await ChooseRecipeAsync()),
                Command("New…", "Write an empty recipe and use it.", async () => await CreateRecipeAsync())));
        }

        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "recipe", Opacity = 0.65, FontSize = 11 },
                content
            }
        };
    }

    /// <summary>Where a recipe named by the project actually is.</summary>
    private string Resolved(string recipe)
    {
        var directory = Workspace.Document.BaseDirectory;

        return Path.GetFullPath(directory.Length == 0 ? recipe : Path.Combine(directory, recipe));
    }

    private static Button Command(string content, string tip, Action run)
    {
        var button = new Button
        {
            Content = content,
            FontSize = 11,
            Padding = new Thickness(8, 2),
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center
        };

        ToolTip.SetTip(button, tip);

        button.Click += (_, _) => run();

        return button;
    }

    private static Control Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        foreach (var button in buttons)
        {
            panel.Children.Add(button);
        }

        Grid.SetColumn(panel, 1);

        return panel;
    }

    /// <summary>Asks which recipe to use, and uses it.</summary>
    private async Task ChooseRecipeAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a recipe",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType> { Recipes }
        }).ConfigureAwait(true);

        if (files.Select(file => file.TryGetLocalPath()).FirstOrDefault(path => path is { Length: > 0 }) is { } chosen)
        {
            SetRecipe(chosen);
        }
    }

    /// <summary>Asks where to write a recipe, writes it, and uses it.</summary>
    private async Task CreateRecipeAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "New recipe",
            SuggestedFileName = Suggested(),
            DefaultExtension = "recipe",
            FileTypeChoices = new List<FilePickerFileType> { Recipes }
        }).ConfigureAwait(true);

        if (file?.TryGetLocalPath() is { Length: > 0 } path)
        {
            CreateRecipe(path);
        }
    }

    /// <summary>What a new recipe is offered as being called: after what it would be a recipe for.</summary>
    private string Suggested()
    {
        var name = Node is SvgcProjectDrawing drawing
            ? Path.GetFileNameWithoutExtension(drawing.Input)
            : Node.Class ?? Node.Namespace ?? Path.GetFileNameWithoutExtension(Workspace.Name);

        return (string.IsNullOrWhiteSpace(name) ? "recipe" : name) + ".recipe";
    }

    /// <summary>Names <paramref name="path"/> as this node's recipe.</summary>
    /// <remarks>
    /// Taking the path rather than asking for it, so everything but the picker can be driven. Held
    /// until the tab is saved, like every other setting typed here — which is also when the drawings
    /// under it are read again through it.
    /// </remarks>
    public void SetRecipe(string path)
    {
        Edit("recipe", Workspace.Carry(path ?? throw new ArgumentNullException(nameof(path))));
        Refresh();
    }

    /// <summary>Writes an empty recipe at <paramref name="path"/>, and names it here.</summary>
    /// <remarks>
    /// A file that is already there is named rather than written over. The save panel has asked
    /// about replacing it, but replacing a recipe somebody wrote with an empty one is never what
    /// picking its name meant.
    /// </remarks>
    public void CreateRecipe(string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        if (!File.Exists(path))
        {
            File.WriteAllText(path, Skeleton);
        }

        SetRecipe(path);
    }

    /// <summary>Stops this node naming a recipe. The file itself is left alone.</summary>
    public void RemoveRecipe()
    {
        Edit("recipe", null);
        Refresh();
    }

    /// <summary>
    /// One setting: what this node says, with what it would inherit shown behind it.
    /// </summary>
    /// <remarks>
    /// An empty box means "inherited", so clearing one is how an override is taken back — which is
    /// why the watermark carries the inherited value rather than a hint.
    /// </remarks>
    private Control Row(SvgcProjectNode node, string name)
    {
        var value = Shown(node, name);

        var box = new TextBox
        {
            Text = value,
            PlaceholderText = Inherited(node, name),
            FontSize = 12,
            Tag = name
        };

        box.TextChanged += (_, _) => Track(name, box.Text);

        box.LostFocus += (_, _) =>
        {
            var text = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text!.Trim();

            if (text == value)
            {
                return;
            }

            if (!Edit(name, text))
            {
                // Put back, and said, rather than left looking accepted.
                box.Text = value;
                return;
            }

            value = text;
        };

        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = name, Opacity = 0.65, FontSize = 11 },
                box
            }
        };
    }

    /// <summary>
    /// Records a setting as typed here, without writing it to the project.
    /// </summary>
    /// <remarks>
    /// The edit is kept until <see cref="Save"/>, so a value typed back to what the file already
    /// says leaves nothing to save rather than a tab marked for no change.
    /// </remarks>
    /// <returns>Whether the value was one the setting can hold.</returns>
    public bool Edit(string name, string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        // Checked as it is typed rather than at save, so a value the project cannot hold is
        // rejected while the box that holds it is still on screen.
        try
        {
            Validate(name, text);
        }
        catch (SvgcProjectException failure)
        {
            Fault = failure.Message;
            return false;
        }

        Fault = null;

        Track(name, text);

        return true;
    }

    /// <summary>
    /// Records what a box holds as it is typed, without asking whether the project can hold it.
    /// </summary>
    /// <remarks>
    /// Unchecked on purpose: a number is typed through "1." and "-", and snatching those back
    /// between keystrokes is worse than letting the box keep them until the caret leaves or a save
    /// asks. What it buys is the mark appearing at the first keystroke rather than when the caret
    /// leaves, which is the only thing saying there is anything to save.
    /// </remarks>
    private void Track(string name, string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var was = IsModified;

        if (text == Value(Node, name))
        {
            _pending.Remove(name);
        }
        else
        {
            _pending[name] = text;
        }

        Announce(was);
    }

    /// <summary>Throws unless <paramref name="value"/> is one this setting can hold.</summary>
    /// <remarks>
    /// The same parsers the setters use, called without them: a setting checked here is not written
    /// anywhere yet, and the setters write to the document an unsaved edit must not touch.
    /// </remarks>
    private static void Validate(string name, string? value)
    {
        switch (name)
        {
            // The one setting with no empty form: a drawing is a file, and a row naming none is
            // not a row the build can read.
            case "input" when string.IsNullOrWhiteSpace(value):
                throw new SvgcProjectException("A drawing needs an input file.");
            case "width": SvgcProject.ParseLength(value, "width"); break;
            case "height": SvgcProject.ParseLength(value, "height"); break;
            case "scale": SvgcProject.ParseScale(value); break;
            case "emit": SvgcProject.ParseEmit(value); break;
            case "cache": SvgcProject.ParseCache(value); break;
            case "helperScope": SvgcProject.ParseHelperScope(value); break;
            case "skiaSharp": SvgcProject.ParseSkiaSharpTarget(value); break;
        }
    }

    /// <summary>What the node would take for <paramref name="name"/> if it said nothing itself.</summary>
    private static string? Inherited(SvgcProjectNode node, string name)
    {
        if (node.Parent is not { } parent)
        {
            return null;
        }

        var owner = parent.OwnerOf(name);

        return owner is null ? null : $"{Value(owner, name)} — from {ProjectWorkspace.Label(owner)}";
    }

    private static string? Value(SvgcProjectNode node, string name) => name switch
    {
        "input" => (node as SvgcProjectDrawing)?.Input,
        "output" => (node as SvgcProjectDrawing)?.Output,
        "namespace" => node.Namespace,
        "class" => node.Class,
        "recipe" => node.Recipe,
        "padding" => node.Padding,
        "width" => node.Width is { } width ? Number(width) : null,
        "height" => node.Height is { } height ? Number(height) : null,
        "scale" => node.Scale is { } scale ? Number(scale) : null,
        // The project's own five. Left out, they showed empty however the file was written, and an
        // edit to one could never be recognised as typed back to what the file says — so it stayed
        // pending for ever.
        "singleFile" => (node as SvgcProjectRoot)?.SingleFile,
        "emit" => Text((node as SvgcProjectRoot)?.Emit),
        "cache" => Text((node as SvgcProjectRoot)?.Cache),
        "helperScope" => Text((node as SvgcProjectRoot)?.HelperScope),
        "skiaSharp" => (node as SvgcProjectRoot)?.SkiaSharp is { } target
            ? (target == SkiaSharpTarget.V3 ? "3" : "4")
            : null,
        _ => null
    };

    /// <summary>Why the last edit was refused, or null.</summary>
    public string? Fault { get; private set; }

    private static string Number(float value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Text<T>(T? value) where T : struct
        => value is { } set ? set.ToString()!.ToLowerInvariant() : null;
}
