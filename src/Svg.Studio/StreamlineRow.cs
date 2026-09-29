// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Svg.Expressions;
using Svg.Expressions.Recipes;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>An icon as Streamline served it, and as a template sees it.</summary>
internal sealed record StreamlineDownload(StreamlineIcon Icon, string Svg, TemplateIcon Prepared);

/// <summary>
/// One decision in an import: icons of one family with the same colours, the template they go in
/// through, and what each colour becomes.
/// </summary>
public sealed class StreamlineRow : IDisposable
{
    public const string Keep = "keep";

    private static readonly IBrush s_sure = new SolidColorBrush(Color.Parse("#2EA043"));
    private static readonly IBrush s_check = new SolidColorBrush(Color.Parse("#D29922"));

    private readonly IReadOnlyList<StreamlineDownload> _icons;
    private readonly IReadOnlyList<IReadOnlyList<TemplateSuggestion>> _suggestions;
    private readonly ProjectGroup? _target;
    private readonly IReadOnlyList<string> _names;

    /// <summary>The colours somebody has given a role by hand, and null for one they kept.</summary>
    private readonly Dictionary<string, string?> _roles = new(StringComparer.Ordinal);

    private readonly Dictionary<string, ComboBox> _roleBoxes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _labels = new(StringComparer.Ordinal);
    private readonly Ellipse _dot = new() { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _swatches = new() { Spacing = 4 };
    private readonly StackPanel _toggles = new() { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private SvgViewerDocument? _document;
    private Dictionary<string, ExprValue> _values = new(StringComparer.Ordinal);

    internal StreamlineRow(IReadOnlyList<StreamlineDownload> icons, TemplateLibrary? library, ProjectGroup? target)
    {
        _icons = icons;
        _target = target;
        _suggestions = icons
            .Select(icon => library is { } && target is { } ? library.Suggest(icon.Prepared, target) : Array.Empty<TemplateSuggestion>())
            .ToList();
        _names = target is { } ? ColourNames(target) : Array.Empty<string>();
        SaveAs.IsEnabled = target is { };

        var offered = _suggestions[0].Select(suggestion => suggestion.Template.Name).ToList();

        Template = new ComboBox
        {
            ItemsSource = offered.Count > 0 ? offered : new[] { TemplateLibrary.KeepColoursName },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            [ToolTip.TipProperty] = "The template these icons go in through"
        };

        Template.SelectionChanged += (_, _) =>
        {
            _roles.Clear();

            if (Template.SelectedItem is string chosen)
            {
                foreach (var icon in _icons.Select(one => one.Prepared).DistinctBy(one => one.ChoiceKey))
                {
                    TemplateLibrary.Remember(icon, chosen);
                }
            }

            Show();

            Chosen?.Invoke(this, EventArgs.Empty);
        };

        var names = string.Join(", ", icons.Take(3).Select(icon => icon.Icon.Name));

        var heading = new TextBlock
        {
            Text = icons.Count > 3 ? $"{names} and {icons.Count - 3} more" : names,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            [ToolTip.TipProperty] = $"{icons.Count} icon{(icons.Count == 1 ? "" : "s")} from {icons[0].Icon.FamilyName ?? icons[0].Icon.FamilySlug ?? "Streamline"}"
        };

        var choosing = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_dot, Dock.Left);
        _dot.Margin = new Thickness(0, 0, 6, 0);
        choosing.Children.Add(_dot);
        choosing.Children.Add(Template);

        var preview = new DockPanel();
        DockPanel.SetDock(Preview, Dock.Left);
        preview.Children.Add(Preview);
        preview.Children.Add(_toggles);

        View = new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse("#40808080")),
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { heading, choosing, _swatches, preview, Declares, Spread, SaveAs }
            }
        };

        Show();
    }

    /// <summary>Raised when the template is changed by hand, for the panel to offer it to rows like this one.</summary>
    public event EventHandler? Chosen;

    public Control View { get; }

    public IReadOnlyList<StreamlineIcon> Icons => _icons.Select(icon => icon.Icon).ToList();

    public string? Family => _icons[0].Prepared.Family;

    /// <summary>The eligible templates, best first, and Keep colours.</summary>
    public ComboBox Template { get; }

    /// <summary>For each colour of the icons, what it is to become: <see cref="Keep"/> or a colour the target can name.</summary>
    public IReadOnlyDictionary<string, ComboBox> Roles => _roleBoxes;

    /// <summary>One per boolean the bound drawing reaches, seeded as the group panel seeds them.</summary>
    public IReadOnlyList<CheckBox> Toggles => _toggles.Children.OfType<CheckBox>().ToList();

    public SvgViewerCanvas Preview { get; } = new()
    {
        Width = 72,
        Height = 72,
        IsZoomEnabled = false,
        IsPanEnabled = false,
        Margin = new Thickness(0, 0, 8, 0)
    };

    public TextBlock Declares { get; } = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };

    /// <summary>Offers the template just chosen to the other rows of the family.</summary>
    public Button Spread { get; } = new() { IsVisible = false };

    /// <summary>Keeps what the row does now, template and colours changed by hand, as a template of the project's.</summary>
    public Button SaveAs { get; } = new() { Content = "Save as template…" };

    /// <summary>Whether <paramref name="template"/> fits these icons.</summary>
    public bool Offers(string template) => Template.Items.OfType<string>().Contains(template);

    internal IReadOnlyList<StreamlineDownload> Downloads => _icons;

    /// <summary>What the icon at <paramref name="index"/> goes in through, or null to go in as it is.</summary>
    public SvgRecipe? Recipe(int index)
    {
        var bound = Recipe(_suggestions[index], Template.SelectedItem as string);

        return _roles.Count == 0 ? bound : Overridden(bound);
    }

    /// <summary>The recipe of the suggestion called <paramref name="chosen"/>, or null for Keep colours or one not suggested.</summary>
    internal static SvgRecipe? Recipe(IEnumerable<TemplateSuggestion> suggestions, string? chosen)
        => chosen == TemplateLibrary.KeepColoursName
            ? null
            : suggestions.FirstOrDefault(suggestion => suggestion.Template.Name == chosen)?.Recipe;

    /// <summary>The icon at <paramref name="index"/> with its template applied, as it will be imported.</summary>
    public string Text(int index)
    {
        var text = _icons[index].Prepared.Text;

        return Recipe(index) is { } recipe
            ? SvgRecipeRewriter.Apply(TemplateLibrary.Sized(text, recipe), recipe).Svg
            : text;
    }

    public void Dispose()
    {
        Preview.Svg = null;
        _document?.Dispose();
        _document = null;
    }

    /// <summary>
    /// The bound template with the colours given a role by hand written as rules of their own: an
    /// ordinary slot-free recipe, which the import applies as it would any other, bringing only the
    /// declarations its rules still reach.
    /// </summary>
    private SvgRecipe? Overridden(SvgRecipe? bound)
    {
        var ns = (XNamespace)SvgRecipe.Namespace;
        var root = TemplateLibrary.Framed(bound);
        var rules = (bound?.Rules ?? Array.Empty<SvgReplaceRule>())
            .Where(rule => rule.Name != SvgRecipeValue.ColorName || !_roles.ContainsKey(rule.Key))
            .Select(rule => (rule.Name, Value: rule.ValueText, rule.Expression))
            .Concat(_roles.Where(role => role.Value is { }).Select(role => (Name: SvgRecipeValue.ColorName, Value: role.Key, Expression: role.Value!)))
            .ToList();

        var declarations = Reached(bound?.Declarations ?? Array.Empty<XElement>(), rules.Select(rule => rule.Expression));

        if (declarations.Count > 0)
        {
            root.Add(new XElement(ns + "code", declarations.Select(declaration => new XElement(declaration))));
        }

        foreach (var (name, value, expression) in rules)
        {
            root.Add(new XElement(ns + "replace", new XAttribute(name, value), expression));
        }

        return rules.Count > 0 ? SvgRecipe.Parse(root.ToString()) : null;
    }

    /// <summary>The declarations <paramref name="expressions"/> name, and those they name in turn.</summary>
    /// <remarks>By whole word, which can keep one a string literal happens to spell, never drop one that is used.</remarks>
    private static List<XElement> Reached(IReadOnlyList<XElement> declarations, IEnumerable<string> expressions)
    {
        var texts = expressions.ToList();
        var kept = new List<XElement>();

        for (var more = true; more;)
        {
            more = false;

            foreach (var declaration in declarations.Except(kept).ToList())
            {
                if (((string?)declaration.Attribute("name"))?.Trim() is { Length: > 0 } name
                    && texts.Any(text => Regex.IsMatch(text, $@"(?<![\w.]){Regex.Escape(name)}(?!\w)")))
                {
                    kept.Add(declaration);
                    texts.Add(declaration.ToString());
                    more = true;
                }
            }
        }

        return declarations.Where(kept.Contains).ToList();
    }

    private void Show()
    {
        Swatches();
        Showing();
    }

    /// <summary>Everything a role changes: the dot, the labels, the preview and what is declared.</summary>
    private void Showing()
    {
        var sure = _roles.Count == 0 && Template.SelectedIndex == 0 && _suggestions[0].FirstOrDefault()?.Sure == true;

        _dot.Fill = sure ? s_sure : s_check;
        ToolTip.SetTip(_dot, sure ? "Remembered, or well ahead of the next template" : "Check this: another template came close, or a colour was changed by hand");

        // The slot is the template's, and an overridden recipe is slot-free, so it is read from the template.
        var recipe = Recipe(0);
        var bound = Recipe(_suggestions[0], Template.SelectedItem as string);

        foreach (var (colour, label) in _labels)
        {
            var rule = Colour(recipe, colour);
            var slot = _roles.ContainsKey(colour) ? "by hand" : Colour(bound, colour)?.Slot?.Name;

            label.Text = rule is null ? "kept" : slot is { } ? $"{slot} → {rule.Expression}" : $"→ {rule.Expression}";
        }

        Draw();
        Declare();
    }

    private void Swatches()
    {
        _swatches.Children.Clear();
        _roleBoxes.Clear();
        _labels.Clear();

        var recipe = Recipe(0);

        foreach (var value in _icons[0].Prepared.Survey.Where(one => one.Name == SvgRecipeValue.ColorName))
        {
            var colour = value.Text;
            var written = Colour(recipe, colour)?.Expression;
            var roles = new List<string> { Keep };

            // An expression the template writes that is not a bare name is still what the colour becomes, so it is offered too.
            if (written is { } && !_names.Contains(written))
            {
                roles.Add(written);
            }

            roles.AddRange(_names);

            var role = new ComboBox
            {
                ItemsSource = roles,
                SelectedItem = written ?? Keep,
                IsEnabled = _target is { },
                MinWidth = 110,
                [ToolTip.TipProperty] = "What this colour becomes"
            };

            role.SelectionChanged += (_, _) =>
            {
                _roles[colour] = role.SelectedItem as string is { } chosen && chosen != Keep ? chosen : null;

                Showing();
            };

            var label = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                [ToolTip.TipProperty] = colour
            };

            var swatch = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#80808080")),
                Background = SvgRecipeColor.TryParse(colour, out var argb) ? new SolidColorBrush(Color.FromUInt32((uint)argb)) : null,
                VerticalAlignment = VerticalAlignment.Center
            };

            var line = new DockPanel();

            DockPanel.SetDock(swatch, Dock.Left);
            DockPanel.SetDock(role, Dock.Right);
            line.Children.Add(swatch);
            line.Children.Add(role);
            line.Children.Add(label);

            _roleBoxes[colour] = role;
            _labels[colour] = label;
            _swatches.Children.Add(line);
        }
    }

    private static SvgReplaceRule? Colour(SvgRecipe? recipe, string colour)
        => recipe?.Rules.FirstOrDefault(rule => rule.Name == SvgRecipeValue.ColorName && rule.Key == colour);

    /// <summary>The first icon as it will be imported, drawn through the target's declarations, with a toggle per boolean it reaches.</summary>
    private void Draw()
    {
        Dispose();
        _toggles.Children.Clear();

        if ((_document = Drawn(Text(0), _target)) is null)
        {
            return;
        }

        _values = GroupPanel.Seeded(_document);

        foreach (var parameter in _document.Declarations.Parameters.Where(one => one.Type == ExprType.Boolean))
        {
            var name = parameter.Name;
            var toggle = new CheckBox
            {
                Content = name,
                IsChecked = _values.TryGetValue(name, out var seeded) && seeded.AsBoolean
            };

            toggle.IsCheckedChanged += (_, _) =>
            {
                _values[name] = ExprValue.Boolean(toggle.IsChecked == true);

                Bind();
            };

            _toggles.Children.Add(toggle);
        }

        Bind();

        Preview.Svg = _document.Svg;
    }

    /// <summary>A drawing on its way into <paramref name="target"/>, read through the declarations it will inherit there, or null where it cannot be.</summary>
    internal static SvgViewerDocument? Drawn(string text, ProjectGroup? target)
    {
        try
        {
            return SvgViewerDocument.LoadFromSvg(text, null, default, target is { } ? own => ProjectDeclarations.Built(target, own) : null);
        }
        catch (Exception)
        {
            // Anything, as a board's drawing: this is a download reaching a parser, and a preview
            // that cannot be drawn is no reason to refuse the row.
            return null;
        }
    }

    private void Bind()
    {
        try
        {
            _document?.Svg.SetExpressionValues(_values);
        }
        catch (ExprException)
        {
            // A value the drawing will not take leaves its last rendering up, as on a board.
        }

        Preview.Publish();
    }

    private void Declare()
    {
        var adding = Enumerable.Range(0, _icons.Count)
            .Select(Recipe)
            .OfType<SvgRecipe>()
            .SelectMany(recipe => recipe.Declarations)
            .Select(declaration => ((string?)declaration.Attribute("name"))?.Trim())
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Declares.Text = _target is null || adding.Count == 0
            ? "Nothing to declare"
            : $"Adds {string.Join(", ", adding)} to {ProjectWorkspace.Label(_target)}";
    }

    /// <summary>The colour names a drawing added to <paramref name="target"/> can use, in the order they are declared.</summary>
    private static IReadOnlyList<string> ColourNames(ProjectGroup target)
    {
        var scope = ProjectDeclarations.Scope(target);
        var checker = ExprChecker.For(scope);

        return scope.Parameters.Select(parameter => parameter.Name)
            .Concat(scope.Lets.Select(let => let.Name))
            .Where(name =>
            {
                try
                {
                    return checker.Check(name).Type == ExprType.Color;
                }
                catch (ExprException)
                {
                    return false;
                }
            })
            .ToList();
    }
}
