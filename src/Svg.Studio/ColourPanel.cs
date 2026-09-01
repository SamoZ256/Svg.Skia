// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Svg.Expressions.Recipes;
using Svg.SourceEditing;

namespace Svg.Studio;

/// <summary>
/// The colours one drawing paints with, and the expression a recipe gives each.
/// </summary>
/// <remarks>
/// What a recipe is written for. Binding a colour used to mean reading it out of the SVG yourself
/// and typing a <c>&lt;replace&gt;</c> for it; the drawing already knows which colours it has, and
/// the recipe already knows which of them it has claimed, so the two together are this list.
///
/// The rows are the drawing's, not the recipe's: a recipe usually covers a family and its other
/// rules are none of this drawing's business — except that a rule matching nothing here is worth
/// seeing rather than looking lost, so those follow underneath.
///
/// Edits go into the recipe's buffer and nowhere near the drawing, which is the whole point: the
/// drawing keeps the colours it was drawn with, and what they are painted as is the recipe's to say.
/// </remarks>
public sealed class ColourPanel : UserControl
{
    /// <summary>What a relative include is read against, which nothing here writes one of.</summary>
    private static readonly Uri Home = new("avares://Svg.Studio/");

    private readonly StackPanel _rows = new() { Spacing = 10, Margin = new Thickness(10) };

    private readonly TextBlock _fault = new()
    {
        Margin = new Thickness(10, 6),
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false
    };

    /// <summary>The drawing as it stands, which is the pane's text once it has been typed in.</summary>
    private readonly Func<string> _drawing;

    /// <summary>Whether a rebuild was put off because somebody was typing in a row.</summary>
    private bool _waiting;

    private IReadOnlyList<SvgRecipeSurveyColor> _colours = Array.Empty<SvgRecipeSurveyColor>();

    public ColourPanel(RecipeWorkspace recipe, Func<string> drawing)
    {
        Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        _drawing = drawing ?? throw new ArgumentNullException(nameof(drawing));

        // The expression box and the palette it paints by, carried here rather than left to the
        // host: the same reason the viewer's own panels carry theirs, and the reason this file
        // exists apart from SvgViewer at all.
        Resources.MergedDictionaries.Add(new ResourceInclude(Home)
        {
            Source = new Uri("avares://Svg.Viewer.Skia.Avalonia/SvgExpressionBox.axaml")
        });

        // Anything typed anywhere into the recipe changes what these rows say, including this
        // panel's own writes.
        recipe.Document.TextChanged += (_, _) => Refresh();

        var panel = new DockPanel();

        panel.Children.Add(_fault);
        DockPanel.SetDock(_fault, Dock.Bottom);
        panel.Children.Add(new ScrollViewer { Content = _rows });

        Content = panel;

        Refresh();
    }

    /// <summary>The recipe these colours are painted by.</summary>
    public RecipeWorkspace Recipe { get; }

    /// <summary>The colours the drawing paints with, as a rule would name them.</summary>
    public IReadOnlyList<string> Colours => _colours.Select(colour => colour.Text).ToList();

    /// <summary>Why the last edit was refused, or null.</summary>
    public string? Fault { get; private set; }

    /// <summary>What <paramref name="colour"/> is painted with, or null when nothing claims it.</summary>
    public string? Expression(string colour) => Rule(colour)?.Expression;

    /// <summary>
    /// Says what paints <paramref name="colour"/>, writing the rule into the recipe.
    /// </summary>
    /// <remarks>
    /// Taking the colour rather than a row, so everything but the pointer can be driven. The rule is
    /// named as the recipe already writes this colour where there is one — a colour has many
    /// spellings and only one of them can be the rule's, or the recipe would hold two rules for one
    /// colour and refuse to read at all.
    /// </remarks>
    public bool Bind(string colour, string expression)
    {
        if (colour is null)
        {
            throw new ArgumentNullException(nameof(colour));
        }

        return Splice(SvgRecipeRuleEditor.SetRule(Recipe.Text, Rule(colour)?.ColorText ?? colour, expression ?? string.Empty));
    }

    /// <summary>Takes back whatever paints <paramref name="colour"/>, leaving it as the drawing has it.</summary>
    public bool Unbind(string colour)
    {
        if (colour is null)
        {
            throw new ArgumentNullException(nameof(colour));
        }

        return Rule(colour) is { } rule && Splice(SvgRecipeRuleEditor.RemoveRule(Recipe.Text, rule.ColorText));
    }

    /// <summary>Reads the drawing and the recipe again, and says what the two come to.</summary>
    public void Refresh()
    {
        // Not under somebody's caret. Rebuilding takes the box being typed in out of the tree, and
        // this runs on every keystroke of its own writes.
        if (Typing())
        {
            _waiting = true;

            return;
        }

        _waiting = false;

        try
        {
            _colours = SvgRecipeRewriter.Survey(_drawing());
        }
        catch (SvgRecipeException)
        {
            // Halfway through being typed. The rows it had are better than none until it reads.
            return;
        }

        Show();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // A tab's content leaves the tree when another tab is picked, so this is every time the
        // pane is looked at — which is when a colour typed into the source pane since should appear.
        Refresh();
    }

