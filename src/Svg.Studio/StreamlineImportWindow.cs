// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Svg.Expressions;
using Svg.Expressions.Recipes;
using Svg.SourceEditing;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>Streamline's icons on their way into a group: each batch drawn through every template it can take, to choose one.</summary>
/// <remarks>
/// Shown modally, and answers true for Import and false for Cancel. What it does to the project's
/// templates is a step of its own each time, kept whichever it answers.
/// </remarks>
public sealed class StreamlineImportWindow : Window
{
    private const double CardSize = 64d;

    private const double PreviewSize = 56d;

    /// <summary>How many of a batch are drawn as they will look; the rest are counted.</summary>
    private const int Previewed = 8;

    // Immutable, as a SolidColorBrush keeps the dispatcher it was made on, and a static one outlives it.
    private static readonly IBrush s_sure = new ImmutableSolidColorBrush(Color.Parse("#2EA043"));
    private static readonly IBrush s_check = new ImmutableSolidColorBrush(Color.Parse("#D29922"));

    // Drawn, since the ⋯ character comes out a speck in the fonts a window falls back on.
    private static readonly Geometry s_more = Geometry.Parse("M2 8a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0-3 0zm4.5 0a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0-3 0zm4.5 0a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0-3 0z");

    // Each drawing goes on whichever of its theme's light plate and the dark one its ink stands out on more.
    private static readonly SKColor s_lightPlate = new(0xF2, 0xF2, 0xF4);
    private static readonly SKColor s_lightPlateInDark = new(0xC8, 0xC8, 0xCE);
    private static readonly SKColor s_darkPlate = new(0x2E, 0x2E, 0x34);

    private readonly StreamlineImport _import;
    private readonly MainWindow _window;

    /// <summary>What each canvas shows, which the window owns and disposes.</summary>
    private readonly Dictionary<SvgViewerCanvas, SvgViewerDocument> _drawn = new();

    /// <summary>The booleans every preview is drawn with, whatever each drawing would seed them to.</summary>
    private readonly Dictionary<string, bool> _flags = new(StringComparer.Ordinal);

