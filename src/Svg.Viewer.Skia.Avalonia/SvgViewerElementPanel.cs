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
using Svg.SourceEditing;

namespace Svg.Viewer.Skia.Avalonia;

/// <summary>
/// The attributes of the element that is picked, each editable as the file writes it.
/// </summary>
/// <remarks>
/// The other question a selection asks. The parameters are the drawing's, and a recipe's rules are a
/// value everywhere; this is one element and one attribute, which is the only way to say that
/// <em>this</em> shape follows a parameter and its neighbour does not.
///
/// A row's box holds the value itself, whatever it is — a literal, or a <c>{{ … }}</c>. So binding
/// and unbinding are one gesture: type an expression and the attribute follows it, type a value back
/// and it does not. Nothing has to remember what a value used to be, which is the thing a rule-shaped
/// editor cannot do once it has replaced one.
///
/// Rows come from the text and not from the parsed drawing. The text is what is being edited, so
/// what is listed is what is there in the order somebody wrote it, and an attribute already holding
/// an expression reads back as that rather than as the placeholder the parser puts in its place.
///
/// Host-agnostic in the way <see cref="SvgViewerDeclarationCommands"/> is: what differs between a
/// drawing's own tab and a group's is where the text is and what applying an edit means, so both are
/// handed in. The address handed to <see cref="Show"/> is an address in <em>that</em> text — a host
/// whose drawing is built from something made of the file translates it first.
/// </remarks>
public sealed class SvgViewerElementPanel : UserControl
{
    private static readonly Uri Home = new("avares://Svg.Viewer.Skia.Avalonia/");

    private readonly StackPanel _rows = new() { Spacing = 10, Margin = new Thickness(10) };

    private readonly TextBlock _note = new()
    {
        Margin = new Thickness(10),
        Opacity = 0.6,
        TextWrapping = TextWrapping.Wrap
    };

    private readonly TextBlock _fault = new()
    {
        Margin = new Thickness(10, 6),
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false
    };

    private readonly Func<string> _text;

    /// <summary>Where the names an expression may use are declared.</summary>
    /// <remarks>
    /// Not always the text being edited. A drawing under a recipe is built with the recipe's
    /// declarations injected into it, so <c>{{ tint }}</c> is a name in scope while the file itself
    /// declares nothing — checking against the file would refuse every expression a recipe makes
    /// available.
    /// </remarks>
    private readonly Func<string> _declarations;

    private readonly Func<SvgSourceEditResult, bool> _write;

    private readonly Func<ExprEvaluator?> _values;

    private readonly List<(string Name, TextBox Box, TextBlock Readout, TextBlock Trouble)> _shown = new();

    private readonly ContentControl _host = new();

    /// <summary>Made once and kept: a second one around the same rows is a second parent for them.</summary>
    private readonly ScrollViewer _scroll;

    private string? _address;

    public SvgViewerElementPanel(
        Func<string> text,
        Func<string> declarations,
        Func<SvgSourceEditResult, bool> write,
        Func<ExprEvaluator?> values)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _declarations = declarations ?? throw new ArgumentNullException(nameof(declarations));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _values = values ?? throw new ArgumentNullException(nameof(values));

        _scroll = new ScrollViewer { Content = _rows };

        Resources.MergedDictionaries.Add(new ResourceInclude(Home)
        {
            Source = new Uri("avares://Svg.Viewer.Skia.Avalonia/SvgExpressionBox.axaml")
        });

        var panel = new DockPanel();

        panel.Children.Add(_fault);
        DockPanel.SetDock(_fault, Dock.Bottom);
        panel.Children.Add(_host);

        Content = panel;