    private void Show()
    {
        _rows.Children.Clear();

        if (_colours.Count == 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = "This drawing paints with no colour a recipe could name.",
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap
            });
        }

        foreach (var colour in _colours)
        {
            _rows.Children.Add(Row(colour.Text, Places(colour.Count)));
        }

        // Rules this drawing gives nothing to. Not an error — one recipe usually covers a family,
        // and a rule is for whichever of them has the colour — but a rule that appeared to have
        // vanished would be worse than one shown as unused.
        var elsewhere = (Recipe.Recipe?.ColorRules ?? Array.Empty<SvgColorRule>())
            .Where(rule => !_colours.Any(colour => colour.Argb == rule.Argb))
            .ToList();

        if (elsewhere.Count == 0)
        {
            return;
        }

        _rows.Children.Add(new TextBlock
        {
            Text = "Not in this drawing",
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.7,
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 0)
        });

        foreach (var rule in elsewhere)
        {
            _rows.Children.Add(Row(rule.ColorText, null));
        }
    }

    private static string Places(int count) => count == 1 ? "1 place" : $"{count} places";

    /// <summary>One colour: what it is, how much of the drawing it is, and what paints it.</summary>
    private Control Row(string colour, string? places)
    {
        var swatch = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse("#40808080")),
            VerticalAlignment = VerticalAlignment.Center,
            Background = SvgRecipeColor.TryParse(colour, out var argb)
                ? new SolidColorBrush(Color.FromUInt32(unchecked((uint)argb)))
                : Brushes.Transparent
        };

        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };

        Grid.SetColumn(swatch, 0);
        heading.Children.Add(swatch);

        var name = new TextBlock
        {
            Text = colour,
            FontFamily = new FontFamily("Menlo, Consolas, monospace"),
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(name, 1);
        heading.Children.Add(name);

        if (places is { })
        {
            var count = new TextBlock
            {
                Text = places,
                Opacity = 0.5,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(count, 2);
            heading.Children.Add(count);
        }

        var box = new TextBox
        {
            Text = Expression(colour),
            Watermark = "not painted by the recipe",
            FontSize = 12,
            Margin = new Thickness(22, 0, 0, 0),
            Tag = colour
        };

        if (this.TryFindResource("SvgExpressionBox", ActualThemeVariant, out var theme) && theme is ControlTheme box_)
        {
            box.Theme = box_;
        }

        box.LostFocus += (_, _) => Commit(box, colour);

        box.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Return))
            {
                return;
            }

            e.Handled = true;
            Commit(box, colour);
        };

        return new StackPanel { Spacing = 3, Children = { heading, box } };
    }

    /// <summary>Writes what a box says into the recipe, if it says something else than the rule does.</summary>
    private void Commit(TextBox box, string colour)
    {
        var written = box.Text?.Trim() ?? string.Empty;

        if (string.Equals(written, Expression(colour) ?? string.Empty, StringComparison.Ordinal))
        {
            Settle();

            return;
        }

        // An emptied box takes the rule away, which is the only way to say "leave this colour as the
        // drawing has it" without a second control saying it.
        if (!(written.Length == 0 ? Unbind(colour) : Bind(colour, written)))
        {
            // Put back, and said, rather than left looking accepted.
            box.Text = Expression(colour);
        }

        Settle();
    }

    /// <summary>Catches up a rebuild that was put off while this row was being typed in.</summary>
    private void Settle()
    {
        if (_waiting && !Typing())
        {
            Refresh();
        }
    }

    private bool Splice(SvgSourceEditResult result)
    {
        if (!result.Succeeded)
        {
            Say(result.Refusal);

            return false;
        }

        Say(null);

        // Through the workspace, which is also where the parameter panel's edits land: one way into
        // the buffer means one answer to what the recipe says and one stack to take it back on.
        Recipe.Apply(result.Edits);

        return true;
    }

    private void Say(string? refusal)
    {
        Fault = refusal;

        _fault.Text = refusal;
        _fault.IsVisible = refusal is { };
        _fault[!TextBlock.ForegroundProperty] =
            new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SvgViewerSourceErrorBrush");
    }

    /// <summary>The rule for <paramref name="colour"/>, matched by value rather than by spelling.</summary>
    private SvgColorRule? Rule(string colour)
        => Recipe.Recipe is { } recipe && SvgRecipeColor.TryParse(colour, out var argb)
            ? recipe.ColorRules.FirstOrDefault(rule => rule.Argb == argb)
            : null;

    /// <summary>Whether the caret is in one of this panel's boxes.</summary>
    private bool Typing()
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox box
           && ReferenceEquals(box.FindAncestorOfType<ColourPanel>(), this);
}
