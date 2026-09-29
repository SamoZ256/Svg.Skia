// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
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
/// Values come from the text and not from the parsed drawing. The text is what is being edited, so an
/// attribute already holding an expression reads back as that rather than as the placeholder the
/// parser puts in its place. Rows are filed by what they do rather than where they were written, and
/// a row the file does not set is listed only where the element usually has one — or, with
/// <see cref="ShowsAll"/>, wherever the parser would read it.
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

    private readonly Func<string, Func<SvgSourceDocument, string?>, string?> _write;

    private readonly Func<ExprEvaluator?> _values;

    private readonly List<(string Name, TextBox Box, TextBlock Readout, TextBlock Trouble, Action<string, ExprValue?>? Follow)> _shown = new();

    private readonly ContentControl _host = new();

    /// <summary>Made once and kept: a second one around the same rows is a second parent for them.</summary>
    private readonly ScrollViewer _scroll;

    /// <summary>The heading and the rows under it, which is what <see cref="_host"/> shows once something is picked.</summary>
    private readonly DockPanel _body = new();

    private readonly TextBlock _title = new()
    {
        FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private readonly CheckBox _all = new()
    {
        Content = "Show all",
        FontSize = 12,
        MinHeight = 0,
        VerticalAlignment = VerticalAlignment.Center
    };

    // Static because they are one setting however many panels there are — Studio has one on every
    // drawing's tab and every group's — and a section folded on one tab should not open on the next.
    private static bool s_all;

    private static readonly HashSet<SvgViewerAttributeGroup> s_folded = new();

    /// <summary>The <see cref="s_all"/> this panel's rows were last listed under.</summary>
    private bool _listedAll;

    /// <summary>Whether a refresh was put off while a row was held, and is owed once it is let go.</summary>
    private bool _stale;

    /// <summary>Whether a pointer went down in the panel and has not come up yet.</summary>
    private bool _pressing;

    /// <summary>How many rows have a picker or a menu open.</summary>
    private int _picking;

    /// <summary>What each row's box was drawn with, or last wrote.</summary>
    /// <remarks>
    /// A box is written only where it says something else. Against the file alone, a row left
    /// behind by a refresh put off — the file undone under it — would write itself back on leaving,
    /// and the undo would be undone without anybody asking.
    /// </remarks>
    private readonly Dictionary<TextBox, string> _drawn = new();

    private string? _address;

    /// <summary>The variable the drag over this panel is carrying, or null while none is.</summary>
    private string? _carried;

    /// <summary>The rows that would take it, so the check is made once a drag and not once a move.</summary>
    /// <remarks>
    /// <see cref="Trouble"/> reparses everything in scope each time it is asked, which is fine per
    /// keystroke and not fine per pointer move.
    /// </remarks>
    private readonly HashSet<string> _takes = new(StringComparer.Ordinal);

    /// <summary>The row the pointer is over, which wears the heavier outline of the two.</summary>
    private TextBox? _aimed;

    public SvgViewerElementPanel(
        Func<string> text,
        Func<string> declarations,
        Func<string, Func<SvgSourceDocument, string?>, string?> write,
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

        // Fluent draws a section as a bordered card, which around every few rows reads as a form
        // inside a form.
        Resources["ExpanderHeaderBackground"] = Brushes.Transparent;
        Resources["ExpanderContentBackground"] = Brushes.Transparent;
        Resources["ExpanderHeaderBorderThickness"] = new Thickness(0);
        Resources["ExpanderContentDownBorderThickness"] = new Thickness(0);
        Resources["ExpanderHeaderPadding"] = new Thickness(0);
        Resources["ExpanderContentPadding"] = new Thickness(0, 4, 0, 8);
        Resources["ExpanderMinHeight"] = 28d;

        _all.IsChecked = s_all;
        _all.IsCheckedChanged += (_, _) => ShowsAll = _all.IsChecked == true;
        ToolTip.SetTip(_all, "List every attribute this element can take, not only the usual ones.");

        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(10, 8, 10, 0) };

        heading.Children.Add(_title);
        Grid.SetColumn(_all, 1);
        heading.Children.Add(_all);

        _body.Children.Add(heading);
        DockPanel.SetDock(heading, Dock.Top);
        _body.Children.Add(_scroll);

        var panel = new DockPanel();

        panel.Children.Add(_fault);
        DockPanel.SetDock(_fault, Dock.Bottom);
        panel.Children.Add(_host);

        Content = panel;

        DragDrop.SetAllowDrop(this, true);

        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => Release());
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Marked on the way down, before the focus it moves: pressing a swatch just after typing
        // leaves the box, whose write arrives while the press is still going on.
        AddHandler(PointerPressedEvent, (_, _) => _pressing = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(
            PointerReleasedEvent,
            (_, _) =>
            {
                _pressing = false;
                Owed();
            },
            handledEventsToo: true);
        AddHandler(LostFocusEvent, (_, _) => Owed(), handledEventsToo: true);

        Rebuild();
    }

    /// <summary>The attributes on show, in the order they are listed.</summary>
    public IReadOnlyList<string> Attributes => _shown.Select(row => row.Name).ToList();

    /// <summary>Why the last edit was refused, or null.</summary>
    public string? Fault { get; private set; }

    /// <summary>Whether every attribute the element can take is listed, rather than the usual ones.</summary>
    /// <remarks>One setting for every panel, for the reason <see cref="s_all"/> is static.</remarks>
    public bool ShowsAll
    {
        get => s_all;
        set
        {
            s_all = value;

            // Against what this panel lists rather than the setting, which another panel may have
            // changed since this one was drawn.
            if (_listedAll != value)
            {
                Refresh();
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>Catching up with a toggle another panel made while this one was out of sight.</remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (_listedAll != s_all)
        {
            Refresh();
        }
    }

    /// <summary>Says again what each row's expression comes to, for a host whose values have moved.</summary>
    /// <remarks>Without rebuilding anything, so it is safe while a row is being typed in or pressed.</remarks>
    public void Readouts()
    {
        foreach (var row in _shown)
        {
            Says(row);
        }
    }

    /// <summary>What the box for <paramref name="name"/> says, or null when there is no such row.</summary>
    public string? Shown(string name)
        => _shown.FirstOrDefault(row => string.Equals(row.Name, name, StringComparison.Ordinal)) is { Box: { } box }
            ? box.Text
            : null;

    /// <summary>Writes <paramref name="value"/> into <paramref name="name"/>, or takes it away.</summary>
    /// <remarks>Taking the name rather than a row, so everything but the pointer can be driven.</remarks>
    public bool Set(string name, string? value) => _address is { } address && Set(address, name, value);

    private bool Set(string address, string name, string? value)
    {
        // Content is not trimmed. Under xml:space="preserve" a leading space is a value, and
        // trimming would rewrite a file for somebody who opened a box and left it without typing.
        var written = IsText(name) ? value : value?.Trim();

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
        if (string.Equals(_address, addressKey, StringComparison.Ordinal))
        {
            Refresh();

            return;
        }

        // Another element's rows are wrong whoever is holding them, and a row still being left
        // writes to the element it was made for.
        _address = addressKey;

        Rebuild();
    }

    /// <summary>Reads the element again, for a host whose text has changed underneath it.</summary>
    public void Refresh()
    {
        // Not under somebody's caret or pointer. Rebuilding takes the box being typed in, or the
        // control being pressed, out of the tree — and a drawing is rebuilt on every keystroke.
        // Pressing a swatch just after typing leaves the box, which writes and lands here while
        // the press is still going on.
        if (Held())
        {
            _stale = true;

            return;
        }

        Rebuild();
    }

    /// <summary>Rebuilds a refresh that was put off, once nothing is holding the rows.</summary>
    /// <remarks>Posted, so focus has landed wherever it is going before it is asked where that is.</remarks>
    private void Owed()
        => Dispatcher.UIThread.Post(() =>
        {
            if (_stale && !Held())
            {
                Rebuild();
            }
        });

    private void Rebuild()
    {
        // The keyboard goes back to the same control in the new rows, so a rebuild between two
        // presses of Tab is not the end of somebody's place.
        if (Keyboard() is { } kept)
        {
            Dispatcher.UIThread.Post(
                () => _rows.GetVisualDescendants().OfType<Control>().FirstOrDefault(again => Kind(again) == kept)?.Focus(),
                DispatcherPriority.Loaded);
        }

        _stale = false;
        _shown.Clear();
        _drawn.Clear();
        _rows.Children.Clear();
        _listedAll = s_all;

        if (_address is not { } address)
        {
            _note.Text = "Pick an element to see what it is written with.";
            _host.Content = _note;

            return;
        }

        var open = Open();

        if (open is null || SvgAttributeEditor.ElementName(open, address) is not { } element)
        {
            // In the drawing but not in the file: the declarations block a recipe injected is the
            // one of these anybody meets.
            _note.Text = "This element is not written in the file, so it has nothing to edit.";
            _host.Content = _note;

            return;
        }

        _title.Text = SvgViewerAttributes.Title(element);
        ToolTip.SetTip(_title, $"<{element}>");
        _all.IsChecked = s_all;

        // First and outside the sections, because it is what the element says rather than how it is
        // said: somebody who picked a <text> came for the words far more often than for its spacing.
        if (SvgAttributeEditor.Content(open, address, out var missing) is { } content)
        {
            _rows.Children.Add(Row(SvgExpressionAttributes.ContentName, content, content.Length > 0, element, null));
        }
        else if (missing is { })
        {
            _rows.Children.Add(Told(missing));
        }

        var rows = SvgAttributeEditor.Attributes(open, address)
            .Select(attribute => (attribute.Name, attribute.Value, Set: true))
            .ToList();

        // By the name without its prefix, so an xlink:href the file writes is not offered an href
        // beside it — writing both is how a drawing comes to point two ways at once.
        var listed = new HashSet<string>(rows.Select(row => Unprefixed(row.Name)), StringComparer.Ordinal);

        foreach (var name in SvgViewerAttributes.Usual(element)
                     .Concat(s_all ? SvgElementNames.AttributesOf(element) : Array.Empty<string>()))
        {
            if (listed.Add(Unprefixed(name)))
            {
                rows.Add((name, string.Empty, false));
            }
        }

        foreach (var section in rows
                     .GroupBy(row => SvgViewerAttributes.Find(row.Name).Group)
                     .OrderBy(group => group.Key))
        {
            var body = new StackPanel { Spacing = 10 };
            var set = 0;

            foreach (var row in section.OrderBy(row => SvgViewerAttributes.Find(row.Name).Rank))
            {
                body.Children.Add(Row(row.Name, row.Value, row.Set, element, SvgAttributeEditor.Styled(open, address, row.Name)));
                set += row.Set ? 1 : 0;
            }

            _rows.Children.Add(Section(section.Key, set, body));
        }

        _host.Content = _body;
    }

    /// <summary>One collapsible section of rows, which stays the way somebody left it.</summary>
    private static Expander Section(SvgViewerAttributeGroup group, int set, Control rows)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        header.Children.Add(new TextBlock { Text = group.ToString(), FontWeight = FontWeight.SemiBold, FontSize = 12 });

        if (set > 0)
        {
            var count = new TextBlock { Text = $"{set} set", Opacity = 0.5, FontSize = 11, Margin = new Thickness(8, 0, 0, 0) };

            Grid.SetColumn(count, 1);
            header.Children.Add(count);
        }

        var section = new Expander
        {
            Header = header,
            Content = rows,
            Tag = group.ToString(),
            IsExpanded = !s_folded.Contains(group),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        // After IsExpanded is set, so reading the setting back is not taken for somebody changing it.
        section.Expanded += (_, _) => s_folded.Remove(group);
        section.Collapsed += (_, _) => s_folded.Add(group);

        return section;
    }

    private static string Unprefixed(string name) => name.Substring(name.IndexOf(':') + 1);

    /// <param name="styled">What the element's style attribute sets this to instead, or null.</param>
    private Control Row(string name, string value, bool set, string element, string? styled)
    {
        var about = SvgViewerAttributes.Find(name);

        // The row's own, and not whatever is picked by the time a posted write runs.
        var address = _address!;
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

        var label = new TextBlock
        {
            Text = IsText(name) ? "Text" : about.Label,
            FontSize = 12,
            FontWeight = set ? FontWeight.SemiBold : FontWeight.Normal,
            Opacity = set ? 1 : 0.7,
            VerticalAlignment = VerticalAlignment.Center
        };

        ToolTip.SetTip(label, IsText(name) ? "The words between the element's tags" : name);
        heading.Children.Add(label);

        // What an expression here would have to come to. A row that takes none says nothing, and
        // braces typed into it are refused with the reason the moment they are typed.
        if (Typed(name) is { } type)
        {
            var kind = new TextBlock
            {
                Text = ExprFunctions.Describe(type)
                       + (SvgExpressionAttributes.IsInArguments(name) ? " per argument" : string.Empty)
                       + (SvgExpressionAttributes.IsResolvedBeforeRecording(name) ? ", built in" : string.Empty),
                Opacity = 0.45,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(kind, 1);
            heading.Children.Add(kind);
        }

        var box = new TextBox
        {
            Text = value,
            Watermark = styled is { } ? $"{styled} in style" : IsText(name) ? "no text" : "not set",
            FontSize = 12,
            Tag = name
        };

        // A style declaration wins over the attribute and writing one it overrides is refused, so
        // the row says what the element is painted with and where that can be changed, and offers
        // nothing that would be turned away.
        if (styled is { })
        {
            box.IsReadOnly = true;
            ToolTip.SetTip(box, "Set in this element's style attribute, which wins over the attribute. Change it under Inline style.");
        }

        if (this.TryFindResource("SvgExpressionBox", ActualThemeVariant, out var theme) && theme is ControlTheme box_)
        {
            box.Theme = box_;
        }

        // Under the box rather than beside it, where a whole evaluated transform took the width a
        // narrow pane had for the box.
        var readout = new TextBlock
        {
            Opacity = 0.55,
            FontSize = 11,
            FontFamily = new FontFamily("Menlo, Consolas, monospace"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsVisible = false
        };

        var trouble = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 0),
            IsVisible = false
        };

        // The themed brush rather than the dark-theme red it used to be written as, which is the
        // same red and unreadable on a light ground. Every other line saying something is wrong
        // already goes through this key.
        trouble[!TextBlock.ForegroundProperty] =
            new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SvgViewerSourceErrorBrush");

        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        Grid.SetColumn(box, 1);
        line.Children.Add(box);

        var lines = new StackPanel { Spacing = 3, Children = { heading, line } };

        Action<string, ExprValue?>? follow = null;

        void Put(string text) => Commit(box, name, address, text);

        if (!IsText(name) && Controlled(element) && styled is null)
        {
            follow = about.Control switch
            {
                SvgViewerAttributeControl.Colour => Swatch(line, box, Put),
                SvgViewerAttributeControl.Fraction => Fraction(lines, box, name, Put),
                _ => null
            };

            if (about.Choices.Count > 0)
            {
                Choices(line, name, Put, about.Choices);
            }

            if (about.Control == SvgViewerAttributeControl.Number)
            {
                box.AddHandler(KeyDownEvent, Nudge, RoutingStrategies.Tunnel);
            }
        }

        lines.Children.Add(readout);
        lines.Children.Add(trouble);

        var row = (name, box, readout, trouble, follow);

        _shown.Add(row);
        _drawn[box] = value;

        if (set)
        {
            // Not the edit class: every edit button in the viewer is visible, and this one is not
            // always.
            var reset = new Button
            {
                Content = "×",
                Classes = { "reset" },
                Tag = name,
                Padding = new Thickness(6, 0),
                MinHeight = 0,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };

            ToolTip.SetTip(reset, IsText(name) ? "Clear the text" : $"Remove {name}");
            reset.Click += (_, _) => Put(string.Empty);

            Grid.SetColumn(reset, 2);
            heading.Children.Add(reset);
        }

        box.TextChanged += (_, _) => Says(row);

        box.LostFocus += (_, _) => Commit(box, name, address);

        box.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Return))
            {
                return;
            }

            // Not handled: the viewer takes the keyboard off the box next, and leaving commits again,
            // which is a no-op once the text matches the file.
            Commit(box, name, address);
        };

        Says(row);

        return lines;
    }

    /// <summary>Whether a row on <paramref name="element"/> may offer a control beside its box.</summary>
    /// <remarks>
    /// Not on an animation or a transfer function, where the names mean something else:
    /// <c>fill="freeze"</c> is not a paint and an <c>&lt;feFuncA&gt;</c>'s <c>offset</c> is not a stop's.
    /// </remarks>
    private static bool Controlled(string element)
        => element != "set"
           && !element.StartsWith("animate", StringComparison.Ordinal)
           && !element.StartsWith("feFunc", StringComparison.Ordinal);

    /// <summary>A swatch before the box that opens a colour picker, answering what the box says.</summary>
    private Action<string, ExprValue?> Swatch(Grid line, TextBox box, Action<string> put)
    {
        var shown = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1)
        };

        shown[!Border.BorderBrushProperty] =
            new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextControlBorderBrush");

        var swatch = new Button
        {
            Content = shown,
            Classes = { "swatch" },
            Tag = box.Tag,
            Padding = new Thickness(4),
            MinHeight = 0,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

        swatch.Flyout = flyout;
        ToolTip.SetShowOnDisabled(swatch, true);

        flyout.Opened += (_, _) => _picking++;

        // Written once, when the picker goes: every write rebuilds the rows and is one more step
        // to undo.
        flyout.Closed += (_, _) =>
        {
            _picking--;
            put(box.Text ?? string.Empty);
            Owed();
        };

        // Made as it opens rather than with the row, which Studio builds for every pick; and before
        // it is shown, since showing it hands the keyboard to what it holds.
        flyout.Opening += (_, _) =>
        {
            var had = shown.Background is ISolidColorBrush { Color: var known } ? known : (Color?)null;
            var was = box.Text;

            // Grey where the row has no colour. Opened on black, the spectrum stays black wherever it
            // is clicked, and black itself could never be picked.
            var picker = new ColorView
            {
                IsAlphaEnabled = false,
                IsAlphaVisible = false,
                Color = had is { } colour ? Color.FromRgb(colour.R, colour.G, colour.B) : Colors.Gray
            };

            picker.Styles.Add(new StyleInclude(Home)
            {
                Source = new Uri("avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml")
            });

            // Into the box as it moves, so the row reads what is picked. The colour it opened on
            // puts back what was written, so opening and closing it cannot turn red into #ff0000.
            picker.ColorChanged += (_, e) => box.Text =
                had is { } same && e.NewColor.R == same.R && e.NewColor.G == same.G && e.NewColor.B == same.B
                    ? was
                    : Spelt(e.NewColor, had?.A ?? byte.MaxValue);

            flyout.Content = picker;
        };

        Grid.SetColumn(swatch, 0);
        line.Children.Add(swatch);

        return (written, value) =>
        {
            var bound = SvgExpressionAttributes.TryUnwrap(written, out _);
            var colour = bound
                ? value is { Type: ExprType.Color } ? SvgViewerParameterFactory.ToColor(value) : (Color?)null
                : Parsed(written);

            shown.Background = colour is { } known ? new SolidColorBrush(known) : null;
            swatch.IsEnabled = !bound;

            ToolTip.SetTip(swatch, bound
                ? "Follows the expression. Type a colour into the box to set one instead."
                : "Pick a colour");
        };
    }

    /// <summary>A picked colour as the row writes it, at the opacity the row already had.</summary>
    /// <remarks>
    /// The picker has no alpha of its own, so a translucent fill keeps its own rather than being made
    /// opaque by a change of hue. Spelt rgba() rather than #rrggbbaa, which a paint reads and a
    /// stop-color does not.
    /// </remarks>
    private static string Spelt(Color colour, byte alpha)
        => alpha == byte.MaxValue
            ? SvgViewerParameterFactory.Describe(ExprValue.Color(colour.R, colour.G, colour.B, alpha))
            : string.Format(CultureInfo.InvariantCulture, "rgba({0}, {1}, {2}, {3:0.###})", colour.R, colour.G, colour.B, alpha / 255d);

    /// <summary>A written colour, or null where it is none, a reference, or not a colour at all.</summary>
    private static Color? Parsed(string written)
    {
        // Not through the converter, which answers these with an exception on every keystroke.
        if (written.Length == 0
            || written is "none" or "currentColor" or "inherit"
            || written.StartsWith("url(", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return new SvgColourConverter().ConvertFrom(null, CultureInfo.InvariantCulture, written)
                is System.Drawing.Color { IsEmpty: false } colour
                ? Color.FromArgb(colour.A, colour.R, colour.G, colour.B)
                : null;
        }
        catch (Exception failure) when (failure is SvgException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A slider under the box for a value from 0 to 1, answering what the box says.</summary>
    private static Action<string, ExprValue?> Fraction(StackPanel lines, TextBox box, string name, Action<string> put)
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            SmallChange = 0.01,
            LargeChange = 0.1,
            Tag = name,
            Margin = new Thickness(0, -6, 0, -10)
        };

        // Which way a change is going, so the box answering the slider does not move it back.
        var answering = false;

        slider.ValueChanged += (_, e) =>
        {
            if (answering)
            {
                return;
            }

            answering = true;
            box.Text = e.NewValue.ToString("0.##", CultureInfo.InvariantCulture);
            answering = false;
        };

        // On release and not per tick, for the reason the picker writes when it closes.
        slider.AddHandler(PointerReleasedEvent, (_, _) => put(box.Text ?? string.Empty), handledEventsToo: true);
        slider.LostFocus += (_, _) => put(box.Text ?? string.Empty);

        ToolTip.SetShowOnDisabled(slider, true);
        lines.Children.Add(slider);

        return (written, value) =>
        {
            var bound = SvgExpressionAttributes.TryUnwrap(written, out _);

            slider.IsEnabled = !bound;
            slider.Opacity = written.Length == 0 ? 0.4 : 1;
            ToolTip.SetTip(slider, bound ? "Follows the expression. Type a number into the box to set one instead." : null);

            if (answering)
            {
                return;
            }

            answering = true;

            // An unset opacity is 1 and an unset stop offset 0, which is where the slider sits.
            slider.Value = bound
                ? value is { Type: ExprType.Number } number ? number.AsNumber : slider.Value
                : FractionOf(written) ?? (name == "offset" ? 0 : 1);

            answering = false;
        };
    }

    /// <summary>A written fraction, <c>0.5</c> or <c>50%</c>, or null where it is not one.</summary>
    private static double? FractionOf(string written)
    {
        var percent = written.EndsWith("%", StringComparison.Ordinal);

        return double.TryParse(
                percent ? written.Substring(0, written.Length - 1) : written,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number)
            ? Math.Clamp(percent ? number / 100 : number, 0, 1)
            : null;
    }

    /// <summary>A button after the box listing the values SVG spells for this attribute.</summary>
    private void Choices(Grid line, string name, Action<string> put, IReadOnlyList<string> choices)
    {
        var pick = new Button
        {
            Content = "▾",
            Classes = { "choices" },
            Tag = name,
            Padding = new Thickness(6, 0),
            MinHeight = 0,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // A list the menu watches, not its Items: the popup reads Items once, when it is made, and
        // that is before Opening — filled there, the menu opened as an empty sliver.
        var offered = new ObservableCollection<MenuItem>();
        var menu = new MenuFlyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            ItemsSource = offered
        };

        pick.Flyout = menu;
        ToolTip.SetTip(pick, "Choose a value");

        menu.Opened += (_, _) => _picking++;
        menu.Closed += (_, _) =>
        {
            _picking--;
            Owed();
        };

        // Filled as it opens, so a url(#…) names what the drawing holds now. Picking one on a row
        // an expression drives is how that row is let go of, so this is never disabled.
        menu.Opening += (_, _) =>
        {
            offered.Clear();

            foreach (var choice in Expanded(choices))
            {
                // A TextBlock and not the string, which a menu reads for an access key: the first
                // underscore of paint0_linear_1 would vanish, and two ids could read the same.
                var item = new MenuItem { Header = new TextBlock { Text = choice } };

                item.Click += (_, _) => put(choice);
                offered.Add(item);
            }
        };

        Grid.SetColumn(pick, 2);
        line.Children.Add(pick);
    }

    /// <summary>The choices with each <c>#tag</c> spelt out as a reference to every such element with an id.</summary>
    private IEnumerable<string> Expanded(IReadOnlyList<string> choices)
    {
        var document = Open()?.Document;

        foreach (var choice in choices)
        {
            if (!choice.StartsWith("#", StringComparison.Ordinal))
            {
                yield return choice;

                continue;
            }

            foreach (var element in document?.Descendants() ?? Enumerable.Empty<System.Xml.Linq.XElement>())
            {
                if (element.Name.LocalName == choice.Substring(1) && (string?)element.Attribute("id") is { Length: > 0 } id)
                {
                    yield return $"url(#{id})";
                }
            }
        }
    }

    /// <summary>Up and down step a number in the box, without writing it until the box is left.</summary>
    /// <remarks>Tunnelling, because the box's own handler takes the arrows to move the caret.</remarks>
    private static void Nudge(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down) || sender is not TextBox box)
        {
            return;
        }

        var match = s_measure.Match(box.Text?.Trim() ?? string.Empty);

        if (!match.Success
            || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return;
        }

        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10
            : e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? 0.1
            : 1;

        number = Math.Round(number + (e.Key == Key.Up ? step : -step), 6);

        box.Text = number.ToString("0.######", CultureInfo.InvariantCulture) + match.Groups[2].Value;
        box.CaretIndex = box.Text.Length;
        e.Handled = true;
    }

    /// <summary>A number and the unit after it, if any: <c>12</c>, <c>-0.5em</c>, <c>50%</c>.</summary>
    private static readonly Regex s_measure = new(@"^([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)([a-zA-Z%]*)$", RegexOptions.CultureInvariant);

    /// <summary>Puts <paramref name="text"/> in a row's box and writes it, as though it had been typed.</summary>
    /// <remarks>
    /// Posted, because writing rebuilds every row, and the control whose handler asked for this is
    /// still inside that handler. A second write of the same text is a no-op in the commit below.
    /// </remarks>
    private void Commit(TextBox box, string name, string address, string text)
    {
        box.Text = text;

        Dispatcher.UIThread.Post(() => Commit(box, name, address));
    }

    private void Commit(TextBox box, string name, string address)
    {
        var content = IsText(name);
        var written = content ? box.Text ?? string.Empty : box.Text?.Trim() ?? string.Empty;

        if (_drawn.TryGetValue(box, out var drawn) && string.Equals(written, content ? drawn : drawn.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        var open = Open();

        var had = content
            ? (open is { } ? SvgAttributeEditor.Content(open, address, out _) : null) ?? string.Empty
            : (open is { }
                    ? SvgAttributeEditor.Attributes(open, address)
                    : Array.Empty<SvgSourceAttribute>())
                .FirstOrDefault(attribute => string.Equals(attribute.Name, name, StringComparison.Ordinal))
                .Value ?? string.Empty;

        if (string.Equals(written, had, StringComparison.Ordinal))
        {
            return;
        }

        // Said on the row already. The box keeps what was typed, so it can be finished.
        if (Trouble(name, written) is { })
        {
            return;
        }

        if (!Set(address, name, written))
        {
            box.Text = had;
        }
        else if (_drawn.ContainsKey(box))
        {
            _drawn[box] = written;
        }
    }

    /// <summary>Fills one row's trouble and readout from what its box currently says.</summary>
    private void Says((string Name, TextBox Box, TextBlock Readout, TextBlock Trouble, Action<string, ExprValue?>? Follow) row)
    {
        var written = row.Box.Text?.Trim() ?? string.Empty;
        var trouble = Trouble(row.Name, written);

        row.Trouble.Text = trouble;
        row.Trouble.IsVisible = trouble is { };

        var value = trouble is null ? Value(written) : null;
        var readout = trouble is null ? Readout(row.Name, written, value) : string.Empty;

        row.Readout.Text = readout;
        row.Readout.IsVisible = readout.Length > 0;

        row.Follow?.Invoke(written, value);
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

        if (Typed(name) is not { } type)
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

    /// <summary>What the expression a box holds comes to, or null where it holds none or it fails.</summary>
    private ExprValue? Value(string written)
    {
        if (!SvgExpressionAttributes.TryUnwrap(written, out var expression) || _values() is not { } evaluator)
        {
            return null;
        }

        try
        {
            return evaluator.Evaluate(expression);
        }
        catch (Exception failure) when (failure is ExprException or ArgumentException)
        {
            return null;
        }
    }

    private string Readout(string name, string written, ExprValue? value)
    {
        if (!SvgExpressionAttributes.IsInArguments(name))
        {
            return value is { } known
                ? $"{ExprFunctions.Describe(known.Type)}  {SvgViewerParameterFactory.Describe(known)}"
                : string.Empty;
        }

        if (_values() is not { } evaluator)
        {
            return string.Empty;
        }

        try
        {
            var arguments = SvgTransformExpression.Parse(written);

            // The whole transform as it currently stands, since one argument's number says
            // nothing about where the shape ends up.
            return arguments.Any
                ? arguments.With(argument =>
                    SvgViewerParameterFactory.Describe(evaluator.Evaluate(argument.Expression!)))
                : string.Empty;
        }
        catch (Exception failure) when (failure is ExprException or ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>The drawing as a tree, or null while its text will not read back.</summary>
    private SvgSourceDocument? Open() => SvgSourceDocument.Read(_text(), out _);

    /// <summary>
    /// Writes one attribute of the element, through whatever is holding the drawing.
    /// </summary>
    /// <remarks>
    /// The host is handed the edit rather than the text it comes to, because the two hosts this
    /// panel serves keep their drawing in different places and only they know where. A prefix is
    /// part of an attribute's name, so this is the one path that can tell xlink:href from href.
    /// </remarks>
    private bool Write(string address, string name, string? value)
    {
        var refusal = IsText(name)
            ? _write(
                value is null ? "clear text" : "set text",
                source => SvgAttributeEditor.SetContent(source, address, value))
            : _write(
                value is null ? $"remove {name}" : $"set {name}",
                source => SvgAttributeEditor.SetAttribute(source, address, name, value));

        Say(refusal);

        return refusal is null;
    }

    /// <summary>Whether this row is the element's text rather than one of its attributes.</summary>
    private static bool IsText(string name)
        => string.Equals(name, SvgExpressionAttributes.ContentName, StringComparison.Ordinal);

    /// <summary>
    /// What an expression in <paramref name="name"/> has to come to, text included.
    /// </summary>
    /// <remarks>
    /// The content key is answered here rather than put in the placeholder table, which is keyed by
    /// attribute local name and read by the recipes, the element factory and the list of names a row
    /// can be offered for — none of which may grow a name no attribute can have. The substitution
    /// already special-cases the same name the same way.
    /// </remarks>
    private static ExprType? Typed(string name)
        => IsText(name) ? SvgExpressionAttributes.ContentType : SvgExpressionAttributes.TypeFor(name);

    /// <summary>A line saying why an element's text is not one value to edit.</summary>
    private static Control Told(string why)
        => new TextBlock
        {
            Text = why,
            Opacity = 0.6,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };

    private void Say(string? refusal)
    {
        Fault = refusal;

        _fault.Text = refusal;
        _fault.IsVisible = refusal is { };
        _fault[!TextBlock.ForegroundProperty] =
            new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SvgViewerSourceErrorBrush");
    }

    // ---- a variable dropped on a row -------------------------------------------------------------

    /// <summary>The rows a variable being dragged over the panel would be written into.</summary>
    /// <remarks>Empty while nothing is being carried. Named rather than drawn, for the reason
    /// <see cref="Set"/> takes a name: everything but the pointer can be driven.</remarks>
    public IReadOnlyList<string> Offered
        => _shown.Where(row => _takes.Contains(row.Name)).Select(row => row.Name).ToList();

    /// <summary>
    /// Offers every row a dragged variable could be written into, and takes the drag while over one.
    /// </summary>
    /// <remarks>
    /// Marked handled either way. The viewer this usually sits in turns away any drag carrying no
    /// files, which is every drag of a variable — left unhandled, not one of them could be made at
    /// all. Where the pointer is over no row that would take it the drag is still this panel's; it
    /// says so by offering nothing rather than by letting something else answer.
    /// </remarks>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (SvgViewerVariableDrag.Carried(e) is not { } name)
        {
            Release();

            return;
        }

        Offer(name);

        var over = Under(e);

        Aim(over);

        e.DragEffects = over is { } ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    /// <inheritdoc cref="OnDragOver"/>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        var name = SvgViewerVariableDrag.Carried(e);

        // Before the release, which is what forgets which rows would have taken it.
        var over = Under(e);

        Release();

        if (name is null || over is not { Tag: string attribute })
        {
            return;
        }

        e.DragEffects = DragDropEffects.Link;
        e.Handled = true;

        // Through the box and its own commit, so the refusal, the history's label, the readout and
        // the check against a style declaration are the ones typing it would have got.
        over.Text = SvgViewerVariableDrag.Bound(name);

        Commit(over, attribute, _address!);
    }

    /// <summary>Works out which rows <paramref name="name"/> could be written into, and says so.</summary>
    private void Offer(string name)
    {
        if (string.Equals(_carried, name, StringComparison.Ordinal))
        {
            return;
        }

        Release();

        _carried = name;

        var written = SvgViewerVariableDrag.Bound(name);

        foreach (var row in _shown)
        {
            if (row.Box.IsReadOnly || Trouble(row.Name, written) is { })
            {
                continue;
            }

            _takes.Add(row.Name);

            row.Box[!TemplatedControl.BorderBrushProperty] =
                new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextControlBorderBrushFocused");
        }
    }

    /// <summary>The box under the pointer, where it is one that would take what is being carried.</summary>
    private TextBox? Under(DragEventArgs e)
        => (e.Source as Visual)?.FindAncestorOfType<TextBox>(true) is { Tag: string name } box
           && _takes.Contains(name)
            ? box
            : null;

    private void Aim(TextBox? box)
    {
        if (ReferenceEquals(_aimed, box))
        {
            return;
        }

        _aimed?.ClearValue(TemplatedControl.BorderThicknessProperty);
        _aimed = box;
        _aimed?.SetValue(TemplatedControl.BorderThicknessProperty, new Thickness(2d));
    }

    /// <summary>Puts every row back the way it was drawn.</summary>
    private void Release()
    {
        if (_carried is null)
        {
            return;
        }

        Aim(null);

        foreach (var row in _shown)
        {
            row.Box.ClearValue(TemplatedControl.BorderBrushProperty);
            row.Box.ClearValue(TemplatedControl.BorderThicknessProperty);
        }

        _takes.Clear();
        _carried = null;
    }

    /// <summary>
    /// Whether rebuilding the rows now would take something from under somebody: the box being
    /// typed in, a control still being pressed, or a picker or menu a row has open.
    /// </summary>
    /// <remarks>
    /// Not wherever the keyboard happens to rest. A row kept because a button still had focus stayed
    /// behind for as long as it did — through an undo, whose rows it then wrote back.
    /// </remarks>
    private bool Held()
        => _pressing
           || _picking > 0
           || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox { Tag: string } box
           && box.FindAncestorOfType<SvgViewerElementPanel>() is { } panel
           && ReferenceEquals(panel, this);

    /// <summary>What has the keyboard in these rows, as something the rebuilt rows have again, or null.</summary>
    private string? Keyboard()
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Control held
           && held.FindAncestorOfType<SvgViewerElementPanel>() is { } panel
           && ReferenceEquals(panel, this)
            ? Kind(held)
            : null;

    /// <summary>A control named by its row and its part, or by its section for a section's header.</summary>
    private static string? Kind(Control control)
        => control.TemplatedParent is Expander { Tag: string section }
            ? "section " + section
            : control.Tag is string row && control.TemplatedParent is null
                ? $"{row} {control.GetType().Name} {string.Join(" ", control.Classes.Where(name => !name.StartsWith(':')))}"
                : null;
}