        Show(null);
    }

    /// <summary>The attributes on show, in the order they are listed.</summary>
    public IReadOnlyList<string> Attributes => _shown.Select(row => row.Name).ToList();

    /// <summary>Why the last edit was refused, or null.</summary>
    public string? Fault { get; private set; }

    /// <summary>What the box for <paramref name="name"/> says, or null when there is no such row.</summary>
    public string? Shown(string name)
        => _shown.FirstOrDefault(row => string.Equals(row.Name, name, StringComparison.Ordinal)) is { Box: { } box }
            ? box.Text
            : null;

    /// <summary>Writes <paramref name="value"/> into <paramref name="name"/>, or takes it away.</summary>
    /// <remarks>Taking the name rather than a row, so everything but the pointer can be driven.</remarks>
    public bool Set(string name, string? value)
    {
        if (_address is not { } address)
        {
            return false;
        }

        var written = value?.Trim();

        if (Trouble(name, written ?? string.Empty) is { } trouble)
        {
            Say(trouble);

            return false;
        }

        return Write(address, name, written is { Length: > 0 } ? written : null);
    }

    /// <summary>Shows the element at <paramref name="addressKey"/>, or says nothing is picked.</summary>
    public void Show(string? addressKey)
    {
        _address = addressKey;

        Refresh();
    }

    /// <summary>Reads the element again, for a host whose text has changed underneath it.</summary>
    public void Refresh()
    {
        // Not under somebody's caret. Rebuilding takes the box being typed in out of the tree, and a
        // drawing is rebuilt on every keystroke.
        if (Typing())
        {
            return;
        }

        _shown.Clear();
        _rows.Children.Clear();

        if (_address is not { } address)
        {
            _note.Text = "Pick an element to see what it is written with.";
            _host.Content = _note;

            return;
        }

        var open = Open();
        var written = open is null
            ? Array.Empty<SvgSourceAttribute>()
            : SvgAttributeEditor.Attributes(open, address);

        if (open is null || !SvgAttributeEditor.Contains(open, address))
        {
            // In the drawing but not in the file: the declarations block a recipe injected is the
            // one of these anybody meets.
            _note.Text = "This element is not written in the file, so it has nothing to edit.";
            _host.Content = _note;

            return;
        }

        foreach (var attribute in written)
        {
            _rows.Children.Add(Row(attribute.Name, attribute.Value));
        }

        var absent = SvgExpressionAttributes.Supported
            .Where(name => !written.Any(had => string.Equals(had.Name, name, StringComparison.Ordinal)))
            .ToList();

        if (absent.Count > 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = "Not set",
                FontWeight = FontWeight.SemiBold,
                Opacity = 0.7,
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 0)
            });

            foreach (var name in absent)
            {
                _rows.Children.Add(Row(name, string.Empty));
            }
        }

        _host.Content = _scroll;
    }

    /// <summary>Says again what each row's expression comes to, without disturbing the rows.</summary>
    public void Readouts()
    {
        foreach (var row in _shown)
        {
            Says(row);
        }
    }

    private Control Row(string name, string value)
    {
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var label = new TextBlock
        {
            Text = name,
            FontFamily = new FontFamily("Menlo, Consolas, monospace"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };

        heading.Children.Add(label);

        // What an expression here would have to come to, or that one would do nothing. The second is
        // worth saying up front: the braces are read as an ordinary value and nothing else says so.
        var says = SvgExpressionAttributes.TypeFor(name) is { } type
            ? ExprFunctions.Describe(type)
              + (SvgExpressionAttributes.IsInArguments(name) ? " per argument" : string.Empty)
              + (SvgExpressionAttributes.IsResolvedBeforeRecording(name) ? ", built in" : string.Empty)
            : "no expression";

        var kind = new TextBlock
        {
            Text = says,
            Opacity = 0.45,
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(kind, 1);
        heading.Children.Add(kind);

        var box = new TextBox
        {
            Text = value,
            Watermark = "not set",
            FontSize = 12,
            Tag = name
        };

        if (this.TryFindResource("SvgExpressionBox", ActualThemeVariant, out var theme) && theme is ControlTheme box_)
        {
            box.Theme = box_;
        }

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

        var trouble = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#e05252")),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 0),
            IsVisible = false
        };

        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        Grid.SetColumn(box, 0);
        line.Children.Add(box);

        Grid.SetColumn(readout, 1);
        line.Children.Add(readout);

        var row = (name, box, readout, trouble);

        _shown.Add(row);

        box.TextChanged += (_, _) => Says(row);

        box.LostFocus += (_, _) => Commit(box, name);

        box.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Return))
            {
                return;
            }

            e.Handled = true;
            Commit(box, name);
        };

        Says(row);

        return new StackPanel { Spacing = 3, Children = { heading, line, trouble } };
    }

    private void Commit(TextBox box, string name)
    {
        var written = box.Text?.Trim() ?? string.Empty;
        var was = (Open() is { } open
                ? SvgAttributeEditor.Attributes(open, _address ?? string.Empty)
                : Array.Empty<SvgSourceAttribute>())
            .FirstOrDefault(attribute => string.Equals(attribute.Name, name, StringComparison.Ordinal));

        if (string.Equals(written, was.Value ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        // Said on the row already. The box keeps what was typed, so it can be finished.
        if (Trouble(name, written) is { })
        {
            return;
        }

        if (!Set(name, written))
        {
            box.Text = was.Value ?? string.Empty;
        }
    }

    /// <summary>Fills one row's trouble and readout from what its box currently says.</summary>
    private void Says((string Name, TextBox Box, TextBlock Readout, TextBlock Trouble) row)
    {
        var written = row.Box.Text?.Trim() ?? string.Empty;
        var trouble = Trouble(row.Name, written);

        row.Trouble.Text = trouble;
        row.Trouble.IsVisible = trouble is { };

        var readout = trouble is null ? Readout(row.Name, written) : string.Empty;

        row.Readout.Text = readout;
        row.Readout.IsVisible = readout.Length > 0;
    }

    /// <summary>
    /// What is wrong with what a row says, or null.
    /// </summary>
    /// <remarks>
    /// Only an expression is judged here. Anything else is the file's own value and the parser
    /// answers for it — a bad colour is a diagnostic on the drawing, not a refusal to write what
    /// somebody typed.
    /// </remarks>
    private string? Trouble(string name, string written)
    {
        if (SvgExpressionAttributes.IsInArguments(name))
        {
            // Braces that are not one whole argument drive nothing, which the row has to say before
            // the arguments it could read are judged.
            if (SvgExpressionAttributes.WhyUnsupported(name, written) is { } stray)
            {
                return stray;
            }

            foreach (var function in SvgTransformExpression.Parse(written).Functions)
            {
                foreach (var argument in function.Arguments)
                {
                    if (argument.Expression is { } code && Checked(code, ExprType.Number) is { } fault)
                    {
                        return fault;
                    }
                }
            }

            return null;
        }

        if (!SvgExpressionAttributes.TryUnwrap(written, out var expression))
        {
            return null;
        }

        if (SvgExpressionAttributes.TypeFor(name) is not { } type)
        {
            return SvgExpressionAttributes.WhyUnsupported(name);
        }

        return Checked(expression, type);
    }

    /// <summary>What is wrong with one expression asked to come to <paramref name="type"/>, or null.</summary>
    private string? Checked(string expression, ExprType type)
    {
        var declarations = SvgExpressionDeclarations.Parse(_declarations(), out var diagnostics);

        if (diagnostics.Count > 0)
        {
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

    private string Readout(string name, string written)
    {
        if (_values() is not { } evaluator)
        {
            return string.Empty;
        }

        try
        {
            if (SvgExpressionAttributes.IsInArguments(name))
            {
                var arguments = SvgTransformExpression.Parse(written);

                // The whole transform as it currently stands, since one argument's number says
                // nothing about where the shape ends up.
                return arguments.Any
                    ? arguments.With(argument =>
                        SvgViewerParameterFactory.Describe(evaluator.Evaluate(argument.Expression!)))
                    : string.Empty;
            }

            if (!SvgExpressionAttributes.TryUnwrap(written, out var expression))
            {
                return string.Empty;
            }

            var value = evaluator.Evaluate(expression);

            return $"{ExprFunctions.Describe(value.Type)}  {SvgViewerParameterFactory.Describe(value)}";
        }
        catch (Exception failure) when (failure is ExprException or ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>The drawing as a tree, or null while its text will not read back.</summary>
    private SvgSourceDocument? Open() => SvgSourceDocument.Read(_text(), out _);

    /// <summary>
    /// Writes one attribute of the element, through the tree rather than into the text.
    /// </summary>
    /// <remarks>
    /// The tree because a prefix is part of an attribute's name: reading xlink:href back as href and
    /// writing href is how a drawing comes to hold both, and the span half does exactly that. What
    /// the host is handed is still text — one span standing for the whole document, since the two
    /// hosts write into different things and only one of them has a tree to be given.
    /// </remarks>
    private bool Write(string address, string name, string? value)
    {
        var text = _text();

        if (SvgSourceDocument.Read(text, out var unreadable) is not { } source)
        {
            Say(unreadable);

            return false;
        }

        if (SvgAttributeEditor.SetAttribute(source, address, name, value) is { } refusal)
        {
            Say(refusal);

            return false;
        }

        Say(null);

        var written = source.ToText();

        return string.Equals(written, text, StringComparison.Ordinal)
               || _write(SvgSourceEditResult.From(new[] { new SvgTextEdit(0, text.Length, written) }));
    }

    private void Say(string? refusal)
    {
        Fault = refusal;

        _fault.Text = refusal;
        _fault.IsVisible = refusal is { };
        _fault[!TextBlock.ForegroundProperty] =
            new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SvgViewerSourceErrorBrush");
    }

    /// <summary>Whether the caret is in one of this panel's boxes.</summary>
    private bool Typing()
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox box
           && box.FindAncestorOfType<SvgViewerElementPanel>() is { } panel
           && ReferenceEquals(panel, this);
}
