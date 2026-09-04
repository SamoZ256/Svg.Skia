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
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using SkiaSharp;
using Svg.CodeGen.Skia;
using Svg.CodeGen.Skia.Projects;
using Svg.Expressions;
using Svg.Skia;
using Svg.Viewer.Skia.Avalonia;

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
    // Named because the canvas beside it has buttons of its own, and a test asking what the settings
    // offer has to be able to say which half it means.
    private readonly StackPanel _properties = new() { Name = "Settings", Spacing = 8, Margin = new Thickness(10) };
    private readonly SvgViewerCanvas _canvas = new();
    private readonly TextBlock _heading = new() { FontWeight = FontWeight.SemiBold, Margin = new Thickness(10, 10, 10, 0) };

    private readonly TextBlock _zoom = new()
    {
        Width = 64,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        FontFamily = new FontFamily("Menlo, Consolas, monospace"),
        Text = "100%"
    };

    private readonly TextBlock _notice = new()
    {
        IsVisible = false,
        Margin = new Thickness(10, 0, 10, 6),
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.65
    };

    /// <summary>The drawings on the canvas.</summary>
    /// <remarks>
    /// Held because a picture belongs to the document that built it: the canvas only borrows one, so
    /// both have to be let go together, in that order.
    /// </remarks>
    private readonly List<SvgViewerDocument> _loaded = new();

    /// <summary>Whether this is the tab being looked at.</summary>
    /// <remarks>
    /// A tab's content leaves the visual tree when another tab is picked, so this is the whole of
    /// when the pictures are worth having — and every save refreshes every open panel.
    /// </remarks>
    private bool _watched;

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
        var tools = Tools();

        centre.Children.Add(_heading);
        DockPanel.SetDock(_heading, Dock.Top);

        centre.Children.Add(tools);
        DockPanel.SetDock(tools, Dock.Top);

        centre.Children.Add(_notice);
        DockPanel.SetDock(_notice, Dock.Top);

        centre.Children.Add(_canvas);

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

    /// <summary>What a drawing's text goes through on its way to being drawn, or null to draw the file.</summary>
    /// <remarks>
    /// A hook rather than a recipe path, so the canvas draws what the drawing's own tab draws: the
    /// host renders through an open buffer, and reading the file here would show a recipe as it was
    /// last saved rather than as it is being typed.
    /// </remarks>
    public Func<SvgcProjectDrawing, string, string>? Rewrite { get; set; }

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

        if (Node is SvgcProjectGroup && _watched)
        {
            ShowDrawings();
        }

        ShowProperties(Node);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _watched = true;

        if (Node is SvgcProjectGroup)
        {
            ShowDrawings();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _watched = false;

        Release();
    }

    /// <summary>What the group builds, drawn on one canvas.</summary>
    /// <remarks>
    /// Laid out at the sizes the project builds them at, with nothing scaled to fit: the canvas has
    /// a zoom of its own, so a spread that is true to itself can be looked at whole or up close, and
    /// a drawing built at ×4 stands four times one built at ×1 wherever the zoom is. That difference
    /// is what a group's settings do, and the question a group tab exists to answer.
    /// </remarks>
    private void ShowDrawings()
    {
        Release();

        var drawings = ((SvgcProjectGroup)Node).Drawings.ToList();

        if (drawings.Count == 0)
        {
            Says("This group holds no drawings.");
            return;
        }

        var built = drawings.Select(Draw).ToList();

        Says(Trouble(built));

        _canvas.Show(Spread(built.Where(drawn => drawn.Svg is { }).ToList()));
    }

    /// <summary>One drawing built the way the project builds it, or why it could not be.</summary>
    private Drawn Draw(SvgcProjectDrawing drawing)
    {
        try
        {
            var document = SvgViewerDocument.Load(
                drawing.ResolvedInput,
                ProjectWorkspace.SizeOf(drawing),
                Rewrite is { } rewrite ? text => rewrite(drawing, text) : null);

            _loaded.Add(document);

            // The declared defaults. A plain load leaves a drawing with expressions in it rendering
            // its placeholders, which is not what the project builds; a parameter with no default at
            // all is refused, and placeholders are then the honest answer.
            try
            {
                document.Svg.SetExpressionValues(new Dictionary<string, ExprValue>());
            }
            catch (ExprException)
            {
            }

            return new Drawn(drawing, document.Svg, document.Svg.Picture?.CullRect.Size ?? default, null);
        }
        catch (Exception failure)
        {
            // Anything: this is user data reaching a parser, and it arrives as an XmlException, a
            // FormatException, one of the IO exceptions or the loader's own refusal. A narrower set
            // would eventually let one through, and one bad drawing would cost the whole tab.
            return new Drawn(drawing, null, default, failure.Message);
        }
    }

    /// <summary>
    /// Where each drawing goes: a grid as square as the count allows, every row standing on a line.
    /// </summary>
    /// <remarks>
    /// A column is as wide as the widest thing in it, caption included — measured rather than
    /// guessed, since a caption is usually wider than the icon it names and two that overlap say
    /// less than either. Sizes are in drawing units throughout: the canvas is what turns them into
    /// pixels, and it is the only thing that knows how big the pane is.
    /// </remarks>
    private static IReadOnlyList<SvgViewerPlacement> Spread(IReadOnlyList<Drawn> drawn)
    {
        if (drawn.Count == 0)
        {
            return Array.Empty<SvgViewerPlacement>();
        }

        var columns = (int)Math.Ceiling(Math.Sqrt(drawn.Count));
        var rows = (int)Math.Ceiling(drawn.Count / (double)columns);

        var largest = drawn.Max(one => Math.Max(one.Size.Width, one.Size.Height));
        var label = Math.Max(largest * 0.05f, 1f);
        var gap = label * 2f;

        using var font = new SKFont(SKTypeface.Default, label);

        var captions = drawn.Select(one => Caption(one.Drawing)).ToList();
        var widths = new float[columns];
        var heights = new float[rows];

        for (var index = 0; index < drawn.Count; index++)
        {
            var wanted = Math.Max(drawn[index].Size.Width, Widest(font, captions[index]));

            widths[index % columns] = Math.Max(widths[index % columns], wanted);
            heights[index / columns] = Math.Max(heights[index / columns], drawn[index].Size.Height);
        }

        var placed = new List<SvgViewerPlacement>(drawn.Count);
        var y = 0f;

        for (var row = 0; row < rows; row++)
        {
            var x = 0f;

            for (var column = 0; column < columns; column++)
            {
                var index = row * columns + column;

                if (index < drawn.Count)
                {
                    // Centred across its column and standing on the row's floor, so the captions of
                    // a row line up however differently sized the drawings above them are.
                    placed.Add(new SvgViewerPlacement(
                        drawn[index].Svg!,
                        new SKPoint(
                            x + (widths[column] - drawn[index].Size.Width) / 2f,
                            y + heights[row] - drawn[index].Size.Height),
                        captions[index],
                        label));
                }

                x += widths[column] + gap;
            }

            // Two lines of caption under the row, and a gap before the next.
            y += heights[row] + label * 3.4f + gap;
        }

        return placed;
    }

    private static float Widest(SKFont font, string caption)
        => caption.Split('\n').Max(line => font.MeasureText(line));

    /// <summary>Lets go of the drawings, and of the documents that own them.</summary>
    /// <remarks>
    /// The canvas first: a picture belongs to its document, so one still placed after the document
    /// is disposed is a surface drawing freed memory.
    /// </remarks>
    private void Release()
    {
        _canvas.Show(Array.Empty<SvgViewerPlacement>());

        foreach (var document in _loaded)
        {
            document.Dispose();
        }

        _loaded.Clear();

        Says(null);
    }

    /// <summary>Puts a line above the canvas, or takes it away.</summary>
    private void Says(string? said)
    {
        _notice.Text = said ?? string.Empty;
        _notice.IsVisible = said is { Length: > 0 };
    }

    /// <summary>What could not be read, in one line however many of them there were.</summary>
    private static string? Trouble(IReadOnlyList<Drawn> built)
    {
        var faults = built.Where(one => one.Fault is { }).ToList();

        if (faults.Count == 0)
        {
            return null;
        }

        var first = $"{Path.GetFileName(faults[0].Drawing.Input)} could not be read: {faults[0].Fault}";

        return faults.Count == 1 ? first : $"{first} And {faults.Count - 1} more could not be read.";
    }

    /// <summary>The canvas's own controls, which are the viewer's in the order the viewer has them.</summary>
    private Control Tools()
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(10, 8, 10, 6)
        };

        var bounds = new ToggleButton
        {
            Content = "Bounds",
            IsChecked = _canvas.ShowBounds,
            [ToolTip.TipProperty] = "Outline each drawing's own edges"
        };

        bounds.IsCheckedChanged += (_, _) => _canvas.ShowBounds = bounds.IsChecked == true;

        _canvas.ViewChanged += (_, _) =>
            _zoom.Text = (_canvas.Scale * 100d).ToString("0", CultureInfo.CurrentCulture) + "%";

        bar.Children.Add(Tool("Fit", "Fit to window", () => _canvas.Fit()));
        bar.Children.Add(Tool("1:1", "Actual size", () => _canvas.ActualSize()));
        bar.Children.Add(Tool("−", "Zoom out, or scroll down", () => _canvas.ZoomOut()));
        bar.Children.Add(_zoom);
        bar.Children.Add(Tool("+", "Zoom in, or scroll up", () => _canvas.ZoomIn()));
        bar.Children.Add(bounds);

        return bar;
    }

    private static Button Tool(string content, string tip, Action click)
    {
        var button = new Button { Content = content, [ToolTip.TipProperty] = tip };

        button.Click += (_, _) => click();

        return button;
    }

    /// <summary>What a drawing is called and what it is built at, over two lines.</summary>
    /// <remarks>
    /// Two, because one is about twice as wide as the drawings it sits under and the columns are
    /// sized to hold it.
    /// </remarks>
    private static string Caption(SvgcProjectDrawing drawing)
    {
        var name = drawing.EffectiveNamespace is { } space && drawing.EffectiveClass is { } className
            ? $"{space}.{className}"
            : drawing.EffectiveClass ?? drawing.EffectiveNamespace ?? "(unnamed)";

        return $"{Path.GetFileName(drawing.Input)}\n{name}   {Size(drawing)}";
    }

    private sealed record Drawn(SvgcProjectDrawing Drawing, SKSvg? Svg, SKSize Size, string? Fault);

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

    /// <remarks>
    /// No Apple type identifier, for the reason the project's has none: nothing registers
    /// <c>.recipe</c> with macOS, so it is given a type conforming to nothing and naming
    /// <c>public.xml</c> greys every recipe out rather than letting one be picked.
    /// </remarks>
    private static readonly FilePickerFileType Recipes = new("Svg Recipes")
    {
        Patterns = new[] { "*.recipe" },
        MimeTypes = new[] { "application/xml" }
    };

    /// <summary>What a new recipe is written with: the root, and nothing said in it yet.</summary>
    /// <remarks>
    /// The namespace and no more. A seeded parameter and let applied cleanly and put a slider on the
    /// drawing straight away, and a survey of the colours the drawings paint was written under it as
    /// commented-out rules — but both are somebody else's opening line to read and delete before the
    /// file says what you meant. The root is the only part of it that cannot be typed wrong.
    /// </remarks>
    private const string Skeleton = """
        <?xml version="1.0" encoding="utf-8"?>
        <recipe xmlns="https://svg.skia/expr/1.0">
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
