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
using Svg.Expressions;
using Svg.Expressions.Recipes;
using Svg.SourceEditing;
using Svg.Viewer.Skia.Avalonia;

namespace Svg.Studio;

/// <summary>
/// The values one drawing uses that a recipe can replace, and the expression it gives each.
/// </summary>
/// <remarks>
/// What a recipe is written for. Binding a value used to mean reading it out of the SVG yourself
/// and typing a <c>&lt;replace&gt;</c> for it; the drawing already knows which values it has, and
/// the recipe already knows which of them it has claimed, so the two together are this list.
///
/// The rows are the drawing's, not the recipe's: a recipe usually covers a family and its other
/// rules are none of this drawing's business — except that a rule matching nothing here is worth
/// seeing rather than looking lost, so those follow underneath.
///
/// Edits go into the recipe's buffer and nowhere near the drawing, which is the whole point: the
/// drawing keeps the values it was drawn with, and what stands in for them is the recipe's to say.
///
/// With no drawing behind it — the recipe's own tab — every rule is one of those, so the list is
/// the recipe entire and a rule has to be started from nothing rather than found in a survey. That
/// is the only difference, and it is why this is one panel and not two: a rule row is a value, an
/// expression box and a readout wherever it is read.
/// </remarks>
public sealed class ReplacementsPanel : UserControl
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

    /// <summary>The drawing these rules are read against, or null on a recipe's own tab.</summary>
    private readonly Func<string>? _drawing;

    /// <summary>
    /// What the drawing's expressions currently come to, or null while nothing can be worked out.
    /// </summary>
    /// <remarks>
    /// The drawing's, not the recipe's: a readout is what a rule paints <em>now</em>, which takes the
    /// values on the parameter panel as well as the declarations. Checking is a separate question and
    /// deliberately does not go through this — an evaluator needs a value for every parameter, so a
    /// recipe whose parameters have no defaults would report that instead of the typo asked about.
    /// </remarks>
    private readonly Func<ExprEvaluator?> _values;

    /// <summary>The rows on screen, so a readout can be moved without rebuilding them.</summary>
    private readonly List<(SvgRecipeSurveyValue Value, TextBox Box, TextBlock Readout, TextBlock Trouble)> _shown = new();

    /// <summary>Whether a rebuild was put off because somebody was typing in a row.</summary>
    private bool _waiting;

    private IReadOnlyList<SvgRecipeSurveyValue> _found = Array.Empty<SvgRecipeSurveyValue>();

    /// <param name="drawing">
    /// The drawing to read the rules against, or null for the recipe on its own — where every rule
    /// is listed and a new one is started from the row at the bottom.
    /// </param>
    public ReplacementsPanel(RecipeWorkspace recipe, Func<string>? drawing, Func<ExprEvaluator?> values)
    {
        Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        _drawing = drawing;
        _values = values ?? throw new ArgumentNullException(nameof(values));

        // The expression box and the palette it paints by, carried here rather than left to the
        // host: the same reason the viewer's own panels carry theirs, and the reason this file
        // exists apart from SvgViewer at all.
        Resources.MergedDictionaries.Add(new ResourceInclude(Home)
        {
            Source = new Uri("avares://Svg.Viewer.Skia.Avalonia/SvgExpressionBox.axaml")
        });

        // Anything typed anywhere into the recipe changes what these rows say, including this
        // panel's own writes.
        recipe.Edited += (_, _) => Refresh();

        var panel = new DockPanel();

        panel.Children.Add(_fault);
        DockPanel.SetDock(_fault, Dock.Bottom);
        panel.Children.Add(new ScrollViewer { Content = _rows });

        Content = panel;

        Refresh();
    }

    /// <summary>The recipe these values are replaced by.</summary>
    public RecipeWorkspace Recipe { get; }

    /// <summary>The values the drawing uses that a rule could name.</summary>
    public IReadOnlyList<SvgRecipeSurveyValue> Values => _found;

    /// <summary>Why the last edit was refused, or null.</summary>
    public string? Fault { get; private set; }

    /// <summary>What replaces <paramref name="value"/>, or null when nothing claims it.</summary>
    public string? Expression(string name, string value) => Rule(name, value)?.Expression;

    /// <summary>
    /// Says what replaces <paramref name="value"/>, writing the rule into the recipe.
    /// </summary>
    /// <remarks>
    /// Taking the value rather than a row, so everything but the pointer can be driven. The rule is
    /// named as the recipe already writes this value where there is one — a colour has many
    /// spellings and only one of them can be the rule's, or the recipe would hold two rules for one
    /// colour and refuse to read at all.
    /// </remarks>
    public bool Bind(string name, string value, string expression)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        var written = Rule(name, value)?.ValueText ?? value;

        return Splice(
            $"bind {written}",
            source => SvgRecipeRuleEditor.SetRule(source, name, written, expression ?? string.Empty));
    }

    /// <summary>Takes back whatever replaces <paramref name="value"/>, leaving it as the drawing has it.</summary>
    public bool Unbind(string name, string value)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        return Rule(name, value) is { } rule
               && Splice(
                   $"unbind {rule.ValueText}",
                   source => SvgRecipeRuleEditor.RemoveRule(source, name, rule.ValueText));
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

        if (_drawing is { } text)
        {
            try
            {
                _found = SvgRecipeRewriter.Survey(text());
            }
            catch (SvgRecipeException)
            {
                // Halfway through being typed. The rows it had are better than none until it reads.
                return;
            }
        }

        Show();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // A tab's content leaves the tree when another tab is picked, so this is every time the
        // pane is looked at — which is when a value typed into the source pane since should appear.
        Refresh();
    }

    /// <summary>Says again what each row's expression comes to, without disturbing the rows.</summary>
    /// <remarks>
    /// A readout follows the values on the parameter panel, so it moves as a slider is dragged —
    /// and rebuilding the rows for that would take the box being typed in out of the tree.
    /// </remarks>
    public void Readouts()
    {
        foreach (var row in _shown)
        {
            Says(row);
        }
    }

    /// <summary>Fills one row's trouble and readout from what its box currently says.</summary>
    private void Says((SvgRecipeSurveyValue Value, TextBox Box, TextBlock Readout, TextBlock Trouble) row)
    {
        var written = row.Box.Text?.Trim() ?? string.Empty;
        var trouble = Trouble(written, row.Value.Type);

        row.Trouble.Text = trouble;
        row.Trouble.IsVisible = trouble is { };

        // One or the other. A readout of an expression that will not check is a second, quieter
        // account of the same trouble.
        var readout = trouble is null ? Readout(written) : string.Empty;

        row.Readout.Text = readout;
        row.Readout.IsVisible = readout.Length > 0;
    }

    /// <summary>
    /// What is wrong with <paramref name="expression"/> here, or null.
    /// </summary>
    /// <remarks>
    /// Checked rather than evaluated, and checked against the recipe's own declarations: this has to
    /// answer while the parameters are still being typed and before any value has been bound.
    ///
    /// As whatever the attribute holds, which is what the row is for: a rule's body lands in the
    /// attribute it names, so an expression for <c>opacity</c> has to be a number and one for
    /// <c>fill</c> a colour. That catches the second kind of mistake nothing caught before — an
    /// expression that is well formed and the wrong type — where it was typed rather than on the
    /// drawing's status line.
    /// </remarks>
    private string? Trouble(string expression, ExprType type)
    {
        if (expression.Length == 0)
        {
            return null;
        }

        var declarations = SvgExpressionDeclarations.Parse(Recipe.Text, out var diagnostics);

        if (diagnostics.Count > 0)
        {
            // The block itself does not read, which is not this row's fault but is why it cannot
            // be answered for.
            return diagnostics[0].Message;
        }

        try
        {
            ExprChecker.For(declarations).CheckAs(expression, type, ExprFunctions.DescribeUse(type));

            return null;
        }
        catch (ExprException failure)
        {
            return failure.Message;
        }
    }

    /// <summary>What <paramref name="expression"/> comes to right now, or nothing where it cannot be said.</summary>
    private string Readout(string expression)
    {
        if (expression.Length == 0 || _values() is not { } evaluator)
        {
            return string.Empty;
        }

        try
        {
            var value = evaluator.Evaluate(expression);

            return $"{ExprFunctions.Describe(value.Type)}  {SvgViewerParameterFactory.Describe(value)}";
        }
        catch (Exception failure) when (failure is ExprException or ArgumentException)
        {
            return string.Empty;
        }
    }

    private void Show()
    {
        _shown.Clear();
        _rows.Children.Clear();

        if (_found.Count == 0 && _drawing is { })
        {
            _rows.Children.Add(new TextBlock
            {
                Text = "This drawing uses no value a recipe could name.",
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap
            });
        }

        foreach (var value in _found)
        {
            _rows.Children.Add(Row(value, Places(value.Count)));
        }

        // Rules this drawing gives nothing to. Not an error — one recipe usually covers a family,
        // and a rule is for whichever of them has the value — but a rule that appeared to have
        // vanished would be worse than one shown as unused.
        var elsewhere = (Recipe.Recipe?.Rules ?? Array.Empty<SvgReplaceRule>())
            .Where(rule => !_found.Any(value => value.Name == rule.Name && value.Text == rule.Key))
            .ToList();

        // A heading only where there is something above it to tell these apart from. On a recipe's
        // own tab every rule is here and there is nothing for them to be "not in".
        if (elsewhere.Count > 0 && _drawing is { })
        {
            _rows.Children.Add(new TextBlock
            {
                Text = "Not in this drawing",
                FontWeight = FontWeight.SemiBold,
                Opacity = 0.7,
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 0)
            });
        }

        foreach (var rule in elsewhere)
        {
            // Named as the recipe writes it, so the box that appears is the one that rule's edits
            // go through — and counted as nothing, since this drawing gives it nothing.
            _rows.Children.Add(Row(new SvgRecipeSurveyValue(rule.Name, rule.ValueText, rule.Type, 0), null));
        }

        if (_drawing is null)
        {
            if (elsewhere.Count == 0)
            {
                _rows.Children.Add(new TextBlock
                {
                    Text = "This recipe replaces nothing yet.",
                    Opacity = 0.65,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            _rows.Children.Add(Draft());
        }
    }

    private static string Places(int count) => count == 1 ? "1 place" : $"{count} places";

    /// <summary>
    /// The row a rule is started from, for a recipe with no drawing to find values in.
    /// </summary>
    /// <remarks>
    /// A rule is three things and a survey supplies two of them. Without one they have to be typed,
    /// so this is what a row is minus the count: what it replaces, the value, and the expression.
    /// Kept in line with the rows above it rather than put behind a dialog — a recipe is usually
    /// given several rules at a sitting, and the list coming back with the new one in it is what
    /// says it landed.
    /// </remarks>
    private Control Draft()
    {
        var names = new ComboBox
        {
            ItemsSource = SvgRecipeValue.Names,
            SelectedIndex = 0,
            FontSize = 12,
            MinWidth = 110
        };

        var value = new TextBox { Watermark = "the value to replace", FontSize = 12 };
        var expression = new TextBox { Watermark = "the expression to replace it with", FontSize = 12 };

        if (this.TryFindResource("SvgExpressionBox", ActualThemeVariant, out var theme) && theme is ControlTheme box_)
        {
            expression.Theme = box_;
        }

        var add = new Button { Content = "Add", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };

        add.Click += (_, _) =>
        {
            var name = names.SelectedItem as string ?? SvgRecipeValue.ColorName;
            var written = value.Text?.Trim() ?? string.Empty;
            var says = expression.Text?.Trim() ?? string.Empty;

            // Checked here as well as in the editor, because the editor answers for the rule and
            // this answers for the expression: an expression of the wrong type writes a rule that
            // reads and paints nothing.
            if (says.Length > 0
                && SvgRecipeValue.TypeFor(name) is { } type
                && Trouble(says, type) is { } trouble)
            {
                Say(trouble);

                return;
            }

            if (!Bind(name, written, says))
            {
                return;
            }

            // Emptied rather than left holding what was just written: the row the rule now has is
            // above this one, and two copies of it on screen would read as two rules.
            value.Text = string.Empty;
            expression.Text = string.Empty;
        };

        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 4, 0, 0) };

        Grid.SetColumn(names, 0);
        line.Children.Add(names);

        Grid.SetColumn(value, 1);
        value.Margin = new Thickness(8, 0, 0, 0);
        line.Children.Add(value);

        return new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 10, 0, 0),
            Children =
            {
                new TextBlock
                {
                    Text = "Replace something else",
                    FontWeight = FontWeight.SemiBold,
                    Opacity = 0.7,
                    FontSize = 11
                },
                line,
                expression,
                add
            }
        };
    }

    /// <summary>One value: what it is, how much of the drawing it is, and what replaces it.</summary>
    private Control Row(SvgRecipeSurveyValue value, string? places)
    {
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };

        // A colour shows itself; everything else has to say which attribute it is on, since 0.5
        // means one thing on an opacity and another on a stop-opacity and the value alone cannot
        // tell them apart.
        var mark = value.Type == ExprType.Color
            ? new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#40808080")),
                VerticalAlignment = VerticalAlignment.Center,
                Background = SvgRecipeColor.TryParse(value.Text, out var argb)
                    ? new SolidColorBrush(Color.FromUInt32(unchecked((uint)argb)))
                    : Brushes.Transparent
            }
            : (Control)new TextBlock
            {
                Text = value.Name,
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center
            };

        Grid.SetColumn(mark, 0);
        heading.Children.Add(mark);

        var name = new TextBlock
        {
            Text = value.Text,
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
            Text = Expression(value.Name, value.Text),
            Watermark = "not replaced by the recipe",
            FontSize = 12,
            Tag = (value.Name, value.Text)
        };

        if (this.TryFindResource("SvgExpressionBox", ActualThemeVariant, out var theme) && theme is ControlTheme box_)
        {
            box.Theme = box_;
        }

        // Beside the box, as a let's is: what it comes to is worth reading next to what it says.
        var readout = new TextBlock
        {
            Opacity = 0.55,
            FontSize = 11,
            FontFamily = new FontFamily("Menlo, Consolas, monospace"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            IsVisible = false
        };

        // Under it, where the box it is about is still in sight. It used to be said on the drawing's
        // status line, which is a long way from what was typed.
        var trouble = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#e05252")),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(22, 0, 0, 0),
            IsVisible = false
        };

        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(22, 0, 0, 0) };

        Grid.SetColumn(box, 0);
        line.Children.Add(box);

        Grid.SetColumn(readout, 1);
        line.Children.Add(readout);

        var row = (value, box, readout, trouble);

        _shown.Add(row);

        box.TextChanged += (_, _) => Says(row);

        box.LostFocus += (_, _) => Commit(box, value);

        box.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Return))
            {
                return;
            }

            e.Handled = true;
            Commit(box, value);
        };

        Says(row);

        return new StackPanel { Spacing = 3, Children = { heading, line, trouble } };
    }

    /// <summary>Writes what a box says into the recipe, if it says something else than the rule does.</summary>
    private void Commit(TextBox box, SvgRecipeSurveyValue value)
    {
        var written = box.Text?.Trim() ?? string.Empty;

        if (string.Equals(written, Expression(value.Name, value.Text) ?? string.Empty, StringComparison.Ordinal))
        {
            Settle();

            return;
        }

        // Said on the row already, and writing it would put the drawing's own trouble somewhere
        // else again. The box keeps what was typed, so it can be finished.
        if (Trouble(written, value.Type) is { })
        {
            return;
        }

        // An emptied box takes the rule away, which is the only way to say "leave this value as the
        // drawing has it" without a second control saying it.
        if (!(written.Length == 0
                ? Unbind(value.Name, value.Text)
                : Bind(value.Name, value.Text, written)))
        {
            // Put back, and said, rather than left looking accepted.
            box.Text = Expression(value.Name, value.Text);
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

    private bool Splice(string label, Func<SvgSourceDocument, string?> edit)
    {
        // Through the workspace, which is also where the parameter panel's edits land: one way into
        // the recipe means one answer to what it says and one history to take it back on.
        if (Recipe.Commit(label, edit) is { } refusal)
        {
            Say(refusal);

            return false;
        }

        Say(null);

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

    /// <summary>The rule for <paramref name="value"/>, matched by value rather than by spelling.</summary>
    private SvgReplaceRule? Rule(string name, string value)
        => Recipe.Recipe is { } recipe
           && SvgRecipeValue.TypeFor(name) is { } type
           && SvgRecipeValue.TryKey(type, value, out var key)
            ? recipe.Rules.FirstOrDefault(rule => rule.Name == name && rule.Key == key)
            : null;

    /// <summary>Whether the caret is in one of this panel's boxes.</summary>
    private bool Typing()
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox box
           && ReferenceEquals(box.FindAncestorOfType<ReplacementsPanel>(), this);
}