    private readonly WrapPanel _toggles = new() { ItemSpacing = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _sections = new() { Spacing = 28, Margin = new Thickness(20, 16) };
    private readonly Expander _other = new() { Header = "Other templates" };
    private readonly TextBlock _said = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _declares = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Counted up by each rebuild and the close, so a drawing posted for a canvas that is gone is not made.</summary>
    private int _generation;

    public StreamlineImportWindow(StreamlineImport import, MainWindow window)
    {
        _import = import ?? throw new ArgumentNullException(nameof(import));
        _window = window ?? throw new ArgumentNullException(nameof(window));

        var count = import.Rows.Sum(row => row.Icons.Count);

        Title = import.Updating is { } updating
            ? $"Update {ProjectWorkspace.Label(updating)}"
            : $"Import {count} icon{(count == 1 ? "" : "s")} into {ProjectWorkspace.Label(import.Target)}";
        Width = 860;
        Height = 620;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // A card is chosen by its outline alone: Fluent's accent fill under it drowned the drawing.
        Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(PaddingProperty, new Thickness(0)),
                new Setter(MarginProperty, new Thickness(0, 0, 8, 8)),
                new Setter(CornerRadiusProperty, new CornerRadius(8))
            }
        });
        Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":selected").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) }
        });
        Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":selected").Descendant().OfType<Border>().Class("card"))
        {
            Setters = { new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("TabItemHeaderSelectedPipeFill")) }
        });

        // Sized to the header, so the chevron stays where it was clicked rather than following the content's width.
        Styles.Add(new Style(x => x.OfType<Expander>().Template().OfType<ToggleButton>().Name("ExpanderHeader"))
        {
            Setters = { new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Left) }
        });

        // Disclosures rather than Fluent's bars, which the dark theme paints near black on this window's grey.
        foreach (var key in new[] { "Background", "BackgroundPointerOver", "BackgroundPressed", "BorderBrush", "BorderBrushPointerOver", "BorderBrushPressed" })
        {
            Resources["ExpanderHeader" + key] = Brushes.Transparent;
        }

        Resources["ExpanderContentBackground"] = Brushes.Transparent;
        Resources["ExpanderContentBorderBrush"] = Brushes.Transparent;
        Resources["ExpanderHeaderPadding"] = new Thickness(0, 0, 4, 0);
        Resources["ExpanderContentPadding"] = new Thickness(0, 6, 0, 4);
        Resources["ExpanderMinHeight"] = 32d;

        var accept = new Button
        {
            Content = import.Updating is { } ? "Update" : $"Import {count}",
            Classes = { "accent" },
            IsDefault = true
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true };

        // Only where a drop asked: a drop that was told not to still asks about an unsure match, and the box would then say nothing.
        var again = import.Dropped && StudioSettings.DropAsks
            ? new CheckBox
            {
                Content = "Don't ask again when dropping",
                Margin = new Thickness(0, 0, 16, 0),
                [ToolTip.TipProperty] = "Import sure matches straight away from now on. Settings can bring the question back."
            }
            : null;

        accept.Click += (_, _) =>
        {
            if (again?.IsChecked is true)
            {
                StudioSettings.DropAsks = false;
            }

            Close(true);
        };
        cancel.Click += (_, _) => Close(false);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, accept } };
        var closing = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        closing.Children.Add(buttons);

        if (again is { })
        {
            DockPanel.SetDock(again, Dock.Right);
            closing.Children.Add(again);
        }

        closing.Children.Add(_declares);

        var foot = new StackPanel { Spacing = 10, Margin = new Thickness(20, 10, 20, 16), Children = { _said, closing } };

        var preview = new DockPanel { Margin = new Thickness(20, 14, 20, 0) };
        var previewing = new TextBlock { Text = "Preview as", Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(previewing, Dock.Left);
        preview.Children.Add(previewing);
        preview.Children.Add(_toggles);
        _toggles.Children.CollectionChanged += (_, _) => preview.IsVisible = _toggles.Children.Count > 0;
        preview.IsVisible = false;

        var body = new DockPanel();
        DockPanel.SetDock(preview, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        body.Children.Add(preview);
        body.Children.Add(foot);
        body.Children.Add(new ScrollViewer { Content = _sections, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        Content = body;

        ActualThemeVariantChanged += (_, _) =>
        {
            foreach (var canvas in this.GetVisualDescendants().OfType<SvgViewerCanvas>())
            {
                Plate(canvas);
            }
        };

        Build();
    }

    /// <summary>Why the last change to a template could not be made, or what became of it, or null.</summary>
    public string? Said => _said.IsVisible ? _said.Text : null;

    /// <summary>Asks for a new name for <paramref name="template"/>, and gives it that name as one step.</summary>
    public async Task RenameAsync(string template)
    {
        if (Index(template) is not (>= 0 and var index))
        {
            return;
        }

        var workspace = _import.Workspace;
        var text = workspace.Document.Root.Templates[index].Text;

        if (await _window.AskTemplateName(template, null, this).ConfigureAwait(true) is not { } answer || answer.Name == template)
        {
            return;
        }

        // The project holds it, so it reads.
        var read = SvgSourceDocument.Read(text, out _)!;

        read.Document.Root!.SetAttributeValue("name", answer.Name);

        if (TemplateLibrary.Put(workspace, index, read.ToText()) is { } refusal)
        {
            Say(refusal);

            return;
        }

        Refresh((template, answer.Name));
    }

    /// <summary>Asks for <paramref name="template"/>'s XML edited, and writes it back as one step.</summary>
    /// <remarks>Only what the block cannot hold is refused; a recipe that does not parse is kept, and is then one of the other templates.</remarks>
    public async Task EditAsync(string template)
    {
        if (Index(template) is not (>= 0 and var index))
        {
            return;
        }

        var workspace = _import.Workspace;
        var root = workspace.Document.Root;

        if (await _window.AskTemplateText(root.Templates[index].Text, this).ConfigureAwait(true) is not { } edited)
        {
            return;
        }

        if (TemplateLibrary.Put(workspace, index, edited) is { } refusal)
        {
            Say(refusal);

            return;
        }

        var name = root.Templates[index].Name;

        Refresh((template, name));

        if (new TemplateLibrary(root).Templates[index].Error is { } error)
        {
            Say($"{name} does not parse, so it is under Other templates: {error}");
        }
        else if (!_import.Rows.Any(row => row.Choices.Contains(name)))
        {
            Say($"{name} no longer fits these icons, so it is under Other templates.");
        }
    }

    /// <summary>Takes <paramref name="template"/> out of the project, as one step, once that is confirmed.</summary>
    /// <remarks>Confirmed, since the step's undo is on the window behind, which this one keeps out of reach.</remarks>
    public async Task DeleteAsync(string template)
    {
        if (Index(template) is not (>= 0 and var index) || !await _window.ConfirmDeleteTemplate(template, this).ConfigureAwait(true))
        {
            return;
        }

        var refusal = TemplateLibrary.Put(_import.Workspace, index, null);

        Refresh(null);
        Say(refusal);
    }

    /// <summary>Keeps what <paramref name="row"/> does to its first icon as a template of the project's, asking what to call it.</summary>
    public async Task SaveAsTemplateAsync(StreamlineRow row)
    {
        var workspace = _import.Workspace;
        var download = row.Downloads[0];
        var family = download.Icon.FamilyName ?? download.Prepared.Family;

        try
        {
            // Once before asking, so a row that turns nothing into an expression says so before anything is typed.
            TemplateLibrary.Save(row.Chosen, download.Prepared, row.Recipe(0), null);
        }
        catch (SvgRecipeException failure)
        {
            Say(failure.Message);

            return;
        }

        if (await _window.AskTemplateName(family is { } ? $"{row.Chosen} ({family})" : row.Chosen, download.Prepared.Family is { } ? family : null, this)
                .ConfigureAwait(true) is not { } answer)
        {
            return;
        }

        var text = TemplateLibrary.Save(answer.Name, download.Prepared, row.Recipe(0), answer.OnlyFamily ? download.Prepared.Family : null);

        if (TemplateLibrary.Put(workspace, workspace.Document.Root.Templates.Count, text) is { } refusal)
        {
            Say(refusal);

            return;
        }

        Refresh(null);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        _generation++;
        Forget();
    }

    private int Index(string template)
    {
        var templates = _import.Workspace.Document.Root.Templates;

        for (var index = 0; index < templates.Count; index++)
        {
            if (templates[index].Name == template)
            {
                return index;
            }
        }

        return -1;
    }

    private void Refresh((string From, string To)? renamed)
    {
        _import.Refresh(renamed);
        Say(null);
        Build();
    }

    private void Say(string? said)
    {
        _said.Text = said;
        _said.IsVisible = said is { };
    }

    /// <summary>Every section, the other templates and the declarations, from the rows as they are.</summary>
    private void Build()
    {
        // Kept through the rebuild a template change makes, which would otherwise close the colours it was made from.
        var colouring = _sections.GetVisualDescendants().OfType<Expander>().Where(expander => expander.IsExpanded).Select(expander => expander.Tag).ToHashSet();

        _generation++;
        Forget();
        _sections.Children.Clear();

        foreach (var row in _import.Rows)
        {
            _sections.Children.Add(Section(row, colouring.Contains(row)));
        }

        // Scrolled with the sections, since a project's list of them can be longer than the window.
        _sections.Children.Add(_other);

        var offered = _import.Rows.SelectMany(row => row.Choices).ToHashSet(StringComparer.Ordinal);
        var root = _import.Workspace.Document.Root;

        // Keep colours is the library's own and last, and not the project's to manage.
        var other = new TemplateLibrary(root).Templates.Take(root.Templates.Count).Where(entry => !offered.Contains(entry.Name)).ToList();
        var list = new StackPanel { Spacing = 6 };

        foreach (var entry in other)
        {
            var name = new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var line = new DockPanel();
            var menu = Menu(entry.Name);

            DockPanel.SetDock(menu, Dock.Right);
            line.Children.Add(menu);
            line.Children.Add(entry.Error is { } error
                ? new StackPanel { Children = { name, new TextBlock { Text = error, FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap } } }
                : name);
            list.Children.Add(line);
        }

        _other.Header = $"Other templates ({other.Count})";
        _other.Content = list;
        _other.IsVisible = other.Count > 0;

        Declare();
    }

    private Control Section(StreamlineRow row, bool colouring)
    {
        var icons = row.Icons;
        var names = string.Join(", ", icons.Take(3).Select(icon => icon.Name));
        var family = icons[0].FamilyName ?? icons[0].FamilySlug ?? "Streamline";

        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = icons.Count > 3 ? $"{names} and {icons.Count - 3} more" : names,
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 15,
                    MaxWidth = 520,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    [ToolTip.TipProperty] = $"{icons.Count} icon{(icons.Count == 1 ? "" : "s")} from {family}"
                },
                new TextBlock { Text = family, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Bottom }
            }
        };

        var dot = new Ellipse { Width = 10, Height = 10, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2) };
        var cards = new ListBox
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel()),
            SelectionMode = SelectionMode.Single | SelectionMode.AlwaysSelected,
            AutoScrollToSelectedItem = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };

        ScrollViewer.SetHorizontalScrollBarVisibility(cards, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(cards, ScrollBarVisibility.Disabled);

        foreach (var name in row.Choices)
        {
            cards.Items.Add(new ListBoxItem { Content = Card(row, name, name == row.Suggested ? dot : null), [AutomationProperties.NameProperty] = name });
        }

        cards.SelectedIndex = row.Choices.ToList().IndexOf(row.Chosen);

        var previews = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        var canvases = new List<SvgViewerCanvas>();

        foreach (var plate in icons.Take(Previewed).Select(_ => Plated(PreviewSize)))
        {
            previews.Children.Add(plate);
            canvases.Add((SvgViewerCanvas)plate.Child!);
        }

        if (icons.Count > Previewed)
        {
            previews.Children.Add(new TextBlock { Text = $"and {icons.Count - Previewed} more", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        }

        var colours = new WrapPanel { ItemSpacing = 16, LineSpacing = 10 };
        var save = new Button { Content = "Save as template…", Margin = new Thickness(0, 10, 0, 0) };

        save.Click += async (_, _) => await SaveAsTemplateAsync(row).ConfigureAwait(true);

        cards.SelectionChanged += (_, _) =>
        {
            if (cards.SelectedIndex >= 0 && row.Choices[cards.SelectedIndex] != row.Chosen)
            {
                row.Chosen = row.Choices[cards.SelectedIndex];
                Colours();
                Show();
            }
        };

        Colours();
        Show();

        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                heading,
                cards,
                new TextBlock { Text = "How they will look", Opacity = 0.7, FontSize = 12 },
                previews,
                new Expander { Header = "Colours", Tag = row, IsExpanded = colouring, Content = new StackPanel { Children = { colours, save } } }
            }
        };

        // Everything a template or a role changes: the dot, what the icons look like and what is declared.
        void Show()
        {
            var sure = row.Sure;

            dot.Fill = sure ? s_sure : s_check;
            ToolTip.SetTip(dot, sure ? "Remembered, or well ahead of the next template" : "Check this: another template came close, or a colour was changed by hand");
            AutomationProperties.SetName(dot, sure ? "Template is a sure match" : "Check the template");

            for (var index = 0; index < canvases.Count; index++)
            {
                var at = index;

                Post(() => Draw(canvases[at], () => row.Text(at)));
            }

            Declare();
        }

        // Rebuilt when the template changes, since what a colour can become is the template's.
        void Colours()
        {
            colours.Children.Clear();

            foreach (var colour in row.Colours)
            {
                var note = new TextBlock { Text = row.RoleNote(colour), FontSize = 11, Opacity = 0.8, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis };
                var role = new ComboBox
                {
                    ItemsSource = row.RoleChoices(colour),
                    SelectedItem = row.Role(colour),
                    MinWidth = 150,
                    [ToolTip.TipProperty] = "What this colour becomes"
                };

                ToolTip.SetTip(note, colour);
                role.SelectionChanged += (_, _) =>
                {
                    if (role.SelectedItem is string chosen && chosen != row.Role(colour))
                    {
                        row.SetRole(colour, chosen);
                        note.Text = row.RoleNote(colour);
                        Show();
                    }
                };

                var swatch = new Border
                {
                    Width = 18,
                    Height = 18,
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.Parse("#80808080")),
                    Background = SvgRecipeColor.TryParse(colour, out var argb) ? new SolidColorBrush(Color.FromUInt32((uint)argb)) : null,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };

                var line = new DockPanel();
                DockPanel.SetDock(swatch, Dock.Left);
                line.Children.Add(swatch);
                line.Children.Add(role);

                colours.Children.Add(new StackPanel { Spacing = 4, Children = { line, note } });
            }
        }
    }

    /// <summary>The first icon drawn through <paramref name="template"/>, named, with the dot where it is the suggestion.</summary>
    private Control Card(StreamlineRow row, string template, Ellipse? dot)
    {
        var face = Plated(CardSize);
        var canvas = (SvgViewerCanvas)face.Child!;
        var error = new TextBlock
        {
            FontSize = 10,
            Opacity = 0.8,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsVisible = false
        };

        var card = new Grid
        {
            Children =
            {
                new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        face,
                        new TextBlock
                        {
                            Text = template,
                            FontSize = 12,
                            TextAlignment = TextAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            [ToolTip.TipProperty] = template
                        },
                        error
                    }
                }
            }
        };

        if (dot is { })
        {
            card.Children.Add(dot);
        }

        if (template != TemplateLibrary.KeepColoursName)
        {
            var menu = Menu(template);

            menu.HorizontalAlignment = HorizontalAlignment.Right;
            menu.VerticalAlignment = VerticalAlignment.Top;
            card.Children.Add(menu);
        }

        Post(() => Draw(canvas, () => row.Text(0, template), error));

        // Wide enough for the dot and the menu to sit either side of the drawing rather than on it.
        return new Border
        {
            Classes = { "card" },
            Width = 124,
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            Child = card
        };
    }

    /// <summary>The ⋯ button that renames, edits or deletes <paramref name="template"/>.</summary>
    private Button Menu(string template)
    {
        var rename = new MenuItem { Header = "Rename…" };
        var edit = new MenuItem { Header = "Edit XML…" };
        var delete = new MenuItem { Header = "Delete" };

        rename.Click += async (_, _) => await RenameAsync(template).ConfigureAwait(true);
        edit.Click += async (_, _) => await EditAsync(template).ConfigureAwait(true);
        delete.Click += async (_, _) => await DeleteAsync(template).ConfigureAwait(true);

        return new Button
        {
            Content = new PathIcon { Data = s_more, Width = 14, Height = 14 },
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Flyout = new MenuFlyout { Items = { rename, edit, delete } },
            [AutomationProperties.NameProperty] = $"{template} options",
            [ToolTip.TipProperty] = $"Rename, edit or delete {template}"
        };
    }

    private void Declare()
    {
        var adding = _import.Rows.SelectMany(row => row.Declares).Distinct(StringComparer.Ordinal).ToList();

        _declares.Text = adding.Count == 0
            ? "Nothing to declare"
            : $"Adds {string.Join(", ", adding)} to {ProjectWorkspace.Label(_import.Target)}";
    }

    /// <summary>A canvas on a plate <paramref name="size"/> across, inset so a drawing that reaches its own edges does not touch the plate's.</summary>
    private Border Plated(double size)
    {
        var canvas = new SvgViewerCanvas { IsZoomEnabled = false, IsPanEnabled = false, IsPageOutlined = false };
        var plate = new Border
        {
            Width = size,
            Height = size,
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = canvas
        };

        Plate(canvas);

        return plate;
    }

    private void Plate(SvgViewerCanvas canvas)
    {
        var light = ActualThemeVariant == ThemeVariant.Dark ? s_lightPlateInDark : s_lightPlate;
        var ground = _drawn.TryGetValue(canvas, out var document) && Ink(document.Svg.Picture) is { } ink
                     && Contrast(ink, s_darkPlate) > Contrast(ink, light)
            ? s_darkPlate
            : light;

        canvas.Background = ground;
        ((Border)canvas.Parent!).Background = new SolidColorBrush(Color.FromUInt32((uint)ground));

        // WCAG's contrast ratio.
        static double Contrast(double luminance, SKColor plate)
        {
            var other = SvgRecipeColor.Luminance((int)(uint)plate);

            return (Math.Max(luminance, other) + 0.05d) / (Math.Min(luminance, other) + 0.05d);
        }
    }

    /// <summary>The luminance of what <paramref name="picture"/> paints, by coverage, or null where it paints nothing.</summary>
    private static double? Ink(SKPicture? picture)
    {
        const int Side = 24;

        if (picture is not { CullRect: { Width: > 0f, Height: > 0f } cull })
        {
            return null;
        }

        using var bitmap = new SKBitmap(Side, Side);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(Side / cull.Width, Side / cull.Height);
            canvas.Translate(-cull.Left, -cull.Top);
            canvas.DrawPicture(picture);
        }

        var (covered, lit) = (0d, 0d);

        for (var y = 0; y < Side; y++)
        {
            for (var x = 0; x < Side; x++)
            {
                var pixel = bitmap.GetPixel(x, y);

                covered += pixel.Alpha;
                lit += pixel.Alpha * SvgRecipeColor.Luminance((int)(uint)pixel);
            }
        }

        return covered > 0d ? lit / covered : null;
    }

    /// <summary>Runs <paramref name="drawing"/> once the window is idle, unless it has been rebuilt or closed since.</summary>
    private void Post(Action drawing)
    {
        var generation = _generation;

        Dispatcher.UIThread.Post(
            () =>
            {
                if (generation == _generation)
                {
                    drawing();
                }
            },
            DispatcherPriority.Background);
    }

    /// <summary>Shows <paramref name="text"/> on <paramref name="canvas"/> in place of what it showed, or why it cannot be made in <paramref name="error"/>.</summary>
    private void Draw(SvgViewerCanvas canvas, Func<string> text, TextBlock? error = null)
    {
        Forget(canvas);

        string svg;

        try
        {
            svg = text();
        }
        catch (SvgRecipeException failure)
        {
            if (error is { })
            {
                error.Text = failure.Message;
                error.IsVisible = true;
                ToolTip.SetTip(error, failure.Message);
            }

            return;
        }

        if (StreamlineRow.Drawn(svg, _import.Target) is not { } document)
        {
            return;
        }

        _drawn[canvas] = document;

        var seeded = GroupPanel.Seeded(document);

        foreach (var parameter in document.Declarations.Parameters.Where(one => one.Type == ExprType.Boolean && !_flags.ContainsKey(one.Name)))
        {
            Toggle(parameter.Name, seeded.TryGetValue(parameter.Name, out var value) && value.AsBoolean);
        }

        Bind(canvas, document);
        canvas.Svg = document.Svg;
    }

    /// <summary>A toggle for <paramref name="name"/>, which every preview reaching it is drawn with.</summary>
    private void Toggle(string name, bool seeded)
    {
        var toggle = new CheckBox { Content = name, IsChecked = seeded };

        _flags[name] = seeded;
        toggle.IsCheckedChanged += (_, _) =>
        {
            _flags[name] = toggle.IsChecked == true;

            foreach (var (canvas, document) in _drawn)
            {
                Bind(canvas, document);
            }
        };

        _toggles.Children.Add(toggle);
    }

    private void Bind(SvgViewerCanvas canvas, SvgViewerDocument document)
    {
        var values = GroupPanel.Seeded(document);

        foreach (var (name, flag) in _flags)
        {
            if (values.ContainsKey(name))
            {
                values[name] = ExprValue.Boolean(flag);
            }
        }

        try
        {
            document.Svg.SetExpressionValues(values);
        }
        catch (ExprException)
        {
            // A value the drawing will not take leaves its last rendering up, as on a board.
        }

        Plate(canvas);
        canvas.Publish();
    }

    /// <summary>Lets go of what <paramref name="canvas"/> shows, or of every canvas's where none is given.</summary>
    private void Forget(SvgViewerCanvas? canvas = null)
    {
        foreach (var (shown, document) in _drawn.Where(one => canvas is null || one.Key == canvas).ToList())
        {
            // Off the canvas first, so it never draws a document that is gone.
            shown.Svg = null;
            document.Dispose();
            _drawn.Remove(shown);
        }
    }
}
